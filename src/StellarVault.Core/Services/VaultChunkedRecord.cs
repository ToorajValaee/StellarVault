using System.Buffers.Binary;
using System.Security.Cryptography;

namespace StellarVault.Core.Services;

/// <summary>Authenticated chunked record for database-like vault storage.
/// Format: SVCR, version 1, 16-byte record ID, 8-byte generation, 4-byte count,
/// 8-byte total length, repeated (4-byte ciphertext length, encrypted v2 chunk),
/// and a final authenticated v2 manifest of SHA-256 ciphertext hashes.
/// Readers MUST NOT expose plaintext until the final manifest is verified.
/// </summary>
public static class VaultChunkedRecord
{
    public const int ChunkSize = 256 * 1024;
    public const int MaxChunks = 4096; // 1 GiB per record; configurable policy in future versions
    private const int HeaderSize = 4 + 1 + 16 + 8 + 4 + 8;
    private static ReadOnlySpan<byte> Magic => "SVCR"u8;

    public static async Task EncryptAsync(Stream input, Stream output, ReadOnlyMemory<byte> key,
        Guid recordId, long generation, CancellationToken cancellationToken = default)
    {
        if (key.Length != 32) throw new ArgumentException("Expected AES-256 key.", nameof(key));
        if (!input.CanRead || !output.CanWrite || !output.CanSeek) throw new ArgumentException("Writable seekable output and readable input required.");
        if (generation < 0) throw new ArgumentOutOfRangeException(nameof(generation));
        long start = output.Position;
        byte[] header = new byte[HeaderSize];
        Magic.CopyTo(header);
        header[4] = 1;
        recordId.TryWriteBytes(header.AsSpan(5, 16));
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(21, 8), generation);
        await output.WriteAsync(header, cancellationToken);
        byte[] buffer = new byte[ChunkSize];
        using var hashes = new MemoryStream(MaxChunks * 32);
        int count = 0;
        long total = 0;
        try
        {
            while (true)
            {
                int n = 0;
                while (n < buffer.Length)
                {
                    int read = await input.ReadAsync(buffer.AsMemory(n), cancellationToken);
                    if (read == 0) break;
                    n += read;
                }
                if (n == 0) break;
                if (count == MaxChunks) throw new InvalidDataException("Record exceeds maximum chunk count.");
                byte[] context = Context(header.AsSpan(0, 29), count);
                byte[] encrypted = VaultV2Envelope.Seal(key.Span, buffer.AsSpan(0, n), context);
                byte[] hash = SHA256.HashData(encrypted);
                await hashes.WriteAsync(hash, cancellationToken);
                byte[] length = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(length, encrypted.Length);
                await output.WriteAsync(length, cancellationToken);
                await output.WriteAsync(encrypted, cancellationToken);
                count++;
                total += n;
                CryptographicOperations.ZeroMemory(context);
            }
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(29, 4), count);
            BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(33, 8), total);
            // Bind the final header, all chunk hashes and their order into the manifest.
            byte[] manifestContext = header;
            byte[] manifest = VaultV2Envelope.Seal(key.Span, hashes.ToArray(), manifestContext);
            await output.WriteAsync(manifest, cancellationToken);
            long end = output.Position;
            output.Position = start;
            await output.WriteAsync(header, cancellationToken);
            output.Position = end;
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    /// <summary>Writes to caller-owned PRIVATE staging storage; do not publish it before success.
    /// On failure staging is truncated back to its original position.</summary>
    public static async Task DecryptAsync(Stream input, Stream staging, ReadOnlyMemory<byte> key,
        Guid expectedRecordId, long expectedGeneration, CancellationToken cancellationToken = default)
    {
        if (key.Length != 32) throw new ArgumentException("Expected AES-256 key.", nameof(key));
        if (!staging.CanWrite || !staging.CanSeek) throw new ArgumentException("Seekable staging stream required.", nameof(staging));
        long initialPosition = staging.Position;
        try
        {
        byte[] header = new byte[HeaderSize];
        await input.ReadExactlyAsync(header, cancellationToken);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic) || header[4] != 1 ||
            new Guid(header.AsSpan(5, 16)) != expectedRecordId ||
            BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(21, 8)) != expectedGeneration)
            throw new CryptographicException("Record identity or version mismatch.");
        int count = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(29, 4));
        long total = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(33, 8));
        if (count < 0 || count > MaxChunks || total < 0 || total > (long)count * ChunkSize ||
            (count == 0 && total != 0)) throw new CryptographicException("Invalid record bounds.");
        using var hashes = new MemoryStream(count * 32);
        for (int i = 0; i < count; i++)
        {
            byte[] lengthBytes = new byte[4];
            await input.ReadExactlyAsync(lengthBytes, cancellationToken);
            int length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            if (length < VaultV2Envelope.MinimumLength || length > VaultV2Envelope.MinimumLength + ChunkSize)
                throw new CryptographicException("Invalid chunk length.");
            byte[] encrypted = new byte[length];
            await input.ReadExactlyAsync(encrypted, cancellationToken);
            await hashes.WriteAsync(SHA256.HashData(encrypted), cancellationToken);
            byte[] context = Context(header.AsSpan(0, 29), i);
            try
            {
                byte[] plain = VaultV2Envelope.Open(key.Span, encrypted, context);
                try { await staging.WriteAsync(plain, cancellationToken); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            finally { CryptographicOperations.ZeroMemory(context); }
        }
        int manifestLength = VaultV2Envelope.MinimumLength + count * 32;
        byte[] manifest = new byte[manifestLength];
        await input.ReadExactlyAsync(manifest, cancellationToken);
        if (await input.ReadAsync(new byte[1], cancellationToken) != 0)
            throw new CryptographicException("Trailing record data.");
        byte[] verified = VaultV2Envelope.Open(key.Span, manifest, header);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(verified, hashes.ToArray()) || staging.Position - initialPosition != total)
                throw new CryptographicException("Record manifest authentication failed.");
            return;
        }
        finally { CryptographicOperations.ZeroMemory(verified); }
        }
        catch
        {
            staging.SetLength(initialPosition);
            staging.Position = initialPosition;
            throw;
        }
    }

    private static byte[] Context(ReadOnlySpan<byte> recordIdentity, int index)
    {
        byte[] context = new byte[recordIdentity.Length + 4];
        recordIdentity.CopyTo(context);
        BinaryPrimitives.WriteInt32BigEndian(context.AsSpan(recordIdentity.Length), index);
        return context;
    }
}
