'use strict';
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { createHash } = require('node:crypto');
const root = path.join(__dirname, '..');
const directory = path.join(root, 'src/StellarVault.Pwa/wwwroot/js/vendor');
const provenance = JSON.parse(fs.readFileSync(path.join(directory, 'stellar-sdk.provenance.json')));
const lock = JSON.parse(fs.readFileSync(path.join(root, 'package-lock.json')));
const dependency = lock.packages['node_modules/@stellar/stellar-sdk'];
assert.equal(dependency.version, provenance.version, 'Vendored SDK differs from audited dependency version');
assert.equal(dependency.resolved, provenance.artifact, 'Vendored SDK source differs from lockfile');
const bundle = fs.readFileSync(path.join(directory, 'stellar-sdk.min.js'), 'utf8').replace(/\r\n/g, '\n');
assert.equal(createHash('sha256').update(bundle).digest('hex'), provenance.normalizedSha256,
  'Vendored SDK integrity mismatch');
assert.ok(fs.readFileSync(path.join(directory, 'stellar-sdk.LICENSE'), 'utf8').includes('Apache License'),
  'SDK license/third-party notices missing');
console.log(`PASS: vendored Stellar SDK ${provenance.version}, normalized integrity, audited lockfile and license.`);
