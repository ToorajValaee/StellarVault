namespace StellarVault.Core.Services;

/// <summary>
/// Read-only recovery finding for an uncertain linked-key transaction.
/// "Observed" is never a transaction receipt or independent historical proof.
/// The UI must not use it to clear the write-quarantine.
/// </summary>
public enum VaultLinkedRecoveryStatus
{
    NoDiagnostic = 0,
    CouldNotInspect = 1,
    ExpectedChildObserved = 2,
    ChildNotObserved = 3,
    RecordObserved = 4,
    RecordNotObserved = 5
}

public sealed record VaultLinkedRecoveryFinding(
    VaultLinkedRecoveryStatus Status,
    string Explanation,
    VaultSecretSubmissionDiagnostic? Diagnostic,
    VaultLinkedReceiptObservation? Receipt = null);

/// <summary>
/// Provides a bounded, read-only assessment using the already-connected account
/// and decrypted record inventory. It deliberately has no mutation methods.
/// </summary>
public sealed class VaultLinkedRecoveryInspector(
    IStellarSecretKeyLedger ledger, IVaultLinkedReceiptEvidence receipts)
{
    public async Task<VaultLinkedRecoveryFinding> InspectAsync(
        CancellationToken ct = default)
    {
        var diagnostic = await ledger.InspectUncertainSubmissionAsync(ct);
        if (diagnostic is null)
            return new(VaultLinkedRecoveryStatus.NoDiagnostic,
                "No volatile diagnostic is available. Absence does not establish that no transaction was submitted.", null);

        try
        {
            ct.ThrowIfCancellationRequested();
            var account = await ledger.InspectAccountInfoAsync(ct);
            if (!string.Equals(account.RootAccountId, diagnostic.RootAccountId, StringComparison.Ordinal))
                throw new InvalidDataException("The connected account root changed.");

            var historical = await receipts.InspectAsync(diagnostic, ct);
            if (diagnostic.Kind == "expand")
            {
                var seen = string.Equals(account.TailAccountId, diagnostic.TargetAccountId,
                    StringComparison.Ordinal);
                return new(seen ? VaultLinkedRecoveryStatus.ExpectedChildObserved :
                    VaultLinkedRecoveryStatus.ChildNotObserved,
                    seen
                        ? "The expected child is visible as the current tail. This is not historical signer or transaction inclusion proof."
                        : "The expected child is not the current tail. It may be absent, unconfirmed, or followed by later accounts; do not retry.",
                    diagnostic, historical);
            }

            var items = await ledger.ReadAsync(ct);
            var present = items.Any(x => string.Equals(x.Id, diagnostic.RecordId, StringComparison.Ordinal));
            var observation = diagnostic.Kind == "delete" ? !present : present;
            return new(observation ? VaultLinkedRecoveryStatus.RecordObserved :
                    VaultLinkedRecoveryStatus.RecordNotObserved,
                observation
                    ? "The current record visibility matches the requested operation, but does not prove the exact approved revision or historical inclusion."
                    : "The current record visibility does not establish the requested operation. It must not be retried without historical reconciliation.",
                diagnostic, historical);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return new(VaultLinkedRecoveryStatus.CouldNotInspect,
                "The connected read-only inspection failed. The transaction outcome remains uncertain.", diagnostic);
        }
    }
}
