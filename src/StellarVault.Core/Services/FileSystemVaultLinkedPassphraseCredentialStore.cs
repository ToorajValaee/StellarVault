using System.Security.Cryptography;
using System.Text;

namespace StellarVault.Core.Services;

/// <summary>
/// Native S-key queue credential adapter reusing the existing atomic wrapped
/// credential store. Each exact account/network scope uses its own hashed
/// directory beneath a dedicated secret-chain credential root.
/// This directory is NEVER the HD/local vault credential directory.
/// </summary>
public sealed class FileSystemVaultLinkedPassphraseCredentialStore(
    string baseDirectory,
    VaultNetworkSelection network,
    VaultLinkedLocalRetentionGate linkedRetention) : IVaultLinkedPassphraseCredentialStore
{
    private FileSystemVaultPassphraseCredentialStore Scoped(string accountId)
    {
        linkedRetention.DeviceRetention.RequireLocalPersistence();
        linkedRetention.RequireLocalPersistence();
        StellarAccountAddress.Validate(accountId);
        var scope = "secret-chain:queue-key:v1:" + network.CredentialKey(accountId);
        var fingerprint = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        return new FileSystemVaultPassphraseCredentialStore(
            Path.Combine(Path.GetFullPath(baseDirectory), fingerprint));
    }

    public Task<byte[]?> ReadAsync(string accountId, CancellationToken ct = default) =>
        Scoped(accountId).ReadAsync(accountId, ct);

    public Task<bool> CreateAsync(string accountId, ReadOnlyMemory<byte> record,
        CancellationToken ct = default) =>
        Scoped(accountId).CreateAsync(accountId, record, ct);

    public Task<bool> ReplaceAsync(string accountId, ReadOnlyMemory<byte> expected,
        ReadOnlyMemory<byte> replacement, CancellationToken ct = default) =>
        Scoped(accountId).ReplaceAsync(accountId, expected, replacement, ct);
}
