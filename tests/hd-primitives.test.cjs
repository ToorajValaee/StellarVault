// Execute with Node 20+: node tests/hd-primitives.test.cjs
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const { webcrypto } = require('node:crypto');
const context = vm.createContext({ window: {}, crypto: webcrypto, TextEncoder, Uint8Array, DataView, BigInt, Number, String, Error, TypeError, RangeError, Object });
for (const file of ['hd-keys.js', 'hd-encryption.js', 'hd-registry.js'])
  vm.runInContext(fs.readFileSync(path.join(__dirname, '..', 'wwwroot', 'js', file), 'utf8'), context);

(async () => {
  const mnemonic = 'abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about';
  const seed = await context.window.stellarVaultHd.mnemonicSeed(mnemonic);
  assert.equal(Buffer.from(seed).toString('hex'),
    '5eb00bbddcf069084889a8ab9155568165f5c453ccb85e70811aaed6f6da5fc19a5ac40b389cd370d086206dec8aa6c43daea6690f20ad3d8d48b2d2ce9e38e4');
  const a = await context.window.stellarVaultHd.accountSeed(seed, 0);
  const b = await context.window.stellarVaultHd.accountSeed(seed, 1);
  assert.notDeepEqual(Buffer.from(a), Buffer.from(b));
  assert.equal(a.length, 32);
  const key = await context.window.stellarVaultRegistry.registryMacKey(seed, 'test network', 'GREGISTRY');
  const envelope = await context.window.stellarVaultRegistry.encode(key, 4, 4, 9);
  assert.equal(envelope.length, 50);
  const decoded = await context.window.stellarVaultRegistry.decode(key, envelope);
  assert.equal(decoded.highestAllocatedIndex, 4);
  assert.equal(decoded.revision, 9);
  envelope[3] ^= 1;
  await assert.rejects(() => context.window.stellarVaultRegistry.decode(key, envelope));
  const aes = await context.window.stellarVaultEncryption.deriveKey(seed, 'test network', 'GREGISTRY');
  assert.equal(aes.extractable, false);
  seed.fill(0);
  console.log('HD primitive tests passed');
})().catch(e => { console.error(e); process.exitCode = 1; });
