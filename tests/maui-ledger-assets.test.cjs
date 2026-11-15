// Run with: node --test tests/maui-ledger-assets.test.cjs
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..');
const maui = path.join(root, 'src/StellarVault.Maui');
const pwaJs = path.join(root, 'src/StellarVault.Pwa/wwwroot/js');
const host = fs.readFileSync(path.join(maui, 'wwwroot/index.html'), 'utf8');
const project = fs.readFileSync(path.join(maui, 'StellarVault.Maui.csproj'), 'utf8');

function scripts() {
  return [...host.matchAll(/"((?:js\/)[^"]+\.js)"/g)].map(match => match[1]);
}

test('all WebView ledger scripts exist in the canonical PWA source', () => {
  const loaded = scripts();
  assert.ok(loaded.includes('js/ledger-compat/app.js'));
  for (const script of loaded) {
    const canonical = path.join(pwaJs, script.slice(3));
    assert.ok(fs.existsSync(canonical), 'Missing PWA source: ' + script);
  }
});

test('all WebView ledger scripts are included as native MAUI assets', () => {
  const includes = [...project.matchAll(/<MauiAsset\s+Include="([^"]+)"\s+LogicalName="([^"]+)"\s*\/>/g)]
    .map(match => ({ source: match[1], target: match[2] }));
  const mapped = new Set();
  for (const entry of includes) {
    if (entry.source.endsWith('/*.js')) {
      const dir = path.resolve(maui, entry.source.slice(0, -5));
      assert.ok(fs.existsSync(dir), 'Missing asset folder: ' + dir);
      for (const file of fs.readdirSync(dir).filter(name => name.endsWith('.js'))) {
        mapped.add(entry.target.replace('%(Filename)%(Extension)', file).replace('wwwroot/', ''));
      }
    } else if (entry.source.endsWith('/**')) {
      // MAUI's optional recursive Raw glob is not a concrete script asset.
      // The raw directory can be empty and absent from Git.
      continue;
    } else {
      assert.ok(fs.existsSync(path.resolve(maui, entry.source)), 'Missing asset source: ' + entry.source);
      mapped.add(entry.target.replace('wwwroot/', ''));
    }
  }
  for (const script of scripts()) {
    assert.ok(mapped.has(script), 'Script not packaged as MauiAsset: ' + script);
  }
});

test('native ledger scripts load on demand after Blazor starts', () => {
  assert.match(host, /nativeLedgerScripts/);
  assert.match(host, /stellarVaultNativeRuntime/);
  assert.match(host, /document\.createElement\('script'\)/);
  assert.ok(!/<script\s+src="js\/[^"]+\.js"/.test(host),
    'Blocking ledger scripts must not delay the first native screen');
  const component = fs.readFileSync(path.join(root,
    'src/StellarVault.UI/Components/NativeVaultWorkspace.razor'), 'utf8');
  assert.match(component, /InvokeVoidAsync\("stellarVaultNativeRuntime\.load"\)/);
});

test('native entrypoint mounts the dedicated native workspace with startup fallback', () => {
  const root = fs.readFileSync(path.join(maui, 'AppRoot.razor'), 'utf8');
  const page = fs.readFileSync(path.join(maui, 'MainPage.xaml'), 'utf8');
  assert.match(root, /<NativeVaultWorkspace\s+HostPlatform="@NativePlatform"\s*\/>/);
  assert.doesNotMatch(root, /<VaultHome\b/);
  assert.match(page, /Selector="#app"/);
  assert.match(page, /ComponentType="\{x:Type local:AppRoot\}"/);
  assert.match(host, /_framework\/blazor\.webview\.js/);
  assert.match(host, /data-startup-detail/);
  assert.match(host, /stellarVaultShowStartupFailure/);
});

test('MAUI renders platform-adaptive native shell with scrollable content and fixed actions', () => {
  const workspace = fs.readFileSync(path.join(root,
    'src/StellarVault.UI/Components/NativeVaultWorkspace.razor'), 'utf8');
  const appRoot = fs.readFileSync(path.join(maui, 'AppRoot.razor'), 'utf8');
  const nativeCss = fs.readFileSync(path.join(maui, 'wwwroot/modern-native.css'), 'utf8');
  assert.match(appRoot, /HostPlatform="@NativePlatform"/);
  assert.match(workspace, /class="sv-native-nav"/);
  assert.match(workspace, /class="sv-native-scroll"/);
  assert.match(workspace, /class="sv-native-commandbar @\(/);
  for (const page of ['stellar', 'local', 'sync']) {
    assert.ok(workspace.includes('NavigateNative("' + page + '")'),
      'Missing navigation destination ' + page);
  }
  assert.ok(host.indexOf('href="modern-native.css"') > host.indexOf('href="css/modern-app.css"'),
    'Native override must be loaded after web styling');
  assert.match(nativeCss, /overflow-y:\s*auto/);
  assert.match(nativeCss, /minmax\(0,\s*1fr\)/);
  assert.match(nativeCss, /native-windows \.sv-native-nav/);
  assert.match(nativeCss, /env\(safe-area-inset-bottom/);
  assert.match(nativeCss, /\.sv-native-commandbar/);
});

test('Android keyboard resizes WebView instead of obscuring offline editors', () => {
  const activity = fs.readFileSync(path.join(maui,
    'Platforms/Android/MainActivity.cs'), 'utf8');
  assert.match(activity, /WindowSoftInputMode\s*=\s*SoftInput\.AdjustResize/);
});

test('Android Back returns to Stellar destination without bypassing the vault', () => {
  const page = fs.readFileSync(path.join(maui, 'MainPage.xaml.cs'), 'utf8');
  const app = fs.readFileSync(path.join(maui, 'App.xaml.cs'), 'utf8');
  const workspace = fs.readFileSync(path.join(root,
    'src/StellarVault.UI/Components/NativeVaultWorkspace.razor'), 'utf8');
  assert.match(page, /OnBackButtonPressed/);
  assert.match(page, /_navigation\.TryRequestBack\(\)/);
  assert.match(app, /new MainPage\(_navigation, _startup\)/);
  assert.match(app, /_navigation\.Clear\(\)/);
  assert.match(workspace, /NativeNavigation\.BackRequested \+= OnAndroidBackRequested/);
  assert.match(workspace, /NativeNavigation\.Clear\(\)/);
});

test('native startup fallback is independent of JavaScript and first render reports ready', () => {
  const xaml = fs.readFileSync(path.join(maui, 'MainPage.xaml'), 'utf8');
  const codeBehind = fs.readFileSync(path.join(maui, 'MainPage.xaml.cs'), 'utf8');
  const root = fs.readFileSync(path.join(maui, 'AppRoot.razor'), 'utf8');
  assert.match(xaml, /x:Name="NativeStartupOverlay"/);
  assert.match(xaml, /x:Name="NativeStartupMessage"/);
  assert.match(codeBehind, /ReportUnresponsiveStartupAsync/);
  assert.match(codeBehind, /OnFirstRenderCompleted/);
  assert.match(codeBehind, /NativeStartupOverlay\.IsVisible = false/);
  assert.match(root, /Startup\.NotifyFirstRender\(\)/);
});

test('native styling retains structural components under the modern theme', () => {
  const nativeCss = fs.readFileSync(path.join(maui, 'wwwroot/modern-native.css'), 'utf8');
  assert.match(nativeCss, /--sv-native-accent:#168568/);
  assert.doesNotMatch(nativeCss, /Cascadia Code/);
  assert.match(nativeCss, /\.sv-native-nav/);
  assert.match(nativeCss, /\.sv-native-scroll/);
});

test('native startup reports WebView initialization stages and isolates first Razor render', () => {
  const xaml = fs.readFileSync(path.join(maui, 'MainPage.xaml'), 'utf8');
  const page = fs.readFileSync(path.join(maui, 'MainPage.xaml.cs'), 'utf8');
  const root = fs.readFileSync(path.join(maui, 'AppRoot.razor'), 'utf8');
  assert.match(xaml, /BlazorWebViewInitializing="OnWebViewInitializing"/);
  assert.match(xaml, /BlazorWebViewInitialized="OnWebViewInitialized"/);
  assert.match(page, /StellarVaultStartup/);
  assert.match(page, /Startup stopped at:/);
  assert.match(root, /if \(!_shellReady\)/);
  assert.match(root, /Startup\.NotifyFirstRender\(\)/);
});

test('MAUI WebView and Controls use the same supported stable patch', () => {
  const controls = project.match(/Microsoft\.Maui\.Controls" Version="([^"]+)"/)?.[1];
  const hybrid = project.match(/Microsoft\.AspNetCore\.Components\.WebView\.Maui" Version="([^"]+)"/)?.[1];
  assert.equal(controls, '10.0.120');
  assert.equal(hybrid, controls,
    'Mismatched MAUI/BlazorWebView packages can create native bridge compatibility problems');
});

test('Android bridge timeout includes safe WebView compatibility metadata', () => {
  const page = fs.readFileSync(path.join(maui, 'MainPage.xaml.cs'), 'utf8');
  assert.match(page, /Android\.Webkit\.WebView\.CurrentWebViewPackage/);
  assert.match(page, /Android\.OS\.Build\.VERSION\.SdkInt/);
  assert.match(page, /_androidWebViewInfo/);
  assert.doesNotMatch(page, /JavaScriptInterface|AddJavascriptInterface/);
});

test('MAUI native bridge owns Blazor start and disables script autostart', () => {
  assert.match(host, /<script\s+src="_framework\/blazor\.webview\.js"\s+autostart="false"/);
  assert.doesNotMatch(host, /Blazor\.start\s*\(/);
  assert.doesNotMatch(host, /blazor\.webassembly\.js/);
  assert.doesNotMatch(host, /blazor\.server\.js/);
});

test('native entry screen is task-first with no developer runtime button', () => {
  const component = fs.readFileSync(path.join(root,
    'src/StellarVault.UI/Components/NativeVaultWorkspace.razor'), 'utf8');
  assert.doesNotMatch(component, />Check native runtime</);
  assert.doesNotMatch(component, /@HostPlatform · PRIVATE STORAGE/);
  assert.doesNotMatch(component, /"Welcome back"|"Connect your vault"/);
  assert.match(component, />Recovery phrase</);
  assert.match(component, /OnAfterRenderAsync\(bool firstRender\)/);
  assert.match(component, /await CheckRuntimeAsync\(\)/);
  assert.match(component, /_ledgerMessage = status.Ready \? null/);
  assert.match(component, />Try again</);
  assert.match(component, /SelectNativeNetwork\("public"\)/);
  assert.match(component, /_ledgerNetwork == "public" && !_confirmMainnetAccess/);
});

test('native header network menu discovers a verified custom Horizon passphrase', () => {
  const component = fs.readFileSync(path.join(root,
    'src/StellarVault.UI/Components/NativeVaultWorkspace.razor'), 'utf8');
  assert.match(component, /class="sv-native-header-network"/);
  assert.match(component, /SelectNativeNetwork\("custom"\)/);
  assert.match(component, /stellarVaultV2Network\.probeCustom/);
  assert.match(component, /NetworkSelection\.ConfigureCustom\(detected\)/);
  assert.match(component, /NetworkSelection\.CustomHorizonUrl = requestedHorizon/);
  assert.match(component, /_ledgerNetwork == "custom" && !CustomNetworkReady/);
  assert.doesNotMatch(component, /class="sv-native-network-settings"/);
  assert.doesNotMatch(component, /@HostPlatform · PRIVATE STORAGE/);
});

test('native record editor matches PWA record metadata and preserves full review fingerprint', () => {
  const native = fs.readFileSync(path.join(root,
    'src/StellarVault.UI/Components/NativeVaultWorkspace.razor'), 'utf8');
  for (const field of ['_publishTitle', '_publishFolder', '_publishContent',
    '_publishUsername', '_publishUrl', '_publishNotes', '_publishTags']) {
    assert.ok(native.includes(field), 'Missing native record field: ' + field);
  }
  assert.match(native, /private LedgerRecordDraft CurrentPublishDraft/);
  assert.match(native, /private string CurrentPublishFingerprint/);
  assert.match(native, /_publishFingerprint != CurrentPublishFingerprint\(\)/);
  assert.match(native, /@onclick='\(\) => SetNativeMode\("folder"\)'/);
  assert.match(native, /_nativeMode is "record" or "folder"/);
  assert.match(native, /CurrentPublishDraft\(\)/);
});

test('native explorer has mobile object tiles, desktop folder tree and back navigation', () => {
  const native = fs.readFileSync(path.join(root,
    'src/StellarVault.UI/Components/NativeVaultWorkspace.razor'), 'utf8');
  const styles = fs.readFileSync(path.join(maui, 'wwwroot/modern-native.css'), 'utf8');
  assert.match(native, /<VaultExplorerTree Folders=/);
  assert.match(native, /sv-native-object-grid/);
  assert.match(native, /<VaultRecordInspector @key="_selectedRecord.Id"/);
  assert.match(native, /aria-label="Back to vault root"/);
  assert.match(native, /OpenNativeFolder\(""\)/);
  assert.match(styles, /\.sv-native-folder-tree/);
  assert.match(styles, /\.sv-native-object-card/);
});

test('shared device and hybrid editors have a directory screen separate from item editing', () => {
  const shared = path.join(root, 'src/StellarVault.UI/Components');
  const record = fs.readFileSync(path.join(shared, 'VaultRecordWorkspaceView.razor'), 'utf8');
  const drafts = fs.readFileSync(path.join(shared, 'VaultHybridDraftEditor.razor'), 'utf8');
  const remote = fs.readFileSync(path.join(shared, 'VaultHybridRemoteChanges.razor'), 'utf8');
  assert.match(record, /@if \(_editingScreen\)/);
  assert.match(record, /BackToList/);
  assert.match(record, /_editingScreen = false/);
  assert.match(drafts, /@if \(_editingScreen\)/);
  assert.match(drafts, /BackToDraftList/);
  assert.match(drafts, /_editingScreen = false/);
  assert.match(remote, /Back to records/);
  assert.match(remote, /_editingRemote is null && _editingMutationId is null/);
});

test('MAUI loads final native-modern presentation while preserving first-render bridge', () => {
  const nativeHost = fs.readFileSync(path.join(maui, 'wwwroot/index.html'), 'utf8');
  const css = fs.readFileSync(path.join(maui, 'wwwroot/modern-native.css'), 'utf8');
  const shared = nativeHost.indexOf('href="css/modern-app.css"');
  const modern = nativeHost.indexOf('href="modern-native.css"');
  assert.ok(shared >= 0 && modern > shared);
  assert.ok(!nativeHost.includes('href="native-app.css"'));
  assert.match(nativeHost, /blazor\.webview\.js" autostart="false"/);
  assert.match(css, /--sv-native-bg:#f3f7f6/);
  assert.match(css, /\.sv-native-object-card/);
  assert.match(css, /\.sv-native-nav button\.selected/);
  assert.match(css, /\.sv-native-network-popover/);
  assert.match(css, /prefers-reduced-motion/);
});

test('native modern layout includes functional four-tab navigation and Settings', () => {
  const source = fs.readFileSync(path.join(root,
    'src/StellarVault.UI/Components/NativeVaultWorkspace.razor'), 'utf8');
  const css = fs.readFileSync(path.join(maui,'wwwroot/modern-native.css'), 'utf8');
  assert.match(source, /NavigateNative\("settings"\)/);
  assert.match(source, /_nativePage == "settings"/);
  assert.match(source, /class="sv-native-settings"/);
  assert.match(source, /DisconnectLedgerAsync/);
  assert.match(source, /LockVault/);
  assert.match(css, /\.sv-native-settings/);
  assert.match(css, /\.sv-native-nav button\.selected/);
});

test('MAUI keeps the network preset without storing a trusted custom network identity', () => {
  const workspace = fs.readFileSync(path.join(root,
    'src/StellarVault.UI/Components/NativeVaultWorkspace.razor'), 'utf8');
  const networkJs = fs.readFileSync(path.join(root,
    'src/StellarVault.Pwa/wwwroot/js/vault-v2-network.js'), 'utf8');
  assert.match(workspace, /stellarVaultNetworkPreference\.load/);
  assert.match(workspace, /stellarVaultNetworkPreference\.save/);
  assert.match(workspace, /Verifying remembered custom Horizon/);
  assert.match(workspace, /await DiscoverNativeNetworkAsync\(\)/);
  assert.match(workspace, /Custom network verified and selected/);
  assert.match(workspace, /NetworkSelection\.ResetCustom\(\)/);
  assert.match(networkJs, /stellarVaultNetworkPreference/);
});

test('Native shell theme is initialized before optional ledger runtime', () => {
  const host = fs.readFileSync(path.join(root, 'src/StellarVault.Maui/wwwroot/index.html'), 'utf8');
  const nativeCss = fs.readFileSync(path.join(root, 'src/StellarVault.Maui/wwwroot/modern-native.css'), 'utf8');
  assert.ok(host.indexOf('window.stellarVaultTheme =') > 0);
  assert.ok(host.indexOf('window.stellarVaultTheme =') < host.indexOf('window.stellarVaultNativeRuntime ='));
  assert.match(host, /dataset\.theme === 'dark' \? 'light' : 'dark'/);
  assert.match(nativeCss, /:root\[data-theme="dark"\]/);
});

test('Native phone uses the approved compact vault layout and keeps critical actions', () => {
  const workspace = fs.readFileSync(path.join(root, 'src/StellarVault.UI/Components/NativeVaultWorkspace.razor'), 'utf8');
  const css = fs.readFileSync(path.join(root, 'src/StellarVault.Maui/wwwroot/modern-native.css'), 'utf8');
  assert.match(workspace, /sv-native-mobile-collections/);
  assert.match(workspace, /sv-native-mobile-search/);
  assert.match(workspace, /sv-native-folder-shortcuts/);
  assert.match(workspace, /sv-native-shortcut-grid/);
  assert.match(workspace, /<VaultExplorerItemRow Item="item"/);
  assert.match(workspace, /<VaultRecordInspector @key="_selectedRecord.Id"/);
  const inspector = fs.readFileSync(path.join(root,
    'src/StellarVault.UI/Components/VaultRecordInspector.razor'), 'utf8');
  assert.match(workspace, /<VaultRecordInspector @key="_selectedRecord.Id"/);
  assert.match(inspector, /sv-inspector-tabs/);
  assert.match(workspace, /sv-native-mobile-fab/);
  assert.match(workspace, /sv-native-commandbar-quiet/);
  assert.match(css, /grid-template-columns:repeat\(4,minmax\(0,1fr\)\)!important/);
  assert.match(css, /sv-native-commandbar:not\(\.sv-native-commandbar-quiet\)/);
  assert.match(css, /max-height:60dvh/);
  assert.match(css, /safe-area-inset-bottom/);
  assert.doesNotMatch(workspace, /Online · Synced/);
});


test('native HD file upload shares the unlimited encrypted SVTREE3 Sync staging path', () => {
  const native = fs.readFileSync(path.join(root,
    'src/StellarVault.UI/Components/NativeVaultWorkspace.razor'), 'utf8');
  const staging = fs.readFileSync(path.join(root,
    'src/StellarVault.Core/Services/VaultHybridDraftWorkspace.cs'), 'utf8');
  assert.match(native, /@inject VaultHybridDraftWorkspace HybridDrafts/);
  assert.match(native, /@onclick="StageNativeTreeFileAsync"/);
  assert.match(native, /Retention\.AllowsLocalPersistence && Sessions\.IsUnlocked/);
  assert.match(native, /await using var input = picked\.Stream/);
  assert.match(native, /await HybridDrafts\.StageTreeFileAsync\(/);
  assert.match(staging, /VaultStreamingFileTree\.WriteContentAddressedAsync\(/);
  assert.doesNotMatch(native, /StellarLedgerUploadPlan|UploadCheckpoints|_uploadBytes|MaxFileBytes/);
  assert.doesNotMatch(native, /ChooseUploadFileAsync|PublishUploadStageAsync/);
});

test('native HD publishing commits exact approved metadata and never just title/content', () => {
  const native = fs.readFileSync(path.join(root,
    'src/StellarVault.UI/Components/NativeVaultWorkspace.razor'), 'utf8');
  const begin = native.indexOf('private async Task ConfirmPublishAsync()');
  const end = native.indexOf('private string? _uploadMessage;', begin);
  const action = native.slice(begin, end);
  assert.ok(begin >= 0 && end > begin);
  assert.match(action, /var draft = CurrentPublishDraft\(\)/);
  assert.match(action, /_publishFingerprint != CurrentPublishFingerprint\(\)/);
  assert.match(action, /Ledger\.EstimateAsync\(draft\)/);
  assert.match(action, /Ledger\.PublishDetailedAsync\(draft\)/);
  assert.doesNotMatch(action, /new LedgerRecordDraft\(_publishTitle\.Trim\(\), _publishContent\)/);
});

test('native HD file inspector authenticates SVTREE3 and exports through a bounded pipe', () => {
  const native = fs.readFileSync(path.join(root,
    'src/StellarVault.UI/Components/NativeVaultWorkspace.razor'), 'utf8');
  assert.match(native, /VaultPublishedFileClassification\.IsFile\(selected\)/);
  assert.match(native, /VaultLinkedTreeFilePolicy\.ReadAsync\(\s*current\.Id, fresh, Stream\.Null\)/);
  assert.match(native, /currentRoot\.Sha256Hex, prepared\.Sha256Hex/);
  assert.match(native, /new Pipe\(new PipeOptions\(/);
  assert.match(native, /VaultLinkedTreeFilePolicy\.ReadAsync\(\s*prepared\.RootId, current, writer, cancel\.Token\)/);
  assert.match(native, /await NativeFiles\.SaveAsync\(prepared\.FileName, reader\)/);
  assert.match(native, /CryptographicOperations\.ZeroMemory\(plaintext\)/);
});
