namespace StellarVault.Core.Services;

/// <summary>UI-visible network selection, scoped to a browser vault session.
/// A native host may use its own registration and policy.</summary>
public sealed class VaultNetworkSelection
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
