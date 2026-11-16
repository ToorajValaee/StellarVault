using VaultEngine.Core;

namespace StellarVault.Core.Services;

/// <summary>
/// Pure, provider-adapter-neutral capacity decision used by the original HD
/// review and final publisher. A quote indicating more accounts are needed
/// must never satisfy an explicit SingleAccount mutation.
///
/// Null is a pre-engine legacy queued mutation, whose existing explicitly
/// reviewed publication behavior is retained for compatibility.
/// </summary>
public static class VaultCapacityPolicyGuard
{
    public static bool AllowsCreate(ObjectCapacityPolicy? policy,
        bool requiresCapacityExpansion)
    {
        if (policy is { } selected && !Enum.IsDefined(selected))
            throw new InvalidDataException("Unknown durable account capacity policy.");
        return policy != ObjectCapacityPolicy.SingleAccount ||
            !requiresCapacityExpansion;
    }

    public static void RequireCreateAllowed(ObjectCapacityPolicy? policy,
        bool requiresCapacityExpansion)
    {
        if (!AllowsCreate(policy, requiresCapacityExpansion))
            throw new VaultAccountCapacityRequiredException();
    }
}
