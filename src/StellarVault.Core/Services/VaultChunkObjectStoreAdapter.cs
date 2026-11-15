using System.Security.Cryptography;
using StellarVault.Core.Abstractions;

namespace StellarVault.Core.Services;

/// <summary>
/// Reuses the PWA/MAUI immutable ciphertext object storage for streamed file
/// trees. The existing IVaultChunkStore computes SHA-256 object IDs itself;
/// this adapter verifies that the returned address matches the bytes written.
/// </summary>
public sealed class VaultChunkObjectStoreAdapter(IVaultChunkStore chunks)
    : IVaultChunkObjectStore
{
    // File-tree nodes are small. The envelope limit is independent of total
    // file length; do not allow unbounded or attacker-sized object allocations.
    private const int MaxObjectBytes = 64 * 1024;

    public async Task PutAsync(string objectId, ReadOnlyMemory<byte> ciphertext,
        CancellationToken cancellationToken = default)
    {
        ValidateId(objectId);
        if (ciphertext.IsEmpty || ciphertext.Length > MaxObjectBytes)
            throw new InvalidDataException("Invalid encrypted streaming file node size.");
        var digest = Convert.ToHexString(SHA256.HashData(ciphertext.Span)).ToLowerInvariant();
        if (!string.Equals(objectId, digest, StringComparison.Ordinal))
            throw new CryptographicException("Streaming file object ID is not its ciphertext hash.");
        using var stream = new MemoryStream(ciphertext.ToArray(), writable: false);
        try
        {
            var storedId = await chunks.PutAsync(stream, cancellationToken);
            if (!string.Equals(storedId, objectId, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("File object store returned an unexpected content identifier.");
        }
        finally
        {
            if (stream.TryGetBuffer(out var region) && region.Array is not null)
                CryptographicOperations.ZeroMemory(region.Array);
        }
    }

    public async Task<byte[]> GetAsync(string objectId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(objectId);
        await using var stream = await chunks.OpenReadAsync(objectId, cancellationToken);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        try
        {
            while (true)
            {
                int count = await stream.ReadAsync(block, cancellationToken);
                if (count == 0) break;
                if (buffer.Length + count > MaxObjectBytes)
                    throw new InvalidDataException("Encrypted streaming file node is oversized.");
                await buffer.WriteAsync(block.AsMemory(0, count), cancellationToken);
            }
            byte[] ciphertext = buffer.ToArray();
            if (!string.Equals(
                    Convert.ToHexString(SHA256.HashData(ciphertext)).ToLowerInvariant(),
                    objectId, StringComparison.Ordinal))
            {
                CryptographicOperations.ZeroMemory(ciphertext);
                throw new CryptographicException("Streaming file ciphertext digest mismatch.");
            }
            return ciphertext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(block);
            if (buffer.TryGetBuffer(out var region) && region.Array is not null)
                CryptographicOperations.ZeroMemory(region.Array);
        }
    }

    private static void ValidateId(string id)
    {
        if (id is not { Length: 64 } ||
            !id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidDataException("Invalid ciphertext object identifier.");
    }
}
