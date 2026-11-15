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

            return new(true,
                "Horizon reports a successful transaction from the expected root. Exact account/record operations, child signer state and historical encrypted bytes have NOT been independently verified.",
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
