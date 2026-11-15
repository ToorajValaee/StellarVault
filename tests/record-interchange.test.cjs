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

test('encrypted record backup stays client-side and requires explicit password', () => {
  const panel = read('src/StellarVault.UI/Components/VaultRecordTransferPanel.razor');
  const backup = read('src/StellarVault.Core/Services/VaultEncryptedRecordBackup.cs');
  const smoke = read('tests/StellarVault.Core.CryptoTests/VaultRecordInterchangeSmoke.cs');
  assert.match(panel, /VaultEncryptedRecordBackup\.EncryptAsync/);
  assert.match(panel, /VaultEncryptedRecordBackup\.DecryptAsync/);
  assert.match(panel, /_backupPassword\.Length < 12/);
  assert.match(panel, /"stellar-vault-records\.svrb"/);
  assert.match(backup, /new Argon2id\(password\)/);
  assert.match(backup, /MemoryKiB = 65536/);
  assert.match(backup, /VaultCryptoPrimitives\.Seal\(key, plaintext, header\)/);
  assert.match(backup, /VaultCryptoPrimitives\.Open\(key,/);
  assert.match(backup, /CryptographicOperations\.ZeroMemory\(plaintext\)/);
  assert.match(smoke, /RejectEncryptedAsync\(firstBackup, "the wrong backup password"\)/);
  assert.doesNotMatch(panel, /\.PublishAsync\(|\.SubmitAsync\(|\.QuoteCreateAsync\(/);
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

test('import previews are bound to the same unlocked account and network', () => {
  const panel = read('src/StellarVault.UI/Components/VaultRecordTransferPanel.razor');
  const core = read('src/StellarVault.Core/Services/VaultRecordInterchange.cs');
  assert.match(panel, /private string\? CurrentImportScope\(\)/);
  assert.match(panel, /Networks\.CredentialKey\(root\)/);
  assert.match(panel, /Networks\.CredentialKey\(account\)/);
  assert.match(panel, /HdSession\.Epoch/);
  assert.match(panel, /LinkedLedger\.ConnectionEpoch/);
  assert.match(panel, /initialScope != CurrentImportScope\(\)/);
  assert.match(panel, /approvedScope != CurrentImportScope\(\)/);
  assert.match(panel, /DiscardPending\(\)/);
  assert.match(core, /createdRecordIds\.Add\(newId\)/);
});


test('record export uses fresh account/network pinned ledger reads, not cached UI records', () => {
  const panel = read('src/StellarVault.UI/Components/VaultRecordTransferPanel.razor');
  assert.match(panel, /private async Task<\(VaultRecordInterchange\.Archive Archive, string Scope\)>/);
  assert.match(panel, /await LinkedLedger\.ReadAsync\(\)/);
  assert.match(panel, /await HdLedger\.ReadAsync\(\)/);
  assert.match(panel, /await HdLedger\.InspectAccountInfoAsync\(\)/);
  assert.match(panel, /LinkedLedger\.ConnectionEpoch != epoch/);
  assert.match(panel, /Strategy != requestedStrategy/);
  assert.match(panel, /await RequireSameExportScopeAsync\(exportScope\)/);
  assert.doesNotMatch(panel, /VaultRecordInterchange\.FromPublished\(Records\)/,
    'Never export secrets from the view\'s stale record cache.');
});

test('linked offline import preview cannot survive a lock and re-unlock on same account', () => {
  const keys = read('src/StellarVault.Core/Services/VaultLinkedQueueKeySession.cs');
  const panel = read('src/StellarVault.UI/Components/VaultRecordTransferPanel.razor');
  const smoke = read('tests/StellarVault.Core.CryptoTests/LinkedDraftSmoke.cs');
  assert.match(keys, /public long UnlockEpoch => Interlocked\.Read\(ref _unlockEpoch\)/);
  assert.match(keys, /Interlocked\.Increment\(ref _unlockEpoch\)/);
  assert.match(panel, /LinkedKeys\.UnlockEpoch\.ToString\(/);
  assert.match(smoke, /Offline queue re-unlock reused a stale local authorization generation/);
});
