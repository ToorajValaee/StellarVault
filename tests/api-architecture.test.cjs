const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '..');
const read = p => fs.readFileSync(path.join(root, p), 'utf8');

test('Stellar Horizon integration depends on the engine without consumer or signing dependencies', () => {
  const integration = read('src/VaultEngine.Stellar.Horizon/VaultEngine.Stellar.Horizon.csproj');
  assert.match(integration, /VaultEngine.Core.csproj/);
  assert.doesNotMatch(integration, /StellarVault\.|PackageReference|FrameworkReference|<Compile/);
  const api = read('src/StellarVault.Api/StellarVault.Api.csproj');
  assert.match(api, /VaultEngine.Core.csproj/);
  assert.match(api, /VaultEngine.Stellar.Horizon.csproj/);
  for (const removed of ['StellarVault.Core', 'StellarVault.Application', 'StellarVault.Infrastructure']) {
    assert.ok(!fs.existsSync(path.join(root, 'src', removed)), `${removed} must not be recreated`);
    assert.doesNotMatch(api, new RegExp(removed.replaceAll('.', '\\.')));
  }
});

test('PWA and native use the same provider-owned Horizon inspectors', () => {
  for (const host of ['StellarVault.Pwa', 'StellarVault.Maui']) {
    assert.match(read(`src/${host}/${host}.csproj`), /VaultEngine.Stellar.Horizon.csproj/);
  }
  for (const inspector of ['HorizonVaultNetworkInspector', 'HorizonVaultTransactionInspector']) {
    assert.ok(fs.existsSync(path.join(root, `src/VaultEngine.Stellar.Horizon/Inspectors/${inspector}.cs`)));
    assert.ok(!fs.existsSync(path.join(root, `src/StellarVault.Pwa/Services/${inspector}.cs`)));
  }
});

test('engine and Stellar provider are independent from consumers', () => {
  assert.doesNotMatch(read('src/VaultEngine.Core/VaultEngine.Core.csproj'), /ProjectReference|PackageReference|FrameworkReference/);
  const provider = read('src/VaultEngine.Stellar/VaultEngine.Stellar.csproj');
  assert.match(provider, /VaultEngine.Core.csproj/);
  assert.doesNotMatch(provider, /StellarVault\.|<Compile/);
  for (const host of ['StellarVault.Pwa', 'StellarVault.Maui', 'StellarVault.UI']) {
    const project = read(`src/${host}/${host}.csproj`);
    assert.match(project, /VaultEngine.Core.csproj/);
    assert.match(project, /VaultEngine.Stellar.csproj/);
  }
});

test('HTTP observations execute through the same provider-neutral engine assembly', () => {
  const api = read('src/StellarVault.Api/Program.cs');
  assert.match(api, /ITransactionObservation inspect/);
  assert.match(api, /new TransactionObservationRouter/);
  assert.match(api, /inspect\.InspectAsync\(new TransactionObservationScope/);
  assert.doesNotMatch(api, /inspect\.ExecuteAsync/);
  const adapter = read('src/VaultEngine.Stellar.Horizon/StellarTransactionObservationProvider.cs');
  assert.match(adapter, /ITransactionObservationProvider/);
  assert.match(adapter, /inspect\.ExecuteAsync/);
});

test('tenant authentication and grants remain owned by the HTTP host', () => {
  const auth = read('src/StellarVault.Api/Security/ApiTenantDirectory.cs');
  assert.match(auth, /namespace StellarVault.Api.Security/);
  assert.match(auth, /NetworkClaim/);
  assert.match(read('src/StellarVault.Api/Program.cs'), /RequireAuthorization\(\)/);
  function inspect(folder) {
    for (const entry of fs.readdirSync(folder, { withFileTypes: true })) {
      if (entry.name === 'obj' || entry.name === 'bin') continue;
      const target = path.join(folder, entry.name);
      if (entry.isDirectory()) inspect(target);
      else if (entry.name.endsWith('.cs')) {
        assert.doesNotMatch(fs.readFileSync(target, 'utf8'), /ApiTenantProfile|AuthenticationHandler|Microsoft\.AspNetCore|ClaimsPrincipal/);
      }
    }
  }
  inspect(path.join(root, 'src/VaultEngine.Stellar.Horizon'));
});
