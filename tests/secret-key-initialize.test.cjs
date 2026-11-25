'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const repo = path.join(__dirname, '..');
const home = fs.readFileSync(path.join(repo, 'src', 'StellarVault.UI',
  'Components', 'VaultHome.razor'), 'utf8');
const init = fs.readFileSync(path.join(repo, 'src', 'StellarVault.Pwa',
  'Pages', 'Initialize.razor'), 'utf8');

test('empty S-key root enters shared setup; existing S-key records open directly', () => {
  assert.match(home, /var recovered = await SecretLedger\.ReadAsync\(\)/);
  assert.match(home, /Navigation\.NavigateTo\(recovered\.Count == 0\s*\? "\/initialize" : "\/vault"\)/);
  assert.match(home, /SecretChain\.InspectAsync\(_secretKey, horizon, passphrase\)/);
  assert.match(home, /SecretLedger\.ConnectAsync\(_secretKey, horizon, passphrase\)/);
});

test('shared initialization page admits a connected independent S-key session', () => {
  assert.match(init, /@page "\/initialize"/);
  assert.match(init, /@inject IStellarSecretKeyLedger SecretLedger/);
  assert.match(init, /ActiveMode == VaultWorkspaceMode\.Locked/);
  assert.match(init, /ActiveMode == VaultWorkspaceMode\.ConflictingSessions/);
  assert.match(init, /else if \(SecretLedger\.IsConnected\)/);
  assert.match(init, /await SecretLedger\.InspectAccountInfoAsync\(\)/);
  assert.match(init, /SecretLedger\.ConnectionEpoch == epoch/);
  assert.match(init, /SecretLedger\.RootAccountId == root/);
  assert.match(init, /_linkedInfo\.SpendableNative/);
  assert.match(init, /@onclick="RefreshFundingAsync"/);
  assert.match(init, /@onclick="ContinueLinkedAsync"/);
  assert.match(init, /Nav\.NavigateTo\("\/vault"\)/);
});

test('S-key setup never invokes HD registry writes or calls a connected key an HD account', () => {
  const secretMarkup = init.slice(init.indexOf('else if (SecretLedger.IsConnected)'),
    init.indexOf('else if (_ready)'));
  assert.match(secretMarkup, /not an HD recovery-phrase registry/);
  assert.match(secretMarkup, /does not submit a transaction/);
  assert.doesNotMatch(secretMarkup, /InitializeRegistryAsync|@onclick="InitializeAsync"/);
  assert.match(init, /if \(SecretLedger\.IsConnected\)\s*\{\s*await InspectLinkedAsync\(\);\s*return;/);
  // Registry initialization remains available solely behind the HD ledger branch.
  assert.match(init, /if \(!await Ledger\.HasRegistryAsync\(\)\)\s*await Ledger\.InitializeRegistryAsync\(\)/);
  assert.match(init, /ActiveMode != VaultWorkspaceMode\.MnemonicHd \|\| !Ledger\.IsConnected\) return;/);
  assert.match(init, /ActiveMode == VaultWorkspaceMode\.ConflictingSessions/);
});
