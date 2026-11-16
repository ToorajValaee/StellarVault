using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VaultEngine.Core;

namespace StellarVault.Core.Services;

/// <summary>
/// First real provider seam for the chain-neutral object engine. Reuses the
/// existing authenticated Stellar ledger readers and encrypted local draft
/// queues. Only ordinary UTF-8 records/folders are supported in this phase.
/// File uploads reuse the existing encrypted SVTREE3 queue stage operations.
/// Network fee quoting for files, published reads, revision-pinned edits/deletes
/// and single-account capacity enforcement remain fail-closed.
/// </summary>
public sealed class StellarAccountObjectStoreProvider(
    IStellarLedgerRecords hdLedger,
    IStellarSecretKeyLedger linkedLedger,
    VaultHybridDraftWorkspace hdDrafts,
    VaultLinkedDraftWorkspace linkedDrafts,
    VaultSessionManager hdSessions,
    VaultLinkedQueueKeySession linkedKeys,
    VaultNetworkSelection networks,
    IVaultRecordTypeFactory recordTypes,
    VaultHybridRemoteMutationWorkspace? hdRemoteMutations = null,
    VaultHybridSyncReviewService? hdReview = null,
    VaultLinkedDraftPublication? linkedPublication = null)
    : IAccountObjectStoreProvider, IAccountObjectUpdateProvider, IVerifiedObjectExportProvider, IObjectMutationReviewProvider
{
    public string ProviderId => "stellar";
    private const int MaxOrdinaryContentBytes = 64 * 1024;

    private sealed record ScopeCheckpoint(
        string Root, string Network, string Strategy, long Epoch);

    private ScopeCheckpoint RequireScope(ObjectStoreScope scope, bool requireLedger)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.ProviderId != ProviderId ||
            scope.AccountStrategyId is not
                (VaultMutationStrategyPolicy.Hd or VaultMutationStrategyPolicy.Linked))
            throw new NotSupportedException("Unsupported Stellar object storage strategy.");
        StellarAccountAddress.Validate(scope.StoreId);
        var network = networks.CredentialKey(scope.StoreId);
        if (scope.NetworkId != networks.Network)
            throw new UnauthorizedAccessException(
                "The selected Stellar network does not match the object store.");
        if (scope.AccountStrategyId == VaultMutationStrategyPolicy.Linked)
        {
            if (requireLedger && (!linkedLedger.IsConnected ||
                linkedLedger.RootAccountId != scope.StoreId))
                throw new UnauthorizedAccessException("The requested linked signer is unavailable.");
            if (linkedLedger.IsConnected)
            {
                if (linkedLedger.RootAccountId != scope.StoreId)
                    throw new UnauthorizedAccessException("A different linked signer is connected.");
                var requiredPassphrase = networks.Network switch
                {
                    "testnet" => "Test SDF Network ; September 2015",
                    "public" => "Public Global Stellar Network ; September 2015",
                    "custom" => networks.CustomPassphrase,
                    _ => null
                };
                if (requiredPassphrase is null ||
                    linkedLedger.ConnectedNetworkPassphrase != requiredPassphrase)
                    throw new UnauthorizedAccessException(
                        "The connected linked signer uses a different Stellar network.");
            }
            if (!requireLedger && (!linkedKeys.IsUnlocked ||
                linkedKeys.ActiveRootAccountId != scope.StoreId))
                throw new UnauthorizedAccessException(
                    "Unlock the encrypted local queue for this linked account.");
            return new(scope.StoreId, network, scope.AccountStrategyId,
                requireLedger ? linkedLedger.ConnectionEpoch : linkedKeys.UnlockEpoch);
        }
        if (requireLedger && !hdLedger.IsConnected)
            throw new UnauthorizedAccessException("The requested HD ledger is unavailable.");
        if (!requireLedger && (!hdSessions.IsUnlocked ||
            hdSessions.ActiveAccountId != scope.StoreId))
            throw new UnauthorizedAccessException(
                "Unlock the encrypted local HD queue for the requested account.");
        return new(scope.StoreId, network, scope.AccountStrategyId,
            requireLedger ? 0 : hdSessions.Epoch);
    }

    private async Task RequireSameScopeAsync(ObjectStoreScope scope, ScopeCheckpoint before,
        bool requireLedger)
    {
        var now = RequireScope(scope, requireLedger);
        if (now != before)
            throw new InvalidOperationException("Stellar account, network or signer changed.");
        if (scope.AccountStrategyId == VaultMutationStrategyPolicy.Hd && requireLedger)
        {
            var account = await hdLedger.InspectAccountInfoAsync();
            if (account.RootAccountId != scope.StoreId ||
                RequireScope(scope, true) != before)
                throw new InvalidOperationException("HD ledger root changed.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("Readable object stream required.", nameof(source));
        using var data = new MemoryStream();
        byte[] buffer = new byte[8192];
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                if (data.Length + read > MaxOrdinaryContentBytes)
                    throw new NotSupportedException(
                        "Large or binary objects require the verified streaming file provider.");
                await data.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            return data.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (data.TryGetBuffer(out var segment) && segment.Array is not null)
                CryptographicOperations.ZeroMemory(segment.Array);
        }
    }

    private LedgerRecordDraft BuildDraft(ObjectDescriptor descriptor, string text)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        // The current on-chain ordinary-record layout does not retain an
        // independent logical type discriminator. Until the versioned typed
        // format is integrated, accepting "text" or custom types here would
        // silently read them back as "credential".
        if (descriptor.TypeId is not
            (VaultRecordTypeFactory.Credential or VaultRecordTypeFactory.Folder))
            throw new NotSupportedException(
                "This Stellar provider phase supports only credentials and folders; typed text/files require a versioned record discriminator.");
        if (descriptor.ContentType is not null)
            throw new NotSupportedException(
                "The current Stellar ordinary-record protocol does not persist an independent MIME type.");
        recordTypes.Create(descriptor.TypeId, descriptor.Name, descriptor.ContentType);
        if (descriptor.ParentId is { } parent &&
            (parent.Length != 16 || !parent.All(Uri.IsHexDigit)))
            throw new ArgumentException("Invalid Stellar parent folder ID.");
        if (text.Length == 0 || !VaultRecordInterchange.IsOrdinaryContent(text))
            throw new InvalidDataException("Empty or reserved file-protocol content is not an ordinary record.");
        return new LedgerRecordDraft(descriptor.Name, text, descriptor.ParentId,
            IsFolder: descriptor.TypeId == VaultRecordTypeFactory.Folder);
    }

    private static void RequireSupportedCreate(ObjectWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ExpectedRevision is not null)
            throw new NotSupportedException("Revision-pinned updates need the existing remote-mutation journal adapter.");
        if (options.CapacityPolicy != ObjectCapacityPolicy.ExpandWithExplicitApproval)
            throw new NotSupportedException(
                "Single-account capacity enforcement is not yet wired into the encrypted publisher.");
    }

    public async Task<ObjectStorageQuote> EstimatePutAsync(
        ObjectStoreScope scope, ObjectDescriptor descriptor, Stream source,
        ObjectWriteOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (descriptor is null || descriptor.ObjectId != "new" ||
            options.ExpectedRevision is not null)
            throw new NotSupportedException(
                "This quote path supports only new ordinary records; updates require revision-pinned review.");
        if (scope.AccountStrategyId != VaultMutationStrategyPolicy.Hd)
            throw new NotSupportedException(
                "Linked-account fees require the existing approval-quote lifecycle adapter.");
        var checkpoint = RequireScope(scope, true);
        // The HD ledger has a supported read-only estimator. Do not fabricate
        // a linked quote or force a streaming file through an ordinary record.
        byte[] bytes = await ReadBoundedAsync(source, ct);
        try
        {
            var content = new UTF8Encoding(false, true).GetString(bytes);
            var draft = BuildDraft(descriptor, content);
            var before = await hdLedger.InspectAccountInfoAsync();
            if (before.RootAccountId != scope.StoreId)
                throw new UnauthorizedAccessException(
                    "The connected HD ledger root differs from the requested object store.");
            await RequireSameScopeAsync(scope, checkpoint, true);
            ct.ThrowIfCancellationRequested();
            var quoted = await hdLedger.EstimateAsync(draft);
            ct.ThrowIfCancellationRequested();
            await RequireSameScopeAsync(scope, checkpoint, true);
            // This is only an advisory estimate: the approved publisher must
            // obtain a fresh binding quote immediately before any broadcast.
            return new ObjectStorageQuote(
                ProviderId, scope.NetworkId, "XLM",
                quoted.NetworkFeeXlm.ToString("0.0000000",
                    System.Globalization.CultureInfo.InvariantCulture),
                quoted.PeakReserveXlm.ToString("0.0000000",
                    System.Globalization.CultureInfo.InvariantCulture),
                bytes.Length, quoted.CreatesChildNode);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>
    /// Reuse the deployed encrypted file-tree draft workflows. Each chunk and
    /// manifest is queued locally by the strategy-specific implementation.
    /// The root ID is an object locator, not proof that any node was published.
    /// Incomplete staging is rolled back by the existing draft workspace.
    /// </summary>
    private async Task<ObjectMutationIntent> StageFileAsync(
        ObjectStoreScope scope, ObjectDescriptor descriptor, Stream source,
        ScopeCheckpoint checkpoint, CancellationToken ct)
    {
        if (descriptor.ContentType is not { Length: > 0 })
            throw new ArgumentException("File media type is required.", nameof(descriptor));
        recordTypes.Create(VaultRecordTypeFactory.File,
            descriptor.Name, descriptor.ContentType, descriptor.Name);
        if (descriptor.ParentId is { } parent &&
            (parent.Length != 16 || !parent.All(Uri.IsHexDigit)))
            throw new ArgumentException("Invalid Stellar parent folder ID.", nameof(descriptor));
        if (!source.CanRead)
            throw new ArgumentException("Readable file stream is required.", nameof(source));
        await RequireSameScopeAsync(scope, checkpoint, false);
        var result = scope.AccountStrategyId == VaultMutationStrategyPolicy.Linked
            ? await linkedDrafts.StageTreeFileAsync(
                descriptor.Name, descriptor.Name, descriptor.ContentType,
                source, descriptor.ParentId, ct)
            : await hdDrafts.StageTreeFileAsync(
                descriptor.Name, descriptor.Name, descriptor.ContentType,
                source, descriptor.ParentId, ct);
        await RequireSameScopeAsync(scope, checkpoint, false);
        return new ObjectMutationIntent(
            "staged-tree:" + result.RootId,
            ObjectMutationState.Prepared,
            new ObjectStorageQuote(ProviderId, scope.NetworkId, "XLM",
                null, null, result.Manifest.Length, null),
            ObjectId: result.RootId);
    }

    public async Task<ObjectMutationIntent> PreparePutAsync(
        ObjectStoreScope scope, ObjectDescriptor descriptor, Stream source,
        ObjectWriteOptions options, CancellationToken ct = default)
    {
        RequireSupportedCreate(options);
        if (descriptor is null || descriptor.ObjectId != "new")
            throw new ArgumentException(
                "Create requests must use ObjectId='new'; the encrypted queue allocates the durable record ID.",
                nameof(descriptor));
        var checkpoint = RequireScope(scope, false);
        if (descriptor.TypeId == VaultRecordTypeFactory.File)
            return await StageFileAsync(scope, descriptor, source, checkpoint, ct);
        byte[] bytes = await ReadBoundedAsync(source, ct);
        try
        {
            string text = new UTF8Encoding(false, true).GetString(bytes);
            var draft = BuildDraft(descriptor, text);
            await RequireSameScopeAsync(scope, checkpoint, false);
            // Durable encrypted local mutation only, never a network write.
            string mutationId;
            string recordId;
            if (scope.AccountStrategyId == VaultMutationStrategyPolicy.Linked)
            {
                var queued = await linkedDrafts.CreateAsync(draft, ct);
                mutationId = queued.MutationId;
                recordId = queued.EntityId;
            }
            else
            {
                var queued = await hdDrafts.CreateAsync(draft, ct);
                mutationId = queued.Id;
                recordId = queued.EntityId;
            }
            await RequireSameScopeAsync(scope, checkpoint, false);
            return new ObjectMutationIntent(mutationId,
                ObjectMutationState.Prepared,
                new ObjectStorageQuote(ProviderId, scope.NetworkId, "XLM",
                    null, null, bytes.Length, null), null, recordId);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public async Task<ObjectMutationIntent> PrepareUpdateAsync(
        ObjectStoreScope scope, ObjectDescriptor descriptor,
        Stream source, string expectedRevision, ObjectWriteOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        if (options.ExpectedRevision is not null &&
            options.ExpectedRevision != expectedRevision)
            throw new ArgumentException("Conflicting update revision.", nameof(options));
        if (options.CapacityPolicy != ObjectCapacityPolicy.ExpandWithExplicitApproval)
            throw new NotSupportedException(
                "Single-account publication capacity policy is not yet enforced.");
        if (descriptor.ObjectId is not { Length: 16 } id ||
            !id.All(Uri.IsHexDigit))
            throw new ArgumentException("Update requires the existing 16-hex record ID.");
        if (descriptor.TypeId is not
            (VaultRecordTypeFactory.Credential or VaultRecordTypeFactory.Folder))
            throw new NotSupportedException(
                "Immutable file trees and custom types require a versioned update workflow.");
        // These are existing *on-chain* edit operations, not a local-only
        // overwrite and not an upsert. A valid remote version is mandatory.
        var checkpoint = RequireScope(scope, true);
        byte[] bytes = await ReadBoundedAsync(source, ct);
        try
        {
            var draft = BuildDraft(descriptor,
                new UTF8Encoding(false, true).GetString(bytes));
            // The neutral descriptor does not yet carry the legacy Stellar
            // structured fields. Fail closed rather than accidentally remove
            // username, URL, notes or tags during an otherwise valid Edit.
            var currentRecord = await ReadOrdinaryAsync(scope, id, ct)
                ?? throw new FileNotFoundException("Published record no longer exists.");
            if (currentRecord.IsFolder != draft.IsFolder ||
                !string.IsNullOrEmpty(currentRecord.UserName) ||
                !string.IsNullOrEmpty(currentRecord.Url) ||
                !string.IsNullOrEmpty(currentRecord.Notes) ||
                currentRecord.Tags is { Length: > 0 })
                throw new NotSupportedException(
                    "Structured Stellar record fields require the full typed-record update contract.");
            await RequireSameScopeAsync(scope, checkpoint, true);
            string mutationId;
            if (scope.AccountStrategyId == VaultMutationStrategyPolicy.Linked)
            {
                if (!expectedRevision.StartsWith("linked-manifest:", StringComparison.Ordinal) ||
                    !VaultLinkedRemoteRevisionPolicy.IsValid(
                        expectedRevision["linked-manifest:".Length..]))
                    throw new ArgumentException(
                        "Linked update requires a canonical remote manifest revision.");
                if (!linkedKeys.CanPublish || linkedKeys.ActiveRootAccountId != scope.StoreId)
                    throw new UnauthorizedAccessException(
                        "Unlock the matching linked signer and encrypted draft queue.");
                var epoch = linkedKeys.UnlockEpoch;
                var staged = await linkedDrafts.StageUpdateAsync(
                    id, draft, expectedRevision["linked-manifest:".Length..], ct);
                await RequireSameScopeAsync(scope, checkpoint, true);
                if (!linkedKeys.CanPublish || linkedKeys.UnlockEpoch != epoch)
                    throw new InvalidOperationException("Linked unlock changed during update staging.");
                mutationId = staged.MutationId;
            }
            else
            {
                if (!expectedRevision.StartsWith("hd-sha256:", StringComparison.Ordinal) ||
                    expectedRevision.Length != "hd-sha256:".Length + 64)
                    throw new ArgumentException(
                        "HD update requires its original published record fingerprint.");
                if (hdRemoteMutations is null)
                    throw new NotSupportedException("HD remote mutation workspace is not registered.");
                if (!hdSessions.IsUnlocked || hdSessions.ActiveAccountId != scope.StoreId)
                    throw new UnauthorizedAccessException("Unlock the matching HD encrypted queue.");
                var epoch = hdSessions.Epoch;
                // Compare the caller's original Get revision to a fresh
                // authoritative remote snapshot before staging the update.
                var current = await ReadOrdinaryAsync(scope, id, ct)
                    ?? throw new FileNotFoundException("Published record no longer exists.");
                if (current.IsFolder != draft.IsFolder ||
                    !string.Equals(expectedRevision,
                        "hd-sha256:" + VaultHybridRemoteMutationWorkspace.Fingerprint(current),
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Published record changed since Edit opened. Reload before saving.");
                await RequireSameScopeAsync(scope, checkpoint, true);
                var staged = await hdRemoteMutations.StageUpdateAsync(id, draft, ct);
                await RequireSameScopeAsync(scope, checkpoint, true);
                if (!hdSessions.IsUnlocked || hdSessions.Epoch != epoch)
                    throw new InvalidOperationException("HD unlock changed during update staging.");
                mutationId = staged.Id;
            }
            return new ObjectMutationIntent(
                mutationId, ObjectMutationState.Prepared,
                new ObjectStorageQuote(ProviderId, scope.NetworkId, "XLM",
                    null, null, bytes.LongLength, null),
                ObjectId: id);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task<LedgerRecordItem?> ReadOrdinaryAsync(
        ObjectStoreScope scope, string objectId, CancellationToken ct)
    {
        if (objectId.Length != 16 || !objectId.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid Stellar record ID.", nameof(objectId));
        var checkpoint = RequireScope(scope, true);
        IReadOnlyList<LedgerRecordItem> records;
        if (scope.AccountStrategyId == VaultMutationStrategyPolicy.Linked)
            records = await linkedLedger.ReadAsync(ct);
        else
        {
            var before = await hdLedger.InspectAccountInfoAsync();
            if (before.RootAccountId != scope.StoreId)
                throw new UnauthorizedAccessException("HD ledger root does not match object scope.");
            records = await hdLedger.ReadAsync();
        }
        await RequireSameScopeAsync(scope, checkpoint, true);
        var found = records.SingleOrDefault(x => x.Id == objectId);
        if (found is null) return null;
        if (!VaultRecordInterchange.IsOrdinary(found))
            throw new NotSupportedException(
                "Binary files must be opened through the existing verified file-tree reader.");
        return found;
    }

    // The neutral OpenReadAsync signature has no caller-provided private
    // destination. Bound its authenticated temporary buffer rather than
    // exposing streamed chunks before final SHA-256 verification. Large files
    // must use the existing private-destination file download API until the
    // engine supports caller-supplied verified output sinks.
    private const int MaxVerifiedInMemoryFileBytes = 8 * 1024 * 1024;

    private sealed class VerifiedFileBuffer : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (TryGetBuffer(out var segment) && segment.Array is not null)
                CryptographicOperations.ZeroMemory(segment.Array);
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length > MaxVerifiedInMemoryFileBytes - Length)
                throw new NotSupportedException(
                    "File exceeds the verified in-memory read limit; use the private-destination streaming downloader.");
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private async Task<(VaultStreamingFileTree.Root Manifest, VerifiedFileBuffer Bytes)?>
        LoadVerifiedTreeAsync(ObjectStoreScope scope, string objectId, CancellationToken ct)
    {
        var checkpoint = RequireScope(scope, true);
        if (objectId.Length != 16 || !objectId.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid Stellar file root ID.", nameof(objectId));
        IReadOnlyList<LedgerRecordItem> records;
        if (scope.AccountStrategyId == VaultMutationStrategyPolicy.Linked)
            records = await linkedLedger.ReadAsync(ct);
        else
        {
            var before = await hdLedger.InspectAccountInfoAsync();
            if (before.RootAccountId != scope.StoreId)
                throw new UnauthorizedAccessException("HD ledger root differs from file object scope.");
            records = await hdLedger.ReadAsync();
        }
        await RequireSameScopeAsync(scope, checkpoint, true);
        var matching = records.Where(x => x.Id == objectId).ToArray();
        if (matching.Length == 0) return null;
        if (matching.Length != 1)
            throw new InvalidDataException("Duplicate published object identifier.");
        if (!VaultLinkedTreeFilePolicy.IsRootRecord(matching[0]))
        {
            if (VaultPublishedFileClassification.IsFile(matching[0]) ||
                VaultPublishedFileClassification.IsInternalNode(matching[0]))
                throw new NotSupportedException(
                    "Legacy file and internal file-tree records require their original verified reader.");
            return null; // Existing ordinary credential/folder reader owns this record.
        }
        var buffer = new VerifiedFileBuffer();
        try
        {
            var root = await VaultLinkedTreeFilePolicy.ReadAsync(objectId, records, buffer, ct);
            await RequireSameScopeAsync(scope, checkpoint, true);
            ct.ThrowIfCancellationRequested();
            buffer.Position = 0;
            return (root, buffer);
        }
        catch
        {
            await buffer.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Large-file path: write into a caller-owned PRIVATE destination and
    /// commit only after every SVTREE3 node, the root content hash, and the
    /// active account/network scope have been verified. On any error, abort
    /// the temporary destination; never publish a partially verified file.
    /// </summary>
    public async Task<StoredObject> ExportVerifiedAsync(
        ObjectStoreScope scope, string objectId,
        IVerifiedObjectDestination destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        if (objectId.Length != 16 || !objectId.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid Stellar file root ID.", nameof(objectId));
        var checkpoint = RequireScope(scope, true);
        // The existing strategy-specific transport returns an authenticated
        // ledger snapshot; avoid mixing children from different read epochs.
        IReadOnlyList<LedgerRecordItem> records;
        if (scope.AccountStrategyId == VaultMutationStrategyPolicy.Linked)
            records = await linkedLedger.ReadAsync(ct);
        else
        {
            var before = await hdLedger.InspectAccountInfoAsync();
            if (before.RootAccountId != scope.StoreId)
                throw new UnauthorizedAccessException("HD file root belongs to another account.");
            records = await hdLedger.ReadAsync();
        }
        await RequireSameScopeAsync(scope, checkpoint, true);
        var roots = records.Where(x => x.Id == objectId).ToArray();
        if (roots.Length != 1 || !VaultLinkedTreeFilePolicy.IsRootRecord(roots[0]))
            throw new InvalidDataException("Verified export requires one published SVTREE3 root.");

        // Open only once the root has passed the initial structural check.
        // The destination contract itself guarantees that opening a staging
        // stream does not expose it as a completed downloadable object.
        var opened = false;
        try
        {
            opened = true;
            VaultStreamingFileTree.Root manifest;
            await using (var temporary = await destination.OpenTemporaryAsync(ct))
            {
                if (!temporary.CanWrite)
                    throw new InvalidOperationException(
                        "Verified export destination must provide a writable private staging stream.");
                manifest = await VaultLinkedTreeFilePolicy.ReadAsync(
                    objectId, records, temporary, ct);
                await temporary.FlushAsync(ct);
            }
            await RequireSameScopeAsync(scope, checkpoint, true);
            ct.ThrowIfCancellationRequested();
            await destination.CommitAsync(ct);
            return new StoredObject(
                new ObjectDescriptor(objectId, VaultRecordTypeFactory.File,
                    manifest.FileName, manifest.ContentType),
                "content-sha256:" + manifest.Sha256Hex,
                manifest.Length, manifest.Sha256Hex);
        }
        catch
        {
            if (opened)
            {
                try { await destination.AbortAsync(CancellationToken.None); }
                catch { /* Preserve the original verification/commit error. */ }
            }
            throw;
        }
    }

    public async Task<StoredObject?> GetAsync(
        ObjectStoreScope scope, string objectId, CancellationToken ct = default)
    {
        var file = await LoadVerifiedTreeAsync(scope, objectId, ct);
        if (file is not null)
        {
            await using var verifiedBytes = file.Value.Bytes;
            var root = file.Value.Manifest;
            return new StoredObject(
                new ObjectDescriptor(objectId, VaultRecordTypeFactory.File,
                    root.FileName, root.ContentType),
                "content-sha256:" + root.Sha256Hex,
                root.Length, root.Sha256Hex);
        }
        var record = await ReadOrdinaryAsync(scope, objectId, ct);
        if (record is null) return null;
        // Snapshot fingerprint is NOT a signed remote mutation revision and
        // MUST NOT be used as the authorization to overwrite or delete.
        byte[] snapshot = JsonSerializer.SerializeToUtf8Bytes(record);
        try
        {
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(snapshot));
            string revision;
            if (scope.AccountStrategyId == VaultMutationStrategyPolicy.Linked)
            {
                if (linkedLedger is not IStellarSecretKeyRecordRevisionReader reader)
                    throw new NotSupportedException(
                        "Linked ledger does not expose an authoritative record revision.");
                var checkpoint = RequireScope(scope, true);
                var published = await reader.ReadRecordRevisionAsync(record.Id, ct);
                await RequireSameScopeAsync(scope, checkpoint, true);
                if (!VaultLinkedRemoteRevisionPolicy.IsValid(published))
                    throw new InvalidDataException("Linked published record revision is unavailable.");
                revision = "linked-manifest:" + published;
            }
            else
                revision = "hd-sha256:" +
                    VaultHybridRemoteMutationWorkspace.Fingerprint(record);
            return new StoredObject(
                new ObjectDescriptor(record.Id,
                    record.IsFolder ? VaultRecordTypeFactory.Folder : VaultRecordTypeFactory.Credential,
                    record.Title, null, record.FolderId),
                revision,
                Encoding.UTF8.GetByteCount(record.Content), fingerprint);
        }
        finally { CryptographicOperations.ZeroMemory(snapshot); }
    }

    private sealed class ZeroingReadStream(byte[] plaintext)
        : MemoryStream(plaintext, writable: false)
    {
        private byte[]? _data = plaintext;

        protected override void Dispose(bool disposing)
        {
            if (_data is { } buffer)
                CryptographicOperations.ZeroMemory(buffer);
            _data = null;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }

    public async Task<Stream> OpenReadAsync(
        ObjectStoreScope scope, string objectId, CancellationToken ct = default)
    {
        var file = await LoadVerifiedTreeAsync(scope, objectId, ct);
        if (file is not null) return file.Value.Bytes;
        var record = await ReadOrdinaryAsync(scope, objectId, ct)
            ?? throw new FileNotFoundException("Stellar record not found.");
        // Caller owns plaintext and must dispose the stream. This simple text
        // reader is not the streaming SVTREE3 file reader.
        return new ZeroingReadStream(Encoding.UTF8.GetBytes(record.Content));
    }

    /// <summary>
    /// Connect the neutral API to the EXACT existing manual-review services.
    /// No alternate transaction transport, signer, CAS reservation or implicit
    /// capacity expansion is introduced by this method.
    /// </summary>
    public async Task<ObjectMutationReview> ReviewAsync(
        ObjectStoreScope scope, string operationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var checkpoint = RequireScope(scope, true);
        if (scope.AccountStrategyId == VaultMutationStrategyPolicy.Linked)
        {
            if (linkedPublication is null)
                throw new NotSupportedException("Linked publication review is not registered.");
            if (!linkedKeys.CanPublish || linkedKeys.ActiveRootAccountId != scope.StoreId)
                throw new UnauthorizedAccessException(
                    "Linked publication requires the matching unlocked signer and local queue.");
            var unlockEpoch = linkedKeys.UnlockEpoch;
            var review = await linkedPublication.PrepareAsync(operationId, ct);
            try
            {
                await RequireSameScopeAsync(scope, checkpoint, true);
                if (!linkedKeys.CanPublish || linkedKeys.UnlockEpoch != unlockEpoch)
                    throw new InvalidOperationException("Linked queue authorization changed during fee review.");
                if (review.Quote.CreatesChildNode)
                    throw new InvalidOperationException(
                        "Account expansion requires a separately authorized review.");
                return new ObjectMutationReview(
                    review.MutationId, review.EntityId, ObjectMutationState.AwaitingApproval,
                    new ObjectStorageQuote(
                        ProviderId, scope.NetworkId, "XLM",
                        review.Quote.NetworkFeeXlm, review.Quote.PeakReserveXlm,
                        null, false),
                    review.Explanation, review.Quote.QuoteId);
            }
            catch
            {
                try { await linkedPublication.CancelAsync(CancellationToken.None); }
                catch { /* Failure still must not become a usable neutral approval. */ }
                throw;
            }
        }

        if (hdReview is null)
            throw new NotSupportedException("HD publication review is not registered.");
        if (!hdSessions.IsUnlocked || hdSessions.ActiveAccountId != scope.StoreId)
            throw new UnauthorizedAccessException("Unlock the matching HD encrypted queue.");
        var hdEpoch = hdSessions.Epoch;
        var report = await hdReview.ReviewAsync(scope.StoreId, true, ct);
        await RequireSameScopeAsync(scope, checkpoint, true);
        if (!hdSessions.IsUnlocked || hdSessions.Epoch != hdEpoch ||
            hdSessions.ActiveAccountId != scope.StoreId)
            throw new InvalidOperationException("HD queue session changed during review.");
        var mutation = report.Queue.Ready.SingleOrDefault(x => x.Id == operationId);
        var item = report.Estimates.SingleOrDefault(x => x.MutationId == operationId);
        if (mutation is null || item?.Quote is not { } quote ||
            report.Queue.Conflicts != 0 || report.Queue.NeedsReconciliation != 0)
            throw new InvalidOperationException(
                "The requested HD mutation has no current approved-fee preview; inspect the existing Sync review.");
        return new ObjectMutationReview(
            operationId, mutation.EntityId, ObjectMutationState.AwaitingApproval,
            new ObjectStorageQuote(
                ProviderId, scope.NetworkId, "XLM",
                quote.QuotedFeeXlm.ToString("0.0000000",
                    System.Globalization.CultureInfo.InvariantCulture),
                quote.QuotedPeakReserveXlm.ToString("0.0000000",
                    System.Globalization.CultureInfo.InvariantCulture),
                null, null),
            "Indicative HD fee review. The existing session-bound Sync approval must be confirmed separately.",
            quote.DraftSha256);
    }

    public async Task CancelReviewAsync(
        ObjectStoreScope scope, CancellationToken ct = default)
    {
        var checkpoint = RequireScope(scope, true);
        if (scope.AccountStrategyId == VaultMutationStrategyPolicy.Linked)
        {
            if (linkedPublication is null)
                throw new NotSupportedException("Linked publication review is not registered.");
            if (!linkedKeys.CanPublish || linkedKeys.ActiveRootAccountId != scope.StoreId)
                throw new UnauthorizedAccessException("Linked queue authorization is unavailable.");
            await linkedPublication.CancelAsync(ct);
        }
        // HD review is read-only; it has no live signer quote to cancel.
        await RequireSameScopeAsync(scope, checkpoint, true);
    }

    public Task<ObjectMutationIntent> PrepareDeleteAsync(
        ObjectStoreScope scope, string objectId, string expectedRevision,
        CancellationToken ct = default)
    {
        _ = RequireScope(scope, true);
        throw new NotSupportedException(
            "Deletion requires an authenticated, revision-pinned remote mutation journal.");
    }
}
