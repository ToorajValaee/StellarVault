namespace VaultEngine.Stellar.Services;

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

/// <summary>
/// Local-only queued create/inspect/discard adapter for the independent S-key
/// strategy. Implementations MUST use the existing authenticated queue store,
/// a separately unlocked S-key key and a strategy-isolated storage namespace.
/// No transaction submission or submission-state transition is exposed.
/// </summary>
public interface IVaultLinkedDraftStore : IVaultLinkedQueueSnapshotStore
{
    Task<bool> AddAsync(VaultPendingMutation mutation, CancellationToken ct = default);
    Task<bool> TryDeleteAsync(VaultPendingMutation expected, CancellationToken ct = default);
    Task<bool> TryReplaceAsync(VaultPendingMutation previous, VaultPendingMutation next,
        CancellationToken ct = default);
}
