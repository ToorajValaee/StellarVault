// Stores only random record IDs, a fingerprint and stage index.
// No file bytes, mnemonic, passphrase, key or plaintext draft is persisted.
// These metadata are NOT encrypted; avoid storing sensitive filenames here.
window.stellarVaultUploadCheckpoint = (() => {
  const prefix = 'sv-upload-checkpoint-v1:';
  function key(account) {
    if (!/^[A-Z0-9]{56}$/.test(account || '')) throw new Error('Invalid account for checkpoint.');
    return prefix + account;
  }
  function load(account) {
    try {
      const value = localStorage.getItem(key(account));
      if (value === null) return null;
      const state = JSON.parse(value);
      if (state?.version !== 1 || !Array.isArray(state.ids) ||
          !Number.isInteger(state.step) || state.step < 0 || state.step > state.ids.length ||
          typeof state.fingerprint !== 'string' || !/^[A-F0-9]{64}$/.test(state.fingerprint) ||
          state.ids.length < 1 || state.ids.length > 9 ||
          state.ids.some(id => typeof id !== 'string' || !/^[a-f0-9]{16}$/.test(id)) ||
          new Set(state.ids).size !== state.ids.length)
        throw new Error('Invalid upload checkpoint.');
      return state;
    } catch (error) { throw new Error('Invalid upload checkpoint; publishing is blocked until recovery.', {cause:error}); }
  }
  function save(account, fingerprint, ids, step) {
    if (!/^[A-F0-9]{64}$/.test(fingerprint) || !Array.isArray(ids) ||
        ids.length < 1 || ids.length > 9 ||
        ids.some(id => !/^[a-f0-9]{16}$/.test(id)) ||
        new Set(ids).size !== ids.length ||
        !Number.isInteger(step) || step < 0 || step > ids.length)
      throw new Error('Invalid checkpoint state.');
    // Refuse identity substitution or step rollback. load() throws on corruption,
    // so a damaged checkpoint cannot be overwritten as though it were absent.
    const previous = load(account);
    if (previous && (previous.fingerprint !== fingerprint ||
        previous.ids.length !== ids.length ||
        previous.ids.some((id, index) => id !== ids[index]) ||
        step < previous.step))
      throw new Error('Upload checkpoint identity changed or progress rolled back; reconcile before retry.');
    localStorage.setItem(key(account), JSON.stringify({version: 1, fingerprint, ids, step}));
  }
  function clear(account) { localStorage.removeItem(key(account)); }
  return Object.freeze({load, save, clear});
})();
