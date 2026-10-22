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

        return sessions.ExecuteAsync(async (database, key, ct) =>
        {
            // Do not overwrite a visible text record by uploading a file with
            // the same catalog name. Existing binary files require an explicit
            // version for replacement; the initial UI uses create-only writes.
            var entries = await database.Catalog.ListAsync(key, ct);
            var existing = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            if (existing is not null && (existing.ContentType is null ||
                existing.ContentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("This name belongs to a text record.");
            return await database.PutIndexedAsync(
                name, expectedVersion, plaintext, key, contentType, ct);
        }, cancellationToken);
    }

    /// <summary>
    /// Returns a caller-owned plaintext buffer only after authentication succeeds
    /// and its version is stable. Never stream unauthenticated plaintext into a
    /// download destination.
    /// </summary>
    public Task<BinaryRecord?> OpenAsync(string name, CancellationToken cancellationToken = default) =>
        sessions.ExecuteAsync<BinaryRecord?>(async (database, key, ct) =>
        {
            var entries = await database.Catalog.ListAsync(key, ct);
            var metadata = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            if (metadata is null) return null;
            if (metadata.ContentType is null ||
                metadata.ContentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This record contains text, not a binary file.");
            var before = await database.GetVersionAsync(name, key, ct);
            if (before is null) return null;

            using var staging = new MemoryStream();
            try
            {
                await database.GetAsync(name, staging, key, ct);
                var after = await database.GetVersionAsync(name, key, ct);
                if (after != before)
                    throw new VaultWriteConflictException(database.GetRecordId(name, key.Span));
                // A concurrent tombstone or content-type change must not allow
                // a stale read to return a file hidden from the active catalog.
                var currentEntries = await database.Catalog.ListAsync(key, ct);
                var current = currentEntries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
                if (current is null || current.ContentType is null ||
                    current.ContentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
                    throw new VaultWriteConflictException(database.GetRecordId(name, key.Span));
                // Revalidate after checking catalog visibility to narrow the stale-read window.
                if (await database.GetVersionAsync(name, key, ct) != before)
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

    /// <summary>Retrieve the visible file revision without decrypting its contents.
    /// Used for version-checked catalog removal, not for reading file plaintext.</summary>
    public Task<VaultRecordPointer?> GetFileVersionAsync(string name,
        CancellationToken cancellationToken = default) =>
        sessions.ExecuteAsync<VaultRecordPointer?>(async (database, key, ct) =>
        {
            var entries = await database.Catalog.ListAsync(key, ct);
            var entry = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            if (entry is null) return null;
            if (entry.ContentType is null ||
                entry.ContentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This record is not an encrypted file.");
            var version = await database.GetVersionAsync(name, key, ct);
            if (version is null) return null;
            // Avoid returning a pointer after a concurrent catalog tombstone.
            var current = await database.Catalog.ListAsync(key, ct);
            var visible = current.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            if (visible is null || visible.ContentType is null ||
                visible.ContentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
                throw new VaultWriteConflictException(database.GetRecordId(name, key.Span));
            // The pointer may have changed while the catalog was being reloaded.
            // Do not hand the UI a stale revision for its later delete request.
            var confirmedVersion = await database.GetVersionAsync(name, key, ct);
            if (confirmedVersion != version)
                throw new VaultWriteConflictException(database.GetRecordId(name, key.Span));
            return version;
        }, cancellationToken);

    /// <summary>Logically remove an encrypted file only at its expected revision.
    /// Encrypted historical objects may remain in immutable storage.</summary>
    public Task DeleteAsync(string name, VaultRecordPointer expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedVersion);
        return sessions.ExecuteAsync(async (database, key, ct) =>
        {
            var entries = await database.Catalog.ListAsync(key, ct);
            var entry = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            if (entry is null || entry.ContentType is null ||
                entry.ContentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected file is no longer in the catalog.");
            var current = await database.GetVersionAsync(name, key, ct);
            if (current != expectedVersion)
                throw new VaultWriteConflictException(database.GetRecordId(name, key.Span));
            await database.Catalog.TombstoneAsync(name, key, ct);
            return true;
        }, cancellationToken);
    }

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
