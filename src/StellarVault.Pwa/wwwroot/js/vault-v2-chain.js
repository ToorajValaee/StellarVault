// Registry-aware, read-only account discovery. Only public addresses reach Horizon.
// Validates the authenticated original SVault registry and scans a bounded lookahead.
window.stellarVaultV2Chain = Object.freeze({
  async discover(mnemonic, network, scheme = 'sep5', lookahead = 16) {
    if (scheme !== 'sep5' && scheme !== 'bip32-compat')
      throw new Error('Unsupported root derivation scheme');
    if (!Number.isInteger(lookahead) || lookahead < 1 || lookahead > 32)
      throw new RangeError('Invalid recovery lookahead');
    const networks = Object.freeze({
      testnet: ['https://horizon-testnet.stellar.org', 'Test SDF Network ; September 2015'],
      public: ['https://horizon.stellar.org', 'Public Global Stellar Network ; September 2015']
    });
    if (!Object.prototype.hasOwnProperty.call(networks, network))
      throw new Error('Unsupported network');
    if (!window.stellarVaultRegistry || !window.stellarVaultHd || !window.StellarSdk)
      throw new Error('HD recovery dependencies unavailable');
    const [baseUrl, networkPassphrase] = networks[network];
    const fetchAccount = async (address) => {
      const controller = new AbortController();
      const timeout = setTimeout(() => controller.abort(), 12000);
      try {
        const response = await fetch(baseUrl + '/accounts/' + encodeURIComponent(address), {
          credentials: 'omit', cache: 'no-store', redirect: 'error',
          headers: { Accept: 'application/json' }, signal: controller.signal
        });
        if (response.status === 404) return null;
        if (!response.ok) throw new Error('Horizon lookup failed (' + response.status + ')');
        const account = await response.json();
        if (account.id !== address) throw new Error('Horizon returned another account');
        return account;
      } finally { clearTimeout(timeout); }
    };
    const seed = await window.stellarVaultHd.mnemonicSeed(mnemonic);
    try {
      const addressFor = async (index) => {
        const raw = await window.stellarVaultHd.accountSeed(seed, index, scheme);
        try { return window.StellarSdk.Keypair.fromRawEd25519Seed(raw).publicKey(); }
        finally { raw.fill(0); }
      };
      const rootAddress = await addressFor(0);
      const root = await fetchAccount(rootAddress);
      if (!root) return [];
      const macKey = await window.stellarVaultRegistry.registryMacKey(seed, networkPassphrase, rootAddress);
      let highest = 0;
      const encoded = root.data_attr?.['sv:registry:v2'];
      if (encoded) {
        const envelope = Uint8Array.from(atob(encoded), c => c.charCodeAt(0));
        try {
          const registry = await window.stellarVaultRegistry.decode(macKey, envelope);
          highest = registry.highestAllocatedIndex;
        } finally { envelope.fill(0); }
      }
      const MAX_SCAN = 256; // Defensive preview cap; larger registries require pagination.
      if (highest > MAX_SCAN)
        throw new Error('Registry exceeds current scan cap; recovery requires paginated discovery');
      const limit = Math.min(MAX_SCAN, highest + lookahead);
      const found = [];
      for (let start = 1; start <= limit; start += 8) {
        const batch = Array.from({ length: Math.min(8, limit - start + 1) },
          (_, offset) => start + offset);
        const results = await Promise.all(batch.map(async index => {
          const address = await addressFor(index);
          return (await fetchAccount(address)) ? index + ': ' + address : null;
        }));
        for (const item of results) if (item !== null) found.push(item);
      }
      return found;
    } finally { seed.fill(0); }
  }
});
