using System.Net;
using System.Text.Json;
using StellarVault.Core.Services;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Strict, bounded inspection of one historical manifest-deletion transaction.
/// A tombstone is NOT sufficient to confirm the complete queued deletion:
/// it cannot alone authenticate the former decrypted record or rule out
/// another manifest copy on a different child. Do not register as a verifier.
/// </summary>
public sealed class HorizonVaultDeletionInspector(
    HttpClient client,
    HorizonVaultTransactionInspector transactions,
    HorizonVaultNetworkInspector networks,
    BrowserStellarLedgerRecords connected,
    IStellarLedgerRecords ledger)
{
    private const int MaxResponseBytes = 262144;
    private const int MaxOperations = 200;

    public sealed record TombstoneSource(string AccountId, long OperationId);

    public sealed record Finding(
        bool NetworkMatched,
        bool TransactionSucceeded,
        bool AllOperationsAreTargetManifestDeletes,
        bool SourcesBelongToAuthenticatedHdChain,
        int ManifestDeletes,
        bool RecordCurrentlyAbsent,
        string Explanation,
        bool StagedSourcesMatched = false,
        IReadOnlyList<TombstoneSource>? DeletedManifestOperations = null)
    {
        // Even all true fields do NOT authorize queue confirmation:
        // prior-content provenance and all-copy historical coverage are missing.
        public bool IsQueueConfirmationProof => false;
    }

    public async Task<Finding> InspectAsync(
        string endpoint, string passphrase, string rootAccount,
        string transactionHash, string recordId,
        IReadOnlyList<string> expectedManifestSources,
        CancellationToken ct = default)
    {
        static Finding Reject(string message) =>
            new(false, false, false, false, 0, false, message);

        if (expectedManifestSources is not { Count: > 0 and <= 63 } ||
            expectedManifestSources.Any(x => !StellarAccountAddress.IsValid(x)) ||
            expectedManifestSources.Distinct(StringComparer.Ordinal).Count() !=
                expectedManifestSources.Count ||
            !StellarAccountAddress.IsValid(rootAccount) ||
            recordId is not { Length: 16 } || !recordId.All(Uri.IsHexDigit) ||
            transactionHash is not { Length: 64 } || !transactionHash.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(passphrase) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps || baseUri.UserInfo.Length != 0 ||
            baseUri.Query.Length != 0 || baseUri.Fragment.Length != 0 ||
            baseUri.AbsolutePath != "/" || !connected.IsConnected || !ledger.IsConnected)
            return Reject("Invalid deletion evidence scope or disconnected signing session.");

        try
        {
            ct.ThrowIfCancellationRequested();
            var network = await networks.InspectAsync(endpoint, passphrase, ct);
            if (!network.Matches)
                return Reject("Horizon network passphrase could not be authenticated.");
            var account = await ledger.InspectAccountInfoAsync();
            if (account.RootAccountId != rootAccount)
                return new(true, false, false, false, 0, false,
                    "Connected signer root does not match the queued deletion account.");
            var receipt = await transactions.InspectAsync(endpoint, transactionHash, ct);
            if (receipt is null)
                return new(true, false, false, false, 0, false,
                    "Recorded transaction is not a successful Horizon receipt.");

            var expected = new Uri(baseUri,
                "transactions/" + transactionHash.ToLowerInvariant() +
                "/operations?limit=200");
            using var request = new HttpRequestMessage(HttpMethod.Get, expected);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.OK ||
                response.RequestMessage?.RequestUri is not Uri effective ||
                effective.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(effective.Authority, expected.Authority, StringComparison.OrdinalIgnoreCase) ||
                effective.AbsolutePath != expected.AbsolutePath ||
                effective.Query != expected.Query || effective.Fragment.Length != 0 ||
                response.Content.Headers.ContentLength is > MaxResponseBytes)
                return new(true, true, false, false, 0, false,
                    "Operation history is unavailable or crossed the expected Horizon boundary.");
            if (response.Content.Headers.ContentType?.MediaType is string type &&
                !string.Equals(type, "application/json", StringComparison.OrdinalIgnoreCase) &&
                !type.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
                return new(true, true, false, false, 0, false,
                    "Historical operation response was not JSON.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var bounded = new MemoryStream();
            var buffer = new byte[8192];
            try
            {
                while (true)
                {
                    var length = await stream.ReadAsync(buffer.AsMemory(), ct);
                    if (length == 0) break;
                    if (bounded.Length + length > MaxResponseBytes)
                        return new(true, true, false, false, 0, false,
                            "Historical operation response exceeded the byte limit.");
                    bounded.Write(buffer, 0, length);
                }
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer); }

            bounded.Position = 0;
            using var document = await JsonDocument.ParseAsync(bounded, cancellationToken: ct);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("_embedded", out var embedded) ||
                embedded.ValueKind != JsonValueKind.Object ||
                !embedded.TryGetProperty("records", out var records) ||
                records.ValueKind != JsonValueKind.Array ||
                records.GetArrayLength() is 0 or >= MaxOperations)
                return new(true, true, false, false, 0, false,
                    "Operation page was empty, incomplete or truncated.");

            var expectedName = "svh:" + recordId.ToLowerInvariant();
            var sources = new HashSet<string>(StringComparer.Ordinal);
            var operationIds = new HashSet<long>();
            var tombstones = new List<TombstoneSource>();
            foreach (var operation in records.EnumerateArray())
            {
                if (operation.ValueKind != JsonValueKind.Object ||
                    !operation.TryGetProperty("type", out var kind) ||
                    kind.GetString() != "manage_data" ||
                    !operation.TryGetProperty("transaction_hash", out var hash) ||
                    !string.Equals(hash.GetString(), transactionHash, StringComparison.OrdinalIgnoreCase) ||
                    !operation.TryGetProperty("transaction_successful", out var succeeded) ||
                    succeeded.ValueKind != JsonValueKind.True ||
                    !operation.TryGetProperty("name", out var name) ||
                    !string.Equals(name.GetString(), expectedName, StringComparison.OrdinalIgnoreCase) ||
                    !operation.TryGetProperty("source_account", out var source) ||
                    !StellarAccountAddress.IsValid(source.GetString()) ||
                    operation.TryGetProperty("value", out var value) &&
                    value.ValueKind is not JsonValueKind.Null ||
                    !operation.TryGetProperty("id", out var id) ||
                    !long.TryParse(id.GetString(), out var operationId) ||
                    operationId <= 0 || !operationIds.Add(operationId) ||
                    !sources.Add(source.GetString()!))
                    return new(true, true, false, false, sources.Count, false,
                        "Recorded transaction contains an unexpected, duplicated or non-tombstone operation.");
                tombstones.Add(new TombstoneSource(source.GetString()!, operationId));
            }

            var expectedSources = expectedManifestSources.ToHashSet(StringComparer.Ordinal);
            if (!sources.SetEquals(expectedSources))
                return new(true, true, true, false, sources.Count, false,
                    "Deletion operation sources do not match all manifest-owning accounts recorded when the deletion was staged. Do not confirm or retry.");

            foreach (var source in sources)
            {
                // An HD child source is derived from the live authenticated
                // signing session; no manually provided child index is trusted.
                var index = await connected.ResolveAuthenticatedHdChildAsync(
                    rootAccount, source, endpoint, passphrase, ct);
                if (index is null)
                    return new(true, true, true, false, sources.Count, false,
                        "Manifest deletion source was not in the authenticated HD child chain.");
            }

            // A current absence check is useful to an investigator, but never
            // sufficient for proof of the historical predecessor or all copies.
            var latest = await ledger.ReadAsync();
            var absent = !latest.Any(x =>
                string.Equals(x.Id, recordId, StringComparison.OrdinalIgnoreCase));
            ct.ThrowIfCancellationRequested();
            if (!ledger.IsConnected ||
                (await ledger.InspectAccountInfoAsync()).RootAccountId != rootAccount)
                return Reject("Connected root changed during historical inspection.");
            return new(true, true, true, true, sources.Count, absent,
                absent
                    ? "Authenticated manifest deletion operations found, and the record is currently absent. Prior-record provenance and all-copy history remain unproven; do not confirm or retry."
                    : "Authenticated manifest deletion operations found, but the record is still present. Investigate other copies or subsequent writes; do not confirm or retry.",
                StagedSourcesMatched: true,
                DeletedManifestOperations: tombstones);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return Reject("Historical deletion inspection was unavailable or malformed.");
        }
    }
}
