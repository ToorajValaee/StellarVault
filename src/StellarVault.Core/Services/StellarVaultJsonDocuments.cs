using System.Security.Cryptography;
using System.Text.Json;
using StellarVault.Core.Abstractions;

namespace StellarVault.Core.Services;

/// <summary>Typed JSON documents over encrypted, versioned, account-scoped records.
/// Serialization happens in private memory; no plaintext JSON is stored by this layer.</summary>
public sealed class StellarVaultJsonDocuments(StellarAccountVaultDatabase database)
{
    public sealed record Snapshot<T>(T Value, VaultRecordPointer Version);

    /// <summary>Load an existing document and its optimistic-concurrency version.
    /// Returns null only when the named record has never been committed.</summary>
    public async Task<Snapshot<T>?> ReadAsync<T>(string name, ReadOnlyMemory<byte> vaultKey,
        JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        var version = await database.GetVersionAsync(name, vaultKey, cancellationToken);
        if (version is null) return null;
        using var staging = new MemoryStream();
        try
        {
            await database.GetAsync(name, staging, vaultKey, cancellationToken);
            if (staging.Length > VaultV2Envelope.MaximumPlaintextLength)
                throw new InvalidDataException("JSON document exceeds supported size.");
            // GetAsync may observe a later version. Never return a mismatched value/version pair.
            var current = await database.GetVersionAsync(name, vaultKey, cancellationToken);
            if (current != version)
                throw new VaultWriteConflictException(database.GetRecordId(name, vaultKey.Span));
            byte[] bytes = staging.ToArray();
            try
            {
                var value = JsonSerializer.Deserialize<T>(bytes, options)
                    ?? throw new InvalidDataException("JSON document deserialized to null.");
                return new Snapshot<T>(value, version);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally
        {
            // Stream disposal does not erase decrypted JSON in its backing array.
            if (staging.TryGetBuffer(out ArraySegment<byte> segment) && segment.Array is not null)
                CryptographicOperations.ZeroMemory(segment.Array);
        }
    }

    /// <summary>Write a typed JSON document with compare-and-swap semantics.
    /// The catalog is updated after the document commits, so listing is eventually consistent.</summary>
    public async Task<VaultRecordPointer> WriteAsync<T>(string name, T value,
        VaultRecordPointer? expectedVersion, ReadOnlyMemory<byte> vaultKey,
        JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, options);
        try
        {
            if (json.Length > VaultV2Envelope.MaximumPlaintextLength)
                throw new InvalidDataException("JSON document exceeds supported size.");
            using var source = new MemoryStream(json, writable: false);
            return await database.PutIndexedAsync(name, expectedVersion, source, vaultKey,
                "application/json", cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(json); }
    }
}
