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
    VaultLinkedLocalRetentionGate linkedRetention,
    VaultNetworkSelection networks) : IVaultLinkedDraftStore
{
    private IndexedDbVaultPendingMutationStore ExistingStore() =>
        new(js, keys, linkedRetention.DeviceRetention);

    private void RequireEntry(VaultPendingMutation entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        linkedRetention.DeviceRetention.RequireLocalPersistence();
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
            entry.Kind is not (VaultMutationKind.CreateRecord or
                VaultMutationKind.CreateFolder or VaultMutationKind.UpdateRecord or
                VaultMutationKind.UpdateFolder or VaultMutationKind.DeleteRecord or
                VaultMutationKind.DeleteFolder or VaultMutationKind.ExtendLinkedChain))
            throw new InvalidOperationException(
                "Only unpublished linked drafts or immutable chain-expansion intents can be journaled.");
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
        VaultLinkedMutationTransitionPolicy.RequireValid(previous, next);
        return ExistingStore().TryReplaceAsync(previous, next, ct);
    }

    public async Task<IReadOnlyList<VaultPendingMutation>> ListAsync(
        string accountId, string linkedNetworkScope, CancellationToken ct = default)
    {
        linkedRetention.DeviceRetention.RequireLocalPersistence();
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
