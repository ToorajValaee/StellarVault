namespace StellarVault.Core.Services;

/// <summary>Application-facing owner of a single unlocked account session.
/// Call Lock() on app backgrounding, account switch, or explicit user lock.
/// Session operations should not be run on the UI thread.</summary>
public sealed class VaultSessionManager : IDisposable
{
    private readonly object _sync = new();
    private VaultUnlockedSession? _active;
    private bool _disposed;

    public string? ActiveAccountId
    {
        get { lock (_sync) return _active?.AccountId; }
    }

    public event Action? StateChanged;

    public bool IsUnlocked
    {
        get { lock (_sync) return _active is not null; }
    }

    /// <summary>Atomically install an unlocked session and dispose the prior session.
    /// Ownership transfers to the manager only after successful installation.</summary>
    public void Activate(VaultUnlockedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        VaultUnlockedSession? previous;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ReferenceEquals(_active, session)) return;
            previous = _active;
            _active = session;
        }
        try { previous?.Dispose(); }
        finally { StateChanged?.Invoke(); }
    }

    /// <summary>Run an operation only while a vault is unlocked.
    /// A concurrent lock may dispose the session; callers must handle cancellation
    /// or ObjectDisposedException rather than retrying with cached secrets.</summary>
    public Task<T> ExecuteAsync<T>(
        Func<StellarAccountVaultDatabase, ReadOnlyMemory<byte>, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        VaultUnlockedSession active;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            active = _active ?? throw new InvalidOperationException("Vault is locked.");
        }
        return active.ExecuteAsync(operation, cancellationToken);
    }

    /// <summary>Clear the active vault key. Must be called outside a session operation.</summary>
    public void Lock()
    {
        VaultUnlockedSession? previous;
        lock (_sync)
        {
            previous = _active;
            _active = null;
        }
        try { previous?.Dispose(); }
        finally { if (previous is not null) StateChanged?.Invoke(); }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Lock();
        GC.SuppressFinalize(this);
    }
}
