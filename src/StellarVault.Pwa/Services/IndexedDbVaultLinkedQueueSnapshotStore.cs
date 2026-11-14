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
    VaultLinkedLocalRetentionGate linkedRetention) : IVaultLinkedQueueSnapshotStore
{
    public async Task<IReadOnlyList<VaultPendingMutation>> ListAsync(
        string accountId, string linkedNetworkScope, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        linkedRetention.RequireLocalPersistence();
        var storageScope = VaultLinkedQueueStorageScope.FromNetworkScope(linkedNetworkScope);
        var storage = new IndexedDbVaultPendingMutationStore(js, keys, retention);
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
