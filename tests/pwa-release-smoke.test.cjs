const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '..', 'src', 'StellarVault.Pwa', 'wwwroot');
const page = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
test('startup script files exist and Blazor starts last', () => {
  const assets = ['js/vendor/stellar-sdk.min.js', 'js/hd-keys.js', 'js/vault-v2-keywrap.js', 'js/vault-v2-indexeddb.js', 'js/vault-v2-lifecycle.js'];
  let prior = -1;
  for (const name of assets) {
    const index = page.indexOf('src="' + name + '"');
    assert.ok(index > prior, name + ' must appear after dependencies');
    assert.ok(fs.existsSync(path.join(root, name)), name + ' must exist');
    prior = index;
  }
  assert.ok(page.indexOf('src="_framework/blazor.webassembly.js"') > prior);
});
test('published service worker caches before activation', () => {
  const worker = fs.readFileSync(path.join(root, 'service-worker.published.js'), 'utf8');
  assert.ok(worker.indexOf('await cache.addAll(assetsRequests)') < worker.indexOf('await self.skipWaiting()'));
});

test('IndexedDB reads wait for transaction completion', () => {
  const code = fs.readFileSync(path.join(root, 'js/vault-v2-indexeddb.js'), 'utf8');
  const start = code.indexOf('async function read(store, key)');
  const end = code.indexOf('async function putImmutable(', start);
  assert.ok(start >= 0 && end > start);
  const method = code.slice(start, end);
  assert.ok(method.includes('tx.oncomplete = () => resolve(result)'));
  assert.ok(method.includes('tx.onabort = () => reject('));
  assert.ok(!method.includes('request.onsuccess = () => resolve('));
});
