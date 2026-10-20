using System.Buffers.Binary;
using System.Security.Cryptography;

namespace StellarVault.Core.Services;

/// <summary>Content-addressed encrypted objects. The store sees ciphertext only.
/// Object names are SHA-256 hashes of ciphertext; caller authenticates the manifest.
/// </summary>
public interface IVaultChunkObjectStore
{
    Task PutAsync(string objectId, ReadOnlyMemory<byte> ciphertext, CancellationToken cancellationToken = default);
    Task<byte[]> GetAsync(string objectId, CancellationToken cancellationToken = default);
}

public sealed record VaultChunkReference(int Index, string ObjectId, int PlaintextLength);
public sealed record VaultChunkManifest(Guid RecordId, long Generation, long TotalLength,
    IReadOnlyList<VaultChunkReference> Chunks);

/// <summary>Authenticated manifest and independently addressable encrypted chunks.
/// Callers must store the encrypted manifest separately and commit its pointer atomically.
/// </summary>
public static class VaultChunkObjectStore
{
    public const int ChunkSize = 256 * 1024;
    public const int MaxChunks = 4096;
    private static ReadOnlySpan<byte> ManifestContext => "svault:chunk-manifest:v1"u8;

    public static async Task<byte[]> WriteAsync(Stream plaintext, IVaultChunkObjectStore store,
        ReadOnlyMemory<byte> key, Guid recordId, long generation, CancellationToken cancellationToken = default)
    {
        if (key.Length != 32) throw new ArgumentException("Expected AES-256 key.", nameof(key));
        if (generation < 0) throw new ArgumentOutOfRangeException(nameof(generation));
        var chunks = new List<VaultChunkReference>();
        byte[] buffer = new byte[ChunkSize];
        long total = 0;
        try
        {
            while (true)
            {
                int count = 0;
                while (count < buffer.Length)
                {
                    int read = await plaintext.ReadAsync(buffer.AsMemory(count), cancellationToken);
                    if (read == 0) break;
                    count += read;
                }
                if (count == 0) break;
                if (chunks.Count == MaxChunks) throw new InvalidDataException("Record exceeds chunk count policy.");
                byte[] context = ChunkContext(recordId, generation, chunks.Count);
                try
                {
                    byte[] encrypted = VaultV2Envelope.Seal(key.Span, buffer.AsSpan(0, count), context);
                    string objectId = Convert.ToHexString(SHA256.HashData(encrypted)).ToLowerInvariant();
                    await store.PutAsync(objectId, encrypted, cancellationToken);
                    chunks.Add(new VaultChunkReference(chunks.Count, objectId, count));
                    total += count;
                }
                finally { CryptographicOperations.ZeroMemory(context); }
            }
            byte[] manifest = EncodeManifest(recordId, generation, total, chunks);
            try { return VaultV2Envelope.Seal(key.Span, manifest, ManifestContext); }
            finally { CryptographicOperations.ZeroMemory(manifest); }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    public static async Task ReadAsync(ReadOnlyMemory<byte> encryptedManifest, IVaultChunkObjectStore store,
        Stream privateStaging, ReadOnlyMemory<byte> key, Guid expectedRecordId, long expectedGeneration,
        CancellationToken cancellationToken = default)
    {
        if (key.Length != 32) throw new ArgumentException("Expected AES-256 key.", nameof(key));
        if (!privateStaging.CanSeek || !privateStaging.CanWrite)
            throw new ArgumentException("Seekable private staging required.", nameof(privateStaging));
        long start = privateStaging.Position;
        try
        {
            byte[] serialized = VaultV2Envelope.Open(key.Span, encryptedManifest.Span, ManifestContext);
            VaultChunkManifest manifest;
            try { manifest = DecodeManifest(serialized); }
            finally { CryptographicOperations.ZeroMemory(serialized); }
            if (manifest.RecordId != expectedRecordId || manifest.Generation != expectedGeneration)
                throw new CryptographicException("Unexpected record identity or generation.");
            foreach (var reference in manifest.Chunks)
            {
                byte[] encrypted = await store.GetAsync(reference.ObjectId, cancellationToken);
                if (!Convert.ToHexString(SHA256.HashData(encrypted)).Equals(reference.ObjectId, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException("Chunk object hash mismatch.");
                byte[] context = ChunkContext(manifest.RecordId, manifest.Generation, reference.Index);
                try
                {
                    byte[] plain = VaultV2Envelope.Open(key.Span, encrypted, context);
                    try
                    {
                        if (plain.Length != reference.PlaintextLength)
                            throw new CryptographicException("Chunk length mismatch.");
                        await privateStaging.WriteAsync(plain, cancellationToken);
                    }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
                finally { CryptographicOperations.ZeroMemory(context); }
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

    private static byte[] ChunkContext(Guid id, long generation, int index)
    {
        byte[] result = new byte[28];
        id.TryWriteBytes(result.AsSpan(0, 16));
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(16, 8), generation);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(24, 4), index);
        return result;
    }

    private static byte[] EncodeManifest(Guid id, long generation, long total, List<VaultChunkReference> chunks)
    {
        byte[] result = new byte[36 + chunks.Count * 40];
        "SVCM"u8.CopyTo(result);
        id.TryWriteBytes(result.AsSpan(4, 16));
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(20, 8), generation);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(28, 4), chunks.Count);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(32, 4), checked((int)total));
        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            Convert.FromHexString(chunk.ObjectId).CopyTo(result, 36 + i * 40);
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(36 + i * 40 + 32, 4), chunk.PlaintextLength);
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(36 + i * 40 + 36, 4), chunk.Index);
        }
        return result;
    }

    private static VaultChunkManifest DecodeManifest(ReadOnlySpan<byte> data)
    {
        if (data.Length < 36 || !data[..4].SequenceEqual("SVCM"u8))
            throw new CryptographicException("Invalid chunk manifest.");
        var id = new Guid(data.Slice(4, 16));
        long generation = BinaryPrimitives.ReadInt64BigEndian(data.Slice(20, 8));
        int count = BinaryPrimitives.ReadInt32BigEndian(data.Slice(28, 4));
        int total = BinaryPrimitives.ReadInt32BigEndian(data.Slice(32, 4));
        if (count < 0 || count > MaxChunks || total < 0 || data.Length != 36 + count * 40)
            throw new CryptographicException("Invalid manifest bounds.");
        var chunks = new List<VaultChunkReference>(count);
        long sum = 0;
        for (int i = 0; i < count; i++)
        {
            var entry = data.Slice(36 + i * 40, 40);
            int length = BinaryPrimitives.ReadInt32BigEndian(entry.Slice(32, 4));
            int index = BinaryPrimitives.ReadInt32BigEndian(entry.Slice(36, 4));
            if (index != i || length <= 0 || length > ChunkSize)
                throw new CryptographicException("Invalid chunk reference.");
            chunks.Add(new VaultChunkReference(index, Convert.ToHexString(entry[..32]).ToLowerInvariant(), length));
            sum += length;
        }
        if (sum != total) throw new CryptographicException("Manifest length mismatch.");
        return new VaultChunkManifest(id, generation, total, chunks);
    }
}
