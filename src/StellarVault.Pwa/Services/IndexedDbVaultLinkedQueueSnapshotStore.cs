using Microsoft.JSInterop;
using StellarVault.Core.Services;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Read-only S-key queue view reusing the same authenticated IndexedDB
/// AES-GCM queue store and CAS encoding. The key and storage scope are both
/// independent of the HD queue, even for an identical public G-address.
/// </summary>
public sealed class IndexedDbVaultLinkedQueueSnapshotStore(
    IJSRuntime js,
    VaultLinkedQueueKeySession keys,
    VaultDeviceRetentionGate retention,
    VaultLinkedLocalRetentionGate linkedRetention,
    VaultNetworkSelection networks) : IVaultLinkedDraftStore
{
    private IndexedDbVaultPendingMutationStore ExistingStore() =>
        new(js, keys, retention);

    private void RequireEntry(VaultPendingMutation entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        retention.RequireLocalPersistence();
        linkedRetention.RequireLocalPersistence();
        StellarAccountAddress.Validate(entry.AccountId);
        if (entry.NetworkId != VaultLinkedQueueStorageScope.FromNetworkScope(
                networks.CredentialKey(entry.AccountId)) ||
            entry.OriginStrategyId != VaultMutationStrategyPolicy.Linked)
            throw new UnauthorizedAccessException(
                "Independent S-key draft scope or authenticated origin differs from the local encrypted store.");
    }

    public async Task<bool> AddAsync(VaultPendingMutation entry, CancellationToken ct = default)
    {
        RequireEntry(entry);
        if (entry.State != VaultSyncState.Pending || entry.TransactionHash is not null ||
            entry.SignedFileEntries is { Length: > 0 } ||
            entry.Kind is not (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder))
            throw new InvalidOperationException("Only unpublished linked record/folder drafts can be staged.");
        return await ExistingStore().AddAsync(entry, ct);
    }

    public Task<bool> TryDeleteAsync(VaultPendingMutation expected, CancellationToken ct = default)
    {
        RequireEntry(expected);
        if (expected.State != VaultSyncState.Pending || expected.TransactionHash is not null)
            throw new InvalidOperationException("Only never-submitted linked drafts can be deleted.");
        return ExistingStore().TryDeleteAsync(expected, ct);
    }

    public Task<bool> TryReplaceAsync(VaultPendingMutation previous,
        VaultPendingMutation next, CancellationToken ct = default)
    {
        RequireEntry(previous);
        RequireEntry(next);
        if (previous.Id != next.Id || previous.EntityId != next.EntityId ||
            previous.Kind != next.Kind || previous.AccountId != next.AccountId ||
            previous.NetworkId != next.NetworkId ||
            !previous.EncryptedPayload.AsSpan().SequenceEqual(next.EncryptedPayload) ||
            previous.DependsOn is null || next.DependsOn is null ||
            !previous.DependsOn.SequenceEqual(next.DependsOn) ||
            (previous.State != VaultSyncState.Pending || next.State != VaultSyncState.Submitting) &&
            (previous.State != VaultSyncState.Submitting ||
                next.State is not (VaultSyncState.Uncertain or VaultSyncState.Confirmed)))
            throw new InvalidOperationException(
                "Only original S-key draft submission and immutable outcome transitions are allowed.");
        // A valid transition may change ONLY state and transaction hash;
        // every other authenticated field must match the reviewed revision.
        var expectedNext = previous with
        {
            State = next.State,
            TransactionHash = next.TransactionHash
        };
        if (!Equals(expectedNext, next) ||
            (next.State == VaultSyncState.Confirmed &&
             (next.TransactionHash is not { Length: 64 } ||
              !next.TransactionHash.All(Uri.IsHexDigit))) ||
            next.TransactionHash is { Length: > 0 } hash &&
                (hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
            throw new InvalidOperationException(
                "Linked queue state transition modified an immutable draft field or hash.");
        return ExistingStore().TryReplaceAsync(previous, next, ct);
    }

    public async Task<IReadOnlyList<VaultPendingMutation>> ListAsync(
        string accountId, string linkedNetworkScope, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        linkedRetention.RequireLocalPersistence();
        var storageScope = VaultLinkedQueueStorageScope.FromNetworkScope(linkedNetworkScope);
        var storage = ExistingStore();
        var mutations = await storage.ListAsync(accountId, storageScope, ct);
        foreach (var item in mutations)
        {
            if (VaultMutationStrategyPolicy.Resolve(item.OriginStrategyId) !=
                VaultMutationStrategyPolicy.Linked)
                throw new InvalidDataException(
                    "The isolated S-key queue contains an entry of another account strategy.");
        }
        return mutations;
    }
}
