using Microsoft.JSInterop;
using StellarVault.Core.Services;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Read-only secret-key account discovery. It must not be confused with the HD
/// ledger writer or with a verified, decrypted record inventory.
/// </summary>
public sealed class BrowserSecretKeyVaultDiscovery(IJSRuntime js) : IStellarSecretKeyVaultDiscovery
{
    public async Task<VaultSecretChainInspection> InspectAsync(
        string secretKey, string horizonUrl, string networkPassphrase,
        CancellationToken cancellationToken = default)
    {
        var credential = new VaultAccessCredential(
            VaultCredentialMode.StellarSecretKeyChain, secretKey);
        credential.Validate();
        if (!Uri.TryCreate(horizonUrl, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 ||
            (uri.Scheme != Uri.UriSchemeHttps &&
             !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            throw new ArgumentException("A trusted Horizon URL is required.", nameof(horizonUrl));
        var result = await js.InvokeAsync<VaultSecretChainInspection>(
            "stellarVaultSecretChain.inspect", cancellationToken,
            secretKey, horizonUrl, networkPassphrase);
        if (result is null || !StellarAccountAddress.IsValid(result.RootAccountId) ||
            !StellarAccountAddress.IsValid(result.TailAccountId) ||
            result.Accounts is null || result.Accounts.Count is < 1 or > 64 ||
            result.Accounts[0].AccountId != result.RootAccountId ||
            result.Accounts[^1].AccountId != result.TailAccountId ||
            result.Accounts.Any(x => !x.RootCanSign || !StellarAccountAddress.IsValid(x.AccountId)) ||
            result.Accounts.Select(x => x.AccountId).Distinct(StringComparer.Ordinal).Count() != result.Accounts.Count ||
            result.NetworkPassphrase != networkPassphrase)
            throw new InvalidDataException("Secret-key account-chain inspection failed validation.");
        return result;
    }
}
