namespace StellarVault.Core.Services;

/// <summary>
/// Read-only native S-key queue view reusing the existing authenticated
/// filesystem queue implementation with an independent in-memory key and
/// dedicated application-private directory. HD ciphertext is never inspected.
/// </summary>
public sealed class FileSystemVaultLinkedQueueSnapshotStore(
    VaultLinkedQueueKeySession keys,
    VaultLinkedLocalRetentionGate linkedRetention,
    string directory,
    VaultNetworkSelection networks) : IVaultLinkedDraftStore
{
    private FileSystemVaultPendingMutationStore ExistingStore() =>
        new(keys, linkedRetention.DeviceRetention, directory);

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
        var needsVersion = entry.Kind is VaultMutationKind.UpdateRecord or
            VaultMutationKind.UpdateFolder or VaultMutationKind.DeleteRecord or
            VaultMutationKind.DeleteFolder;
        if (needsVersion
            ? !VaultLinkedRemoteRevisionPolicy.IsValid(entry.ExpectedRemoteVersion) ||
              entry.DependsOn is not { Length: 0 }
            : entry.ExpectedRemoteVersion is not null)
            throw new InvalidOperationException(
                "Linked remote mutations must preserve a canonical original manifest; creates and expansion intents cannot invent one.");
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
