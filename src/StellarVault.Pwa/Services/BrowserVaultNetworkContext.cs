namespace StellarVault.Pwa.Services;

/// <summary>
/// Scoped network selection for local browser credential storage.
/// Testnet retains the original unprefixed key for pre-release compatibility.
/// Public network credentials use a distinct namespace and independently
/// generated data key; network changes are forbidden while a vault is open.
/// </summary>
public sealed class BrowserVaultNetworkContext
{
    public string Network { get; private set; } = "testnet";

    public void Select(string network)
    {
        if (network is not ("testnet" or "public"))
            throw new ArgumentException("Unsupported Stellar network.", nameof(network));
        Network = network;
    }

    public string CredentialKey(string accountId) =>
        Network == "testnet" ? accountId : "public:" + accountId;
}
