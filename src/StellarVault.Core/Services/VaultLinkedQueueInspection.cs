namespace StellarVault.Core.Services;

/// <summary>
/// An integration boundary for the EXISTING encrypted local queue, not a second
/// queue. Strictly read-only: the linked S-key ledger is never given the
/// mnemonic-HD queued payload or its signing/confirmation state.
/// Requires a separately unlocked, consented local device vault namespace.
/// This does not prove local queue ownership or authorize publication.
/// </summary>
public sealed class VaultLinkedQueueInspection(
    IStellarSecretKeyLedger linked,
    VaultScopedQueueReview queue,
    VaultSessionManager sessions,
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
        if (!linked.IsConnected || linked.RootAccountId is not { } root)
            throw new InvalidOperationException("Connect the independent linked Stellar vault first.");
        StellarAccountAddress.Validate(root);
        var scope = networks.CredentialKey(root);
        var epoch = sessions.Epoch;

        var account = await linked.InspectAccountInfoAsync(ct);
        if (account.RootAccountId != root)
            throw new UnauthorizedAccessException("Linked signing root changed during queue inspection.");

        // Scoped review performs its own retention-consent and local-key checks.
        // It does NOT inspect, derive or use a mnemonic or imported S-key.
        var snapshot = await queue.ReadAsync(root, ct);
        if (snapshot.NetworkId != scope || snapshot.LocalSessionEpoch != epoch ||
            !linked.IsConnected || linked.RootAccountId != root ||
            networks.CredentialKey(root) != scope ||
            !sessions.IsUnlocked || sessions.ActiveAccountId != root ||
            sessions.Epoch != epoch)
            throw new InvalidOperationException("Linked vault or encrypted device queue scope changed.");

        // A second connected-account observation catches an account switch
        // during the queue read. Still no permission to submit, even if healthy.
        var after = await linked.InspectAccountInfoAsync(ct);
        if (after.RootAccountId != root || after.TailAccountId != account.TailAccountId ||
            after.TotalAccounts != account.TotalAccounts)
            throw new InvalidOperationException("Linked account chain changed during local queue inspection.");

        // These counts are diagnostics only. Legacy null tags mean HD;
        // a claimed S-key tag does NOT prove the key lifecycle or signing authority.
        var hd = snapshot.Mutations.Count(x =>
            VaultMutationStrategyPolicy.Resolve(x.OriginStrategyId) == VaultMutationStrategyPolicy.Hd);
        var linkedEntries = snapshot.Mutations.Count(x =>
            VaultMutationStrategyPolicy.Resolve(x.OriginStrategyId) == VaultMutationStrategyPolicy.Linked);
        return new(root, scope,
            snapshot.Mutations.Count(x => x.State == VaultSyncState.Pending &&
                VaultMutationStrategyPolicy.Resolve(x.OriginStrategyId) == VaultMutationStrategyPolicy.Linked),
            snapshot.Mutations.Count(x => x.State is VaultSyncState.Submitting or VaultSyncState.Uncertain &&
                VaultMutationStrategyPolicy.Resolve(x.OriginStrategyId) == VaultMutationStrategyPolicy.Linked),
            snapshot.Mutations.Count(x => x.State == VaultSyncState.Conflict &&
                VaultMutationStrategyPolicy.Resolve(x.OriginStrategyId) == VaultMutationStrategyPolicy.Linked),
            snapshot.Mutations.Count(x =>
                (x.Kind is VaultMutationKind.CreateFolder or VaultMutationKind.UpdateFolder or VaultMutationKind.DeleteFolder) &&
                VaultMutationStrategyPolicy.Resolve(x.OriginStrategyId) == VaultMutationStrategyPolicy.Linked),
            hd, linkedEntries, false,
            "Existing encrypted queue inspected read-only. Legacy HD entries are excluded from S-key counts. Tagged entries alone do not authorize S-key publishing.");
    }
}
