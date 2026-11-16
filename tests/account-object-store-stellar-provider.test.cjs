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
    assert.match(content, /AddScoped<IObjectMutationReview>/);
    assert.match(content, /AddScoped<IVerifiedObjectExport>/);
    assert.match(content, /GetRequiredService<IAccountObjectStore>\(\)/);
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

test('neutral verified export streams into private destination and only commits after full hash and scope checks', () => {
  const contract = read('src/StellarVault.Core/Abstractions/AccountObjectStore.cs');
  const provider = read('src/StellarVault.Core/Services/StellarAccountObjectStoreProvider.cs');
  const smoke = read('tests/StellarVault.Core.CryptoTests/StellarObjectEstimateSmoke.cs');
  assert.match(contract, /interface IVerifiedObjectDestination/);
  assert.match(contract, /interface IVerifiedObjectExport/);
  assert.match(contract, /interface IVerifiedObjectExportProvider/);
  assert.match(contract, /provider is IVerifiedObjectExportProvider verified/);
  assert.match(provider, /IAccountObjectStoreProvider, IVerifiedObjectExportProvider/);
  assert.match(provider, /await destination\.OpenTemporaryAsync\(ct\)/);
  assert.match(provider, /await VaultLinkedTreeFilePolicy\.ReadAsync\(\s*objectId, records, temporary, ct\)/);
  assert.match(provider, /await temporary\.FlushAsync\(ct\)/);
  assert.match(provider, /await RequireSameScopeAsync\(scope, checkpoint, true\);\s*ct\.ThrowIfCancellationRequested\(\);\s*await destination\.CommitAsync\(ct\)/);
  assert.match(provider, /await destination\.AbortAsync\(CancellationToken\.None\)/);
  assert.match(smoke, /verifiedDestination\.Commits == 1/);
  assert.match(smoke, /rejectedDestination\.Aborts == 1/);
});

test('trusted filesystem verified export destination is atomic, one-use and cleans up failures', () => {
  const destination = read('src/StellarVault.Core/Abstractions/AtomicFileObjectDestination.cs');
  const smoke = read('tests/StellarVault.Core.CryptoTests/AtomicFileObjectDestinationSmoke.cs');
  const program = read('tests/StellarVault.Core.CryptoTests/Program.cs');
  assert.match(destination, /: IVerifiedObjectDestination, IAsyncDisposable/);
  assert.match(destination, /FileMode\.CreateNew/);
  assert.match(destination, /FileShare\.None/);
  assert.match(destination, /Path\.IsPathFullyQualified\(finalPath\)/);
  assert.match(destination, /File\.Move\(_temporaryPath, _finalPath, overwrite: false\)/);
  assert.match(destination, /File\.Delete\(_temporaryPath\)/);
  assert.match(destination, /if \(_committed\) return Task\.CompletedTask/);
  assert.match(smoke, /Unverified plaintext became publicly visible/);
  assert.match(smoke, /A race replaced the existing final file/);
  assert.match(smoke, /Disposing an uncommitted destination did not clean up/);
  assert.match(program, /AtomicFileObjectDestinationSmoke\.RunAsync\(\)/);
});

test('neutral publication reviews reuse the existing HD and Secret Key publishers without a second submit path', () => {
  const contracts = read('src/StellarVault.Core/Abstractions/AccountObjectStore.cs');
  const provider = read('src/StellarVault.Core/Services/StellarAccountObjectStoreProvider.cs');
  const publisher = read('src/StellarVault.Core/Services/VaultLinkedDraftPublication.cs');
  assert.match(contracts, /interface IObjectMutationReview/);
  assert.match(contracts, /interface IObjectMutationReviewProvider/);
  assert.match(contracts, /provider is IObjectMutationReviewProvider reviewer/);
  assert.match(provider, /IObjectMutationReviewProvider/);
  assert.match(provider, /await hdReview\.ReviewAsync\(scope\.StoreId, true, ct\)/);
  assert.match(provider, /await linkedPublication\.PrepareAsync\(operationId, ct\)/);
  assert.match(provider, /await linkedPublication\.CancelAsync\(CancellationToken\.None\)/);
  assert.match(provider, /await linkedPublication\.CancelAsync\(ct\)/);
  assert.match(provider, /ObjectMutationState\.AwaitingApproval/);
  assert.match(provider, /Account expansion requires a separately authorized review/);
  assert.match(provider, /report\.Queue\.Ready\.SingleOrDefault/);
  assert.match(provider, /report\.Queue\.Conflicts != 0/);
  assert.doesNotMatch(provider, /PublishApprovedCreateAsync\(|SubmitApprovedAsync\(|PublishWithPreparedHashAsync\(/);
  assert.match(publisher, /long Epoch, long UnlockEpoch, byte\[\] Fingerprint/);
  assert.match(publisher, /keys\.UnlockEpoch/);
  assert.match(publisher, /current\.UnlockEpoch != unlockEpoch/);
  assert.match(publisher, /_pending = new\(item\.Id, root, scope, epoch, unlockEpoch/);
  assert.match(publisher, /pending\.UnlockEpoch/);
});

test('Edit is a revision-pinned Update routed to existing encrypted mutation journals', () => {
  const contracts = read('src/StellarVault.Core/Abstractions/AccountObjectStore.cs');
  const provider = read('src/StellarVault.Core/Services/StellarAccountObjectStoreProvider.cs');
  const hd = read('src/StellarVault.Core/Services/VaultHybridRemoteMutationWorkspace.cs');
  const linked = read('src/StellarVault.Core/Services/VaultLinkedDraftWorkspace.cs');
  assert.match(contracts, /interface IAccountObjectUpdate/);
  assert.match(contracts, /interface IAccountObjectUpdateProvider/);
  assert.match(contracts, /provider is IAccountObjectUpdateProvider updater/);
  assert.match(contracts, /options\.ExpectedRevision != expectedRevision/);
  assert.match(provider, /IAccountObjectUpdateProvider/);
  assert.match(provider, /await hdRemoteMutations\.StageUpdateAsync\(id, draft, ct\)/);
  assert.match(provider, /await linkedDrafts\.StageUpdateAsync\(/);
  assert.match(provider, /VaultHybridRemoteMutationWorkspace\.Fingerprint\(current\)/);
  assert.match(provider, /VaultLinkedRemoteRevisionPolicy\.IsValid\(published\)/);
  assert.match(provider, /VaultLinkedRemoteRevisionPolicy\.IsValid\(/);
  assert.match(provider, /"hd-sha256:"/);
  assert.match(provider, /"linked-manifest:"/);
  assert.match(provider, /ObjectMutationState\.Prepared/);
  assert.match(provider, /Single-account publication capacity policy is not yet enforced/);
  assert.match(provider, /Structured Stellar record fields require the full typed-record update contract/);
  assert.match(provider, /!string\.IsNullOrEmpty\(currentRecord\.UserName\)/);
  assert.match(provider, /!string\.IsNullOrEmpty\(currentRecord\.Url\)/);
  assert.match(provider, /!string\.IsNullOrEmpty\(currentRecord\.Notes\)/);
  assert.match(provider, /currentRecord\.Tags is \{ Length: > 0 \}/);
  assert.match(hd, /public async Task<VaultPendingMutation> StageUpdateAsync/);
  assert.match(linked, /public Task<DraftItem> StageUpdateAsync/);
  assert.doesNotMatch(provider, /PublishWithPreparedHashAsync\(|PublishDetailedAsync\(|SubmitApprovedAsync\(/);
  for (const p of ['src/StellarVault.Pwa/Program.cs', 'src/StellarVault.Maui/MauiProgram.cs']) {
    assert.match(read(p), /AddScoped<IAccountObjectUpdate>/);
  }
});
