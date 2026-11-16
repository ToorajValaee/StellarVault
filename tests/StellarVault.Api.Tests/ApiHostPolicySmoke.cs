using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StellarVault.Api.Hosting;

internal static class ApiHostPolicySmoke
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; }
        throw new Exception("Unsafe API hosting configuration was accepted");
    }
    public static async Task RunAsync()
    {
        Check(ApiHostPolicy.CreateForwardedOptions(new()) is null, "Empty proxy configuration enabled trust-all forwarding");
        Reject(() => ApiHostPolicy.CreateForwardedOptions(new() { TrustedProxies = ["0.0.0.0"] }));
        Reject(() => ApiHostPolicy.CreateForwardedOptions(new() { TrustedProxies = ["*"] }));
        Reject(() => new ApiLimitOptions { TenantConcurrency = 99 }.Validate());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "*" }).Build();
        Reject(() => ApiHostPolicy.ValidateProductionConfiguration(config, false));
        var options = ApiHostPolicy.CreateForwardedOptions(new() { TrustedProxies = ["127.0.0.1"] })!;
        async Task<bool> Forward(string remote)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
            context.Request.Scheme = "http";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            var secure = false;
            var middleware = new ForwardedHeadersMiddleware(c => { secure = c.Request.IsHttps; return Task.CompletedTask; },
                NullLoggerFactory.Instance, Options.Create(options));
            await middleware.Invoke(context);
            return secure;
        }
        Check(!await Forward("10.0.0.99"), "Untrusted caller forged HTTPS forwarding");
        Check(await Forward("127.0.0.1"), "Configured proxy could not forward HTTPS");
        var probe = new DefaultHttpContext();
        probe.Request.Method = "GET";
        probe.Request.Path = "/health/ready";
        Check(ApiHostPolicy.IsHealthProbe(probe), "Readiness probe was rate-limited as work");
        probe.Request.Headers.Authorization = "Bearer anything";
        Check(!ApiHostPolicy.IsHealthProbe(probe), "Authenticated work bypassed HTTPS/rate guards as a probe");
        Console.WriteLine("PASS: proxy trust, spoofed forwarding rejection, host validation and bounded API policies.");
    }
}
