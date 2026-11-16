namespace VaultEngine.Stellar.Services;

/// <summary>
/// Short-lived authenticated queue encryption key access. Implementations must
/// bind the requested account and network to an independently authorized
/// unlocked session, serialize key use, and zero temporary key copies.
/// Consent is enforced by the queue stores, not granted by this interface.
/// </summary>
public interface IVaultQueueKeySession
{
    Task<T> ExecuteAsync<T>(string accountId, string networkId,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<T>> operation,
        CancellationToken ct = default);
}

/// <summary>
/// Backward-compatible key source for existing encrypted HD/local vault queues.
/// Future imported-S-key key providers must use separate authorization, key
/// derivation and storage namespaces; do not reuse this adapter for S-key data.
/// </summary>
public sealed class VaultLocalQueueKeySession(VaultSessionManager sessions) : IVaultQueueKeySession
{
    public Task<T> ExecuteAsync<T>(string accountId, string networkId,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        StellarAccountAddress.Validate(accountId);
        if (string.IsNullOrWhiteSpace(networkId) || networkId.Length > 512)
            throw new ArgumentException("Invalid encrypted queue network scope.", nameof(networkId));
        if (!sessions.IsUnlocked || sessions.ActiveAccountId != accountId)
            throw new UnauthorizedAccessException(
                "Unlock the matching local vault before accessing its encrypted queue.");
        return sessions.ExecuteAsync(async (_, key, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (sessions.ActiveAccountId != accountId)
                throw new UnauthorizedAccessException("Local vault changed during queue access.");
            return await operation(key, token);
        }, ct);
    }
}
