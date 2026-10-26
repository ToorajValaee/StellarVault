using System.Security.Cryptography;
using StellarVault.Core.Abstractions;

namespace StellarVault.Core.Services;

/// <summary>Development-stage end-to-end encrypted record operations.
/// The private staging stream must never be published before successful completion.</summary>
public sealed class VaultEncryptedRecordService(IVaultChunkStore objects, IVaultRecordIndex pointers)
{
    private readonly VaultRecordCommitter _committer = new(objects, pointers);

    public async Task<VaultRecordPointer> WriteAsync(Guid recordId, VaultRecordPointer? expected,
        Stream plaintext, ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> manifestContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (!plaintext.CanRead) throw new ArgumentException("Readable input required.", nameof(plaintext));
        if (recordId == Guid.Empty) throw new ArgumentException("Record ID required.", nameof(recordId));
        if (key.Length != 32) throw new ArgumentException("Expected AES-256 key.", nameof(key));
        long generation = expected is null ? 0 : checked(expected.Generation + 1);
        var descriptors = new List<VaultChunkDescriptor>();
        byte[] buffer = new byte[VaultChunkedRecord.ChunkSize];
        long total = 0;
        try
        {
            while (true)
            {
                int count = 0;
                while (count < buffer.Length)
                {
                    int n = await plaintext.ReadAsync(buffer.AsMemory(count), cancellationToken);
                    if (n == 0) break;
                    count += n;
                }
                if (count == 0) break;
                if (descriptors.Count >= VaultChunkIndex.MaxChunks)
                    throw new InvalidDataException("Record exceeds chunk count limit.");
                byte[] aad = BuildChunkContext(recordId, generation, descriptors.Count);
                try
                {
                    byte[] ciphertext = VaultV2Envelope.Seal(key.Span, buffer.AsSpan(0, count), aad);
                    try
                    {
                        using var encryptedStream = new MemoryStream(ciphertext, writable: false);
                        string id = await objects.PutAsync(encryptedStream, cancellationToken);
                        string hash = Convert.ToHexString(SHA256.HashData(ciphertext));
                        if (!string.Equals(id, hash, StringComparison.OrdinalIgnoreCase))
                            throw new CryptographicException("Storage returned an invalid content identifier.");
                        descriptors.Add(new VaultChunkDescriptor(descriptors.Count, id, count, hash));
                        total += count;
                    }
                    finally { CryptographicOperations.ZeroMemory(ciphertext); }
                }
                finally { CryptographicOperations.ZeroMemory(aad); }
            }
            var manifest = new VaultRecordIndex(recordId, generation, total, descriptors);
            byte[] sealedManifest = VaultChunkIndex.Seal(manifest, key.Span, manifestContext.Span);
            try { return await _committer.CommitVerifiedAsync(recordId, expected, sealedManifest, key, manifestContext, cancellationToken); }
            finally { CryptographicOperations.ZeroMemory(sealedManifest); }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    public async Task ReadAsync(Guid recordId, Stream privateStaging, ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> manifestContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(privateStaging);
        if (!privateStaging.CanSeek || !privateStaging.CanWrite || privateStaging.Position != privateStaging.Length)
            throw new ArgumentException("Append-only seekable private staging required.", nameof(privateStaging));
        long start = privateStaging.Position;
        try
        {
            VaultRecordIndex manifest = await _committer.ReadVerifiedAsync(recordId, key, manifestContext, cancellationToken);
            foreach (var descriptor in manifest.Chunks)
            {
                await using var stream = await objects.OpenReadAsync(descriptor.ObjectKey, cancellationToken);
                using var encrypted = new MemoryStream();
                byte[] block = new byte[64 * 1024];
                try
                {
                    while (true)
                    {
                        int n = await stream.ReadAsync(block, cancellationToken);
                        if (n == 0) break;
                        if (encrypted.Length + n > VaultV2Envelope.MaximumLength)
                            throw new CryptographicException("Encrypted chunk exceeds size limit.");
                        await encrypted.WriteAsync(block.AsMemory(0, n), cancellationToken);
                    }
                }
                finally { CryptographicOperations.ZeroMemory(block); }
                byte[] ciphertext = encrypted.ToArray();
                try
                {
                    if (!string.Equals(descriptor.ObjectKey, descriptor.Sha256Hex, StringComparison.OrdinalIgnoreCase) ||
                        !VaultChunkIndex.VerifyCiphertext(descriptor, ciphertext))
                        throw new CryptographicException("Encrypted chunk digest mismatch.");
                    byte[] aad = BuildChunkContext(recordId, manifest.Generation, descriptor.Index);
                    try
                    {
                        byte[] plain = VaultV2Envelope.Open(key.Span, ciphertext, aad);
                        try
                        {
                            if (plain.Length != descriptor.PlaintextLength)
                                throw new CryptographicException("Encrypted chunk length mismatch.");
                            await privateStaging.WriteAsync(plain, cancellationToken);
                        }
                        finally { CryptographicOperations.ZeroMemory(plain); }
                    }
                    finally { CryptographicOperations.ZeroMemory(aad); }
                }
                finally { CryptographicOperations.ZeroMemory(ciphertext); }
            }
            if (privateStaging.Position - start != manifest.TotalLength)
                throw new CryptographicException("Record length mismatch.");
        }
        catch
        {
            privateStaging.SetLength(start);
            privateStaging.Position = start;
            throw;
        }
    }

    private static byte[] BuildChunkContext(Guid recordId, long generation, int index)
    {
        byte[] aad = new byte[28];
        recordId.TryWriteBytes(aad.AsSpan(0, 16));
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(16, 8), generation);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(24, 4), index);
        return aad;
    }
}
