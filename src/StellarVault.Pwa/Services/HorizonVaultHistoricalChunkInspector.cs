using VaultEngine.Stellar.Horizon;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Reconstructs the latest successful, generation-specific chunk writes BEFORE
/// the manifest operation using bounded account operation history. This is a
/// read-only provenance diagnostic, not a network/record confirmation provider.
/// </summary>
public sealed class HorizonVaultHistoricalChunkInspector(
    HttpClient client,
    HorizonVaultTransactionInspector transactions)
{
    private const int PageSize = 200;
    private const int MaxPages = 16;
    private const int MaxPageBytes = 512 * 1024;
    private const int MaxChunks = 64;
    private const int ChunkBytes = 64;

    public sealed record HistoricalFinding(
        bool ChunksPublishedBeforeManifest,
        int VerifiedChunkCount,
        int InspectedPages,
        string Explanation);

    public Task<HistoricalFinding> InspectAsync(
        string horizonUrl,
        HorizonVaultManifestInspector.ManifestEvidence manifest,
        long manifestLedger,
        CancellationToken ct = default) =>
        InspectCoreAsync(horizonUrl, manifest, manifestLedger, null, ct);

    /// <summary>
    /// Only after successful receipt and SHA-256 checks, passes a temporary
    /// ciphertext copy to a trusted in-process comparison callback. The buffer
    /// is zeroed before return. Never persist or log it.
    /// </summary>
    public Task<HistoricalFinding> InspectAndCompareAsync(
        string horizonUrl, HorizonVaultManifestInspector.ManifestEvidence manifest,
        long manifestLedger, Func<byte[], CancellationToken, Task<bool>> compare,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(compare);
        return InspectCoreAsync(horizonUrl, manifest, manifestLedger, compare, ct);
    }

    private async Task<HistoricalFinding> InspectCoreAsync(
        string horizonUrl, HorizonVaultManifestInspector.ManifestEvidence manifest,
        long manifestLedger, Func<byte[], CancellationToken, Task<bool>>? compare,
        CancellationToken ct)
    {
        static HistoricalFinding Failed(int count, int pages, string reason) =>
            new(false, count, pages, reason);

        if (manifest is null || manifestLedger <= 0 || manifest.ManifestOperationId <= 0 ||
            manifest.RecordId is not { Length: 16 } || !manifest.RecordId.All(Uri.IsHexDigit) ||
            manifest.Generation is not { Length: 8 } || !manifest.Generation.All(Uri.IsHexDigit) ||
            manifest.CiphertextSha256 is not { Length: 64 } || !manifest.CiphertextSha256.All(Uri.IsHexDigit) ||
            manifest.TransactionHash is not { Length: 64 } || !manifest.TransactionHash.All(Uri.IsHexDigit) ||
            manifest.OperationSourceAccount is not { Length: 56 } ||
            manifest.OperationSourceAccount[0] != 'G' ||
            !manifest.OperationSourceAccount.All(c => c is >= 'A' and <= 'Z' or >= '2' and <= '7') ||
            manifest.ChunkCount is <= 0 or > MaxChunks ||
            manifest.EncryptedLength <= 0 ||
            (long)(manifest.ChunkCount - 1) * ChunkBytes >= manifest.EncryptedLength ||
            (long)manifest.ChunkCount * ChunkBytes < manifest.EncryptedLength ||
            !Uri.TryCreate(horizonUrl, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || origin.UserInfo.Length != 0 ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0)
            return Failed(0, 0, "Invalid or excessive historical inspection inputs.");

        var chunks = new byte[manifest.ChunkCount][];
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = 0;
        var cursor = manifest.ManifestOperationId;
        var pages = 0;

        try
        {
            for (; pages < MaxPages && found < chunks.Length; pages++)
            {
                var expected = new Uri(new Uri(horizonUrl.TrimEnd('/') + "/"),
                    "accounts/" + manifest.OperationSourceAccount +
                    "/operations?order=desc&limit=" + PageSize.ToString(CultureInfo.InvariantCulture) + "&cursor=" +
                    cursor.ToString(CultureInfo.InvariantCulture));

                using var request = new HttpRequestMessage(HttpMethod.Get, expected);
                using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return Failed(found, pages + 1, "Historical account operations are unavailable.");
                response.EnsureSuccessStatusCode();

                var actual = response.RequestMessage?.RequestUri;
                if (actual is null || actual.Scheme != Uri.UriSchemeHttps ||
                    !string.Equals(actual.Authority, expected.Authority, StringComparison.OrdinalIgnoreCase) ||
                    actual.AbsolutePath != expected.AbsolutePath ||
                    actual.Query != expected.Query || actual.Fragment.Length != 0 ||
                    response.Content.Headers.ContentLength is > MaxPageBytes)
                    return Failed(found, pages + 1, "Historical response failed URL or size checks.");
                if (response.Content.Headers.ContentType?.MediaType is string mediaType &&
                    !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) &&
                    !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
                    return Failed(found, pages + 1, "Historical operation response is not JSON.");

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var bounded = new MemoryStream();
                var buffer = new byte[8192];
                while (true)
                {
                    var count = await stream.ReadAsync(buffer.AsMemory(), ct);
                    if (count == 0) break;
                    if (bounded.Length + count > MaxPageBytes)
                        return Failed(found, pages + 1, "Historical page exceeded the size limit.");
                    bounded.Write(buffer, 0, count);
                }
                bounded.Position = 0;
                using var json = await JsonDocument.ParseAsync(bounded, cancellationToken: ct);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("_embedded", out var embedded) ||
                    embedded.ValueKind != JsonValueKind.Object ||
                    !embedded.TryGetProperty("records", out var records) ||
                    records.ValueKind != JsonValueKind.Array)
                    return Failed(found, pages + 1, "Missing historical operation records.");

                if (records.GetArrayLength() == 0)
                    return Failed(found, pages + 1, "Historical operations ended before all chunks were found.");

                var previous = cursor;
                foreach (var operation in records.EnumerateArray())
                {
                    if (operation.ValueKind != JsonValueKind.Object ||
                        !operation.TryGetProperty("id", out var id) ||
                        id.ValueKind != JsonValueKind.String ||
                        !long.TryParse(id.GetString(), NumberStyles.None,
                            CultureInfo.InvariantCulture, out var operationId) ||
                        operationId <= 0 || operationId >= previous)
                        return Failed(found, pages + 1, "Historical operation ordering is invalid.");
                    previous = operationId;

                    if (!operation.TryGetProperty("type", out var type) ||
                        type.ValueKind != JsonValueKind.String ||
                        type.GetString() != "manage_data" ||
                        !operation.TryGetProperty("name", out var name) ||
                        name.ValueKind != JsonValueKind.String)
                        continue;

                    var prefix = $"svc:{manifest.RecordId.ToLowerInvariant()}:{manifest.Generation.ToLowerInvariant()}:";
                    var key = name.GetString()!;
                    if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                    var suffix = key[prefix.Length..];
                    if (suffix.Length != 4 ||
                        !int.TryParse(suffix, NumberStyles.None,
                            CultureInfo.InvariantCulture, out var index) ||
                        index < 0 || index >= chunks.Length ||
                        !string.Equals(key, prefix + index.ToString("D4", CultureInfo.InvariantCulture),
                            StringComparison.OrdinalIgnoreCase))
                        return Failed(found, pages + 1, "Invalid generation-specific chunk name.");

                    // The newest write for this chunk before the manifest determines
                    // its historical value. Never skip malformed newest writes.
                    if (chunks[index] is not null) continue;

                    if (!operation.TryGetProperty("transaction_successful", out var success) ||
                        success.ValueKind != JsonValueKind.True ||
                        !operation.TryGetProperty("source_account", out var source) ||
                        source.ValueKind != JsonValueKind.String ||
                        !string.Equals(source.GetString(), manifest.OperationSourceAccount, StringComparison.Ordinal) ||
                        !operation.TryGetProperty("transaction_hash", out var hash) ||
                        hash.ValueKind != JsonValueKind.String ||
                        hash.GetString() is not { Length: 64 } transactionHash ||
                        !transactionHash.All(Uri.IsHexDigit) ||
                        !operation.TryGetProperty("value", out var value) ||
                        value.ValueKind != JsonValueKind.String)
                        return Failed(found, pages + 1, "Historical chunk write is not valid successful evidence.");

                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(value.GetString()!); }
                    catch (FormatException)
                    {
                        return Failed(found, pages + 1, "Historical chunk was not base64.");
                    }
                    var expectedLength = Math.Min(ChunkBytes,
                        manifest.EncryptedLength - (index * ChunkBytes));
                    if (bytes.Length != expectedLength)
                        return Failed(found, pages + 1, "Historical chunk length mismatch.");

                    chunks[index] = bytes;
                    hashes.Add(transactionHash);
                    found++;
                }
                if (previous >= cursor)
                    return Failed(found, pages + 1, "Historical page cursor did not advance.");
                cursor = previous;
            }

            if (found != chunks.Length)
                return Failed(found, pages, "Historical scan limit reached before all chunks were found.");

            // Account-operation success alone is not trusted: individually inspect
            // every distinct chunk transaction and bound it by the manifest ledger.
            foreach (var transactionHash in hashes)
            {
                var receipt = await transactions.InspectAsync(horizonUrl, transactionHash, ct);
                if (receipt is null || receipt.Ledger <= 0 || receipt.Ledger > manifestLedger)
                    return Failed(found, pages, "A chunk transaction lacked a valid earlier receipt.");
            }

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var chunk in chunks) sha.AppendData(chunk);
            var actualDigest = sha.GetHashAndReset();
            var expectedDigest = Convert.FromHexString(manifest.CiphertextSha256);
            var equal = CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest);
            CryptographicOperations.ZeroMemory(actualDigest);
            CryptographicOperations.ZeroMemory(expectedDigest);
            if (!equal)
                return Failed(found, pages, "Historical ciphertext does not match the manifest commitment.");

            if (compare is not null)
            {
                // A fixed-size copy prevents a caller from reading the per-chunk
                // storage after the callback finishes. The copy is cleared even
                // when HD decryption throws or is cancelled.
                var ciphertext = new byte[manifest.EncryptedLength];
                try
                {
                    var offset = 0;
                    foreach (var chunk in chunks)
                    {
                        Buffer.BlockCopy(chunk, 0, ciphertext, offset, chunk.Length);
                        offset += chunk.Length;
                    }

                    if (!await compare(ciphertext, ct))
                        return Failed(found, pages, "Historical ciphertext decryption or approved record comparison failed.");
                }
                finally { CryptographicOperations.ZeroMemory(ciphertext); }
                return new(true, found, pages,
                    "Exact historical ciphertext decrypts to the expected record; network and root lineage still require trusted binding.");
            }

            return new(true, found, pages,
                "Historical encrypted writes match the manifest; record decryption and HD root lineage remain unverified.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Failed(found, pages, "Historical account operation evidence could not be validated."); }
        finally
        {
            foreach (var chunk in chunks)
                if (chunk is not null) CryptographicOperations.ZeroMemory(chunk);
        }
    }
}
