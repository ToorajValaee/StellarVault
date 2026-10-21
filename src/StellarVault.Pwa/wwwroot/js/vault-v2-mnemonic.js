// Derive a public account locally; network lookup receives public addresses only.
// SEP-5 only: no obsolete alternative derivation schemes.
window.stellarVaultV2Mnemonic = Object.freeze({
  async resolveRoot(mnemonic, network) {
    if (typeof mnemonic !== 'string' || mnemonic.length > 4096)
      throw new TypeError('Invalid recovery phrase input');
    if (!window.stellarVaultHd || !window.StellarSdk)
      throw new Error('Mnemonic derivation dependencies were not loaded');
    const networks = {
      testnet: ['https://horizon-testnet.stellar.org', 'Test SDF Network ; September 2015'],
      public: ['https://horizon.stellar.org', 'Public Global Stellar Network ; September 2015']
    };
    if (!Object.prototype.hasOwnProperty.call(networks, network))
      throw new Error('Unsupported network');
    const validated = await window.stellarVaultHd.validateMnemonic(mnemonic);
    const [base, passphrase] = networks[network];
    const fetchJson = async url => {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), 12000);
      try {
        const response = await fetch(url, {
          credentials: 'omit', cache: 'no-store', redirect: 'error',
          headers: { Accept: 'application/json' }, signal: controller.signal
        });
        if (response.status === 404) return null;
        if (!response.ok) throw new Error('Horizon returned HTTP ' + response.status);
        return await response.json();
      } finally { clearTimeout(timer); }
    };
    const info = await fetchJson(base + '/');
    if (!info || info.network_passphrase !== passphrase)
      throw new Error('Horizon network identity mismatch');
    const seed = await window.stellarVaultHd.mnemonicSeed(validated);
    try {
      const derive = async () => {
        const raw = await window.stellarVaultHd.accountSeed(seed, 0, 'sep5');
        try { return window.StellarSdk.Keypair.fromRawEd25519Seed(raw).publicKey(); }
        finally { raw.fill(0); }
      };
      const primary = await derive();
      if (await fetchJson(base + '/accounts/' + encodeURIComponent(primary)))
        return { account: primary, found: true };
      // Keep local vault access possible for an unfunded SEP-5 root.
      return { account: primary, scheme: 'sep5', found: false };
    } finally { seed.fill(0); }
  }
});
