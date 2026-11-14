using System.Security.Cryptography;

namespace StellarVault.Core.Services;

/// <summary>
/// An independent, explicitly unlocked queue encryption-key session for imported
/// Stellar S-key vaults. Reuses the existing Argon2id + random wrapped data-key
/// lifecycle but NEVER reads or activates a mnemonic/HD local vault session.
/// Its credential store MUST use a separate durable namespace from HD.
/// This service is not the queue publisher and does not submit transactions.
/// </summary>
public sealed class VaultLinkedQueueKeySession(
    IStellarSecretKeyLedger ledger,
    VaultNetworkSelection network,
    VaultDeviceRetentionGate retention,
    IVaultLinkedPassphraseCredentialStore credentialStore,
    IVaultKeyProtector protector) : IVaultQueueKeySession, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly VaultPassphraseLifecycle _credentials = new(credentialStore, protector);
    private byte[]? _key;
    private string? _root;
    private string? _scope;
    private string? _networkPassphrase;
    private bool _disposed;

    public bool IsUnlocked => !_disposed && _key is not null;

    private (string Root, string Scope, string Passphrase) RequireSigner()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VaultLinkedQueueKeySession));
        retention.RequireLocalPersistence();
        if (!ledger.IsConnected || ledger.RootAccountId is not { } root ||
            ledger.ConnectedNetworkPassphrase is not { Length: > 0 } selected)
            throw new UnauthorizedAccessException("Unlock the independent Stellar S-key ledger first.");
        StellarAccountAddress.Validate(root);
        var expected = network.Network switch
        {
            "testnet" => "Test SDF Network ; September 2015",
            "public" => "Public Global Stellar Network ; September 2015",
            "custom" => network.CustomPassphrase,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(expected) ||
            !string.Equals(expected, selected, StringComparison.Ordinal))
            throw new UnauthorizedAccessException(
                "The connected S-key network differs from the selected device-vault network.");
        return (root, network.CredentialKey(root), selected);
    }

    private void RequireSameSigner(string root, string scope, string passphrase)
    {
        var current = RequireSigner();
        if (current.Root != root || current.Scope != scope ||
            current.Passphrase != passphrase)
            throw new InvalidOperationException(
                "S-key signer or network changed during local queue authorization.");
    }

    private void ClearUnsafe()
    {
        if (_key is not null) CryptographicOperations.ZeroMemory(_key);
        _key = null;
        _root = null;
        _scope = null;
        _networkPassphrase = null;
    }

    /// <summary>
    /// Creates a separately namespaced, Argon2id-wrapped 256-bit random key.
    /// This is explicit local retention consent, not authorization to sync.
    /// </summary>
    public Task CreateAsync(string localPassphrase, CancellationToken ct = default) =>
        ActivateAsync(localPassphrase, create: true, ct);

    public Task UnlockAsync(string localPassphrase, CancellationToken ct = default) =>
        ActivateAsync(localPassphrase, create: false, ct);

    public async Task<bool> HasLocalCredentialsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var (root, scope, passphrase) = RequireSigner();
            var entry = await _credentials.ReadCredentialForDiscoveryAsync(root, ct);
            try
            {
                RequireSameSigner(root, scope, passphrase);
                return entry is not null;
            }
            finally
            {
                if (entry is not null) CryptographicOperations.ZeroMemory(entry);
            }
        }
        finally { _gate.Release(); }
    }


    private async Task ActivateAsync(string localPassphrase, bool create, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(localPassphrase) || localPassphrase.Length < 12)
            throw new ArgumentException(
                "Use a separate local queue passphrase of at least 12 characters.",
                nameof(localPassphrase));
        await _gate.WaitAsync(ct);
        try
        {
            ClearUnsafe();
            var (root, scope, passphrase) = RequireSigner();
            // A validated connected chain and S-key signer are required before
            // any wrapped credential is created or unlocked locally.
            var account = await ledger.InspectAccountInfoAsync(ct);
            if (account.RootAccountId != root)
                throw new UnauthorizedAccessException("Linked account root changed.");
            RequireSameSigner(root, scope, passphrase);

            byte[]? key = null;
            try
            {
                key = create
                    ? await _credentials.CreateAsync(root, localPassphrase, ct)
                    : await _credentials.UnlockAsync(root, localPassphrase, ct);
                ct.ThrowIfCancellationRequested();
                RequireSameSigner(root, scope, passphrase);
                _key = key;
                key = null;
                _root = root;
                _scope = scope;
                _networkPassphrase = passphrase;
            }
            finally
            {
                if (key is not null) CryptographicOperations.ZeroMemory(key);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The strategy-specific caller MUST pass its exact selected network scope.
    /// Temporary key copies are cleared after the callback, even on failure.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        string accountId, string networkId,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _gate.WaitAsync(ct);
        byte[]? copy = null;
        try
        {
            if (_key is null || _root != accountId || _scope is null ||
                VaultLinkedQueueStorageScope.FromNetworkScope(_scope) != networkId ||
                _networkPassphrase is null)
                throw new UnauthorizedAccessException(
                    "Unlock the matching independent S-key local queue first.");
            RequireSameSigner(_root, _scope, _networkPassphrase);
            copy = _key.ToArray();
            var result = await operation(copy, ct);
            ct.ThrowIfCancellationRequested();
            RequireSameSigner(_root, _scope, _networkPassphrase);
            return result;
        }
        finally
        {
            if (copy is not null) CryptographicOperations.ZeroMemory(copy);
            _gate.Release();
        }
    }

    public async Task LockAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { ClearUnsafe(); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            ClearUnsafe();
            _disposed = true;
        }
        finally { _gate.Release(); }
        // The semaphore is intentionally not disposed: queued asynchronous calls
        // should observe the disposed flag rather than race a disposed lock.
    }
}

/// <summary>
/// Credential records MUST be stored in a strategy-specific namespace,
/// separate from all mnemonic/local HD vault credential records.
/// The same authenticated credential format and CAS semantics are reused.
/// </summary>
public interface IVaultLinkedPassphraseCredentialStore : IVaultPassphraseCredentialStore
{
}
