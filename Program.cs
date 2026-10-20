using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using StellarVaultPwa;
using StellarVaultPwa.Services;
using StellarVault.Core.Abstractions;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped<BrowserStorage>();
builder.Services.AddScoped<VaultSession>();
builder.Services.AddScoped<IVaultPreferences>(sp => sp.GetRequiredService<BrowserStorage>());
builder.Services.AddScoped<IVaultPlatformActions>(sp => sp.GetRequiredService<BrowserStorage>());
builder.Services.AddScoped<IVaultSessionStatus>(sp => sp.GetRequiredService<VaultSession>());
builder.Services.AddScoped<IVaultOperations>(sp => sp.GetRequiredService<VaultSession>());

await builder.Build().RunAsync();
