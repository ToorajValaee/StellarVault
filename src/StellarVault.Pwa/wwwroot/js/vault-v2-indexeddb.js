// Fresh StellarVault v2 browser persistence. Stores opaque encrypted bytes only.
// Never store plaintext data keys, passphrases, private keys or unlocked sessions.
window.stellarVaultV2Db = (() => {
  const name = 'stellar-vault-v2';
  const version = 2;
  const stores = ['credentials', 'objects', 'pointers', 'syncQueue'];
  let pending;
  function open() {
    if (!pending) pending = new Promise((resolve, reject) => {
      const request = indexedDB.open(name, version);
      request.onupgradeneeded = () => {
        for (const store of stores)
          if (!request.result.objectStoreNames.contains(store))
            request.result.createObjectStore(store);
      };
      request.onsuccess = () => {
        const db = request.result;
        db.onversionchange = () => {
          db.close();
          pending = null; // A closed connection must never be reused.
        };
        db.onclose = () => { pending = null; };
        resolve(db);
      };
      request.onerror = () => reject(request.error);
      request.onblocked = () => reject(new Error('IndexedDB upgrade blocked by another tab'));
    }).catch(error => { pending = null; throw error; });
    return pending;
  }
  function bytes(value) {
    if (!(value instanceof Uint8Array)) throw new TypeError('Uint8Array required');
    return value.slice();
  }
  async function read(store, key) {
    const db = await open();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(store, 'readonly');
      const request = tx.objectStore(store).get(key);
      let result;
      request.onsuccess = () => {
        try {
          result = request.result == null ? null : bytes(request.result);
        } catch (error) {
          tx.abort();
          reject(error);
        }
      };
      // A request succeeding does not mean the transaction committed.
      // Report data only after the complete readonly transaction finishes.
      tx.oncomplete = () => resolve(result);
      request.onerror = () => reject(request.error || new Error('IndexedDB read failed'));
      tx.onabort = () => reject(tx.error || request.error || new Error('IndexedDB read aborted'));
      tx.onerror = () => {};
    });
  }
  async function putImmutable(store, key, value) {
    const db = await open();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(store, 'readwrite');
      let inserted = false;
      const request = tx.objectStore(store).add(bytes(value), key);
      request.onsuccess = () => { inserted = true; };
      request.onerror = event => {
        if (request.error?.name === 'ConstraintError') {
          event.preventDefault();
          event.stopPropagation();
        }
      };
      tx.oncomplete = () => resolve(inserted);
      tx.onabort = () => reject(tx.error || request.error || new Error('IndexedDB immutable write aborted'));
      tx.onerror = () => {};
    });
  }
  function equal(a, b) {
    if (!(a instanceof Uint8Array) || !(b instanceof Uint8Array) || a.length !== b.length)
      return false;
    let diff = 0;
    for (let i = 0; i < a.length; i++) diff |= a[i] ^ b[i];
    return diff === 0;
  }
  async function compareExchange(store, key, expected, replacement) {
    const db = await open();
    const next = bytes(replacement);
    return new Promise((resolve, reject) => {
      const tx = db.transaction(store, 'readwrite');
      let changed = false;
      const objectStore = tx.objectStore(store);
      const request = objectStore.get(key);
      request.onsuccess = () => {
        const current = request.result;
        const matches = expected === null ? current === undefined :
          current !== undefined && equal(bytes(current), expected);
        if (!matches) return;
        objectStore.put(next, key);
        changed = true;
      };
      request.onerror = () => reject(request.error || new Error('IndexedDB pointer read failed'));
      tx.oncomplete = () => resolve(changed);
      tx.onabort = () => reject(tx.error || request.error || new Error('IndexedDB compare-exchange aborted'));
      tx.onerror = () => {};
    });
  }
  async function removeObject(key) {
    const db = await open();
    return new Promise((resolve, reject) => {
      const tx = db.transaction('objects', 'readwrite');
      const request = tx.objectStore('objects').delete(key);
      request.onerror = () => reject(request.error || new Error('IndexedDB delete failed'));
      tx.oncomplete = () => resolve();
      tx.onabort = () => reject(tx.error || request.error || new Error('IndexedDB delete aborted'));
      tx.onerror = () => {};
    });
  }
  return Object.freeze({
    readQueue: key => read('syncQueue', key),
    addQueue: (key, data) => putImmutable('syncQueue', key, data),
    replaceQueue: (key, previous, next) => compareExchange('syncQueue', key, previous, next),
    readCredential: key => read('credentials', key),
    createCredential: (key, data) => putImmutable('credentials', key, data),
    replaceCredential: (key, oldData, data) => compareExchange('credentials', key, oldData, data),
    readObject: key => read('objects', key),
    createObject: (key, data) => putImmutable('objects', key, data),
    deleteObject: removeObject,
    readPointer: key => read('pointers', key),
    compareExchangePointer: (key, oldData, data) => compareExchange('pointers', key, oldData, data)
  });
})();
