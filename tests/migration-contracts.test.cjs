const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');

const read = (path) => fs.readFileSync(path, 'utf8');

test('vault pages and approval modal use portable interfaces', () => {
  for (const path of ['src/StellarVault.UI/Pages/Index.razor', 'src/StellarVault.UI/Pages/Vault.razor', 'src/StellarVault.UI/Pages/Add.razor']) {
    const source = read(path);
    assert.match(source, /@inject IVaultOperations Session/);
    assert.match(source, /@inject IVaultClientServices Storage/);
    assert.doesNotMatch(source, /@inject (?:VaultSession|BrowserStorage)/);
  }
  assert.match(read('src/StellarVault.UI/Components/CostApprovalModal.razor'), /@inject IVaultClientServices Storage/);
  assert.match(read('src/StellarVault.UI/Layout/MainLayout.razor'), /@inject IVaultPreferences Storage/);
});

test('browser DI resolves portable interfaces from existing scoped services', () => {
  const program = read('Program.cs');
  for (const contract of ['IVaultOperations', 'IVaultSessionStatus', 'IVaultClientServices', 'IVaultPreferences', 'IVaultPlatformActions']) {
    assert.match(program, new RegExp('AddScoped<' + contract + '>'));
  }
});

test('native preview does not expose vault unlock or transaction controls', () => {
  const preview = read('src/StellarVault.Maui/AppRoot.razor');
  assert.match(preview, /NATIVE PREVIEW/);
  assert.doesNotMatch(preview, /@inject IVaultOperations|UnlockAsync\(|AddAsync\(|DeleteAsync\(/);
});

test('shared Razor pages are owned by the Razor class library', () => {
  for (const page of ['Index', 'Vault', 'Add']) {
    assert.equal(fs.existsSync('Pages/' + page + '.razor'), false);
    assert.equal(fs.existsSync('src/StellarVault.UI/Pages/' + page + '.razor'), true);
  }
  assert.equal(fs.existsSync('Layout/MainLayout.razor'), false);
  assert.equal(fs.existsSync('src/StellarVault.UI/Layout/MainLayout.razor'), true);
  assert.match(read('App.razor'), /AdditionalAssemblies/);
  assert.match(read('App.razor'), /StellarVault\.UI\.Pages\.Index/);
});

test('record details and ledger approvals live only in shared UI', () => {
  for (const component of ['RecordDetailModal', 'CostApprovalModal', 'CostEstimatePanel']) {
    assert.equal(fs.existsSync('Components/' + component + '.razor'), false);
    assert.equal(fs.existsSync('src/StellarVault.UI/Components/' + component + '.razor'), true);
  }
  assert.match(read('src/StellarVault.UI/Components/RecordDetailModal.razor'), /@inject IVaultOperations Session/);
  assert.match(read('src/StellarVault.UI/Components/RecordDetailModal.razor'), /@inject IVaultClientServices Storage/);
});
