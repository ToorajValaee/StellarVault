'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.join(__dirname, '..');
const read = name => fs.readFileSync(path.join(root, name), 'utf8');

test('record import/export is accessible in PWA and both native/shared account modes', () => {
  const pwa = read('src/StellarVault.Pwa/Pages/Vault.razor');
  const native = read('src/StellarVault.UI/Components/NativeVaultWorkspace.razor');
  const linked = read('src/StellarVault.UI/Components/VaultSecretKeyWorkspace.razor');
  const panel = read('src/StellarVault.UI/Components/VaultRecordTransferPanel.razor');
  assert.match(pwa, /<VaultRecordTransferPanel Strategy="@VaultMutationStrategyPolicy\.Hd"/);
  assert.match(native, /<VaultRecordTransferPanel Strategy="@VaultMutationStrategyPolicy\.Hd"/);
  assert.match(linked, /<VaultRecordTransferPanel Strategy="@VaultMutationStrategyPolicy\.Linked"/);
  assert.match(panel, /VaultRecordInterchange\.ImportJson/);
  assert.match(panel, /VaultRecordInterchange\.ImportCsv/);
  assert.match(panel, /VaultRecordInterchange\.ExportJson/);
  assert.match(panel, /VaultRecordInterchange\.ExportCsv/);
  assert.match(panel, /_understood/);
  assert.match(panel, /!CanStage/);
  assert.match(panel, /HdDrafts\.CreateAsync/);
  assert.match(panel, /LinkedDrafts\.CreateAsync/);
  assert.doesNotMatch(panel, /\.PublishAsync\(|\.SubmitAsync\(|\.QuoteCreateAsync\(/,
    'Record interchange must never submit on-chain transactions');
});

test('record interchange is a bounded and versioned local-only Core operation', () => {
  const source = read('src/StellarVault.Core/Services/VaultRecordInterchange.cs');
  const program = read('tests/StellarVault.Core.CryptoTests/Program.cs');
  assert.match(source, /MaximumBytes = 1024 \* 1024/);
  assert.match(source, /MaximumRecords = 500/);
  assert.match(source, /public static async Task<int> StageAsync/);
  assert.match(source, /translated\[ready\.FolderId\]/);
  assert.match(source, /!IsOrdinaryContent\(item\.Content\)/);
  assert.match(program, /VaultRecordInterchangeSmoke\.RunAsync\(\)/);
});
