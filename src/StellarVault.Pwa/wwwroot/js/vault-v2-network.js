// Read-only Horizon discovery. Never send mnemonic, seed, encryption passphrase or key.
window.stellarVaultV2Network = Object.freeze({
  async probeCustom(horizonUrl) {
    const parsed = new URL(String(horizonUrl || '').trim());
    // HTTPS required except local loopback testing from the developer's browser.
    const local = ['localhost', '127.0.0.1', '[::1]'].includes(parsed.hostname);
    if ((parsed.protocol !== 'https:' && !(local && parsed.protocol === 'http:')) ||
        parsed.username || parsed.password || parsed.search || parsed.hash ||
        parsed.pathname !== '/' || parsed.origin === 'null')
      throw new Error('Use a direct HTTPS Horizon root URL (HTTP only for localhost)');
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 12000);
    try {
      const response = await fetch(parsed.href, {
        method: 'GET', mode: 'cors', cache: 'no-store',
        credentials: 'omit', redirect: 'error',
        headers: { Accept: 'application/json' }, signal: controller.signal
      });
      if (!response.ok) throw new Error('Horizon HTTP ' + response.status);
      const metadata = await response.json();
      const passphrase = metadata.network_passphrase;
      if (typeof passphrase !== 'string' || !passphrase.trim() || passphrase.length > 512)
        throw new Error('Horizon did not return a valid network passphrase');
      return passphrase;
    } finally { clearTimeout(timeout); }
  },
  async inspectAccount(account, network, customUrl = null, customPassphrase = null) {
    if (typeof account !== 'string' || !/^G[A-Z2-7]{55}$/.test(account))
      throw new Error('A valid public account is required');
    const options = {
      testnet: { base: 'https://horizon-testnet.stellar.org', passphrase: 'Test SDF Network ; September 2015' },
      public: { base: 'https://horizon.stellar.org', passphrase: 'Public Global Stellar Network ; September 2015' }
    };
    let selected;
    if (network === 'custom') {
      const detected = await this.probeCustom(customUrl);
      if (detected !== customPassphrase) throw new Error('Custom network identity changed');
      selected = { base: new URL(customUrl).origin, passphrase: detected };
    } else {
      selected = options[network];
      if (!selected) throw new Error('Unsupported Stellar network');
    }
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 12000);
    try {
      const response = await fetch(selected.base + '/accounts/' + encodeURIComponent(account), {
        method: 'GET', mode: 'cors', cache: 'no-store',
        credentials: 'omit', redirect: 'error',
        headers: { Accept: 'application/json' }, signal: controller.signal
      });
      if (response.status === 404) return false;
      if (!response.ok) throw new Error('Horizon returned HTTP ' + response.status);
      const record = await response.json();
      if (record.id !== account) throw new Error('Horizon returned a mismatched account');
      return true;
    } finally { clearTimeout(timeout); }
  }
});
