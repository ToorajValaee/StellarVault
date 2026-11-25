using System.Globalization;
using System.Net;
using System.Text.Json;
using VaultEngine.Stellar.Services;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Finds the nearest prior manifest mutation on a deleted HD child. It returns
/// a candidate transaction ONLY when that newest mutation is a successful
/// manifest WRITE. A preceding tombstone, malformed write, truncated page or
/// exhausted history fails closed. The caller MUST independently authenticate
/// the returned manifest, registry, encrypted chunks and approved plaintext.
/// </summary>
public sealed class HorizonVaultDeletionPredecessorInspector(HttpClient client)
{
    private const int PageSize = 200;
    private const int MaxPages = 16;
    private const int MaxPageBytes = 512 * 1024;

    public sealed record Candidate(
        string AccountId, long ManifestOperationId, string TransactionHash);

    public async Task<Candidate?> FindLatestManifestWriteAsync(
        string endpoint, string sourceAccount, string recordId,
        long beforeOperationId, CancellationToken ct = default)
    {
        if (!StellarAccountAddress.IsValid(sourceAccount) ||
            recordId is not { Length: 16 } || !recordId.All(Uri.IsHexDigit) ||
            beforeOperationId <= 0 ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || origin.UserInfo.Length != 0 ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            origin.AbsolutePath != "/")
            return null;

        var cursor = beforeOperationId;
        var name = "svh:" + recordId.ToLowerInvariant();
        try
        {
            for (var page = 0; page < MaxPages; page++)
            {
                var expected = new Uri(origin,
                    "accounts/" + sourceAccount + "/operations?order=desc&limit=" +
                    PageSize.ToString(CultureInfo.InvariantCulture) + "&cursor=" +
                    cursor.ToString(CultureInfo.InvariantCulture));
                using var request = new HttpRequestMessage(HttpMethod.Get, expected);
                using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode != HttpStatusCode.OK ||
                    response.RequestMessage?.RequestUri is not Uri actual ||
                    actual.Scheme != Uri.UriSchemeHttps ||
                    !string.Equals(actual.Authority, expected.Authority, StringComparison.OrdinalIgnoreCase) ||
                    actual.AbsolutePath != expected.AbsolutePath ||
                    actual.Query != expected.Query || actual.Fragment.Length != 0 ||
                    response.Content.Headers.ContentLength is > MaxPageBytes)
                    return null;
                if (response.Content.Headers.ContentType?.MediaType is string mediaType &&
                    !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) &&
                    !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
                    return null;

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var buffer = new MemoryStream();
                var scratch = new byte[8192];
                try
                {
                    while (true)
                    {
                        var count = await stream.ReadAsync(scratch.AsMemory(), ct);
                        if (count == 0) break;
                        if (buffer.Length + count > MaxPageBytes) return null;
                        buffer.Write(scratch, 0, count);
                    }
                }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(scratch); }
                buffer.Position = 0;
                using var json = await JsonDocument.ParseAsync(buffer, cancellationToken: ct);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("_embedded", out var embedded) ||
                    embedded.ValueKind != JsonValueKind.Object ||
                    !embedded.TryGetProperty("records", out var records) ||
                    records.ValueKind != JsonValueKind.Array ||
                    records.GetArrayLength() is 0 or > PageSize)
                    return null;

                var previous = cursor;
                foreach (var op in records.EnumerateArray())
                {
                    if (op.ValueKind != JsonValueKind.Object ||
                        !op.TryGetProperty("id", out var id) ||
                        id.ValueKind != JsonValueKind.String ||
                        !long.TryParse(id.GetString(), NumberStyles.None,
                            CultureInfo.InvariantCulture, out var opId) ||
                        opId <= 0 || opId >= previous)
                        return null;
                    previous = opId;
                    if (!op.TryGetProperty("type", out var kind) ||
                        kind.ValueKind != JsonValueKind.String ||
                        kind.GetString() != "manage_data" ||
                        !op.TryGetProperty("name", out var nameElement) ||
                        nameElement.ValueKind != JsonValueKind.String ||
                        !string.Equals(nameElement.GetString(), name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // The nearest manifest mutation is authoritative. A delete
                    // before the queued delete means we cannot prove the original
                    // staged content from an older write.
                    if (!op.TryGetProperty("transaction_successful", out var succeeded) ||
                        succeeded.ValueKind != JsonValueKind.True ||
                        !op.TryGetProperty("source_account", out var source) ||
                        source.ValueKind != JsonValueKind.String ||
                        source.GetString() != sourceAccount ||
                        !op.TryGetProperty("transaction_hash", out var hash) ||
                        hash.ValueKind != JsonValueKind.String ||
                        hash.GetString() is not { Length: 64 } txHash ||
                        !txHash.All(Uri.IsHexDigit) ||
                        !op.TryGetProperty("value", out var value) ||
                        value.ValueKind != JsonValueKind.String)
                        return null;
                    return new(sourceAccount, opId, txHash.ToLowerInvariant());
                }
                if (previous >= cursor) return null;
                cursor = previous;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
        return null;
    }
}
