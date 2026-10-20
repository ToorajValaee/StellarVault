// Fresh StellarVault v2 browser persistence. Stores opaque encrypted bytes only.
// Never store plaintext data keys, passphrases, private keys or unlocked sessions.
window.stellarVaultV2Db = (() => {
  const name = 'stellar-vault-v2';
  const version = 1;
  const stores = ['credentials', 'objects', 'pointers'];
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
      request.onsuccess = () => resolve(request.result == null ? null : bytes(request.result));
      request.onerror = () => reject(request.error);
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
      tx.onabort = () => reject(tx.error);
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
      tx.oncomplete = () => resolve(changed);
      tx.onabort = () => reject(tx.error);
    });
  }
  async function removeObject(key) {
    const db = await open();
    return new Promise((resolve, reject) => {
      const tx = db.transaction('objects', 'readwrite');
      tx.objectStore('objects').delete(key);
      tx.oncomplete = () => resolve();
      tx.onabort = () => reject(tx.error);
    });
  }
  return Object.freeze({
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
