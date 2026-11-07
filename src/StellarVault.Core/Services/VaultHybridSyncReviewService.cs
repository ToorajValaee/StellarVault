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
        IReadOnlyList<Item> Estimates, bool LedgerConnected,
        VaultHybridFolderSyncPlan.Plan FolderPlan);

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
            var isCreate = mutation.Kind is VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder;
            var isRemote = mutation.Kind is VaultMutationKind.UpdateRecord or
                VaultMutationKind.UpdateFolder or VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder;
            if (!isCreate && !isRemote)
            {
                details.Add(new(mutation.Id, mutation.Kind, mutation.Kind.ToString(),
                    mutation.Kind == VaultMutationKind.UploadFile
                        ? "Encrypted file intent is staged in the hybrid queue. Per-part approval, durable transaction checkpoints and manifest-last publication are not yet connected to this Sync coordinator."
                        : "Requires a dedicated file deletion workflow; generic record mutation cannot be used."));
                continue;
            }

            // New deletion queue entries contain a VaultDeletionIntent, not a
            // bare LedgerRecordDraft. Resolve the original approved record for
            // fee previews; the durable queue preflight authenticates the
            // envelope, pinned original fingerprint, and manifest-owner set.
            LedgerRecordDraft? draft;
            try
            {
                draft = mutation.Kind is VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder
                    ? JsonSerializer.Deserialize<VaultDeletionIntent>(mutation.EncryptedPayload)?.Original
                    : JsonSerializer.Deserialize<LedgerRecordDraft>(mutation.EncryptedPayload);
            }
            catch (JsonException) { draft = null; }
            if (draft is null || string.IsNullOrWhiteSpace(draft.Title) ||
                draft.Title.Length > 256 || string.IsNullOrEmpty(draft.Content) ||
                draft.IsFolder != (mutation.Kind is VaultMutationKind.CreateFolder or
                    VaultMutationKind.UpdateFolder or VaultMutationKind.DeleteFolder))
            {
                details.Add(new(mutation.Id, mutation.Kind, mutation.Kind.ToString(),
                    "Queued record/folder payload is malformed or inconsistent."));
                continue;
            }

            var problems = isCreate ? VaultQueuedCreatePolicy.Problems(mutation, all) : [];
            if (problems.Length > 0)
            {
                details.Add(new(mutation.Id, mutation.Kind, draft.Title,
                    "Not ready: " + string.Join(", ", problems)));
                continue;
            }

            if (isRemote && selectedNetwork != "testnet")
            {
                details.Add(new(mutation.Id, mutation.Kind, draft.Title,
                    "Hybrid queued update/delete publication is Testnet-only. Mainnet/custom writes are blocked until an atomic version protocol exists."));
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

            if (isCreate)
            {
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
            }

            try
            {
                // Re-run the durable queue's complete eligibility policy.
                var inspectionQuote = VaultSyncApprovalGuard.Capture(
                    mutation, decimal.MaxValue, decimal.MaxValue, DateTimeOffset.UtcNow);
                if (isCreate)
                    await queue.PreflightCreateAsync(inspectionQuote, accountId,
                        networkId, remote, DateTimeOffset.UtcNow, ct);
                else
                    await queue.PreflightRemoteMutationAsync(inspectionQuote, accountId,
                        networkId, remote, DateTimeOffset.UtcNow, ct);
                // Existing record IDs mean UPDATE, null ID means CREATE.
                // Deletion has its own cost estimator.
                var cost = mutation.Kind is VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder
                    ? await ledger.EstimateDeleteAsync(mutation.EntityId)
                    : await ledger.EstimateAsync(draft, isCreate ? null : mutation.EntityId);
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

        return new(accountId, networkId, review, details, ledger.IsConnected,
            VaultHybridFolderSyncPlan.Build(all, review));
    }
}
