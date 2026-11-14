namespace StellarVault.Core.Services;

/// <summary>
/// Independent, in-memory opt-in for imported S-key local encrypted storage.
/// The general device-retention policy alone is NOT sufficient: opting into
/// mnemonic-HD local storage does not consent to S-key device persistence.
/// No consent is stored or inferred from existing encrypted credentials.
/// </summary>
public sealed class VaultLinkedLocalRetentionGate
{
    public bool AllowsLocalPersistence { get; private set; }

    public void GrantExplicitConsent() => AllowsLocalPersistence = true;

    public void RequireLocalPersistence()
    {
        if (!AllowsLocalPersistence)
            throw new InvalidOperationException(
                "Independent S-key encrypted local storage requires explicit consent for this session.");
    }

    public void Reset() => AllowsLocalPersistence = false;
}
