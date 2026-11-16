namespace VaultEngine.Stellar.Services;

/// <summary>
/// Read-only account identity and size of a currently unlocked Stellar vault.
/// This is not a signer proof, complete chain traversal or permission to publish.
/// </summary>
public sealed record VaultConnectedAccountSummary(
    VaultCredentialMode CredentialMode,
    string RootAccountId,
    string TailAccountId,
    int AccountCount);

/// <summary>
/// The UI selects its credential mode explicitly; the two independent ledger
/// sessions are never tried in fallback order. Never transports secret material.
/// </summary>
public sealed class VaultConnectedStellarAccountInventory(
    IStellarLedgerRecords hd,
    IStellarSecretKeyLedger linked)
{
    public async Task<VaultConnectedAccountSummary> InspectAsync(
        VaultCredentialMode mode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LedgerAccountInfo info;
        switch (mode)
        {
            case VaultCredentialMode.MnemonicHd:
                if (!hd.IsConnected)
                    throw new InvalidOperationException("The mnemonic HD vault is not connected.");
                info = await hd.InspectAccountInfoAsync();
                break;
            case VaultCredentialMode.StellarSecretKeyChain:
                if (!linked.IsConnected)
                    throw new InvalidOperationException("The linked secret-key vault is not connected.");
                info = await linked.InspectAccountInfoAsync(cancellationToken);
                if (!string.Equals(linked.RootAccountId, info.RootAccountId, StringComparison.Ordinal))
                    throw new InvalidDataException("Connected secret-key root identity changed.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), "Unknown account strategy.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!StellarAccountAddress.IsValid(info.RootAccountId) ||
            !StellarAccountAddress.IsValid(info.TailAccountId) ||
            info.TotalAccounts is < 1 or > 64 ||
            (info.TotalAccounts == 1 && info.RootAccountId != info.TailAccountId))
            throw new InvalidDataException("Connected Stellar account inventory is inconsistent.");

        if (mode == VaultCredentialMode.MnemonicHd && !hd.IsConnected ||
            mode == VaultCredentialMode.StellarSecretKeyChain && !linked.IsConnected)
            throw new InvalidOperationException("The selected account session disconnected.");

        return new VaultConnectedAccountSummary(
            mode, info.RootAccountId, info.TailAccountId, info.TotalAccounts);
    }
}
