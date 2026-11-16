using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using VaultEngine.Stellar.Horizon;

namespace StellarVault.Api.Security;

public sealed record ApiTenantProfile(string Id, string[] KeySha256, string[] Networks);

/// <summary>Hosted access is separate from Stellar account ownership or signing authority.</summary>
public sealed class ApiTenantDirectory
{
    public const string Scheme = "TenantApiKey";
    public const string NetworkClaim = "stellarvault:network";
    private sealed record Entry(string Id, byte[][] Hashes, string[] Networks);
    private readonly Entry[] entries;
    public bool IsConfigured => entries.Length != 0;

    public ApiTenantDirectory(IEnumerable<ApiTenantProfile> profiles, INetworkCatalog networks)
    {
        var configured = profiles.ToArray();
        if (configured.Length > 256) throw new ArgumentException("At most 256 API tenants may be configured.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var known = networks.List().Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var result = new List<Entry>();
        foreach (var profile in configured)
        {
            if (profile is null || string.IsNullOrEmpty(profile.Id) || profile.Id.Length > 64 ||
                !profile.Id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') ||
                !ids.Add(profile.Id) || profile.KeySha256 is not { Length: >= 1 and <= 4 } ||
                profile.Networks is not { Length: >= 1 and <= 128 } ||
                profile.Networks.Any(id => id is null || !known.Contains(id)) ||
                profile.Networks.Distinct(StringComparer.Ordinal).Count() != profile.Networks.Length)
                throw new ArgumentException("API tenant configuration is invalid.");
            var keyHashes = new List<byte[]>();
            foreach (var hash in profile.KeySha256)
            {
                if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit) || !hashes.Add(hash))
                    throw new ArgumentException("API key digests must be unique SHA-256 hex values.");
                keyHashes.Add(Convert.FromHexString(hash));
            }
            result.Add(new(profile.Id, keyHashes.ToArray(), profile.Networks.ToArray()));
        }
        entries = result.ToArray();
    }

    public ClaimsPrincipal? Authenticate(string token)
    {
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) return null;
        var bytes = Encoding.UTF8.GetBytes(token);
        var digest = SHA256.HashData(bytes);
        Entry? matching = null;
        try
        {
            // Compare every configured key using constant-time digest comparisons.
            foreach (var entry in entries)
                foreach (var hash in entry.Hashes)
                    if (CryptographicOperations.FixedTimeEquals(hash, digest)) matching = entry;
            if (matching is null) return null;
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, matching.Id) };
            claims.AddRange(matching.Networks.Select(id => new Claim(NetworkClaim, id)));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(digest);
        }
    }
}
