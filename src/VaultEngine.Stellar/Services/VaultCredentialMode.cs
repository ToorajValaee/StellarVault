namespace VaultEngine.Stellar.Services;

/// <summary>
/// Vault credential selection is explicit. Never infer a derivation scheme
/// by trying one parser and silently falling back to another.
/// </summary>
public enum VaultCredentialMode
{
    MnemonicHd = 1,
    StellarSecretKeyChain = 2
}

/// <summary>In-memory input only. Never serialize, log or persist a credential.</summary>
public sealed record VaultAccessCredential(VaultCredentialMode Mode, string Secret)
{
    public void Validate()
    {
        if (Mode is not (VaultCredentialMode.MnemonicHd or VaultCredentialMode.StellarSecretKeyChain))
            throw new ArgumentOutOfRangeException(nameof(Mode), "Unsupported vault credential mode.");
        if (string.IsNullOrWhiteSpace(Secret))
            throw new ArgumentException("Vault credential is required.", nameof(Secret));
        if (Mode == VaultCredentialMode.StellarSecretKeyChain &&
            (Secret.Length != 56 || Secret[0] != 'S' ||
             Secret.Any(c => !((c >= 'A' && c <= 'Z') || (c >= '2' && c <= '7')))))
            throw new ArgumentException("Expected a 56-character Stellar S-key. Its checksum must also be verified.", nameof(Secret));
    }
}

/// <summary>
/// Read-only observation of a linked secret-key vault. A verified link requires
/// a current authorized root signer and sufficient signer weight; it is not a
/// transaction receipt or a guarantee of recoverable encrypted data.
/// </summary>
public sealed record VaultSecretChainAccount(
    string AccountId, int Position, bool RootCanSign, int RootSignerWeight,
    int MediumThreshold, int HighThreshold, int DataEntries);

public sealed record VaultSecretChainInspection(
    string RootAccountId, string NetworkPassphrase, string TailAccountId,
    IReadOnlyList<VaultSecretChainAccount> Accounts, bool HasChain, string Note);

public interface IStellarSecretKeyVaultDiscovery
{
    Task<VaultSecretChainInspection> InspectAsync(
        string secretKey, string horizonUrl, string networkPassphrase,
        CancellationToken cancellationToken = default);
}
