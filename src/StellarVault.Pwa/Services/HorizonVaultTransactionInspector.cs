using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using StellarVault.Core.Services;

namespace StellarVault.Pwa.Services;

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
        if (transactionHash.Length != 64 || !transactionHash.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid Stellar transaction hash.", nameof(transactionHash));
        var baseUrl = horizonUrl.TrimEnd('/') + "/";
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(new Uri(baseUrl), "transactions/" + transactionHash.ToLowerInvariant()));
        // Prevent an HTTP redirect from silently changing the configured evidence host.
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.RequestMessage?.RequestUri?.Host != origin.Host)
            return null;
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var receipt = await response.Content.ReadFromJsonAsync<Receipt>(cancellationToken: ct);
        if (receipt is null || !receipt.Successful || receipt.Ledger <= 0 ||
            !string.Equals(receipt.Hash, transactionHash, StringComparison.OrdinalIgnoreCase))
            return null;
        return receipt;
    }
}
