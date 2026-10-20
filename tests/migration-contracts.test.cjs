const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');

const read = (path) => fs.readFileSync(path, 'utf8');

test('vault pages and approval modal use portable interfaces', () => {
  for (const path of ['Pages/Index.razor', 'Pages/Vault.razor', 'Pages/Add.razor']) {
    const source = read(path);
    assert.match(source, /@inject IVaultOperations Session/);
    assert.match(source, /@inject IVaultClientServices Storage/);
    assert.doesNotMatch(source, /@inject (?:VaultSession|BrowserStorage)/);
  }
  assert.match(read('src/StellarVault.UI/Components/CostApprovalModal.razor'), /@inject IVaultClientServices Storage/);
  assert.match(read('Layout/MainLayout.razor'), /@inject IVaultPreferences Storage/);
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
