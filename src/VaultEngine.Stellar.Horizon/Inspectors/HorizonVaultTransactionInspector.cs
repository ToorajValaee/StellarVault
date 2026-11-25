using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VaultEngine.Stellar.Horizon;

/// <summary>
/// Read-only Horizon transaction inspection. This is not sufficient by itself
/// to establish that a specific encrypted record was published by a transaction.
/// Do not register as a confirmation provider without record-to-transaction linkage.
/// </summary>
public sealed class HorizonVaultTransactionInspector(HttpClient client)
{
    public sealed record Receipt(
        [property: JsonPropertyName("hash")] string? Hash,
        [property: JsonPropertyName("successful")] bool Successful,
        [property: JsonPropertyName("source_account")] string? SourceAccount,
        [property: JsonPropertyName("ledger")] long Ledger);

    public async Task<Receipt?> InspectAsync(string horizonUrl, string transactionHash,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(horizonUrl, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || origin.UserInfo.Length != 0 ||
            !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            throw new ArgumentException("A valid HTTPS Horizon endpoint is required.", nameof(horizonUrl));
        if (string.IsNullOrWhiteSpace(transactionHash) || transactionHash.Length != 64 || !transactionHash.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid Stellar transaction hash.", nameof(transactionHash));
        var baseUrl = horizonUrl.TrimEnd('/') + "/";
        var expectedUri = new Uri(new Uri(baseUrl), "transactions/" + transactionHash.ToLowerInvariant());
        using var request = new HttpRequestMessage(HttpMethod.Get, expectedUri);
        // Prevent an HTTP redirect from silently changing the configured evidence host.
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.RequestMessage?.RequestUri is not Uri effective ||
            effective.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(effective.Authority, expectedUri.Authority, StringComparison.OrdinalIgnoreCase) ||
            effective.AbsolutePath != expectedUri.AbsolutePath ||
            effective.Query.Length != 0 || effective.Fragment.Length != 0)
            return null;
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 262144) return null;
        if (response.Content.Headers.ContentType?.MediaType is string mediaType &&
            !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) return null;
        const int maxReceiptBytes = 262144;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) break;
            if (bounded.Length + count > maxReceiptBytes) return null;
            bounded.Write(buffer, 0, count);
        }
        bounded.Position = 0;
        Receipt? receipt;
        try { receipt = await JsonSerializer.DeserializeAsync<Receipt>(bounded, cancellationToken: ct); }
        catch (JsonException) { return null; }
        if (receipt is null || !receipt.Successful || receipt.Ledger <= 0 ||
            string.IsNullOrWhiteSpace(receipt.SourceAccount) ||
            !string.Equals(receipt.Hash, transactionHash, StringComparison.OrdinalIgnoreCase))
            return null;
        return receipt;
    }
}
