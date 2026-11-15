'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.join(__dirname, '..', 'src');
const read = relative => fs.readFileSync(path.join(root, relative), 'utf8');

test('one canonical /vault route selects explicitly authenticated HD or S-key strategy', () => {
  const mode = read('StellarVault.Core/Services/VaultWorkspaceModeResolver.cs');
  const vault = read('StellarVault.Pwa/Pages/Vault.razor');
  const index = read('StellarVault.Pwa/Pages/Index.razor');
  const legacy = read('StellarVault.Pwa/Pages/SecretVault.razor');
  const home = read('StellarVault.UI/Components/VaultHome.razor');
  const init = read('StellarVault.Pwa/Pages/Initialize.razor');

  assert.match(mode, /enum VaultWorkspaceMode/);
  assert.match(mode, /ConflictingSessions/);
  assert.match(mode, /if \(secretKeyLedgerConnected && \(hdLocalUnlocked \|\| hdLedgerConnected\)\)/);
  assert.match(mode, /if \(secretKeyLedgerConnected\) return VaultWorkspaceMode.StellarSecretKey/);
  assert.match(vault, /@page "\/vault"/);
  assert.match(vault, /VaultWorkspaceModeResolver\.Resolve\(/);
  assert.match(vault, /ActiveMode == VaultWorkspaceMode\.StellarSecretKey/);
  assert.match(vault, /<VaultSecretKeyWorkspace Embedded="true"/);
  assert.doesNotMatch(vault, /<VaultLinkedWorkspaceShell/);
  assert.match(vault, /ActiveMode == VaultWorkspaceMode\.ConflictingSessions/);
  assert.match(vault, /@onclick="LockConflictingSessionsAsync"/);
  assert.match(vault, /await LinkedQueueKeys\.LockAsync\(\)/);
  assert.match(vault, /await SecretLedger\.DisconnectAsync\(\)/);
  assert.match(vault, /Workflow\.Lock\(\)/);
  assert.match(vault, /if \(ActiveMode != VaultWorkspaceMode\.MnemonicHd\) return;/);
  assert.match(index, /Navigation.NavigateTo\("\/vault", replace: true\)/);
  assert.doesNotMatch(index, /NavigateTo\("\/secret-vault"/);
  assert.match(legacy, /@page "\/secret-vault"/);
  assert.doesNotMatch(legacy, /<VaultSecretKeyWorkspace/);
  assert.match(home, /\? "\/initialize" : "\/vault"/);
  assert.match(init, /Nav\.NavigateTo\("\/vault"\)/);
});

test('both strategies reuse one PWA app chrome and two identical explorer components', () => {
  const vault = read('StellarVault.Pwa/Pages/Vault.razor');
  const linked = read('StellarVault.UI/Components/VaultSecretKeyWorkspace.razor');
  const tree = read('StellarVault.UI/Components/VaultExplorerTree.razor');
  const row = read('StellarVault.UI/Components/VaultExplorerItemRow.razor');
  const css = read('StellarVault.Pwa/wwwroot/css/modern-app.css');

  for (const word of ['Vault', 'Drafts', 'Sync', 'Settings', 'Documentation &amp; Help']) {
    assert.ok(vault.includes(word), 'missing shared navigation: ' + word);
  }
  assert.match(vault, /class="sv-pwa-app"/);
  assert.match(vault, /class="sv-pwa-rail"/);
  assert.match(vault, /class="sv-pwa-stage"/);
  assert.match(vault, /class="sv-pwa-mobile-tabs"/);
  assert.match(vault, /@key="WorkspaceRenderKey"/);
  assert.match(vault, /OnViewChange="SelectAppView"/);
  assert.match(vault, /<VaultExplorerTree Folders="@Folders"/);
  assert.match(linked, /<VaultExplorerTree Folders="@_items/);
  assert.match(vault, /<VaultExplorerItemRow Item="item"/);
  assert.match(linked, /<VaultExplorerItemRow Item="item"/);
  assert.match(tree, /class="sv-explorer-tree terminal-panel"/);
  assert.match(row, /class="sv-object-row/);
  assert.match(row, /VaultPublishedFileClassification.IsFile/);
  assert.match(css, /\.sv-linked-unified-workspace \.sv-linked-explorer-layout/);
});

test('HD and S-key render the same record form fields; only their submit logic differs', () => {
  const hd = read('StellarVault.Pwa/Pages/VaultItemEditor.razor');
  const linked = read('StellarVault.UI/Components/VaultSecretKeyWorkspace.razor');
  const fields = read('StellarVault.UI/Components/VaultRecordFields.razor');
  assert.match(hd, /<VaultRecordFields IsFolder=/);
  assert.match(linked, /<VaultRecordFields IsFolder=/);
  for (const field of ['Title', 'FolderId', 'Username', 'Url', 'Secret',
                      'Notes', 'Tags', 'ShowSecret']) {
    assert.match(hd, new RegExp('@bind-' + field + '='));
    assert.match(linked, new RegExp('@bind-' + field + '='));
    assert.match(fields, new RegExp(field + 'Changed'));
  }
  assert.match(hd, /Ledger\.EstimateAsync/);
  assert.match(linked, /WritePlanner\.PrepareAsync/);
  assert.match(linked, /LinkedExpansion\.SubmitApprovedAsync/);
  assert.doesNotMatch(fields, /QuoteCreateAsync|SubmitApprovedAsync|Ledger\.ReadAsync/);
});

test('shared UI retains independent S-key offline drafts and immutable file safeguards', () => {
  const vault = read('StellarVault.Pwa/Pages/Vault.razor');
  const linked = read('StellarVault.UI/Components/VaultSecretKeyWorkspace.razor');
  const policy = read('StellarVault.Core/Services/VaultPublishedFileClassification.cs');
  const native = read('StellarVault.UI/Components/NativeVaultWorkspace.razor');
  const runner = fs.readFileSync(path.join(__dirname, 'StellarVault.Core.CryptoTests',
    'Program.cs'), 'utf8');

  assert.match(vault, /Recover independent encrypted S-key drafts offline/);
  assert.match(vault, /<VaultLinkedLocalQueuePanel \/>/);
  assert.match(linked, /!IsTreePart\(x\)/);
  assert.match(linked, /VaultPublishedFileClassification.IsFile\(_selected\)/);
  assert.match(linked, /Open verified file downloads/);
  assert.match(policy, /StellarLedgerFileProtocol\.ChunkPrefix/);
  assert.match(policy, /VaultLinkedTreeFilePolicy.IsRootRecord/);
  assert.match(vault, /_linkedWorkspace\?\.LockFromShellAsync\(\)/);
  assert.match(linked, /await LinkedQueueKeys\.LockAsync\(\)/);
  assert.match(native, /<VaultSecretKeyWorkspace Embedded="true"/);
  assert.match(native, /OnViewChange="NavigateLinkedNative"/);
  assert.match(native, /NativeContentKey => SecretLedger.IsConnected/);
  assert.match(native, /<VaultRecordFields IsFolder=/);
  assert.match(runner, /VaultWorkspaceModeSmoke\.Run\(\)/);
});
