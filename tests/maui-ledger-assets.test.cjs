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
  return [...host.matchAll(/<script\s+src="(js\/[^"]+\.js)"/g)].map(match => match[1]);
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

test('native scripts appear before the Blazor WebView initialization', () => {
  const bootstrap = host.indexOf('src="_framework/blazor.webview.js"');
  assert.ok(bootstrap > 0);
  for (const script of scripts()) {
    assert.ok(host.indexOf('src="' + script + '"') < bootstrap, 'Late script: ' + script);
  }
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
  const nativeCss = fs.readFileSync(path.join(maui, 'wwwroot/native-app.css'), 'utf8');
  assert.match(appRoot, /HostPlatform="@NativePlatform"/);
  assert.match(workspace, /class="sv-native-nav"/);
  assert.match(workspace, /class="sv-native-scroll"/);
  assert.match(workspace, /class="sv-native-commandbar"/);
  for (const page of ['stellar', 'local', 'sync']) {
    assert.ok(workspace.includes('NavigateNative("' + page + '")'),
      'Missing navigation destination ' + page);
  }
  assert.ok(host.indexOf('href="native-app.css"') > host.indexOf('href="css/app.css"'),
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
