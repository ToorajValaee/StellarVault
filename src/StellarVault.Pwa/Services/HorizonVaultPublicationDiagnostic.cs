namespace StellarVault.Pwa.Services;

/// <summary>
/// Read-only receipt and HD-v2 manifest diagnostics. This is NEVER sufficient
/// to confirm an offline queue mutation: encrypted chunk history and network
/// binding remain unproven.
/// </summary>
public sealed class HorizonVaultPublicationDiagnostic(
    HorizonVaultTransactionInspector transactions,
    HorizonVaultManifestInspector manifests,
    HorizonVaultCurrentChunkInspector chunks,
    HorizonVaultHistoricalChunkInspector history)
{
    public Task<HorizonVaultPublicationFinding> InspectAsync(
        string endpoint, string hash, string recordId, CancellationToken ct = default) =>
        InspectForNodeAsync(endpoint, hash, recordId, null, ct);

    /// <summary>
    /// Inspect successful pre-manifest chunk operations, their receipts and
    /// committed ciphertext hash. This is a read-only diagnostic, never queue proof.
    /// </summary>
    public async Task<HorizonVaultHistoricalChunkInspector.HistoricalFinding> InspectHistoricalChunksAsync(
        string endpoint, string hash, string recordId, string expectedNodeAccount,
        CancellationToken ct = default)
    {
        static HorizonVaultHistoricalChunkInspector.HistoricalFinding Reject(string reason) =>
            new(false, 0, 0, reason);

        if (expectedNodeAccount is not { Length: 56 } ||
            expectedNodeAccount[0] != 'G' ||
            !expectedNodeAccount.All(c => c is >= 'A' and <= 'Z' or >= '2' and <= '7'))
            return Reject("Expected HD child account is invalid.");

        try
        {
            var preliminary = await InspectForNodeAsync(
                endpoint, hash, recordId, expectedNodeAccount, ct);
            if (!preliminary.TransactionSucceeded || !preliminary.ManifestFound ||
                !string.Equals(preliminary.ManifestOperationSourceAccount,
                    expectedNodeAccount, StringComparison.Ordinal))
                return Reject("Receipt and expected HD child manifest were not established.");

            var receipt = await transactions.InspectAsync(endpoint, hash, ct);
            var manifest = await manifests.InspectManifestAsync(endpoint, hash, recordId, ct);
            if (receipt is null || manifest is null ||
                !string.Equals(manifest.OperationSourceAccount,
                    expectedNodeAccount, StringComparison.Ordinal))
                return Reject("Manifest receipt or child-account linkage changed.");

            return await history.InspectAsync(endpoint, manifest, receipt.Ledger, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Reject("Historical chunk diagnostic failed closed."); }
    }

    /// <summary>
    /// Opt-in read-only current-account snapshot check. This deliberately does NOT
    /// set confirmation eligibility: snapshot state cannot prove chunk transaction
    /// history or the intended network passphrase.
    /// </summary>
    public async Task<HorizonVaultCurrentChunkInspector.SnapshotFinding> InspectCurrentSnapshotAsync(
        string endpoint, string hash, string recordId, string expectedNodeAccount,
        CancellationToken ct = default)
    {
        var preliminary = await InspectForNodeAsync(endpoint, hash, recordId, expectedNodeAccount, ct);
        if (!preliminary.TransactionSucceeded || !preliminary.ManifestFound ||
            !string.Equals(preliminary.ManifestOperationSourceAccount, expectedNodeAccount, StringComparison.Ordinal))
            return new(false, false, 0, "Receipt and expected HD child manifest were not established.");

        try
        {
            var manifest = await manifests.InspectManifestAsync(endpoint, hash, recordId, ct);
            if (manifest is null ||
                !string.Equals(manifest.OperationSourceAccount, expectedNodeAccount, StringComparison.Ordinal))
                return new(false, false, 0, "Manifest evidence changed during snapshot inspection.");

            return await chunks.InspectAsync(endpoint, manifest, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new(false, false, 0, "Current account snapshot could not be verified."); }
    }

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
