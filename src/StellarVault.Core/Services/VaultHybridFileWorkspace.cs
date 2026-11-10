using System.Security.Cryptography;
using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// Encrypted hybrid-queue staging for file uploads. The queue's host adapter
/// encrypts the complete intent (file bytes, metadata and stable record IDs)
/// at rest. This workspace never submits to Stellar.
/// </summary>
public sealed class VaultHybridFileWorkspace(
    VaultPendingMutationQueue queue,
    VaultSessionManager sessions,
    VaultDeviceRetentionGate retention,
    VaultNetworkSelection networks,
    IStellarLedgerRecords ledger,
    IVaultLedgerEvidenceProvider evidenceProvider,
    IVaultPreparedCreateLedger preparedCreates)
{
    private const int IntentVersion = 1;

    public sealed record FileUploadIntent(
        int Version, string Title, string FileName, string ContentType,
        string? FolderId, byte[] Content, string Fingerprint, string[] EntryIds);

    public sealed record FileDraftItem(
        string MutationId, string ManifestId, string FileName, string ContentType,
        string? FolderId, long Length, int LedgerEntries, string Fingerprint,
        VaultSyncState State, DateTimeOffset CreatedUtc,
        string[] DependsOn, bool IsCorrupt = false,
        IReadOnlyList<VaultSignedFileEntry>? SignedEntries = null,
        string? NextEntryId = null);

    public async Task<FileDraftItem> StageAsync(
        string fileName, string contentType, Stream content,
        string? folderId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead) throw new ArgumentException("Readable file stream required.", nameof(content));
        var (account, scope) = RequireScope();
        StellarLedgerFileProtocol.ValidateName(fileName);
        if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > 128 ||
            contentType.Contains('\0'))
            throw new ArgumentException("Invalid file content type.", nameof(contentType));
        if (folderId is not null &&
            (folderId.Length != 16 || !folderId.All(Uri.IsHexDigit)))
            throw new ArgumentException("Invalid parent folder ID.", nameof(folderId));

        byte[] bytes = await ReadBoundedAsync(content, ct);
        byte[]? payload = null;
        try
        {
            if (bytes.Length == 0) throw new InvalidDataException("Empty files are not supported.");
            var all = await queue.ReviewAllAsync(account, scope, ct);
            string[] dependencies;
            HashSet<string> occupied;
            try
            {
                dependencies = ParentDependencies(folderId, all);
                occupied = new HashSet<string>(
                    all.Select(x => x.EntityId), StringComparer.OrdinalIgnoreCase);
                foreach (var existingFile in all.Where(x => x.Kind == VaultMutationKind.UploadFile))
                {
                    var priorIntent = DeserializeAndValidate(existingFile);
                    try
                    {
                        // The last stable ID is the manifest and is already
                        // represented by the queue entry's EntityId above.
                        foreach (var id in priorIntent.EntryIds.Take(priorIntent.EntryIds.Length - 1))
                            if (!occupied.Add(id))
                                throw new InvalidDataException("Existing file upload IDs collide; investigate the queue before staging another file.");
                    }
                    finally { CryptographicOperations.ZeroMemory(priorIntent.Content); }
                }
            }
            finally
            {
                foreach (var queued in all)
                    CryptographicOperations.ZeroMemory(queued.EncryptedPayload);
            }

            var displayTitle = fileName.Length <= 256 ? fileName : fileName[..256];
            var partCount = bytes.Length <= StellarLedgerFileProtocol.MaxChunkBytes
                ? 1
                : (bytes.Length + StellarLedgerFileProtocol.MaxChunkBytes - 1) /
                    StellarLedgerFileProtocol.MaxChunkBytes + 1;
            var ids = CreateUniqueIds(partCount, occupied);
            var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
            var stagedBytes = bytes.ToArray();
            try
            {
                var intent = new FileUploadIntent(IntentVersion, displayTitle, fileName,
                    contentType, folderId, stagedBytes, fingerprint, ids);
                payload = JsonSerializer.SerializeToUtf8Bytes(intent);
            }
            finally { CryptographicOperations.ZeroMemory(stagedBytes); }
            if (payload.Length > 8 * 1024 * 1024)
                throw new InvalidDataException("Encrypted file upload intent exceeds the queue size limit.");
            var mutation = VaultPendingMutation.Create(account, scope,
                VaultMutationKind.UploadFile, ids[^1], payload,
                dependsOn: dependencies);
            try
            {
                await queue.EnqueueAsync(mutation, ct);
                RequireSameScope(account, scope);
                return new(mutation.Id, mutation.EntityId, fileName, contentType,
                    folderId, bytes.LongLength, ids.Length, fingerprint,
                    VaultSyncState.Pending, mutation.CreatedUtc, dependencies);
            }
            finally { CryptographicOperations.ZeroMemory(mutation.EncryptedPayload); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
        }
    }

    public async Task<IReadOnlyList<FileDraftItem>> ListAsync(CancellationToken ct = default)
    {
        var (account, scope) = RequireScope();
        var entries = await queue.ReviewAllAsync(account, scope, ct);
        var result = new List<FileDraftItem>();
        foreach (var entry in entries.Where(x => x.Kind == VaultMutationKind.UploadFile))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var intent = DeserializeAndValidate(entry);
                try
                {
                    var journal = entry.SignedFileEntries ?? [];
                    if (journal.Length > intent.EntryIds.Length ||
                        (entry.State == VaultSyncState.Pending && journal.Length > 0) ||
                        journal.Where((x, index) =>
                            x is null ||
                            !string.Equals(x.LedgerRecordId,
                                intent.EntryIds[index], StringComparison.OrdinalIgnoreCase) ||
                            x.TransactionHash is not { Length: 64 } ||
                            !x.TransactionHash.All(Uri.IsHexDigit)).Any() ||
                        journal.Select(x => x.TransactionHash)
                            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Length)
                        throw new InvalidDataException(
                            "File journal does not match the immutable staged entry order.");
                    result.Add(new(entry.Id, entry.EntityId, intent.FileName,
                        intent.ContentType, intent.FolderId, intent.Content.LongLength,
                        intent.EntryIds.Length, intent.Fingerprint, entry.State, entry.CreatedUtc,
                        entry.DependsOn.ToArray(), SignedEntries: journal.ToArray(),
                        NextEntryId: journal.Length < intent.EntryIds.Length
                            ? intent.EntryIds[journal.Length] : null));
                }
                finally { CryptographicOperations.ZeroMemory(intent.Content); }
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or
                                        ArgumentException or CryptographicException)
            {
                result.Add(new(entry.Id, entry.EntityId, "UNREADABLE FILE INTENT",
                    "application/octet-stream", null, 0, 0, string.Empty,
                    entry.State, entry.CreatedUtc, entry.DependsOn.ToArray(), true));
            }
            finally { CryptographicOperations.ZeroMemory(entry.EncryptedPayload); }
        }
        RequireSameScope(account, scope);
        return result.OrderBy(x => x.CreatedUtc).ToArray();
    }

    /// <summary>Reconstruct the immutable manifest-last plan for a future
    /// approved publisher. This method performs no network calls.</summary>
    public async Task<StellarLedgerUploadPlan> GetPlanAsync(
        string mutationId, CancellationToken ct = default)
    {
        var (account, scope) = RequireScope();
        var entries = await queue.ReviewAllAsync(account, scope, ct);
        try
        {
            var entry = entries.SingleOrDefault(x =>
                x.Id == mutationId && x.Kind == VaultMutationKind.UploadFile);
            if (entry is null) throw new KeyNotFoundException("Staged file upload does not exist.");
            if (entry.State != VaultSyncState.Pending)
                throw new InvalidOperationException("Only an unsubmitted file upload can be opened for publication review.");
            var intent = DeserializeAndValidate(entry);
            try
            {
                var plan = new StellarLedgerUploadPlan(intent.Title, intent.FileName,
                    intent.ContentType, intent.Content, intent.FolderId, intent.EntryIds);
                RequireSameScope(account, scope);
                return plan;
            }
            finally { CryptographicOperations.ZeroMemory(intent.Content); }
        }
        finally
        {
            foreach (var queued in entries)
                CryptographicOperations.ZeroMemory(queued.EncryptedPayload);
        }
    }

    /// <summary>
    /// Read-only eligibility inspection for the FIRST immutable file entry.
    /// Fee estimates are indicative; they are NOT approval snapshots and
    /// must never authorize an upload transaction.
    /// </summary>
    public sealed record PublicationPreview(
        string MutationId, string NextLedgerRecordId, bool IsManifest,
        int EntryCount, int RemainingEntries, LedgerCostPreview Cost,
        string Explanation);

    public async Task<PublicationPreview> PreviewFirstEntryAsync(
        string mutationId, CancellationToken ct = default)
    {
        var (account, scope) = RequireScope();
        var epoch = sessions.Epoch;
        if (!ledger.IsConnected)
            throw new InvalidOperationException("Connect the matching HD Stellar root first.");
        var info = await ledger.InspectAccountInfoAsync();
        if (info.RootAccountId != account)
            throw new UnauthorizedAccessException("The connected HD root differs from this queue.");
        var queueReview = await queue.PrepareReviewAsync(account, scope, ct);
        if (queueReview.NeedsReconciliation != 0 || queueReview.Conflicts != 0)
            throw new InvalidOperationException("Reconcile earlier uncertain submissions first.");
        var item = queueReview.Ready.SingleOrDefault(x =>
            x.Id == mutationId && x.Kind == VaultMutationKind.UploadFile);
        if (item is null || item.State != VaultSyncState.Pending ||
            item.TransactionHash is not null ||
            item.SignedFileEntries is { Length: > 0 })
            throw new InvalidOperationException("File is not an untouched, dependency-ready local upload.");

        var all = await queue.ReviewAllAsync(account, scope, ct);
        try
        {
            var intent = DeserializeAndValidate(item);
            try
            {
                if (intent.FolderId is not null &&
                    all.Any(x => x.EntityId.Equals(intent.FolderId,
                        StringComparison.OrdinalIgnoreCase) &&
                        x.Kind != VaultMutationKind.CreateFolder))
                    throw new InvalidOperationException("Local parent is not a folder.");
                var plan = new StellarLedgerUploadPlan(intent.Title, intent.FileName,
                    intent.ContentType, intent.Content, intent.FolderId, intent.EntryIds);
                var remote = await ledger.ReadAsync();
                if (remote.Any(x => x is null || x.Id is not { Length: 16 } ||
                    !x.Id.All(Uri.IsHexDigit)) ||
                    remote.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                        .Any(g => g.Count() != 1))
                    throw new InvalidDataException("Remote record inventory is ambiguous.");
                var remoteIds = remote.Select(x => x.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (intent.EntryIds.Any(remoteIds.Contains))
                    throw new InvalidOperationException("A file part or manifest ID already exists on Stellar.");
                if (intent.FolderId is not null &&
                    !remote.Any(x => x.Id.Equals(intent.FolderId,
                        StringComparison.OrdinalIgnoreCase) && x.IsFolder))
                    throw new InvalidOperationException("Parent folder is not present on the current ledger.");
                var first = plan.Entries[0];
                var cost = await ledger.EstimateAsync(first.Draft, first.Id);
                var after = await queue.ReviewAllAsync(account, scope, ct);
                try
                {
                    var current = after.SingleOrDefault(x => x.Id == mutationId);
                    if (current is null || current.State != VaultSyncState.Pending ||
                        current.TransactionHash is not null ||
                        current.SignedFileEntries is { Length: > 0 } ||
                        !item.EncryptedPayload.AsSpan().SequenceEqual(current.EncryptedPayload) ||
                        !item.DependsOn.SequenceEqual(current.DependsOn, StringComparer.Ordinal) ||
                        sessions.Epoch != epoch ||
                        !sessions.IsUnlocked || sessions.ActiveAccountId != account ||
                        networks.CredentialKey(account) != scope || !ledger.IsConnected)
                        throw new InvalidOperationException("File, session or network changed during preview.");
                    var finalInfo = await ledger.InspectAccountInfoAsync();
                    if (finalInfo.RootAccountId != account)
                        throw new UnauthorizedAccessException("Connected HD root changed during preview.");
                    return new(mutationId, first.Id, plan.Entries.Count == 1,
                        plan.Entries.Count, plan.Entries.Count, cost,
                        "Read-only estimate for the FIRST file transaction only. " +
                        "Full multipart fees, durable approval and broadcast are not implemented. " +
                        "No transaction was submitted.");
                }
                finally
                {
                    foreach (var queued in after)
                        CryptographicOperations.ZeroMemory(queued.EncryptedPayload);
                }
            }
            finally { CryptographicOperations.ZeroMemory(intent.Content); }
        }
        finally
        {
            foreach (var queued in all)
                CryptographicOperations.ZeroMemory(queued.EncryptedPayload);
        }
    }

    /// <summary>
    /// Read-only current-ledger observation for an encrypted queued upload.
    /// Matching record content is NOT historical transaction proof and MUST NOT
    /// authorize confirmation, resumed broadcasting or automatic retries.
    /// </summary>
    public sealed record FileLedgerObservation(
        string MutationId, int TotalEntries, int VisibleMatchingEntries,
        int SignedCheckpointEntries, string? NextMissingEntryId,
        bool ManifestVisible, string Explanation);

    public async Task<FileLedgerObservation> InspectLedgerProgressAsync(
        string mutationId, CancellationToken ct = default)
    {
        var (account, scope) = RequireScope();
        var epoch = sessions.Epoch;
        if (!ledger.IsConnected)
            throw new InvalidOperationException("Connect the matching HD Stellar root first.");
        var info = await ledger.InspectAccountInfoAsync();
        RequireSameScope(account, scope);
        if (sessions.Epoch != epoch || info.RootAccountId != account)
            throw new UnauthorizedAccessException("Connected HD root or vault session changed.");

        var entries = await queue.ReviewAllAsync(account, scope, ct);
        try
        {
            var entry = entries.SingleOrDefault(x =>
                x.Id == mutationId && x.Kind == VaultMutationKind.UploadFile)
                ?? throw new KeyNotFoundException("Staged file upload does not exist.");
            var intent = DeserializeAndValidate(entry);
            try
            {
                var journal = entry.SignedFileEntries ?? [];
                if (journal.Length > intent.EntryIds.Length ||
                    (entry.State == VaultSyncState.Pending && journal.Length != 0) ||
                    journal.Where((checkpoint, index) =>
                        checkpoint is null ||
                        !string.Equals(checkpoint.LedgerRecordId, intent.EntryIds[index],
                            StringComparison.OrdinalIgnoreCase) ||
                        checkpoint.TransactionHash is not { Length: 64 } ||
                        !checkpoint.TransactionHash.All(Uri.IsHexDigit)).Any() ||
                    journal.Select(x => x.TransactionHash)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Length)
                    throw new InvalidDataException("File journal order or identity is corrupt.");

                var plan = new StellarLedgerUploadPlan(intent.Title, intent.FileName,
                    intent.ContentType, intent.Content, intent.FolderId, intent.EntryIds);
                var remote = await ledger.ReadAsync();
                // Remaining rejects duplicate IDs, unexpected content and
                // manifest visibility before all referenced file parts.
                var remaining = plan.Remaining(remote);
                var visible = plan.Entries.Count - remaining.Count;
                var current = await queue.ReviewAllAsync(account, scope, ct);
                try
                {
                    var now = current.SingleOrDefault(x => x.Id == mutationId);
                    if (now is null || now.State != entry.State ||
                        !now.EncryptedPayload.AsSpan().SequenceEqual(entry.EncryptedPayload) ||
                        !(now.SignedFileEntries ?? []).SequenceEqual(journal) ||
                        sessions.Epoch != epoch)
                        throw new InvalidOperationException("Queued file changed during ledger inspection.");
                    RequireSameScope(account, scope);
                    var lastInfo = await ledger.InspectAccountInfoAsync();
                    if (lastInfo.RootAccountId != account)
                        throw new UnauthorizedAccessException("Connected signing root changed.");

                    string explanation = visible > journal.Length
                        ? "Matching ledger entries exceed the signed local journal. Investigate external writes or a lost checkpoint; do not publish."
                        : visible == plan.Entries.Count
                            ? "All staged entry contents are currently visible. Historical signed-receipt verification is still required; do not mark confirmed."
                            : journal.Length > visible
                                ? "Some signed checkpoints are not currently visible. Their broadcasts may have succeeded or remain unresolved; never retry."
                                : "Only a time-of-read content observation. Missing entries are not proof of failed transactions; no publish authorization.";
                    return new FileLedgerObservation(mutationId, plan.Entries.Count,
                        visible, journal.Length,
                        remaining.Count == 0 ? null : remaining[0].Id,
                        visible == plan.Entries.Count, explanation);
                }
                finally
                {
                    foreach (var queued in current)
                        CryptographicOperations.ZeroMemory(queued.EncryptedPayload);
                }
            }
            finally { CryptographicOperations.ZeroMemory(intent.Content); }
        }
        finally
        {
            foreach (var queued in entries)
                CryptographicOperations.ZeroMemory(queued.EncryptedPayload);
        }
    }

    /// <summary>
    /// Authenticates each durable signed upload checkpoint through the existing
    /// root/HD-bound historical ciphertext and transaction verifier. No state
    /// transitions, fee approvals, signing or retransmissions are permitted.
    /// A missing checkpoint is UNKNOWN, never evidence of failed broadcast.
    /// </summary>
    public sealed record FileHistoricalEntryEvidence(
        string LedgerRecordId, string? TransactionHash, bool Verified,
        string Explanation);

    public sealed record FileHistoricalReview(
        string MutationId, int TotalEntries, int VerifiedEntries,
        bool AllEntriesVerified, IReadOnlyList<FileHistoricalEntryEvidence> Entries,
        string Explanation);

    public async Task<FileHistoricalReview> ReviewHistoricalReceiptsAsync(
        string mutationId, CancellationToken ct = default)
    {
        var (account, scope) = RequireScope();
        var epoch = sessions.Epoch;
        if (!ledger.IsConnected)
            throw new InvalidOperationException("Connect the matching HD Stellar root first.");
        var info = await ledger.InspectAccountInfoAsync();
        RequireSameScope(account, scope);
        if (sessions.Epoch != epoch || info.RootAccountId != account)
            throw new UnauthorizedAccessException("Connected root or vault session changed.");

        var queued = await queue.ReviewAllAsync(account, scope, ct);
        try
        {
            var entry = queued.SingleOrDefault(x =>
                x.Id == mutationId && x.Kind == VaultMutationKind.UploadFile)
                ?? throw new KeyNotFoundException("Staged file upload does not exist.");
            var intent = DeserializeAndValidate(entry);
            try
            {
                var journal = entry.SignedFileEntries ?? [];
                if (journal.Length > intent.EntryIds.Length ||
                    (entry.State == VaultSyncState.Pending && journal.Length > 0) ||
                    journal.Where((x, index) => x is null ||
                        !string.Equals(x.LedgerRecordId, intent.EntryIds[index],
                            StringComparison.OrdinalIgnoreCase) ||
                        x.TransactionHash is not { Length: 64 } ||
                        !x.TransactionHash.All(Uri.IsHexDigit)).Any() ||
                    journal.Select(x => x.TransactionHash)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Length)
                    throw new InvalidDataException("Signed upload journal identity or ordering is invalid.");

                var plan = new StellarLedgerUploadPlan(intent.Title, intent.FileName,
                    intent.ContentType, intent.Content, intent.FolderId, intent.EntryIds);
                var evidence = new List<FileHistoricalEntryEvidence>(plan.Entries.Count);
                for (int index = 0; index < plan.Entries.Count; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var part = plan.Entries[index];
                    if (index >= journal.Length)
                    {
                        evidence.Add(new(part.Id, null, false,
                            "No signed checkpoint. Submission status is unknown; do not retry."));
                        continue;
                    }

                    var signed = journal[index];
                    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(part.Draft);
                    try
                    {
                        // This temporary CREATE-shaped proof request is read-only.
                        // The provider authenticates historical ciphertext, the
                        // HD source lineage and the exact draft against this hash.
                        // It is never inserted into the queue or marked Confirmed.
                        var proofRequest = entry with
                        {
                            Kind = VaultMutationKind.CreateRecord,
                            EntityId = part.Id,
                            EncryptedPayload = payload,
                            State = VaultSyncState.Uncertain,
                            TransactionHash = signed.TransactionHash,
                            SignedFileEntries = null
                        };
                        VaultLedgerEvidence? proof;
                        try
                        {
                            proof = await evidenceProvider.InspectAsync(
                                proofRequest, signed.TransactionHash, ct);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception) { proof = null; }
                        var verified = proof is not null &&
                            proof.TransactionSucceeded && proof.RecordLinkedToTransaction &&
                            proof.AccountId == account && proof.NetworkId == scope &&
                            string.Equals(proof.TransactionHash, signed.TransactionHash,
                                StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(proof.RecordId, part.Id,
                                StringComparison.OrdinalIgnoreCase) &&
                            proof.Record is { } record &&
                            string.Equals(record.Id, part.Id, StringComparison.OrdinalIgnoreCase) &&
                            record.Title == part.Draft.Title &&
                            record.Content == part.Draft.Content &&
                            record.FolderId == part.Draft.FolderId &&
                            record.IsFolder == part.Draft.IsFolder &&
                            record.UserName == part.Draft.UserName &&
                            record.Url == part.Draft.Url &&
                            record.Notes == part.Draft.Notes &&
                            (record.Tags ?? []).SequenceEqual(part.Draft.Tags ?? [], StringComparer.Ordinal);
                        evidence.Add(new(part.Id, signed.TransactionHash, verified,
                            verified
                                ? "Signed transaction and exact record payload authenticated historically."
                                : "Historical signed transaction/record proof unavailable or mismatched. Do not retry."));
                    }
                    finally { CryptographicOperations.ZeroMemory(payload); }
                }

                var latest = await queue.ReviewAllAsync(account, scope, ct);
                try
                {
                    var current = latest.SingleOrDefault(x => x.Id == mutationId);
                    if (current is null || current.State != entry.State ||
                        !current.EncryptedPayload.AsSpan().SequenceEqual(entry.EncryptedPayload) ||
                        !(current.SignedFileEntries ?? []).SequenceEqual(journal) ||
                        !current.DependsOn.SequenceEqual(entry.DependsOn, StringComparer.Ordinal) ||
                        sessions.Epoch != epoch)
                        throw new InvalidOperationException("File journal or vault session changed during historical review.");
                    RequireSameScope(account, scope);
                    var finalInfo = await ledger.InspectAccountInfoAsync();
                    if (finalInfo.RootAccountId != account)
                        throw new UnauthorizedAccessException("Connected HD root changed during review.");
                    var verifiedCount = evidence.Count(x => x.Verified);
                    var allVerified = journal.Length == plan.Entries.Count &&
                        verifiedCount == plan.Entries.Count;
                    return new FileHistoricalReview(mutationId, plan.Entries.Count,
                        verifiedCount, allVerified, evidence,
                        allVerified
                            ? "All signed upload entries have independent historical evidence. This review does NOT confirm the queue or authorize another transaction."
                            : "Incomplete historical evidence. Preserve the encrypted queue; do not retry or resume publishing.");
                }
                finally
                {
                    foreach (var item in latest)
                        CryptographicOperations.ZeroMemory(item.EncryptedPayload);
                }
            }
            finally { CryptographicOperations.ZeroMemory(intent.Content); }
        }
        finally
        {
            foreach (var item in queued)
                CryptographicOperations.ZeroMemory(item.EncryptedPayload);
        }
    }

    private readonly SemaphoreSlim filePublicationGate = new(1, 1);

    /// <summary>A single file-entry fee approval. Each part requires a fresh
    /// independent quote; approval never carries forward to another part.</summary>
    public sealed record FileEntryApproval(
        string MutationId, string AccountId, string NetworkId,
        string LedgerRecordId, int SignedEntriesBefore, string PayloadSha256,
        decimal FeeXlm, decimal PeakReserveXlm, DateTimeOffset QuotedUtc,
        long SessionEpoch, bool IsManifest);

    public sealed record FileEntryAttempt(
        string MutationId, string LedgerRecordId, string? TransactionHash,
        bool IsManifest, string Explanation);

    /// <summary>
    /// Read-only preview for the exact NEXT entry. Existing signed entries
    /// require independent historical proof and present ledger visibility;
    /// missing or ambiguous receipts do not authorize continuation.
    /// </summary>
    public async Task<FileEntryApproval> QuoteNextFileEntryAsync(
        string mutationId, CancellationToken ct = default)
    {
        var (account, scope) = RequireScope();
        var epoch = sessions.Epoch;
        if (!ledger.IsConnected) throw new InvalidOperationException("Connect the matching HD root.");
        var accountInfo = await ledger.InspectAccountInfoAsync();
        if (accountInfo.RootAccountId != account)
            throw new UnauthorizedAccessException("Connected HD root differs from the file queue.");

        var all = await queue.ReviewAllAsync(account, scope, ct);
        try
        {
            var current = all.SingleOrDefault(x => x.Id == mutationId &&
                x.Kind == VaultMutationKind.UploadFile)
                ?? throw new KeyNotFoundException("File upload not found.");
            var journal = current.SignedFileEntries ?? [];
            if (current.TransactionHash is not null ||
                current.State != (journal.Length == 0 ? VaultSyncState.Pending : VaultSyncState.Uncertain))
                throw new InvalidOperationException("File has an unresolved reservation or unsupported queue state.");
            if (all.Any(x => x.Id != mutationId &&
                x.State is VaultSyncState.Submitting or VaultSyncState.Uncertain or VaultSyncState.Conflict))
                throw new InvalidOperationException("Other unresolved queue mutations block file approval.");
            if (current.DependsOn.Any(id => !all.Any(x => x.Id == id &&
                x.State == VaultSyncState.Confirmed)))
                throw new InvalidOperationException("Local parent folder has not been confirmed.");
            var intent = DeserializeAndValidate(current);
            try
            {
                var plan = new StellarLedgerUploadPlan(intent.Title, intent.FileName,
                    intent.ContentType, intent.Content, intent.FolderId, intent.EntryIds);
                if (journal.Length >= plan.Entries.Count ||
                    journal.Where((x, i) => x is null ||
                        !string.Equals(x.LedgerRecordId, plan.Entries[i].Id,
                            StringComparison.OrdinalIgnoreCase) ||
                        x.TransactionHash is not { Length: 64 } ||
                        !x.TransactionHash.All(Uri.IsHexDigit)).Any() ||
                    journal.Select(x => x.TransactionHash)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Length)
                    throw new InvalidDataException("Invalid signed multipart upload journal.");

                var remote = await ledger.ReadAsync();
                var remaining = plan.Remaining(remote);
                if (remaining.Count == 0 || remaining[0].Id != plan.Entries[journal.Length].Id ||
                    plan.Entries.Count - remaining.Count != journal.Length)
                    throw new InvalidOperationException(
                        "Current on-chain file entries differ from the signed journal; investigate before publishing.");
                if (intent.FolderId is not null &&
                    !remote.Any(x => string.Equals(x.Id, intent.FolderId,
                        StringComparison.OrdinalIgnoreCase) && x.IsFolder))
                    throw new InvalidOperationException("Parent folder is absent from Stellar.");
                if (journal.Length > 0)
                {
                    // Historical transaction evidence is independent of present
                    // ledger contents and must be complete before any new part.
                    var previous = await ReviewHistoricalReceiptsAsync(mutationId, ct);
                    if (previous.VerifiedEntries != journal.Length)
                        throw new InvalidOperationException(
                            "Earlier signed entries are not independently verified.");
                }
                var next = plan.Entries[journal.Length];
                var cost = await ledger.EstimateAsync(next.Draft, next.Id);
                if (cost.NetworkFeeXlm < 0 || cost.PeakReserveXlm < 0)
                    throw new InvalidDataException("Invalid ledger fee/reserve estimate.");
                RequireSameScope(account, scope);
                if (sessions.Epoch != epoch)
                    throw new InvalidOperationException("Vault session changed during file review.");
                var latestInfo = await ledger.InspectAccountInfoAsync();
                if (latestInfo.RootAccountId != account)
                    throw new UnauthorizedAccessException("Connected root changed during file review.");
                var latest = await queue.ReviewAllAsync(account, scope, ct);
                try
                {
                    var now = latest.SingleOrDefault(x => x.Id == mutationId);
                    if (now is null || now.State != current.State ||
                        !now.EncryptedPayload.AsSpan().SequenceEqual(current.EncryptedPayload) ||
                        !(now.SignedFileEntries ?? []).SequenceEqual(journal) ||
                        !now.DependsOn.SequenceEqual(current.DependsOn, StringComparer.Ordinal))
                        throw new InvalidOperationException("File draft changed during cost review.");
                }
                finally
                {
                    foreach (var x in latest)
                        CryptographicOperations.ZeroMemory(x.EncryptedPayload);
                }
                return new(mutationId, account, scope, next.Id, journal.Length,
                    Convert.ToHexString(SHA256.HashData(current.EncryptedPayload)),
                    cost.NetworkFeeXlm, cost.PeakReserveXlm,
                    DateTimeOffset.UtcNow, epoch, journal.Length == plan.Entries.Count - 1);
            }
            finally { CryptographicOperations.ZeroMemory(intent.Content); }
        }
        finally
        {
            foreach (var x in all)
                CryptographicOperations.ZeroMemory(x.EncryptedPayload);
        }
    }

    /// <summary>
    /// Explicit single-entry upload. Never retries. Durable reservation is
    /// saved before calling the signer, and the signed hash is saved before
    /// broadcasting. Every outcome after reservation remains Uncertain.
    /// </summary>
    public async Task<FileEntryAttempt> PublishApprovedFileEntryAsync(
        FileEntryApproval approval, bool explicitlyApproved,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (!explicitlyApproved)
            throw new InvalidOperationException("Explicit paid file-part approval required.");
        if (!await filePublicationGate.WaitAsync(0, ct))
            throw new InvalidOperationException("Another file publication is active.");
        try
        {
            var (account, scope) = RequireScope();
            if (approval.AccountId != account || approval.NetworkId != scope ||
                approval.SessionEpoch != sessions.Epoch ||
                DateTimeOffset.UtcNow < approval.QuotedUtc ||
                DateTimeOffset.UtcNow - approval.QuotedUtc > TimeSpan.FromMinutes(5))
                throw new InvalidOperationException("File approval expired or changed scope.");
            // Re-run the complete remote/journal/HD proof and cost preflight.
            var refreshed = await QuoteNextFileEntryAsync(approval.MutationId, ct);
            if (refreshed.LedgerRecordId != approval.LedgerRecordId ||
                refreshed.SignedEntriesBefore != approval.SignedEntriesBefore ||
                refreshed.PayloadSha256 != approval.PayloadSha256 ||
                refreshed.FeeXlm > approval.FeeXlm ||
                refreshed.PeakReserveXlm > approval.PeakReserveXlm ||
                refreshed.IsManifest != approval.IsManifest)
                throw new InvalidOperationException("File entry, remote state or cost changed; approve again.");

            var entries = await queue.ReviewAllAsync(account, scope, ct);
            try
            {
                var current = entries.Single(x => x.Id == approval.MutationId);
                if (Convert.ToHexString(SHA256.HashData(current.EncryptedPayload)) != approval.PayloadSha256 ||
                    (current.SignedFileEntries?.Length ?? 0) != approval.SignedEntriesBefore)
                    throw new InvalidOperationException("Approved file revision changed.");
                var intent = DeserializeAndValidate(current);
                LedgerRecordDraft draft;
                try
                {
                    var plan = new StellarLedgerUploadPlan(intent.Title, intent.FileName,
                        intent.ContentType, intent.Content, intent.FolderId, intent.EntryIds);
                    draft = plan.Entries[approval.SignedEntriesBefore].Draft;
                }
                finally { CryptographicOperations.ZeroMemory(intent.Content); }

                // CAS boundary: after reservation, the only safe default is
                // Uncertain, including when signer work fails before callback.
                var reserved = approval.SignedEntriesBefore == 0
                    ? await queue.ReserveFileUploadAsync(current, ct)
                    : await queue.ReserveVerifiedNextFileEntryAsync(current, ct);
                var revision = reserved;
                string? hash = null;
                Exception? failure = null;
                try
                {
                    RequireSameScope(account, scope);
                    if (sessions.Epoch != approval.SessionEpoch ||
                        (await ledger.InspectAccountInfoAsync()).RootAccountId != account)
                        throw new InvalidOperationException("Connected root/session changed.");
                    hash = await preparedCreates.PublishWithPreparedHashAsync(
                        approval.LedgerRecordId, draft, async signedHash =>
                        {
                            RequireSameScope(account, scope);
                            if (sessions.Epoch != approval.SessionEpoch)
                                throw new InvalidOperationException("Vault changed before signed checkpoint.");
                            revision = await queue.CheckpointSignedFileEntryAsync(
                                revision, approval.LedgerRecordId,
                                signedHash.ToLowerInvariant(), CancellationToken.None);
                            hash = signedHash.ToLowerInvariant();
                        });
                }
                catch (Exception ex) { failure = ex; }
                // Always leave a durable no-replay quarantine after this attempt.
                try { await queue.MarkUncertainAsync(revision, null, CancellationToken.None); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "Cannot quarantine reserved upload. Preserve queue; NEVER retry.", ex);
                }
                return new(approval.MutationId, approval.LedgerRecordId,
                    revision.SignedFileEntries?.LastOrDefault()?.TransactionHash,
                    approval.IsManifest, failure is null
                        ? "Submission attempted. Independently verify history before another file entry."
                        : "Submission outcome unknown. Preserve the journal and investigate; do not retry.");
            }
            finally
            {
                foreach (var entry in entries)
                    CryptographicOperations.ZeroMemory(entry.EncryptedPayload);
            }
        }
        finally { filePublicationGate.Release(); }
    }

    public async Task DiscardAsync(string mutationId, CancellationToken ct = default)
    {
        var (account, scope) = RequireScope();
        await queue.DiscardPendingAsync(account, scope, mutationId, ct);
        RequireSameScope(account, scope);
    }

    private FileUploadIntent DeserializeAndValidate(VaultPendingMutation entry)
    {
        var intent = JsonSerializer.Deserialize<FileUploadIntent>(entry.EncryptedPayload)
            ?? throw new InvalidDataException("Staged file intent is empty.");
        if (intent.Version != IntentVersion ||
            string.IsNullOrWhiteSpace(intent.Title) || intent.Title.Length > 256 ||
            intent.Content is not { Length: > 0 and <= StellarLedgerFileProtocol.MaxFileBytes } ||
            intent.EntryIds is not { Length: > 0 and <= StellarLedgerFileProtocol.MaxParts + 1 } ||
            intent.Fingerprint is not { Length: 64 } ||
            !intent.Fingerprint.All(Uri.IsHexDigit) ||
            intent.EntryIds.Any(x => x is null || x.Length != 16 || !x.All(Uri.IsHexDigit)) ||
            intent.EntryIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != intent.EntryIds.Length ||
            (intent.FolderId is not null &&
                (intent.FolderId.Length != 16 || !intent.FolderId.All(Uri.IsHexDigit))))
        {
            if (intent.Content is not null) CryptographicOperations.ZeroMemory(intent.Content);
            throw new InvalidDataException("Staged file intent metadata is invalid.");
        }
        try
        {
            StellarLedgerFileProtocol.ValidateName(intent.FileName);
            if (string.IsNullOrWhiteSpace(intent.ContentType) ||
                intent.ContentType.Length > 128 || intent.ContentType.Contains('\0'))
                throw new InvalidDataException("Staged file content type is invalid.");
            var actual = Convert.ToHexString(SHA256.HashData(intent.Content));
            if (!string.Equals(actual, intent.Fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Staged file fingerprint does not match its bytes.");
            var expectedEntries = intent.Content.Length <= StellarLedgerFileProtocol.MaxChunkBytes
                ? 1
                : (intent.Content.Length + StellarLedgerFileProtocol.MaxChunkBytes - 1) /
                    StellarLedgerFileProtocol.MaxChunkBytes + 1;
            if (intent.EntryIds.Length != expectedEntries ||
                !string.Equals(intent.EntryIds[^1], entry.EntityId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Staged file part inventory or manifest identity differs from its queue entry.");
            return intent;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(intent.Content);
            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        byte[] block = new byte[16 * 1024];
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(block, ct);
                if (read == 0) break;
                if (buffer.Length + read > StellarLedgerFileProtocol.MaxFileBytes)
                    throw new InvalidDataException("File exceeds the 1 MiB hybrid upload limit.");
                await buffer.WriteAsync(block.AsMemory(0, read), ct);
            }
            return buffer.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(block);
            if (buffer.TryGetBuffer(out var segment) && segment.Array is not null)
                CryptographicOperations.ZeroMemory(segment.Array);
        }
    }

    private static string[] CreateUniqueIds(int count, HashSet<string> occupied)
    {
        var ids = new string[count];
        for (var index = 0; index < count; index++)
        {
            var allocated = false;
            for (var attempt = 0; attempt < 64; attempt++)
            {
                var candidate = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
                if (!occupied.Add(candidate)) continue;
                ids[index] = candidate;
                allocated = true;
                break;
            }
            if (!allocated)
                throw new InvalidOperationException("Could not allocate collision-free file chunk identifiers.");
        }
        return ids;
    }

    private static string[] ParentDependencies(
        string? folderId, IReadOnlyList<VaultPendingMutation> entries)
    {
        if (folderId is null) return [];
        var parents = entries.Where(x => x.EntityId.Equals(
            folderId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (parents.Length > 1) throw new InvalidOperationException("Parent folder ID is ambiguous.");
        if (parents.Length == 0 || parents[0].State == VaultSyncState.Confirmed) return [];
        var parent = parents[0];
        if (parent.Kind != VaultMutationKind.CreateFolder ||
            parent.State != VaultSyncState.Pending || parent.TransactionHash is not null)
            throw new InvalidOperationException("Only a pending or confirmed folder can contain a staged file.");
        return [parent.Id];
    }

    private (string Account, string Network) RequireScope()
    {
        retention.RequireLocalPersistence();
        var account = sessions.ActiveAccountId
            ?? throw new InvalidOperationException("Unlock the encrypted local vault first.");
        if (!sessions.IsUnlocked)
            throw new InvalidOperationException("Unlock the encrypted local vault first.");
        return (account, networks.CredentialKey(account));
    }

    private void RequireSameScope(string account, string network)
    {
        if (!sessions.IsUnlocked || sessions.ActiveAccountId != account ||
            networks.CredentialKey(account) != network)
            throw new InvalidOperationException("Vault account or network changed during file staging.");
    }
}
