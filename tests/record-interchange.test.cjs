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
  assert.match(pwa, /<VaultRecordTransferPanel @key="ActiveMode" Mode="import"/);
  assert.match(pwa, /VaultMutationStrategyPolicy\.Linked : VaultMutationStrategyPolicy\.Hd/);
  assert.match(native, /<VaultRecordTransferPanel Mode="import" Strategy="@VaultMutationStrategyPolicy\.Hd"/);
  assert.match(linked, /<VaultRecordTransferPanel Mode="import" Strategy="@VaultMutationStrategyPolicy\.Linked"/);
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
  assert.match(panel, /initialScope != CurrentPreviewScope\(\)/);
  assert.match(panel, /_pendingScope != CurrentImportScope\(\)/);
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

test('PWA file picker accepts max-size authenticated SVRB overhead but no larger file', () => {
  const js = read('src/StellarVault.Pwa/wwwroot/js/vault-v2-download.js');
  const adapter = read('src/StellarVault.Pwa/Services/BrowserVaultNativeFileAccess.cs');
  const backup = read('src/StellarVault.Core/Services/VaultEncryptedRecordBackup.cs');
  assert.match(js, /maxPortableRecordBackupBytes = 1024 \* 1024 \+ 5 \+ 16 \+ 12 \+ 16/);
  assert.match(js, /file\.size > maxPortableRecordBackupBytes/);
  assert.match(adapter, /VaultEncryptedRecordBackup\.MaximumBackupBytes/);
  assert.match(backup, /MaximumBackupBytes = VaultRecordInterchange\.MaximumBytes \+/);
});

test('browser export destination is chosen before work and cannot be reused', async () => {
  const vm = require('node:vm');
  const code = read('src/StellarVault.Pwa/wwwroot/js/vault-v2-download.js');
  const activity = [];
  let bytesWritten = 0;
  const target = {
    async abort() { activity.push('abort'); }
  };
  const handle = {
    async createWritable() {
      activity.push('createWritable');
      return target;
    }
  };
  const window = {
    async showSaveFilePicker() {
      activity.push('showSaveFilePicker');
      return handle;
    }
  };
  vm.runInNewContext(code, { window, document: {}, URL: {}, setTimeout });
  const saver = await window.stellarVaultV2Download.selectSaveDestination('records.svrb');
  assert.deepEqual(activity, ['showSaveFilePicker'],
    'Selecting a destination must not start a file write');
  // Simulate awaited ledger verification and Argon2id work between
  // selecting a destination and committing the prepared bytes.
  await Promise.resolve();
  await saver.writeStream({
    async stream() {
      return {
        async pipeTo(sink) {
          assert.equal(sink, target);
          bytesWritten = 12;
          activity.push('pipeTo');
        }
      };
    }
  });
  assert.equal(bytesWritten, 12);
  assert.deepEqual(activity, ['showSaveFilePicker', 'createWritable', 'pipeTo']);
  await assert.rejects(saver.writeStream({ stream: async () => {} }),
    /already used/);
});

test('record exports select browser destination before any asynchronous ledger read', () => {
  const panel = read('src/StellarVault.UI/Components/VaultRecordTransferPanel.razor');
  const adapter = read('src/StellarVault.Pwa/Services/BrowserVaultNativeFileAccess.cs');
  const contract = read('src/StellarVault.Core/Services/IVaultNativeFileAccess.cs');
  const js = read('src/StellarVault.Pwa/wwwroot/js/vault-v2-download.js');
  assert.match(contract, /interface IVaultDeferredFileSaveAccess/);
  assert.match(contract, /interface IVaultPendingFileSave/);
  assert.match(adapter, /SelectSaveDestinationAsync\(string fileName\)/);
  assert.match(adapter, /stellarVaultV2Download\.selectSaveDestination/);
  assert.match(js, /async selectSaveDestination\(filename\)/);
  assert.match(panel, /browserDestination = await SelectBrowserDestinationAsync\(\s*"stellar-vault-records\.svrb"\);\s*var \(archive, exportScope\) = await LoadExportSnapshotAsync\(\)/);
  assert.match(panel, /browserDestination = await SelectBrowserDestinationAsync\(filename\);\s*var \(archive, exportScope\) = await LoadExportSnapshotAsync\(\)/);
  assert.match(panel, /await RequireSameExportScopeAsync\(exportScope\);/);
  assert.match(panel, /await browserDestination\.SaveAsync\(stream\)/);
});

test('PWA imports open from left rail, not record header, for both account strategies', () => {
  const pwa = read('src/StellarVault.Pwa/Pages/Vault.razor');
  const native = read('src/StellarVault.UI/Components/NativeVaultWorkspace.razor');
  const linked = read('src/StellarVault.UI/Components/VaultSecretKeyWorkspace.razor');
  const row = read('src/StellarVault.UI/Components/VaultExplorerItemRow.razor');
  const inspector = read('src/StellarVault.UI/Components/VaultRecordInspector.razor');
  const panel = read('src/StellarVault.UI/Components/VaultRecordTransferPanel.razor');
  const railStart = pwa.indexOf('<nav class="sv-pwa-rail"');
  const railEnd = pwa.indexOf('</nav>',railStart);
  const importAction = pwa.indexOf('sv-rail-import-action');
  assert.ok(railStart >= 0 && importAction > railStart && importAction < railEnd,
    'Import must be a left-navigation action, not a main vault header action');
  assert.doesNotMatch(pwa, /sv-vault-import-menu/);
  assert.match(pwa, /@onclick="OpenImportDialog"/);
  assert.match(pwa, /role="dialog" aria-modal="true" aria-label="Import records"/);
  assert.match(pwa, /Mode="import"/);
  assert.match(pwa, /VaultMutationStrategyPolicy\.Linked : VaultMutationStrategyPolicy\.Hd/);
  assert.match(native, /Mode="import"/);
  assert.match(linked, /@if \(!Embedded\)[\s\S]*?Mode="import"/);
  assert.match(row, /class="sv-row-export-action"/);
  assert.match(row, /@onclick="\(\) => OnOpen\.InvokeAsync\(Item\)">Export…/);
  assert.match(inspector, /Mode="export" Strategy="Strategy" Records="Records" SelectedRecordId="@Item.Id"/);
  assert.match(panel, /if \(Mode == "import"\)/);
  assert.match(panel, /Mode == "export" && SelectedRecordId is not null/);
  assert.match(panel, /selected\.Length != 1 \|\| !VaultRecordInterchange\.IsOrdinary/);
  assert.match(panel, /selected\[0\] with \{ FolderId = null \}/);
  assert.match(panel, /await RequireSameExportScopeAsync\(exportScope\)/);
});

test('record transfer uses themed import sections and a real export action dialog', () => {
  const panel = read('src/StellarVault.UI/Components/VaultRecordTransferPanel.razor');
  const inspector = read('src/StellarVault.UI/Components/VaultRecordInspector.razor');
  const css = read('src/StellarVault.Pwa/wwwroot/css/modern-app.css');
  const pwa = read('src/StellarVault.Pwa/Pages/Vault.razor');
  assert.match(panel, /class="sv-transfer-content sv-transfer-import"/);
  assert.match(panel, /class="sv-transfer-option"/);
  assert.match(panel, /class="pixel-button compact sv-transfer-export-trigger"/);
  assert.match(panel, /aria-haspopup="dialog" aria-expanded="@_showExportDialog"/);
  assert.match(panel, /class="modal-backdrop sv-transfer-export-backdrop"/);
  assert.match(panel, /role="dialog"\s+aria-modal="true" aria-label="Export record"/);
  assert.match(panel, /@onclick="CloseExportDialog"/);
  assert.match(panel, /@onclick="ExportJsonAsync"/);
  assert.match(panel, /@onclick="ExportEncryptedAsync"/);
  assert.doesNotMatch(inspector, /<summary>Export this item<\/summary>/);
  assert.equal((inspector.match(/<VaultRecordTransferPanel Mode="export"/g) || []).length, 1);
  assert.match(css, /\.sv-transfer-option-heading/);
  assert.match(css, /\.sv-transfer-export-trigger/);
  assert.match(css, /\.sv-vault-import-dialog/);
  assert.match(pwa, /sv-rail-import-action/);
});

test('export consent and backup password enable action without relying on stale record cache', () => {
  const panel = read('src/StellarVault.UI/Components/VaultRecordTransferPanel.razor');
  const inspector = read('src/StellarVault.UI/Components/VaultRecordInspector.razor');
  const row = read('src/StellarVault.UI/Components/VaultExplorerItemRow.razor');
  const css = read('src/StellarVault.Pwa/wwwroot/css/modern-app.css');
  assert.match(panel, /private bool CanExport => SelectedRecordId is \{ Length: > 0 \}/);
  assert.doesNotMatch(panel, /Records\.Count > 0 &&/);
  assert.match(panel, /@bind="_backupPassword" @bind:event="oninput"/);
  assert.match(panel, /private bool CanPreviewImport => CanStage \|\|/);
  assert.match(panel, /private string\? CurrentPreviewScope\(\)/);
  assert.match(panel, /if \(_busy \|\| !_understood \|\| !CanPreviewImport\) return/);
  assert.match(panel, /if \(_pendingScope is null \|\| !CanStage \|\|/);
  assert.match(inspector, /Verify file integrity/);
  assert.match(row, /sv-row-export-action/);
  assert.doesNotMatch(row, /VaultRecordTransferPanel Mode="export"/);
  assert.match(css, /sv-transfer-export-backdrop/);
  assert.match(css, /sv-record-inspector-backdrop:has\(\.sv-transfer-export-backdrop\)/);
});

test('access-page storage options stay compact and required content remains reachable', () => {
  const access = read('src/StellarVault.UI/Components/VaultHome.razor');
  const css = read('src/StellarVault.Pwa/wwwroot/css/modern-app.css');
  assert.match(access, /class="sv-storage-optional"/);
  assert.match(access, /<span>Stellar only<\/span>/);
  assert.match(access, /<span>Encrypted on this device<\/span>/);
  assert.match(access, /<span>Enable device unlock<\/span>/);
  assert.match(access, /sv-device-storage-info/);
  assert.match(css, /\.sv-entry-storage-outside \.sv-access-storage-choices \{/);
  assert.match(css, /grid-template-columns:repeat\(2,minmax\(0,1fr\)\)/);
  assert.match(css, /@media \(min-width:1021px\) and \(max-height:780px\)/);
});
