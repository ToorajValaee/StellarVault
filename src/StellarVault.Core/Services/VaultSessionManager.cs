namespace StellarVault.Core.Services;

/// <summary>Application-facing owner of a single unlocked account session.
/// Call Lock() on app backgrounding, account switch, or explicit user lock.
/// Session operations should not be run on the UI thread.</summary>
public sealed class VaultSessionManager : IDisposable
{
    private readonly object _sync = new();
    private VaultUnlockedSession? _active;
    private bool _disposed;
    private long _epoch;

    /// <summary>Invalidation token for pending unlock operations. Lock or account change
    /// advances it, so an operation started earlier cannot resurrect an old session.</summary>
    public long Epoch { get { lock (_sync) return _epoch; } }

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
            _epoch++;
        }
        try { previous?.Dispose(); }
        finally { StateChanged?.Invoke(); }
    }

    /// <summary>Install a session only if no lock or account transition occurred
    /// after an asynchronous authorization/unlock began. Caller retains ownership on failure.</summary>
    public bool TryActivate(VaultUnlockedSession session, long expectedEpoch)
    {
        ArgumentNullException.ThrowIfNull(session);
        VaultUnlockedSession? previous;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_epoch != expectedEpoch) return false;
            previous = _active;
            _active = session;
            _epoch++;
        }
        try { previous?.Dispose(); }
        finally { StateChanged?.Invoke(); }
        return true;
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
            _epoch++;
        }
        try { previous?.Dispose(); }
        finally
        {
            // Notify even if already locked: an in-flight ownership verification
            // or KDF operation must learn that its starting epoch was invalidated.
            StateChanged?.Invoke();
        }
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
