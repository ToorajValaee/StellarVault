const test = require('node:test');
const assert = require('node:assert/strict');
const sdk = require('../src/StellarVault.Pwa/wwwroot/js/vendor/stellar-sdk.min.js');
const fs = require('node:fs');
const vm = require('node:vm');
const { webcrypto } = require('node:crypto');

test('bundled SDK signs the RFC 8032 Ed25519 empty-message vector', () => {
  const key = sdk.Keypair.fromRawEd25519Seed(Buffer.from(
    '9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60', 'hex'));
  assert.equal(key.rawPublicKey().toString('hex'),
    'd75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a');
  const signature = key.sign(Buffer.alloc(0));
  assert.equal(signature.toString('hex'),
    'e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555f' +
    'b8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b');
  assert.ok(key.verify(Buffer.alloc(0), signature));
  assert.ok(!key.verify(Buffer.from('tampered'), signature));
});

test('independent S-key writer builds real SDK binary operations and quarantines a lost response', async () => {
  const key = sdk.Keypair.fromRawEd25519Seed(Buffer.alloc(32, 9));
  const root = key.publicKey();
  const passphrase = 'Synthetic integration test network';
  let checkpoint = null, attempts = 0;
  class Server {
    async loadAccount() {
      return Object.assign(new sdk.Account(root, '1'), { account_id: root, data_attr: {},
        balances: [{ asset_type: 'native', balance: '100.0000000', selling_liabilities: '0' }],
        signers: [{ key: root, type: 'ed25519_public_key', weight: 1 }],
        thresholds: { low_threshold: 1, med_threshold: 1, high_threshold: 1 },
        subentry_count: 0, num_sponsoring: 0, num_sponsored: 0 });
    }
    async submitTransaction(tx) {
      attempts++;
      assert.equal(tx.hash().toString('hex'), checkpoint, 'Broadcast preceded durable hash checkpoint');
      assert.ok(key.verify(tx.hash(), tx.signatures[0].signature()));
      assert.ok(tx.operations.some(operation => operation.name.startsWith('skh:')));
      assert.ok(tx.operations.some(operation => operation.name.startsWith('skc:')));
      for (const operation of tx.operations)
        assert.ok(operation.value.byteLength <= 64, 'ManageData value exceeded the protocol limit');
      throw new Error('Synthetic lost response');
    }
  }
  const sandbox = { window: { StellarSdk: { ...sdk, Horizon: { Server } } },
    crypto: webcrypto, TextEncoder, TextDecoder, URL, atob, btoa,
    fetch: async url => ({ ok: true, json: async () => url.includes('/ledgers?')
      ? { _embedded: { records: [{ base_reserve_in_stroops: 5000000 }] } }
      : url.endsWith('/fee_stats') ? { last_ledger_base_fee: '100' }
      : { network_passphrase: passphrase } }) };
  vm.runInNewContext(fs.readFileSync(require.resolve('../src/StellarVault.Pwa/wwwroot/js/vault-secret-ledger.js'), 'utf8'), sandbox);
  const api = sandbox.window.stellarVaultSecretLedger;
  await api.open('test', key.secret(), 'https://synthetic.invalid', passphrase);
  const quote = await api.plan('test', 'create', '0123456789abcdef', {
    Title: 'Synthetic test', Content: 'Synthetic data', Tags: [], IsFolder: false });
  await assert.rejects(api.submit('test', quote.QuoteId, { invokeMethodAsync: async (method, hash) => {
    assert.equal(method, 'CheckpointSignedHash'); checkpoint = hash;
  } }), /outcome is uncertain/);
  assert.equal(attempts, 1);
  assert.equal(api.inspectUncertain('test').TransactionHash, checkpoint);
  await assert.rejects(api.plan('test', 'create', 'fedcba9876543210', {}), /unresolved/);
  api.close('test');
});

test('bundled SDK round-trips signed ManageData XDR and binds the network', () => {
  const key = sdk.Keypair.fromRawEd25519Seed(Buffer.alloc(32, 7));
  const network = 'Synthetic verification network';
  const tx = new sdk.TransactionBuilder(new sdk.Account(key.publicKey(), '1'), { fee: '100', networkPassphrase: network })
    .addOperation(sdk.Operation.manageData({ name: 'synthetic-test', value: key.rawPublicKey().constructor.from('synthetic-data') }))
    .setTimeout(30).build();
  tx.sign(key);
  const decoded = sdk.TransactionBuilder.fromXDR(tx.toXDR(), network);
  assert.equal(decoded.source, key.publicKey());
  assert.equal(decoded.operations[0].name, 'synthetic-test');
  assert.ok(key.verify(decoded.hash(), decoded.signatures[0].signature()));
  const wrongNetwork = sdk.TransactionBuilder.fromXDR(tx.toXDR(), 'Wrong synthetic network');
  assert.ok(!key.verify(wrongNetwork.hash(), wrongNetwork.signatures[0].signature()));
});
