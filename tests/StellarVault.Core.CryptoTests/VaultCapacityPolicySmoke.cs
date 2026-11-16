using StellarVault.Core.Services;
using VaultEngine.Core;

internal static class VaultCapacityPolicySmoke
{
    public static Task RunAsync()
    {
        Check(VaultCapacityPolicyGuard.AllowsCreate(
            ObjectCapacityPolicy.SingleAccount, requiresCapacityExpansion: false),
            "A normal single-account create was rejected.");
        Check(!VaultCapacityPolicyGuard.AllowsCreate(
            ObjectCapacityPolicy.SingleAccount, requiresCapacityExpansion: true),
            "Single-account capacity unexpectedly allowed a new account.");
        Check(VaultCapacityPolicyGuard.AllowsCreate(
            ObjectCapacityPolicy.ExpandWithExplicitApproval, requiresCapacityExpansion: true),
            "Explicit-expansion policy was mistaken for single-account mode.");
        Check(VaultCapacityPolicyGuard.AllowsCreate(
            null, requiresCapacityExpansion: true),
            "Legacy queued mutations unexpectedly changed policy.");

        try
        {
            VaultCapacityPolicyGuard.RequireCreateAllowed(
                ObjectCapacityPolicy.SingleAccount, requiresCapacityExpansion: true);
            throw new Exception("Single-account policy permitted an expanding create.");
        }
        catch (VaultAccountCapacityRequiredException) { }

        VaultCapacityPolicyGuard.RequireCreateAllowed(
            ObjectCapacityPolicy.SingleAccount, requiresCapacityExpansion: false);

        try
        {
            VaultCapacityPolicyGuard.AllowsCreate(
                (ObjectCapacityPolicy)999, requiresCapacityExpansion: false);
            throw new Exception("Unknown persisted capacity policy was accepted.");
        }
        catch (InvalidDataException) { }

        Console.WriteLine("PASS: durable capacity policy rejects expanding single-account creates");
        return Task.CompletedTask;
    }

    private static void Check(bool result, string message)
    {
        if (!result) throw new Exception(message);
    }
}
