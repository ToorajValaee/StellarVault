using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace StellarVault.Api.Security;

public sealed class TenantApiKeyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, ApiTenantDirectory tenants)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var values = Request.Headers.Authorization;
        if (values.Count == 0) return Task.FromResult(AuthenticateResult.NoResult());
        if (values.Count != 1 || values[0] is not { } header || header.Length != 71 ||
            !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.Fail("Invalid API credentials."));
        var principal = tenants.Authenticate(header[7..]);
        return Task.FromResult(principal is null
            ? AuthenticateResult.Fail("Invalid API credentials.")
            : AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}

public sealed class TenantNetworkFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var network = context.HttpContext.Request.RouteValues["network"]?.ToString();
        if (network is null || !context.HttpContext.User.HasClaim(ApiTenantDirectory.NetworkClaim, network))
            return ValueTask.FromResult<object?>(Results.Problem(statusCode: 404, title: "Network is not available"));
        return next(context);
    }
}
