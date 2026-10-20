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
            await authorizer.VerifyOwnershipAsync(accountId, ct);
            byte[] key = await credentials.CreateAsync(accountId, passphrase, ct);
            Activate(accountId, key);
        }
        finally { _transitions.Release(); }
    }

    public async Task UnlockAsync(string accountId, string passphrase, CancellationToken ct = default)
    {
        await _transitions.WaitAsync(ct);
        try
        {
            // Clear any other account before attempting authorization or derivation.
            sessions.Lock();
            await authorizer.VerifyOwnershipAsync(accountId, ct);
            byte[] key = await credentials.UnlockAsync(accountId, passphrase, ct);
            Activate(accountId, key);
        }
        finally { _transitions.Release(); }
    }

    public async Task RotatePassphraseAsync(string accountId, string oldPassphrase,
        string newPassphrase, CancellationToken ct = default)
    {
        await _transitions.WaitAsync(ct);
        try
        {
            sessions.Lock();
            await authorizer.VerifyOwnershipAsync(accountId, ct);
            await credentials.RotateAsync(accountId, oldPassphrase, newPassphrase, ct);
        }
        finally { _transitions.Release(); }
    }

    public void Lock() => sessions.Lock();

    private void Activate(string accountId, byte[] ownedKey)
    {
        bool transferred = false;
        try
        {
            var session = new VaultUnlockedSession(accountId, ownedKey, objects, pointers);
            transferred = true;
            try { sessions.Activate(session); }
            catch { session.Dispose(); throw; }
        }
        finally
        {
            if (!transferred) CryptographicOperations.ZeroMemory(ownedKey);
        }
    }
}
