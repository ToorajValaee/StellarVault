using System.Security.Cryptography;
using System.Text.Json;

namespace VaultEngine.Stellar.Services;

/// <summary>Authenticated logical record index referencing separately stored ciphertext chunks.
/// Storage providers treat object keys as opaque identifiers, never as authorization.</summary>
public sealed record VaultChunkDescriptor(int Index, string ObjectKey, int PlaintextLength, string Sha256Hex);
public sealed record VaultRecordIndex(Guid RecordId, long Generation, long TotalLength,
    IReadOnlyList<VaultChunkDescriptor> Chunks);

public interface IVaultBlobStore
{
    Task PutAsync(string objectKey, ReadOnlyMemory<byte> ciphertext, CancellationToken cancellationToken = default);
    Task<byte[]> GetAsync(string objectKey, CancellationToken cancellationToken = default);
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default);
}

public static class VaultChunkIndex
{
    public const int MaxChunks = VaultChunkedRecord.MaxChunks;
    // JSON descriptors have variable lengths; reserve enough headroom for all supported chunks.
    public const int MaxIndexBytes = VaultV2Envelope.MaximumPlaintextLength;

    /// <summary>Encrypt a record index; the index itself is a v2 authenticated envelope.</summary>
    public static byte[] Seal(VaultRecordIndex index, ReadOnlySpan<byte> key, ReadOnlySpan<byte> context)
    {
        Validate(index);
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(index);
        if (serialized.Length > MaxIndexBytes)
        {
            CryptographicOperations.ZeroMemory(serialized);
            throw new CryptographicException("Record index exceeds authenticated envelope capacity.");
        }
        try { return VaultV2Envelope.Seal(key, serialized, context); }
        finally { CryptographicOperations.ZeroMemory(serialized); }
    }

    public static VaultRecordIndex Open(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> key, ReadOnlySpan<byte> context)
    {
        byte[] serialized = VaultV2Envelope.Open(key, envelope, context);
        try
        {
            var index = JsonSerializer.Deserialize<VaultRecordIndex>(serialized)
                ?? throw new CryptographicException("Missing record index.");
            Validate(index);
            return index;
        }
        finally { CryptographicOperations.ZeroMemory(serialized); }
    }

    public static void Validate(VaultRecordIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (index.RecordId == Guid.Empty || index.Generation < 0 || index.TotalLength < 0 ||
            index.Chunks is null || index.Chunks.Count > MaxChunks ||
            index.TotalLength > (long)index.Chunks.Count * VaultChunkedRecord.ChunkSize)
            throw new CryptographicException("Invalid record index bounds.");
        long sum = 0;
        var objectKeys = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < index.Chunks.Count; i++)
        {
            var chunk = index.Chunks[i];
            if (chunk is null || chunk.Index != i || chunk.PlaintextLength <= 0 ||
                chunk.PlaintextLength > VaultChunkedRecord.ChunkSize ||
                string.IsNullOrWhiteSpace(chunk.ObjectKey) || chunk.ObjectKey.Length > 128 ||
                !objectKeys.Add(chunk.ObjectKey) || chunk.Sha256Hex is null || chunk.Sha256Hex.Length != 64 ||
                !IsHex(chunk.Sha256Hex))
                throw new CryptographicException("Invalid chunk descriptor.");
            sum += chunk.PlaintextLength;
        }
        if (sum != index.TotalLength) throw new CryptographicException("Record length mismatch.");
    }

    public static bool VerifyCiphertext(VaultChunkDescriptor descriptor, ReadOnlySpan<byte> ciphertext)
    {
        Span<byte> expected = stackalloc byte[32];
        if (descriptor.Sha256Hex is null || descriptor.Sha256Hex.Length != 64 || !IsHex(descriptor.Sha256Hex))
            return false;
        Convert.FromHexString(descriptor.Sha256Hex).CopyTo(expected);
        Span<byte> actual = stackalloc byte[32];
        SHA256.HashData(ciphertext, actual);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static bool IsHex(string value)
    {
        foreach (char c in value)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}
