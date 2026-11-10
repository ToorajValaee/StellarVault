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
