namespace VaultEngine.Stellar.Services;

/// <summary>
/// Authoritative workspace selection uses only authenticated host sessions,
/// never guesses based on record contents or saved local preferences.
/// Explicitly refuse simultaneous independent HD/S-key sessions.
/// </summary>
public enum VaultWorkspaceMode
{
    Locked,
    MnemonicHd,
    StellarSecretKey,
    ConflictingSessions
}

public static class VaultWorkspaceModeResolver
{
    public static VaultWorkspaceMode Resolve(bool hdLocalUnlocked,
        bool hdLedgerConnected, bool secretKeyLedgerConnected)
    {
        if (secretKeyLedgerConnected && (hdLocalUnlocked || hdLedgerConnected))
            return VaultWorkspaceMode.ConflictingSessions;
        if (secretKeyLedgerConnected) return VaultWorkspaceMode.StellarSecretKey;
        if (hdLocalUnlocked || hdLedgerConnected) return VaultWorkspaceMode.MnemonicHd;
        return VaultWorkspaceMode.Locked;
    }
}
