'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '..');
const read = p => fs.readFileSync(path.join(root, p), 'utf8');

test('first engine provider reuses existing Stellar ledger readers and encrypted drafts', () => {
  const provider = read('src/StellarVault.Core/Services/StellarAccountObjectStoreProvider.cs');
  assert.match(provider, /: IAccountObjectStoreProvider/);
  assert.match(provider, /await linkedDrafts\.CreateAsync\(draft, ct\)/);
  assert.match(provider, /await hdDrafts\.CreateAsync\(draft, ct\)/);
  assert.match(provider, /await linkedLedger\.ReadAsync\(ct\)/);
  assert.match(provider, /await hdLedger\.ReadAsync\(\)/);
  assert.match(provider, /ObjectMutationState\.Prepared/);
  assert.match(provider, /new ObjectStorageQuote\(ProviderId, scope\.NetworkId, "XLM",\s*null/);
  assert.doesNotMatch(provider, /SubmitApprovedAsync\(|PublishDetailedAsync\(|PublishAsync\(/,
    'Provider creation must never bypass existing manual signing and fee review');
});

test('Stellar provider fails closed when capacity and file policies are not wired', () => {
  const provider = read('src/StellarVault.Core/Services/StellarAccountObjectStoreProvider.cs');
  assert.match(provider, /options\.CapacityPolicy != ObjectCapacityPolicy\.ExpandWithExplicitApproval/);
  assert.match(provider, /Single-account capacity enforcement is not yet wired/);
  assert.match(provider, /ObjectId='new'/);
  assert.match(provider, /Large or binary objects require the verified streaming file provider/);
  assert.match(provider, /Revision-pinned updates need the existing remote-mutation journal adapter/);
  assert.match(provider, /Deletion requires an authenticated, revision-pinned remote mutation journal/);
  assert.match(provider, /Object quotes must use the existing Stellar fee\/reserve planner/);
});

test('Stellar adapter binds tenant-free engine scope to selected wallet, network, and local queue', () => {
  const provider = read('src/StellarVault.Core/Services/StellarAccountObjectStoreProvider.cs');
  assert.match(provider, /StellarAccountAddress\.Validate\(scope\.StoreId\)/);
  assert.match(provider, /scope\.NetworkId != networks\.Network/);
  assert.match(provider, /networks\.CredentialKey\(scope\.StoreId\)/);
  assert.match(provider, /linkedLedger\.ConnectedNetworkPassphrase != requiredPassphrase/);
  assert.match(provider, /linkedKeys\.ActiveRootAccountId != scope\.StoreId/);
  assert.match(provider, /linkedKeys\.UnlockEpoch/);
  assert.match(provider, /hdSessions\.ActiveAccountId != scope\.StoreId/);
  assert.match(provider, /hdSessions\.Epoch/);
  assert.match(provider, /await RequireSameScopeAsync\(scope, checkpoint, false\)/);
});

test('PWA and native host register the same engine-facing interface and provider', () => {
  for (const p of ['src/StellarVault.Pwa/Program.cs', 'src/StellarVault.Maui/MauiProgram.cs']) {
    const content = read(p);
    assert.match(content, /AddScoped<StellarAccountObjectStoreProvider>\(\)/);
    assert.match(content, /AddScoped<IAccountObjectStoreProvider>/);
    assert.match(content, /AddScoped<IAccountObjectStore>/);
    assert.match(content, /new AccountObjectStoreRouter/);
  }
});

test('provider refuses unversioned type loss and clears ordinary stream bytes on disposal', () => {
  const provider = read('src/StellarVault.Core/Services/StellarAccountObjectStoreProvider.cs');
  assert.match(provider, /descriptor\.TypeId is not/);
  assert.match(provider, /VaultRecordTypeFactory\.Credential or VaultRecordTypeFactory\.Folder/);
  assert.match(provider, /descriptor\.ContentType is not null/);
  assert.match(provider, /current Stellar ordinary-record protocol does not persist/);
  assert.match(provider, /private sealed class ZeroingReadStream/);
  assert.match(provider, /CryptographicOperations\.ZeroMemory\(buffer\)/);
  assert.match(provider, /new ZeroingReadStream\(Encoding\.UTF8\.GetBytes\(record\.Content\)\)/);
});
