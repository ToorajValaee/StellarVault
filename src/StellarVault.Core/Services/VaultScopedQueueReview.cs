namespace StellarVault.Core.Services;

/// <summary>
/// Read-only account/network-scoped review of the EXISTING encrypted mutation
/// queue and folder dependency plan. It never supplies signing credentials,
/// approves fees, reserves queue mutations or publishes to Stellar.
/// The caller must unlock the separately encrypted device vault with explicit
/// local-storage consent. A matching G address is a namespace check, not proof
/// of ownership or a mapping between HD and imported S-key credentials.
/// </summary>
public sealed class VaultScopedQueueReview(
    VaultPendingMutationQueue queue,
    VaultSessionManager sessions,
    VaultDeviceRetentionGate retention,
    VaultNetworkSelection networks)
{
    public sealed record Snapshot(
        string AccountId, string NetworkId, long LocalSessionEpoch,
        VaultSyncReview Queue,
        IReadOnlyList<VaultPendingMutation> Mutations,
        VaultHybridFolderSyncPlan.Plan FolderPlan);

    public async Task<Snapshot> ReadAsync(string accountId, CancellationToken ct = default)
    {
        StellarAccountAddress.Validate(accountId);
        retention.RequireLocalPersistence();
        if (!sessions.IsUnlocked || sessions.ActiveAccountId != accountId)
            throw new UnauthorizedAccessException(
                "Unlock the matching encrypted device vault before inspecting its pending queue.");

        var epoch = sessions.Epoch;
        var selectedNetwork = networks.Network;
        var selectedPassphrase = networks.CustomPassphrase;
        var selectedEndpoint = networks.CustomHorizonUrl;
        var scope = networks.CredentialKey(accountId);
        // Read the authenticated queue exactly once. Readiness and folder
        // dependency decisions must describe the same immutable snapshot.
        var all = await queue.ReviewAllAsync(accountId, scope, ct);
        var review = VaultPendingMutationReviewPolicy.Build(all);

        ct.ThrowIfCancellationRequested();
        retention.RequireLocalPersistence();
        if (!sessions.IsUnlocked || sessions.ActiveAccountId != accountId ||
            sessions.Epoch != epoch || networks.Network != selectedNetwork ||
            networks.CustomPassphrase != selectedPassphrase ||
            networks.CustomHorizonUrl != selectedEndpoint ||
            networks.CredentialKey(accountId) != scope)
            throw new InvalidOperationException(
                "Device vault session, storage consent or network changed during queue review.");

        return new(accountId, scope, epoch, review, all,
            VaultHybridFolderSyncPlan.Build(all, review));
    }
}
