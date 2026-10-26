using System.Security.Cryptography;
using System.Text.Json;
using StellarVault.Core.Abstractions;

namespace StellarVault.Core.Services;

/// <summary>Encrypted per-account directory of logical record names and user metadata.
/// The catalog is itself a versioned encrypted vault record; names never appear in filesystem paths.
/// Catalog updates use optimistic concurrency, but record writes and catalog updates are not atomic.</summary>
public sealed class StellarVaultRecordCatalog(StellarAccountVaultDatabase database)
{
    private const string CatalogName = "__svault/system/catalog/v1";
    private const int MaxEntries = 2048;
    private const int MaxCatalogBytes = VaultV2Envelope.MaximumPlaintextLength;

    public sealed record Entry(string Name, string? ContentType, long UpdatedUnixSeconds, bool Deleted);
    private sealed record Document(int SchemaVersion, List<Entry> Entries);

    /// <summary>Return a snapshot of active named records. Catalog content is authenticated and encrypted.</summary>
    public async Task<IReadOnlyList<Entry>> ListAsync(ReadOnlyMemory<byte> vaultKey,
        CancellationToken cancellationToken = default)
    {
        var (_, document) = await LoadAsync(vaultKey, cancellationToken);
        return document.Entries.Where(e => !e.Deleted).OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Update metadata for a committed record. The record write must happen first.</summary>
    public Task RegisterAsync(string name, string? contentType, ReadOnlyMemory<byte> vaultKey,
        CancellationToken cancellationToken = default) =>
        MutateAsync(name, contentType, deleted: false, vaultKey, cancellationToken);

    /// <summary>Hide a record from the catalog without deleting its encrypted history.
    /// This is a logical tombstone, not secure erasure or revocation of existing ciphertext.</summary>
    public Task TombstoneAsync(string name, ReadOnlyMemory<byte> vaultKey,
        CancellationToken cancellationToken = default) =>
        MutateAsync(name, null, deleted: true, vaultKey, cancellationToken);

    private async Task MutateAsync(string name, string? contentType, bool deleted,
        ReadOnlyMemory<byte> vaultKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("__svault/", StringComparison.Ordinal) ||
            System.Text.Encoding.UTF8.GetByteCount(name) > 512 || name.Contains('\0'))
            throw new ArgumentException("Invalid or reserved catalog record name.", nameof(name));
        if (contentType is not null && (contentType.Length > 128 || contentType.Contains('\0')))
            throw new ArgumentException("Content type exceeds limits.", nameof(contentType));
        for (int attempt = 0; attempt < 8; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var (version, document) = await LoadAsync(vaultKey, ct);
            int existing = document.Entries.FindIndex(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            if (existing < 0 && document.Entries.Count >= MaxEntries)
                throw new InvalidDataException("Encrypted catalog is full.");
            var entry = new Entry(name, contentType, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), deleted);
            if (existing < 0) document.Entries.Add(entry);
            else document.Entries[existing] = entry;
            byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(document);
            try
            {
                if (serialized.Length > MaxCatalogBytes)
                    throw new InvalidDataException("Encrypted catalog exceeds record limit.");
                using var source = new MemoryStream(serialized, writable: false);
                try
                {
                    await database.PutAsync(CatalogName, version, source, vaultKey, ct);
                    return;
                }
                catch (VaultWriteConflictException) when (attempt < 7) { }
            }
            finally { CryptographicOperations.ZeroMemory(serialized); }
        }
        throw new InvalidOperationException("Catalog update conflicted repeatedly; retry later.");
    }

    private async Task<(VaultRecordPointer? Version, Document Data)> LoadAsync(
        ReadOnlyMemory<byte> key, CancellationToken ct)
    {
        var version = await database.GetVersionAsync(CatalogName, key, ct);
        if (version is null) return (null, new Document(1, new List<Entry>()));
        using var staging = new MemoryStream();
        try
        {
            return await LoadDecryptedCatalogAsync(version, staging, key, ct);
        }
        finally
        {
            // The stream's decrypted backing buffer otherwise survives Dispose.
            if (staging.TryGetBuffer(out ArraySegment<byte> segment) && segment.Array is not null)
                CryptographicOperations.ZeroMemory(segment.Array);
        }
    }

    private async Task<(VaultRecordPointer? Version, Document Data)> LoadDecryptedCatalogAsync(
        VaultRecordPointer version, MemoryStream staging, ReadOnlyMemory<byte> key, CancellationToken ct)
    {
        await database.GetAsync(CatalogName, staging, key, ct);
        // Prevent using a newer catalog body with an older CAS pointer.
        var afterRead = await database.GetVersionAsync(CatalogName, key, ct);
        if (afterRead != version)
            throw new VaultWriteConflictException(database.GetRecordId(CatalogName, key.Span));
        if (staging.Length > MaxCatalogBytes) throw new InvalidDataException("Catalog exceeds size limit.");
        byte[] plaintext = staging.ToArray();
        try
        {
            var document = JsonSerializer.Deserialize<Document>(plaintext)
                ?? throw new InvalidDataException("Catalog is missing.");
            if (document.SchemaVersion != 1 || document.Entries is null ||
                document.Entries.Count > MaxEntries)
                throw new InvalidDataException("Invalid encrypted catalog schema.");

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in document.Entries)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Name) ||
                    entry.Name.StartsWith("__svault/", StringComparison.Ordinal) ||
                    System.Text.Encoding.UTF8.GetByteCount(entry.Name) > 512 ||
                    entry.Name.Contains('\0') || !names.Add(entry.Name) ||
                    entry.UpdatedUnixSeconds < 0 ||
                    (entry.ContentType is not null &&
                     (entry.ContentType.Length > 128 || entry.ContentType.Contains('\0'))))
                    throw new InvalidDataException("Invalid encrypted catalog entry.");
            }
            return (version, document);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}
