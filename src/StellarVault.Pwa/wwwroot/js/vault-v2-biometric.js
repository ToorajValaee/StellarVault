// Optional browser passkey PRF binding. A successful authenticator verification
// derives an encryption key; localStorage holds ciphertext, never plaintext.
// User verification may be biometric, device PIN or another platform mechanism.
window.stellarVaultBiometric = (() => {
  const prefix = 'sv-biometric-v1:';
  const encoder = new TextEncoder();
  const decoder = new TextDecoder();
  const to64 = bytes => btoa(String.fromCharCode(...bytes)).replace(/\\+/g, '-').replace(/\\//g, '_').replace(/=+$/, '');
  const from64 = data => Uint8Array.from(atob(data.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - data.length % 4) % 4)), c => c.charCodeAt(0));
  const random = n => crypto.getRandomValues(new Uint8Array(n));
  const keyFor = account => prefix + account;
  const requireApi = () => {
    if (!isSecureContext || !window.PublicKeyCredential || !navigator.credentials || !crypto.subtle)
      throw new Error('Passkeys require HTTPS or localhost and a supported browser.');
  };
  const derive = async output => crypto.subtle.importKey('raw', output, 'AES-GCM', false, ['encrypt', 'decrypt']);
  const assert = async (credentialId, salt) => {
    const credential = await navigator.credentials.get({
      publicKey: {
        challenge: random(32),
        allowCredentials: [{ type: 'public-key', id: from64(credentialId) }],
        userVerification: 'required',
        timeout: 60000,
        extensions: { prf: { eval: { first: salt } } }
      }
    });
    const output = credential?.getClientExtensionResults?.().prf?.results?.first;
    if (!output) throw new Error('This passkey does not support encrypted biometric unlock (PRF).');
    return derive(output);
  };
  function available() {
    return !!(isSecureContext && window.PublicKeyCredential && navigator.credentials && crypto.subtle);
  }
  function has(account) {
    try { return !!localStorage.getItem(keyFor(account)); } catch { return false; }
  }
  async function enroll(account, passphrase) {
    requireApi();
    window.stellarVaultV2Lifecycle?.setCredentialPrompt(true);
    try {
    if (!account || !passphrase) throw new Error('Account and vault passphrase are required.');
    const userId = random(32);
    const credential = await navigator.credentials.create({
      publicKey: {
        challenge: random(32),
        rp: { name: 'StellarVault' },
        user: { id: userId, name: 'StellarVault local unlock', displayName: 'StellarVault local unlock' },
        pubKeyCredParams: [{ type: 'public-key', alg: -7 }, { type: 'public-key', alg: -257 }],
        authenticatorSelection: { residentKey: 'preferred', userVerification: 'required' },
        timeout: 60000,
        attestation: 'none',
        extensions: { prf: {} }
      }
    });
    if (!credential || !credential.rawId) throw new Error('Passkey registration was canceled.');
    const credentialId = to64(new Uint8Array(credential.rawId));
    const salt = random(32);
    const key = await assert(credentialId, salt);
    const iv = random(12);
    const data = await crypto.subtle.encrypt({ name: 'AES-GCM', iv, additionalData: encoder.encode(account) }, key, encoder.encode(passphrase));
    localStorage.setItem(keyFor(account), JSON.stringify({ credentialId, salt: to64(salt), iv: to64(iv), data: to64(new Uint8Array(data)) }));
    return true;
    } finally { window.stellarVaultV2Lifecycle?.setCredentialPrompt(false); }
  }
  async function unlock(account) {
    requireApi();
    window.stellarVaultV2Lifecycle?.setCredentialPrompt(true);
    try {
    const raw = localStorage.getItem(keyFor(account));
    if (!raw) throw new Error('Biometric unlock has not been set up on this device.');
    const item = JSON.parse(raw);
    const key = await assert(item.credentialId, from64(item.salt));
    const data = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: from64(item.iv), additionalData: encoder.encode(account) }, key, from64(item.data));
    return decoder.decode(data);
    } finally { window.stellarVaultV2Lifecycle?.setCredentialPrompt(false); }
  }
  return Object.freeze({ available, has, enroll, unlock });
})();
