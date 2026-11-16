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
  assert.ok(vault.includes('Records'));
  assert.ok(vault.includes('FOLDERS'));
  assert.ok(vault.includes('sv-pwa-app'));
  assert.ok(vault.includes('sv-pwa-rail'));
  assert.ok(vault.includes('sv-pwa-mobile-tabs'));
  assert.ok(vault.includes('SelectAppView("records")'));
  assert.ok(vault.includes('search-console'));
  assert.ok(vault.includes('Ledger.ReadAsync()'));
  assert.ok(!vault.includes('Offline local vault'));
  assert.ok(!vault.includes('<VaultRecordWorkspaceView />'));
  assert.ok(!vault.includes('<VaultBinaryFilesView />'));
  assert.ok(app.includes('Navigation.NavigateTo("/vault")'));
});

test('unlocked PWA session redirects from access to canonical vault without duplicate workspace', () => {
  const index = fs.readFileSync(path.join(root, '..', 'Pages', 'Index.razor'), 'utf8');
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  const access = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.UI', 'Components', 'VaultHome.razor'), 'utf8');
  assert.ok(index.includes('Navigation.NavigateTo("/vault", replace: true)'));
  assert.match(index, /VaultWorkspaceModeResolver\.Resolve\(/);
  assert.match(index, /VaultWorkspaceMode\.ConflictingSessions/);
  assert.doesNotMatch(index, /Navigation\.NavigateTo\("\/secret-vault"/);
  assert.match(vault, /<VaultSecretKeyWorkspace Embedded="true"/);
  assert.doesNotMatch(vault, /<VaultLinkedWorkspaceShell \/>/);
  assert.match(vault, /ActiveMode == VaultWorkspaceMode\.StellarSecretKey/);
  assert.match(vault, /if \(ActiveMode != VaultWorkspaceMode\.MnemonicHd\) return;/);
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
 assert.match(vault, /else if \(ActiveMode == VaultWorkspaceMode\.MnemonicHd &&\s*Ledger\.IsConnected && _registryReady == false\)/);
 assert.ok(vault.includes('search-console'));
 assert.ok(vault.includes('sv-folder-grid'));
 assert.ok(vault.includes('record-list'));
 assert.ok(vault.includes('STELLAR SIGNING SESSION NOT RESTORED'));
 assert.ok(vault.includes('ON-CHAIN RECORDS NOT LOADED'));
 assert.ok(vault.includes('Reconnect with your recovery phrase'));
});

test('refresh without signing credentials is not mislabeled as offline', () => {
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  assert.ok(vault.includes('Not connected'));
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
  assert.ok(access.includes('Saved device vault'));
  assert.ok(!access.includes('REOPEN LOCAL VAULT</button>'));
});

test('device retention requires explicit opt-in and defaults to on-chain-only', () => {
  const core = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.Core', 'Services', 'VaultStorageModeGate.cs'), 'utf8');
  assert.ok(core.includes('OnChainOnly = 0'));
  assert.ok(core.includes('EncryptedLocalWithManualSync = 1'));
  assert.ok(core.includes('Policy { get; private set; } = VaultDeviceRetentionPolicy.OnChainOnly'));
  assert.ok(core.includes('RequireLocalPersistence()'));
  assert.ok(core.includes('Local persistence requires explicit device-storage consent.'));
});

test('on-chain-only login bypasses local credentials and permits ledger pages', () => {
  const access = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.UI', 'Components', 'VaultHome.razor'), 'utf8');
  const program = fs.readFileSync(path.join(root, '..', 'Program.cs'), 'utf8');
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  const add = fs.readFileSync(path.join(root, '..', 'Pages', 'VaultItemEditor.razor'), 'utf8');
  assert.match(access, /<span>Stellar only<\/span>/);
  assert.ok(access.includes('checked="@(!CurrentDeviceStoragePreference)"'));
  assert.match(access, /if \(_enableLocalStorage\)\s*\{\s*Retention\.RequireLocalPersistence\(\)/);
  assert.ok(access.includes('if (!_enableLocalStorage || _workspaceMode == "ledger")'));
  assert.ok(program.includes('AddScoped<VaultDeviceRetentionGate>()'));
  // A connected HD ledger is a valid on-chain-only session even without
  // a local-vault unlock. The canonical route resolves credential strategy
  // explicitly instead of using the old HD-only boolean guard.
  const mode = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.Core',
    'Services', 'VaultWorkspaceModeResolver.cs'), 'utf8');
  assert.match(vault, /ActiveMode == VaultWorkspaceMode\.Locked/);
  assert.match(vault, /VaultWorkspaceModeResolver\.Resolve\(/);
  assert.match(mode, /if \(hdLocalUnlocked \|\| hdLedgerConnected\) return VaultWorkspaceMode\.MnemonicHd/);
  assert.match(add, /!Sessions\.IsUnlocked && !Ledger\.IsConnected/);
});

test('HD hybrid file upload uses encrypted durable queue separately from session-only direct plans', () => {
  const add = fs.readFileSync(path.join(root, '..', 'Pages', 'VaultItemEditor.razor'), 'utf8');
  const css = fs.readFileSync(path.join(root, 'css', 'modern-app.css'), 'utf8');
  assert.match(add, /!Sessions\.IsUnlocked \|\| !Retention\.AllowsLocalPersistence/);
  assert.match(add, /HybridDrafts\.StageTreeFileAsync\(/);
  assert.match(add, /selected\.OpenReadStream\(selected\.Size\)/);
  assert.doesNotMatch(add, /Checkpoints\.LoadAsync|CurrentUploadPlan|_fileBytes/);
  assert.match(add, /if \(CanSaveOffline\)/);
  assert.match(add, /new StellarLedgerUploadPlan\(/);
  assert.match(add, /DirectFileUpload\.PublishNextApprovedAsync\(/);
  assert.ok(css.includes('input[type="file"].sv-ledger-file-native-input'));
  assert.ok(add.includes('sv-file-native-input sv-ledger-file-native-input'));
});

test('large HD on-chain staging requires explicit local encrypted retention', () => {
  const add = fs.readFileSync(path.join(root, '..', 'Pages', 'VaultItemEditor.razor'), 'utf8');
  assert.match(add, /!Sessions\.IsUnlocked \|\| !Retention\.AllowsLocalPersistence/);
  assert.match(add, /HybridDrafts\.StageTreeFileAsync\(/);
  assert.match(add, /No transaction was submitted/);
  assert.match(add, /@onclick="StageHdTreeFileAsync"/);
});

test('opt-in encrypted seed remains separate from on-chain-only login', () => {
  const access = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.UI', 'Components', 'VaultHome.razor'), 'utf8');
  const app = fs.readFileSync(path.join(root, '..', 'App.razor'), 'utf8');
  const recent = fs.readFileSync(path.join(root, 'js', 'vault-v2-recent-vault.js'), 'utf8');
  const begin = access.search(/if \(_enableLocalStorage\)\s*\{\s*Retention\.RequireLocalPersistence\(\)/);
  const end = access.indexOf('await ReportAccessStageAsync("connecting to Stellar ledger")', begin);
  assert.ok(begin > 0 && end > begin);
  const optIn = access.slice(begin, end);
  assert.ok(optIn.includes('Workflow.CreateAsync'));
  assert.ok(optIn.includes('stellarVaultRecentVault.saveEncryptedSeed'));
  assert.ok(!access.slice(end, end + 500).includes('saveEncryptedSeed'));
  assert.ok(recent.includes("additionalData: aad"));
  assert.ok(recent.includes("iterations: 600000"));
  assert.ok(recent.includes("crypto.subtle.decrypt"));
  assert.ok(app.includes('stellarVaultRecentVault.unlockEncryptedSeed'));
  assert.match(app, /catch\s*\{\s*Workflow\.Lock\(\);\s*Retention\.Reset\(\);\s*StorageMode\.Reset\(\);\s*throw;/);
});

test('shared offline queue requires storage consent and reconciles uncertain submissions', () => {
  const queue = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.Core', 'Services', 'VaultPendingMutationQueue.cs'), 'utf8');
  for (const status of ['Pending', 'Submitting', 'Uncertain', 'Confirmed', 'Conflict', 'Failed'])
    assert.ok(queue.includes(status));
  assert.ok(queue.includes('retention.RequireLocalPersistence()'));
  assert.ok(queue.includes('DependsOn.All'));
  assert.ok(queue.includes('TryReplaceAsync(item, next, ct)'));
  assert.ok(queue.includes('State = VaultSyncState.Uncertain'));
  assert.ok(queue.includes('State = VaultSyncState.Confirmed'));
  assert.ok(!queue.includes('PublishAsync('));
});

test('on-chain-only access distinguishes unfunded root from connection failure', () => {
  const access = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.UI', 'Components', 'VaultHome.razor'), 'utf8');
  const preflight = access.indexOf('await ReportAccessStageAsync("checking Stellar root account")');
  const local = access.indexOf('await ReportAccessStageAsync("checking local credentials")');
  const ledger = access.indexOf('await ReportAccessStageAsync("connecting to Stellar ledger")');
  assert.ok(preflight > 0 && preflight < local && local < ledger);
  assert.ok(access.includes('Fund or create this account on that network'));
  const network = fs.readFileSync(path.join(root, 'js', 'vault-v2-network.js'), 'utf8');
  assert.ok(network.includes('if (response.status === 404) return false'));
});

test('offline mutation confirmation requires independent verification and scoped status', () => {
  const queue = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.Core', 'Services', 'VaultPendingMutationQueue.cs'), 'utf8');
  assert.ok(queue.includes('interface IVaultLedgerMutationVerifier'));
  assert.ok(queue.includes('await verifier.IsConfirmedAsync(current, transactionHash, ct)'));
  assert.ok(queue.includes('Queue entry belongs to another account or network.'));
  assert.ok(queue.includes('Task<VaultQueueStatus> StatusAsync'));
  assert.ok(queue.includes('MarkConflictAsync'));
});

test('encrypted offline queue stores authenticated envelopes, not readable drafts', () => {
  const db = fs.readFileSync(path.join(root, 'js', 'vault-v2-indexeddb.js'), 'utf8');
  const adapter = fs.readFileSync(path.join(root, '..', 'Services', 'IndexedDbVaultPendingMutationStore.cs'), 'utf8');
  const program = fs.readFileSync(path.join(root, '..', 'Program.cs'), 'utf8');
  assert.ok(db.includes("const version = 2;"));
  assert.ok(db.includes("'syncQueue'"));
  assert.ok(db.includes('listQueue'));
  assert.ok(adapter.includes('VaultCryptoPrimitives.Seal'));
  assert.ok(adapter.includes('VaultCryptoPrimitives.Open'));
  assert.ok(adapter.includes('AssociatedData(account, network, id)'));
  assert.ok(adapter.includes('retention.RequireLocalPersistence()'));
  // The queue no longer reads the active account from a mutable UI session.
  // Its key-session operation must bind the requested account and network
  // and reject cross-scope attempts before any plaintext can be decoded.
  assert.match(adapter, /StellarAccountAddress\.Validate\(accountId\)/);
  assert.match(adapter, /keySession\.ExecuteAsync\(accountId, networkId/);
  assert.match(adapter, /item\.Id != id \|\| item\.AccountId != account \|\| item\.NetworkId != network/);
  assert.ok(adapter.includes('stellarVaultV2Db.replaceQueue'));
  assert.ok(program.includes('IVaultPendingMutationStore, IndexedDbVaultPendingMutationStore'));
  assert.ok(program.includes('IVaultLedgerMutationVerifier, EvidenceBasedVaultLedgerMutationVerifier'));
});

test('PWA exposes independent record, offline draft and manual sync views', () => {
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  assert.ok(vault.includes('SelectAppView("drafts")'));
  assert.ok(vault.includes('SelectAppView("sync")'));
  assert.ok(vault.includes('_appView == "drafts" && Retention.AllowsLocalPersistence && Sessions.IsUnlocked'));
  assert.ok(vault.includes('_appView == "sync" && Retention.AllowsLocalPersistence && Sessions.IsUnlocked'));
  assert.ok(vault.includes('<VaultHybridDraftEditor />'));
  assert.ok(vault.includes('<VaultHybridRemoteChanges />'));
  assert.ok(vault.includes('<VaultHybridSyncReview />'));
  assert.ok(vault.includes('_appView == "records"'));
  assert.ok(vault.includes('Ledger.ReadAsync()'));
  assert.ok(vault.includes('Ledger.DisconnectAsync()'));
});

test('PWA item editor offers one focused task with app navigation', () => {
  const add = fs.readFileSync(path.join(root, '..', 'Pages', 'VaultItemEditor.razor'), 'utf8');
  const css = fs.readFileSync(path.join(root, 'css', 'modern-app.css'), 'utf8');
  assert.ok(add.includes('sv-pwa-editor-breadcrumb'));
  assert.ok(add.includes('sv-pwa-editor-modes'));
  assert.ok(add.includes('href="/add?kind=folder"'));
  assert.ok(add.includes('href="/add?kind=file"'));
  assert.ok(css.includes('.sv-pwa-mobile-tabs'));
  assert.ok(css.includes('.sv-pwa-rail'));
  assert.ok(css.includes('safe-area-inset-bottom'));
});

test('PWA access starts with recovery phrase and network icon in the header', () => {
  const access = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.UI',
    'Components', 'VaultHome.razor'), 'utf8');
  assert.match(access, /sv-network-icon-trigger/);
  assert.match(access, /SelectNetwork\("custom"\)/);
  assert.match(access, /DetectCustomAsync/);
  assert.match(access, /stellarVaultV2Network\.probeCustom/);
  assert.match(access, /NetworkSelection\.ConfigureCustom\(detected\)/);
  assert.match(access, /_detectedHorizon = requestedHorizon/);
  assert.match(access, /<VaultMnemonicInput/);
  assert.match(access, /sv-access-storage-settings/);
  assert.doesNotMatch(access, /<h2>Unlock your vault<\/h2>/);
});

test('PWA desktop explorer and mobile object storage share one folder model', () => {
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  const css = fs.readFileSync(path.join(root, 'css', 'modern-app.css'), 'utf8');
  assert.match(vault, /sv-explorer-layout/);
  const sharedTree = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.UI',
    'Components', 'VaultExplorerTree.razor'), 'utf8');
  assert.match(vault, /<VaultExplorerTree Folders="@Folders"/);
  assert.match(sharedTree, /sv-explorer-tree/);
  assert.match(vault, /sv-explorer-main/);
  assert.match(sharedTree, /Depth\(folder.Id\)/);
  assert.match(vault, /OnFolderSelected="SelectFolderFromTree"/);
  assert.match(css, /\.sv-explorer-tree/);
  assert.match(css, /\.sv-explorer-main \.record-list/);
  assert.match(css, /@media\(max-width:1040px\)/);
  assert.match(css, /@media\(max-width:540px\)/);
});

test('PWA modern design overrides CRT geometry and supports resizable record inspector', () => {
  const css = fs.readFileSync(path.join(root, 'css', 'modern-app.css'), 'utf8');
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  const host = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
  assert.match(css, /--sv-crt-grid:none/);
  assert.match(css, /--sv-record-inspector-width:400px/);
  assert.match(vault, /sv-inspector-resize-handle/);
  assert.match(host, /pointerdown/);
});

test('PWA loads only the consolidated modern design after scoped styles', () => {
  const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
  const css = fs.readFileSync(path.join(root, 'css', 'modern-app.css'), 'utf8');
  const scoped = html.indexOf('href="StellarVault.Pwa.styles.css"');
  const modern = html.indexOf('href="css/modern-app.css');
  assert.ok(scoped >= 0 && modern > scoped);
  assert.ok(!html.includes('href="css/app.css"'));
  assert.match(css, /--sv-radius:16px/);
  assert.match(css, /\.screen-noise,\.scanlines \{display:none!important;/);
  assert.match(css, /sv-explorer-layout/);
  assert.match(css, /sv-pwa-mobile-tabs/);
  assert.match(css, /modal-backdrop:has\(#sv-record-title\)/);
  assert.match(css, /prefers-reduced-motion/);
});

test('PWA modern destinations expose usable settings and mobile new action', () => {
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  const css = fs.readFileSync(path.join(root, 'css', 'modern-app.css'), 'utf8');
  assert.match(vault, /SelectAppView\("settings"\)/);
  assert.match(vault, /_appView == "settings"/);
  assert.match(vault, /OpenAccountInfoAsync/);
  assert.match(vault, /LockAsync/);
  assert.match(vault, /class="sv-modern-fab"/);
  assert.match(css, /\.sv-modern-settings/);
  assert.match(css, /\.sv-modern-fab/);
  assert.match(vault, /sv-modern-record-icon/);
  assert.match(vault, /sv-modern-search/);
});


test('Screenshot regressions: inspector uses one width reservation and entry has four-column desktop mnemonic', () => {
  const css = fs.readFileSync(path.join(root, 'css', 'modern-app.css'), 'utf8');
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  const home = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.UI', 'Components', 'VaultHome.razor'), 'utf8');
  assert.match(css, /width:calc\(100% - var\(--sv-record-inspector-width\)\)/);
  assert.match(css, /grid-template-columns:repeat\(4,minmax\(0,1fr\)\)/);
  assert.match(css, /\.sv-web-shell \.sv-access-storage-choices/);
  assert.match(css, /\.sv-web-shell \.sv-account-info-modal h2/);
  assert.match(css, /\.sv-web-shell \.sv-pwa-app > nav\.sv-pwa-rail/);
  assert.match(vault, /sv-inspector-resize-handle/);
  assert.match(home, /sv-modern-entry/);
  assert.doesNotMatch(css, /--bg-0:\s*#020604/);
  assert.doesNotMatch(css, /font-family:\s*"Courier New"/);
});


test('Explorer rows expose individual action menus and the inspector never centers the workspace', () => {
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  const css = fs.readFileSync(path.join(root, 'css', 'modern-app.css'), 'utf8');
  assert.match(vault, /class="sv-object-row/);
  assert.match(vault, /class="sv-object-actions"/);
  assert.match(vault, /Actions for folder/);
  const sharedRow = fs.readFileSync(path.join(root, '..', '..',
    'StellarVault.UI', 'Components', 'VaultExplorerItemRow.razor'), 'utf8');
  assert.match(sharedRow, /class="sv-object-row/);
  assert.match(sharedRow, /class="sv-object-actions"/);
  assert.match(sharedRow, /Actions for \{Item.Title\}/);
  assert.doesNotMatch(vault, /<small>📁 @FolderPath\(item.FolderId\)<\/small>/);
  assert.match(css, /\.sv-web-shell:has\(\.sv-pwa-app\) > \.sv-adaptive-main/);
  assert.match(css, /width:calc\(100% - var\(--sv-record-inspector-width\)\)/);
  assert.match(css, /\.sv-explorer-tree \.sv-tree-item\.selected/);
  assert.match(css, /\.sv-object-menu/);
});


test('Vault desktop explorer uses a genuine third-column inspector and nested folder tree', () => {
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  const editor = fs.readFileSync(path.join(root, '..', 'Pages', 'VaultItemEditor.razor'), 'utf8');
  const css = fs.readFileSync(path.join(root, 'css', 'modern-app.css'), 'utf8');
  const sharedTree = fs.readFileSync(path.join(root, '..', '..',
    'StellarVault.UI', 'Components', 'VaultExplorerTree.razor'), 'utf8');
  assert.match(sharedTree, /sv-tree-node/);
  assert.match(vault, /OnToggleFolder="ToggleTreeFolder"/);
  assert.match(vault, /sv-current-folder/);
  assert.match(vault, /sv-explorer-list-head/);
  assert.match(vault, /sv-layout-switch/);
  assert.match(vault, /_explorerLayout == "grid"/);
  assert.match(vault, /<VaultItemEditor @ref="_inlineEditor" Embedded="true"/);
  const inspector = fs.readFileSync(path.join(root, '..', '..',
    'StellarVault.UI', 'Components', 'VaultRecordInspector.razor'), 'utf8');
  assert.match(vault, /<VaultRecordInspector @key="_selected.Id"/);
  assert.match(inspector, /sv-inspector-tabs/);
  assert.match(inspector, /sv-inspector-more/);
  // The obsolete flat-file upload checkpoint was removed; the shared HD
  // streaming queue owns interrupted-file recovery instead.
  assert.match(editor, /BlocksDismissal => _busy \|\| _estimate is not null \|\| _directFilePlan is not null/);
  assert.doesNotMatch(editor, /_uncertainStage/);
  assert.match(editor, /_directFileUncertain/);
  assert.match(editor, /OnSaved.InvokeAsync/);
  assert.match(css, /grid-template-columns:245px minmax\(0,1fr\) var\(--sv-record-inspector-width\)/);
  assert.match(css, /\.sv-editor-embedded/);
});


test('Embedded Add/Edit renders without a nested app grid; create actions stay out of left rail', () => {
  const editor = fs.readFileSync(path.join(root, '..', 'Pages', 'VaultItemEditor.razor'), 'utf8');
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  const css = fs.readFileSync(path.join(root, 'css', 'modern-app.css'), 'utf8');
  const shell = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.UI', 'Components', 'AdaptiveShell.razor'), 'utf8');
  assert.match(editor, /Embedded \? "sv-inline-editor-layout" : "sv-pwa-app sv-pwa-secondary-app"/);
  assert.match(editor, /Embedded \? "sv-inline-editor-stage" : "sv-pwa-stage sv-pwa-secondary-stage"/);
  assert.match(editor, /@if \(!Embedded\)/);
  assert.match(shell, /@if \(Embedded\)/);
  const rail = vault.slice(vault.indexOf('<nav class="sv-pwa-rail"'), vault.indexOf('</nav>') + 6);
  assert.doesNotMatch(rail, /OpenEditor\(/);
  assert.match(vault, /<VaultItemEditor @ref="_inlineEditor"/);
  assert.match(vault, /<ErrorBoundary @key="_editorKey">/);
  assert.match(css, /\.sv-editor-sidecar \.sv-inline-editor-layout/);
  assert.match(css, /\.sv-folder-tile \.sv-folder-open > span:last-child/);
  assert.match(css, /text-align:left!important/);
});

test('Add/Edit route only forwards navigation parameters to a reusable editor', () => {
  const wrapper = fs.readFileSync(path.join(root, '..', 'Pages', 'Add.razor'), 'utf8');
  const reusable = fs.readFileSync(path.join(root, '..', 'Pages', 'VaultItemEditor.razor'), 'utf8');
  assert.match(wrapper, /@page "\/add"/);
  assert.match(wrapper, /@page "\/edit\/\{RecordId\}"/);
  assert.match(wrapper, /<VaultItemEditor RecordId="@RecordId" InitialFolder="@InitialFolder" Kind="@Kind"\s*\/>/);
  assert.doesNotMatch(reusable, /@page/);
  assert.match(reusable, /OnSaved.InvokeAsync/);
});

test('Network preference contains only non-secret metadata and custom network must be redetected', () => {
  const js = fs.readFileSync(path.join(root, 'js', 'vault-v2-network.js'), 'utf8');
  const home = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.UI', 'Components', 'VaultHome.razor'), 'utf8');
  assert.match(js, /stellarVaultNetworkPreference/);
  assert.match(js, /stellar-vault-v2-network-preference/);
  assert.match(js, /horizonUrl/);
  assert.ok(js.includes("localStorage.setItem(key, JSON.stringify({ version: 1, network, horizonUrl: url }))"));
  assert.match(home, /stellarVaultNetworkPreference\.load/);
  assert.match(home, /stellarVaultNetworkPreference\.save/);
  assert.match(home, /NetworkSelection\.ResetCustom\(\)/);
  assert.match(home, /Verifying remembered custom Horizon/);
  assert.match(home, /await DetectCustomAsync\(\)/);
  assert.match(home, /Custom network verified and selected/);
  assert.match(home, /select Detect network to retry/);
  assert.doesNotMatch(home, /Security and release status/);
});

test('Unlock button stays actionable and explains prerequisites without bypassing validation', () => {
  const home = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.UI', 'Components', 'VaultHome.razor'), 'utf8');
  const css = fs.readFileSync(path.join(root, 'css', 'modern-app.css'), 'utf8');
  // The unlock action lives outside the form for stable access-page layout,
  // but still submits that form and remains disabled while access is busy.
  assert.match(home, /type="submit" form="sv-access-form" disabled="@_busy"/);
  assert.doesNotMatch(home, /sv-access-readiness|private string AccessReadiness/);
  assert.doesNotMatch(home, /words entered\)/);
  assert.match(home, /if \(EnteredWordCount is not \(12 or 15 or 18 or 21 or 24\)\)/);
  assert.match(home, /if \(_enableLocalStorage && string\.IsNullOrWhiteSpace\(_passphrase\)\)/);
  assert.doesNotMatch(home, /sv-access-security-notice/);
  assert.match(home, /sv-entry-storage-guide/);
  assert.match(css, /\.sv-entry-storage-guide/);
});

test('Signed approval and account modal block blurred background without obscuring dialog controls', () => {
  const css = fs.readFileSync(path.join(root, 'css', 'modern-app.css'), 'utf8');
  const lifecycle = fs.readFileSync(path.join(root, 'js', 'vault-v2-lifecycle.js'), 'utf8');
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  const approval = fs.readFileSync(path.join(root, '..', 'Shared', 'LedgerCostApprovalModal.razor'), 'utf8');
  assert.match(css, /\.sv-editor-sidecar:has\(\.approval-modal-backdrop\)/);
  assert.match(css, /\.modal-backdrop:not\(\.sv-record-inspector-backdrop\)/);
  assert.match(css, /backdrop-filter:blur\(7px\)!important/);
  assert.match(css, /z-index:8000!important/);
  assert.match(css, /\.close-button:focus-visible/);
  assert.match(lifecycle, /MutationObserver\(sync\)/);
  assert.match(lifecycle, /element\.inert = true/);
  assert.match(lifecycle, /event\.key !== 'Tab'/);
  assert.match(approval, /aria-modal="true"/);
  assert.match(vault, /sv-account-info-modal/);
});
test('Product Help stays inside the Vault layout and attribution is a thin footer', () => {
  const rootCss = fs.readFileSync(path.join(root, 'css', 'modern-app.css'), 'utf8');
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  const help = fs.readFileSync(path.join(root, '..', 'Pages', 'Help.razor'), 'utf8');
  const helpContent = fs.readFileSync(path.join(root, '..', 'Shared', 'VaultHelpContent.razor'), 'utf8');
  const lifecycle = fs.readFileSync(path.join(root, 'js', 'vault-v2-lifecycle.js'), 'utf8');
  const shell = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.UI', 'Components', 'AdaptiveShell.razor'), 'utf8');
  assert.match(help, /@page "\/help"/);
  assert.match(help, /NavigateTo\("\/vault\?view=help"/);
  assert.match(helpContent, /id="getting-started"/);
  assert.match(helpContent, /id="security"/);
  assert.doesNotMatch(helpContent, /sv-help-sidebar|sv-help-toc/);
  assert.match(vault, /<VaultHelpContent\s*\/>/);
  assert.match(vault, /SelectAppView\("help"\)/);
  assert.match(vault, /sv-nav-vault-icon/);
  assert.match(vault, /sv-rail-attribution/);
  assert.match(vault, /https:\/\/github\.com\/ToorajValaee\/StellarVault/);
  assert.match(vault, /https:\/\/badje\.ir\//);
  assert.match(rootCss, /\.sv-pwa-stage \.sv-help-main\.sv-help-embedded/);
  assert.match(rootCss, /\.sv-rail-attribution/);
  assert.match(rootCss, /:root\[data-theme="dark"\]/);
  assert.match(lifecycle, /dataset\.theme === 'dark' \? 'light' : 'dark'/);
  assert.match(shell, /sv-network-symbol/);
  assert.match(shell, /sv-brand-shield/);
});

test('Vault query navigation uses one combined OnParametersSet override', () => {
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  assert.equal((vault.match(/protected override void OnParametersSet\s*\(/g) || []).length, 1);
  assert.match(vault, /if \(!string\.IsNullOrEmpty\(InitialFolder\)\) _folder = InitialFolder/);
  assert.match(vault, /if \(RequestedView == "help"\) _appView = "help"/);
});

test('Vault inspector, Settings, Drafts and Sync use the unified light-dark workspace theme', () => {
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  const editor = fs.readFileSync(path.join(root, '..', 'Pages', 'VaultItemEditor.razor'), 'utf8');
  const css = fs.readFileSync(path.join(root, 'css', 'modern-app.css'), 'utf8');
  const shared = path.join(root, '..', '..', 'StellarVault.UI', 'Components');
  const drafts = fs.readFileSync(path.join(shared, 'VaultHybridDraftEditor.razor'), 'utf8');
  const sync = fs.readFileSync(path.join(shared, 'VaultHybridSyncReview.razor'), 'utf8');
  const remote = fs.readFileSync(path.join(shared, 'VaultHybridRemoteChanges.razor'), 'utf8');
  const sidebar = vault.slice(vault.indexOf('<nav class="sv-pwa-rail"'), vault.indexOf('</nav>') + 6);
  assert.doesNotMatch(sidebar, /sv-sidebar-folder-link|> Folders<\/button>/);
  assert.doesNotMatch(editor, /ENCRYPT → CHUNK → SIGN → SUBMIT/);
  assert.doesNotMatch(editor, /<span>MODE<\/span>|<span>TARGET<\/span>/);
  assert.match(vault, /sv-modern-settings sv-workspace-surface/);
  assert.match(vault, /sv-drafts-workspace sv-workspace-surface/);
  assert.match(vault, /sv-sync-workspace sv-workspace-surface/);
  assert.match(drafts, /sv-hybrid-drafts-panel/);
  assert.match(sync, /sv-hybrid-sync-panel/);
  assert.match(remote, /sv-hybrid-remote-panel/);
  assert.match(css, /--sv-work-panel:#1c2e32/);
  assert.match(css, /\.sv-modern-settings \.sv-modern-settings-section/);
  assert.match(css, /\.sv-workspace-surface \.sv-hybrid-panel/);
  assert.match(css, /\.sv-record-inspector:has\(#sv-record-title\)/);
  assert.match(css, /\.sv-record-inspector \.sv-inspector-tabs button\.selected/);
});

test('Local draft create/save never requires Stellar discovery or ledger refresh', () => {
  const editor = fs.readFileSync(path.join(root, '..', 'Pages', 'VaultItemEditor.razor'), 'utf8');
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  const init = editor.slice(editor.indexOf('protected override async Task OnParametersSetAsync()'),
    editor.indexOf('private LedgerRecordDraft Draft()'));
  const save = editor.slice(editor.indexOf('private async Task SaveOfflineDraftAsync()'),
    editor.indexOf('private static bool SameDraft('));
  const localCallback = vault.slice(vault.indexOf('private async Task OnLocalDraftSavedAsync()'),
    vault.indexOf('private string _syncTask', vault.indexOf('private async Task OnLocalDraftSavedAsync()')));
  assert.match(editor, /\[Parameter\] public EventCallback OnDraftSaved/);
  assert.match(editor, /\[Parameter\] public IReadOnlyList<LedgerRecordItem>\? CachedFolders/);
  assert.match(init, /if \(string\.IsNullOrWhiteSpace\(RecordId\)\)/);
  assert.ok(init.indexOf('if (string.IsNullOrWhiteSpace(RecordId))') < init.indexOf('await Ledger.ReadAsync()'));
  assert.match(init, /_folders = CachedFolders \?\? \[\]/);
  assert.match(save, /await PendingQueue\.EnqueueAsync\(mutation\)/);
  assert.match(save, /CompleteEditorAsync\(savedLocally: true\)/);
  assert.doesNotMatch(save, /Ledger\.ReadAsync\(|Ledger\.EstimateAsync\(|RefreshAsync\(/);
  assert.match(vault, /OnDraftSaved="OnLocalDraftSavedAsync"/);
  assert.match(vault, /CachedFolders="@Folders"/);
  assert.match(localCallback, /_appView = "drafts"/);
  assert.match(localCallback, /await LoadPendingAsync\(\)/);
  assert.doesNotMatch(localCallback, /RefreshAsync\(|Ledger\.ReadAsync\(/);
});
test('Offline ledger refresh leaves last verified records and gives short status', () => {
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  const refresh = vault.slice(vault.indexOf('private async Task RefreshAsync()'),
    vault.indexOf('private async Task ReconnectAsync()', vault.indexOf('private async Task RefreshAsync()')));
  assert.match(refresh, /_records = await Ledger\.ReadAsync\(\)/);
  assert.match(refresh, /Stellar network is unavailable/);
  assert.doesNotMatch(refresh, /"Ledger synchronization failed: " \+ ex\.Message/);
});

test('Unreachable registry never redirects an unlocked local vault to initialization', () => {
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  assert.match(vault, /else if \(ActiveMode == VaultWorkspaceMode\.MnemonicHd &&\s*Ledger\.IsConnected && _registryReady == false\)/);
  assert.match(vault, /Stellar cannot be reached right now\. You can continue working with encrypted local drafts/);
  assert.doesNotMatch(vault, /Ledger setup check failed: " \+ ex\.Message/);
});

test('Offline record loader and fee estimation never display vendor network stack traces', () => {
  const editor = fs.readFileSync(path.join(root, '..', 'Pages', 'VaultItemEditor.razor'), 'utf8');
  assert.match(editor, /IsHorizonUnavailable\(ex\)/);
  assert.match(editor, /Stellar is offline\. Published records cannot be loaded/);
  assert.match(editor, /Stellar is unreachable\. No transaction was sent/);
  assert.doesNotMatch(editor, /"Cannot load record: " \+ ex\.Message/);
  assert.doesNotMatch(editor, /"Cannot estimate ledger stage: " \+ ex\.Message/);
});

test('Phone PWA reproduces the approved object-storage workspace while desktop stays separate', () => {
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  const css = fs.readFileSync(path.join(root, 'css', 'modern-app.css'), 'utf8');
  assert.match(vault, /sv-mobile-collection-tabs/);
  assert.match(vault, /sv-mobile-folder-shortcuts/);
  assert.match(vault, /sv-mobile-shortcut-grid/);
  assert.match(vault, /sv-mobile-items-heading/);
  assert.match(vault, /sv-mobile-collection-@_mobileCollection/);
  assert.match(vault, /sv-mobile-folder-back/);
  assert.match(vault, /_mobileCollection == "folders"/);
  assert.match(vault, /OpenEditor\("record"\)/);
  const inspector = fs.readFileSync(path.join(root, '..', '..',
    'StellarVault.UI', 'Components', 'VaultRecordInspector.razor'), 'utf8');
  assert.match(vault, /<VaultRecordInspector @key="_selected.Id"/);
  assert.match(inspector, /sv-record-inspector-backdrop/);
  assert.match(css, /MOBILE VAULT/);
  assert.match(css, /@media\(max-width:840px\)/);
  assert.match(css, /sv-mobile-collection-folders/);
  assert.match(css, /sv-mobile-collection-records/);
  assert.match(css, /max-height:60dvh/);
  assert.match(css, /sv-pwa-mobile-tabs/);
  assert.match(css, /safe-area-inset-bottom/);
});

test('Inspector preserves selected grid mode and keeps explorer and details scrollable', () => {
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  const css = fs.readFileSync(path.join(root, 'css', 'modern-app.css'), 'utf8');
  assert.match(vault, /data-explorer-layout="@_explorerLayout"/);
  assert.match(vault, /sv-explorer-main @\(_explorerLayout == "grid"/);
  assert.match(vault, /sv-settings-help-link/);
  assert.doesNotMatch(vault, /sv-mobile-help-entry/);
  assert.match(css, /sv-view-grid \.record-list/);
  assert.match(css, /sv-view-grid \.sv-folder-grid/);
  assert.match(css, /overflow-y:auto!important;overflow-x:hidden!important/);
  assert.match(css, /overscroll-behavior-y:contain!important/);
  assert.match(css, /sv-settings-help-link/);
  assert.match(css, /html\[data-theme="dark"\] \.sv-web-shell \.sv-sync-workspace/);
});

test('desktop add/edit uses the same drag width as record preview and explorer tree fills its pane', () => {
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  const css = fs.readFileSync(path.join(root, 'css', 'modern-app.css'), 'utf8');
  const index = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
  assert.match(vault,/sv-inspector-resize-handle sv-editor-resize-handle/);
  assert.match(index,/--sv-record-inspector-width/);
  assert.match(index,/pointerdown/);
  assert.match(index,/ArrowLeft/);
  assert.match(css,/\.sv-editor-sidecar > \.sv-editor-resize-handle/);
  assert.match(css,/\.sv-explorer-layout \{\s*align-items:stretch!important/);
  assert.match(css,/\.sv-explorer-tree \{\s*align-self:stretch!important/);
  assert.match(css,/min-height:100%!important/);
});

test('device storage stays expanded, the radio layout is stable and both credential modes can enroll device unlock', () => {
  const home = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.UI',
    'Components', 'VaultHome.razor'), 'utf8');
  const queue = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.UI',
    'Components', 'VaultLinkedLocalQueuePanel.razor'), 'utf8');
  const css = fs.readFileSync(path.join(root, 'css', 'modern-app.css'), 'utf8');
  const program = fs.readFileSync(path.join(root, '..', 'Program.cs'), 'utf8');
  assert.match(home,/<section class="sv-access-storage-settings sv-entry-storage-outside"/);
  assert.doesNotMatch(home,/<details class="sv-access-storage-settings/);
  assert.match(home,/@if \(CurrentDeviceStoragePreference\)/);
  assert.match(home,/@bind="_requestBiometricEnrollment"/);
  assert.match(home,/DeviceUnlockPreference\.Requested/);
  assert.match(css,/\.sv-access-storage-choices \.sv-biometric-choice > span/);
  assert.match(css,/min-height:48px!important/);
  assert.match(css,/\.sv-entry-showcase-content \{\s*margin:clamp\(34px,7vh,92px\) 0 0!important/);
  assert.match(program,/AddScoped<VaultDeviceUnlockPreference>/);
  assert.match(queue,/stellarVaultBiometric\.enroll/);
  assert.match(queue,/stellarVaultBiometric\.unlock/);
  assert.match(queue,/linked-s-key:/);
});

test('local and HD on-chain file editors stage large files with streaming instead of fixed UI caps', () => {
  const local = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.UI',
    'Components', 'VaultBinaryFilesView.razor'), 'utf8');
  const ledger = fs.readFileSync(path.join(root, '..', 'Pages',
    'VaultItemEditor.razor'), 'utf8');
  const download = fs.readFileSync(path.join(root, 'js','vault-v2-download.js'),'utf8');
  assert.match(local,/OpenReadStream\(_file\.Size\)/);
  assert.match(local,/Files\.CopyVerifiedToAsync\(_openName, Stream\.Null\)/);
  assert.match(local,/Files\.StreamVerifiedToAsync\(/);
  assert.match(local,/NativeFiles\.SaveAsync\(name, reader\)/);
  assert.match(download,/async saveStream\(filename, dotnetStreamReference\)/);
  assert.match(download,/await source\.pipeTo\(target\)/);
  // Documentation may explain the removed 1 MiB limit; only reject an
  // actual fixed-size validation or OpenReadStream cap.
  assert.doesNotMatch(local,/MaximumFileLength/);
  assert.doesNotMatch(local,/_file\.Size\s*>\s*1024\s*\*\s*1024/);
  assert.doesNotMatch(local,/OpenReadStream\(1024\s*\*\s*1024\)/);
  assert.doesNotMatch(download,/Download exceeds limit/);
  assert.match(ledger,/HybridDrafts\.StageTreeFileAsync\(/);
  assert.match(ledger,/selected\.OpenReadStream\(selected\.Size\)/);
  assert.match(ledger,/STAGE ENCRYPTED FILE FOR SYNC/);
  assert.doesNotMatch(ledger,/if \(file\.Size > MaximumLedgerFileLength\)/);
});

test('HD and Secret Key native sessions offer the same record commands without changing signing flows', () => {
  const native = fs.readFileSync(path.join(root, '..', '..',
    'StellarVault.UI', 'Components', 'NativeVaultWorkspace.razor'), 'utf8');
  const linked = fs.readFileSync(path.join(root, '..', '..',
    'StellarVault.UI', 'Components', 'VaultSecretKeyWorkspace.razor'), 'utf8');
  const nativeCommands = native.slice(native.indexOf('<div class="sv-native-commandbar'), native.indexOf('@code {'));
  const secretStart = nativeCommands.indexOf('@if (SecretLedger.IsConnected)');
  const hdStart = nativeCommands.indexOf('else if (Ledger.IsConnected)', secretStart);
  const disconnectedStart = nativeCommands.indexOf('else', hdStart + 'else if (Ledger.IsConnected)'.length);
  assert.ok(secretStart >= 0 && hdStart > secretStart && disconnectedStart > hdStart);
  const secretCommands = nativeCommands.slice(secretStart, hdStart);
  const hdCommands = nativeCommands.slice(hdStart, disconnectedStart);
  for (const action of ['NewNativeRecordForActiveStrategy', 'NewNativeFolderForActiveStrategy',
    'RefreshNativeActiveStrategyAsync', 'DisconnectSelectedLedgerAsync']) {
    assert.ok(secretCommands.includes('@onclick="' + action + '"'), 'Secret Key: ' + action);
    assert.ok(hdCommands.includes('@onclick="' + action + '"'), 'HD: ' + action);
  }
  assert.match(native, /if \(SecretLedger\.IsConnected\) _linkedWorkspace\?\.CreateRecordFromShell\(\)/);
  assert.match(native, /else if \(Ledger\.IsConnected\) SetNativeMode\("record"\)/);
  assert.match(linked, /<VaultExplorerTree Folders="@_items\.Where\(x => x\.IsFolder\)\.ToArray\(\)"/);
});

test('HD and independent Secret Key workspaces share one published record filter and row renderer', () => {
  const native = fs.readFileSync(path.join(root, '..', '..',
    'StellarVault.UI', 'Components', 'NativeVaultWorkspace.razor'), 'utf8');
  const linked = fs.readFileSync(path.join(root, '..', '..',
    'StellarVault.UI', 'Components', 'VaultSecretKeyWorkspace.razor'), 'utf8');
  const published = fs.readFileSync(path.join(root, '..', '..',
    'StellarVault.UI', 'Components', 'VaultPublishedItemsView.razor'), 'utf8');
  assert.match(native, /<VaultPublishedItemsView Strategy="@VaultMutationStrategyPolicy\.Hd" Items="_ledgerRecords"/);
  assert.match(linked, /<VaultPublishedItemsView Strategy="@VaultMutationStrategyPolicy\.Linked" Items="_items"/);
  assert.match(native, /Collection="_nativeCollection"/);
  assert.match(linked, /OnEdit="EditLinkedRow"/);
  assert.match(linked, /OnDelete="DeleteLinkedRowAsync"/);
  assert.match(published, /VaultPublishedFileClassification\.IsInternalNode\(x\)/);
  assert.match(published, /string\.Equals\(x\.FolderId \?\? "", FolderId \?\? "", StringComparison\.Ordinal\)/);
  assert.match(published, /OrderByDescending\(x => x\.IsFolder\)/);
  assert.match(published, /<VaultExplorerItemRow Item="item"/);
});

test('ending a hybrid session cannot force passphrase unlock on a later on-chain-only login', () => {
  const app = fs.readFileSync(path.join(root, '..', 'App.razor'), 'utf8');
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  const access = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.UI', 'Components', 'VaultHome.razor'), 'utf8');
  const recent = fs.readFileSync(path.join(root, 'js', 'vault-v2-recent-vault.js'), 'utf8');
  assert.match(recent, /sessionStorage\.setItem\(activeHybridTabKey/);
  assert.match(recent, /sessionStorage\.removeItem\(activeHybridTabKey/);
  assert.match(recent, /function hasHybridSession\(account, network\)/);
  assert.match(app, /stellarVaultRecentVault\.hasHybridSession/);
  assert.match(app, /_coldAccount = recent\.Account/);
  assert.match(app, /stellarVaultRecentVault\.clearHybridSession/);
  assert.match(app, /Retention\.Reset\(\)/);
  assert.match(app, /StorageMode\.Reset\(\)/);
  assert.match(app, /SecretLedger\.DisconnectAsync\(\)/);
  assert.match(access, /stellarVaultRecentVault\.markHybridSession/);
  assert.match(access, /if \(!_enableLocalStorage\)\s*await JS\.InvokeVoidAsync\("stellarVaultRecentVault\.clearHybridSession"\)/);
  assert.match(access, /Retention\.Reset\(\)/);
  assert.match(vault, /private async Task ClearHybridResumeIntentAsync\(\)/);
  assert.match(vault, /await ClearHybridResumeIntentAsync\(\)/);
  assert.match(vault, /Privacy\.Clear\(\)/);
  assert.match(vault, /StorageMode\.Reset\(\)/);
  assert.match(vault, /@onclick="LockActiveVaultAsync">Lock vault/);
});

test('access passphrase and help controls are shared by HD and independent S-key', () => {
  const home = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.UI', 'Components', 'VaultHome.razor'), 'utf8');
  const notice = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.UI', 'Components', 'VaultStorageSecurityNotice.razor'), 'utf8');
  const css = fs.readFileSync(path.join(root, 'css', 'modern-app.css'), 'utf8');
  const key = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.Core', 'Services', 'VaultLinkedQueueKeySession.cs'), 'utf8');
  assert.match(home, /@if \(CurrentDeviceStoragePreference\)\s*\{\s*<div class="sv-storage-credential-group">/);
  assert.match(home, /class="sv-biometric-choice sv-storage-device-unlock"/);
  assert.match(home, /class="sv-storage-passphrase-field"/);
  assert.match(home, /id="sv-passphrase" form="sv-access-form"/);
  assert.equal((home.match(/id="sv-passphrase"/g) || []).length, 1);
  assert.ok(home.indexOf('id="sv-passphrase"') < home.indexOf('<form id="sv-access-form"'));
  assert.match(home, /LinkedQueueKeys\.HasLocalCredentialsAsync\(\)/);
  assert.match(home, /LinkedQueueKeys\.UnlockAsync\(_passphrase\)/);
  assert.match(home, /LinkedQueueKeys\.CreateAsync\(_passphrase\)/);
  assert.match(home, /_passphrase\.Length < 12/);
  assert.match(home, /private void SelectCredentialMode\(VaultCredentialMode mode\)[\s\S]*?_passphrase = string\.Empty;\s*ClearMnemonic\(\)/);
  assert.match(home, /class="sv-entry-storage-guide"/);
  assert.match(home, /About local storage and Sync/);
  assert.match(home, /aria-expanded="@_storageHelpOpen"/);
  assert.match(home, /aria-expanded="@_deviceHelpOpen"/);
  assert.match(home, /class="sv-storage-help-trigger"/);
  assert.doesNotMatch(home, /Security and release status|sv-access-readiness/);
  assert.match(notice, /Strategy == VaultMutationStrategyPolicy\.Linked/);
  assert.match(key, /public Task UnlockAsync\(string localPassphrase/);
  assert.match(css, /\.sv-storage-help-trigger/);
});

test('PWA disposes decrypted pending entries and Sync reviews on lock and navigation', () => {
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  assert.match(vault, /@implements IDisposable/);
  assert.match(vault, /private void ClearPendingSnapshot\(\)/);
  assert.match(vault, /private void ClearSyncReview\(\)/);
  assert.match(vault, /CryptographicOperations\.ZeroMemory\(selected\.EncryptedPayload\)/);
  assert.match(vault, /CryptographicOperations\.ZeroMemory\(entry\.EncryptedPayload\)/);
  assert.match(vault, /CryptographicOperations\.ZeroMemory\(ready\.EncryptedPayload\)/);
  assert.match(vault, /ReplacePendingSnapshot\(await PendingQueue\.ReviewAllAsync\(/);
  assert.match(vault, /SetSyncReview\(await PendingQueue\.PrepareReviewAsync\(/);
  assert.match(vault, /public void Dispose\(\)\s*\{\s*ClearPendingSnapshot\(\);\s*ClearSyncReview\(\);/);
  const lock = vault.slice(vault.indexOf('private async Task LockAsync()'));
  assert.match(lock, /ClearPendingSnapshot\(\);\s*ClearSyncReview\(\);/);
});

test('direct on-chain deletion excludes malformed file envelopes and refreshes approved snapshot', () => {
  const rootPath = path.join(root, '..', '..');
  const classifier = fs.readFileSync(path.join(rootPath,
    'StellarVault.Core', 'Services', 'VaultPublishedFileClassification.cs'), 'utf8');
  const row = fs.readFileSync(path.join(rootPath,
    'StellarVault.UI', 'Components', 'VaultExplorerItemRow.razor'), 'utf8');
  const inspector = fs.readFileSync(path.join(rootPath,
    'StellarVault.UI', 'Components', 'VaultRecordInspector.razor'), 'utf8');
  const vault = fs.readFileSync(path.join(root, '..', 'Pages', 'Vault.razor'), 'utf8');
  assert.match(classifier, /public static bool IsManagedFileProtocolRecord\(/);
  const smoke = fs.readFileSync(path.join(rootPath, '..', 'tests',
    'StellarVault.Core.CryptoTests', 'VaultPublishedFileClassificationSmoke.cs'), 'utf8');
  const runner = fs.readFileSync(path.join(rootPath, '..', 'tests',
    'StellarVault.Core.CryptoTests', 'Program.cs'), 'utf8');
  assert.match(smoke, /Malformed published file was not protected from ordinary mutation/);
  assert.match(runner, /VaultPublishedFileClassificationSmoke\.RunAsync\(\)/);
  for (const marker of ['SinglePrefix', 'ManifestPrefix', 'ChunkPrefix', 'VaultLinkedTreeFilePolicy.Prefix'])
    assert.ok(classifier.includes(marker), 'must recognize ' + marker);
  assert.match(row, /!VaultPublishedFileClassification\.IsManagedFileProtocolRecord\(Item\) && CanDelete/);
  assert.match(inspector, /CanDelete && !VaultPublishedFileClassification\.IsManagedFileProtocolRecord\(Item\)/);
  const prepare = vault.slice(vault.indexOf('private async Task SelectDelete('),
    vault.indexOf('private async Task DeleteSelectedAsync()'));
  const submit = vault.slice(vault.indexOf('private async Task DeleteSelectedAsync()'),
    vault.indexOf('private string? _error;', vault.indexOf('private async Task DeleteSelectedAsync()')));
  assert.match(prepare, /VaultPublishedFileClassification\.IsManagedFileProtocolRecord\(item\)/);
  assert.match(prepare, /var remote = await Ledger\.ReadAsync\(\)/);
  assert.match(prepare, /SamePublishedDeleteSnapshot\(item, current\)/);
  assert.match(prepare, /current\.IsFolder && remote\.Any/);
  assert.match(submit, /var remote = await Ledger\.ReadAsync\(\)/);
  assert.match(submit, /SamePublishedDeleteSnapshot\(candidate, current\)/);
  assert.match(submit, /freshCost\.NetworkFeeXlm > _deleteEstimate\.NetworkFeeXlm/);
  assert.match(submit, /freshCost\.PeakReserveXlm > _deleteEstimate\.PeakReserveXlm/);
  assert.match(submit, /await Ledger\.DeleteAsync\(candidate\.Id\)/);
  assert.ok(submit.indexOf('SamePublishedDeleteSnapshot(candidate, current)') <
    submit.indexOf('await Ledger.DeleteAsync(candidate.Id)'), 'preflight must run before deletion');
});

test('Stellar-only HD file upload uses a separate no-persistence fee-approved file plan', () => {
  const editor = fs.readFileSync(path.join(root, '..', 'Pages', 'VaultItemEditor.razor'), 'utf8');
  const core = fs.readFileSync(path.join(root, '..', '..', 'StellarVault.Core',
    'Services', 'StellarLedgerUploadCoordinator.cs'), 'utf8');
  assert.match(editor, /@inject StellarLedgerUploadCoordinator DirectFileUpload/);
  assert.match(editor, /@onclick="PrepareDirectFileAsync"/);
  assert.match(editor, /!CanSaveOffline && _selectedHdFile is \{ Size: > StellarLedgerFileProtocol\.MaxFileBytes \}/);
  assert.match(editor, /selected\.OpenReadStream\(StellarLedgerFileProtocol\.MaxFileBytes\)/);
  assert.match(editor, /new StellarLedgerUploadPlan\(/);
  assert.match(editor, /CryptographicOperations\.ZeroMemory\(content\)/);
  assert.match(editor, /DirectFileUpload\.ReviewAsync\(plan\)/);
  assert.match(editor, /DirectFileUpload\.PublishNextApprovedAsync\(/);
  assert.match(editor, /fresh\.NetworkFeeXlm <= approvedCost\.NetworkFeeXlm/);
  assert.match(editor, /_directFileUncertain = true/);
  assert.match(editor, /StellarLedgerFileProtocol\.DecodeFile\(manifest, remote\)/);
  assert.match(core, /plan\.Remaining\(finalSnapshot\)/);
  assert.match(core, /await ledger\.PublishWithIdAsync\(next\.Id, next\.Draft\)/);
  assert.match(core, /requestApproval\(next\.Draft\)/);
  assert.doesNotMatch(editor, /localStorage\.setItem|sessionStorage\.setItem/);
});
