using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using StellarVault.Api.Security;
using VaultEngine.Stellar.Horizon;

internal static class TenantAuthSmoke
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        var networks = new ConfiguredNetworkCatalog([
            new("one", "https://one.example", "Private network one"),
            new("two", "https://two.example", "Private network two")]);
        var keyA = new string('a', 64);
        var keyB = new string('b', 64);
        static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var allowed = new[] { "one" };
        var profiles = new[] {
            new ApiTenantProfile("tenant-a", [Hash(keyA)], allowed),
            new ApiTenantProfile("tenant-b", [Hash(keyB)], ["two"]) };
        var directory = new ApiTenantDirectory(profiles, networks);
        allowed[0] = "two"; // Caller-owned configuration must not mutate authorization.
        var a = directory.Authenticate(keyA)!;
        var b = directory.Authenticate(keyB)!;
        Check(a.Identity?.IsAuthenticated == true && a.FindFirstValue(ClaimTypes.NameIdentifier) == "tenant-a", "Tenant identity lost");
        Check(a.HasClaim(ApiTenantDirectory.NetworkClaim, "one") && !a.HasClaim(ApiTenantDirectory.NetworkClaim, "two"), "Tenant A crossed network scope");
        Check(b.HasClaim(ApiTenantDirectory.NetworkClaim, "two") && !b.HasClaim(ApiTenantDirectory.NetworkClaim, "one"), "Tenant B crossed network scope");
        Check(directory.Authenticate(new string('c', 64)) is null && directory.Authenticate("short") is null, "Invalid key accepted");
        Check(new ApiTenantDirectory([], networks).Authenticate(keyA) is null, "Empty configuration allowed access");
        foreach (var invalid in new[] {
            new[] { profiles[0], profiles[0] },
            new[] { profiles[0] with { Networks = ["missing"] } },
            new[] { profiles[0] with { KeySha256 = ["raw-secret-key"] } },
            new[] { profiles[0], profiles[1] with { KeySha256 = profiles[0].KeySha256 } } })
        {
            var rejected = false;
            try { _ = new ApiTenantDirectory(invalid, networks); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid or ambiguous tenant configuration accepted");
        }
        Console.WriteLine("PASS: tenant key authentication, network isolation, configuration snapshots and invalid credentials.");
    }
}
