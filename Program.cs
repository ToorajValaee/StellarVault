using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using StellarVaultPwa;
using StellarVault.Core.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// New vault architecture only. No legacy HD-account store/session is injected.
builder.Services.AddScoped<VaultSessionManager>();
builder.Services.AddScoped<IVaultAccountAuthorizer, DenyVaultAccountAuthorizer>();

await builder.Build().RunAsync();
