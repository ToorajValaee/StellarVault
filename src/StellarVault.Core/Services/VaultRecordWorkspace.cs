using System.Text;
using System.Security.Cryptography;
using StellarVault.Core.Abstractions;

namespace StellarVault.Core.Services;

/// <summary>Shared PWA/MAUI application service for encrypted UTF-8 records.
/// All operations are guarded by the active session and use the encrypted catalog.
/// It never caches data keys or persists plaintext.</summary>
public sealed class VaultRecordWorkspace(VaultSessionManager sessions, VaultStorageModeGate storageMode)
{
    public sealed record RecordSummary(string Name, string? ContentType, long UpdatedUnixSeconds);
    public sealed record TextRecord(string Name, string Text, VaultRecordPointer Version);

    public Task<IReadOnlyList<RecordSummary>> ListAsync(CancellationToken ct = default) =>
        sessions.ExecuteAsync<IReadOnlyList<RecordSummary>>(async (db, key, token) =>
        {
            storageMode.Require(VaultStorageMode.LocalEncrypted);
            var entries = await db.Catalog.ListAsync(key, token);
            return entries.Select(e => new RecordSummary(e.Name, e.ContentType, e.UpdatedUnixSeconds)).ToArray();
        }, ct);

    public Task<TextRecord?> ReadTextAsync(string name, CancellationToken ct = default) =>
        sessions.ExecuteAsync<TextRecord?>(async (db, key, token) =>
        {
            storageMode.Require(VaultStorageMode.LocalEncrypted);
            var entries = await db.Catalog.ListAsync(key, token);
            var metadata = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            if (metadata is null) return null;
            if (!VaultRecordTypeFactory.IsLegacyText(metadata.ContentType))
                throw new InvalidOperationException("This record contains a binary file, not editable text.");
            var version = await db.GetVersionAsync(name, key, token);
            if (version is null) return null;
            using var staging = new MemoryStream();
            try
            {
                await db.GetAsync(name, staging, key, token);
                if (staging.Length > VaultV2Envelope.MaximumPlaintextLength)
                    throw new InvalidDataException("Record exceeds supported size.");
                var current = await db.GetVersionAsync(name, key, token);
                if (current != version)
                    throw new VaultWriteConflictException(db.GetRecordId(name, key.Span));
                var currentEntries = await db.Catalog.ListAsync(key, token);
                var currentMetadata = currentEntries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
                if (currentMetadata is null || (!VaultRecordTypeFactory.IsLegacyText(currentMetadata.ContentType)))
                    throw new VaultWriteConflictException(db.GetRecordId(name, key.Span));
                // Close the gap between catalog revalidation and returning plaintext.
                // This narrows the race; fully atomic snapshots require a storage-level transaction.
                if (await db.GetVersionAsync(name, key, token) != version)
                    throw new VaultWriteConflictException(db.GetRecordId(name, key.Span));
                byte[] bytes = staging.ToArray();
                try { return new TextRecord(name, new UTF8Encoding(false, true).GetString(bytes), version); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            finally
            {
                // Disposing a MemoryStream does not erase its decrypted backing array.
                if (staging.TryGetBuffer(out ArraySegment<byte> segment) && segment.Array is not null)
                    CryptographicOperations.ZeroMemory(segment.Array);
            }
        }, ct);

    public Task<VaultRecordPointer> SaveTextAsync(string name, string text,
        VaultRecordPointer? expectedVersion, CancellationToken ct = default) =>
        sessions.ExecuteAsync(async (db, key, token) =>
        {
            storageMode.Require(VaultStorageMode.LocalEncrypted);
            ArgumentNullException.ThrowIfNull(text);
            // A text edit must never silently replace a file with the same name.
            // Catalog metadata is encrypted and scoped to the unlocked account.
            var catalog = await db.Catalog.ListAsync(key, token);
            var existing = catalog.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            if (existing is not null && !VaultRecordTypeFactory.IsLegacyText(existing.ContentType))
                throw new InvalidOperationException("This record contains a binary file, not editable text.");
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            try
            {
                if (bytes.Length > VaultV2Envelope.MaximumPlaintextLength)
                    throw new InvalidDataException("Record exceeds supported size.");
                using var plaintext = new MemoryStream(bytes, writable: false);
                storageMode.Require(VaultStorageMode.LocalEncrypted);
                return await db.PutIndexedAsync(name, expectedVersion, plaintext, key, "text/plain; charset=utf-8", token);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }, ct);

    public Task DeleteAsync(string name, VaultRecordPointer expectedVersion, CancellationToken ct = default) =>
        sessions.ExecuteAsync(async (db, key, token) =>
        {
            storageMode.Require(VaultStorageMode.LocalEncrypted);
            // A logical deletion must not silently target a different record revision.
            ArgumentNullException.ThrowIfNull(expectedVersion);
            var entries = await db.Catalog.ListAsync(key, token);
            var metadata = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            if (metadata is null || !VaultRecordTypeFactory.IsLegacyText(metadata.ContentType))
                throw new InvalidOperationException("The selected text record is no longer in the catalog.");
            var current = await db.GetVersionAsync(name, key, token);
            if (current != expectedVersion)
                throw new VaultWriteConflictException(db.GetRecordId(name, key.Span));
            // This check and catalog update are not atomic across separate record pointers.
            // Logical deletion only; old ciphertext may remain in immutable object history.
            storageMode.Require(VaultStorageMode.LocalEncrypted);
            await db.Catalog.TombstoneAsync(name, key, token);
            return true;
        }, ct);
}
