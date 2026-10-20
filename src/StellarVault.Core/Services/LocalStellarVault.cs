using StellarVault.Core.Abstractions;
using System.Security.Cryptography;

namespace StellarVault.Core.Services;

/// <summary>Local encrypted storage composition root for a Stellar-account vault.
/// Only encrypted objects and opaque pointers are persisted in this directory.
/// The caller must select an application-private directory and securely manage the vault key.</summary>
public sealed class LocalStellarVault
{
    public IVaultChunkStore Objects { get; }
    public IVaultRecordIndex Pointers { get; }
    public IVaultKeyEnvelopeStore KeyEnvelopes { get; }
    public VaultKeyLifecycle Keys { get; }

    public LocalStellarVault(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        string root = Path.GetFullPath(rootDirectory);
        Objects = new FileSystemVaultChunkStore(Path.Combine(root, "objects"));
        Pointers = new FileSystemVaultRecordIndex(Path.Combine(root, "pointers"));
        KeyEnvelopes = new FileSystemVaultKeyEnvelopeStore(Path.Combine(root, "keys"));
        Keys = new VaultKeyLifecycle(KeyEnvelopes);
    }

    /// <summary>Provision a new vault and return an unlocked, disposable session.
    /// The wrapping key must come from a vetted KDF or a platform keystore.</summary>
    public async Task<VaultUnlockedSession> CreateSessionAsync(string stellarAccountId,
        ReadOnlyMemory<byte> wrappingKey, CancellationToken cancellationToken = default)
    {
        byte[] key = await Keys.CreateAsync(stellarAccountId, wrappingKey, cancellationToken);
        try { return new VaultUnlockedSession(stellarAccountId, key, Objects, Pointers); }
        catch { CryptographicOperations.ZeroMemory(key); throw; }
    }

    /// <summary>Unlock an existing vault into a disposable session.</summary>
    public async Task<VaultUnlockedSession> UnlockSessionAsync(string stellarAccountId,
        ReadOnlyMemory<byte> wrappingKey, CancellationToken cancellationToken = default)
    {
        byte[] key = await Keys.UnlockAsync(stellarAccountId, wrappingKey, cancellationToken);
        try { return new VaultUnlockedSession(stellarAccountId, key, Objects, Pointers); }
        catch { CryptographicOperations.ZeroMemory(key); throw; }
    }

    public StellarAccountVaultDatabase ForAccount(string stellarAccountId) =>
        new(Objects, Pointers, stellarAccountId);
}
