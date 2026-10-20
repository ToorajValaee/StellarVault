using System.Security.Cryptography;
using StellarVault.Core.Abstractions;

namespace StellarVault.Core.Services;

/// <summary>Short-lived, explicitly disposable unlocked vault session.
/// Key bytes are kept in a private array and zeroed on disposal. Do not retain
/// references to key memory outside a session operation.</summary>
public sealed class VaultUnlockedSession : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private byte[]? _dataKey;
    private bool _disposed;
    public string AccountId { get; }
    public StellarAccountVaultDatabase Database { get; }

    internal VaultUnlockedSession(string accountId, byte[] ownedDataKey,
        IVaultChunkStore objects, IVaultRecordIndex pointers)
    {
        StellarAccountAddress.Validate(accountId);
        if (ownedDataKey.Length != VaultKeyEnvelope.DataKeyLength)
            throw new ArgumentException("Expected 256-bit vault key.", nameof(ownedDataKey));
        AccountId = accountId;
        _dataKey = ownedDataKey;
        Database = new StellarAccountVaultDatabase(objects, pointers, accountId);
    }

    /// <summary>Execute a database operation with a temporary key copy. The copy is
    /// zeroed after the operation; disposal prevents new operations and waits for
    /// active operations before clearing the session key.</summary>
    public async Task<T> ExecuteAsync<T>(Func<StellarAccountVaultDatabase, ReadOnlyMemory<byte>, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await _operations.WaitAsync(cancellationToken);
        byte[]? copy = null;
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                copy = _dataKey!.ToArray();
            }
            return await action(Database, copy, cancellationToken);
        }
        finally
        {
            if (copy is not null) CryptographicOperations.ZeroMemory(copy);
            _operations.Release();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _operations.Wait();
        try
        {
            lock (_sync)
            {
                if (_dataKey is not null) CryptographicOperations.ZeroMemory(_dataKey);
                _dataKey = null;
            }
        }
        finally { _operations.Release(); }
        GC.SuppressFinalize(this);
    }
}
