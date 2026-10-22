// Stores only public account identity and public network endpoint.
// The custom network passphrase is re-fetched and verified after reload.
window.stellarVaultRecentVault = (() => {
  const key = 'stellar-vault-v2-recent-account';
  const valid = a => typeof a === 'string' && /^G[A-Z2-7]{55}$/.test(a);
  function endpoint(value) {
    try {
      const url = new URL(value);
      const loopback = ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname);
      if (!(url.protocol === 'https:' || (loopback && url.protocol === 'http:')) ||
          url.username || url.password || url.search || url.hash ||
          url.pathname !== '/' || url.origin === 'null') return null;
      return url.origin + '/';
    } catch { return null; }
  }
  function load() {
    try {
      const raw = localStorage.getItem(key);
      if (raw === null) return null;
      const value = JSON.parse(raw);
      if (!valid(value?.account) || !['testnet', 'public', 'custom'].includes(value.network) ||
          (value.network === 'custom' && !endpoint(value.horizonUrl))) return null;
      return { account: value.account, network: value.network,
        horizonUrl: value.network === 'custom' ? endpoint(value.horizonUrl) : null };
    } catch { return null; }
  }
  function save(account, network, horizonUrl = null) {
    if (!valid(account) || !['testnet', 'public', 'custom'].includes(network))
      throw new Error('Invalid remembered account or network');
    const url = network === 'custom' ? endpoint(horizonUrl) : null;
    if (network === 'custom' && !url) throw new Error('Invalid custom Horizon endpoint');
    localStorage.setItem(key, JSON.stringify({ version: 2, account, network, horizonUrl: url }));
  }
  function clear() { localStorage.removeItem(key); }
  return Object.freeze({ load, save, clear });
})();
