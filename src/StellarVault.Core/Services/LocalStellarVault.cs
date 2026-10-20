using StellarVault.Core.Abstractions;

namespace StellarVault.Core.Services;

/// <summary>Local encrypted storage composition root for a Stellar-account vault.
/// Only encrypted objects and opaque pointers are persisted in this directory.
/// The caller must select an application-private directory and securely manage the vault key.</summary>
public sealed class LocalStellarVault
{
    public IVaultChunkStore Objects { get; }
    public IVaultRecordIndex Pointers { get; }

    public LocalStellarVault(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        string root = Path.GetFullPath(rootDirectory);
        Objects = new FileSystemVaultChunkStore(Path.Combine(root, "objects"));
        Pointers = new FileSystemVaultRecordIndex(Path.Combine(root, "pointers"));
    }

    public StellarAccountVaultDatabase ForAccount(string stellarAccountId) =>
        new(Objects, Pointers, stellarAccountId);
}
