const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function harness({ affordable = true, revision = 2, failSubmit = false } = {}) {
  const operations = [];
  const signed = [];
  let submissions = 0;
  const registry = { highestAllocatedIndex: 1, activeWriteIndex: 1, revision: 2 };
  const account = { data_attr: { 'sv:registry:v2': 'AA==' } };
  const server = {
    loadAccount: async () => account,
    submitTransaction: async tx => {
      submissions++;
      if (failSubmit) throw new Error('tx_bad_seq');
      return { hash: 'hash-1', tx };
    }
  };
  class Builder {
    constructor(source) { assert.equal(source, account); }
    addOperation(op) { operations.push(op); return this; }
    setTimeout() { return this; }
    build() { return { sign: key => signed.push(key.publicKey()) }; }
  }
  const window = {
    StellarSdk: {
      Keypair: { fromRawEd25519Seed: () => ({ publicKey: () => 'child-2' }) },
      TransactionBuilder: Builder,
      Operation: {
        beginSponsoringFutureReserves: o => ({ kind: 'begin', ...o }),
        createAccount: o => ({ kind: 'create', ...o }),
        endSponsoringFutureReserves: o => ({ kind: 'end', ...o }),
        manageData: o => ({ kind: 'registry', ...o })
      }
    },
    stellarVaultAllocationPreflight: {
      check: async () => ({ nextIndex: 2, accountId: 'child-2', registryRevision: 2 })
    },
    stellarVaultHd: { accountSeed: async () => new Uint8Array(32) },
    stellarVaultRegistry: {
      registryMacKey: async () => 'key',
      decode: async () => ({ ...registry, revision }),
      encode: async () => new Uint8Array(50)
    },
    stellarVaultTreasury: { calculate: () => ({ canAffordPlan: affordable }) }
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../wwwroot/js/hd-allocator.js'), 'utf8'),
    { window, Uint8Array, BigInt, atob: () => '\0', Set });
  const opened = {
    rootAccountId: 'root', rootKeypair: { publicKey: () => 'root' },
    registry: { ...registry }, server, networkPassphrase: 'test'
  };
  return {
    allocate: () => window.stellarVaultHdAllocator.allocate(new Uint8Array(64), opened,
      { baseReserveStroops: '5000000', baseFeeStroops: '100' }),
    operations, signed, opened, submissions: () => submissions
  };
}
test('sponsors child and persists registry in one signed transaction', async () => {
  const h = harness();
  const result = await h.allocate();
  assert.deepEqual(h.operations.map(o => o.kind), ['begin', 'create', 'end', 'registry']);
  assert.equal(h.operations[1].startingBalance, '0');
  assert.equal(h.operations[3].value.length, 50);
  assert.equal(h.operations[3].source, 'root');
  assert.deepEqual(h.signed, ['root', 'child-2']);
  assert.equal(h.submissions(), 1);
  assert.equal(result.creationHash, result.registryHash);
  assert.equal(h.opened.registry.highestAllocatedIndex, 2);
});
test('does not submit when root treasury is insufficient', async () => {
  const h = harness({ affordable: false });
  await assert.rejects(h.allocate(), /cannot cover/);
  assert.equal(h.submissions(), 0);
});
test('does not submit when registry changed', async () => {
  const h = harness({ revision: 3 });
  await assert.rejects(h.allocate(), /Registry changed/i);
  assert.equal(h.submissions(), 0);
});
test('failed atomic submission does not advance local registry', async () => {
  const h = harness({ failSubmit: true });
  await assert.rejects(h.allocate(), /refresh before retrying/);
  assert.equal(h.opened.registry.highestAllocatedIndex, 1);
});
