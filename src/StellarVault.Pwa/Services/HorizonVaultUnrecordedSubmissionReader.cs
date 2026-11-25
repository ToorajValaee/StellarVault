using System.Net;
using System.Text.Json;
using VaultEngine.Stellar.Services;
using StellarVaultPwa.Core;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Read-only, bounded root-funded transaction history investigation when the
/// encrypted queue has no transaction hash. Positive matches only mean that
/// some historical transaction published the approved ciphertext; they CANNOT
/// be uniquely attributed to this interrupted queue attempt.
/// </summary>
public sealed class HorizonVaultUnrecordedSubmissionReader(
    HttpClient client,
    VaultNetworkSelection networks,
    VaultSessionManager sessions,
    VaultDeviceRetentionGate retention,
    IStellarLedgerRecords ledger,
    BrowserStellarLedgerRecords connected,
    HorizonVaultManifestInspector manifests,
    HorizonVaultPublicationDiagnostic historical)
    : IVaultUnrecordedSubmissionReader
{
    private const int MaxTransactions = 32;
    private const int MaxBytes = 256 * 1024;

    public async Task<VaultUnrecordedSubmissionFinding> InspectAsync(
        VaultPendingMutation mutation, CancellationToken ct = default)
    {
        static VaultUnrecordedSubmissionFinding Reject(string reason) =>
            new([], 0, false, reason);
        if (!retention.AllowsLocalPersistence || !sessions.IsUnlocked ||
            sessions.ActiveAccountId != mutation.AccountId ||
            !ledger.IsConnected || !connected.IsConnected ||
            mutation.TransactionHash is not null ||
            mutation.State is not (VaultSyncState.Uncertain or VaultSyncState.Submitting) ||
            mutation.Kind is not (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder or
                VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder) ||
            mutation.EntityId is not { Length: 16 } ||
            !mutation.EntityId.All(Uri.IsHexDigit))
            return Reject("Unrecorded transaction investigation is unavailable for this mutation.");
        var epoch = sessions.Epoch;
        var selected = networks.Network;
        var customHorizon = networks.CustomHorizonUrl;
        var customPassphrase = networks.CustomPassphrase;
        var scope = networks.CredentialKey(mutation.AccountId);
        if (scope != mutation.NetworkId ||
            mutation.Kind is VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder &&
                selected != "testnet")
            return Reject("Queue network scope or experimental update network does not match.");

        var (endpoint, passphrase) = selected switch
        {
            "testnet" => ("https://horizon-testnet.stellar.org", StellarNetworkPassphrases.Testnet),
            "public" => ("https://horizon.stellar.org", StellarNetworkPassphrases.Public),
            "custom" => (customHorizon, customPassphrase ?? ""),
            _ => ("", "")
        };
        if (string.IsNullOrWhiteSpace(passphrase) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || origin.UserInfo.Length != 0 ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 ||
            origin.Fragment.Length != 0)
            return Reject("A direct HTTPS Horizon network root is required.");

        LedgerRecordDraft? draft;
        try { draft = JsonSerializer.Deserialize<LedgerRecordDraft>(mutation.EncryptedPayload); }
        catch (JsonException) { return Reject("Queued draft cannot be authenticated."); }
        if (draft is null || string.IsNullOrWhiteSpace(draft.Title) ||
            string.IsNullOrEmpty(draft.Content) ||
            draft.IsFolder != (mutation.Kind is VaultMutationKind.CreateFolder or VaultMutationKind.UpdateFolder))
            return Reject("Queued draft has an invalid record/folder kind.");

        try
        {
            ct.ThrowIfCancellationRequested();
            var root = await ledger.InspectAccountInfoAsync();
            if (root.RootAccountId != mutation.AccountId)
                return Reject("The connected HD signing root differs from the queued account.");

            // Root is the fee-paying source of all the legacy HD-v2 sponsored
            // ManageData transactions. Only inspect the latest bounded window.
            // This window is NEVER asserted complete, even if zero matches.
            var expected = new Uri(origin, "accounts/" + mutation.AccountId +
                "/transactions?order=desc&limit=100");
            using var request = new HttpRequestMessage(HttpMethod.Get, expected);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.OK ||
                response.RequestMessage?.RequestUri is not Uri actual ||
                actual.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(actual.Authority, expected.Authority, StringComparison.OrdinalIgnoreCase) ||
                actual.AbsolutePath != expected.AbsolutePath ||
                actual.Query != expected.Query || actual.Fragment.Length != 0 ||
                response.Content.Headers.ContentLength is > MaxBytes)
                return Reject("Horizon transaction list could not be read at the selected endpoint.");
            if (response.Content.Headers.ContentType?.MediaType is string mediaType &&
                !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) &&
                !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
                return Reject("Horizon history response was not JSON.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var bounded = new MemoryStream();
            var buffer = new byte[8192];
            try
            {
                while (true)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(), ct);
                    if (n == 0) break;
                    if (bounded.Length + n > MaxBytes)
                        return Reject("Historical search response exceeded the safety bound.");
                    bounded.Write(buffer, 0, n);
                }
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer); }
            bounded.Position = 0;
            using var json = await JsonDocument.ParseAsync(bounded, cancellationToken: ct);
            if (!json.RootElement.TryGetProperty("_embedded", out var embedded) ||
                !embedded.TryGetProperty("records", out var records) ||
                records.ValueKind != JsonValueKind.Array ||
                records.GetArrayLength() > 100)
                return Reject("Historical transaction page was malformed or truncated.");

            var matches = new List<string>();
            var checkedTransactions = 0;
            string? previousPagingToken = null;
            foreach (var tx in records.EnumerateArray())
            {
                if (checkedTransactions >= MaxTransactions) break;
                if (tx.ValueKind != JsonValueKind.Object ||
                    !tx.TryGetProperty("paging_token", out var token) ||
                    token.ValueKind != JsonValueKind.String ||
                    token.GetString() is not string pagingToken ||
                    !long.TryParse(pagingToken, out var tokenNumber) ||
                    tokenNumber <= 0 ||
                    previousPagingToken is not null &&
                        (!long.TryParse(previousPagingToken, out var previousNumber) ||
                         tokenNumber >= previousNumber))
                    return Reject("Transaction history ordering could not be authenticated.");
                previousPagingToken = pagingToken;
                checkedTransactions++;
                if (!tx.TryGetProperty("hash", out var hashElement) ||
                    hashElement.ValueKind != JsonValueKind.String ||
                    hashElement.GetString() is not { Length: 64 } hash ||
                    !hash.All(Uri.IsHexDigit) ||
                    !tx.TryGetProperty("successful", out var success) ||
                    success.ValueKind != JsonValueKind.True ||
                    !tx.TryGetProperty("source_account", out var source) ||
                    source.GetString() != mutation.AccountId)
                    continue;

                var manifest = await manifests.InspectManifestAsync(
                    endpoint, hash, mutation.EntityId, ct);
                if (manifest is null) continue;
                var index = await connected.ResolveAuthenticatedHdChildAsync(
                    mutation.AccountId, manifest.OperationSourceAccount,
                    endpoint, passphrase, ct);
                if (index is null) continue;
                var proof = await historical.InspectHistoricalRecordAsync(
                    endpoint, passphrase, hash, mutation.EntityId,
                    mutation.AccountId, manifest.OperationSourceAccount,
                    index.Value, draft, ct);
                if (proof.EndpointNetworkMatches && proof.HistoricalChunksAvailable &&
                    proof.HistoricalRecordMatches)
                    matches.Add(hash.ToLowerInvariant());
            }

            if (!sessions.IsUnlocked || sessions.ActiveAccountId != mutation.AccountId ||
                sessions.Epoch != epoch || !ledger.IsConnected || !connected.IsConnected ||
                networks.Network != selected ||
                networks.CustomHorizonUrl != customHorizon ||
                networks.CustomPassphrase != customPassphrase ||
                networks.CredentialKey(mutation.AccountId) != scope)
                return Reject("Signing session or selected network changed during search.");

            return new(matches.Distinct(StringComparer.Ordinal).ToArray(),
                checkedTransactions, false,
                matches.Count == 0
                    ? "No matching publication was authenticated within the bounded recent transaction window. Older or pruned history was not checked; absence of a match is NOT proof of failure. Do not retry."
                    : "Matching historical publication candidate(s) found. Their exact transaction receipts, HD manifests, encrypted chunks and approved plaintext matched, but no candidate can be attributed uniquely to this submission without the recorded hash. Queue remains Uncertain; do not retry.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Reject("Historical candidate search failed closed. No queue changes were made."); }
    }
}
