using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// Shared, read-only hybrid queue review for PWA and native hosts. It neither
/// reserves mutations nor publishes to Stellar; approval snapshots are only
/// cost-review inputs and expire under VaultSyncApprovalGuard.
/// </summary>
public sealed class VaultHybridSyncReviewService(
    VaultPendingMutationQueue queue,
    IStellarLedgerRecords ledger,
    VaultNetworkSelection networks,
    VaultDeviceRetentionGate retention)
{
    public sealed record Item(string MutationId, VaultMutationKind Kind, string Title,
        string Explanation, VaultSyncApprovalSnapshot? Quote = null);

    public sealed record Report(
        string AccountId, string NetworkId, VaultSyncReview Queue,
        IReadOnlyList<Item> Estimates, bool LedgerConnected);

    public async Task<Report> ReviewAsync(string accountId, bool estimateFees,
        CancellationToken ct = default)
    {
        retention.RequireLocalPersistence();
        var networkId = networks.CredentialKey(accountId);
        var selectedNetwork = networks.Network;
        var selectedPassphrase = networks.CustomPassphrase;
        var selectedEndpoint = networks.CustomHorizonUrl;
        var review = await queue.PrepareReviewAsync(accountId, networkId, ct);
        var canEstimate = estimateFees && ledger.IsConnected &&
            review.NeedsReconciliation == 0 && review.Conflicts == 0;
        var details = new List<Item>();

        // A single source inventory is used for every estimate in this review.
        // Never charge a fee or offer a publication quote when ledger state
        // cannot be read or unresolved submissions still exist.
        IReadOnlyList<LedgerRecordItem>? remote = null;
        if (canEstimate)
        {
            try { remote = await ledger.ReadAsync(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { canEstimate = false; }
        }
        var all = await queue.ReviewAllAsync(accountId, networkId, ct);

        foreach (var mutation in review.Ready)
        {
            ct.ThrowIfCancellationRequested();
            if (mutation.Kind is not (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder))
            {
                details.Add(new(mutation.Id, mutation.Kind, mutation.Kind.ToString(),
                    "Requires protocol-aware update, delete or file synchronization."));
                continue;
            }

            LedgerRecordDraft? draft;
            try { draft = JsonSerializer.Deserialize<LedgerRecordDraft>(mutation.EncryptedPayload); }
            catch (JsonException) { draft = null; }
            if (draft is null || string.IsNullOrWhiteSpace(draft.Title) ||
                draft.Title.Length > 256 || string.IsNullOrEmpty(draft.Content) ||
                draft.IsFolder != (mutation.Kind == VaultMutationKind.CreateFolder))
            {
                details.Add(new(mutation.Id, mutation.Kind, mutation.Kind.ToString(),
                    "Queued creation payload is malformed or inconsistent."));
                continue;
            }

            var problems = VaultQueuedCreatePolicy.Problems(mutation, all);
            if (problems.Length > 0)
            {
                details.Add(new(mutation.Id, mutation.Kind, draft.Title,
                    "Not ready: " + string.Join(", ", problems)));
                continue;
            }

            if (!canEstimate || remote is null)
            {
                details.Add(new(mutation.Id, mutation.Kind, draft.Title,
                    review.NeedsReconciliation != 0 || review.Conflicts != 0
                        ? "Resolve uncertain submissions or conflicts before fee review."
                        : estimateFees ? "Connected ledger inventory unavailable."
                            : "Ready for an optional read-only fee estimate."));
                continue;
            }

            if (remote.Any(x => string.Equals(x.Id, mutation.EntityId, StringComparison.OrdinalIgnoreCase)))
            {
                details.Add(new(mutation.Id, mutation.Kind, draft.Title,
                    "Ledger already contains this record ID; reconcile before publishing."));
                continue;
            }

            if (!string.IsNullOrEmpty(draft.FolderId) &&
                !remote.Any(x => string.Equals(x.Id, draft.FolderId,
                    StringComparison.OrdinalIgnoreCase) && x.IsFolder))
            {
                details.Add(new(mutation.Id, mutation.Kind, draft.Title,
                    "Parent folder has not been confirmed on Stellar."));
                continue;
            }

            try
            {
                // Re-run the durable queue's complete eligibility policy.
                await queue.PreflightCreateAsync(
                    VaultSyncApprovalGuard.Capture(mutation, decimal.MaxValue,
                        decimal.MaxValue, DateTimeOffset.UtcNow),
                    accountId, networkId, remote, DateTimeOffset.UtcNow, ct);
                var cost = await ledger.EstimateAsync(draft, mutation.EntityId);
                if (networks.Network != selectedNetwork ||
                    networks.CredentialKey(accountId) != networkId ||
                    networks.CustomPassphrase != selectedPassphrase ||
                    networks.CustomHorizonUrl != selectedEndpoint ||
                    !ledger.IsConnected)
                {
                    details.Add(new(mutation.Id, mutation.Kind, draft.Title,
                        "Network selection changed; review again."));
                    continue;
                }
                var quote = VaultSyncApprovalGuard.Capture(mutation,
                    cost.NetworkFeeXlm, cost.PeakReserveXlm, DateTimeOffset.UtcNow);
                details.Add(new(mutation.Id, mutation.Kind, draft.Title,
                    $"Indicative: {cost.Transactions} transactions, {cost.Operations} operations, fee {cost.NetworkFeeXlm} XLM, peak reserve {cost.PeakReserveXlm} XLM.", quote));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                details.Add(new(mutation.Id, mutation.Kind, draft.Title,
                    "Fee or remote-state preflight unavailable; review again."));
            }
        }

        // A review never returns usable quotes after account/network context
        // changes, even if the selected network changed during a network call.
        if (networks.Network != selectedNetwork ||
            networks.CredentialKey(accountId) != networkId ||
            networks.CustomPassphrase != selectedPassphrase ||
            networks.CustomHorizonUrl != selectedEndpoint)
            throw new InvalidOperationException("Selected network changed during hybrid review.");

        return new(accountId, networkId, review, details, ledger.IsConnected);
    }
}
