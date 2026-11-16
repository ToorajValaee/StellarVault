using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;

namespace StellarVault.Api.Hosting;

public sealed class ApiHostingOptions
{
    public string[] TrustedProxies { get; init; } = [];
}

public sealed class ApiLimitOptions
{
    public int GlobalRequestsPerMinute { get; init; } = 600;
    public int TenantRequestsPerMinute { get; init; } = 60;
    public int GlobalConcurrency { get; init; } = 16;
    public int TenantConcurrency { get; init; } = 4;

    public void Validate()
    {
        if (GlobalRequestsPerMinute is < 1 or > 100000 || TenantRequestsPerMinute is < 1 or > 100000 ||
            TenantRequestsPerMinute > GlobalRequestsPerMinute || GlobalConcurrency is < 1 or > 64 ||
            TenantConcurrency < 1 || TenantConcurrency > GlobalConcurrency)
            throw new ArgumentException("API request/concurrency budgets are invalid.");
    }
}

public static class ApiHostPolicy
{
    public static ForwardedHeadersOptions? CreateForwardedOptions(ApiHostingOptions options)
    {
        if (options.TrustedProxies is null || options.TrustedProxies.Length > 16)
            throw new ArgumentException("Configure at most 16 explicit trusted proxy addresses.");
        // An empty ASP.NET trust list means trust everyone. Never install the
        // middleware when no proxies have been explicitly configured.
        if (options.TrustedProxies.Length == 0) return null;
        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1
        };
        forwarded.KnownIPNetworks.Clear();
        forwarded.KnownProxies.Clear();
        foreach (var value in options.TrustedProxies)
        {
            if (!IPAddress.TryParse(value, out var address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                throw new ArgumentException("Trusted proxies must be explicit IP addresses.");
            forwarded.KnownProxies.Add(address);
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                forwarded.KnownProxies.Add(address.MapToIPv6());
        }
        return forwarded;
    }

    public static void ValidateProductionConfiguration(IConfiguration configuration, bool development)
    {
        foreach (var key in new[] { "ASPNETCORE_FORWARDEDHEADERS_ENABLED", "DOTNET_FORWARDEDHEADERS_ENABLED" })
            if (string.Equals(Environment.GetEnvironmentVariable(key), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Automatic forwarded-header trust is forbidden; configure explicit trusted proxies.");
        if (configuration.GetValue<bool>("ForwardedHeaders_Enabled"))
            throw new InvalidOperationException("Automatic forwarded-header trust is forbidden.");
        if (!development)
        {
            var hosts = configuration["AllowedHosts"]?.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (hosts is null || hosts.Length == 0 || hosts.Any(h => h.Contains('*')))
                throw new InvalidOperationException("Production requires an explicit AllowedHosts list.");
        }
    }

    public static bool IsHealthProbe(HttpContext context) =>
        (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) &&
        (context.Request.Path == "/health/live" || context.Request.Path == "/health/ready") &&
        !context.Request.Headers.ContainsKey("Authorization");
}
