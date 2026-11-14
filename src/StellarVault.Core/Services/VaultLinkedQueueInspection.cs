namespace StellarVault.Core.Services;

/// <summary>
/// Inspect the existing encrypted queue format under an independently unlocked,
/// consented S-key key and storage namespace. No HD queue credentials, signing
/// session or ciphertext are ever passed into this strategy-specific inspection.
/// This is NOT permission to publish or automatically migrate local drafts.
/// </summary>
public sealed class VaultLinkedQueueInspection(
    IStellarSecretKeyLedger linked,
    IVaultLinkedQueueSnapshotStore store,
    VaultLinkedQueueKeySession keys,
    VaultDeviceRetentionGate retention,
    VaultLinkedLocalRetentionGate linkedRetention,
    VaultNetworkSelection networks)
{
    public sealed record Report(
        string RootAccountId, string NetworkId,
        int Pending, int UncertainOrSubmitting, int Conflicts,
        int FolderEntries,
        int LegacyHdEntries, int LinkedTaggedEntries,
        bool CanPublish,
        string Explanation);

    public async Task<Report> InspectAsync(CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        linkedRetention.RequireLocalPersistence();
        if (!keys.IsUnlocked)
            throw new UnauthorizedAccessException(
                "Unlock the independent encrypted S-key queue before inspecting local drafts.");
        if (!linked.IsConnected || linked.RootAccountId is not { } root)
            throw new InvalidOperationException("Connect the imported S-key Stellar vault first.");
        StellarAccountAddress.Validate(root);
        var scope = networks.CredentialKey(root);
        var expectedPassphrase = linked.ConnectedNetworkPassphrase;

        var account = await linked.InspectAccountInfoAsync(ct);
        if (account.RootAccountId != root)
            throw new UnauthorizedAccessException("Linked signing root changed during queue inspection.");

        // The host-specific read-only adapter uses the ORIGINAL encrypted queue
        // implementation, but exclusively with its dedicated S-key key session
        // and prefixed namespace. It rejects any untagged/HD-origin entry.
        var mutations = await store.ListAsync(root, scope, ct);
        ct.ThrowIfCancellationRequested();
        retention.RequireLocalPersistence();
        if (!keys.IsUnlocked || !linked.IsConnected || linked.RootAccountId != root ||
            linked.ConnectedNetworkPassphrase != expectedPassphrase ||
            networks.CredentialKey(root) != scope)
            throw new InvalidOperationException(
                "Independent S-key queue context changed during read-only inspection.");

        var after = await linked.InspectAccountInfoAsync(ct);
        if (after.RootAccountId != root || after.TailAccountId != account.TailAccountId ||
            after.TotalAccounts != account.TotalAccounts ||
            linked.ConnectedNetworkPassphrase != expectedPassphrase ||
            networks.CredentialKey(root) != scope)
            throw new InvalidOperationException("Linked chain or selected network changed during inspection.");

        var linkedCount = mutations.Count;
        return new(root, scope,
            mutations.Count(x => x.State == VaultSyncState.Pending),
            mutations.Count(x => x.State is VaultSyncState.Submitting or VaultSyncState.Uncertain),
            mutations.Count(x => x.State == VaultSyncState.Conflict),
            mutations.Count(x =>
                x.Kind is VaultMutationKind.CreateFolder or
                    VaultMutationKind.UpdateFolder or VaultMutationKind.DeleteFolder),
            0, linkedCount, false,
            "Read-only inspection used a separately unlocked S-key encryption key and an isolated queue namespace. No HD drafts are read. Publishing is not yet authorized.");
    }
}
