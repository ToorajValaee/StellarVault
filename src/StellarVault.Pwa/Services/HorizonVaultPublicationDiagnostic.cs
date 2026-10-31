namespace StellarVault.Pwa.Services;

public sealed class HorizonVaultPublicationDiagnostic(
    HorizonVaultTransactionInspector transactions,
    HorizonVaultManifestInspector manifests)
{
    public async Task<HorizonVaultPublicationFinding> InspectAsync(
        string endpoint, string hash, string recordId, CancellationToken ct = default)
    {
        var receipt = await transactions.InspectAsync(endpoint, hash, ct);
        if (receipt is null)
            return new(false, false, null, "Transaction receipt unavailable.");
        var found = await manifests.ContainsManifestWriteAsync(endpoint, hash, recordId, ct);
        return new(true, found, receipt.SourceAccount,
            found ? "Manifest operation found; full record verification remains unavailable."
                  : "No matching record manifest operation found.");
    }
}

public sealed record HorizonVaultPublicationFinding(
    bool TransactionSucceeded, bool ManifestFound, string? SourceAccount, string Explanation);
