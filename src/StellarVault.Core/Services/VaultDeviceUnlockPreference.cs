namespace StellarVault.Core.Services;

/// <summary>
/// Ephemeral preference for offering optional device-passkey enrollment after
/// encrypted local storage has actually been unlocked. Never persists or holds
/// a passphrase, signing key, mnemonic, or WebAuthn credential.
/// The two credential strategies consume this intent independently.
/// </summary>
public sealed class VaultDeviceUnlockPreference
{
    public bool Requested { get; set; }
    public void Reset() => Requested = false;
}
