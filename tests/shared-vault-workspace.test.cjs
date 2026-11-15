'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.join(__dirname, '..', 'src');
function read(relative) { return fs.readFileSync(path.join(root, relative), 'utf8'); }

test('one canonical /vault route selects explicit authenticated HD or S-key strategy', () => {
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
  assert.match(vault, /<VaultLinkedWorkspaceShell \/>/);
  assert.match(vault, /ActiveMode == VaultWorkspaceMode\.ConflictingSessions/);
  assert.match(vault, /if \(ActiveMode != VaultWorkspaceMode\.MnemonicHd\) return;/);
  assert.match(index, /Navigation.NavigateTo\("\/vault", replace: true\)/);
  assert.doesNotMatch(index, /NavigateTo\("\/secret-vault"/);
  assert.match(legacy, /@page "\/secret-vault"/);
  assert.match(legacy, /Navigation.NavigateTo\(mode == VaultWorkspaceMode.ConflictingSessions/);
  assert.doesNotMatch(legacy, /<VaultSecretKeyWorkspace/);
  assert.match(home, /\? "\/initialize" : "\/vault"/);
  assert.match(init, /Nav\.NavigateTo\("\/vault"\)/);
});

test('shared app shell exposes same navigation and explorer for imported S-key', () => {
  const shell = read('StellarVault.Pwa/Shared/VaultLinkedWorkspaceShell.razor');
  const linked = read('StellarVault.UI/Components/VaultSecretKeyWorkspace.razor');
  const css = read('StellarVault.Pwa/wwwroot/css/modern-app.css');

  for (const word of ['Vault', 'Drafts', 'Sync', 'Settings', 'Documentation &amp; Help']) {
    assert.ok(shell.includes(word), 'missing shared navigation: ' + word);
  }
  assert.match(shell, /class="sv-pwa-app"/);
  assert.match(shell, /class="sv-pwa-rail"/);
  assert.match(shell, /class="sv-pwa-stage"/);
  assert.match(shell, /class="sv-pwa-mobile-tabs"/);
  assert.match(shell, /<VaultSecretKeyWorkspace Embedded="true"/);
  assert.match(shell, /OnViewChange="SelectView"/);
  assert.match(linked, /\[Parameter\] public bool Embedded/);
  assert.match(linked, /\[Parameter\] public string View/);
  assert.match(linked, /class="sv-linked-explorer-layout"/);
  assert.match(linked, /class="sv-explorer-tree terminal-panel"/);
  assert.match(linked, /hidden="@\(Embedded && View is not/);
  assert.match(linked, /<VaultLinkedLocalQueuePanel \/>/);
  assert.match(css, /\.sv-linked-unified-workspace \.sv-linked-explorer-layout/);
});

test('unified UI preserves independent S-key offline drafts and immutable file roots', () => {
  const vault = read('StellarVault.Pwa/Pages/Vault.razor');
  const linked = read('StellarVault.UI/Components/VaultSecretKeyWorkspace.razor');
  const shell = read('StellarVault.Pwa/Shared/VaultLinkedWorkspaceShell.razor');
  const smoke = fs.readFileSync(path.join(__dirname, 'StellarVault.Core.CryptoTests',
    'Program.cs'), 'utf8');

  assert.match(vault, /Recover independent encrypted S-key drafts offline/);
  assert.match(vault, /<VaultLinkedLocalQueuePanel \/>/);
  assert.match(linked, /!IsTreePart\(x\)/);
  assert.match(linked, /VaultLinkedTreeFilePolicy.IsRootRecord\(_selected\)/);
  assert.match(linked, /Open verified file downloads/);
  assert.match(shell, /_workspace\.LockFromShellAsync\(\)/);
  assert.match(linked, /await LinkedQueueKeys\.LockAsync\(\)/);
  assert.match(smoke, /VaultWorkspaceModeSmoke\.Run\(\)/);
});
