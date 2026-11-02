namespace StellarVault.Pwa.Services;

/// <summary>
/// Independent read-only receipt and manifest diagnostics. Never enough to
/// confirm an offline queue mutation without record content and network proof.
/// </summary>
public sealed class HorizonVaultPublicationDiagnostic(
    HorizonVaultTransactionInspector transactions,
    HorizonVaultManifestInspector manifests)
{
    public async Task<HorizonVaultPublicationFinding> InspectAsync(
        string endpoint, string hash, string recordId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint) ||
            hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit) ||
            recordId is not { Length: 16 } || !recordId.All(Uri.IsHexDigit))
            return new(false, false, null, "Invalid inspection inputs.");

        try
        {
            var receipt = await transactions.InspectAsync(endpoint, hash, ct);
            if (receipt is null)
                return new(false, false, null, "Successful transaction receipt unavailable.");
            var found = await manifests.ContainsManifestWriteAsync(endpoint, hash, recordId, ct);
            return new(true, found, receipt.SourceAccount,
                found ? "Manifest operation found; full record and network verification remain unavailable."
                      : "No matching record manifest operation found.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new(false, false, null, "Horizon evidence could not be verified."); }
    }
}

public sealed record HorizonVaultPublicationFinding(
    bool TransactionSucceeded, bool ManifestFound, string? SourceAccount, string Explanation);
