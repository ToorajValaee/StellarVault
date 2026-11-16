using VaultEngine.Stellar.Services;
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
        Check(!VaultCapacityPolicyGuard.AllowsCreate(
            ObjectCapacityPolicy.SingleAccount, requiresCapacityExpansion: true),
            "An unspecified capacity choice must never allow account expansion.");

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
        Check(restored.OriginStrategyId == VaultMutationStrategyPolicy.Hd,
            "Queued mutation must include an explicit HD origin strategy.");
        var originalJson = System.Text.Encoding.UTF8.GetString(serialized);
        var noOrigin = originalJson.Replace(
            ",\"OriginStrategyId\":\"stellar-hd-v2\"", "", StringComparison.Ordinal);
        if (noOrigin == originalJson)
            throw new Exception("Origin field was unexpectedly absent from serialized mutation.");
        try
        {
            _ = System.Text.Json.JsonSerializer.Deserialize<VaultPendingMutation>(noOrigin);
            throw new Exception("A mutation without an explicit account strategy was accepted.");
        }
        catch (System.Text.Json.JsonException) { }
        try
        {
            VaultMutationStrategyPolicy.Resolve(null);
            throw new Exception("Missing mutation strategy silently defaulted to HD.");
        }
        catch (InvalidDataException) { }

        // No backwards-compatibility path: old queue entries missing the
        // authenticated policy are rejected during JSON reconstruction.
        var unversioned = originalJson
            .Replace(",\"CapacityPolicy\":0", "", StringComparison.Ordinal);
        if (unversioned == originalJson)
            throw new Exception("Capacity policy unexpectedly absent from serialized mutation.");
        try
        {
            _ = System.Text.Json.JsonSerializer.Deserialize<VaultPendingMutation>(unversioned);
            throw new Exception("An unversioned queue entry without policy was accepted.");
        }
        catch (System.Text.Json.JsonException) { }

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
