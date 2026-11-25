using VaultEngine.Core;

namespace VaultEngine.Stellar.Services;

public sealed partial class StellarAccountObjectStoreProvider
{
    private readonly SemaphoreSlim approvalGate = new(1, 1);
    private PendingObjectApproval? pendingObjectApproval;

    // Retain immutable public quotes only, never a decrypted queue payload.
    private sealed record PendingObjectApproval(
        ObjectStoreScope Scope, string Reference, string OperationId, string ObjectId,
        long UnlockEpoch, long SignerEpoch, string Endpoint, string? Passphrase,
        DateTimeOffset CapturedAt, VaultMutationKind? Kind,
        VaultSyncApprovalSnapshot? HdQuote, VaultSecretLedgerQuote? LinkedQuote);

    private string CaptureApproval(ObjectStoreScope scope, string operationId, string objectId,
        VaultMutationKind? kind, VaultSyncApprovalSnapshot? hdQuote, VaultSecretLedgerQuote? linkedQuote)
    {
        var reference = Guid.NewGuid().ToString("N");
        pendingObjectApproval = new(scope, reference, operationId, objectId,
            scope.AccountStrategyId == VaultMutationStrategyPolicy.Linked ? linkedKeys.UnlockEpoch : hdSessions.Epoch,
            scope.AccountStrategyId == VaultMutationStrategyPolicy.Linked ? linkedLedger.ConnectionEpoch : 0,
            networks.CustomHorizonUrl, networks.CustomPassphrase, DateTimeOffset.UtcNow, kind, hdQuote, linkedQuote);
        return reference;
    }

    private async Task ReleaseApprovalAsync(PendingObjectApproval? pending)
    {
        if (pending?.LinkedQuote is not null && linkedPublication is not null)
            await linkedPublication.CancelAsync(CancellationToken.None);
    }

    public async Task<ObjectMutationReview> ReviewAsync(
        ObjectStoreScope scope, string operationId, CancellationToken ct = default)
    {
        await approvalGate.WaitAsync(ct);
        try
        {
            var old = pendingObjectApproval;
            pendingObjectApproval = null;
            await ReleaseApprovalAsync(old);
            return await ReviewCoreAsync(scope, operationId, ct);
        }
        finally { approvalGate.Release(); }
    }

    public async Task CancelReviewAsync(ObjectStoreScope scope, CancellationToken ct = default)
    {
        await approvalGate.WaitAsync(ct);
        try
        {
            var pending = pendingObjectApproval;
            pendingObjectApproval = null;
            await ReleaseApprovalAsync(pending);
            await CancelReviewCoreAsync(scope, ct);
        }
        finally { approvalGate.Release(); }
    }

    public async Task<ObjectMutationReconciliation> ReconcileAsync(
        ObjectStoreScope scope, string operationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        await approvalGate.WaitAsync(ct);
        try
        {
            var old = pendingObjectApproval;
            pendingObjectApproval = null;
            await ReleaseApprovalAsync(old);
            var checkpoint = RequireScope(scope, true);
            if (scope.AccountStrategyId == VaultMutationStrategyPolicy.Linked)
            {
                if (linkedReconciliation is null || !linkedKeys.CanPublish ||
                    linkedKeys.ActiveRootAccountId != scope.StoreId)
                    throw new UnauthorizedAccessException("Matching linked reconciliation session is unavailable.");
                var result = await linkedReconciliation.ConfirmVerifiedRecordAsync(operationId, ct);
                await RequireSameScopeAsync(scope, checkpoint, true);
                return new(result.MutationId,
                    result.Confirmed ? ObjectMutationState.Verified : ObjectMutationState.SubmittedUncertain,
                    result.Confirmed, result.Explanation);
            }
            if (hdQueue is null || !hdSessions.IsUnlocked || hdSessions.ActiveAccountId != scope.StoreId)
                throw new UnauthorizedAccessException("Matching HD reconciliation session is unavailable.");
            var epoch = hdSessions.Epoch;
            await RequireSameScopeAsync(scope, checkpoint, true);
            var finding = await hdQueue.ReconcileAsync(scope.StoreId, networks.CredentialKey(scope.StoreId), operationId, ct);
            await RequireSameScopeAsync(scope, checkpoint, true);
            if (!hdSessions.IsUnlocked || hdSessions.Epoch != epoch)
                throw new UnauthorizedAccessException("HD queue changed during reconciliation. Inspect durable state.");
            return new(finding.MutationId,
                finding.State == VaultSyncState.Confirmed ? ObjectMutationState.Verified :
                finding.State is VaultSyncState.Submitting or VaultSyncState.Uncertain ? ObjectMutationState.SubmittedUncertain : ObjectMutationState.Prepared,
                finding.State == VaultSyncState.Confirmed, finding.Explanation);
        }
        finally { approvalGate.Release(); }
    }
    public async Task<ObjectMutationSubmission> SubmitApprovedAsync(
        ObjectStoreScope scope, ObjectMutationApproval approval, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        await approvalGate.WaitAsync(ct);
        PendingObjectApproval? pending = null;
        try
        {
            pending = pendingObjectApproval;
            pendingObjectApproval = null; // Consumed even on invalid approval or failed preflight.
            if (pending is null || pending.Scope != scope ||
                pending.Reference != approval.ReviewReference)
                throw new InvalidOperationException("No matching one-shot review exists. Review again.");
            if (!approval.FeesAndReserveApproved || !approval.UncertainOutcomeAcknowledged)
                throw new UnauthorizedAccessException("Explicit fees, reserve and unknown-outcome approval is required.");
            var age = DateTimeOffset.UtcNow - pending.CapturedAt;
            if (age < TimeSpan.Zero || age > TimeSpan.FromMinutes(5))
                throw new InvalidOperationException("Review expired. Review again.");
            var checkpoint = RequireScope(scope, true);
            if (pending.Endpoint != networks.CustomHorizonUrl || pending.Passphrase != networks.CustomPassphrase)
                throw new InvalidOperationException("Selected network endpoint changed. Review again.");
            if ((networks.Network == "public" ||
                 networks.CustomPassphrase == "Public Global Stellar Network ; September 2015") &&
                !approval.PublicNetworkApproved)
                throw new UnauthorizedAccessException("Separate public-network approval is required.");
            await RequireSameScopeAsync(scope, checkpoint, true);
            ct.ThrowIfCancellationRequested();
            if (scope.AccountStrategyId == VaultMutationStrategyPolicy.Linked)
            {
                if (linkedPublication is null || pending.LinkedQuote is null ||
                    !linkedKeys.CanPublish || linkedKeys.ActiveRootAccountId != scope.StoreId ||
                    linkedKeys.UnlockEpoch != pending.UnlockEpoch ||
                    linkedLedger.ConnectionEpoch != pending.SignerEpoch)
                    throw new UnauthorizedAccessException("Linked signer or queue session changed. Review again.");
                var hash = await linkedPublication.SubmitAsync(pending.LinkedQuote, true, ct);
                return new(pending.OperationId, pending.ObjectId, ObjectMutationState.SubmittedUncertain,
                    hash, "Submission attempted; independently reconcile the durable journal. Never retry automatically.");
            }
            if (hdPublication is null || pending.HdQuote is null ||
                !hdSessions.IsUnlocked || hdSessions.ActiveAccountId != scope.StoreId ||
                hdSessions.Epoch != pending.UnlockEpoch)
                throw new UnauthorizedAccessException("HD queue session or publisher is unavailable. Review again.");
            var outcome = pending.Kind is VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder
                ? await hdPublication.PublishApprovedCreateAsync(pending.HdQuote, scope.StoreId, true, ct)
                : await hdPublication.PublishApprovedRemoteMutationAsync(pending.HdQuote, scope.StoreId, true, ct);
            return new(outcome.MutationId, pending.ObjectId, ObjectMutationState.SubmittedUncertain,
                outcome.TransactionHash, outcome.Message);
        }
        finally
        {
            // Cleanup must not hide a post-reservation unknown outcome or enable reuse.
            try { await ReleaseApprovalAsync(pending); } catch { }
            approvalGate.Release();
        }
    }
}
