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
  assert.match(provider, /Linked-account fees require the existing approval-quote lifecycle adapter/);
  assert.match(provider, /await hdLedger\.EstimateAsync\(draft\)/);
  assert.match(provider, /await hdLedger\.InspectAccountInfoAsync\(\)/);
  assert.match(provider, /quoted\.CreatesChildNode/);
  assert.match(provider, /quoted\.PeakReserveXlm/);
  assert.match(provider, /quoted\.NetworkFeeXlm/);
  assert.match(provider, /InvariantCulture/);
  assert.match(provider, /advisory estimate/);
  assert.doesNotMatch(provider, /await linkedLedger\.QuoteCreateAsync/,
    'Do not create a signer-backed quote without lifecycle cancellation and approval binding');
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

test('linked draft workspace independently invalidates in-flight offline work after re-unlock', () => {
  const workspace = read('src/StellarVault.Core/Services/VaultLinkedDraftWorkspace.cs');
  const keys = read('src/StellarVault.Core/Services/VaultLinkedQueueKeySession.cs');
  assert.match(workspace, /private sealed record Context\(string Root, string Scope, long Epoch,\s*long UnlockEpoch\)/);
  assert.match(workspace, /ledger\.ConnectionEpoch, keys\.UnlockEpoch\)/);
  assert.match(workspace, /keys\.UnlockEpoch != original\.UnlockEpoch/);
  assert.match(keys, /Interlocked\.Increment\(ref _unlockEpoch\)/);
});

test('file Put uses existing encrypted SVTREE3 staging for HD and independent S-key', () => {
  const provider = read('src/StellarVault.Core/Services/StellarAccountObjectStoreProvider.cs');
  const hd = read('src/StellarVault.Core/Services/VaultHybridDraftWorkspace.cs');
  const linked = read('src/StellarVault.Core/Services/VaultLinkedDraftWorkspace.cs');
  assert.match(provider, /descriptor\.TypeId == VaultRecordTypeFactory\.File/);
  assert.match(provider, /await linkedDrafts\.StageTreeFileAsync\(/);
  assert.match(provider, /await hdDrafts\.StageTreeFileAsync\(/);
  assert.match(provider, /await RequireSameScopeAsync\(scope, checkpoint, false\);/);
  assert.match(provider, /"staged-tree:" \+ result\.RootId/);
  assert.match(provider, /ObjectId: result\.RootId/);
  assert.match(provider, /result\.Manifest\.Length/);
  assert.match(provider, /null, null, result\.Manifest\.Length, null/);
  assert.match(hd, /VaultStreamingFileTree\.WriteContentAddressedAsync\(/);
  assert.match(linked, /VaultStreamingFileTree\.WriteAsync\(/);
  assert.match(provider, /recordTypes\.Create\(VaultRecordTypeFactory\.File/);
  assert.doesNotMatch(provider, /PublishDetailedAsync\(|SubmitApprovedAsync\(/,
    'Engine file staging must not skip consented publication and receipt checks');
});

test('neutral Get/OpenRead verifies SVTREE3 before exposing any bytes', () => {
  const provider = read('src/StellarVault.Core/Services/StellarAccountObjectStoreProvider.cs');
  const smoke = read('tests/StellarVault.Core.CryptoTests/StellarObjectEstimateSmoke.cs');
  assert.match(provider, /VaultLinkedTreeFilePolicy\.ReadAsync\(objectId, records, buffer, ct\)/);
  assert.match(provider, /await RequireSameScopeAsync\(scope, checkpoint, true\)/);
  assert.match(provider, /private sealed class VerifiedFileBuffer/);
  assert.match(provider, /MaxVerifiedInMemoryFileBytes = 8 \* 1024 \* 1024/);
  assert.match(provider, /MaxVerifiedInMemoryFileBytes - Length/);
  assert.match(provider, /CryptographicOperations\.ZeroMemory\(segment\.Array\)/);
  assert.match(provider, /if \(file is not null\) return file\.Value\.Bytes/);
  assert.match(provider, /return null; \/\/ Existing ordinary credential\/folder reader owns this record/);
  assert.match(smoke, /await fileEngine\.OpenReadAsync\(scope, rootRecord\.Id\)/);
  assert.match(smoke, /Engine returned a corrupted file as verified plaintext/);
  assert.doesNotMatch(provider, /\.PublishDetailedAsync\(|\.SubmitApprovedAsync\(/);
});
