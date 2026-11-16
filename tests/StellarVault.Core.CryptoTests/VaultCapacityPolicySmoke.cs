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

        // The encrypted queue persists full mutation metadata. A policy
        // must round-trip with its mutation and invalidate an older fee
        // approval if the same draft is later presented with a new policy.
        const string root = "GAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAWHF";
        var mutation = VaultPendingMutation.Create(root, "testnet",
            VaultMutationKind.CreateRecord, "0123456789abcdef", [1, 2, 3])
            with { CapacityPolicy = ObjectCapacityPolicy.SingleAccount };
        var serialized = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(mutation);
        var restored = System.Text.Json.JsonSerializer.Deserialize<VaultPendingMutation>(serialized)
            ?? throw new Exception("Encrypted queue mutation failed JSON roundtrip.");
        Check(restored.CapacityPolicy == ObjectCapacityPolicy.SingleAccount,
            "Queued single-account policy did not survive serialization.");
        var approval = VaultSyncApprovalGuard.Capture(
            restored, 0.001m, 1.0m, DateTimeOffset.UtcNow);
        VaultSyncApprovalGuard.RequireFresh(
            approval, restored, root, "testnet", DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5));
        var changed = restored with {
            CapacityPolicy = ObjectCapacityPolicy.ExpandWithExplicitApproval };
        try
        {
            VaultSyncApprovalGuard.RequireFresh(
                approval, changed, root, "testnet", DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(5));
            throw new Exception("A changed account-capacity policy reused an old quote.");
        }
        catch (InvalidOperationException) { }

        Console.WriteLine("PASS: durable capacity policy rejects expanding single-account creates");
        return Task.CompletedTask;
    }

    private static void Check(bool result, string message)
    {
        if (!result) throw new Exception(message);
    }
}
