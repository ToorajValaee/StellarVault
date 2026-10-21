// SEP-5-only local derivation; Horizon receives only the public account address.
// Network failures must never make encrypted local records inaccessible.
window.stellarVaultV2Mnemonic = Object.freeze({
  async resolveRoot(mnemonic, network) {
    if (typeof mnemonic !== 'string' || mnemonic.length > 4096)
      throw new TypeError('Invalid recovery phrase input');
    if (!window.stellarVaultHd || !window.StellarSdk)
      throw new Error('Mnemonic derivation dependencies were not loaded');
    const networks = Object.freeze({
      testnet: ['https://horizon-testnet.stellar.org', 'Test SDF Network ; September 2015'],
      public: ['https://horizon.stellar.org', 'Public Global Stellar Network ; September 2015']
    });
    if (!Object.prototype.hasOwnProperty.call(networks, network))
      throw new Error('Unsupported network');
    const validated = await window.stellarVaultHd.validateMnemonic(mnemonic);
    const seed = await window.stellarVaultHd.mnemonicSeed(validated);
    let account;
    try {
      const raw = await window.stellarVaultHd.accountSeed(seed, 0, 'sep5');
      try { account = window.StellarSdk.Keypair.fromRawEd25519Seed(raw).publicKey(); }
      finally { raw.fill(0); }
    } finally { seed.fill(0); }

    const [base, expected] = networks[network];
    const read = async url => {
      const aborter = new AbortController();
      const timeout = setTimeout(() => aborter.abort(), 12000);
      try {
        const response = await fetch(url, {
          credentials: 'omit', cache: 'no-store', redirect: 'error',
          headers: { Accept: 'application/json' }, signal: aborter.signal
        });
        if (response.status === 404) return null;
        if (!response.ok) throw new Error('Horizon HTTP ' + response.status);
        return await response.json();
      } finally { clearTimeout(timeout); }
    };
    try {
      const info = await read(base + '/');
      // A network identity mismatch is not an outage: do not accept that
      // endpoint as authoritative even for a read-only account discovery.
      if (!info || info.network_passphrase !== expected)
        return { account, found: false, networkAvailable: false };
      const root = await read(base + '/accounts/' + encodeURIComponent(account));
      if (root && root.id !== account)
        return { account, found: false, networkAvailable: false };
      return { account, found: root !== null, networkAvailable: true };
    } catch {
      return { account, found: false, networkAvailable: false };
    }
  }
});
