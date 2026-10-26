// SVault v2: independent encryption key derivation from BIP-39 seed.
// The output key is nonextractable and cannot sign Stellar transactions.
window.stellarVaultEncryption = (() => {
  const encoder = new TextEncoder();
  async function deriveKey(bip39Seed, networkPassphrase, registryAccountId) {
    if (!(bip39Seed instanceof Uint8Array) || bip39Seed.length !== 64)
      throw new TypeError('Expected 64-byte BIP-39 seed');
    if (!networkPassphrase || !registryAccountId)
      throw new Error('Network and registry account identity are required');
    const base = await crypto.subtle.importKey('raw', bip39Seed, 'HKDF', false, ['deriveKey']);
    const salt = await crypto.subtle.digest('SHA-256', encoder.encode(
      'svault-v2|network|' + networkPassphrase + '|registry|' + registryAccountId));
    return crypto.subtle.deriveKey({
      name: 'HKDF', hash: 'SHA-256', salt,
      info: encoder.encode('svault-v2/aes-256-gcm/records')
    }, base, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
  }
  function recordAad(networkPassphrase, registryAccountId, nodeIndex, recordId, generation) {
    if (!Number.isSafeInteger(nodeIndex) || nodeIndex < 1) throw new RangeError('Invalid storage node');
    if (!Number.isSafeInteger(generation) || generation < 0) throw new RangeError('Invalid generation');
    return encoder.encode(JSON.stringify([
      'svault-v2', networkPassphrase, registryAccountId, nodeIndex, recordId, generation
    ]));
  }
  return Object.freeze({ deriveKey, recordAad });
})();
