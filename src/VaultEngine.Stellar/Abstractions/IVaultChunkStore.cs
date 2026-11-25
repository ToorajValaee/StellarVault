namespace VaultEngine.Stellar.Abstractions;

/// <summary>Storage backend for opaque encrypted chunks. No plaintext or signing keys belong here.</summary>
public interface IVaultChunkStore
{
    /// <summary>Stores an immutable encrypted object and returns its content identifier.</summary>
    Task<string> PutAsync(Stream encryptedContent, CancellationToken cancellationToken = default);

    /// <summary>Opens an encrypted object by identifier. Caller owns the returned stream.</summary>
    Task<Stream> OpenReadAsync(string contentId, CancellationToken cancellationToken = default);

    /// <summary>Removes an unreachable encrypted object during garbage collection.</summary>
    Task DeleteAsync(string contentId, CancellationToken cancellationToken = default);
}

/// <summary>Atomic compare-and-swap pointer for authenticated record manifests.
/// Backends must prevent lost updates. This does not by itself prevent malicious rollback.</summary>
public interface IVaultRecordIndex
{
    Task<VaultRecordPointer?> ReadAsync(Guid recordId, CancellationToken cancellationToken = default);
    Task<bool> CompareExchangeAsync(Guid recordId, VaultRecordPointer? expected,
        VaultRecordPointer next, CancellationToken cancellationToken = default);
}

/// <summary>Immutable pointer to a separately encrypted/authenticated record manifest.</summary>
public sealed record VaultRecordPointer(string ManifestContentId, long Generation);
