namespace StellarVault.Core.Services;

/// <summary>
/// Product scope policy: offline/hybrid file mutations are unavailable until a
/// separately approved implementation is shipped. Keep this independent of the
/// direct on-chain SVFILE protocol, which remains supported.
///
/// Do not replace this with a user preference: an unsupported hybrid file
/// transaction must not be enabled by a UI toggle or local storage setting.
/// </summary>
public static class VaultHybridFilePolicy
{
    public static bool Enabled => false;

    public static void RequireEnabled()
    {
        if (!Enabled)
            throw new NotSupportedException(
                "Hybrid file operations are disabled. Use direct on-chain file storage instead.");
    }
}
