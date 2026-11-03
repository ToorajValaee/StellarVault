using StellarVault.Core.Services;

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
    HorizonVaultHistoricalChunkInspector history,
    HorizonVaultNetworkInspector networks,
    BrowserStellarLedgerRecords connectedRecords)
{
    public Task<HorizonVaultPublicationFinding> InspectAsync(
        string endpoint, string hash, string recordId, CancellationToken ct = default) =>
        InspectForNodeAsync(endpoint, hash, recordId, null, ct);

    /// <summary>
    /// Compare the EXACT historically published encrypted bytes, after verifying
    /// network, final transaction, manifest, and chunk history, through the
    /// connected HD session. Still never a queue confirmation provider.
    /// </summary>
    public async Task<HorizonVaultHistoricalRecordFinding> InspectHistoricalRecordAsync(
        string endpoint, string expectedPassphrase, string transactionHash,
        string recordId, string expectedRootAccount, string expectedNodeAccount,
        int expectedNodeIndex, LedgerRecordDraft approvedDraft,
        CancellationToken ct = default)
    {
        static HorizonVaultHistoricalRecordFinding Reject(string reason) =>
            new(false, false, false, reason);

        if (approvedDraft is null ||
            string.IsNullOrWhiteSpace(approvedDraft.Title) ||
            string.IsNullOrEmpty(approvedDraft.Content) ||
            transactionHash is not { Length: 64 } || !transactionHash.All(Uri.IsHexDigit) ||
            recordId is not { Length: 16 } || !recordId.All(Uri.IsHexDigit) ||
            expectedRootAccount is not { Length: 56 } ||
            expectedNodeAccount is not { Length: 56 } ||
            expectedNodeIndex is <= 0 or >= 64)
            return Reject("Historical record verification inputs are invalid.");

        try
        {
            var network = await networks.InspectAsync(endpoint, expectedPassphrase, ct);
            if (!network.Matches)
                return Reject(network.Explanation);

            var receipt = await transactions.InspectAsync(endpoint, transactionHash, ct);
            if (receipt is null)
                return new(true, false, false, "Manifest transaction receipt unavailable.");

            var manifest = await manifests.InspectManifestAsync(
                endpoint, transactionHash, recordId, ct);
            if (manifest is null || manifest.ManifestOperationId <= 0 ||
                !string.Equals(manifest.OperationSourceAccount,
                    expectedNodeAccount, StringComparison.Ordinal))
                return new(true, false, false,
                    "Manifest was not published by the expected HD child account.");

            var historyResult = await history.InspectAndCompareAsync(
                endpoint, manifest, receipt.Ledger,
                (ciphertext, token) => connectedRecords.CompareHistoricalEncryptedRecordAsync(
                    expectedRootAccount, expectedNodeAccount, expectedNodeIndex,
                    endpoint, expectedPassphrase, recordId, manifest.Generation,
                    ciphertext, approvedDraft, token),
                ct);

            // A combined positive result establishes an endpoint-attested,
            // unlocked-session historical record match, but is still not
            // independently audited chain lineage or queue authorization.
            return new(true, historyResult.VerifiedChunkCount == manifest.ChunkCount,
                historyResult.ChunksPublishedBeforeManifest, historyResult.Explanation);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Reject("Historical record comparison failed closed."); }
    }

    /// <summary>
    /// Strictly diagnostic: require a matching selected Horizon network, historical
    /// ciphertext commitment, matching current snapshot, and decryption through the
    /// already-open HD session. Confirmation still requires an audited provider and
    /// immutable record-to-transaction proof; no queue state is changed here.
    /// </summary>
    public async Task<HorizonVaultRecordComparisonFinding> InspectBoundRecordAsync(
        string endpoint, string expectedNetworkPassphrase, string hash, string recordId,
        string expectedRootAccount, string expectedNodeAccount, int expectedNodeIndex,
        LedgerRecordDraft approvedDraft, CancellationToken ct = default)
    {
        static HorizonVaultRecordComparisonFinding Reject(string reason) =>
            new(false, false, false, false, reason);

        if (approvedDraft is null || string.IsNullOrWhiteSpace(approvedDraft.Title) ||
            string.IsNullOrEmpty(approvedDraft.Content))
            return Reject("Approved record draft is missing or malformed.");

        try
        {
            var history = await InspectBoundHistoryAsync(endpoint, expectedNetworkPassphrase,
                hash, recordId, expectedNodeAccount, ct);
            if (!history.EndpointNetworkMatches || !history.HistoricalCiphertextMatches)
                return new(history.EndpointNetworkMatches, history.HistoricalCiphertextMatches,
                    false, false, history.Explanation);

            var snapshot = await InspectCurrentSnapshotAsync(
                endpoint, hash, recordId, expectedNodeAccount, ct);
            if (!snapshot.CurrentManifestMatches || !snapshot.CurrentChunksMatch)
                return new(true, true, false, false,
                    "Current account ciphertext does not match the historically inspected manifest.");

            var decrypted = await connectedRecords.InspectConnectedRecordAsync(
                expectedRootAccount, expectedNodeAccount, expectedNodeIndex, endpoint,
                expectedNetworkPassphrase, recordId, approvedDraft, ct);

            return new(true, true, true, decrypted.Matches,
                decrypted.Matches
                    ? "Historical/current ciphertext and current decrypted record match; this diagnostic is NOT queue confirmation."
                    : decrypted.Explanation);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Reject("Bound record comparison failed closed."); }
    }

    /// <summary>
    /// Combined read-only network-passphrase and historical chunk diagnostic.
    /// Never authorizes queue confirmation, key use, or publication.
    /// </summary>
    public async Task<HorizonVaultBoundPublicationFinding> InspectBoundHistoryAsync(
        string endpoint, string expectedPassphrase, string hash,
        string recordId, string expectedNodeAccount, CancellationToken ct = default)
    {
        var network = await networks.InspectAsync(endpoint, expectedPassphrase, ct);
        if (!network.Matches)
            return new(false, false, network.Explanation, null);
        var historical = await InspectHistoricalChunksAsync(
            endpoint, hash, recordId, expectedNodeAccount, ct);
        return new(true, historical.ChunksPublishedBeforeManifest,
            historical.Explanation, network.NetworkIdHex);
    }

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

        if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit) ||
            recordId is not { Length: 16 } || !recordId.All(Uri.IsHexDigit) ||
            expectedNodeAccount is not { Length: 56 } ||
            expectedNodeAccount[0] != 'G' ||
            !expectedNodeAccount.All(c => c is >= 'A' and <= 'Z' or >= '2' and <= '7'))
            return Reject("Historical inspection inputs are invalid.");

        try
        {
            // Inspect each immutable on-chain transaction once, rather than
            // re-fetching its receipt and manifest through a preliminary call.
            var receipt = await transactions.InspectAsync(endpoint, hash, ct);
            if (receipt is null)
                return Reject("Successful final transaction receipt unavailable.");

            var manifest = await manifests.InspectManifestAsync(endpoint, hash, recordId, ct);
            if (manifest is null || manifest.ManifestOperationId <= 0 ||
                !string.Equals(manifest.TransactionHash, hash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifest.OperationSourceAccount,
                    expectedNodeAccount, StringComparison.Ordinal))
                return Reject("Final transaction lacks a valid manifest written by the expected HD child.");

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

/// <summary>
/// Diagnostic only. Even a true ciphertext result does not prove root HD lineage,
/// decrypted record fields, or an approved queued mutation.
/// </summary>
public sealed record HorizonVaultBoundPublicationFinding(
    bool EndpointNetworkMatches,
    bool HistoricalCiphertextMatches,
    string Explanation,
    string? NetworkIdHex);

/// <summary>
/// Read-only diagnostics of a connected root and current HD-v2 record.
/// Never indicates that publishing or queue confirmation may proceed.
/// </summary>
public sealed record HorizonVaultRecordComparisonFinding(
    bool EndpointNetworkMatches,
    bool HistoricalCiphertextMatches,
    bool CurrentCiphertextMatches,
    bool CurrentDecryptedRecordMatches,
    string Explanation);

/// <summary>
/// Endpoint-attested read-only historical record comparison. Never implies
/// approval to broadcast, retry or mark a mutation Confirmed.
/// </summary>
public sealed record HorizonVaultHistoricalRecordFinding(
    bool EndpointNetworkMatches,
    bool HistoricalChunksAvailable,
    bool HistoricalRecordMatches,
    string Explanation);
