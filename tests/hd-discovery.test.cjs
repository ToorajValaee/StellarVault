const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function setup(existing, highest = 3) {
  const window = {
    stellarVaultHd: { accountSeed: async (_, i) => Uint8Array.of(i) },
    StellarSdk: { Keypair: { fromRawEd25519Seed: raw => {
      const index = raw[0];
      return { publicKey: () => 'child-' + index };
    } } }
  };
  vm.runInNewContext(fs.readFileSync(
    path.join(__dirname, '../wwwroot/js/hd-discovery.js'), 'utf8'),
  { window, Uint8Array, Number, Math, Error, RangeError });
  const opened = {
    rootAccountId: 'root',
    registry: { highestAllocatedIndex: highest },
    server: { loadAccount: async id => {
      const index = Number(id.split('-')[1]);
      if (!existing.includes(index)) {
        const error = new Error('not found');
        error.response = { status: 404 };
        throw error;
      }
      return { subentry_count: 0, data_attr: {} };
    } }
  };
  return { discover: (options = { recoveryLookahead: 2 }) =>
    window.stellarVaultHdDiscovery.discover(new Uint8Array(64), opened, options) };
}
test('discovers registered accounts and next unused index', async () => {
  const result = await setup([1, 2, 3]).discover();
  assert.equal(result.requiresReconciliation, false);
  assert.equal(result.scannedThrough, 5);
  assert.equal(result.registeredGaps.length, 0);
});
test('reports missing registered children', async () => {
  const result = await setup([1, 3]).discover();
  assert.equal(result.requiresReconciliation, true);
  assert.deepEqual(Array.from(result.registeredGaps), [2]);
});
test('reports derived accounts beyond registry', async () => {
  const result = await setup([1, 2, 3, 4]).discover();
  assert.equal(result.requiresReconciliation, true);
  assert.equal(result.unregisteredNodes[0].index, 4);
});
test('rejects a scan that cannot inspect the next index', async () => {
  await assert.rejects(setup([1, 2, 3]).discover({ recoveryLookahead: 0 }),
    /lookahead/);
});

test('allows scanning the last supported registered index', async () => {
  const result = await setup([], 4096).discover({
    recoveryLookahead: 1, maxScan: 4096
  });
  assert.equal(result.scannedThrough, 4096);
  assert.equal(result.registeredGaps.length, 4096);
});

test('rejects a malformed registry index before deriving accounts', async () => {
  await assert.rejects(setup([], -1).discover(), /invalid highest allocated index/);
  await assert.rejects(setup([], 1.5).discover(), /invalid highest allocated index/);
});
