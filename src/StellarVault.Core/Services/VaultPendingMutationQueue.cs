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

    public async Task<VaultPendingMutation> MarkUncertainAsync(
        VaultPendingMutation submitting, string? transactionHash, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        if (submitting.State != VaultSyncState.Submitting)
            throw new InvalidOperationException("Only submitting mutations can become uncertain.");
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

    public async Task<VaultPendingMutation> ConfirmReconciledAsync(
        VaultPendingMutation previous, string transactionHash, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        if (previous.State is not (VaultSyncState.Uncertain or VaultSyncState.Submitting) ||
            string.IsNullOrWhiteSpace(transactionHash))
            throw new InvalidOperationException("A submitted mutation and verified transaction hash are required.");
        if (!await verifier.IsConfirmedAsync(previous, transactionHash, ct))
            throw new InvalidOperationException("Ledger confirmation did not match the intended mutation.");
        var next = previous with { State = VaultSyncState.Confirmed, TransactionHash = transactionHash };
        if (!await store.TryReplaceAsync(previous, next, ct))
            throw new InvalidOperationException("Concurrent queue change; reconcile manually.");
        return next;
    }
}
