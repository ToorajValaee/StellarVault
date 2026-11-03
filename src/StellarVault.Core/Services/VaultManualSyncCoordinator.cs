using System.Text.Json;
namespace StellarVault.Core.Services;

/// <summary>
/// Single-shot manual publishing boundary. The caller must obtain an explicit,
/// per-mutation user approval immediately before invoking this method.
/// Never retry automatically after durable reservation.
/// </summary>
public sealed class VaultManualSyncCoordinator(
    VaultPendingMutationQueue queue,
    IStellarLedgerRecords ledger,
    VaultDeviceRetentionGate retention,
    VaultNetworkSelection networks,
    VaultSessionManager sessions,
    IVaultLedgerManifestCopyInventory copyInventory)
{
    private readonly SemaphoreSlim submissionGate = new(1, 1);

    private sealed record NetworkContext(string AccountId, string NetworkId,
        string Network, string? Passphrase, string HorizonUrl, long SessionEpoch);

    private NetworkContext CaptureContext(string accountId)
    {
        StellarAccountAddress.Validate(accountId);
        retention.RequireLocalPersistence();
        if (!sessions.IsUnlocked || sessions.ActiveAccountId != accountId)
            throw new UnauthorizedAccessException("Unlock the matching device vault before hybrid Sync.");
        return new NetworkContext(accountId, networks.CredentialKey(accountId),
            networks.Network, networks.CustomPassphrase, networks.CustomHorizonUrl,
            sessions.Epoch);
    }

    private void RequireSameContext(NetworkContext context)
    {
        if (!ledger.IsConnected ||
            !retention.AllowsLocalPersistence ||
            !sessions.IsUnlocked || sessions.ActiveAccountId != context.AccountId ||
            sessions.Epoch != context.SessionEpoch ||
            !string.Equals(networks.CredentialKey(context.AccountId),
                context.NetworkId, StringComparison.Ordinal) ||
            networks.Network != context.Network ||
            networks.CustomPassphrase != context.Passphrase ||
            networks.CustomHorizonUrl != context.HorizonUrl)
            throw new InvalidOperationException("Signing network changed; review again.");
    }

    private async Task RequireConnectedRootAsync(NetworkContext context, CancellationToken ct)
    {
        RequireSameContext(context);
        ct.ThrowIfCancellationRequested();
        var info = await ledger.InspectAccountInfoAsync();
        ct.ThrowIfCancellationRequested();
        RequireSameContext(context);
        if (!string.Equals(info.RootAccountId, context.AccountId,
            StringComparison.Ordinal))
            throw new UnauthorizedAccessException(
                "Connected HD signing root differs from the unlocked local queue account.");
    }

    public async Task<bool> CanPublishApprovedCreateAsync(
        VaultSyncApprovalSnapshot quote, string accountId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(quote);
        retention.RequireLocalPersistence();
        if (!ledger.IsConnected) return false;
        try
        {
            var context = CaptureContext(accountId);
            if (quote.AccountId != accountId || quote.NetworkId != context.NetworkId) return false;
            await RequireConnectedRootAsync(context, ct);
            var review = await queue.PrepareReviewAsync(accountId, context.NetworkId, ct);
            var mutation = review.Ready.SingleOrDefault(x => x.Id == quote.MutationId);
            if (mutation is null || review.NeedsReconciliation != 0 || review.Conflicts != 0)
                return false;
            VaultSyncApprovalGuard.RequireFresh(quote, mutation, accountId, context.NetworkId,
                DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
            var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(mutation.EncryptedPayload);
            if (draft is null) return false;
            var remote = await ledger.ReadAsync();
            await queue.PreflightCreateAsync(quote, accountId, context.NetworkId,
                remote, DateTimeOffset.UtcNow, ct);
            var cost = await ledger.EstimateAsync(draft);
            await RequireConnectedRootAsync(context, ct);
            return cost.NetworkFeeXlm <= quote.QuotedFeeXlm &&
                cost.PeakReserveXlm <= quote.QuotedPeakReserveXlm;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    public async Task<VaultManualSyncOutcome> PublishApprovedCreateAsync(
        VaultSyncApprovalSnapshot quote, string accountId, bool explicitlyApproved,
        CancellationToken ct = default)
    {
        if (!explicitlyApproved)
            throw new InvalidOperationException("User approval is required before publishing.");
        retention.RequireLocalPersistence();
        if (!ledger.IsConnected)
            throw new InvalidOperationException("Connect to the selected Stellar ledger first.");
        var context = CaptureContext(accountId);
        var network = context.NetworkId;
        if (quote.AccountId != accountId || quote.NetworkId != network)
            throw new InvalidOperationException("Approved quote does not belong to this account/network.");

        if (!await submissionGate.WaitAsync(0, ct)) throw new InvalidOperationException("Another submission is active.");
        try {
        // Device-only namespace is NOT a signing authorization. Bind the
        // connected mnemonic-derived HD root to the exact queue account.
        await RequireConnectedRootAsync(context, ct);
        // Perform all fallible validation BEFORE recording submission intent.
        var ready = await queue.PrepareReviewAsync(accountId, network, ct);
        var mutation = ready.Ready.SingleOrDefault(x => x.Id == quote.MutationId)
            ?? throw new InvalidOperationException("Draft is no longer ready for publishing.");
        VaultSyncApprovalGuard.RequireFresh(quote, mutation, accountId, network,
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(mutation.EncryptedPayload)
            ?? throw new InvalidDataException("Invalid queued create payload.");
        var remote = await ledger.ReadAsync();
        await queue.PreflightCreateAsync(quote, accountId, network, remote, DateTimeOffset.UtcNow, ct);
        var cost = await ledger.EstimateAsync(draft);
        if (cost.NetworkFeeXlm > quote.QuotedFeeXlm ||
            cost.PeakReserveXlm > quote.QuotedPeakReserveXlm)
            throw new InvalidOperationException("Transaction costs exceed approved quote.");

        // Durable CAS reservation is the no-replay boundary. No automatic retries
        // are allowed after this point, even when publishing throws.
        await RequireConnectedRootAsync(context, ct);
        ct.ThrowIfCancellationRequested();
        var reserved = await queue.ReservePreflightedSubmissionAsync(
            quote, accountId, network, remote, DateTimeOffset.UtcNow, ct);
        string? txHash = null;
        Exception? submissionError = null;
        try
        {
            await RequireConnectedRootAsync(context, ct);
            ct.ThrowIfCancellationRequested();
            txHash = await ledger.PublishWithIdAsync(reserved.EntityId, draft);
        }
        catch (Exception ex)
        {
            submissionError = ex;
        }

        // PublishWithIdAsync returning a hash does not prove that this exact
        // record was included. Always require independent reconciliation.
        if (txHash is not null && (txHash.Length != 64 || !txHash.All(Uri.IsHexDigit)))
            txHash = null;
        try { await queue.MarkUncertainAsync(reserved, txHash, CancellationToken.None); }
        catch (Exception ex) { throw new InvalidOperationException("Uncertain state could not be saved; do not retry. Reconcile durable Submitting entry.", ex); }
        return new VaultManualSyncOutcome(
            reserved.Id, txHash, VaultSyncState.Uncertain,
            submissionError is null
                ? "Submission attempted. Independent record-to-transaction verification is required."
                : "Submission result unknown. Do not retry; reconcile with Stellar before taking any action.");
        }
        finally { submissionGate.Release(); }
    }

    /// <summary>
    /// A single, explicitly approved version-pinned UPDATE or DELETE attempt.
    /// A recorded hash is never sufficient for confirmation: the current
    /// evidence verifier deliberately supports CREATE only. The result stays
    /// Uncertain until update/delete-specific evidence exists.
    /// </summary>
    public async Task<bool> CanPublishApprovedRemoteMutationAsync(
        VaultSyncApprovalSnapshot quote, string accountId, CancellationToken ct = default)
    {
        try
        {
            // Remote update/delete ledger writes currently have no atomic
            // on-chain expected-version precondition. Restrict experimental
            // manual submission to Testnet until that race is resolved.
            if (networks.Network != "testnet") return false;
            var context = CaptureContext(accountId);
            if (quote.AccountId != accountId || quote.NetworkId != context.NetworkId)
                return false;
            await RequireConnectedRootAsync(context, ct);
            var review = await queue.PrepareReviewAsync(accountId, context.NetworkId, ct);
            if (review.NeedsReconciliation != 0 || review.Conflicts != 0 ||
                !review.Ready.Any(x => x.Id == quote.MutationId)) return false;
            var mutation = review.Ready.Single(x => x.Id == quote.MutationId);
            VaultSyncApprovalGuard.RequireFresh(quote, mutation, accountId,
                context.NetworkId, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
            var remote = await ledger.ReadAsync();
            await queue.PreflightRemoteMutationAsync(quote, accountId,
                context.NetworkId, remote, DateTimeOffset.UtcNow, ct);
            await RequireFreshDeletionCopyInventoryAsync(mutation, context, ct);
            var cost = await EstimateRemoteCostAsync(mutation);
            await RequireConnectedRootAsync(context, ct);
            return cost.NetworkFeeXlm <= quote.QuotedFeeXlm &&
                cost.PeakReserveXlm <= quote.QuotedPeakReserveXlm;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    private async Task RequireFreshDeletionCopyInventoryAsync(
        VaultPendingMutation mutation, NetworkContext context, CancellationToken ct)
    {
        if (mutation.Kind is not (VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder))
            return;
        VaultDeletionIntent intent;
        try
        {
            intent = JsonSerializer.Deserialize<VaultDeletionIntent>(mutation.EncryptedPayload)
                ?? throw new InvalidDataException("Encrypted deletion intent is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Encrypted deletion intent is malformed.", ex);
        }
        var expected = intent.ManifestSourceAccounts;
        if (expected is not { Length: > 0 and <= 63 } ||
            expected.Any(x => !StellarAccountAddress.IsValid(x)) ||
            expected.Distinct(StringComparer.Ordinal).Count() != expected.Length)
            throw new InvalidDataException("Deletion manifest-owner inventory is missing or invalid.");
        await RequireConnectedRootAsync(context, ct);
        var current = await copyInventory.InspectOwnersAsync(context.AccountId,
            mutation.EntityId, ct);
        if (current.Count != expected.Length ||
            !current.Order(StringComparer.Ordinal)
                .SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidOperationException(
                "Manifest-owner HD inventory changed since staging; do not submit this deletion.");
        await RequireConnectedRootAsync(context, ct);
    }

    private async Task<LedgerCostPreview> EstimateRemoteCostAsync(VaultPendingMutation mutation)
    {
        if (mutation.Kind is VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder)
            return await ledger.EstimateDeleteAsync(mutation.EntityId);
        if (mutation.Kind is not (VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder))
            throw new InvalidOperationException("Unsupported version-pinned operation.");
        var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(mutation.EncryptedPayload)
            ?? throw new InvalidDataException("Update draft is missing.");
        return await ledger.EstimateAsync(draft, mutation.EntityId);
    }

    public async Task<VaultManualSyncOutcome> PublishApprovedRemoteMutationAsync(
        VaultSyncApprovalSnapshot quote, string accountId, bool explicitlyApproved,
        CancellationToken ct = default)
    {
        if (!explicitlyApproved)
            throw new InvalidOperationException("Explicit transaction approval is required.");
        if (networks.Network != "testnet")
            throw new InvalidOperationException(
                "Hybrid update/delete submission is Testnet-only until atomic remote-version protection and historical confirmation are implemented.");
        var context = CaptureContext(accountId);
        if (quote.AccountId != accountId || quote.NetworkId != context.NetworkId)
            throw new InvalidOperationException("Approved quote belongs to another account or network.");
        if (!await submissionGate.WaitAsync(0, ct))
            throw new InvalidOperationException("Another submission is active.");
        try
        {
            await RequireConnectedRootAsync(context, ct);
            var review = await queue.PrepareReviewAsync(accountId, context.NetworkId, ct);
            if (review.NeedsReconciliation != 0 || review.Conflicts != 0)
                throw new InvalidOperationException("Resolve earlier submissions and conflicts first.");
            var mutation = review.Ready.SingleOrDefault(x => x.Id == quote.MutationId)
                ?? throw new InvalidOperationException("The approved remote mutation is no longer ready.");
            VaultSyncApprovalGuard.RequireFresh(quote, mutation, accountId,
                context.NetworkId, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
            if (mutation.Kind is not (VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder or
                VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder))
                throw new InvalidOperationException("Only version-pinned remote changes can use this operation.");
            var remote = await ledger.ReadAsync();
            await queue.PreflightRemoteMutationAsync(quote, accountId,
                context.NetworkId, remote, DateTimeOffset.UtcNow, ct);
            await RequireFreshDeletionCopyInventoryAsync(mutation, context, ct);
            var cost = await EstimateRemoteCostAsync(mutation);
            if (cost.NetworkFeeXlm > quote.QuotedFeeXlm ||
                cost.PeakReserveXlm > quote.QuotedPeakReserveXlm)
                throw new InvalidOperationException("Network costs changed; obtain a new approval.");

            await RequireConnectedRootAsync(context, ct);
            ct.ThrowIfCancellationRequested();
            await RequireFreshDeletionCopyInventoryAsync(mutation, context, ct);
            var reserved = await queue.ReservePreflightedRemoteMutationAsync(
                quote, accountId, context.NetworkId, remote, DateTimeOffset.UtcNow, ct);

            string? hash = null;
            Exception? submissionError = null;
            try
            {
                await RequireConnectedRootAsync(context, ct);
                ct.ThrowIfCancellationRequested();
                LedgerMutationResult outcome;
                if (reserved.Kind is VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder)
                {
                    outcome = await ledger.DeleteAsync(reserved.EntityId);
                }
                else
                {
                    var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(reserved.EncryptedPayload)
                        ?? throw new InvalidDataException("Reserved update draft was invalid.");
                    outcome = await ledger.UpdateAsync(reserved.EntityId, draft);
                }
                // The mutation's identity is the manifest WRITE for updates
                // or manifest DELETE for deletions, not a later cleanup tx.
                // If the writer did not report the primary receipt, quarantine
                // without assigning an unverifiable cleanup hash.
                hash = outcome.CommitTransactionHash;
            }
            catch (Exception ex) { submissionError = ex; }
            if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit))
                hash = null;
            try { await queue.MarkUncertainAsync(reserved, hash, CancellationToken.None); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Submission state could not be persisted. The durable Submitting entry MUST be investigated; do not retry.", ex);
            }
            return new VaultManualSyncOutcome(reserved.Id, hash, VaultSyncState.Uncertain,
                submissionError is null
                    ? reserved.Kind is VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder
                        ? "Update manifest submitted. Inspect the recorded commit hash with authenticated historical verification; do not retry."
                        : "Deletion attempted. Tombstone confirmation is unavailable; do not retry."
                    : "Remote update/delete outcome unknown. The mutation is quarantined; do not retry.");
        }
        finally { submissionGate.Release(); }
    }
}

public sealed record VaultManualSyncOutcome(
    string MutationId, string? TransactionHash, VaultSyncState State, string Message);
