using System.Security.Cryptography;
using VaultEngine.Stellar.Abstractions;

namespace VaultEngine.Stellar.Services;

/// <summary>
/// Shared encrypted binary record operations for native and browser hosts.
/// All plaintext is supplied by the caller and is encrypted before reaching the
/// chunk store. Reads stage the entire decrypted record privately before exposing
/// any bytes to a caller; callers must dispose the returned BinaryRecord.
/// </summary>
public sealed class VaultBinaryWorkspace(VaultSessionManager sessions, VaultStorageModeGate storageMode, IVaultRecordTypeFactory recordTypes)
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
        var recordDefinition = recordTypes.Create(VaultRecordTypeFactory.File, name, contentType);

        return sessions.ExecuteAsync(async (database, key, ct) =>
        {
            storageMode.Require(VaultStorageMode.LocalEncrypted);
            // Do not overwrite a visible text record by uploading a file with
            // the same catalog name. Existing binary files require an explicit
            // version for replacement; the initial UI uses create-only writes.
            var entries = await database.Catalog.ListAsync(key, ct);
            var existing = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            if (existing is not null && (VaultRecordTypeFactory.IsLegacyText(existing.ContentType)))
                throw new InvalidOperationException("This name belongs to a text record.");
            storageMode.Require(VaultStorageMode.LocalEncrypted);
            return await database.PutIndexedStreamingFileAsync(
                name, expectedVersion, plaintext, key, recordDefinition.ContentType!, ct);
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
            storageMode.Require(VaultStorageMode.LocalEncrypted);
            var entries = await database.Catalog.ListAsync(key, ct);
            var metadata = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            if (metadata is null) return null;
            if (VaultRecordTypeFactory.IsLegacyText(metadata.ContentType))
                throw new InvalidOperationException("This record contains text, not a binary file.");
            var before = await database.GetVersionAsync(name, key, ct);
            if (before is null) return null;

            using var staging = new MemoryStream();
            try
            {
                await database.ReadStreamingFileAsync(name, staging, key, ct);
                var after = await database.GetVersionAsync(name, key, ct);
                if (after != before)
                    throw new VaultWriteConflictException(database.GetRecordId(name, key.Span));
                // A concurrent tombstone or content-type change must not allow
                // a stale read to return a file hidden from the active catalog.
                var currentEntries = await database.Catalog.ListAsync(key, ct);
                var current = currentEntries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
                if (current is null || VaultRecordTypeFactory.IsLegacyText(current.ContentType))
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

    /// <summary>
    /// Streaming download for hosts with a disposable, private seekable output.
    /// No whole-file byte array is allocated. The caller MUST NOT expose the
    /// output until this method returns successfully; on failure it is rolled
    /// back to its original length. Retains the existing catalog/version CAS
    /// checks rather than trusting a possibly stale file pointer.
    /// </summary>
    public Task<VaultRecordPointer?> CopyVerifiedToAsync(string name,
        Stream privateStaging, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(privateStaging);
        if (!privateStaging.CanSeek || !privateStaging.CanWrite ||
            privateStaging.Position != privateStaging.Length)
            throw new ArgumentException("Append-only seekable private file staging required.",
                nameof(privateStaging));
        return sessions.ExecuteAsync<VaultRecordPointer?>(async (database, key, ct) =>
        {
            storageMode.Require(VaultStorageMode.LocalEncrypted);
            long start = privateStaging.Position;
            try
            {
                var catalog = await database.Catalog.ListAsync(key, ct);
                var item = catalog.FirstOrDefault(x => string.Equals(
                    x.Name, name, StringComparison.Ordinal));
                if (item is null) return null;
                if (VaultRecordTypeFactory.IsLegacyText(item.ContentType))
                    throw new InvalidOperationException("This record is not a binary file.");
                var before = await database.GetVersionAsync(name, key, ct);
                if (before is null) return null;

                await database.ReadStreamingFileAsync(name, privateStaging, key, ct);
                if (await database.GetVersionAsync(name, key, ct) != before)
                    throw new VaultWriteConflictException(database.GetRecordId(name, key.Span));
                var visible = await database.Catalog.ListAsync(key, ct);
                var current = visible.FirstOrDefault(x => string.Equals(
                    x.Name, name, StringComparison.Ordinal));
                if (current is null || VaultRecordTypeFactory.IsLegacyText(current.ContentType) ||
                    await database.GetVersionAsync(name, key, ct) != before)
                    throw new VaultWriteConflictException(database.GetRecordId(name, key.Span));
                storageMode.Require(VaultStorageMode.LocalEncrypted);
                return before;
            }
            catch
            {
                privateStaging.SetLength(start);
                privateStaging.Position = start;
                throw;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Streams a previously authenticated encrypted file through a bounded
    /// nonseekable host export pipe. Does not materialize a whole-file array.
    /// The destination MUST be discarded/aborted if verification or the
    /// version check fails; it is intentionally not a reversible staging stream.
    /// </summary>
    public Task<VaultRecordPointer?> StreamVerifiedToAsync(string name,
        Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("Writable streaming destination required.", nameof(destination));
        return sessions.ExecuteAsync<VaultRecordPointer?>(async (database, key, ct) =>
        {
            storageMode.Require(VaultStorageMode.LocalEncrypted);
            var catalog = await database.Catalog.ListAsync(key, ct);
            var item = catalog.FirstOrDefault(x => string.Equals(
                x.Name, name, StringComparison.Ordinal));
            if (item is null) return null;
            if (VaultRecordTypeFactory.IsLegacyText(item.ContentType))
                throw new InvalidOperationException("This is not a binary file.");
            var before = await database.GetVersionAsync(name, key, ct);
            if (before is null) return null;
            await database.CopyStreamingFileToAsync(name, destination, key, ct);
            if (await database.GetVersionAsync(name, key, ct) != before)
                throw new VaultWriteConflictException(database.GetRecordId(name, key.Span));
            var after = await database.Catalog.ListAsync(key, ct);
            var visible = after.FirstOrDefault(x => string.Equals(
                x.Name, name, StringComparison.Ordinal));
            if (visible is null || VaultRecordTypeFactory.IsLegacyText(visible.ContentType) ||
                await database.GetVersionAsync(name, key, ct) != before)
                throw new VaultWriteConflictException(database.GetRecordId(name, key.Span));
            storageMode.Require(VaultStorageMode.LocalEncrypted);
            return before;
        }, cancellationToken);
    }

    /// <summary>Retrieve the visible file revision without decrypting its contents.
    /// Used for version-checked catalog removal, not for reading file plaintext.</summary>
    public Task<VaultRecordPointer?> GetFileVersionAsync(string name,
        CancellationToken cancellationToken = default) =>
        sessions.ExecuteAsync<VaultRecordPointer?>(async (database, key, ct) =>
        {
            storageMode.Require(VaultStorageMode.LocalEncrypted);
            var entries = await database.Catalog.ListAsync(key, ct);
            var entry = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            if (entry is null) return null;
            if (VaultRecordTypeFactory.IsLegacyText(entry.ContentType))
                throw new InvalidOperationException("This record is not an encrypted file.");
            var version = await database.GetVersionAsync(name, key, ct);
            if (version is null) return null;
            // Avoid returning a pointer after a concurrent catalog tombstone.
            var current = await database.Catalog.ListAsync(key, ct);
            var visible = current.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            if (visible is null || VaultRecordTypeFactory.IsLegacyText(visible.ContentType))
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
            storageMode.Require(VaultStorageMode.LocalEncrypted);
            var entries = await database.Catalog.ListAsync(key, ct);
            var entry = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            if (entry is null || VaultRecordTypeFactory.IsLegacyText(entry.ContentType))
                throw new InvalidOperationException("The selected file is no longer in the catalog.");
            var current = await database.GetVersionAsync(name, key, ct);
            if (current != expectedVersion)
                throw new VaultWriteConflictException(database.GetRecordId(name, key.Span));
            storageMode.Require(VaultStorageMode.LocalEncrypted);
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
