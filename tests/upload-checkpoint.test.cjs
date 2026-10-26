// Run with: node --test tests/upload-checkpoint.test.cjs
const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');

function harness() {
  const entries = new Map();
  const localStorage = {
    getItem: key => entries.has(key) ? entries.get(key) : null,
    setItem: (key, value) => entries.set(key, value),
    removeItem: key => entries.delete(key)
  };
  const context = vm.createContext({ window: {}, localStorage, Error, Set });
  vm.runInContext(fs.readFileSync(path.join(__dirname, '..',
    'src/StellarVault.Pwa/wwwroot/js/vault-v2-upload-checkpoint.js'), 'utf8'), context);
  return { api: context.window.stellarVaultUploadCheckpoint, entries };
}

const account = 'G' + 'A'.repeat(55);
const fingerprint = 'A'.repeat(64);
const ids = ['0123456789abcdef', 'fedcba9876543210'];
const testnetKey = 'sv-upload-checkpoint-v1:' + account;

test('legacy Testnet checkpoint key is preserved', () => {
  const { api, entries } = harness();
  api.save(account, fingerprint, ids, 1);
  assert.ok(entries.has(testnetKey));
  const loaded = api.load(account);
  assert.equal(loaded.step, 1);
  assert.deepEqual(Array.from(loaded.ids), ids);
});

test('Public and custom networks have independent checkpoint keys', () => {
  const { api, entries } = harness();
  const publicKey = 'public:' + account;
  const customKey = 'custom:' + 'b'.repeat(64) + ':' + account;
  api.save(account, fingerprint, ids, 0);
  api.save(publicKey, fingerprint, ids, 1);
  api.save(customKey, fingerprint, ids, 2);
  assert.equal(entries.size, 3);
  assert.equal(api.load(account).step, 0);
  assert.equal(api.load(publicKey).step, 1);
  assert.equal(api.load(customKey).step, 2);
});

test('corruption is not treated as a missing checkpoint', () => {
  const { api, entries } = harness();
  entries.set(testnetKey, '{bad json');
  assert.throws(() => api.load(account), /Invalid upload checkpoint/);
  assert.throws(() => api.save(account, fingerprint, ids, 0), /Invalid upload checkpoint/);
  assert.equal(entries.get(testnetKey), '{bad json');
});

test('duplicate IDs are rejected during save and recovery', () => {
  const { api, entries } = harness();
  assert.throws(() => api.save(account, fingerprint, [ids[0], ids[0]], 0), /Invalid checkpoint state/);
  entries.set(testnetKey, JSON.stringify({ version: 1, fingerprint, ids: [ids[0], ids[0]], step: 0 }));
  assert.throws(() => api.load(account), /Invalid upload checkpoint/);
});

test('checkpoint IDs, fingerprint and step cannot roll back', () => {
  const { api } = harness();
  api.save(account, fingerprint, ids, 1);
  assert.throws(() => api.save(account, fingerprint, ids, 0), /rolled back/);
  assert.throws(() => api.save(account, 'B'.repeat(64), ids, 1), /identity changed/);
  assert.throws(() => api.save(account, fingerprint, [ids[1], ids[0]], 1), /identity changed/);
  assert.equal(api.load(account).step, 1);
  api.save(account, fingerprint, ids, 2);
  assert.equal(api.load(account).step, 2);
});

test('clear removes only the selected network checkpoint', () => {
  const { api } = harness();
  const publicKey = 'public:' + account;
  api.save(account, fingerprint, ids, 0);
  api.save(publicKey, fingerprint, ids, 0);
  api.clear(publicKey);
  assert.equal(api.load(publicKey), null);
  assert.ok(api.load(account));
});
