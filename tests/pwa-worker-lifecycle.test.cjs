const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const crypto = require('node:crypto');
const source = fs.readFileSync(path.join(__dirname, '../src/StellarVault.Pwa/wwwroot/service-worker.published.js'), 'utf8');
function harness({ failInstall = false, existing = false } = {}) {
  const scope = 'https://app.svault.ir/';
  const stores = new Map();
  const requested = [];
  let claimed = 0;
  let skipped = 0;
  const normalize = request => new URL(typeof request === 'string' ? request : request.url, scope).href;
  const assets = ['index.html', 'js/app.js', 'css/app.css', '_framework/data.dat'];
  const currentName = 'stellar-vault-cache-release-one';
  if (existing) stores.set(currentName, new Map([[scope + 'index.html', 'verified-old-shell']]));
  const caches = {
    keys: async () => [...stores.keys()], delete: async key => stores.delete(key),
    open: async key => {
      if (!stores.has(key)) stores.set(key, new Map());
      const entries = stores.get(key);
      return {
        addAll: async requests => {
          if (failInstall) throw new Error('cache quota');
          for (const request of requests) {
            assert.ok(request.integrity.startsWith('sha256-'));
            assert.equal(request.credentials, 'omit');
            assert.equal(request.redirect, 'error');
            entries.set(normalize(request), request.url.endsWith('/index.html') ? 'release-one-shell' : 'release-one-asset');
          }
        },
        match: async request => entries.has(normalize(request)) ? new Response(entries.get(normalize(request))) : undefined
      };
    }
  };
  const context = vm.createContext({ URL, Request, Response, Map, Array, Promise, Error, caches,
    fetch: async request => { requested.push(request); return new Response('network'); },
    self: { registration: { scope }, assetsManifest: { version: 'release-one', assets: assets.map(url => ({ url,
      hash: 'sha256-' + crypto.createHash('sha256').update(url).digest('base64') })) },
      importScripts() {}, addEventListener() {}, skipWaiting: async () => { skipped++; },
      clients: { claim: async () => { claimed++; } } }
  });
  vm.runInContext(source, context);
  const request = (url, options = {}) => ({ url: new URL(url, scope).href, method: options.method || 'GET',
    headers: new Headers(options.headers), mode: options.mode || 'cors', signal: new AbortController().signal });
  return { context, stores, requested, request, currentName, get skipped() { return skipped; }, get claimed() { return claimed; } };
}

test('verified worker pins its shell and known query-versioned public assets without hot activation', async () => {
  const h = harness();
  await h.context.onInstall();
  const shell = await h.context.onFetch({ request: h.request('/vault', { mode: 'navigate' }) });
  assert.equal(await shell.text(), 'release-one-shell');
  const css = await h.context.onFetch({ request: h.request('/css/app.css?v=new-release') });
  assert.equal(await css.text(), 'release-one-asset');
  assert.equal(h.requested.length, 0);
  assert.equal(h.skipped, 0);
  await h.context.onActivate();
  assert.equal(h.claimed, 0);
});

test('failed precache removes only a new release and preserves a working same-version cache', async () => {
  const fresh = harness({ failInstall: true });
  await assert.rejects(fresh.context.onInstall(), /quota/);
  assert.ok(!fresh.stores.has(fresh.currentName));
  const existing = harness({ failInstall: true, existing: true });
  await assert.rejects(existing.context.onInstall(), /quota/);
  assert.equal(existing.stores.get(existing.currentName).get('https://app.svault.ir/index.html'), 'verified-old-shell');
});

test('worker never caches API, authenticated, cross-origin or mutation requests', async () => {
  const h = harness();
  await h.context.onInstall();
  for (const request of [h.request('/v1/networks', { mode: 'navigate' }),
    h.request('/js/app.js', { headers: { Authorization: 'Bearer synthetic' } }),
    h.request('https://api.svault.ir/v1/networks'), h.request('/vault', { method: 'POST' })]) {
    const response = await h.context.onFetch({ request });
    assert.equal(await response.text(), 'network');
  }
  assert.equal(h.requested.length, 4);
});

test('missing pinned assets require original integrity and a missing shell fails closed', async () => {
  const h = harness();
  await h.context.onInstall();
  h.stores.get(h.currentName).delete('https://app.svault.ir/js/app.js');
  await h.context.onFetch({ request: h.request('/js/app.js') });
  assert.match(h.requested[0].integrity, /^sha256-/);
  assert.equal(h.requested[0].redirect, 'error');
  h.stores.get(h.currentName).delete('https://app.svault.ir/index.html');
  await assert.rejects(h.context.onFetch({ request: h.request('/vault', { mode: 'navigate' }) }), /shell is missing/);
});
