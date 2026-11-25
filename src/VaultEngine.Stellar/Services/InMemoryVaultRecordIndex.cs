using VaultEngine.Stellar.Abstractions;

namespace VaultEngine.Stellar.Services;

/// <summary>Thread-safe development-only record pointer store with compare-and-swap semantics.
/// Not persistent and not a defense against a malicious storage provider or rollback.</summary>
public sealed class InMemoryVaultRecordIndex : IVaultRecordIndex
{
    private readonly Dictionary<Guid, VaultRecordPointer> _pointers = new();
    private readonly object _gate = new();

    public Task<VaultRecordPointer?> ReadAsync(Guid recordId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (recordId == Guid.Empty) throw new ArgumentException("Record ID is required.", nameof(recordId));
        lock (_gate)
        {
            _pointers.TryGetValue(recordId, out var value);
            return Task.FromResult(value);
        }
    }

    public Task<bool> CompareExchangeAsync(Guid recordId, VaultRecordPointer? expected,
        VaultRecordPointer next, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (recordId == Guid.Empty) throw new ArgumentException("Record ID is required.", nameof(recordId));
        ArgumentNullException.ThrowIfNull(next);
        if (string.IsNullOrWhiteSpace(next.ManifestContentId) || next.ManifestContentId.Length > 256 || next.Generation < 0)
            throw new ArgumentException("Invalid manifest pointer.", nameof(next));
        lock (_gate)
        {
            _pointers.TryGetValue(recordId, out var current);
            if (current != expected) return Task.FromResult(false);
            if (next.Generation != (current is null ? 0 : checked(current.Generation + 1)))
                throw new ArgumentException("Record generation must advance exactly once.", nameof(next));
            _pointers[recordId] = next;
            return Task.FromResult(true);
        }
    }
}
