// Only public account selection metadata is retained across page reloads.
// Never store mnemonic, passphrase, derived seed or unlocked encryption keys.
window.stellarVaultRecentVault = (() => {
  const key = 'stellar-vault-v2-recent-account';
  const valid = a => typeof a === 'string' && /^G[A-Z2-7]{55}$/.test(a);
  function load() {
    try {
      const raw = localStorage.getItem(key);
      if (raw === null) return null;
      const value = JSON.parse(raw);
      if (value?.version !== 1 || !valid(value.account) ||
          !['testnet', 'public'].includes(value.network))
        throw new Error('Invalid recent vault metadata');
      return value;
    } catch { return null; }
  }
  function save(account, network) {
    if (!valid(account) || !['testnet', 'public'].includes(network))
      return; // Custom network identity must not be restored without detection.
    localStorage.setItem(key, JSON.stringify({ version: 1, account, network }));
  }
  function clear() { localStorage.removeItem(key); }
  return Object.freeze({ load, save, clear });
})();
