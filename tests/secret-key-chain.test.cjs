'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const root = path.join(__dirname, '..');
const source = fs.readFileSync(path.join(root, 'src', 'StellarVault.Pwa', 'wwwroot', 'js', 'vault-secret-chain.js'), 'utf8');
const rootId = 'G' + 'A'.repeat(55);
const childId = 'G' + 'B'.repeat(55);
const seed = 'S' + 'C'.repeat(55);
const passphrase = 'Test SDF Network ; September 2015';

function start({ linked = true, childAuthorized = true, cycle = false, rightNetwork = true } = {}) {
  const records = {
    [rootId]: {
      signers: [{ type: 'ed25519_public_key', key: rootId, weight: 1 }],
      thresholds: { low_threshold: 1, med_threshold: 1, high_threshold: 1 },
      data_attr: linked ? { 'sv:next:v1': btoa(childId) } : {},
    },
    [childId]: {
      signers: [{ type: 'ed25519_public_key', key: rootId, weight: childAuthorized ? 1 : 0 }],
      thresholds: { low_threshold: 1, med_threshold: 1, high_threshold: 1 },
      data_attr: cycle ? { 'sv:next:v1': btoa(rootId) } : {},
    },
  };
  let submissions = 0;
  const sdk = {
    StrKey: {
      isValidEd25519SecretSeed: v => v === seed,
      isValidEd25519PublicKey: v => v === rootId || v === childId,
    },
    Keypair: { fromSecret: () => ({ publicKey: () => rootId }) },
    Horizon: { Server: class {
      async loadAccount(id) {
        if (!records[id]) throw new Error('Account not found');
        return records[id];
      }
      async submitTransaction() { submissions++; throw new Error('Forbidden network write'); }
    }},
  };
  const sandbox = {
    window: { StellarSdk: sdk }, URL, atob,
    fetch: async () => ({
      ok: true,
      json: async () => ({ network_passphrase: rightNetwork ? passphrase : 'Wrong network' }),
    }),
  };
  vm.runInNewContext(source, sandbox, { filename: 'vault-secret-chain.js' });
  return { inspect: (credential = seed) =>
    sandbox.window.stellarVaultSecretChain.inspect(
      credential, 'https://horizon-testnet.stellar.org', passphrase),
    writes: () => submissions, records,
  };
}

test('single root is valid without inventing child accounts', async () => {
  const fixture = start({ linked: false });
  const result = await fixture.inspect();
  assert.equal(result.Accounts.length, 1);
  assert.equal(result.RootAccountId, rootId);
  assert.equal(result.HasChain, false);
  assert.equal(fixture.writes(), 0);
});

test('linked-account inspection follows pointers and verifies root signer thresholds', async () => {
  const fixture = start();
  const result = await fixture.inspect();
  assert.equal(result.Accounts.length, 2);
  assert.equal(result.TailAccountId, childId);
  assert.equal(result.Accounts[1].RootSignerWeight, 1);
  assert.equal(result.HasChain, true);
  assert.equal(fixture.writes(), 0);
});

test('unauthorized child fails closed', async () => {
  await assert.rejects(start({ childAuthorized: false }).inspect(), /root-signing authorization/);
});

test('chain cycles fail closed', async () => {
  await assert.rejects(start({ cycle: true }).inspect(), /Cycle/);
});

test('network mismatch fails closed before account traversal', async () => {
  await assert.rejects(start({ rightNetwork: false }).inspect(), /network identity/);
});

test('secret key input is checksum-validated by the Stellar SDK', async () => {
  await assert.rejects(start().inspect('S' + 'A'.repeat(55)), /correct checksum/);
});

test('neither host silently substitutes a secret for an HD mnemonic', () => {
  const home = fs.readFileSync(path.join(root, 'src/StellarVault.UI/Components/VaultHome.razor'), 'utf8');
  const native = fs.readFileSync(path.join(root, 'src/StellarVault.UI/Components/NativeVaultWorkspace.razor'), 'utf8');
  const core = fs.readFileSync(path.join(root, 'src/StellarVault.Core/Services/VaultCredentialMode.cs'), 'utf8');
  const pwa = fs.readFileSync(path.join(root, 'src/StellarVault.Pwa/wwwroot/index.html'), 'utf8');
  const maui = fs.readFileSync(path.join(root, 'src/StellarVault.Maui/wwwroot/index.html'), 'utf8');
  const csproj = fs.readFileSync(path.join(root, 'src/StellarVault.Maui/StellarVault.Maui.csproj'), 'utf8');
  assert.match(core, /MnemonicHd/);
  assert.match(core, /StellarSecretKeyChain/);
  assert.match(home, /SelectCredentialMode\(VaultCredentialMode.StellarSecretKeyChain\)/);
  assert.match(native, /SelectLedgerCredentialMode\(VaultCredentialMode.StellarSecretKeyChain\)/);
  assert.match(home, /SecretChain.InspectAsync/);
  assert.match(native, /SecretChain.InspectAsync/);
  assert.match(home, /_credentialMode == VaultCredentialMode.StellarSecretKeyChain/);
  assert.match(native, /_ledgerCredentialMode == VaultCredentialMode.MnemonicHd/);
  assert.match(pwa, /js\/vault-secret-chain.js/);
  assert.match(maui, /js\/vault-secret-chain.js/);
  assert.match(csproj, /vault-secret-chain.js/);
});
test('newly funded root with zero Stellar thresholds can be recovered read-only', async () => {
  const fixture = start({ linked: false });
  fixture.records[rootId].thresholds = {
    low_threshold: 0, med_threshold: 0, high_threshold: 0
  };
  const inspected = await fixture.inspect();
  assert.equal(inspected.RootAccountId, rootId);
  assert.equal(inspected.Accounts.length, 1);
  assert.match(inspected.Note, /Read-only recovery/);
  assert.equal(fixture.writes(), 0);
});

test('child accounts with zero signing thresholds remain blocked', async () => {
  const fixture = start();
  fixture.records[childId].thresholds.med_threshold = 0;
  await assert.rejects(fixture.inspect(), /unsafe zero signer thresholds/);
});

test('an HD-v2 registered root cannot be mistaken for an independent S-key chain', async () => {
  const fixture = start({ linked: false });
  fixture.records[rootId].data_attr['sv:registry:v2'] = btoa('registry');
  await assert.rejects(fixture.inspect(), /HD-v2 vault/);
});

test('an additional active child signer blocks single-secret chain recovery',async()=>{
  const fixture=start();
  fixture.records[childId].signers.push({
    type:'ed25519_public_key',key:'G'+'D'.repeat(55),weight:1
  });
  await assert.rejects(fixture.inspect(),/additional active signer/);
});
