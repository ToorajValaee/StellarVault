// Read-only network discovery. Never send a mnemonic, seed, passphrase or secret key.
// Horizon sees only the derived public address, as required for public-account lookup.
window.stellarVaultV2Network = Object.freeze({
  async inspectAccount(account, network) {
    if (typeof account !== 'string' || !/^G[A-Z2-7]{55}$/.test(account))
      throw new Error('A valid derived public account is required');
    const options = Object.freeze({
      testnet: { base: 'https://horizon-testnet.stellar.org', passphrase: 'Test SDF Network ; September 2015' },
      public: { base: 'https://horizon.stellar.org', passphrase: 'Public Global Stellar Network ; September 2015' }
    });
    if (!Object.hasOwn(options, network)) throw new Error('Unsupported Stellar network');
    const selected = options[network];
    const fetchJson = async url => {
      const controller = new AbortController();
      const timeout = setTimeout(() => controller.abort(), 12000);
      try {
        const response = await fetch(url, {
          method: 'GET',
          mode: 'cors',
          cache: 'no-store',
          credentials: 'omit',
          redirect: 'error',
          headers: { Accept: 'application/json' },
          signal: controller.signal
        });
        if (response.status === 404) return { missing: true };
        if (!response.ok) throw new Error('Horizon returned HTTP ' + response.status);
        if (Number(response.headers.get('Content-Length') || 0) > 131072)
          throw new Error('Horizon response was too large');
        return { data: await response.json() };
      } finally { clearTimeout(timeout); }
    };
    const root = await fetchJson(selected.base + '/');
    if (root.missing || root.data?.network_passphrase !== selected.passphrase)
      throw new Error('Horizon network identity mismatch');
    const record = await fetchJson(selected.base + '/accounts/' + encodeURIComponent(account));
    if (record.missing) return false;
    if (record.data?.id !== account) throw new Error('Horizon returned a mismatched account');
    return true;
  }
});
