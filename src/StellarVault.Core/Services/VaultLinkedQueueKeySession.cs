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
    VaultLinkedLocalRetentionGate linkedRetention,
    IVaultLinkedPassphraseCredentialStore credentialStore,
    IVaultKeyProtector protector) : IVaultQueueKeySession, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly VaultPassphraseLifecycle _credentials = new(credentialStore, protector);
    private byte[]? _key;
    private string? _root;
    private string? _scope;
    private string? _networkPassphrase;
    private long? _connectionEpoch;
    private bool _offlineOnly;
    private bool _disposed;

    public bool IsUnlocked => !_disposed && _key is not null &&
        linkedRetention.DeviceRetention.AllowsLocalPersistence &&
        linkedRetention.AllowsLocalPersistence &&
        _root is not null && _scope == network.CredentialKey(_root) &&
        (_offlineOnly
            ? (!ledger.IsConnected || ledger.RootAccountId == _root)
            : ledger.IsConnected &&
              _connectionEpoch == ledger.ConnectionEpoch &&
              ledger.RootAccountId == _root);
    public bool IsOfflineOnly => IsUnlocked && _offlineOnly;
    public string? ActiveRootAccountId => IsUnlocked ? _root : null;

    /// <summary>
    /// An offline passphrase unlock grants encrypted local operations ONLY.
    /// Publication requires a NEW live signer-backed queue unlock with the
    /// exact selected Stellar network, not merely a later connection.
    /// </summary>
    public bool CanPublish => IsUnlocked && !_offlineOnly &&
        _networkPassphrase is not null &&
        _networkPassphrase == (network.Network switch
        {
            "testnet" => "Test SDF Network ; September 2015",
            "public" => "Public Global Stellar Network ; September 2015",
            "custom" => network.CustomPassphrase,
            _ => null
        }) &&
        ledger.ConnectedNetworkPassphrase == _networkPassphrase;

    private (string Root, string Scope, string Passphrase, long Epoch) RequireSigner()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VaultLinkedQueueKeySession));
        linkedRetention.DeviceRetention.RequireLocalPersistence();
        linkedRetention.RequireLocalPersistence();
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
        return (root, network.CredentialKey(root), selected, ledger.ConnectionEpoch);
    }

    private void RequireSameSigner(string root, string scope, string passphrase, long epoch)
    {
        var current = RequireSigner();
        if (current.Root != root || current.Scope != scope ||
            current.Passphrase != passphrase || current.Epoch != epoch)
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
        _connectionEpoch = null;
        _offlineOnly = false;
    }

    /// <summary>
    /// Creates a separately namespaced, Argon2id-wrapped 256-bit random key.
    /// This is explicit local retention consent, not authorization to sync.
    /// </summary>
    public Task CreateAsync(string localPassphrase, CancellationToken ct = default) =>
        ActivateAsync(localPassphrase, create: true, ct);

    public Task UnlockAsync(string localPassphrase, CancellationToken ct = default) =>
        ActivateAsync(localPassphrase, create: false, ct);

    /// <summary>
    /// Unlock an EXISTING wrapped queue key for local-only draft work using the
    /// public root address and the separately chosen local passphrase.
    /// No imported S-key or network access is needed. This NEVER creates a new
    /// device credential, proves Stellar ownership or authorizes publication.
    /// </summary>
    public async Task UnlockOfflineAsync(string publicRoot, string localPassphrase,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(localPassphrase) || localPassphrase.Length < 12)
            throw new ArgumentException("Local queue passphrase must be at least 12 characters.",
                nameof(localPassphrase));
        StellarAccountAddress.Validate(publicRoot);
        await _gate.WaitAsync(ct);
        byte[]? derived = null;
        try
        {
            ClearUnsafe();
            linkedRetention.RequireLocalPersistence();
            var selected = network.CredentialKey(publicRoot);
            if (ledger.IsConnected && ledger.RootAccountId != publicRoot)
                throw new UnauthorizedAccessException(
                    "Connected signer belongs to a different account.");
            derived = await _credentials.UnlockAsync(publicRoot, localPassphrase, ct);
            ct.ThrowIfCancellationRequested();
            linkedRetention.RequireLocalPersistence();
            if (network.CredentialKey(publicRoot) != selected ||
                ledger.IsConnected && ledger.RootAccountId != publicRoot)
                throw new InvalidOperationException("Local vault account or network changed.");
            _root = publicRoot;
            _scope = selected;
            _key = derived;
            derived = null;
            _offlineOnly = true;
        }
        finally
        {
            if (derived is not null) CryptographicOperations.ZeroMemory(derived);
            _gate.Release();
        }
    }

    public async Task<bool> HasLocalCredentialsOfflineAsync(string publicRoot,
        CancellationToken ct = default)
    {
        StellarAccountAddress.Validate(publicRoot);
        await _gate.WaitAsync(ct);
        try
        {
            linkedRetention.RequireLocalPersistence();
            var selected = network.CredentialKey(publicRoot);
            byte[]? stored = await _credentials.ReadCredentialForDiscoveryAsync(publicRoot, ct);
            try
            {
                linkedRetention.RequireLocalPersistence();
                if (network.CredentialKey(publicRoot) != selected)
                    throw new InvalidOperationException("Selected network changed.");
                return stored is not null;
            }
            finally
            {
                if (stored is not null) CryptographicOperations.ZeroMemory(stored);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> HasLocalCredentialsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var (root, scope, passphrase, epoch) = RequireSigner();
            var entry = await _credentials.ReadCredentialForDiscoveryAsync(root, ct);
            try
            {
                RequireSameSigner(root, scope, passphrase, epoch);
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
            var (root, scope, passphrase, epoch) = RequireSigner();
            // A validated connected chain and S-key signer are required before
            // any wrapped credential is created or unlocked locally.
            var account = await ledger.InspectAccountInfoAsync(ct);
            if (account.RootAccountId != root)
                throw new UnauthorizedAccessException("Linked account root changed.");
            RequireSameSigner(root, scope, passphrase, epoch);

            byte[]? key = null;
            try
            {
                key = create
                    ? await _credentials.CreateAsync(root, localPassphrase, ct)
                    : await _credentials.UnlockAsync(root, localPassphrase, ct);
                ct.ThrowIfCancellationRequested();
                RequireSameSigner(root, scope, passphrase, epoch);
                _key = key;
                key = null;
                _root = root;
                _scope = scope;
                _networkPassphrase = passphrase;
                _connectionEpoch = epoch;
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
                (_offlineOnly ? false :
                    _networkPassphrase is null || _connectionEpoch is null))
                throw new UnauthorizedAccessException(
                    "Unlock the matching independent S-key local queue first.");
            if (_offlineOnly)
            {
                // Local passphrase-derived access authorizes ONLY this encrypted
                // local namespace. No Horizon or signer call is made.
                if (!IsUnlocked)
                    throw new UnauthorizedAccessException("Offline queue scope changed.");
                copy = _key.ToArray();
                var localResult = await operation(copy, ct);
                ct.ThrowIfCancellationRequested();
                linkedRetention.RequireLocalPersistence();
                if (!IsUnlocked || _root != accountId ||
                    _scope is null ||
                    VaultLinkedQueueStorageScope.FromNetworkScope(_scope) != networkId)
                    throw new InvalidOperationException("Offline local queue context changed.");
                return localResult;
            }

            RequireSameSigner(_root, _scope, _networkPassphrase!, _connectionEpoch!.Value);
            // Online signer-backed unlocks retain full live Horizon inspection.
            var before = await ledger.InspectAccountInfoAsync(ct);
            if (before.RootAccountId != _root)
                throw new UnauthorizedAccessException("Linked signer is no longer available.");
            copy = _key.ToArray();
            var result = await operation(copy, ct);
            ct.ThrowIfCancellationRequested();
            RequireSameSigner(_root, _scope, _networkPassphrase!, _connectionEpoch!.Value);
            var after = await ledger.InspectAccountInfoAsync(ct);
            if (after.RootAccountId != _root ||
                after.TotalAccounts != before.TotalAccounts ||
                after.TailAccountId != before.TailAccountId)
                throw new InvalidOperationException(
                    "Linked account signer or chain changed during local encrypted queue access.");
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
