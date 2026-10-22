// SVKW v1 format: "SVKW" | 0x01 | 12-byte nonce | 32-byte ciphertext | 16-byte tag.
// Identical to the native VaultKeyEnvelope implementation.
window.stellarVaultV2KeyWrap = Object.freeze({
  async wrap(dataKey, wrappingKey, accountId) {
    if (!(dataKey instanceof Uint8Array) || dataKey.length !== 32 ||
        !(wrappingKey instanceof Uint8Array) || wrappingKey.length !== 32)
      throw new TypeError('Expected two 256-bit keys');
    if (typeof accountId !== 'string' || !/^G[A-Z2-7]{55}$/.test(accountId))
      throw new TypeError('Invalid Stellar public account');
    const nonce = crypto.getRandomValues(new Uint8Array(12));
    const aad = new TextEncoder().encode('StellarVault/key-wrap/v1\0' + accountId);
    const key = await crypto.subtle.importKey('raw', wrappingKey, { name: 'AES-GCM' }, false, ['encrypt']);
    const ciphertextAndTag = new Uint8Array(await crypto.subtle.encrypt(
      { name: 'AES-GCM', iv: nonce, additionalData: aad, tagLength: 128 }, key, dataKey));
    if (ciphertextAndTag.length !== 48) throw new Error('Unexpected AES-GCM envelope length');
    const result = new Uint8Array(65);
    result.set([83, 86, 75, 87, 1], 0);
    result.set(nonce, 5);
    result.set(ciphertextAndTag, 17);
    ciphertextAndTag.fill(0);
    nonce.fill(0);
    return result;
  },
  async unwrap(envelope, wrappingKey, accountId) {
    if (!(envelope instanceof Uint8Array) || envelope.length !== 65 ||
        !(wrappingKey instanceof Uint8Array) || wrappingKey.length !== 32)
      throw new TypeError('Invalid encrypted credential envelope');
    if (![83, 86, 75, 87, 1].every((v, i) => envelope[i] === v))
      throw new Error('Unsupported encrypted credential envelope');
    if (typeof accountId !== 'string' || !/^G[A-Z2-7]{55}$/.test(accountId))
      throw new TypeError('Invalid Stellar public account');
    const aad = new TextEncoder().encode('StellarVault/key-wrap/v1\0' + accountId);
    const key = await crypto.subtle.importKey('raw', wrappingKey, { name: 'AES-GCM' }, false, ['decrypt']);
    const raw = new Uint8Array(await crypto.subtle.decrypt({
      name: 'AES-GCM', iv: envelope.subarray(5, 17), additionalData: aad, tagLength: 128
    }, key, envelope.subarray(17)));
    if (raw.length !== 32) { raw.fill(0); throw new Error('Invalid decrypted data key'); }
    return raw;
  }
});
