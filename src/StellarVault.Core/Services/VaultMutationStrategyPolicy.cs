namespace StellarVault.Core.Services;

/// <summary>
/// Explicit account-strategy discriminator in every encrypted mutation.
/// The product has not launched: missing or unknown strategy IDs are invalid,
/// never guessed as HD or imported S-key.
/// </summary>
public static class VaultMutationStrategyPolicy
{
    public const string Hd = "stellar-hd-v2";
    public const string Linked = "stellar-secret-key-chain";

    public static string Resolve(string? originStrategyId) => originStrategyId switch
    {
        Hd => Hd,
        Linked => Linked,
        _ => throw new InvalidDataException(
            "Encrypted mutation requires an explicit recognized account strategy.")
    };

    public static void RequireHd(VaultPendingMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (Resolve(mutation.OriginStrategyId) != Hd)
            throw new InvalidOperationException(
                "Independent S-key draft cannot be approved through the mnemonic-HD transport.");
    }

    public static void Validate(string? originStrategyId) => _ = Resolve(originStrategyId);
}
