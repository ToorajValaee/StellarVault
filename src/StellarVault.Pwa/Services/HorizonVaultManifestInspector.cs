using System.Net;
using System.Text.Json;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Reads ManageData operations from one Stellar transaction and checks whether
/// its final record-manifest operation targets the expected vault record.
/// This is a provenance component, NOT a complete vault ledger verifier.
/// </summary>
public sealed class HorizonVaultManifestInspector(HttpClient client)
{
    private const int MaxResponseBytes = 256 * 1024;

    public async Task<bool> ContainsManifestWriteAsync(
        string horizonUrl, string transactionHash, string recordId,
        CancellationToken ct = default)
    {
        if (recordId is not { Length: 16 } || !recordId.All(Uri.IsHexDigit) ||
            transactionHash is not { Length: 64 } || !transactionHash.All(Uri.IsHexDigit))
            return false;
        if (!Uri.TryCreate(horizonUrl, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || origin.UserInfo.Length != 0 ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0)
            return false;

        // The manifest is submitted after its chunks. Checking the manifest
        // operation ties the returned final transaction hash to the record ID,
        // not necessarily to the full record contents or all previous chunks.
        var expected = new Uri(new Uri(horizonUrl.TrimEnd('/') + "/"),
            "transactions/" + transactionHash.ToLowerInvariant() + "/operations?limit=200");
        using var request = new HttpRequestMessage(HttpMethod.Get, expected);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        var effective = response.RequestMessage?.RequestUri;
        if (effective is null || effective.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(effective.Authority, expected.Authority, StringComparison.OrdinalIgnoreCase) ||
            effective.AbsolutePath != expected.AbsolutePath || effective.Query != expected.Query)
            return false;
        if (response.Content.Headers.ContentLength is > MaxResponseBytes) return false;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) break;
            if (data.Length + count > MaxResponseBytes) return false;
            data.Write(buffer, 0, count);
        }
        data.Position = 0;
        JsonDocument document;
        try { document = await JsonDocument.ParseAsync(data, cancellationToken: ct); }
        catch (JsonException) { return false; }
        using (document)
        {
        if (!document.RootElement.TryGetProperty("_embedded", out var embedded) ||
            !embedded.TryGetProperty("records", out var records) ||
            records.ValueKind != JsonValueKind.Array)
            return false;
        var expectedName = "svh:" + recordId.ToLowerInvariant();
        foreach (var operation in records.EnumerateArray())
        {
            if (operation.TryGetProperty("type", out var type) &&
                type.GetString() == "manage_data" &&
                operation.TryGetProperty("name", out var name) &&
                string.Equals(name.GetString(), expectedName, StringComparison.OrdinalIgnoreCase) &&
                operation.TryGetProperty("value", out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()) &&
                operation.TryGetProperty("transaction_hash", out var hash) &&
                string.Equals(hash.GetString(), transactionHash, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
        }
    }
}
