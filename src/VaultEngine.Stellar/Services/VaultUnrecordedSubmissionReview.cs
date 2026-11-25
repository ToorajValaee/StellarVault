namespace VaultEngine.Stellar.Services;

/// <summary>
/// Read-only historical search for a durable queued submission that lacks a
/// recorded transaction hash. Candidates are NEVER queue confirmation, and
/// must not be attached to a mutation or used to authorize a retry.
/// </summary>
public interface IVaultUnrecordedSubmissionReader
{
    Task<VaultUnrecordedSubmissionFinding> InspectAsync(
        VaultPendingMutation mutation, CancellationToken ct = default);
}

public sealed record VaultUnrecordedSubmissionFinding(
    IReadOnlyList<string> HistoricallyMatchedTransactionHashes,
    int ExaminedTransactions, bool SearchComplete, string Explanation);

public sealed class VaultUnrecordedSubmissionReview(
    VaultPendingMutationQueue queue,
    VaultSessionManager sessions,
    VaultNetworkSelection networks,
    VaultDeviceRetentionGate retention,
    IVaultUnrecordedSubmissionReader reader)
{
    public async Task<VaultUnrecordedSubmissionFinding> InspectAsync(
        string accountId, string mutationId, CancellationToken ct = default)
    {
        static VaultUnrecordedSubmissionFinding Reject(string reason) =>
            new([], 0, false, reason);
        retention.RequireLocalPersistence();
        if (!sessions.IsUnlocked || sessions.ActiveAccountId != accountId)
            return Reject("Unlock the matching encrypted local vault to inspect transaction history.");
        var epoch = sessions.Epoch;
        var network = networks.Network;
        var horizon = networks.CustomHorizonUrl;
        var passphrase = networks.CustomPassphrase;
        var scope = networks.CredentialKey(accountId);
        var original = (await queue.ReviewAllAsync(accountId, scope, ct))
            .SingleOrDefault(x => x.Id == mutationId);
        if (original is null ||
            original.State is not (VaultSyncState.Uncertain or VaultSyncState.Submitting) ||
            original.TransactionHash is not null ||
            original.Kind is not (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder or
                VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder))
            return Reject("No unresolved supported create/update with a missing recorded hash exists.");
        try
        {
            var finding = await reader.InspectAsync(original, ct);
            var after = (await queue.ReviewAllAsync(accountId, scope, ct))
                .SingleOrDefault(x => x.Id == mutationId);
            if (after is null || !SameRevision(original, after) ||
                !sessions.IsUnlocked || sessions.ActiveAccountId != accountId ||
                sessions.Epoch != epoch ||
                networks.Network != network ||
                networks.CustomPassphrase != passphrase ||
                networks.CustomHorizonUrl != horizon ||
                networks.CredentialKey(accountId) != scope)
                return Reject("Vault session, selected network or durable queue changed during investigation.");
            return finding;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return Reject("Historical search unavailable. The queued submission remains unresolved. Do not retry.");
        }
    }

    private static bool SameRevision(VaultPendingMutation a, VaultPendingMutation b) =>
        a.Id == b.Id && a.AccountId == b.AccountId && a.NetworkId == b.NetworkId &&
        a.Kind == b.Kind && a.EntityId == b.EntityId &&
        a.OriginStrategyId == b.OriginStrategyId &&
        a.EncryptedPayload.AsSpan().SequenceEqual(b.EncryptedPayload) &&
        a.ExpectedRemoteVersion == b.ExpectedRemoteVersion &&
        a.DependsOn.SequenceEqual(b.DependsOn, StringComparer.Ordinal) &&
        a.State == b.State && a.TransactionHash == b.TransactionHash &&
        a.CreatedUtc == b.CreatedUtc &&
        (a.SignedDeletionManifests ?? []).SequenceEqual(b.SignedDeletionManifests ?? []) &&
        (a.SignedFileEntries ?? []).SequenceEqual(b.SignedFileEntries ?? []);
}
