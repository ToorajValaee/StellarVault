using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Checks a configured HTTPS Horizon root's reported network passphrase.
/// Trust in the endpoint/TLS certificate is assumed; this is not an
/// independent consensus proof. Do not use it alone to confirm publications.
/// </summary>
public sealed class HorizonVaultNetworkInspector(HttpClient client)
{
    private const int MaxResponseBytes = 64 * 1024;

    public sealed record Finding(bool Matches, string? NetworkIdHex, string Explanation);

    public async Task<Finding> InspectAsync(
        string horizonUrl, string expectedPassphrase, CancellationToken ct = default)
    {
        static Finding Reject(string reason) => new(false, null, reason);

        if (string.IsNullOrWhiteSpace(expectedPassphrase) ||
            expectedPassphrase.Length > 512 ||
            !Uri.TryCreate(horizonUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            return Reject("Network verification inputs are invalid.");

        try
        {
            var expected = new Uri(horizonUrl.TrimEnd('/') + "/");
            using var request = new HttpRequestMessage(HttpMethod.Get, expected);
            using var response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var actual = response.RequestMessage?.RequestUri;
            if (actual is null || actual.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(actual.Authority, expected.Authority, StringComparison.OrdinalIgnoreCase) ||
                actual.AbsolutePath != expected.AbsolutePath ||
                actual.Query.Length != 0 || actual.Fragment.Length != 0 ||
                response.Content.Headers.ContentLength is > MaxResponseBytes)
                return Reject("Network response origin or size was not trusted.");

            if (response.Content.Headers.ContentType?.MediaType is string mediaType &&
                !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) &&
                !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
                return Reject("Network response was not JSON.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var bounded = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var length = await stream.ReadAsync(buffer.AsMemory(), ct);
                if (length == 0) break;
                if (bounded.Length + length > MaxResponseBytes)
                    return Reject("Network response exceeded the size limit.");
                bounded.Write(buffer, 0, length);
            }
            bounded.Position = 0;
            using var json = await JsonDocument.ParseAsync(bounded, cancellationToken: ct);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("network_passphrase", out var reported) ||
                reported.ValueKind != JsonValueKind.String ||
                reported.GetString() is not { } actualPassphrase ||
                actualPassphrase.Length > 512 ||
                !string.Equals(expectedPassphrase, actualPassphrase, StringComparison.Ordinal))
                return Reject("Horizon network passphrase does not match the selected network.");

            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(expectedPassphrase));
            try
            {
                return new(true, Convert.ToHexStringLower(digest),
                    "Horizon reports the selected network passphrase; network trust still depends on this endpoint.");
            }
            finally { CryptographicOperations.ZeroMemory(digest); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Reject("Unable to verify Horizon network identity."); }
    }
}
