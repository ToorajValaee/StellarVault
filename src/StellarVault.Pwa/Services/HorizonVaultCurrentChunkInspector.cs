using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Inspects the CURRENT Horizon account data snapshot against a transaction's
/// HD-v2 manifest commitment. This does not establish when the chunks were
/// written, whether they preceded the manifest, or which network was used.
/// No plaintext or ciphertext is returned to callers.
/// </summary>
public sealed class HorizonVaultCurrentChunkInspector(HttpClient client)
{
    private const int ChunkBytes = 64;
    private const int MaxAccountResponseBytes = 2 * 1024 * 1024;
    private const int MaxEncryptedBytes = 1024 * 1024;

    public sealed record SnapshotFinding(
        bool CurrentManifestMatches,
        bool CurrentChunksMatch,
        int VerifiedChunkCount,
        string Explanation);

    public async Task<SnapshotFinding> InspectAsync(
        string horizonUrl,
        HorizonVaultManifestInspector.ManifestEvidence manifest,
        CancellationToken ct = default)
    {
        static SnapshotFinding Reject(string reason) => new(false, false, 0, reason);

        if (manifest is null ||
            manifest.RecordId is not { Length: 16 } || !manifest.RecordId.All(Uri.IsHexDigit) ||
            manifest.Generation is not { Length: 8 } || !manifest.Generation.All(Uri.IsHexDigit) ||
            manifest.CiphertextSha256 is not { Length: 64 } || !manifest.CiphertextSha256.All(Uri.IsHexDigit) ||
            manifest.OperationSourceAccount is not { Length: 56 } ||
            manifest.OperationSourceAccount[0] != 'G' ||
            !manifest.OperationSourceAccount.All(c => c is >= 'A' and <= 'Z' or >= '2' and <= '7') ||
            manifest.ChunkCount <= 0 || manifest.EncryptedLength <= 0 ||
            manifest.EncryptedLength > MaxEncryptedBytes ||
            (long)(manifest.ChunkCount - 1) * ChunkBytes >= manifest.EncryptedLength ||
            (long)manifest.ChunkCount * ChunkBytes < manifest.EncryptedLength ||
            !Uri.TryCreate(horizonUrl, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || origin.UserInfo.Length != 0 ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0)
            return Reject("Invalid manifest or endpoint for account-snapshot inspection.");

        try
        {
            var expected = new Uri(new Uri(horizonUrl.TrimEnd('/') + "/"),
                "accounts/" + manifest.OperationSourceAccount);
            using var request = new HttpRequestMessage(HttpMethod.Get, expected);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Reject("HD child account is unavailable.");
            response.EnsureSuccessStatusCode();
            var actual = response.RequestMessage?.RequestUri;
            if (actual is null || actual.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(actual.Authority, expected.Authority, StringComparison.OrdinalIgnoreCase) ||
                actual.AbsolutePath != expected.AbsolutePath ||
                actual.Query.Length != 0 || actual.Fragment.Length != 0 ||
                response.Content.Headers.ContentLength is > MaxAccountResponseBytes)
                return Reject("Account response destination or size was not trusted.");

            if (response.Content.Headers.ContentType?.MediaType is string mediaType &&
                !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) &&
                !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
                return Reject("Account response was not JSON.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var bounded = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(), ct);
                if (count == 0) break;
                if (bounded.Length + count > MaxAccountResponseBytes)
                    return Reject("Account response exceeded the size limit.");
                bounded.Write(buffer, 0, count);
            }
            bounded.Position = 0;
            using var json = await JsonDocument.ParseAsync(bounded, cancellationToken: ct);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("account_id", out var accountId) ||
                accountId.ValueKind != JsonValueKind.String ||
                !string.Equals(accountId.GetString(), manifest.OperationSourceAccount, StringComparison.Ordinal) ||
                !root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
                return Reject("Account identity or data was missing.");

            var headerName = "svh:" + manifest.RecordId.ToLowerInvariant();
            if (!data.TryGetProperty(headerName, out var header) ||
                header.ValueKind != JsonValueKind.String ||
                !TryDecode(header.GetString(), 51, out var headerBytes) ||
                headerBytes[0] != 2 ||
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(headerBytes.AsSpan(1, 2)) != manifest.ChunkCount ||
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(headerBytes.AsSpan(3, 4)) != manifest.EncryptedLength ||
                System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(headerBytes.AsSpan(39, 8)) != manifest.UpdatedUnixSeconds ||
                !string.Equals(Convert.ToHexString(headerBytes.AsSpan(47, 4)), manifest.Generation, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Convert.ToHexString(headerBytes.AsSpan(7, 32)), manifest.CiphertextSha256, StringComparison.OrdinalIgnoreCase))
                return Reject("Current account manifest does not match the inspected transaction.");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var verifiedBytes = 0;
            for (var i = 0; i < manifest.ChunkCount; i++)
            {
                var name = $"svc:{manifest.RecordId.ToLowerInvariant()}:{manifest.Generation.ToLowerInvariant()}:{i:D4}";
                var expectedLength = Math.Min(ChunkBytes, manifest.EncryptedLength - verifiedBytes);
                if (expectedLength <= 0 ||
                    !data.TryGetProperty(name, out var chunk) ||
                    chunk.ValueKind != JsonValueKind.String ||
                    !TryDecode(chunk.GetString(), expectedLength, out var bytes))
                    return new(true, false, i, "Current encrypted chunks are incomplete or malformed.");

                hash.AppendData(bytes);
                verifiedBytes += bytes.Length;
                CryptographicOperations.ZeroMemory(bytes);
            }

            var digest = hash.GetHashAndReset();
            var committed = Convert.FromHexString(manifest.CiphertextSha256);
            var digestMatches = CryptographicOperations.FixedTimeEquals(digest, committed);
            CryptographicOperations.ZeroMemory(digest);
            CryptographicOperations.ZeroMemory(committed);
            return new(true, digestMatches, manifest.ChunkCount,
                digestMatches
                    ? "Current account chunks match the manifest ciphertext commitment; historical provenance, decryption, and network identity remain unverified."
                    : "Current account chunks fail the manifest ciphertext commitment.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Reject("Current account chunk evidence was unavailable or malformed."); }
    }

    private static bool TryDecode(string? encoded, int expectedLength, out byte[] bytes)
    {
        bytes = [];
        if (encoded is null || encoded.Length > 88) return false;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { return false; }
        if (bytes.Length == expectedLength) return true;
        CryptographicOperations.ZeroMemory(bytes);
        bytes = [];
        return false;
    }
}
