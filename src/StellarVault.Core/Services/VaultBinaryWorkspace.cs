using System.Security.Cryptography;
using StellarVault.Core.Abstractions;

namespace StellarVault.Core.Services;

/// <summary>
/// Shared encrypted binary record operations for native and browser hosts.
/// All plaintext is supplied by the caller and is encrypted before reaching the
/// chunk store. Reads stage the entire decrypted record privately before exposing
/// any bytes to a caller; callers must dispose the returned BinaryRecord.
/// </summary>
public sealed class VaultBinaryWorkspace(VaultSessionManager sessions)
{
    public Task<VaultRecordPointer> SaveAsync(
        string name,
        Stream plaintext,
        string contentType,
        VaultRecordPointer? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (!plaintext.CanRead)
            throw new ArgumentException("A readable plaintext stream is required.", nameof(plaintext));
        if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > 128 ||
            contentType.Contains('\0'))
            throw new ArgumentException("Invalid content type.", nameof(contentType));

        return sessions.ExecuteAsync(
            (database, key, ct) => database.PutIndexedAsync(
                name, expectedVersion, plaintext, key, contentType, ct),
            cancellationToken);
    }

    /// <summary>
    /// Returns a caller-owned plaintext buffer only after authentication succeeds
    /// and its version is stable. Never stream unauthenticated plaintext into a
    /// download destination.
    /// </summary>
    public Task<BinaryRecord?> OpenAsync(string name, CancellationToken cancellationToken = default) =>
        sessions.ExecuteAsync<BinaryRecord?>(async (database, key, ct) =>
        {
            var before = await database.GetVersionAsync(name, key, ct);
            if (before is null) return null;

            using var staging = new MemoryStream();
            try
            {
                await database.GetAsync(name, staging, key, ct);
                var after = await database.GetVersionAsync(name, key, ct);
                if (after != before)
                    throw new VaultWriteConflictException(database.GetRecordId(name, key.Span));

                // The returned copy belongs to BinaryRecord, which clears it on disposal.
                return new BinaryRecord(name, before, staging.ToArray());
            }
            finally
            {
                // MemoryStream.Dispose does not clear its backing plaintext buffer.
                if (staging.TryGetBuffer(out ArraySegment<byte> segment) && segment.Array is not null)
                    CryptographicOperations.ZeroMemory(segment.Array);
            }
        }, cancellationToken);

    public sealed class BinaryRecord : IDisposable
    {
        private byte[]? _plaintext;

        internal BinaryRecord(string name, VaultRecordPointer version, byte[] plaintext)
        {
            Name = name;
            Version = version;
            _plaintext = plaintext;
        }

        public string Name { get; }
        public VaultRecordPointer Version { get; }

        public ReadOnlyMemory<byte> Content =>
            _plaintext is { } bytes ? bytes :
            throw new ObjectDisposedException(nameof(BinaryRecord));

        public void Dispose()
        {
            if (_plaintext is not null)
                CryptographicOperations.ZeroMemory(_plaintext);
            _plaintext = null;
        }
    }
}
