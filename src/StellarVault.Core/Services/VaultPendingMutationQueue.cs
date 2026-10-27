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

public enum VaultMutationKind { CreateRecord, UpdateRecord, DeleteRecord, CreateFolder, UpdateFolder, DeleteFolder, UploadFile, DeleteFile }

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
    DateTimeOffset CreatedUtc)
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
        if (entry.State != VaultSyncState.Pending || entry.TransactionHash is not null)
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
            x.DependsOn.All(id => byId.TryGetValue(id, out var dependency) &&
                dependency.State == VaultSyncState.Confirmed))
            .OrderBy(x => x.CreatedUtc).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Read-only preparation report. It never estimates fees or submits writes.</summary>
    public async Task<VaultSyncReview> PrepareReviewAsync(string accountId, string networkId,
        CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        var all = await ScopedEntriesAsync(accountId, networkId, ct);
        var byId = all.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var ready = new List<VaultPendingMutation>();
        var blocked = new List<VaultSyncBlockedMutation>();
        foreach (var item in all.Where(x => x.State == VaultSyncState.Pending))
        {
            var missing = item.DependsOn.Where(id => !byId.ContainsKey(id)).ToArray();
            var waiting = item.DependsOn.Where(id => byId.TryGetValue(id, out var parent) &&
                parent.State != VaultSyncState.Confirmed).ToArray();
            // New ledger records require 16 hexadecimal characters. Legacy
            // device draft IDs are never silently truncated or published.
            var invalidCreateId = item.Kind is VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder &&
                (item.EntityId.Length != 16 || !item.EntityId.All(Uri.IsHexDigit));
            if (invalidCreateId)
                blocked.Add(new VaultSyncBlockedMutation(item.Id, ["invalid-ledger-record-id"], waiting));
            else if (missing.Length == 0 && waiting.Length == 0) ready.Add(item);
            else blocked.Add(new VaultSyncBlockedMutation(item.Id, missing, waiting));
        }
        return new VaultSyncReview(ready.OrderBy(x => x.CreatedUtc).ToArray(),
            blocked.ToArray(), all.Count(x => x.State == VaultSyncState.Uncertain ||
                x.State == VaultSyncState.Submitting),
            all.Count(x => x.State == VaultSyncState.Conflict));
    }

    public async Task<VaultPendingMutation> MarkSubmittingAsync(
        string accountId, string networkId, string mutationId, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        var item = await store.GetAsync(accountId, networkId, mutationId, ct)
            ?? throw new KeyNotFoundException("Pending mutation not found.");
        RequireScope(item, accountId, networkId);
        if (item.State != VaultSyncState.Pending)
            throw new InvalidOperationException("Only pending mutations can enter submission.");
        var ready = await ReviewReadyAsync(accountId, networkId, ct);
        if (!ready.Any(x => x.Id == item.Id))
            throw new InvalidOperationException("Mutation dependencies are not confirmed.");
        // Persist intent BEFORE any external network submission. On a crash,
        // the entry remains Submitting and must be reconciled, never auto-replayed.
        var next = item with { State = VaultSyncState.Submitting };
        if (!await store.TryReplaceAsync(item, next, ct))
            throw new InvalidOperationException("Concurrent queue change; review again.");
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

    public async Task<VaultPendingMutation> MarkUncertainAsync(
        VaultPendingMutation submitting, string? transactionHash, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        if (submitting.State != VaultSyncState.Submitting)
            throw new InvalidOperationException("Only submitting mutations can become uncertain.");
        if (transactionHash is not null && !IsTransactionHash(transactionHash))
            throw new ArgumentException("Expected a 64-character Stellar transaction hash.", nameof(transactionHash));
        if (submitting.TransactionHash is not null && transactionHash != submitting.TransactionHash)
            throw new InvalidOperationException("Cannot replace a previously recorded transaction hash.");
        var next = submitting with { State = VaultSyncState.Uncertain, TransactionHash = transactionHash };
        if (!await store.TryReplaceAsync(submitting, next, ct))
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

    /// <summary>Discard only an unsubmitted draft, and only when no other active
    /// mutation depends on its identifier. Deletion is never a ledger operation.</summary>
    public async Task DiscardPendingAsync(string accountId, string networkId,
        string mutationId, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        var entries = await ScopedEntriesAsync(accountId, networkId, ct);
        var current = entries.SingleOrDefault(x => x.Id == mutationId)
            ?? throw new KeyNotFoundException("Local draft not found.");
        if (current.State != VaultSyncState.Pending || current.TransactionHash is not null)
            throw new InvalidOperationException("Submitted, uncertain or resolved changes cannot be discarded.");
        if (entries.Any(x => x.Id != mutationId &&
            x.State is not (VaultSyncState.Confirmed or VaultSyncState.Failed) &&
            x.DependsOn.Contains(mutationId, StringComparer.Ordinal)))
            throw new InvalidOperationException("Another pending change depends on this draft.");
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
        foreach (var item in entries) RequireScope(item, accountId, networkId);
        return entries;
    }

    private static void RequireScope(VaultPendingMutation item, string accountId, string networkId)
    {
        if (item.AccountId != accountId || item.NetworkId != networkId)
            throw new InvalidDataException("Queue entry belongs to another account or network.");
    }

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
        if (!await verifier.IsConfirmedAsync(previous, hash, ct))
            return new VaultReconciliationResult(previous.Id, previous.State, false,
                "The configured ledger verifier did not confirm this mutation.");
        var confirmed = previous with { State = VaultSyncState.Confirmed };
        if (!await store.TryReplaceAsync(previous, confirmed, ct))
            throw new InvalidOperationException("Queue changed during reconciliation. Review again.");
        return new VaultReconciliationResult(confirmed.Id, confirmed.State, true,
            "Mutation independently verified and marked confirmed.");
    }

    public async Task<VaultPendingMutation> ConfirmReconciledAsync(
        VaultPendingMutation previous, string transactionHash, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        if (previous.State is not (VaultSyncState.Uncertain or VaultSyncState.Submitting) ||
            !IsTransactionHash(transactionHash))
            throw new InvalidOperationException("A submitted mutation and valid Stellar transaction hash are required.");
        if (previous.TransactionHash is not null &&
            !string.Equals(previous.TransactionHash, transactionHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Reconciliation hash differs from the original submitted transaction.");
        if (!await verifier.IsConfirmedAsync(previous, transactionHash, ct))
            throw new InvalidOperationException("Ledger confirmation did not match the intended mutation.");
        var next = previous with { State = VaultSyncState.Confirmed, TransactionHash = transactionHash };
        if (!await store.TryReplaceAsync(previous, next, ct))
            throw new InvalidOperationException("Concurrent queue change; reconcile manually.");
        return next;
    }
}
