namespace StellarVault.Core.Services;

/// <summary>
/// Explicitly local-only vault mode. A Stellar public address selects an isolated
/// local encrypted namespace, but its ownership is NOT established. This mode
/// must never be used for ledger operations or represented as wallet login.
/// </summary>
public sealed class LocalOnlyVaultNamespaceAuthorizer : IVaultAccountAuthorizer
{
    public Task VerifyOwnershipAsync(string accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StellarAccountAddress.Validate(accountId);
        return Task.CompletedTask;
    }
}
