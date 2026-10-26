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

test('IndexedDB errors propagate for every encrypted storage operation', () => {
  const code = fs.readFileSync(path.join(root, 'js/vault-v2-indexeddb.js'), 'utf8');
  for (const [begin, end] of [
    ['async function read(store, key)', 'async function putImmutable('],
    ['async function putImmutable(', 'function equal('],
    ['async function compareExchange(', 'async function removeObject('],
    ['async function removeObject(', 'return Object.freeze(']
  ]) {
    const start = code.indexOf(begin);
    const stop = code.indexOf(end, start + begin.length);
    assert.ok(start >= 0 && stop > start, begin + ' must be present');
    const body = code.slice(start, stop);
    assert.ok(body.includes('tx.oncomplete'), begin + ' must await commit');
    assert.ok(body.includes('tx.onabort'), begin + ' must reject abort');
  }
  for (const error of ['IndexedDB read failed', 'IndexedDB immutable write aborted',
    'IndexedDB pointer read failed', 'IndexedDB delete failed']) {
    assert.ok(code.includes(error), error + ' must propagate');
  }
});

test('offline navigation falls back to verified release shell', () => {
  const worker = fs.readFileSync(path.join(root, 'service-worker.published.js'), 'utf8');
  const navigation = worker.indexOf("event.request.mode === 'navigate'");
  assert.ok(navigation >= 0);
  const branch = worker.slice(navigation);
  assert.ok(branch.includes('await fetch(event.request)'));
  assert.ok(branch.includes("await cache.match('index.html')"));
  assert.ok(branch.indexOf('await fetch(event.request)') < branch.indexOf("await cache.match('index.html')"));
});

test('offline bundle does not require debug symbols', () => {
  const worker = fs.readFileSync(path.join(root, 'service-worker.published.js'), 'utf8');
  const include = worker.split('const offlineAssetsInclude = ')[1]?.split(';')[0];
  assert.ok(include, 'precache allowlist must be present');
  assert.ok(!include.includes('pdb'), 'PDB symbols must not be mandatory offline assets');
});

test('public Stellar network access requires explicit acknowledgement', () => {
  const component = fs.readFileSync(path.join(root, '..', 'Components', '..', '..', 'StellarVault.UI', 'Components', 'VaultHome.razor'), 'utf8');
  assert.ok(component.includes('MAINNET // REAL ASSETS AND NETWORK FEES'));
  assert.ok(component.includes('_network == "public" && !_confirmMainnetAccess'));
  assert.ok(component.includes('_confirmMainnetAccess = false;'));
});

test('canonical vault keeps the original ledger browser, never substitutes a local editor', () => {
  const pages = path.join(root, '..', 'Pages');
  const vault = fs.readFileSync(path.join(pages, 'Vault.razor'), 'utf8');
  const app = fs.readFileSync(path.join(root, '..', 'App.razor'), 'utf8');
  assert.ok(vault.includes('@page "/vault"'));
  assert.ok(vault.includes('VAULT RECORDS'));
  assert.ok(vault.includes('FOLDERS'));
  assert.ok(vault.includes('search-console'));
  assert.ok(vault.includes('Ledger.ReadAsync()'));
  assert.ok(!vault.includes('Offline local vault'));
  assert.ok(!vault.includes('<VaultRecordWorkspaceView />'));
  assert.ok(!vault.includes('<VaultBinaryFilesView />'));
  assert.ok(app.includes('Navigation.NavigateTo("/vault")'));
});

test('unlocked PWA session redirects from access to canonical vault without duplicate workspace', () => {
  const index = fs.readFileSync(path.join(root, '..', 'Pages', 'Index.razor'), 'utf8');
  const access = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.UI', 'Components', 'VaultHome.razor'), 'utf8');
  assert.ok(index.includes('Navigation.NavigateTo("/vault", replace: true)'));
  assert.ok(index.includes('if (!Sessions.IsUnlocked)'));
  assert.ok(access.includes('Sessions.IsUnlocked && Platform != "WEB PWA"'));
});

test('disconnected ledger reconnect ends local session before returning to access', () => {
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  assert.ok(vault.includes('@onclick="ReconnectAsync"'));
  const start = vault.indexOf('private async Task ReconnectAsync()');
  const end = vault.indexOf('private async Task LockAsync()', start);
  assert.ok(start > 0 && end > start);
  const reconnect = vault.slice(start, end);
  assert.ok(reconnect.includes('Workflow.Lock()'));
  assert.ok(reconnect.includes('Ledger.DisconnectAsync()'));
  assert.ok(reconnect.includes('Nav.NavigateTo("/", replace: true)'));
});

test('saved local unlock navigates to the vault and disconnected state remains usable', () => {
  const access = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.UI', 'Components', 'VaultHome.razor'), 'utf8');
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  const reopening = access.slice(access.indexOf('private async Task ReopenLocalAsync()'), access.indexOf('private async Task ForgetRecentAsync()'));
  assert.ok(reopening.includes('Navigation.NavigateTo("/vault")'));
  assert.ok(vault.includes('STELLAR SIGNING SESSION NOT RESTORED'));
  assert.ok(vault.includes('CONNECT STELLAR'));
});

test('offline vault keeps the normal ledger browser layout instead of an alternate editor', () => {
 const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
 assert.ok(vault.includes('else if (Ledger.IsConnected && _registryReady != true)'));
 assert.ok(vault.includes('search-console'));
 assert.ok(vault.includes('sv-folder-grid'));
 assert.ok(vault.includes('record-list'));
 assert.ok(vault.includes('STELLAR SIGNING SESSION NOT RESTORED'));
 assert.ok(!vault.includes('else if (!Ledger.IsConnected)'));
});

test('refresh without signing credentials is not mislabeled as offline', () => {
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  assert.ok(vault.includes('NOT SIGNED IN'));
  assert.ok(vault.includes('browser refresh ends the in-memory Stellar signing session'));
  assert.ok(!vault.includes('STELLAR DATA UNAVAILABLE OFFLINE'));
});

test('explicit-lock policy keeps session while switching tabs and requires mnemonic on reload', () => {
  const lifecycle = fs.readFileSync(path.join(root, 'js', 'vault-v2-lifecycle.js'), 'utf8');
  const app = fs.readFileSync(path.join(root, '..', 'App.razor'), 'utf8');
  const access = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.UI', 'Components', 'VaultHome.razor'), 'utf8');
  assert.ok(!lifecycle.includes("addEventListener('visibilitychange'"));
  assert.ok(!lifecycle.includes("addEventListener('pagehide'"));
  assert.ok(!lifecycle.includes("invokeMethodAsync('SuspendForPrivacy'"));
  assert.ok(app.includes('_coldAccount = null;'));
  assert.ok(access.includes('SAVED VAULT FOUND'));
  assert.ok(!access.includes('REOPEN LOCAL VAULT</button>'));
});
