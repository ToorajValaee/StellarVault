using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Bounded inspection of an independent S-key v1 record create transaction.
/// Rebuilds its historical skc ciphertext from the SAME transaction as its skh
/// manifest. This proves only historical operation/commitment consistency,
/// NOT child signer authorization, root lineage, or decryption to a local draft.
/// Never use this result alone to clear a durable mutation quarantine.
/// </summary>
public sealed class HorizonVaultLinkedRecordOperationInspector(HttpClient client)
{
    private const int MaxBytes = 262144;
    private const int MaxOperations = 90;

    public sealed record Finding(bool ManifestAndChunksMatch, string? StorageAccount,
        int ChunkCount, string Explanation);

    public async Task<Finding> InspectCreateAsync(
        string horizonUrl, string transactionHash, string recordId,
        CancellationToken ct = default)
    {
        static Finding No(string reason) => new(false, null, 0, reason);
        if (recordId is not { Length: 16 } ||
            !recordId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            transactionHash is not { Length: 64 } || !transactionHash.All(Uri.IsHexDigit) ||
            !Uri.TryCreate(horizonUrl, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || origin.UserInfo.Length != 0 ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            origin.AbsolutePath != "/")
            return No("Invalid independent S-key record inspection scope.");

        var expected = new Uri(origin, "transactions/" +
            transactionHash.ToLowerInvariant() + "/operations?limit=200");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, expected);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return No("The historical transaction operations were not found.");
            response.EnsureSuccessStatusCode();
            var effective = response.RequestMessage?.RequestUri;
            if (effective is null || effective.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(effective.Authority, expected.Authority,
                    StringComparison.OrdinalIgnoreCase) ||
                effective.AbsolutePath != expected.AbsolutePath ||
                effective.Query != expected.Query || effective.Fragment.Length != 0 ||
                response.Content.Headers.ContentLength is > MaxBytes)
                return No("Historical operation response origin or size changed.");
            if (response.Content.Headers.ContentType?.MediaType is string media &&
                media != "application/json" && !media.EndsWith("+json",
                    StringComparison.OrdinalIgnoreCase))
                return No("Historical operation response was not JSON.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var block = new byte[8192];
            while (true)
            {
                var n = await stream.ReadAsync(block.AsMemory(), ct);
                if (n == 0) break;
                if (buffer.Length + n > MaxBytes)
                    return No("Historical operation response exceeded the size bound.");
                buffer.Write(block, 0, n);
            }
            buffer.Position = 0;
            using var json = await JsonDocument.ParseAsync(buffer,
                cancellationToken: ct);
            var root = json.RootElement;
            if (!root.TryGetProperty("_embedded", out var embedded) ||
                !embedded.TryGetProperty("records", out var records) ||
                records.ValueKind != JsonValueKind.Array ||
                records.GetArrayLength() is < 2 or > MaxOperations)
                return No("Incomplete or excessive atomic transaction history.");

            var headerName = "skh:" + recordId;
            var prefix = "skc:" + recordId + ":";
            string? account = null;
            string? generation = null;
            byte[]? digest = null;
            int length = 0, count = 0;
            var chunkWrites = new Dictionary<int, byte[]>();
            var manifestSeen = false;
            try
            {
                foreach (var operation in records.EnumerateArray())
                {
                    if (operation.ValueKind != JsonValueKind.Object ||
                        !operation.TryGetProperty("transaction_hash", out var txValue) ||
                        txValue.ValueKind != JsonValueKind.String ||
                        !string.Equals(txValue.GetString(), transactionHash,
                            StringComparison.OrdinalIgnoreCase) ||
                        !operation.TryGetProperty("transaction_successful", out var success) ||
                        success.ValueKind != JsonValueKind.True ||
                        !operation.TryGetProperty("type", out var type) ||
                        type.ValueKind != JsonValueKind.String)
                        return No("Operation was not part of the expected successful transaction.");

                    if (type.GetString() != "manage_data") continue;
                    if (!operation.TryGetProperty("name", out var nameValue) ||
                        nameValue.ValueKind != JsonValueKind.String)
                        return No("Historical ManageData name is invalid.");
                    var name = nameValue.GetString()!;
                    if (name != headerName && !name.StartsWith(prefix,
                            StringComparison.Ordinal))
                        continue;
                    if (!operation.TryGetProperty("source_account", out var owner) ||
                        owner.ValueKind != JsonValueKind.String ||
                        owner.GetString() is not { Length: 56 } source ||
                        source[0] != 'G' ||
                        !source.All(c => c is >= 'A' and <= 'Z' or >= '2' and <= '7') ||
                        !operation.TryGetProperty("value", out var value) ||
                        value.ValueKind != JsonValueKind.String)
                        return No("Record write source or value is invalid.");
                    if (account is not null && account != source)
                        return No("Record chunks and manifest came from different accounts.");
                    account = source;
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(value.GetString()!); }
                    catch (FormatException) { return No("Historical ManageData was not base64."); }
                    if (name == headerName)
                    {
                        if (manifestSeen || bytes.Length != 47 || bytes[0] != 1)
                            return No("Missing, duplicate or invalid skh v1 manifest.");
                        manifestSeen = true;
                        generation = Encoding.ASCII.GetString(bytes, 1, 8);
                        if (!generation.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                            return No("Invalid skh record generation.");
                        count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(9, 2));
                        length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(11, 4));
                        if (count is < 1 or > 89 || length < 28 ||
                            (long)(count - 1) * 64 >= length || (long)count * 64 < length ||
                            chunkWrites.Count != count)
                            return No("skh manifest count or encrypted size is inconsistent.");
                        digest = bytes.AsSpan(15, 32).ToArray();
                        foreach (var (index, data) in chunkWrites)
                            if (index >= count || data.Length != Math.Min(64, length - index * 64))
                                return No("Historical chunk length or index is invalid.");
                    }
                    else
                    {
                        if (manifestSeen)
                            return No("Record chunk appeared after the manifest.");
                        // skc:<id>:<8-hex generation>:<base36 index>
                        var parts = name[prefix.Length..].Split(':');
                        if (parts.Length != 2 || parts[0].Length != 8 ||
                            !parts[0].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                            parts[1].Length is < 1 or > 2 ||
                            !parts[1].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'z'))
                            return No("Invalid historical skc generation or index.");
                        var idx = 0;
                        foreach (var c in parts[1])
                            idx = idx * 36 + (c is >= '0' and <= '9' ? c - '0' : c - 'a' + 10);
                        if (idx > 88 || Convert.ToString(idx, 36) != parts[1])
                            return No("Noncanonical historical skc index.");
                        if (generation is null) generation = parts[0];
                        if (generation != parts[0] || chunkWrites.ContainsKey(idx))
                            return No("Mixed generations or duplicate record chunk.");
                        chunkWrites.Add(idx, bytes);
                    }
                }
                if (!manifestSeen || account is null || digest is null ||
                    chunkWrites.Count != count)
                    return No("Transaction lacks a complete S-key v1 manifest and chunks.");
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                for (var i = 0; i < count; i++)
                {
                    if (!chunkWrites.TryGetValue(i, out var chunk))
                        return No("Historical record chunk index is missing.");
                    sha.AppendData(chunk);
                }
                var actual = sha.GetHashAndReset();
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(actual, digest))
                        return No("Historical ciphertext SHA-256 differs from the manifest.");
                }
                finally { CryptographicOperations.ZeroMemory(actual); }
                return new(true, account, count,
                    "The transaction wrote matching skh/skc ciphertext history. Signer policy, vault lineage and decrypted record fields are not independently proven.");
            }
            finally
            {
                if (digest is not null) CryptographicOperations.ZeroMemory(digest);
                foreach (var bytes in chunkWrites.Values)
                    CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return No("Historical operation inspection was unavailable or invalid.");
        }
    }
}
