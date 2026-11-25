// Stores public account and network identity only. Stellar network passphrases
// identify chains; they are public consensus metadata, NOT vault credentials.
// Online ledger connections separately revalidate Horizon before signing.
window.stellarVaultRecentVault = (() => {
  const key = 'stellar-vault-v2-recent-account';
  const valid = a => typeof a === 'string' && /^G[A-Z2-7]{55}$/.test(a);
  function endpoint(value) {
    try {
      const url = new URL(value);
      const loopback = ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname);
      if (!(url.protocol === 'https:' || (loopback && url.protocol === 'http:')) ||
          url.username || url.password || url.search || url.hash ||
          url.pathname !== '/' || url.origin === 'null') return null;
      return url.origin + '/';
    } catch { return null; }
  }
  function load() {
    try {
      const raw = localStorage.getItem(key);
      if (raw === null) return null;
      const value = JSON.parse(raw);
      if (!valid(value?.account) || !['testnet', 'public', 'custom'].includes(value.network) ||
          (value.network === 'custom' && !endpoint(value.horizonUrl))) return null;
      return { account: value.account, network: value.network,
        horizonUrl: value.network === 'custom' ? endpoint(value.horizonUrl) : null,
        networkPassphrase: value.network === 'custom' && typeof value.networkPassphrase === 'string' &&
          value.networkPassphrase.trim() && value.networkPassphrase.length <= 512
          ? value.networkPassphrase : null };
    } catch { return null; }
  }
  function save(account, network, horizonUrl = null, networkPassphrase = null) {
    if (!valid(account) || !['testnet', 'public', 'custom'].includes(network))
      throw new Error('Invalid remembered account or network');
    const url = network === 'custom' ? endpoint(horizonUrl) : null;
    if (network === 'custom' && (!url || typeof networkPassphrase !== 'string' ||
      !networkPassphrase.trim() || networkPassphrase.length > 512))
      throw new Error('Invalid custom network identity');
    localStorage.setItem(key, JSON.stringify({ version: 3, account, network, horizonUrl: url,
      networkPassphrase: network === 'custom' ? networkPassphrase : null }));
  }
  // Tab-scoped access intent, NOT a credential and NOT local-storage consent.
  // A saved encrypted vault by itself must never force a passphrase screen
  // when the user ends a session and later chooses on-chain-only access.
  const activeHybridTabKey = 'stellar-vault-v2-active-hybrid-tab';
  function markHybridSession(account, network) {
    if (!valid(account) || !['testnet', 'public', 'custom'].includes(network))
      throw new Error('Invalid active hybrid session scope');
    sessionStorage.setItem(activeHybridTabKey, JSON.stringify({ account, network }));
  }
  function hasHybridSession(account, network) {
    try {
      const entry = JSON.parse(sessionStorage.getItem(activeHybridTabKey));
      return valid(account) && entry?.account === account && entry?.network === network;
    } catch { return false; }
  }
  function clearHybridSession() {
    try { sessionStorage.removeItem(activeHybridTabKey); }
    catch { /* In-memory sessions remain independently lockable. */ }
  }
  // Optional opt-in encrypted signing recovery. Never persist plaintext mnemonic.
  // PBKDF2-SHA256 is supported by WebCrypto across browser hosts; parameters
  // are versioned for future migration to a memory-hard platform KDF.
  const seedKey = 'stellar-vault-v2-encrypted-seed';
  const utf8 = new TextEncoder();
  const decode = new TextDecoder();
  const b64 = bytes => btoa(String.fromCharCode(...bytes));
  const un64 = str => Uint8Array.from(atob(str), c => c.charCodeAt(0));
  async function seedCipherKey(passphrase, salt) {
    if (!crypto?.subtle || !globalThis.isSecureContext) throw new Error('Secure browser context required for encrypted seed storage.');
    const base = await crypto.subtle.importKey('raw', utf8.encode(passphrase), 'PBKDF2', false, ['deriveKey']);
    return crypto.subtle.deriveKey(
      { name: 'PBKDF2', hash: 'SHA-256', salt, iterations: 600000 },
      base, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
  }
  async function saveEncryptedSeed(account, network, mnemonic, passphrase) {
    if (!valid(account) || !['testnet', 'public', 'custom'].includes(network) ||
        typeof mnemonic !== 'string' || !mnemonic.trim() || !passphrase)
      throw new Error('Missing encrypted credential inputs.');
    const salt = crypto.getRandomValues(new Uint8Array(16));
    const iv = crypto.getRandomValues(new Uint8Array(12));
    const keyMaterial = await seedCipherKey(passphrase, salt);
    const aad = utf8.encode('StellarVault-v2-seed:' + account + ':' + network);
    const encrypted = new Uint8Array(await crypto.subtle.encrypt(
      { name: 'AES-GCM', iv, additionalData: aad }, keyMaterial, utf8.encode(mnemonic)));
    // Only ciphertext is persisted. A failed encryption never overwrites an older record.
    localStorage.setItem(seedKey, JSON.stringify({
      version: 1, account, network, kdf: 'PBKDF2-SHA256', iterations: 600000,
      salt: b64(salt), iv: b64(iv), ciphertext: b64(encrypted)
    }));
  }
  function hasEncryptedSeed(account, network) {
    try { const item = JSON.parse(localStorage.getItem(seedKey)); return item?.version === 1 && item.account === account && item.network === network; }
    catch { return false; }
  }
  async function unlockEncryptedSeed(account, network, passphrase) {
    if (!passphrase || !hasEncryptedSeed(account, network)) throw new Error('No encrypted signing credentials for this account.');
    const item = JSON.parse(localStorage.getItem(seedKey));
    if (item.kdf !== 'PBKDF2-SHA256' || item.iterations !== 600000)
      throw new Error('Unsupported encrypted credential format.');
    const salt = un64(item.salt), iv = un64(item.iv), ciphertext = un64(item.ciphertext);
    if (salt.length !== 16 || iv.length !== 12 || ciphertext.length > 2048)
      throw new Error('Invalid encrypted credential envelope.');
    const keyMaterial = await seedCipherKey(passphrase, salt);
    const raw = await crypto.subtle.decrypt(
      { name: 'AES-GCM', iv, additionalData: utf8.encode('StellarVault-v2-seed:' + account + ':' + network) },
      keyMaterial, ciphertext);
    return decode.decode(raw);
  }
  function clearEncryptedSeed() { localStorage.removeItem(seedKey); }
  function clear() { localStorage.removeItem(key); }
  return Object.freeze({ load, save, clear, saveEncryptedSeed, hasEncryptedSeed, unlockEncryptedSeed, clearEncryptedSeed,
    markHybridSession, hasHybridSession, clearHybridSession });
})();
