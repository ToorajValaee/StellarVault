// Deliberately fail-closed wallet bridge. Never accept an injected arbitrary
// callback or unsigned account assertion as proof of ownership.
// Replace this with an explicitly audited wallet-specific integration only after
// the wallet supports signing the EXACT opaque challenge bytes.
window.stellarVaultV2Wallet = Object.freeze({
  async signChallenge(accountId, networkPassphrase, challengeBase64) {
    if (typeof accountId !== 'string' || typeof networkPassphrase !== 'string' ||
        typeof challengeBase64 !== 'string')
      throw new TypeError('Invalid ownership challenge request');
    throw new Error('No verified Stellar message-signing wallet is configured');
  }
});
