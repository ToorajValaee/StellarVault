namespace StellarVault.Core.Services;

/// <summary>
/// Strategy-specific namespace for imported S-key queue ciphertext.
/// This is separate from the HD queue network scope even when the two
/// strategies happen to refer to the same public Stellar G address.
/// </summary>
public static class VaultLinkedQueueStorageScope
{
    public const string Prefix = "secret-chain:queue:v1:";

    public static string FromNetworkScope(string networkScope)
    {
        if (string.IsNullOrWhiteSpace(networkScope) || networkScope.Length > 512 - Prefix.Length ||
            networkScope.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("Invalid linked queue network scope.", nameof(networkScope));
        return Prefix + networkScope;
    }
}

/// <summary>
/// Read-only view over an existing encrypted queue storage implementation,
/// bound to an independently unlocked imported S-key credential.
/// No mutation methods or publisher are exposed until protocol verification
/// and reviewed submission integration are completed.
/// </summary>
public interface IVaultLinkedQueueSnapshotStore
{
    Task<IReadOnlyList<VaultPendingMutation>> ListAsync(
        string accountId, string linkedNetworkScope, CancellationToken ct = default);
}
