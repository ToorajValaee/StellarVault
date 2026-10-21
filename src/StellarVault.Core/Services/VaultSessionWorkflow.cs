using System.Security.Cryptography;
using StellarVault.Core.Abstractions;

namespace StellarVault.Core.Services;

/// <summary>Authorization gate for binding an unlocked vault to a Stellar account.
/// Implementations MUST verify a fresh, domain-separated challenge against the
/// account's current authorized signers; mere address validation is insufficient.</summary>
public interface IVaultAccountAuthorizer
{
    Task VerifyOwnershipAsync(string accountId, CancellationToken cancellationToken = default);
}

/// <summary>Secure default until a platform-specific Stellar signature verifier is installed.</summary>
public sealed class DenyVaultAccountAuthorizer : IVaultAccountAuthorizer
{
    public Task VerifyOwnershipAsync(string accountId, CancellationToken cancellationToken = default) =>
        throw new UnauthorizedAccessException("Stellar account ownership verification is not configured.");
}

/// <summary>Coordinates passphrase credentials and disposable sessions.
/// A vault may only be activated after ownership authorization succeeds.</summary>
public sealed class VaultSessionWorkflow(
    VaultPassphraseLifecycle credentials,
    IVaultAccountAuthorizer authorizer,
    VaultSessionManager sessions,
    IVaultChunkStore objects,
    IVaultRecordIndex pointers)
{
    private readonly SemaphoreSlim _transitions = new(1, 1);

    public async Task CreateAsync(string accountId, string passphrase, CancellationToken ct = default)
    {
        await _transitions.WaitAsync(ct);
        try
        {
            // A request canceled while waiting for the transition gate must not
            // lock the currently active account.
            ct.ThrowIfCancellationRequested();
            sessions.Lock();
            long epoch = sessions.Epoch;
            using var pending = new PendingTransition(sessions, epoch, ct);
            await authorizer.VerifyOwnershipAsync(accountId, pending.Token);
            pending.EnsureValid();
            byte[] key = await credentials.CreateAsync(accountId, passphrase, pending.Token);
            ActivateAfterAuthorization(accountId, key, epoch, pending);
        }
        finally { _transitions.Release(); }
    }

    public async Task UnlockAsync(string accountId, string passphrase, CancellationToken ct = default)
    {
        await _transitions.WaitAsync(ct);
        try
        {
            // A canceled request must not lock a different active account.
            ct.ThrowIfCancellationRequested();
            // Clear any other account before attempting authorization or derivation.
            sessions.Lock();
            long epoch = sessions.Epoch;
            using var pending = new PendingTransition(sessions, epoch, ct);
            await authorizer.VerifyOwnershipAsync(accountId, pending.Token);
            pending.EnsureValid();
            byte[] key = await credentials.UnlockAsync(accountId, passphrase, pending.Token);
            ActivateAfterAuthorization(accountId, key, epoch, pending);
        }
        finally { _transitions.Release(); }
    }

    public async Task RotatePassphraseAsync(string accountId, string oldPassphrase,
        string newPassphrase, CancellationToken ct = default)
    {
        await _transitions.WaitAsync(ct);
        try
        {
            // A request canceled while waiting for the transition gate must not
            // lock the currently active account.
            ct.ThrowIfCancellationRequested();
            sessions.Lock();
            long epoch = sessions.Epoch;
            using var pending = new PendingTransition(sessions, epoch, ct);
            await authorizer.VerifyOwnershipAsync(accountId, pending.Token);
            pending.EnsureValid();
            await credentials.RotateAsync(accountId, oldPassphrase, newPassphrase, pending.Token);
            // Storage commits cannot be rolled back, but never report a canceled
            // rotation as successful after an explicit user lock.
            pending.EnsureValid();
        }
        finally { _transitions.Release(); }
    }

    public void Lock() => sessions.Lock();

    /// <summary>
    /// A user lock cancels in-flight authorization/KDF work, including when no
    /// active session was installed. The epoch is still checked at activation.
    /// Cancellation cannot roll back an already committed credential write.
    /// </summary>
    private sealed class PendingTransition : IDisposable
    {
        private readonly VaultSessionManager _sessions;
        private readonly long _epoch;
        private readonly CancellationTokenSource _cancel;

        public PendingTransition(VaultSessionManager sessions, long epoch, CancellationToken requested)
        {
            _sessions = sessions;
            _epoch = epoch;
            _cancel = CancellationTokenSource.CreateLinkedTokenSource(requested);
            _sessions.StateChanged += OnStateChanged;
            OnStateChanged();
        }

        public CancellationToken Token => _cancel.Token;

        public void EnsureValid()
        {
            Token.ThrowIfCancellationRequested();
            if (_sessions.Epoch != _epoch)
                throw new OperationCanceledException("Vault was locked during authorization; retry explicitly.");
        }

        private void OnStateChanged()
        {
            if (_sessions.Epoch == _epoch) return;
            try { _cancel.Cancel(); }
            catch (ObjectDisposedException) { /* In-flight event during unsubscribe. */ }
        }

        public void Dispose()
        {
            _sessions.StateChanged -= OnStateChanged;
            _cancel.Dispose();
        }
    }

    /// <summary>Recheck cancellation after KDF and clear a rejected derived key.</summary>
    private void ActivateAfterAuthorization(string accountId, byte[] ownedKey,
        long expectedEpoch, PendingTransition pending)
    {
        try { pending.EnsureValid(); }
        catch
        {
            CryptographicOperations.ZeroMemory(ownedKey);
            throw;
        }
        Activate(accountId, ownedKey, expectedEpoch);
    }

    private void Activate(string accountId, byte[] ownedKey, long expectedEpoch)
    {
        bool transferred = false;
        try
        {
            var session = new VaultUnlockedSession(accountId, ownedKey, objects, pointers);
            transferred = true;
            try
            {
                if (!sessions.TryActivate(session, expectedEpoch))
                    throw new OperationCanceledException("Vault was locked while unlocking; retry explicitly.");
            }
            catch { session.Dispose(); throw; }
        }
        finally
        {
            if (!transferred) CryptographicOperations.ZeroMemory(ownedKey);
        }
    }
}
