using StellarVault.Core.Services;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Read-only advisory evidence: checks selected HTTPS Horizon network identity,
/// then a successful transaction receipt with the expected root as source.
/// Does NOT inspect operation bodies, child authorization or encrypted record
/// bytes, and must never be used to mark an uncertain mutation confirmed.
/// </summary>
public sealed class HorizonVaultLinkedReceiptEvidence(
    HorizonVaultNetworkInspector network,
    HorizonVaultTransactionInspector transactions,
    HorizonVaultLinkedRecordOperationInspector records) : IVaultLinkedReceiptEvidence
{
    public async Task<VaultLinkedReceiptObservation> InspectAsync(
        VaultSecretSubmissionDiagnostic diagnostic, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (diagnostic.TransactionHash is null)
            return new(false, "Horizon returned no transaction hash; the outcome remains unknown.");
        try
        {
            var verified = await network.InspectAsync(
                diagnostic.HorizonUrl, diagnostic.NetworkPassphrase, ct);
            if (!verified.Matches)
                return new(false, "The selected Horizon network identity could not be verified.");

            var receipt = await transactions.InspectAsync(
                diagnostic.HorizonUrl, diagnostic.TransactionHash, ct);
            if (receipt is null || receipt.SourceAccount != diagnostic.RootAccountId)
                return new(false,
                    "No successful transaction receipt from the expected root was established on the selected network.");

            if (diagnostic.Kind == "create" &&
                diagnostic.RecordId is { Length: 16 } recordId)
            {
                var inspected = await records.InspectCreateAsync(
                    diagnostic.HorizonUrl, diagnostic.TransactionHash, recordId, ct);
                return new(true, inspected.Explanation +
                    " Successful transaction receipt observed from the expected root, but exact historical signer authorization and decrypted draft equality remain unverified. The queue must remain quarantined.",
                    receipt.Ledger, inspected.ManifestAndChunksMatch, inspected.StorageAccount);
            }
            return new(true,
                "Horizon reports a successful transaction from the expected root. Exact account expansion, signer policy and network operation history remain unverified.",
                receipt.Ledger);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return new(false,
                "Read-only historical receipt inspection was unavailable. Do not retry or clear quarantine.");
        }
    }
}
