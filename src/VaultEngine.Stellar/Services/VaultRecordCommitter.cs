using System.Security.Cryptography;
using VaultEngine.Stellar.Abstractions;

namespace VaultEngine.Stellar.Services;

/// <summary>Publishes a previously authenticated manifest only after its encrypted object exists.
/// An unsuccessful compare-and-swap leaves an unreachable object for later garbage collection.
/// This does not provide durability, rollback resistance, or authorization by itself.</summary>
public sealed class VaultRecordCommitter(IVaultChunkStore objects, IVaultRecordIndex index)
{
    /// <summary>Verify the authenticated record index and expected generation before publication.</summary>
    public async Task<VaultRecordPointer> CommitVerifiedAsync(Guid recordId, VaultRecordPointer? expected,
        ReadOnlyMemory<byte> encryptedManifest, ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> context, CancellationToken cancellationToken = default)
    {
        long generation = expected is null ? 0 : checked(expected.Generation + 1);
        var manifest = VaultChunkIndex.Open(encryptedManifest.Span, key.Span, context.Span);
        if (manifest.RecordId != recordId || manifest.Generation != generation)
            throw new CryptographicException("Manifest identity or generation mismatch.");
        await VerifyReferencedChunksAsync(manifest, cancellationToken);
        return await CommitAsync(recordId, expected, encryptedManifest, cancellationToken);
    }

    /// <summary>Check every referenced ciphertext object before publishing the manifest.</summary>
    private async Task VerifyReferencedChunksAsync(VaultRecordIndex manifest, CancellationToken cancellationToken)
    {
        foreach (var descriptor in manifest.Chunks)
        {
            if (!string.Equals(descriptor.ObjectKey, descriptor.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("Chunk object identifier must match its ciphertext digest.");
            await using var source = await objects.OpenReadAsync(descriptor.ObjectKey, cancellationToken);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            long length = 0;
            try
            {
                while (true)
                {
                    int read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    length += read;
                    if (length > VaultV2Envelope.MaximumLength)
                        throw new CryptographicException("Referenced ciphertext exceeds maximum size.");
                    hash.AppendData(buffer, 0, read);
                }
                if (length != (long)descriptor.PlaintextLength + VaultV2Envelope.MinimumLength)
                    throw new CryptographicException("Referenced ciphertext length mismatch.");
                byte[] digest = hash.GetHashAndReset();
                try
                {
                    byte[] expected = Convert.FromHexString(descriptor.Sha256Hex);
                    try
                    {
                        if (!CryptographicOperations.FixedTimeEquals(digest, expected))
                            throw new CryptographicException("Referenced ciphertext hash mismatch.");
                    }
                    finally { CryptographicOperations.ZeroMemory(expected); }
                }
                finally { CryptographicOperations.ZeroMemory(digest); }
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        }
    }

    /// <summary>Read and authenticate a record index against the committed pointer.</summary>
    public async Task<VaultRecordIndex> ReadVerifiedAsync(Guid recordId, ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> context, CancellationToken cancellationToken = default)
    {
        var pointer = await index.ReadAsync(recordId, cancellationToken)
            ?? throw new FileNotFoundException("Record does not exist.");
        byte[] encrypted = await ReadManifestByPointerAsync(pointer, cancellationToken);
        try
        {
            var manifest = VaultChunkIndex.Open(encrypted, key.Span, context.Span);
            if (manifest.RecordId != recordId || manifest.Generation != pointer.Generation)
                throw new CryptographicException("Committed manifest identity or generation mismatch.");
            // A concurrent writer may publish a newer pointer while the manifest
            // is being loaded. Never return it as the current committed revision.
            var currentPointer = await index.ReadAsync(recordId, cancellationToken);
            if (currentPointer != pointer)
                throw new VaultWriteConflictException(recordId);
            return manifest;
        }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
    }

    private async Task<VaultRecordPointer> CommitAsync(Guid recordId, VaultRecordPointer? expected,
        ReadOnlyMemory<byte> authenticatedManifest, CancellationToken cancellationToken = default)
    {
        if (recordId == Guid.Empty) throw new ArgumentException("Record ID required.", nameof(recordId));
        if (authenticatedManifest.Length < VaultV2Envelope.MinimumLength ||
            authenticatedManifest.Length > VaultV2Envelope.MaximumLength)
            throw new ArgumentOutOfRangeException(nameof(authenticatedManifest));
        // This coordinator cannot authenticate a manifest without a key. The caller must
        // authenticate and bind record ID/generation before invoking CommitAsync.
        long generation = expected is null ? 0 : checked(expected.Generation + 1);
        // PutAsync needs a stream-backed copy. Erase that temporary ciphertext
        // buffer on every path, including cancellation and compare-exchange failure.
        byte[] temporaryManifest = authenticatedManifest.ToArray();
        try
        {
            using var stream = new MemoryStream(temporaryManifest, writable: false);
            string contentId = await objects.PutAsync(stream, cancellationToken);
            var next = new VaultRecordPointer(contentId, generation);
            if (!await index.CompareExchangeAsync(recordId, expected, next, cancellationToken))
                throw new VaultWriteConflictException(recordId);
            return next;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(temporaryManifest);
        }
    }

    public async Task<byte[]> ReadManifestAsync(Guid recordId, CancellationToken cancellationToken = default)
    {
        var pointer = await index.ReadAsync(recordId, cancellationToken)
            ?? throw new FileNotFoundException("Record does not exist.");
        return await ReadManifestByPointerAsync(pointer, cancellationToken);
    }

    private async Task<byte[]> ReadManifestByPointerAsync(VaultRecordPointer pointer, CancellationToken cancellationToken)
    {
        await using var stream = await objects.OpenReadAsync(pointer.ManifestContentId, cancellationToken);
        using var output = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int n = await stream.ReadAsync(buffer, cancellationToken);
                if (n == 0) break;
                if (output.Length + n > VaultV2Envelope.MaximumLength)
                    throw new InvalidDataException("Manifest exceeds size limit.");
                await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
            }
            byte[] ciphertext = output.ToArray();
            if (ciphertext.Length < VaultV2Envelope.MinimumLength ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(ciphertext)), pointer.ManifestContentId,
                    StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("Manifest content identifier mismatch.");
            return ciphertext;
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}

public sealed class VaultWriteConflictException(Guid recordId)
    : InvalidOperationException($"Record {recordId} changed before the update could commit.");
