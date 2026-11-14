namespace StellarVault.Core.Services;

/// <summary>
/// Authenticated-at-rest *format discriminator*, not proof that a signer owns a
/// queue entry. Null is reserved for historical HD entries; it MUST NOT be
/// interpreted as an imported secret-key draft.
/// </summary>
public static class VaultMutationStrategyPolicy
{
    public const string Hd = "stellar-hd-v2";
    public const string Linked = "stellar-secret-key-chain";

    public static string Resolve(string? originStrategyId) => originStrategyId switch
    {
        null or Hd => Hd,
        Linked => Linked,
        _ => throw new InvalidDataException("Unknown encrypted mutation origin strategy.")
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
