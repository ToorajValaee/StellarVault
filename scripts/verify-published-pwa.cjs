'use strict';
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { createHash } = require('node:crypto');

const root = path.resolve(process.argv[2] || 'artifacts/release/pwa/wwwroot');
const manifest = { self: {} };
vm.runInNewContext(fs.readFileSync(path.join(root, 'service-worker-assets.js'), 'utf8'), manifest,
  { timeout: 1000 });
const assets = manifest.self.assetsManifest;
assert.ok(assets.version && assets.assets.length > 0, 'Published asset manifest is empty');
for (const asset of assets.assets) {
  // The SDK hashes its development worker before our publish alias replacement.
  // These bootstrap files are deliberately excluded from the worker's cache.
  if (/^service-worker(?:\.published)?\.js$|^service-worker-assets\.js$/.test(asset.url)) continue;
  const file = path.resolve(root, asset.url);
  assert.ok(file.startsWith(root + path.sep), 'Asset escapes the published root');
  assert.equal('sha256-' + createHash('sha256').update(fs.readFileSync(file)).digest('base64'),
    asset.hash, `Integrity mismatch: ${asset.url}`);
}
const productionWorker = fs.readFileSync(path.join(__dirname,
  '../src/StellarVault.Pwa/wwwroot/service-worker.published.js'));
assert.ok(fs.readFileSync(path.join(root, 'service-worker.js')).equals(productionWorker),
  'Published worker alias must contain the production implementation');
for (const extension of ['gz', 'br'])
  assert.ok(!fs.existsSync(path.join(root, 'service-worker.js.' + extension)),
    'A compressed worker alias can serve a different release');
const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
for (const match of html.matchAll(/(?:src|href)="([^"#]+)"/g)) {
  const url = new URL(match[1], 'https://release.invalid/');
  assert.equal(url.origin, 'https://release.invalid', 'Application shell loads an external asset');
  if (url.pathname !== '/')
    assert.ok(fs.existsSync(path.join(root, url.pathname)), `Missing shell asset: ${url.pathname}`);
}
console.log(`PASS: ${assets.assets.length} published asset hashes, shell dependencies and production worker alias (${assets.version}).`);
