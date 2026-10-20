using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using StellarVaultPwa;
using StellarVaultPwa.Services;
using StellarVault.Core.Abstractions;
using StellarVault.Core.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped<BrowserStorage>();
builder.Services.AddScoped<VaultSession>();
// V2 session is intentionally isolated from the legacy browser vault.
// No credential writer or permissive authorizer is registered for the PWA preview.
builder.Services.AddScoped<VaultSessionManager>();
builder.Services.AddScoped<IVaultAccountAuthorizer, DenyVaultAccountAuthorizer>();
builder.Services.AddScoped<IVaultPreferences>(sp => sp.GetRequiredService<BrowserStorage>());
builder.Services.AddScoped<IVaultPlatformActions>(sp => sp.GetRequiredService<BrowserStorage>());
builder.Services.AddScoped<IVaultClientServices>(sp => sp.GetRequiredService<BrowserStorage>());
builder.Services.AddScoped<IVaultSessionStatus>(sp => sp.GetRequiredService<VaultSession>());
builder.Services.AddScoped<IVaultOperations>(sp => sp.GetRequiredService<VaultSession>());

await builder.Build().RunAsync();
