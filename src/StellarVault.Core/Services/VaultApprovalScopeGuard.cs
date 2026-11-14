namespace StellarVault.Core.Services;

/// <summary>
/// Account-strategy-neutral approval scope and quote lifetime rules.
/// The caller still owns transport-specific quote verification, ledger
/// preflight, signing and a durable submission reservation where required.
/// No signing or queue storage is performed here.
/// </summary>
public static class VaultApprovalScopeGuard
{
    public static void RequireFresh(
        string quotedAccountId, string quotedNetworkScope,
        string currentAccountId, string currentNetworkScope,
        DateTimeOffset quotedUtc, DateTimeOffset now, TimeSpan maxAge)
    {
        StellarAccountAddress.Validate(quotedAccountId);
        StellarAccountAddress.Validate(currentAccountId);
        if (string.IsNullOrWhiteSpace(quotedNetworkScope) ||
            string.IsNullOrWhiteSpace(currentNetworkScope) ||
            !string.Equals(quotedAccountId, currentAccountId, StringComparison.Ordinal) ||
            !string.Equals(quotedNetworkScope, currentNetworkScope, StringComparison.Ordinal))
            throw new InvalidOperationException("Vault account or network changed after review.");

        if (maxAge <= TimeSpan.Zero || quotedUtc > now ||
            now - quotedUtc > maxAge)
            throw new InvalidOperationException("Transaction review expired. Review the current costs again.");
    }
}
