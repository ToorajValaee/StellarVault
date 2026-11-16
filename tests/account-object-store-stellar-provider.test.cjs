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
  assert.match(provider, /await linkedDrafts\.CreateAsync\(draft, options\.CapacityPolicy, ct\)/);
  assert.match(provider, /await hdDrafts\.CreateAsync\(draft, options\.CapacityPolicy, ct\)/);
  assert.match(provider, /await linkedLedger\.ReadAsync\(ct\)/);
  assert.match(provider, /await hdLedger\.ReadAsync\(\)/);
  assert.match(provider, /ObjectMutationState\.Prepared/);
  assert.match(provider, /new ObjectStorageQuote\(ProviderId, scope\.NetworkId, "XLM",\s*null/);
  assert.doesNotMatch(provider, /SubmitApprovedAsync\(|PublishDetailedAsync\(|PublishAsync\(/,
    'Provider creation must never bypass existing manual signing and fee review');
});

test('Stellar provider validates capacity mode and keeps unsupported mutation paths fail closed', () => {
  const provider = read('src/StellarVault.Core/Services/StellarAccountObjectStoreProvider.cs');
  assert.match(provider, /if \(!Enum\.IsDefined\(options\.CapacityPolicy\)\)/);
  assert.match(provider, /options\.CapacityPolicy, ct\)/);
  assert.match(provider, /ObjectId='new'/);
  assert.match(provider, /Large or binary objects require the verified streaming file provider/);
  assert.match(provider, /Revision-pinned updates need the existing remote-mutation journal adapter/);
  assert.match(provider, /await hdRemoteMutations\.StageDeleteAsync\(/);
  assert.match(provider, /await linkedDrafts\.StageDeleteAsync\(/);
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
  assert.match(provider, /IAccountObjectStoreProvider, IAccountObjectUpdateProvider, IVerifiedObjectExportProvider/);
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
  assert.match(provider, /await hdRemoteMutations\.StageUpdateAsync\(/);
  assert.match(provider, /expectedRevision\["hd-sha256:"\.Length\.\.\]/);
  assert.match(hd, /string expectedBaselineSha256/);
  assert.match(hd, /!string\.Equals\(baseline, expectedBaselineSha256, StringComparison\.Ordinal\)/);
  assert.match(provider, /await linkedDrafts\.StageUpdateAsync\(/);
  assert.match(provider, /VaultHybridRemoteMutationWorkspace\.Fingerprint\(current\)/);
  assert.match(provider, /VaultLinkedRemoteRevisionPolicy\.IsValid\(published\)/);
  assert.match(provider, /VaultLinkedRemoteRevisionPolicy\.IsValid\(/);
  assert.match(provider, /"hd-sha256:"/);
  assert.match(provider, /"linked-manifest:"/);
  assert.match(provider, /ObjectMutationState\.Prepared/);
  assert.match(provider, /Unknown requested account capacity policy/);
  assert.match(provider, /Updates require explicit complete structured fields/);
  assert.match(provider, /fields\?\.Tags\?\.ToArray\(\)/);
  assert.match(provider, /UserName: fields\?\.UserName/);
  assert.match(provider, /Url: fields\?\.Url/);
  assert.match(provider, /Notes: fields\?\.Notes/);
  assert.match(provider, /new ObjectRecordFields\(record\.UserName, record\.Url/);
  assert.match(contracts, /public sealed record ObjectRecordFields/);
  assert.match(hd, /public async Task<VaultPendingMutation> StageUpdateAsync/);
  assert.match(linked, /public Task<DraftItem> StageUpdateAsync/);
  assert.doesNotMatch(provider, /PublishWithPreparedHashAsync\(|PublishDetailedAsync\(|SubmitApprovedAsync\(/);
  for (const p of ['src/StellarVault.Pwa/Program.cs', 'src/StellarVault.Maui/MauiProgram.cs']) {
    assert.match(read(p), /AddScoped<IAccountObjectUpdate>/);
  }
});

test('explicit ordinary capacity policy survives encrypted queue and blocks HD expansion before publish', () => {
  const contracts = read('src/StellarVault.Core/Abstractions/AccountObjectStore.cs');
  const provider = read('src/StellarVault.Core/Services/StellarAccountObjectStoreProvider.cs');
  const queue = read('src/StellarVault.Core/Services/VaultPendingMutationQueue.cs');
  const hdDraft = read('src/StellarVault.Core/Services/VaultHybridDraftWorkspace.cs');
  const linkedDraft = read('src/StellarVault.Core/Services/VaultLinkedDraftWorkspace.cs');
  const review = read('src/StellarVault.Core/Services/VaultHybridSyncReviewService.cs');
  const publisher = read('src/StellarVault.Core/Services/VaultManualSyncCoordinator.cs');
  const guard = read('src/StellarVault.Core/Services/VaultCapacityPolicyGuard.cs');
  const approval = read('src/StellarVault.Core/Services/VaultSyncApprovalGuard.cs');
  const linkedPublisher = read('src/StellarVault.Core/Services/VaultLinkedDraftPublication.cs');
  const program = read('tests/StellarVault.Core.CryptoTests/Program.cs');
  assert.match(contracts, /enum ObjectCapacityPolicy/);
  assert.match(queue, /ObjectCapacityPolicy CapacityPolicy = ObjectCapacityPolicy\.SingleAccount/);
  assert.match(queue, /JsonRequired/);
  assert.match(queue, /string OriginStrategyId = VaultMutationStrategyPolicy\.Hd/);
  assert.match(queue, /ObjectCapacityPolicy CapacityPolicy = ObjectCapacityPolicy\.SingleAccount/);
  const origin = read('src/StellarVault.Core/Services/VaultMutationStrategyPolicy.cs');
  assert.match(origin, /Hd => Hd/);
  assert.doesNotMatch(origin, /null or Hd => Hd/);
  assert.match(origin, /Encrypted mutation requires an explicit recognized account strategy/);
  assert.match(hdDraft, /CapacityPolicy = capacityPolicy/);
  assert.match(linkedDraft, /CapacityPolicy = capacityPolicy/);
  assert.match(provider, /hdDrafts\.CreateAsync\(draft, options\.CapacityPolicy, ct\)/);
  assert.match(provider, /linkedDrafts\.CreateAsync\(draft, options\.CapacityPolicy, ct\)/);
  assert.match(review, /VaultCapacityPolicyGuard\.AllowsCreate/);
  assert.match(publisher, /VaultCapacityPolicyGuard\.AllowsCreate/);
  assert.match(publisher, /VaultCapacityPolicyGuard\.RequireCreateAllowed/);
  assert.match(approval, /writer\.Write\(\(int\)item\.CapacityPolicy\)/);
  assert.match(linkedPublisher, /Unknown linked mutation capacity policy/);
  assert.match(guard, /policy != ObjectCapacityPolicy\.SingleAccount/);
  assert.match(program, /VaultCapacityPolicySmoke\.RunAsync\(\)/);
  assert.doesNotMatch(provider, /\.PublishWithPreparedHashAsync\(|\.SubmitApprovedAsync\(/);
});

test('all SVTREE3 nodes and revision-pinned edits carry mandatory policy to reviewed publish paths', () => {
  const provider = read('src/StellarVault.Core/Services/StellarAccountObjectStoreProvider.cs');
  const hdFiles = read('src/StellarVault.Core/Services/VaultHybridDraftWorkspace.cs');
  const linkedFiles = read('src/StellarVault.Core/Services/VaultLinkedDraftWorkspace.cs');
  const hdEdits = read('src/StellarVault.Core/Services/VaultHybridRemoteMutationWorkspace.cs');
  const review = read('src/StellarVault.Core/Services/VaultHybridSyncReviewService.cs');
  const publisher = read('src/StellarVault.Core/Services/VaultManualSyncCoordinator.cs');
  assert.match(provider, /StageFileAsync\(scope, descriptor, source, checkpoint,/);
  assert.match(provider, /source, descriptor\.ParentId, ct, capacityPolicy/);
  assert.match(hdFiles, /with \{ CapacityPolicy = capacityPolicy \}/);
  assert.match(linkedFiles, /CapacityPolicy = capacityPolicy/);
  assert.match(provider, /expectedRevision\["hd-sha256:"\.Length\.\.\],\s*options\.CapacityPolicy, ct/);
  assert.match(provider, /expectedRevision\["linked-manifest:"\.Length\.\.\],\s*options\.CapacityPolicy, ct/);
  assert.match(hdEdits, /expectedBaselineSha256, capacityPolicy/);
  assert.match(hdEdits, /with \{ CapacityPolicy = capacityPolicy \}/);
  assert.match(linkedFiles, /with \{ OriginStrategyId = VaultMutationStrategyPolicy\.Linked,\s*CapacityPolicy = capacityPolicy \}/);
  assert.match(review, /mutation\.Kind is VaultMutationKind\.UpdateRecord/);
  assert.match(publisher, /VaultCapacityPolicyGuard\.RequireCreateAllowed/);
});

test('neutral Delete requires original remote revision and stages only in existing encrypted queues', () => {
  const provider = read('src/StellarVault.Core/Services/StellarAccountObjectStoreProvider.cs');
  const hd = read('src/StellarVault.Core/Services/VaultHybridRemoteMutationWorkspace.cs');
  const linked = read('src/StellarVault.Core/Services/VaultLinkedDraftWorkspace.cs');
  const router = read('src/StellarVault.Core/Abstractions/AccountObjectStore.cs');
  assert.match(router, /provider\.PrepareDeleteAsync\(scope, objectId, expectedRevision, ct\)/);
  assert.match(provider, /public async Task<ObjectMutationIntent> PrepareDeleteAsync\(/);
  assert.match(provider, /ReadOrdinaryAsync\(scope, objectId, ct\)/);
  assert.match(provider, /linked-manifest:/);
  assert.match(provider, /hd-sha256:/);
  assert.match(provider, /VaultLinkedRemoteRevisionPolicy\.IsValid\(expectedRevision\[prefix\.Length\.\.\]\)/);
  assert.match(provider, /hdRemoteMutations\.StageDeleteAsync\(\s*objectId, expectedRevision\[prefix\.Length\.\.\], ct\)/);
  assert.match(provider, /linkedDrafts\.StageDeleteAsync\(\s*objectId, expectedRevision\[prefix\.Length\.\.\], ct\)/);
  assert.match(provider, /hdSessions\.Epoch != epoch/);
  assert.match(provider, /linkedKeys\.UnlockEpoch != epoch/);
  assert.match(provider, /ObjectMutationState\.Prepared/);
  assert.match(hd, /StageDeleteAsync\(\s*string recordId, string expectedBaselineSha256,/);
  assert.match(hd, /StageAsync\(recordId, null, delete: true, ct,\s*expectedBaselineSha256\)/);
  assert.match(hd, /!string\.Equals\(baseline, expectedBaselineSha256, StringComparison\.Ordinal\)/);
  assert.match(linked, /beforeRevision != expectedRevision/);
  assert.match(linked, /currentRevision != expectedRevision/);
  assert.doesNotMatch(provider, /PublishWithPreparedHashAsync\(|PublishDetailedAsync\(|SubmitApprovedAsync\(/);
});

test('file metadata Get verifies a complete tree without storing plaintext in the 8 MiB read buffer', () => {
  const provider = read('src/StellarVault.Core/Services/StellarAccountObjectStoreProvider.cs');
  const source = read('src/StellarVault.Core/Services/VaultStreamingFileTree.cs');
  assert.match(provider, /private async Task<VaultStreamingFileTree.Root\?> ReadVerifiedTreeMetadataAsync\(/);
  assert.match(provider, /VaultLinkedTreeFilePolicy.ReadAsync\(\s*objectId, records, Stream.Null, ct\)/);
  assert.match(provider, /await RequireSameScopeAsync\(scope, checkpoint, true\);\s*ct.ThrowIfCancellationRequested\(\);\s*return manifest;/);
  assert.match(provider, /var file = await ReadVerifiedTreeMetadataAsync\(scope, objectId, ct\)/);
  assert.match(provider, /if \(file is not null\) return file.Value.Bytes/);
  assert.match(provider, /private const int MaxVerifiedInMemoryFileBytes = 8 \* 1024 \* 1024/);
  assert.match(provider, /public override void Write\(ReadOnlySpan<byte> buffer\)/);
  assert.match(provider, /public override void WriteByte\(byte value\)/);
  assert.match(provider, /public override void SetLength\(long value\)/);
  assert.match(provider, /public override Task WriteAsync\(byte\[\] buffer, int offset, int count,/);
  assert.match(provider, /public override ValueTask WriteAsync\(/);
  assert.match(source, /IncrementalHash.CreateHash\(HashAlgorithmName.SHA256\)/);
  assert.match(source, /temporaryDestination.WriteAsync\(leaf.Data, ct\)/);
});

test('verified SVTREE3 node inventories are chain-neutral read-only and never approve a deletion', () => {
  const contract = read('src/StellarVault.Core/Abstractions/AccountObjectStore.cs');
  const provider = read('src/StellarVault.Core/Services/StellarAccountObjectStoreProvider.cs');
  const reader = read('src/StellarVault.Core/Services/VaultLinkedTreeFilePolicy.cs');
  const smoke = read('tests/StellarVault.Core.CryptoTests/HdStreamingFileTreeSmoke.cs');
  assert.match(contract, /interface IVerifiedObjectNodeInventory/);
  assert.match(contract, /interface IVerifiedObjectNodeInventoryProvider/);
  assert.match(contract, /provider is IVerifiedObjectNodeInventoryProvider inspector/);
  assert.match(contract, /interface IVerifiedObjectNodeInventoryProvider[\s\S]*?InspectNodesAsync/);
  assert.match(provider, /IVerifiedObjectNodeInventoryProvider/);
  assert.match(provider, /VaultLinkedTreeFilePolicy.InspectVerifiedAsync\(\s*objectId, records, ct\)/);
  assert.match(provider, /await RequireSameScopeAsync\(scope, checkpoint, true\);\s*ct.ThrowIfCancellationRequested\(\);\s*return new VerifiedObjectNodeInventory/);
  assert.match(reader, /actualGroup != groupTag/);
  assert.match(reader, /ReadVerifiedAsync\(rootId,/);
  assert.match(reader, /Stream.Null, ids, ct/);
  assert.match(reader, /Another file root claims this streaming file group/);
  assert.match(smoke, /Cross-file SVTREE3 node ownership was accepted/);
  assert.match(smoke, /Ambiguous shared file group became a deletion inventory/);
  for (const host of [
    'src/StellarVault.Pwa/Program.cs',
    'src/StellarVault.Maui/MauiProgram.cs'
  ]) {
    assert.match(read(host), /AddScoped<IVerifiedObjectNodeInventory>/);
  }
  assert.doesNotMatch(reader, /PublishDetailedAsync|SubmitApprovedAsync|DeleteIfUnchanged/);
});

test('recovery inspection exposes every durable signed checkpoint but never confirms it', () => {
  const queue = read('src/StellarVault.Core/Services/VaultPendingMutationQueue.cs');
  assert.match(queue, /public sealed record VaultRecoveryCheckpoint/);
  assert.match(queue, /SignedDeletionManifests \?\? \[\]/);
  assert.match(queue, /SignedFileEntries \?\? \[\]/);
  assert.match(queue, /StellarAccountAddress.IsValid\(manifest.SourceAccountId\)/);
  assert.match(queue, /file.LedgerRecordId is not \{ Length: 16 \} id/);
  assert.match(queue, /!IsTransactionHash\(tx\)/);
  assert.match(queue, /item.TransactionHash is null \|\| hasHash/);
  assert.match(queue, /item.Kind is VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder/);
  assert.match(queue, /item.Kind == VaultMutationKind.UploadFile/);
  assert.match(queue, /if \(!valid\)\s*checkpoints.Clear\(\)/);
  assert.match(queue, /new VaultRecoveryInspection\(item.Id, item.State,/);
  assert.match(queue, /return entries.Where\(x => x.State is VaultSyncState.Submitting or VaultSyncState.Uncertain\)/);
  assert.match(queue, /return DescribeRecovery\(item\)/);
  assert.match(queue, /Never resubmit automatically/);
  assert.match(queue, /if \(!await verifier.IsConfirmedAsync\(current, transactionHash, ct\)\)/);
  assert.doesNotMatch(queue, /CheckpointJournalValid\)\s*await .*ConfirmReconciledAsync/s);
});

test('interrupted HD and linked file staging cleans up unacknowledged encrypted nodes safely', () => {
  const hd = read('src/StellarVault.Core/Services/VaultHybridDraftWorkspace.cs');
  const linked = read('src/StellarVault.Core/Services/VaultLinkedDraftWorkspace.cs');
  for (const workspace of [hd, linked]) {
    assert.match(workspace, /private static bool IsPendingTreeGroupNode\(/);
    assert.match(workspace, /VaultLinkedTreeFilePolicy\.Prefix/);
    assert.match(workspace, /svtree3-group:/);
    assert.match(workspace, /VaultSyncState\.Pending/);
    assert.match(workspace, /CryptographicOperations\.ZeroMemory/);
  }
  assert.match(hd, /var snapshot = await queue\.ReviewAllAsync\(/);
  assert.match(hd, /discovered\.Concat\(\s*stagedMutationIds\.AsEnumerable\(\)\.Reverse\(\)\)/);
  assert.match(hd, /foreach \(var entry in local\)\s*CryptographicOperations\.ZeroMemory\(entry\.EncryptedPayload\)/);
  assert.match(linked, /!IsPendingTreeGroupNode\(entry, groupId\)/);
  assert.match(linked, /entry\.SignedFileEntries is \{ Length: > 0 \}/);
  assert.match(linked, /!await store\.TryDeleteAsync\(entry, CancellationToken\.None\)/);
  assert.match(linked, /finally \{ ClearEntries\(entries\); \}/);
});

test('prelaunch HD records have no legacy local ID migration or silent version upgrades', () => {
  const queue = read('src/StellarVault.Core/Services/VaultPendingMutationQueue.cs');
  const pwa = read('src/StellarVault.Pwa/Pages/Vault.razor');
  const origin = read('src/StellarVault.Core/Services/VaultMutationStrategyPolicy.cs');
  assert.doesNotMatch(queue, /MigrateLegacyCreateIdAsync/);
  assert.doesNotMatch(pwa, /MigrateSelectedLegacyIdAsync|MIGRATE LEGACY LOCAL ID/);
  assert.match(origin, /Encrypted mutation requires an explicit recognized account strategy/);
  assert.match(queue, /VaultMutationStrategyPolicy\.Validate\(entry\.OriginStrategyId\)/);
});

test('PWA crash-recovery UI displays every signed checkpoint without calling a submit path', () => {
  const pwa = read('src/StellarVault.Pwa/Pages/Vault.razor');
  assert.match(pwa, /recovery\.CheckpointJournalValid/);
  assert.match(pwa, /recovery\.Checkpoints/);
  assert.match(pwa, /checkpoint\.TargetId/);
  assert.match(pwa, /checkpoint\.TransactionHash/);
  assert.match(pwa, /NOT VERIFIED/);
  assert.match(pwa, /Do not retry, confirm or discard this submission/);
  assert.doesNotMatch(pwa, /MigrateSelectedLegacyIdAsync/);
});
