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
    VaultNetworkSelection networks)
{
    private readonly SemaphoreSlim submissionGate = new(1, 1);
    public async Task<bool> CanPublishApprovedCreateAsync(
        VaultSyncApprovalSnapshot quote, string accountId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(quote);
        retention.RequireLocalPersistence();
        if (!ledger.IsConnected) return false;
        var network = networks.CredentialKey(accountId);
        if (quote.AccountId != accountId || quote.NetworkId != network) return false;
        try
        {
            var review = await queue.PrepareReviewAsync(accountId, network, ct);
            var mutation = review.Ready.SingleOrDefault(x => x.Id == quote.MutationId);
            if (mutation is null) return false;
            VaultSyncApprovalGuard.RequireFresh(quote, mutation, accountId, network,
                DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
            var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(mutation.EncryptedPayload);
            if (draft is null) return false;
            var remote = await ledger.ReadAsync();
            await queue.PreflightCreateAsync(quote, accountId, network, remote, DateTimeOffset.UtcNow, ct);
            var cost = await ledger.EstimateAsync(draft, mutation.EntityId);
            return ledger.IsConnected && networks.CredentialKey(accountId) == network &&
                cost.NetworkFeeXlm <= quote.QuotedFeeXlm &&
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
        var network = networks.CredentialKey(accountId);
        if (quote.AccountId != accountId || quote.NetworkId != network)
            throw new InvalidOperationException("Approved quote does not belong to this account/network.");

        if (!await submissionGate.WaitAsync(0, ct)) throw new InvalidOperationException("Another submission is active.");
        try {
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
        var cost = await ledger.EstimateAsync(draft, mutation.EntityId);
        if (cost.NetworkFeeXlm > quote.QuotedFeeXlm ||
            cost.PeakReserveXlm > quote.QuotedPeakReserveXlm)
            throw new InvalidOperationException("Transaction costs exceed approved quote.");

        // Durable CAS reservation is the no-replay boundary. No automatic retries
        // are allowed after this point, even when publishing throws.
        if (!ledger.IsConnected || networks.CredentialKey(accountId) != network)
            throw new InvalidOperationException("Ledger selection changed; review again.");
        ct.ThrowIfCancellationRequested();
        var reserved = await queue.ReservePreflightedSubmissionAsync(
            quote, accountId, network, remote, DateTimeOffset.UtcNow, ct);
        string? txHash = null;
        Exception? submissionError = null;
        try
        {
            if (!ledger.IsConnected || networks.CredentialKey(accountId) != network)
                throw new InvalidOperationException("Ledger selection changed after reservation.");
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
}

public sealed record VaultManualSyncOutcome(
    string MutationId, string? TransactionHash, VaultSyncState State, string Message);
