using VaultEngine.Stellar.Services;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Manual investigation of a found transaction hash for a queued submission
/// whose original hash was not saved. Read-only: cannot attach hashes, retry
/// submissions, or transition queue state. A matching candidate is NOT proof
/// of the identity of the original network submission.
/// </summary>
public sealed class VaultUnrecordedSubmissionDiagnostic(
    IVaultPendingMutationStore store,
    VaultNetworkSelection selection,
    VaultDeviceRetentionGate retention,
    VaultQueuedCreateHistoricalReview historical)
{
    public async Task<VaultQueuedCreateHistoricalReview.Finding> InspectCandidateAsync(
        string accountId, string mutationId, string candidateTransactionHash,
        string expectedHdChildAccount, int expectedHdChildIndex,
        CancellationToken ct = default)
    {
        static VaultQueuedCreateHistoricalReview.Finding Reject(string explanation) =>
            new(false, false, explanation);

        if (accountId is not { Length: 56 } || accountId[0] != 'G' ||
            !accountId.All(c => c is >= 'A' and <= 'Z' or >= '2' and <= '7') ||
            mutationId is not { Length: 32 } || !Guid.TryParseExact(mutationId, "N", out _) ||
            candidateTransactionHash is not { Length: 64 } ||
            !candidateTransactionHash.All(Uri.IsHexDigit))
            return Reject("Investigation requires an account, mutation ID and candidate transaction hash.");

        try
        {
            retention.RequireLocalPersistence();
            var network = selection.CredentialKey(accountId);
            var item = await store.GetAsync(accountId, network, mutationId, ct);
            if (item is null || item.State is not (VaultSyncState.Submitting or VaultSyncState.Uncertain) ||
                item.TransactionHash is not null)
                return Reject("Only an unresolved durable submission without a recorded hash can be investigated.");

            return await historical.InspectUnrecordedCandidateAsync(
                item, candidateTransactionHash, expectedHdChildAccount,
                expectedHdChildIndex, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Reject("Cannot inspect this candidate against the durable queue."); }
    }
}
