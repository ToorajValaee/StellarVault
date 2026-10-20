const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function setup(discovery, latest) {
  const window = {
    stellarVaultHdDiscovery: { discover: async () => discovery },
    stellarVaultRegistry: { registryMacKey: async () => ({}), decode: async () => latest },
    stellarVaultHd: { accountSeed: async (_, index) => Uint8Array.from([index]) },
    StellarSdk: { Keypair: { fromRawEd25519Seed: raw => ({ publicKey: () => 'child-' + raw[0] }) } }
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../wwwroot/js/hd-allocation-preflight.js'), 'utf8'),
    { window, Uint8Array, atob: () => '\0' });
  return window.stellarVaultAllocationPreflight;
}
const registry = { highestAllocatedIndex: 3, activeWriteIndex: 3, revision: 7 };
const opened = () => ({
  registry, rootAccountId: 'root', networkPassphrase: 'test',
  server: { loadAccount: async () => ({ data_attr: { 'sv:registry:v2': 'AA==' } }) }
});
test('allocates next derived index only after successful scan', async () => {
  const result = await setup({ scannedThrough: 19, requiresReconciliation: false }, registry)
    .check(new Uint8Array(64), opened());
  assert.equal(result.nextIndex, 4);
  assert.equal(result.accountId, 'child-4');
});
test('rejects incomplete scan', async () => {
  await assert.rejects(
    setup({ scannedThrough: 3, requiresReconciliation: false }, registry)
      .check(new Uint8Array(64), opened()), /not scanned/);
});
test('rejects registry divergence', async () => {
  await assert.rejects(
    setup({ scannedThrough: 19, requiresReconciliation: false },
      { highestAllocatedIndex: 4, activeWriteIndex: 4, revision: 8 })
      .check(new Uint8Array(64), opened()), /Registry changed/);
});
test('rejects inconsistent derived account discovery', async () => {
  await assert.rejects(
    setup({ scannedThrough: 19, requiresReconciliation: true }, registry)
      .check(new Uint8Array(64), opened()), /registry mismatch/);
});

test('rejects allocation after maximum supported child index', async () => {
  const full = { highestAllocatedIndex: 4096, activeWriteIndex: 4096, revision: 8 };
  await assert.rejects(
    setup({ scannedThrough: 4096, requiresReconciliation: false }, full)
      .check(new Uint8Array(64), { ...opened(), registry: full }),
    /allocation limit reached/);
});

test('rejects fractional and negative highest allocation indices', async () => {
  const preflight = setup({ scannedThrough: 19, requiresReconciliation: false }, registry);
  for (const highestAllocatedIndex of [-1, 1.5]) {
    await assert.rejects(preflight.check(new Uint8Array(64), {
      ...opened(), registry: { ...registry, highestAllocatedIndex }
    }), /registry index invalid/);
  }
});
