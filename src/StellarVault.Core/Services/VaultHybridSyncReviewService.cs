using VaultEngine.Core;
using System.Text.Json;
using System.Security.Cryptography;

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
    VaultDeviceRetentionGate retention,
    VaultSessionManager sessions,
    VaultScopedQueueReview scopedQueue)
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
        if (!sessions.IsUnlocked || sessions.ActiveAccountId != accountId)
            throw new UnauthorizedAccessException(
                "Unlock the matching encrypted vault before reviewing Hybrid Sync.");
        var sessionEpoch = sessions.Epoch;
        var networkId = networks.CredentialKey(accountId);
        var selectedNetwork = networks.Network;
        var selectedPassphrase = networks.CustomPassphrase;
        var selectedEndpoint = networks.CustomHorizonUrl;
        // Reuse the same consent-bound encrypted queue and folder-graph
        // inspection as any other Stellar account strategy.
        var snapshot = await scopedQueue.ReadAsync(accountId, ct);
        try
        {
        if (snapshot.NetworkId != networkId || snapshot.LocalSessionEpoch != sessionEpoch)
            throw new InvalidOperationException("Encrypted queue scope changed during review.");
        // Legacy untagged entries retain HD semantics. A tagged independent
        // S-key intent cannot be silently interpreted as an HD mutation.
        if (snapshot.Mutations.Any(x =>
            VaultMutationStrategyPolicy.Resolve(x.OriginStrategyId) != VaultMutationStrategyPolicy.Hd))
            throw new InvalidOperationException(
                "Mixed account-strategy queue requires independent review; HD publishing is blocked.");
        var review = snapshot.Queue;
        var canEstimate = estimateFees && ledger.IsConnected &&
            review.NeedsReconciliation == 0 && review.Conflicts == 0;
        var details = new List<Item>();

        // A single source inventory is used for every estimate in this review.
        // Never charge a fee or offer a publication quote when ledger state
        // cannot be read or unresolved submissions still exist.
        IReadOnlyList<LedgerRecordItem>? remote = null;
        if (canEstimate)
        {
            try
            {
                // A connected network is not sufficient: fee quoting requires
                // the exact mnemonic-derived root corresponding to this queue.
                var root = await ledger.InspectAccountInfoAsync();
                if (root.RootAccountId != accountId)
                    throw new UnauthorizedAccessException(
                        "Signing HD root differs from the unlocked encrypted queue.");
                remote = await ledger.ReadAsync();
                var afterReadRoot = await ledger.InspectAccountInfoAsync();
                if (afterReadRoot.RootAccountId != accountId)
                    throw new UnauthorizedAccessException("Connected HD root changed during review.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { canEstimate = false; remote = null; }
        }
        var all = snapshot.Mutations;

        foreach (var mutation in review.Ready)
        {
            ct.ThrowIfCancellationRequested();
            var isCreate = mutation.Kind is VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder;
            var isRemote = mutation.Kind is VaultMutationKind.UpdateRecord or
                VaultMutationKind.UpdateFolder or VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder;
            if (!isCreate && !isRemote)
            {
                details.Add(new(mutation.Id, mutation.Kind, mutation.Kind.ToString(),
                    mutation.Kind is VaultMutationKind.UploadFile or VaultMutationKind.DeleteFile
                        ? "Hybrid files are disabled by product policy. Legacy file intents remain encrypted for read-only recovery; no publication is authorized."
                        : "Unsupported mutation kind. No publication is authorized."));
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
                if (!sessions.IsUnlocked || sessions.ActiveAccountId != accountId ||
                    sessions.Epoch != sessionEpoch ||
                    networks.Network != selectedNetwork ||
                    networks.CredentialKey(accountId) != networkId ||
                    networks.CustomPassphrase != selectedPassphrase ||
                    networks.CustomHorizonUrl != selectedEndpoint ||
                    !ledger.IsConnected)
                {
                    details.Add(new(mutation.Id, mutation.Kind, draft.Title,
                        "Network selection changed; review again."));
                    continue;
                }
                if ((isCreate || mutation.Kind is VaultMutationKind.UpdateRecord or
                        VaultMutationKind.UpdateFolder) &&
                    !VaultCapacityPolicyGuard.AllowsCreate(
                        mutation.CapacityPolicy, cost.CreatesChildNode))
                {
                    details.Add(new(mutation.Id, mutation.Kind, draft.Title,
                        "Single-account policy: record needs another storage account. Change policy only by staging a new authorized mutation."));
                    continue;
                }
                var quote = VaultSyncApprovalGuard.Capture(mutation,
                    cost.NetworkFeeXlm, cost.PeakReserveXlm, DateTimeOffset.UtcNow);
                details.Add(new(mutation.Id, mutation.Kind, draft.Title,
                    $"Indicative: {cost.Transactions} transactions, {cost.Operations} operations, fee {cost.NetworkFeeXlm} native asset, peak reserve {cost.PeakReserveXlm} native asset.", quote));
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
        if (!sessions.IsUnlocked || sessions.ActiveAccountId != accountId ||
            sessions.Epoch != sessionEpoch ||
            networks.Network != selectedNetwork ||
            networks.CredentialKey(accountId) != networkId ||
            networks.CustomPassphrase != selectedPassphrase ||
            networks.CustomHorizonUrl != selectedEndpoint)
            throw new InvalidOperationException(
                "Unlocked vault session or selected network changed during hybrid review.");

        // Only Ready entries are intentionally returned to the caller. A
        // full same-snapshot folder review requires ALL entries temporarily,
        // but keeping blocked/conflicted plaintext afterward is unnecessary.
        var retained = new HashSet<VaultPendingMutation>(
            review.Ready, ReferenceEqualityComparer.Instance);
        foreach (var item in snapshot.Mutations)
            if (!retained.Contains(item))
                CryptographicOperations.ZeroMemory(item.EncryptedPayload);
        return new(accountId, networkId, review, details, ledger.IsConnected,
            snapshot.FolderPlan);
        }
        catch
        {
            // On error no review is returned: zero ALL copies, even Ready.
            foreach (var entry in snapshot.Mutations)
                CryptographicOperations.ZeroMemory(entry.EncryptedPayload);
            throw;
        }
    }
}
