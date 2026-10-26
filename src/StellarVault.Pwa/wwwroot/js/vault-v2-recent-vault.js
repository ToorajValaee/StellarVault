// Stores public account and network identity only. Stellar network passphrases
// identify chains; they are public consensus metadata, NOT vault credentials.
// Online ledger connections separately revalidate Horizon before signing.
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
        horizonUrl: value.network === 'custom' ? endpoint(value.horizonUrl) : null,
        networkPassphrase: value.network === 'custom' && typeof value.networkPassphrase === 'string' &&
          value.networkPassphrase.trim() && value.networkPassphrase.length <= 512
          ? value.networkPassphrase : null };
    } catch { return null; }
  }
  function save(account, network, horizonUrl = null, networkPassphrase = null) {
    if (!valid(account) || !['testnet', 'public', 'custom'].includes(network))
      throw new Error('Invalid remembered account or network');
    const url = network === 'custom' ? endpoint(horizonUrl) : null;
    if (network === 'custom' && (!url || typeof networkPassphrase !== 'string' ||
      !networkPassphrase.trim() || networkPassphrase.length > 512))
      throw new Error('Invalid custom network identity');
    localStorage.setItem(key, JSON.stringify({ version: 3, account, network, horizonUrl: url,
      networkPassphrase: network === 'custom' ? networkPassphrase : null }));
  }
  function clear() { localStorage.removeItem(key); }
  return Object.freeze({ load, save, clear });
})();
