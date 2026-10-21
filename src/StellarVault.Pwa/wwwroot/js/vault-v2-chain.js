// Bounded, read-only discovery of SEP-5 child accounts for an entered mnemonic.
// No mnemonic/seed/private key is persisted or sent to Horizon.
// Registry-based recovery and legacy derivation fallback are separate milestones.
window.stellarVaultV2Chain = Object.freeze({
  async discover(mnemonic, network, count = 16) {
    if (!Number.isInteger(count) || count < 1 || count > 32)
      throw new RangeError('Invalid child-account scan limit');
    const endpoints = {
      testnet: ['https://horizon-testnet.stellar.org', 'Test SDF Network ; September 2015'],
      public: ['https://horizon.stellar.org', 'Public Global Stellar Network ; September 2015']
    };
    if (!Object.prototype.hasOwnProperty.call(endpoints, network))
      throw new Error('Unsupported network');
    const [url, expectedPassphrase] = endpoints[network];
    const response = await fetch(url + '/', { credentials: 'omit', cache: 'no-store', redirect: 'error' });
    if (!response.ok) throw new Error('Horizon network unavailable');
    const metadata = await response.json();
    if (metadata.network_passphrase !== expectedPassphrase)
      throw new Error('Horizon network mismatch');

    const seed = await window.stellarVaultHd.mnemonicSeed(mnemonic);
    const found = [];
    try {
      const sdk = window.StellarSdk;
      if (!sdk?.Keypair?.fromRawEd25519Seed)
        throw new Error('Stellar derivation SDK unavailable');
      for (let index = 1; index <= count; index++) {
        const raw = await window.stellarVaultHd.accountSeed(seed, index, 'sep5');
        let address;
        try { address = sdk.Keypair.fromRawEd25519Seed(raw).publicKey(); }
        finally { raw.fill(0); }
        const accountResponse = await fetch(url + '/accounts/' + encodeURIComponent(address), {
          credentials: 'omit', cache: 'no-store', redirect: 'error'
        });
        if (accountResponse.status === 404) continue;
        if (!accountResponse.ok) throw new Error('Child account lookup failed');
        const account = await accountResponse.json();
        if (account.id !== address) throw new Error('Horizon account mismatch');
        found.push(index + ': ' + address);
      }
      return found;
    } finally { seed.fill(0); }
  }
});
