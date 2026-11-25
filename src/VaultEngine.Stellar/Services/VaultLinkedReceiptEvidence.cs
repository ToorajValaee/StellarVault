namespace VaultEngine.Stellar.Services;

/// <summary>
/// Advisory historical receipt observation. Even a successful Horizon response is
/// not proof of the exact encrypted record revision, child signer policy or ledger
/// consensus. Never use this value to clear a transaction quarantine.
/// </summary>
public sealed record VaultLinkedReceiptObservation(
    bool ReceiptObserved, string Explanation, long? LedgerSequence = null,
    bool HistoricalRecordOperationsMatch = false, string? StorageAccount = null,
    bool HistoricalExpansionOperationsMatch = false, string? HistoricalParentAccount = null);

public interface IVaultLinkedReceiptEvidence
{
    Task<VaultLinkedReceiptObservation> InspectAsync(
        VaultSecretSubmissionDiagnostic diagnostic, CancellationToken ct = default);
}

/// <summary>Fail closed when no historical Horizon evidence adapter is configured.</summary>
public sealed class DenyVaultLinkedReceiptEvidence : IVaultLinkedReceiptEvidence
{
    public Task<VaultLinkedReceiptObservation> InspectAsync(
        VaultSecretSubmissionDiagnostic diagnostic, CancellationToken ct = default) =>
        Task.FromResult(new VaultLinkedReceiptObservation(false,
            "Independent historical transaction evidence is unavailable on this host."));
}
