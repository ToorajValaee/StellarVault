// PWA desktop Freighter SEP-53 integration. Never receives or exports a seed.
// The server-side-equivalent Core verifier independently checks signature,
// signer weight and the Horizon network before allowing vault operations.
window.stellarVaultV2Wallet = Object.freeze({
  async connect() {
    const wallet = window.freighterApi;
    if (!wallet) throw new Error('Freighter wallet API is not installed');
    const connected = await wallet.isConnected();
    if (!connected?.isConnected) throw new Error('Freighter browser extension is not connected');
    const access = await wallet.requestAccess();
    if (access?.error || typeof access?.address !== 'string')
      throw new Error('Freighter access was not approved');
    return access.address;
  },

  async signChallenge(accountId, networkPassphrase, challengeBase64) {
    const wallet = window.freighterApi;
    if (!wallet?.signMessage)
      throw new Error('Freighter message signing is unavailable');
    if (typeof accountId !== 'string' || !/^G[A-Z2-7]{55}$/.test(accountId) ||
        typeof networkPassphrase !== 'string' || !networkPassphrase ||
        typeof challengeBase64 !== 'string' || !/^[A-Za-z0-9+/]+={0,2}$/.test(challengeBase64))
      throw new TypeError('Invalid ownership challenge request');
    if (networkPassphrase !== 'Test SDF Network ; September 2015')
      throw new Error('Only Stellar Testnet is enabled for this development preview');
    if (typeof wallet.getNetwork !== 'function')
      throw new Error('Wallet network verification is unavailable');
    const currentNetwork = await wallet.getNetwork();
    if (currentNetwork?.error || currentNetwork?.networkPassphrase !== networkPassphrase)
      throw new Error('Switch Freighter to Stellar Testnet before signing');
    const response = await wallet.signMessage(challengeBase64, { address: accountId });
    if (response?.error || response?.signerAddress !== accountId ||
        typeof response.signedMessage !== 'string')
      throw new Error('Freighter rejected ownership signing');
    return { signerAccountId: response.signerAddress, signatureBase64: response.signedMessage };
  }
});
