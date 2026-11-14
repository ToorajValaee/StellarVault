namespace StellarVault.Core.Services;

/// <summary>
/// Read-only native S-key queue view reusing the existing authenticated
/// filesystem queue implementation with an independent in-memory key and
/// dedicated application-private directory. HD ciphertext is never inspected.
/// </summary>
public sealed class FileSystemVaultLinkedQueueSnapshotStore(
    VaultLinkedQueueKeySession keys,
    VaultDeviceRetentionGate retention,
    string directory) : IVaultLinkedQueueSnapshotStore
{
    public async Task<IReadOnlyList<VaultPendingMutation>> ListAsync(
        string accountId, string linkedNetworkScope, CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        var storageScope = VaultLinkedQueueStorageScope.FromNetworkScope(linkedNetworkScope);
        var storage = new FileSystemVaultPendingMutationStore(keys, retention, directory);
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
