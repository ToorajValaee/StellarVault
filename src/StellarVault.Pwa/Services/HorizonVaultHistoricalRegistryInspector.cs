using System.Globalization;
using System.Net;
using System.Text.Json;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Read-only lookup for the latest successful authenticated-registry candidate
/// before a manifest operation. Returned bytes are UNAUTHENTICATED until the
/// unlocked seed-based registry verifier checks their HMAC and network binding.
/// </summary>
public sealed class HorizonVaultHistoricalRegistryInspector(HttpClient client)
{
    private const int MaxPages = 16;
    private const int PageSize = 200;
    private const int MaxPageBytes = 512 * 1024;

    public sealed record HistoricalRegistryCandidate(
        byte[] Envelope, long OperationId, string TransactionHash);

    public async Task<HistoricalRegistryCandidate?> InspectAsync(
        string endpoint, string rootAccount, long beforeOperationId,
        CancellationToken ct = default)
    {
        if (beforeOperationId <= 0 || rootAccount is not { Length: 56 } ||
            rootAccount[0] != 'G' ||
            !rootAccount.All(c => c is >= 'A' and <= 'Z' or >= '2' and <= '7') ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || origin.UserInfo.Length != 0 ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0)
            return null;

        var cursor = beforeOperationId;
        try
        {
            for (var page = 0; page < MaxPages; page++)
            {
                var expected = new Uri(new Uri(endpoint.TrimEnd('/') + "/"),
                    "accounts/" + rootAccount + "/operations?order=desc&limit=" +
                    PageSize.ToString(CultureInfo.InvariantCulture) + "&cursor=" +
                    cursor.ToString(CultureInfo.InvariantCulture));
                using var request = new HttpRequestMessage(HttpMethod.Get, expected);
                using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                response.EnsureSuccessStatusCode();
                var effective = response.RequestMessage?.RequestUri;
                if (effective is null || effective.Scheme != Uri.UriSchemeHttps ||
                    !string.Equals(effective.Authority, expected.Authority, StringComparison.OrdinalIgnoreCase) ||
                    effective.AbsolutePath != expected.AbsolutePath ||
                    effective.Query != expected.Query || effective.Fragment.Length != 0 ||
                    response.Content.Headers.ContentLength is > MaxPageBytes)
                    return null;
                if (response.Content.Headers.ContentType?.MediaType is string mediaType &&
                    !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) &&
                    !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
                    return null;
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var bounded = new MemoryStream();
                var buffer = new byte[8192];
                while (true)
                {
                    var count = await stream.ReadAsync(buffer.AsMemory(), ct);
                    if (count == 0) break;
                    if (bounded.Length + count > MaxPageBytes) return null;
                    bounded.Write(buffer, 0, count);
                }
                bounded.Position = 0;
                using var document = await JsonDocument.ParseAsync(bounded, cancellationToken: ct);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("_embedded", out var embedded) ||
                    embedded.ValueKind != JsonValueKind.Object ||
                    !embedded.TryGetProperty("records", out var records) ||
                    records.ValueKind != JsonValueKind.Array ||
                    records.GetArrayLength() == 0) return null;

                var previous = cursor;
                foreach (var op in records.EnumerateArray())
                {
                    if (op.ValueKind != JsonValueKind.Object ||
                        !op.TryGetProperty("id", out var id) ||
                        id.ValueKind != JsonValueKind.String ||
                        !long.TryParse(id.GetString(), NumberStyles.None,
                            CultureInfo.InvariantCulture, out var operationId) ||
                        operationId <= 0 || operationId >= previous)
                        return null;
                    previous = operationId;

                    if (!op.TryGetProperty("type", out var type) ||
                        type.ValueKind != JsonValueKind.String ||
                        type.GetString() != "manage_data" ||
                        !op.TryGetProperty("name", out var name) ||
                        name.ValueKind != JsonValueKind.String ||
                        name.GetString() != "sv:registry:v2")
                        continue;

                    // The newest registry mutation is authoritative. If it is
                    // deletion, failure, or malformed, never skip to an older one.
                    if (!op.TryGetProperty("transaction_successful", out var success) ||
                        success.ValueKind != JsonValueKind.True ||
                        !op.TryGetProperty("source_account", out var source) ||
                        source.ValueKind != JsonValueKind.String ||
                        source.GetString() != rootAccount ||
                        !op.TryGetProperty("transaction_hash", out var hash) ||
                        hash.ValueKind != JsonValueKind.String ||
                        hash.GetString() is not { Length: 64 } txHash ||
                        !txHash.All(Uri.IsHexDigit) ||
                        !op.TryGetProperty("value", out var value) ||
                        value.ValueKind != JsonValueKind.String)
                        return null;

                    byte[] envelope;
                    try { envelope = Convert.FromBase64String(value.GetString()!); }
                    catch (FormatException) { return null; }
                    if (envelope.Length != 50) return null;
                    return new HistoricalRegistryCandidate(envelope, operationId,
                        txHash.ToLowerInvariant());
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
