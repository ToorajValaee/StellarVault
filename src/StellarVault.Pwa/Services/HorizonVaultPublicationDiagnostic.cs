namespace StellarVault.Pwa.Services;

/// <summary>
/// Read-only receipt and HD-v2 manifest diagnostics. This is NEVER sufficient
/// to confirm an offline queue mutation: encrypted chunk history and network
/// binding remain unproven.
/// </summary>
public sealed class HorizonVaultPublicationDiagnostic(
    HorizonVaultTransactionInspector transactions,
    HorizonVaultManifestInspector manifests)
{
    public Task<HorizonVaultPublicationFinding> InspectAsync(
        string endpoint, string hash, string recordId, CancellationToken ct = default) =>
        InspectForNodeAsync(endpoint, hash, recordId, null, ct);

    /// <summary>
    /// When known, bind the manifest operation's source account to the expected
    /// deterministic HD child account (not the sponsored transaction source).
    /// </summary>
    public async Task<HorizonVaultPublicationFinding> InspectForNodeAsync(
        string endpoint, string hash, string recordId, string? expectedNodeAccount,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint) ||
            hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit) ||
            recordId is not { Length: 16 } || !recordId.All(Uri.IsHexDigit) ||
            (expectedNodeAccount is not null &&
             (expectedNodeAccount.Length != 56 || expectedNodeAccount[0] != 'G' ||
              !expectedNodeAccount.All(c => c is >= 'A' and <= 'Z' or >= '2' and <= '7'))))
            return new(false, false, null, "Invalid inspection inputs.");

        try
        {
            var receipt = await transactions.InspectAsync(endpoint, hash, ct);
            if (receipt is null)
                return new(false, false, null, "Successful transaction receipt unavailable.");

            var manifest = await manifests.InspectManifestAsync(endpoint, hash, recordId, ct);
            if (manifest is null)
                return new(true, false, receipt.SourceAccount,
                    "No unique, structurally valid HD-v2 manifest operation was found.");

            if (expectedNodeAccount is not null &&
                !string.Equals(expectedNodeAccount, manifest.OperationSourceAccount, StringComparison.Ordinal))
                return new(true, false, receipt.SourceAccount,
                    "Manifest operation source does not match the expected HD child account.",
                    manifest.OperationSourceAccount);

            return new(true, true, receipt.SourceAccount,
                "HD-v2 manifest structure and operation source inspected; chunk history, decrypted record and network lineage remain unverified.",
                manifest.OperationSourceAccount, manifest.ChunkCount, manifest.EncryptedLength,
                manifest.Generation, manifest.CiphertextSha256);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new(false, false, null, "Horizon evidence could not be verified."); }
    }
}

public sealed record HorizonVaultPublicationFinding(
    bool TransactionSucceeded,
    bool ManifestFound,
    string? SourceAccount,
    string Explanation,
    string? ManifestOperationSourceAccount = null,
    int? ExpectedChunkCount = null,
    int? EncryptedLength = null,
    string? Generation = null,
    string? CiphertextSha256 = null);
