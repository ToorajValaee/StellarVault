using System.Buffers.Binary;
using System.Net;
using System.Text.Json;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Read-only HD-v2 manifest evidence from one final transaction. A valid manifest
/// alone cannot prove earlier chunk writes, decrypted contents, or network identity.
/// </summary>
public sealed class HorizonVaultManifestInspector(HttpClient client)
{
    private const int MaxResponseBytes = 256 * 1024;
    private const int ManifestLength = 51;
    private const int ChunkBytes = 64;

    public sealed record ManifestEvidence(
        string RecordId,
        string TransactionHash,
        string OperationSourceAccount,
        int ChunkCount,
        int EncryptedLength,
        string Generation,
        string CiphertextSha256,
        long UpdatedUnixSeconds);

    public async Task<bool> ContainsManifestWriteAsync(
        string horizonUrl, string transactionHash, string recordId,
        CancellationToken ct = default) =>
        await InspectManifestAsync(horizonUrl, transactionHash, recordId, ct) is not null;

    public async Task<ManifestEvidence?> InspectManifestAsync(
        string horizonUrl, string transactionHash, string recordId,
        CancellationToken ct = default)
    {
        if (recordId is not { Length: 16 } || !recordId.All(Uri.IsHexDigit) ||
            transactionHash is not { Length: 64 } || !transactionHash.All(Uri.IsHexDigit) ||
            !Uri.TryCreate(horizonUrl, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || origin.UserInfo.Length != 0 ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0)
            return null;

        var expected = new Uri(new Uri(horizonUrl.TrimEnd('/') + "/"),
            "transactions/" + transactionHash.ToLowerInvariant() + "/operations?limit=200");
        using var request = new HttpRequestMessage(HttpMethod.Get, expected);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var effective = response.RequestMessage?.RequestUri;
        if (effective is null || effective.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(effective.Authority, expected.Authority, StringComparison.OrdinalIgnoreCase) ||
            effective.AbsolutePath != expected.AbsolutePath || effective.Query != expected.Query ||
            effective.Fragment.Length != 0)
            return null;
        if (response.Content.Headers.ContentLength is > MaxResponseBytes) return null;
        if (response.Content.Headers.ContentType?.MediaType is string mediaType &&
            !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) break;
            if (data.Length + count > MaxResponseBytes) return null;
            data.Write(buffer, 0, count);
        }
        data.Position = 0;
        JsonDocument document;
        try { document = await JsonDocument.ParseAsync(data, cancellationToken: ct); }
        catch (JsonException) { return null; }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("_embedded", out var embedded) ||
                embedded.ValueKind != JsonValueKind.Object ||
                !embedded.TryGetProperty("records", out var records) ||
                records.ValueKind != JsonValueKind.Array)
                return null;

            // A single page is not sufficient evidence if results are truncated.
            // Horizon's maximum page size for this endpoint is 200 operations.
            if (records.GetArrayLength() >= 200) return null;

            var expectedName = "svh:" + recordId.ToLowerInvariant();
            ManifestEvidence? found = null;
            foreach (var operation in records.EnumerateArray())
            {
                if (operation.ValueKind != JsonValueKind.Object ||
                    !operation.TryGetProperty("type", out var type) ||
                    type.ValueKind != JsonValueKind.String ||
                    type.GetString() != "manage_data" ||
                    !operation.TryGetProperty("name", out var name) ||
                    name.ValueKind != JsonValueKind.String ||
                    !string.Equals(name.GetString(), expectedName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Multiple manifest writes in a receipt are ambiguous, even if one
                // appears valid. Reject duplicates rather than choosing a match.
                if (found is not null) return null;
                if (!operation.TryGetProperty("transaction_hash", out var hash) ||
                    hash.ValueKind != JsonValueKind.String ||
                    !string.Equals(hash.GetString(), transactionHash, StringComparison.OrdinalIgnoreCase) ||
                    !operation.TryGetProperty("source_account", out var source) ||
                    source.ValueKind != JsonValueKind.String ||
                    !IsClassicAccount(source.GetString()) ||
                    !operation.TryGetProperty("value", out var value) ||
                    value.ValueKind != JsonValueKind.String)
                    return null;

                byte[] bytes;
                try { bytes = Convert.FromBase64String(value.GetString()!); }
                catch (FormatException) { return null; }
                if (bytes.Length != ManifestLength || bytes[0] != 2) return null;

                var chunkCount = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(1, 2));
                var encryptedLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(3, 4));
                var updated = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(39, 8));
                if (chunkCount == 0 || encryptedLength <= 0 || updated <= 0 ||
                    (long)(chunkCount - 1) * ChunkBytes >= encryptedLength ||
                    (long)chunkCount * ChunkBytes < encryptedLength)
                    return null;

                found = new ManifestEvidence(
                    recordId.ToLowerInvariant(),
                    transactionHash.ToLowerInvariant(),
                    source.GetString()!,
                    chunkCount,
                    encryptedLength,
                    Convert.ToHexString(bytes.AsSpan(47, 4)).ToLowerInvariant(),
                    Convert.ToHexString(bytes.AsSpan(7, 32)).ToLowerInvariant(),
                    updated);
            }

            return found;
        }
    }

    private static bool IsClassicAccount(string? value) =>
        value is { Length: 56 } && value[0] == 'G' &&
        value.All(c => c is >= 'A' and <= 'Z' or >= '2' and <= '7');
}
