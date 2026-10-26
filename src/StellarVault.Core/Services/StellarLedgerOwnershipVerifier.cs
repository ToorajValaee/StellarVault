using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;

namespace StellarVault.Core.Services;

/// <summary>Platform cryptographic primitive for verification of an exact Ed25519
/// challenge message against the public G-address. Never accept client-provided
/// verification results in place of an actual signature check.</summary>
public interface IStellarEd25519SignatureVerifier
{
    bool Verify(string signerAccountId, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature);
}

/// <summary>Reads current account signer policy from a pinned Horizon endpoint.
/// Does not take signers or thresholds from the wallet response.</summary>
public sealed class StellarLedgerOwnershipVerifier(
    HttpClient http,
    IStellarEd25519SignatureVerifier signatures,
    Uri horizonBaseUrl,
    string networkPassphrase) : IStellarOwnershipProofVerifier
{
    public async Task<bool> VerifyAsync(StellarOwnershipChallenge challenge, string signerAccountId,
        ReadOnlyMemory<byte> signature, CancellationToken cancellationToken = default)
    {
        if (!StellarOwnershipChallengeCodec.IsWellFormed(challenge) ||
            !StellarAccountAddress.IsValid(challenge.AccountId) ||
            !StellarAccountAddress.IsValid(signerAccountId) ||
            challenge.NetworkPassphrase != networkPassphrase ||
            string.IsNullOrWhiteSpace(networkPassphrase) ||
            challenge.Payload.Length < 40 || challenge.Payload.Length > 2048 ||
            challenge.ExpiresAt <= DateTimeOffset.UtcNow ||
            challenge.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2) || signature.Length != 64)
            return false;
        if (horizonBaseUrl.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(horizonBaseUrl.UserInfo) ||
            !string.IsNullOrEmpty(horizonBaseUrl.Query) ||
            !string.IsNullOrEmpty(horizonBaseUrl.Fragment))
            throw new InvalidOperationException("Use a pinned HTTPS Horizon base URL.");

        // Verify the signature BEFORE trusting the signer's claim of account control.
        if (!signatures.Verify(signerAccountId, challenge.Payload, signature.Span)) return false;
        // Horizon's root descriptor identifies the network. A signer list from a
        // wrong-network Horizon instance must never authorize a vault.
        var root = await FetchBoundedJsonAsync<NetworkResponse>(horizonBaseUrl, cancellationToken);
        if (root?.NetworkPassphrase != networkPassphrase) return false;

        var endpoint = new Uri(horizonBaseUrl.AbsoluteUri.TrimEnd('/') + "/accounts/" + challenge.AccountId);
        var account = await FetchBoundedJsonAsync<AccountResponse>(endpoint, cancellationToken);
        if (account is null || account.Id != challenge.AccountId || account.Thresholds is null || account.Signers is null)
            return false;
        // Vault control is conservatively limited to an Ed25519 signer whose own
        // weight meets the account's high threshold. Multi-signature aggregation is
        // deliberately unsupported until a separate validated proof protocol exists.
        int threshold = account.Thresholds.High;
        if (threshold <= 0 || threshold > 255) return false;
        return account.Signers.Any(s => s.Type == "ed25519_public_key" &&
            s.Key == signerAccountId && s.Weight >= threshold && s.Weight > 0 && s.Weight <= 255);
    }

    private async Task<T?> FetchBoundedJsonAsync<T>(Uri endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.RequestMessage?.RequestUri != endpoint)
            throw new InvalidOperationException("Horizon response was redirected.");
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        response.EnsureSuccessStatusCode();
        const int maximumBytes = 131072;
        if (response.Content.Headers.ContentLength is > maximumBytes)
            throw new InvalidDataException("Horizon response exceeds limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bounded = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;
            if (bounded.Length + count > maximumBytes)
                throw new InvalidDataException("Horizon response exceeds limit.");
            await bounded.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        bounded.Position = 0;
        return await System.Text.Json.JsonSerializer.DeserializeAsync<T>(bounded, cancellationToken: cancellationToken);
    }

    private sealed class NetworkResponse
    {
        [JsonPropertyName("network_passphrase")] public string? NetworkPassphrase { get; set; }
    }

    private sealed class AccountResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("thresholds")] public ThresholdResponse? Thresholds { get; set; }
        [JsonPropertyName("signers")] public List<SignerResponse>? Signers { get; set; }
    }

    private sealed class ThresholdResponse
    {
        [JsonPropertyName("high_threshold")] public int High { get; set; }
    }

    private sealed class SignerResponse
    {
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("weight")] public int Weight { get; set; }
    }
}
