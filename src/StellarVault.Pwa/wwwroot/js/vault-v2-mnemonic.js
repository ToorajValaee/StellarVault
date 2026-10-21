// Reuse original StellarVault BIP-39 checksum validation and SEP-5 index-0
// derivation. Only the public account address crosses into .NET.
// No mnemonic, seed, or secret-key material is saved in browser storage.
window.stellarVaultV2Mnemonic = Object.freeze({
  async rootPublicKey(mnemonic) {
    if (typeof mnemonic !== 'string' || mnemonic.length > 4096)
      throw new TypeError('Invalid recovery phrase input');
    if (!window.stellarVaultHd || !window.StellarSdk)
      throw new Error('Mnemonic derivation dependencies were not loaded');
    const validated = await window.stellarVaultHd.validateMnemonic(mnemonic);
    // Preserve the original application's index-0 SEP-5 derivation.
    // Legacy BIP32 fallback requires network-backed root discovery later.
    const pair = await window.stellarVaultHd.deriveKeypair(validated, 0);
    return pair.publicKey();
  }
});
