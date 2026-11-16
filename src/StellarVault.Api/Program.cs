using System.Security.Claims;
using StellarVault.Api.Hosting;
using VaultEngine.Core;
using Microsoft.AspNetCore.Authentication;
using StellarVault.Api.Security;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using VaultEngine.Stellar.Horizon;

// Container readiness probe. It has no external endpoint, key or signer access.
if (args is ["--healthcheck"])
{
    using var probe = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(2) };
    try
    {
        using var response = await probe.GetAsync("http://127.0.0.1:8080/health/ready", HttpCompletionOption.ResponseHeadersRead);
        Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1;
    }
    catch { Environment.ExitCode = 1; }
    return;
}
var builder = WebApplication.CreateBuilder(args);
ApiHostPolicy.ValidateProductionConfiguration(builder.Configuration, builder.Environment.IsDevelopment());
var forwardedOptions = ApiHostPolicy.CreateForwardedOptions(
    builder.Configuration.GetSection("ApiHosting").Get<ApiHostingOptions>() ?? new());
var budgets = builder.Configuration.GetSection("ApiLimits").Get<ApiLimitOptions>() ?? new();
budgets.Validate();
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 1024;
    options.Limits.MaxConcurrentConnections = 128;
    options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
    options.Limits.MaxRequestHeaderCount = 40;
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry)
            ? Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)) : 1;
        context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await Results.Problem(statusCode: 429, title: "API request budget exceeded").ExecuteAsync(context.HttpContext);
    };
    // Partition keys come only from authenticated configured identities; never
    // from arbitrary tenant headers. All partitions and waiting queues are bounded.
    static string Tenant(HttpContext c) => c.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
    var globalRate = PartitionedRateLimiter.Create<HttpContext, string>(c =>
        ApiHostPolicy.IsHealthProbe(c) ? RateLimitPartition.GetNoLimiter("health") :
        RateLimitPartition.GetFixedWindowLimiter("global", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = budgets.GlobalRequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    var globalConcurrent = PartitionedRateLimiter.Create<HttpContext, string>(c =>
        ApiHostPolicy.IsHealthProbe(c) ? RateLimitPartition.GetNoLimiter("health") :
        RateLimitPartition.GetConcurrencyLimiter("global", _ => new ConcurrencyLimiterOptions
        { PermitLimit = budgets.GlobalConcurrency, QueueLimit = 0 }));
    var tenantConcurrent = PartitionedRateLimiter.Create<HttpContext, string>(c =>
        ApiHostPolicy.IsHealthProbe(c) ? RateLimitPartition.GetNoLimiter("health") :
        RateLimitPartition.GetConcurrencyLimiter(Tenant(c), _ => new ConcurrencyLimiterOptions
        { PermitLimit = budgets.TenantConcurrency, QueueLimit = 0 }));
    options.GlobalLimiter = PartitionedRateLimiter.CreateChained(globalRate, globalConcurrent, tenantConcurrent);
    options.AddPolicy("tenant", c => RateLimitPartition.GetFixedWindowLimiter(Tenant(c), _ => new FixedWindowRateLimiterOptions
    { PermitLimit = budgets.TenantRequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
});var profiles = builder.Configuration.GetSection("Networks").Get<NetworkProfile[]>()
    ?? throw new InvalidOperationException("Configure Networks before starting the API.");
var networkCatalog = new ConfiguredNetworkCatalog(profiles);
builder.Services.AddSingleton<INetworkCatalog>(networkCatalog);
var tenantProfiles = builder.Configuration.GetSection("ApiAccess:Tenants").Get<ApiTenantProfile[]>() ?? [];
builder.Services.AddSingleton(new ApiTenantDirectory(tenantProfiles, networkCatalog));
builder.Services.AddAuthentication(ApiTenantDirectory.Scheme)
    .AddScheme<AuthenticationSchemeOptions, TenantApiKeyHandler>(ApiTenantDirectory.Scheme, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<InspectTransactionReceipt>();
builder.Services.AddScoped<ITransactionObservationProvider, StellarTransactionObservationProvider>();
builder.Services.AddScoped<ITransactionObservation>(services =>
    new TransactionObservationRouter(services.GetServices<ITransactionObservationProvider>()));
builder.Services.AddHttpClient<IReceiptGateway, HorizonReceiptGateway>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseCookies = false,
    MaxConnectionsPerServer = 8,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
});

var app = builder.Build();
if (forwardedOptions is not null) app.UseForwardedHeaders(forwardedOptions);
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        return Task.CompletedTask;
    });
    if (!app.Environment.IsDevelopment() && !context.Request.IsHttps && !ApiHostPolicy.IsHealthProbe(context))
    {
        await Results.Problem(statusCode: 400, title: "HTTPS is required").ExecuteAsync(context);
        return;
    }
    await next(context);
});
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();app.MapHealthChecks("/health/live");
app.MapGet("/health/ready", (ApiTenantDirectory tenants) => tenants.IsConfigured
    ? Results.Ok(new { ready = true })
    : Results.Problem(statusCode: 503, title: "API access is not configured"));
var api = app.MapGroup("/v1").RequireAuthorization().RequireRateLimiting("tenant");
api.MapGet("/networks", (INetworkCatalog catalog, HttpContext context) => Results.Ok(catalog.List().Where(n => context.User.HasClaim(ApiTenantDirectory.NetworkClaim, n.Id))));
api.MapGet("/capabilities", (INetworkCatalog catalog, HttpContext context) => Results.Ok(new
{
    apiVersion = "v1",
    engine = typeof(ITransactionObservation).Assembly.GetName().Name,
    releaseStatus = "preview",
    blockchain = "stellar",
    credentialStrategies = new[] { "mnemonic-hd", "secret-key-chain" },
    networks = catalog.List().Where(n => context.User.HasClaim(ApiTenantDirectory.NetworkClaim, n.Id)),
    operations = new[] { "transaction-receipt-inspection" },
    acceptsSigningKeys = false,
    acceptsPlaintext = false,
    storageWrites = false,
    transactionSubmission = false,
    mutationConfirmation = false
}));
api.MapGet("/networks/{network}/transactions/{hash}", async (
    string network, string hash, HttpContext context, ITransactionObservation inspect) =>
{
    try
    {
        var finding = await inspect.InspectAsync(new TransactionObservationScope("stellar", network), hash, context.RequestAborted);
        if (finding is null)
            return Results.Problem(statusCode: 404, title: "No usable successful receipt",
                detail: "Receipt may be absent, unsuccessful or invalid. This does not authorize retry.");
        return Results.Ok(new
        {
            network,
            networkId = finding.NetworkIdentity,
            transactionHash = finding.TransactionId,
            sourceAccount = finding.SourceAccount,
            ledger = finding.BlockNumber,
            observedAtUtc = finding.ObservedAtUtc,
            evidence = "endpoint-attested-successful-receipt",
            recordLinkedToTransaction = false,
            mutationConfirmed = false
        });
    }
    catch (ArgumentException)
    {
        return Results.Problem(statusCode: 400, title: "Invalid transaction hash");
    }
    catch (KeyNotFoundException)
    {
        return Results.Problem(statusCode: 404, title: "Network is not configured");
    }
    catch (NetworkIdentityException)
    {
        return Results.Problem(statusCode: 502, title: "Horizon network identity was not verified");
    }
    catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
    {
        return Results.Problem(statusCode: 504, title: "Horizon inspection timed out");
    }
    catch (HttpRequestException)
    {
        return Results.Problem(statusCode: 502, title: "Horizon inspection unavailable");
    }
}).AddEndpointFilter<TenantNetworkFilter>();
app.Run();

public partial class Program { }
