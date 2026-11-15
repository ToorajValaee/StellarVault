using System.Security.Cryptography;

namespace StellarVault.Core.Services;

/// <summary>Queued mutations are local drafts, never confirmed Stellar records.</summary>
public enum VaultSyncState
{
    Pending = 0,
    Submitting = 1,
    Uncertain = 2,
    Confirmed = 3,
    Conflict = 4,
    Failed = 5
}

public enum VaultMutationKind { CreateRecord, UpdateRecord, DeleteRecord, CreateFolder, UpdateFolder, DeleteFolder, UploadFile, DeleteFile, ExtendLinkedChain }

/// <summary>One signed deletion transaction for one deterministic HD manifest owner.
/// Stored only within the account/network encrypted queue entry.</summary>
public sealed record VaultSignedDeletionManifest(string SourceAccountId, string TransactionHash);

/// <summary>
/// One immutable, fully signed Stellar transaction identity for an SVFILE
/// part or final manifest. A hash is NOT proof of successful inclusion.
/// </summary>
public sealed record VaultSignedFileEntry(string LedgerRecordId, string TransactionHash);

/// <summary>
/// Ciphertext only. Host adapters must encrypt the entire serialized queue at rest,
/// using authenticated account/network-associated data, before storing any entry.
/// </summary>
public sealed record VaultPendingMutation(
    string Id,
    string AccountId,
    string NetworkId,
    VaultMutationKind Kind,
    string EntityId,
    byte[] EncryptedPayload,
    string? ExpectedRemoteVersion,
    string[] DependsOn,
    VaultSyncState State,
    string? TransactionHash,
    DateTimeOffset CreatedUtc,
    VaultSignedDeletionManifest[]? SignedDeletionManifests = null,
    VaultSignedFileEntry[]? SignedFileEntries = null,
    string? OriginStrategyId = null)
{
    public static VaultPendingMutation Create(string accountId, string networkId,
        VaultMutationKind kind, string entityId, byte[] encryptedPayload,
        string? expectedRemoteVersion = null, string[]? dependsOn = null)
    {
        StellarAccountAddress.Validate(accountId);
        if (string.IsNullOrWhiteSpace(networkId) || string.IsNullOrWhiteSpace(entityId))
            throw new ArgumentException("Network and entity IDs are required.");
        if (!Enum.IsDefined(kind) || encryptedPayload is not { Length: > 0 })
            throw new ArgumentException("Invalid mutation or payload.");
        if (encryptedPayload.Length > 8 * 1024 * 1024)
            throw new ArgumentException("A single offline mutation exceeds the current 8 MiB limit.");
        var prerequisites = dependsOn?.ToArray() ?? [];
        if (prerequisites.Any(x => !Guid.TryParseExact(x, "N", out _)) ||
            prerequisites.Distinct(StringComparer.Ordinal).Count() != prerequisites.Length)
            throw new ArgumentException("Dependencies must be unique mutation IDs.", nameof(dependsOn));
        return new VaultPendingMutation(Guid.NewGuid().ToString("N"), accountId, networkId,
            kind, entityId, encryptedPayload.ToArray(), expectedRemoteVersion,
            prerequisites, VaultSyncState.Pending, null, DateTimeOffset.UtcNow);
    }
}

/// <summary>Durable implementation must atomically compare and replace queue revisions,
/// and encrypt authenticated serialized content before writing to disk or IndexedDB.</summary>
public interface IVaultPendingMutationStore
{
    Task<VaultPendingMutation?> GetAsync(string accountId, string networkId, string mutationId, CancellationToken ct = default);
    Task<IReadOnlyList<VaultPendingMutation>> ListAsync(string accountId, string networkId, CancellationToken ct = default);
    Task<bool> AddAsync(VaultPendingMutation entry, CancellationToken ct = default);
    Task<bool> TryReplaceAsync(VaultPendingMutation previous, VaultPendingMutation replacement, CancellationToken ct = default);
    Task<bool> TryDeleteAsync(VaultPendingMutation expected, CancellationToken ct = default);
}

/// <summary>
/// Safety-first queue coordinator. Does NOT submit Stellar transactions. A higher
/// level must obtain explicit fee approval and confirm remote state before marking
/// a submission as confirmed. Uncertain entries cannot be retried blindly.
/// </summary>
public interface IVaultLedgerMutationVerifier
{
    Task<bool> IsConfirmedAsync(VaultPendingMutation mutation, string transactionHash,
        CancellationToken ct = default);
}

/// <summary>Default until a network-specific transaction and entity verifier is installed.</summary>
public sealed class DenyVaultLedgerMutationVerifier : IVaultLedgerMutationVerifier
{
    public Task<bool> IsConfirmedAsync(VaultPendingMutation mutation,
        string transactionHash, CancellationToken ct = default) => Task.FromResult(false);
}

public sealed record VaultSyncPreflightResult(string MutationId, string LedgerRecordId,
    string DraftSha256, string Explanation);

public sealed record VaultReconciliationResult(string MutationId, VaultSyncState State,
    bool Confirmed, string Explanation);

public sealed record VaultSyncBlockedMutation(string MutationId, string[] MissingDependencies,
    string[] WaitingDependencies);

public sealed record VaultSyncReview(IReadOnlyList<VaultPendingMutation> Ready,
    IReadOnlyList<VaultSyncBlockedMutation> Blocked, int NeedsReconciliation, int Conflicts);

public sealed record VaultQueueStatus(int Pending, int Submitting, int Uncertain,
    int Confirmed, int Conflicts, int Failed);

public sealed class VaultPendingMutationQueue(IVaultPendingMutationStore store,
    VaultDeviceRetentionGate retention, IVaultLedgerMutationVerifier verifier)
{
    public async Task<VaultPendingMutation> EnqueueAsync(VaultPendingMutation entry, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        VaultMutationStrategyPolicy.Validate(entry.OriginStrategyId);
        // The current queue authoring path is HD-bound. Linked-key entries need
        // a separately authenticated key lifecycle before they may be created.
        VaultMutationStrategyPolicy.RequireHd(entry);
        if (entry.Kind == VaultMutationKind.ExtendLinkedChain)
            throw new InvalidOperationException(
                "Linked-account expansion journals are never HD queue mutations.");
        if (entry.Kind is VaultMutationKind.UploadFile or VaultMutationKind.DeleteFile)
            VaultHybridFilePolicy.RequireEnabled();
        if (entry.State != VaultSyncState.Pending || entry.TransactionHash is not null ||
            entry.SignedFileEntries is { Length: > 0 })
            throw new InvalidOperationException("Only new pending drafts can be queued.");
        if (!Guid.TryParseExact(entry.Id, "N", out _) || entry.DependsOn.Contains(entry.Id, StringComparer.Ordinal))
            throw new InvalidOperationException("Mutation ID is invalid or depends on itself.");
        if (!await store.AddAsync(entry, ct))
            throw new InvalidOperationException("A mutation with this ID already exists.");
        return entry;
    }

    public async Task<IReadOnlyList<VaultPendingMutation>> ReviewReadyAsync(
        string accountId, string networkId, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        var entries = await ScopedEntriesAsync(accountId, networkId, ct);
        var byId = entries.ToDictionary(x => x.Id, StringComparer.Ordinal);
        return entries.Where(x => x.State == VaultSyncState.Pending &&
            VaultMutationStrategyPolicy.Resolve(x.OriginStrategyId) == VaultMutationStrategyPolicy.Hd &&
            // DeleteFile is staged only until a dedicated multi-record
            // tombstone journal and verifier are implemented.
            x.Kind is not (VaultMutationKind.UploadFile or VaultMutationKind.DeleteFile) &&
            x.DependsOn is not null &&
            x.DependsOn.All(id => byId.TryGetValue(id, out var dependency) &&
                dependency.State == VaultSyncState.Confirmed) &&
            (x.Kind is not (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder) ||
             VaultQueuedCreatePolicy.Problems(x, entries).Length == 0))
            .OrderBy(x => x.CreatedUtc).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Read-only preparation report. It never estimates fees or submits writes.</summary>
    public async Task<VaultSyncReview> PrepareReviewAsync(string accountId, string networkId,
        CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        var all = await ScopedEntriesAsync(accountId, networkId, ct);
        return VaultPendingMutationReviewPolicy.Build(all);
    }

    [Obsolete("Use ReserveApprovedSubmissionAsync with a fresh reviewed quote.")]
    public Task<VaultPendingMutation> MarkSubmittingAsync(
        string accountId, string networkId, string mutationId, CancellationToken ct = default) =>
        throw new NotSupportedException("Submission reservation requires an approved quote.");

    /// <summary>Reserve one mutation for submission only after checking a fresh
    /// account/network-scoped quote and the exact durable queue revision.
    /// This method never performs the network call.</summary>
    [Obsolete("Use ReservePreflightedSubmissionAsync after an independent remote ID inventory check.")]
    public Task<VaultPendingMutation> ReserveApprovedSubmissionAsync(
        VaultSyncApprovalSnapshot quote, string accountId, string networkId,
        DateTimeOffset now, CancellationToken ct = default) =>
        throw new NotSupportedException("Submission reservation requires a fresh remote record inventory.");

    /// <summary>Durably reserve only after comparing a fresh remote ID inventory
    /// with the exact approved local queue revision. Never broadcasts a transaction.
    /// The remote inventory is advisory: a network race still requires reconciliation.</summary>
    public async Task<VaultPendingMutation> ReservePreflightedSubmissionAsync(
        VaultSyncApprovalSnapshot quote, string accountId, string networkId,
        IReadOnlyCollection<LedgerRecordItem> remoteRecords, DateTimeOffset now,
        CancellationToken ct = default)
    {
        await PreflightCreateAsync(quote, accountId, networkId, remoteRecords, now, ct);
        var item = await store.GetAsync(accountId, networkId, quote.MutationId, ct)
            ?? throw new KeyNotFoundException("Approved mutation no longer exists.");
        RequireScope(item, accountId, networkId);
        VaultSyncApprovalGuard.RequireFresh(quote, item, accountId, networkId,
            now, TimeSpan.FromMinutes(5));
        var reserved = item with { State = VaultSyncState.Submitting };
        if (!await store.TryReplaceAsync(item, reserved, ct))
            throw new InvalidOperationException("Local draft was modified during submission reservation.");
        return reserved;
    }

    /// <summary>Read-only last-mile eligibility guard. Requires a current ledger
    /// record-ID inventory supplied by the caller and never mutates queue state.</summary>
    public async Task<VaultSyncPreflightResult> PreflightCreateAsync(
        VaultSyncApprovalSnapshot quote, string accountId, string networkId,
        IReadOnlyCollection<LedgerRecordItem> remoteRecords, DateTimeOffset now,
        CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        ArgumentNullException.ThrowIfNull(remoteRecords);
        var item = await store.GetAsync(accountId, networkId, quote.MutationId, ct)
            ?? throw new KeyNotFoundException("Quoted mutation not found.");
        RequireScope(item, accountId, networkId);
        VaultSyncApprovalGuard.RequireFresh(quote, item, accountId, networkId,
            now, TimeSpan.FromMinutes(5));
        var review = await PrepareReviewAsync(accountId, networkId, ct);
        if (!review.Ready.Any(x => x.Id == item.Id) || review.NeedsReconciliation != 0 ||
            review.Conflicts != 0)
            throw new InvalidOperationException("Queue is not clear for publication.");
        if (item.Kind is not (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder) ||
            item.EntityId.Length != 16 || !item.EntityId.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Unsupported creation ID or mutation type.");
        if (remoteRecords.Any(x => string.IsNullOrWhiteSpace(x.Id)) ||
            remoteRecords.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            throw new InvalidDataException("Ledger record inventory is malformed or contains duplicate IDs.");
        if (remoteRecords.Any(x => x.Id.Equals(item.EntityId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Target ledger record ID already exists.");
        var all = await ScopedEntriesAsync(accountId, networkId, ct);
        var problems = VaultQueuedCreatePolicy.Problems(item, all);
        if (problems.Length > 0)
            throw new InvalidDataException("Queued create cannot be published: " + string.Join(", ", problems));
        // A locally Confirmed tree child is not enough: immediately before
        // approval and signed broadcast, each referenced child must still
        // match its complete originally approved record on Stellar.
        var remoteTreeProblems = VaultHdTreeFilePolicy.RemoteProblems(
            item, all, remoteRecords);
        if (remoteTreeProblems.Length > 0)
            throw new InvalidDataException(
                "HD file tree cannot be published: " +
                string.Join(", ", remoteTreeProblems));
        LedgerRecordDraft draft;
        try
        {
            draft = System.Text.Json.JsonSerializer.Deserialize<LedgerRecordDraft>(item.EncryptedPayload)
                ?? throw new InvalidDataException("Missing queued draft.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidDataException("Queued draft is malformed.", ex);
        }
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 256 ||
            string.IsNullOrEmpty(draft.Content) ||
            draft.IsFolder != (item.Kind == VaultMutationKind.CreateFolder))
            throw new InvalidDataException("Queued record does not match the requested creation kind.");
        if (draft.FolderId is not null && all.Any(x => x.EntityId.Equals(draft.FolderId, StringComparison.OrdinalIgnoreCase) &&
            x.State != VaultSyncState.Confirmed))
            throw new InvalidOperationException("Local parent folder has not been confirmed on Stellar.");
        if (draft.FolderId is not null && !remoteRecords.Any(x => x.Id.Equals(draft.FolderId, StringComparison.OrdinalIgnoreCase) && x.IsFolder))
            throw new InvalidOperationException("Parent folder is absent from the current ledger inventory.");
        if (all.Any(x => x.Id != item.Id && (x.State == VaultSyncState.Submitting || x.State == VaultSyncState.Uncertain)))
            throw new InvalidOperationException("An earlier submission must be reconciled first.");
        if (all.Any(x => x.Id != item.Id && x.EntityId.Equals(item.EntityId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Another local mutation shares this record ID.");
        return new VaultSyncPreflightResult(item.Id, item.EntityId, quote.DraftSha256,
            "Read-only preflight passed. This is not permission to broadcast.");
    }

    /// <summary>
    /// Version-pinned, read-only preflight for a published record/folder update
    /// or deletion. This is not a transaction authorization and never writes
    /// the ledger. Fingerprints are computed from the full recovered record.
    /// </summary>
    public async Task<VaultSyncPreflightResult> PreflightRemoteMutationAsync(
        VaultSyncApprovalSnapshot quote, string accountId, string networkId,
        IReadOnlyCollection<LedgerRecordItem> remoteRecords, DateTimeOffset now,
        CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        ArgumentNullException.ThrowIfNull(remoteRecords);
        var current = await store.GetAsync(accountId, networkId, quote.MutationId, ct)
            ?? throw new KeyNotFoundException("Quoted mutation is missing.");
        RequireScope(current, accountId, networkId);
        VaultSyncApprovalGuard.RequireFresh(quote, current, accountId, networkId,
            now, TimeSpan.FromMinutes(5));
        if (current.Kind is not (VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder or
            VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder) ||
            current.EntityId is not { Length: 16 } ||
            !current.EntityId.All(Uri.IsHexDigit) ||
            current.ExpectedRemoteVersion is not { Length: 64 } baseline ||
            !baseline.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            current.DependsOn.Length != 0)
            throw new InvalidOperationException("Mutation is not a valid version-pinned remote change.");
        var review = await PrepareReviewAsync(accountId, networkId, ct);
        if (review.NeedsReconciliation != 0 || review.Conflicts != 0 ||
            !review.Ready.Any(x => x.Id == current.Id))
            throw new InvalidOperationException("Unresolved submissions or conflicts block Sync.");
        var scoped = await ScopedEntriesAsync(accountId, networkId, ct);
        if (scoped.Any(x => x.Id != current.Id &&
            x.EntityId.Equals(current.EntityId, StringComparison.OrdinalIgnoreCase) &&
            x.State is not (VaultSyncState.Failed or VaultSyncState.Confirmed)))
            throw new InvalidOperationException("Another active queue mutation targets the same remote record.");

        if (remoteRecords.Any(x => x is null || x.Id is not { Length: 16 } ||
            !x.Id.All(Uri.IsHexDigit)) ||
            remoteRecords.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() != 1))
            throw new InvalidDataException("Remote inventory is invalid or ambiguous.");
        var remote = remoteRecords.SingleOrDefault(x =>
            x.Id.Equals(current.EntityId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Remote target disappeared: conflict.");
        if (remote.IsFolder != (current.Kind is VaultMutationKind.UpdateFolder or VaultMutationKind.DeleteFolder) ||
            VaultHybridRemoteMutationWorkspace.Fingerprint(remote) != baseline)
            throw new InvalidOperationException("Remote contents differ from the pinned baseline: conflict.");
        if (remote.Content.StartsWith(StellarLedgerFileProtocol.SinglePrefix, StringComparison.Ordinal) ||
            remote.Content.StartsWith(StellarLedgerFileProtocol.ManifestPrefix, StringComparison.Ordinal) ||
            remote.Content.StartsWith(StellarLedgerFileProtocol.ChunkPrefix, StringComparison.Ordinal) ||
            remote.Content.StartsWith(VaultLinkedTreeFilePolicy.Prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("File-protocol records cannot use the text update/delete flow.");
        if (current.Kind is VaultMutationKind.DeleteFolder &&
            remoteRecords.Any(x => string.Equals(x.FolderId, current.EntityId,
                StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Folder has children: conflict.");
        if (current.Kind is VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder)
        {
            VaultDeletionIntent intent;
            try
            {
                intent = System.Text.Json.JsonSerializer.Deserialize<VaultDeletionIntent>(
                    current.EncryptedPayload)
                    ?? throw new InvalidDataException("Deletion baseline envelope missing.");
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new InvalidDataException("Deletion baseline envelope is malformed.", ex);
            }
            if (intent.Original is null || intent.ManifestSourceAccounts is not
                    { Length: > 0 and <= 63 } owners ||
                owners.Any(x => !StellarAccountAddress.IsValid(x)) ||
                owners.Distinct(StringComparer.Ordinal).Count() != owners.Length ||
                intent.Original.IsFolder != remote.IsFolder ||
                VaultHybridRemoteMutationWorkspace.Fingerprint(new LedgerRecordItem(
                    current.EntityId, intent.Original.Title, intent.Original.Content,
                    intent.Original.FolderId, intent.Original.Tags,
                    intent.Original.IsFolder, intent.Original.UserName,
                    intent.Original.Url, intent.Original.Notes)) != baseline)
                throw new InvalidDataException(
                    "Deletion lacks an authenticated original record and manifest-owner snapshot.");
        }
        if (current.Kind is VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder)
        {
            LedgerRecordDraft draft;
            try
            {
                draft = System.Text.Json.JsonSerializer.Deserialize<LedgerRecordDraft>(current.EncryptedPayload)
                    ?? throw new InvalidDataException("Update draft is missing.");
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new InvalidDataException("Update draft is invalid.", ex);
            }
            if (draft.IsFolder != remote.IsFolder || string.IsNullOrWhiteSpace(draft.Title) ||
                draft.Title.Length > 256 || string.IsNullOrEmpty(draft.Content) ||
                draft.Tags is { Length: > 24 } ||
                draft.Tags?.Any(x => x is null || x.Length > 64) == true ||
                draft.Content.StartsWith(StellarLedgerFileProtocol.SinglePrefix, StringComparison.Ordinal) ||
                draft.Content.StartsWith(StellarLedgerFileProtocol.ManifestPrefix, StringComparison.Ordinal) ||
                draft.Content.StartsWith(StellarLedgerFileProtocol.ChunkPrefix, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Update draft is malformed, changes record kind, or embeds disabled file protocol data.");
            if (!string.IsNullOrEmpty(draft.FolderId))
            {
                if (draft.FolderId.Length != 16 || !draft.FolderId.All(Uri.IsHexDigit) ||
                    !remoteRecords.Any(x => x.IsFolder && x.Id.Equals(draft.FolderId,
                        StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Parent folder is unavailable: conflict.");
                var ancestor = draft.FolderId;
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { current.EntityId };
                while (!string.IsNullOrEmpty(ancestor))
                {
                    if (!visited.Add(ancestor))
                        throw new InvalidOperationException("Remote folder move forms a cycle.");
                    var parent = remoteRecords.Single(x => x.IsFolder &&
                        x.Id.Equals(ancestor, StringComparison.OrdinalIgnoreCase));
                    ancestor = parent.FolderId;
                }
            }
        }
        return new(current.Id, current.EntityId, quote.DraftSha256,
            "Versioned remote record matches the pinned baseline. This is a read-only preflight.");
    }

    /// <summary>
    /// Reserves an explicitly approved remote mutation with durable CAS. No
    /// network call is made here; any later outcome must remain quarantined
    /// until a transaction-specific verifier is available.
    /// </summary>
    public async Task<VaultPendingMutation> ReservePreflightedRemoteMutationAsync(
        VaultSyncApprovalSnapshot quote, string accountId, string networkId,
        IReadOnlyCollection<LedgerRecordItem> remoteRecords, DateTimeOffset now,
        CancellationToken ct = default)
    {
        await PreflightRemoteMutationAsync(quote, accountId, networkId, remoteRecords, now, ct);
        var current = await store.GetAsync(accountId, networkId, quote.MutationId, ct)
            ?? throw new KeyNotFoundException("Quoted mutation disappeared.");
        RequireScope(current, accountId, networkId);
        VaultSyncApprovalGuard.RequireFresh(quote, current, accountId, networkId,
            now, TimeSpan.FromMinutes(5));
        var next = current with { State = VaultSyncState.Submitting };
        if (!await store.TryReplaceAsync(current, next, ct))
            throw new InvalidOperationException("Queue revision changed during reservation.");
        return next;
    }

    /// <summary>Conservatively recover interrupted attempts: never automatically resubmit
    /// an entry once submission intent has been durably written.</summary>
    public async Task<IReadOnlyList<VaultPendingMutation>> ReviewInterruptedAsync(
        string accountId, string networkId, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        var entries = await ScopedEntriesAsync(accountId, networkId, ct);
        return entries.Where(x => x.State is VaultSyncState.Submitting or VaultSyncState.Uncertain)
            .OrderBy(x => x.CreatedUtc).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>After a process crash, convert persisted Submitting intent into
    /// Uncertain. No retry is performed, even if no transaction hash was saved.</summary>
    public async Task<VaultPendingMutation> QuarantineInterruptedAsync(
        string accountId, string networkId, string mutationId, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        var item = await store.GetAsync(accountId, networkId, mutationId, ct)
            ?? throw new KeyNotFoundException("Interrupted mutation not found.");
        RequireScope(item, accountId, networkId);
        if (item.State == VaultSyncState.Uncertain) return item;
        if (item.State != VaultSyncState.Submitting)
            throw new InvalidOperationException("Only interrupted submission attempts can be quarantined.");
        var next = item with { State = VaultSyncState.Uncertain };
        if (!await store.TryReplaceAsync(item, next, ct))
            throw new InvalidOperationException("Queue changed during recovery; review again.");
        return next;
    }

    /// <summary>
    /// Quarantine exactly the interrupted submission revision displayed to
    /// the user. A stale UI snapshot may not change another tab's queue entry.
    /// Durable CAS is the only state transition; never submits to Stellar.
    /// </summary>
    public async Task<VaultPendingMutation> QuarantineInterruptedIfUnchangedAsync(
        VaultPendingMutation expected, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.State != VaultSyncState.Submitting)
            throw new InvalidOperationException("Only recorded Submitting intent may be quarantined.");
        var current = await store.GetAsync(expected.AccountId, expected.NetworkId,
            expected.Id, ct) ?? throw new KeyNotFoundException("Interrupted mutation missing.");
        RequireScope(current, expected.AccountId, expected.NetworkId);
        if (!SameDurableRevision(current, expected) ||
            current.State != VaultSyncState.Submitting)
            throw new InvalidOperationException(
                "Submission changed since the review. Reopen the encrypted queue.");
        var quarantined = current with { State = VaultSyncState.Uncertain };
        if (!await store.TryReplaceAsync(current, quarantined, ct))
            throw new InvalidOperationException(
                "Submission changed during quarantine. Refresh queue before investigating.");
        return quarantined;
    }

    /// <summary>Read-only recovery advice. Missing transaction identity never authorizes replay.</summary>
    /// <summary>Enumerate unresolved submissions without changing durable state.</summary>
    public async Task<IReadOnlyList<VaultRecoveryInspection>> InspectOutstandingRecoveriesAsync(
        string accountId, string networkId, CancellationToken ct = default)
    {
        var entries = await ScopedEntriesAsync(accountId, networkId, ct);
        var outstanding = entries.Where(x =>
            x.State is VaultSyncState.Submitting or VaultSyncState.Uncertain);
        return outstanding.Select(x => new VaultRecoveryInspection(x.Id, x.State,
            x.TransactionHash is { Length: 64 } hash && IsTransactionHash(hash) ? hash : null,
            true, x.TransactionHash is { Length: 64 } tx && IsTransactionHash(tx)
                ? "Verify the recorded transaction against the intended record. Never resubmit automatically."
                : "Transaction hash unavailable. Investigate ledger history manually; never resubmit automatically."
        )).ToArray();
    }

    public async Task<VaultRecoveryInspection> InspectRecoveryAsync(
        string accountId, string networkId, string mutationId, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        var item = await store.GetAsync(accountId, networkId, mutationId, ct)
            ?? throw new KeyNotFoundException("Mutation not found.");
        RequireScope(item, accountId, networkId);
        var unresolved = item.State is VaultSyncState.Submitting or VaultSyncState.Uncertain;
        var hasHash = item.TransactionHash is string hash && IsTransactionHash(hash);
        var guidance = unresolved
            ? hasHash
                ? "Submission unresolved. Independently reconcile the recorded transaction hash; do not resubmit."
                : "Submission unresolved without a recorded transaction hash. Investigate ledger history manually; do not resubmit."
            : "No unresolved submission detected.";
        return new VaultRecoveryInspection(item.Id, item.State,
            hasHash ? item.TransactionHash : null, unresolved, guidance);
    }

    public async Task<bool> NeedsManualRecoveryAsync(string accountId, string networkId, string mutationId, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        var item = await store.GetAsync(accountId, networkId, mutationId, ct)
            ?? throw new KeyNotFoundException("Mutation not found.");
        RequireScope(item, accountId, networkId);
        return item.State is VaultSyncState.Submitting or VaultSyncState.Uncertain;
    }

    /// <summary>
    /// Persist the signed final-manifest identity while the submission is still
    /// Submitting and BEFORE the transport broadcasts that manifest.
    /// The first valid hash wins and the full durable revision must match.
    /// This is not confirmation and does not authorize retransmission.
    /// </summary>
    public Task<VaultPendingMutation> CheckpointSignedCreateManifestAsync(
        VaultPendingMutation expectedSubmitting, string signedManifestHash,
        CancellationToken ct = default) =>
        CheckpointSignedPrimaryManifestAsync(expectedSubmitting, signedManifestHash, ct);

    /// <summary>Shared create / Testnet update primary manifest signed-hash
    /// checkpoint. DELETE is excluded because it can require several manifest
    /// tombstone transactions with distinct hashes.</summary>
    public async Task<VaultPendingMutation> CheckpointSignedPrimaryManifestAsync(
        VaultPendingMutation expectedSubmitting, string signedManifestHash,
        CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        ArgumentNullException.ThrowIfNull(expectedSubmitting);
        if (expectedSubmitting.Kind is not (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder or
                VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder) ||
            expectedSubmitting.State != VaultSyncState.Submitting ||
            expectedSubmitting.TransactionHash is not null ||
            !IsTransactionHash(signedManifestHash))
            throw new InvalidOperationException(
                "Only an exact signed primary manifest transaction may be checkpointed.");
        var current = await store.GetAsync(
            expectedSubmitting.AccountId, expectedSubmitting.NetworkId,
            expectedSubmitting.Id, ct)
            ?? throw new KeyNotFoundException("Reserved create mutation was removed.");
        RequireScope(current, expectedSubmitting.AccountId, expectedSubmitting.NetworkId);
        if (current.State != VaultSyncState.Submitting ||
            current.TransactionHash is not null ||
            !SameDurableRevision(current, expectedSubmitting))
            throw new InvalidOperationException(
                "Reserved mutation changed before signed manifest checkpoint; abort broadcast.");
        var next = current with
        {
            TransactionHash = signedManifestHash.ToLowerInvariant()
        };
        if (!await store.TryReplaceAsync(current, next, ct))
            throw new InvalidOperationException(
                "Signed manifest hash could not be persisted before broadcast.");
        return next;
    }

    /// <summary>
    /// Checkpoint one fully signed manifest tombstone before it is broadcast.
    /// Every owner may be recorded only once, in signed/broadcast order.
    /// The queue remains Submitting; a checkpoint is NEVER receipt evidence.
    /// </summary>
    public async Task<VaultPendingMutation> CheckpointSignedDeletionManifestAsync(
        VaultPendingMutation expected, string sourceAccount, string signedHash,
        CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Kind is not (VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder) ||
            expected.State != VaultSyncState.Submitting ||
            expected.TransactionHash is not null ||
            !StellarAccountAddress.IsValid(sourceAccount) ||
            signedHash is not { Length: 64 } ||
            !signedHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidOperationException("Invalid signed deletion manifest checkpoint.");
        VaultDeletionIntent? intent;
        try
        {
            intent = System.Text.Json.JsonSerializer.Deserialize<VaultDeletionIntent>(
                expected.EncryptedPayload);
        }
        catch (System.Text.Json.JsonException) { intent = null; }
        var owners = intent?.ManifestSourceAccounts;
        if (owners is not { Length: > 0 and <= 63 } ||
            owners.Distinct(StringComparer.Ordinal).Count() != owners.Length ||
            !owners.Contains(sourceAccount, StringComparer.Ordinal))
            throw new InvalidOperationException("Signed tombstone is not from a staged HD manifest owner.");
        var journal = expected.SignedDeletionManifests ?? [];
        if (journal.Length >= owners.Length ||
            journal.Any(x => x.SourceAccountId == sourceAccount ||
                x.TransactionHash == signedHash) ||
            journal.Any(x => !owners.Contains(x.SourceAccountId, StringComparer.Ordinal) ||
                !IsTransactionHash(x.TransactionHash)) ||
            journal.Select(x => x.SourceAccountId).Distinct(StringComparer.Ordinal).Count() != journal.Length)
            throw new InvalidOperationException("Deletion manifest checkpoint journal is ambiguous.");
        var current = await store.GetAsync(expected.AccountId, expected.NetworkId,
            expected.Id, ct) ?? throw new KeyNotFoundException("Reserved deletion is missing.");
        RequireScope(current, expected.AccountId, expected.NetworkId);
        if (!SameDurableRevision(current, expected) ||
            current.State != VaultSyncState.Submitting)
            throw new InvalidOperationException("Deletion journal was changed; do not broadcast.");
        var next = current with
        {
            SignedDeletionManifests = [..journal,
                new VaultSignedDeletionManifest(sourceAccount, signedHash)]
        };
        if (!await store.TryReplaceAsync(current, next, ct))
            throw new InvalidOperationException("Deletion checkpoint could not be persisted; do not broadcast.");
        return next;
    }

    /// <summary>
    /// CAS-reserve a pristine hybrid file upload. This does not sign or broadcast.
    /// A reserved upload without a durable signed hash is still uncertain after
    /// interruption; it must never be automatically retried.
    /// </summary>
    public async Task<VaultPendingMutation> ReserveFileUploadAsync(
        VaultPendingMutation expected, CancellationToken ct = default)
    {
        VaultHybridFilePolicy.RequireEnabled();
        retention.RequireLocalPersistence();
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Kind != VaultMutationKind.UploadFile ||
            expected.State != VaultSyncState.Pending ||
            expected.TransactionHash is not null ||
            expected.SignedFileEntries is { Length: > 0 })
            throw new InvalidOperationException("Only a pristine file draft may be reserved.");
        var review = await PrepareReviewAsync(expected.AccountId, expected.NetworkId, ct);
        if (review.NeedsReconciliation != 0 || review.Conflicts != 0 ||
            !review.Ready.Any(x => x.Id == expected.Id))
            throw new InvalidOperationException("File upload dependencies or earlier submissions are unresolved.");
        var current = await store.GetAsync(expected.AccountId, expected.NetworkId,
            expected.Id, ct) ?? throw new KeyNotFoundException("File upload missing.");
        RequireScope(current, expected.AccountId, expected.NetworkId);
        if (!SameDurableRevision(current, expected) ||
            current.Kind != VaultMutationKind.UploadFile ||
            current.State != VaultSyncState.Pending)
            throw new InvalidOperationException("File upload changed before reservation.");
        var reserved = current with { State = VaultSyncState.Submitting };
        if (!await store.TryReplaceAsync(current, reserved, ct))
            throw new InvalidOperationException("File reservation lost the durable CAS race.");
        return reserved;
    }

    /// <summary>
    /// An explicitly approved, independently verified file step can advance
    /// from Uncertain to Submitting. This method never verifies receipts itself:
    /// its caller MUST verify every existing signed hash and the current ledger
    /// snapshot before invoking it. CAS prevents stale devices from advancing.
    /// </summary>
    public async Task<VaultPendingMutation> ReserveVerifiedNextFileEntryAsync(
        VaultPendingMutation expected, CancellationToken ct = default)
    {
        VaultHybridFilePolicy.RequireEnabled();
        retention.RequireLocalPersistence();
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Kind != VaultMutationKind.UploadFile ||
            expected.State != VaultSyncState.Uncertain ||
            expected.TransactionHash is not null ||
            expected.SignedFileEntries is not { Length: > 0 } journal)
            throw new InvalidOperationException("No prior signed file entry to continue.");
        var current = await store.GetAsync(expected.AccountId, expected.NetworkId,
            expected.Id, ct) ?? throw new KeyNotFoundException("File upload missing.");
        RequireScope(current, expected.AccountId, expected.NetworkId);
        if (!SameDurableRevision(current, expected))
            throw new InvalidOperationException("File journal changed before continuation.");
        var reserved = current with { State = VaultSyncState.Submitting };
        if (!await store.TryReplaceAsync(current, reserved, ct))
            throw new InvalidOperationException("File continuation lost durable CAS.");
        return reserved;
    }

    /// <summary>
    /// CAS-confirm an upload only after the caller has independently verified
    /// EVERY signed file part and final manifest against historical receipts
    /// and fresh current contents. This method NEVER submits a transaction.
    /// </summary>
    public async Task<VaultPendingMutation> ConfirmVerifiedFileUploadAsync(
        VaultPendingMutation expected, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Kind != VaultMutationKind.UploadFile ||
            expected.State != VaultSyncState.Uncertain ||
            expected.TransactionHash is not null ||
            expected.SignedFileEntries is not { Length: > 0 })
            throw new InvalidOperationException("File upload is not awaiting final verification.");
        var current = await store.GetAsync(expected.AccountId, expected.NetworkId,
            expected.Id, ct) ?? throw new KeyNotFoundException("File upload missing.");
        RequireScope(current, expected.AccountId, expected.NetworkId);
        if (!SameDurableRevision(current, expected))
            throw new InvalidOperationException("File upload changed during final reconciliation.");
        var confirmed = current with { State = VaultSyncState.Confirmed };
        if (!await store.TryReplaceAsync(current, confirmed, ct))
            throw new InvalidOperationException("File confirmation lost the durable CAS race.");
        return confirmed;
    }

    /// <summary>
    /// The file publisher may checkpoint ONLY the next immutable entry of a
    /// reserved, encrypted UploadFile intent, in SVFILE publication order.
    /// The last entry is the manifest. No network write occurs here.
    /// If durable CAS fails, the signer MUST NOT broadcast that transaction.
    /// </summary>
    public async Task<VaultPendingMutation> CheckpointSignedFileEntryAsync(
        VaultPendingMutation expected, string recordId, string signedHash,
        CancellationToken ct = default)
    {
        VaultHybridFilePolicy.RequireEnabled();
        retention.RequireLocalPersistence();
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Kind != VaultMutationKind.UploadFile ||
            expected.State != VaultSyncState.Submitting ||
            expected.TransactionHash is not null ||
            expected.SignedDeletionManifests is { Length: > 0 } ||
            recordId is not { Length: 16 } ||
            !recordId.All(Uri.IsHexDigit) ||
            signedHash is not { Length: 64 } ||
            !signedHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidOperationException("Invalid signed file part checkpoint.");

        VaultHybridFileWorkspace.FileUploadIntent? intent;
        try
        {
            intent = System.Text.Json.JsonSerializer.Deserialize<
                VaultHybridFileWorkspace.FileUploadIntent>(expected.EncryptedPayload);
        }
        catch (System.Text.Json.JsonException) { intent = null; }
        var ids = intent?.EntryIds;
        if (intent is null || intent.Version != 1 ||
            intent.Content is not { Length: > 0 and <= StellarLedgerFileProtocol.MaxFileBytes } ||
            ids is not { Length: > 0 and <= StellarLedgerFileProtocol.MaxParts + 1 } ||
            ids.Any(x => x is not { Length: 16 } || !x.All(Uri.IsHexDigit)) ||
            ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length ||
            !string.Equals(ids[^1], expected.EntityId, StringComparison.OrdinalIgnoreCase))
        {
            if (intent?.Content is { } leaked)
                CryptographicOperations.ZeroMemory(leaked);
            throw new InvalidDataException("File upload intent or part identity is invalid.");
        }
        try
        {
            var expectedCount = intent.Content.Length <= StellarLedgerFileProtocol.MaxChunkBytes
                ? 1
                : (intent.Content.Length + StellarLedgerFileProtocol.MaxChunkBytes - 1) /
                    StellarLedgerFileProtocol.MaxChunkBytes + 1;
            var expectedFingerprint = Convert.ToHexString(
                SHA256.HashData(intent.Content));
            if (ids.Length != expectedCount ||
                !string.Equals(expectedFingerprint, intent.Fingerprint,
                    StringComparison.Ordinal))
                throw new InvalidDataException("File upload bytes or part count have changed.");

            var journal = expected.SignedFileEntries ?? [];
            if (journal.Length >= ids.Length ||
                journal.Where((entry, i) =>
                    entry is null ||
                    !string.Equals(entry.LedgerRecordId, ids[i],
                        StringComparison.OrdinalIgnoreCase) ||
                    entry.TransactionHash is not { Length: 64 } ||
                    !entry.TransactionHash.All(Uri.IsHexDigit))
                    .Any() ||
                journal.Select(x => x.TransactionHash)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Length ||
                !string.Equals(ids[journal.Length], recordId,
                    StringComparison.OrdinalIgnoreCase) ||
                journal.Any(x => string.Equals(
                    x.TransactionHash, signedHash, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "Signed file journal is out of order or already contains this transaction.");

            var current = await store.GetAsync(expected.AccountId, expected.NetworkId,
                expected.Id, ct) ?? throw new KeyNotFoundException("Reserved file upload is missing.");
            RequireScope(current, expected.AccountId, expected.NetworkId);
            if (current.State != VaultSyncState.Submitting ||
                !SameDurableRevision(current, expected))
                throw new InvalidOperationException(
                    "File queue changed before signed hash checkpoint; abort broadcast.");
            var updated = current with
            {
                SignedFileEntries = [..journal, new VaultSignedFileEntry(
                    ids[journal.Length], signedHash)]
            };
            if (!await store.TryReplaceAsync(current, updated, ct))
                throw new InvalidOperationException(
                    "Signed file part could not be stored durably; abort broadcast.");
            return updated;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(intent.Content);
        }
    }

    public async Task<VaultPendingMutation> MarkUncertainAsync(
        VaultPendingMutation submitting, string? transactionHash, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        if (submitting.State != VaultSyncState.Submitting)
            throw new InvalidOperationException("Only submitting mutations can become uncertain.");
        if (transactionHash is not null && !IsTransactionHash(transactionHash))
            throw new ArgumentException("Expected a 64-character Stellar transaction hash.", nameof(transactionHash));
        if (submitting.TransactionHash is not null && transactionHash is not null &&
            !string.Equals(transactionHash, submitting.TransactionHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cannot replace a previously recorded transaction hash.");
        var durable = await store.GetAsync(submitting.AccountId, submitting.NetworkId, submitting.Id, ct)
            ?? throw new KeyNotFoundException("Submission no longer exists.");
        RequireScope(durable, submitting.AccountId, submitting.NetworkId);
        if (durable.State != VaultSyncState.Submitting)
            throw new InvalidOperationException("Submission state changed; inspect the queue.");
        // A caller must not advance another tab's revised submission, even if
        // its state and transaction hash happen to be unchanged.
        if (!SameDurableRevision(durable, submitting))
            throw new InvalidOperationException("Submission revision changed; reconcile the durable queue before proceeding.");
        if (durable.TransactionHash is not null && transactionHash is not null &&
            !string.Equals(durable.TransactionHash, transactionHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Transaction hash changed; manual investigation required.");
        // A missing response must never erase a transaction hash already persisted.
        var next = durable with {
            State = VaultSyncState.Uncertain,
            TransactionHash = durable.TransactionHash ?? transactionHash
        };
        if (!await store.TryReplaceAsync(durable, next, ct))
            throw new InvalidOperationException("Concurrent queue change; reconcile manually.");
        return next;
    }

    /// <summary>Read all device-only changes, including blocked and uncertain states.
    /// This never performs network submission or treats drafts as ledger records.</summary>
    public async Task<IReadOnlyList<VaultPendingMutation>> ReviewAllAsync(
        string accountId, string networkId, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        return await ScopedEntriesAsync(accountId, networkId, ct);
    }

    public async Task<VaultQueueStatus> StatusAsync(string accountId, string networkId,
        CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        var items = await ScopedEntriesAsync(accountId, networkId, ct);
        return new VaultQueueStatus(
            items.Count(x => x.State == VaultSyncState.Pending),
            items.Count(x => x.State == VaultSyncState.Submitting),
            items.Count(x => x.State == VaultSyncState.Uncertain),
            items.Count(x => x.State == VaultSyncState.Confirmed),
            items.Count(x => x.State == VaultSyncState.Conflict),
            items.Count(x => x.State == VaultSyncState.Failed));
    }

    /// <summary>Revise a device-only draft without changing its mutation identity,
    /// dependencies, intended entity, or original ordering timestamp.</summary>
    public async Task<VaultPendingMutation> RevisePendingAsync(
        string accountId, string networkId, string mutationId, byte[] replacementPayload,
        CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        if (replacementPayload is not { Length: > 0 } || replacementPayload.Length > 8 * 1024 * 1024)
            throw new ArgumentException("Invalid revised draft size.", nameof(replacementPayload));
        var previous = await store.GetAsync(accountId, networkId, mutationId, ct)
            ?? throw new KeyNotFoundException("Local draft not found.");
        RequireScope(previous, accountId, networkId);
        if (previous.State != VaultSyncState.Pending || previous.TransactionHash is not null)
            throw new InvalidOperationException("Only unsubmitted pending drafts can be edited.");
        var revised = previous with { EncryptedPayload = replacementPayload.ToArray() };
        if (!await store.TryReplaceAsync(previous, revised, ct))
            throw new InvalidOperationException("Local draft changed concurrently; reopen it before saving.");
        return revised;
    }

    /// <summary>
    /// Atomically revise a pending local CREATE draft and its parent dependency
    /// in one durable store CAS. This does not submit Stellar transactions.
    /// Prevents stale-editor overwrites, local folder cycles and dangling
    /// dependency IDs. A cross-tab change after the read still fails closed
    /// at later review/preflight; this is not a multi-entry transaction.
    /// </summary>
    public async Task<VaultPendingMutation> RevisePendingCreateAsync(
        VaultPendingMutation expected, byte[] replacementPayload,
        string[] replacementDependencies, string expectedPayloadSha256,
        CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(replacementDependencies);
        if (replacementPayload is not { Length: > 0 } ||
            replacementPayload.Length > 8 * 1024 * 1024 ||
            replacementDependencies.Length > 1 ||
            replacementDependencies.Any(x => !Guid.TryParseExact(x, "N", out _)))
            throw new ArgumentException("Invalid revised local create or parent dependency.");
        var all = await ScopedEntriesAsync(expected.AccountId, expected.NetworkId, ct);
        var previous = await store.GetAsync(expected.AccountId, expected.NetworkId, expected.Id, ct)
            ?? throw new KeyNotFoundException("Pending local create was removed.");
        RequireScope(previous, expected.AccountId, expected.NetworkId);
        if (expectedPayloadSha256 is not { Length: 64 } ||
            !expectedPayloadSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(previous.EncryptedPayload)),
                expectedPayloadSha256, StringComparison.Ordinal) ||
            !SameDurableRevision(previous, expected) ||
            previous.State != VaultSyncState.Pending ||
            previous.TransactionHash is not null ||
            previous.Kind is not (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder))
            throw new InvalidOperationException(
                "Local create changed since it was opened or is no longer editable.");

        LedgerRecordDraft draft;
        try
        {
            draft = System.Text.Json.JsonSerializer.Deserialize<LedgerRecordDraft>(replacementPayload)
                ?? throw new InvalidDataException("Updated local draft was empty.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidDataException("Updated local draft payload is invalid.", ex);
        }
        // Tree nodes form an immutable authenticated graph. Ordinary draft
        // revision may not rewrite a leaf, branch, or root after staging.
        try
        {
            var previousDraft = System.Text.Json.JsonSerializer.Deserialize<
                LedgerRecordDraft>(previous.EncryptedPayload);
            if (VaultHdTreeFilePolicy.IsTreeDraft(previousDraft))
                throw new InvalidOperationException(
                    "Encrypted file-tree nodes are immutable. Stage a new complete file instead.");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new InvalidDataException("Existing encrypted file draft is malformed.");
        }
        var candidate = previous with
        {
            EncryptedPayload = replacementPayload.ToArray(),
            DependsOn = replacementDependencies.ToArray()
        };
        var issues = VaultQueuedCreatePolicy.Problems(candidate, all);
        if (issues.Any(x => x != "unpublished-local-parent-folder"))
            throw new InvalidOperationException(
                "Invalid local create: " + string.Join(", ", issues));

        VaultPendingMutation? localParent = null;
        if (!string.IsNullOrEmpty(draft.FolderId))
        {
            var matches = VaultQueuedCreatePolicy.ParentCandidates(
                all, draft.FolderId, previous.Id);
            if (matches.Length > 1)
                throw new InvalidOperationException("Parent record ID is ambiguous.");
            localParent = matches.SingleOrDefault();
            if (localParent is not null && localParent.Kind != VaultMutationKind.CreateFolder)
                throw new InvalidOperationException("Parent is not a created folder.");
        }
        if (localParent is null || localParent.State == VaultSyncState.Confirmed)
        {
            if (candidate.DependsOn.Length != 0)
                throw new InvalidOperationException(
                    "Remote or confirmed parents must not have pending-local dependencies.");
        }
        else
        {
            if (localParent.State != VaultSyncState.Pending ||
                localParent.TransactionHash is not null ||
                candidate.DependsOn.Length != 1 ||
                candidate.DependsOn[0] != localParent.Id)
                throw new InvalidOperationException(
                    "Pending parent folder must have exactly its own queue dependency.");
        }

        // Detect cycles using queued dependency edges even for parents that
        // have been reparented by a different editor. No local self-ancestor
        // may become a parent after this revision.
        var byId = all.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { candidate.Id };
        var cursor = candidate.DependsOn.SingleOrDefault();
        while (cursor is not null)
        {
            if (!visited.Add(cursor) || !byId.TryGetValue(cursor, out var ancestor))
                throw new InvalidOperationException("Folder dependency cycle or missing ancestor.");
            if (ancestor.Kind != VaultMutationKind.CreateFolder ||
                ancestor.State is VaultSyncState.Failed or VaultSyncState.Conflict)
                throw new InvalidOperationException("Folder ancestor is not publishable.");
            cursor = ancestor.DependsOn.SingleOrDefault();
        }

        if (!await store.TryReplaceAsync(previous, candidate, ct))
            throw new InvalidOperationException(
                "Local draft changed while reparenting; reopen it before saving.");
        return candidate;
    }

    /// <summary>Revise an existing version-pinned UPDATE only when its
    /// originally opened payload digest still matches the durable queue.
    /// A stale tab must never overwrite a newer local update.</summary>
    public async Task<VaultPendingMutation> RevisePendingUpdateIfUnchangedAsync(
        string accountId, string networkId, string mutationId,
        string expectedPayloadSha256, byte[] replacementPayload,
        CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        if (expectedPayloadSha256 is not { Length: 64 } ||
            !expectedPayloadSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            replacementPayload is not { Length: > 0 } ||
            replacementPayload.Length > 8 * 1024 * 1024)
            throw new ArgumentException("Invalid pending update revision.");
        var previous = await store.GetAsync(accountId, networkId, mutationId, ct)
            ?? throw new KeyNotFoundException("Local update no longer exists.");
        RequireScope(previous, accountId, networkId);
        if (previous.State != VaultSyncState.Pending ||
            previous.TransactionHash is not null ||
            previous.Kind is not (VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder) ||
            !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(previous.EncryptedPayload)),
                expectedPayloadSha256, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Offline update changed since it was opened. Refresh before editing.");
        var candidate = previous with { EncryptedPayload = replacementPayload.ToArray() };
        if (!await store.TryReplaceAsync(previous, candidate, ct))
            throw new InvalidOperationException(
                "Offline update was changed by another editor. Refresh before saving.");
        return candidate;
    }

    /// <summary>Explicitly migrate a legacy 32-character draft entity ID.
    /// Never silently truncate: dependent drafts must first be handled separately.</summary>
    public async Task<VaultPendingMutation> MigrateLegacyCreateIdAsync(
        string accountId, string networkId, string mutationId, string newRecordId,
        CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        if (newRecordId.Length != 16 || !newRecordId.All(Uri.IsHexDigit))
            throw new ArgumentException("A new 16-character hexadecimal record ID is required.", nameof(newRecordId));
        var entries = await ScopedEntriesAsync(accountId, networkId, ct);
        var previous = entries.SingleOrDefault(x => x.Id == mutationId)
            ?? throw new KeyNotFoundException("Local draft not found.");
        if (previous.State != VaultSyncState.Pending || previous.TransactionHash is not null ||
            previous.Kind is not (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder) ||
            previous.EntityId.Length != 32 || !Guid.TryParseExact(previous.EntityId, "N", out _))
            throw new InvalidOperationException("Only untouched legacy create drafts can be migrated.");
        if (entries.Any(x => x.Id != mutationId &&
            (x.EntityId == previous.EntityId ||
             x.DependsOn.Contains(mutationId, StringComparer.Ordinal) ||
             System.Text.Encoding.UTF8.GetString(x.EncryptedPayload).Contains(previous.EntityId, StringComparison.Ordinal))))
            throw new InvalidOperationException("Dependent drafts exist. Migrate references before changing this ID.");
        if (entries.Any(x => x.EntityId == newRecordId))
            throw new InvalidOperationException("The target record ID already exists in the local queue.");
        var revised = previous with { EntityId = newRecordId };
        if (!await store.TryReplaceAsync(previous, revised, ct))
            throw new InvalidOperationException("Draft changed concurrently. Refresh and try again.");
        return revised;
    }

    /// <summary>Discard only an unsubmitted draft, and only when no other active
    /// mutation depends on its identifier. Deletion is never a ledger operation.</summary>
    public async Task DiscardPendingAsync(string accountId, string networkId,
        string mutationId, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        var entries = await ScopedEntriesAsync(accountId, networkId, ct);
        var current = entries.SingleOrDefault(x => x.Id == mutationId)
            ?? throw new KeyNotFoundException("Local draft not found.");
        if (current.State != VaultSyncState.Pending || current.TransactionHash is not null ||
            current.SignedFileEntries is { Length: > 0 })
            throw new InvalidOperationException("Submitted, uncertain or resolved changes cannot be discarded.");
        if (entries.Any(x => x.Id != mutationId &&
            x.State is not (VaultSyncState.Confirmed or VaultSyncState.Failed) &&
            x.DependsOn.Contains(mutationId, StringComparer.Ordinal)))
            throw new InvalidOperationException("Another pending change depends on this draft.");
        if (VaultLinkedTreeFilePolicy.BlocksDiscard(current, entries))
            throw new InvalidOperationException(
                "A pending HD file-tree root still references this node, or a confirmed child still requires its root. Resolve the entire file group before discarding.");
        if (!await store.TryDeleteAsync(current, ct))
            throw new InvalidOperationException("Draft changed while discarding; refresh the local queue.");
    }

    public async Task<VaultPendingMutation> MarkConflictAsync(VaultPendingMutation previous,
        CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        if (previous.State != VaultSyncState.Pending)
            throw new InvalidOperationException("Only pending changes can be marked as conflicts.");
        var next = previous with { State = VaultSyncState.Conflict };
        if (!await store.TryReplaceAsync(previous, next, ct))
            throw new InvalidOperationException("Concurrent queue update; review again.");
        return next;
    }

    private async Task<IReadOnlyList<VaultPendingMutation>> ScopedEntriesAsync(
        string accountId, string networkId, CancellationToken ct)
    {
        StellarAccountAddress.Validate(accountId);
        if (string.IsNullOrWhiteSpace(networkId))
            throw new ArgumentException("Network ID required.", nameof(networkId));
        var entries = await store.ListAsync(accountId, networkId, ct);
        foreach (var item in entries)
        {
            if (item.AccountId != accountId || item.NetworkId != networkId)
                throw new InvalidDataException("Queue entry belongs to another account or network.");
            VaultMutationStrategyPolicy.Validate(item.OriginStrategyId);
        }
        return entries;
    }

    private static void RequireScope(VaultPendingMutation item, string accountId, string networkId)
    {
        if (item.AccountId != accountId || item.NetworkId != networkId)
            throw new InvalidDataException("Queue entry belongs to another account or network.");
        // Current durable mutation transitions, including confirmation, use
        // the HD ledger verifier and local vault key. Linked-origin items are
        // inspectable but cannot enter these state-changing methods.
        VaultMutationStrategyPolicy.RequireHd(item);
    }

    private static bool SameDurableRevision(VaultPendingMutation a, VaultPendingMutation b) =>
        a.Id == b.Id && a.AccountId == b.AccountId && a.NetworkId == b.NetworkId &&
        a.Kind == b.Kind && a.EntityId == b.EntityId &&
        a.OriginStrategyId == b.OriginStrategyId &&
        a.EncryptedPayload is not null && b.EncryptedPayload is not null &&
        a.EncryptedPayload.AsSpan().SequenceEqual(b.EncryptedPayload) &&
        a.ExpectedRemoteVersion == b.ExpectedRemoteVersion &&
        a.DependsOn is not null && b.DependsOn is not null &&
        a.DependsOn.SequenceEqual(b.DependsOn, StringComparer.Ordinal) &&
        a.State == b.State && a.TransactionHash == b.TransactionHash &&
        a.CreatedUtc == b.CreatedUtc &&
        (a.SignedDeletionManifests ?? []).SequenceEqual(
            b.SignedDeletionManifests ?? []) &&
        (a.SignedFileEntries ?? []).SequenceEqual(
            b.SignedFileEntries ?? []);

    private static bool IsTransactionHash(string hash) =>
        hash.Length == 64 && hash.All(Uri.IsHexDigit);

    /// <summary>Check an uncertain transaction against the configured independent verifier.
    /// Missing hashes and unverifiable results remain uncertain and are never replayed.</summary>
    public async Task<VaultReconciliationResult> ReconcileAsync(
        string accountId, string networkId, string mutationId, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        var previous = await store.GetAsync(accountId, networkId, mutationId, ct)
            ?? throw new KeyNotFoundException("Mutation not found.");
        RequireScope(previous, accountId, networkId);
        if (previous.State is not (VaultSyncState.Uncertain or VaultSyncState.Submitting))
            return new VaultReconciliationResult(previous.Id, previous.State, false,
                "This entry has no outstanding submission to reconcile.");
        if (previous.TransactionHash is not string hash || !IsTransactionHash(hash))
            return new VaultReconciliationResult(previous.Id, previous.State, false,
                "No verified transaction hash is available; manual investigation required.");
        // Route every confirmation through the same verifier and CAS transition.
        // A receipt lookup by itself must never flip the durable queue state.
        try
        {
            var confirmed = await ConfirmReconciledAsync(previous, hash, ct);
            return new VaultReconciliationResult(confirmed.Id, confirmed.State, true,
                "Mutation independently verified and marked confirmed.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Ledger confirmation", StringComparison.Ordinal))
        {
            return new VaultReconciliationResult(previous.Id, previous.State, false,
                "The configured ledger verifier did not confirm this mutation.");
        }
    }

    public async Task<VaultPendingMutation> ConfirmReconciledAsync(
        VaultPendingMutation previous, string transactionHash, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        if (previous.State is not (VaultSyncState.Uncertain or VaultSyncState.Submitting) ||
            !IsTransactionHash(transactionHash) ||
            previous.TransactionHash is not { Length: 64 } storedHash ||
            !IsTransactionHash(storedHash))
            throw new InvalidOperationException(
                "Confirmation requires the exact hash durably recorded when submission completed.");
        if (!string.Equals(storedHash, transactionHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Reconciliation hash differs from the original submitted transaction.");
        // Always verify the exact durable revision rather than trusting a caller's
        // possibly stale mutation object.
        var current = await store.GetAsync(previous.AccountId, previous.NetworkId, previous.Id, ct)
            ?? throw new KeyNotFoundException("Mutation disappeared before reconciliation.");
        RequireScope(current, previous.AccountId, previous.NetworkId);
        if (!SameDurableRevision(current, previous))
            throw new InvalidOperationException("Queue revision changed during reconciliation; inspect and retry the review.");
        if (current.State is not (VaultSyncState.Uncertain or VaultSyncState.Submitting) ||
            current.TransactionHash is not null &&
            !string.Equals(current.TransactionHash, transactionHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Submission state or transaction hash changed.");
        var sameScope = await ScopedEntriesAsync(current.AccountId, current.NetworkId, ct);
        if (sameScope.Any(x => x.Id != current.Id && x.TransactionHash is string hash &&
            string.Equals(hash, transactionHash, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Transaction hash is already assigned to another queue mutation.");
        if (!await verifier.IsConfirmedAsync(current, transactionHash, ct))
            throw new InvalidOperationException("Ledger confirmation did not match the intended mutation.");
        var next = current with { State = VaultSyncState.Confirmed, TransactionHash = transactionHash };
        if (!await store.TryReplaceAsync(current, next, ct))
            throw new InvalidOperationException("Concurrent queue change; reconcile manually.");
        return next;
    }
}

public sealed record VaultRecoveryInspection(string MutationId, VaultSyncState State,
    string? TransactionHash, bool NeedsInvestigation, string Guidance);
