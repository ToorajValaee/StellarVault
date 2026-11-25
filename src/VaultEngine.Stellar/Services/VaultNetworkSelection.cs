using System.Security.Cryptography;
using System.Text;

namespace VaultEngine.Stellar.Services;

/// <summary>Selected Stellar network and encrypted-credential namespace.
/// Custom networks are isolated by their exact network passphrase, not by URL.</summary>
public sealed class VaultNetworkSelection
{
    public string Network { get; private set; } = "testnet";
    public string? CustomPassphrase { get; private set; }
    public string SelectedPreset { get; private set; } = "testnet";
    public string CustomHorizonUrl { get; set; } = string.Empty;
    public void ChoosePreset(string preset)
    {
        if (preset is not ("testnet" or "public" or "custom"))
            throw new ArgumentException("Unsupported Stellar network.", nameof(preset));
        SelectedPreset = preset;
        if (preset != "custom") Select(preset);
    }

    public void Select(string network)
    {
        if (network is not ("testnet" or "public" or "custom"))
            throw new ArgumentException("Unsupported Stellar network.", nameof(network));
        if (network == "custom" && string.IsNullOrWhiteSpace(CustomPassphrase))
            throw new InvalidOperationException("Detect the custom network before opening the vault.");
        Network = network;
        SelectedPreset = network;
    }

    public void ConfigureCustom(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length > 512)
            throw new ArgumentException("Invalid custom network passphrase.", nameof(passphrase));
        CustomPassphrase = passphrase;
        Network = "custom";
        SelectedPreset = "custom";
    }

    public void ResetCustom()
    {
        CustomPassphrase = null;
        if (Network == "custom") Network = "testnet";
    }

    public string CredentialKey(string accountId)
    {
        StellarAccountAddress.Validate(accountId);
        if (Network == "testnet") return accountId; // Preserve current test vaults.
        if (Network == "public") return "public:" + accountId;
        if (Network != "custom" || string.IsNullOrWhiteSpace(CustomPassphrase))
            throw new InvalidOperationException("Custom network has not been verified.");
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(CustomPassphrase));
        try { return "custom:" + Convert.ToHexStringLower(digest) + ":" + accountId; }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }
}
