using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using StellarVaultPwa;
using StellarVaultPwa.Services;
using StellarVault.Core.Services;
using StellarVault.Core.Abstractions;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// New vault architecture only. No legacy HD-account store/session is injected.
builder.Services.AddScoped<VaultSessionManager>();
builder.Services.AddScoped<IVaultAccountAuthorizer, DenyVaultAccountAuthorizer>();
// Adapter is available for future audited wallet integration, but the authorizer
// remains deny-by-default until signing and ledger verification are validated.
builder.Services.AddScoped<IStellarChallengeSigner, BrowserWalletChallengeSigner>();
builder.Services.AddScoped<IVaultPassphraseCredentialStore, IndexedDbVaultPassphraseCredentialStore>();
builder.Services.AddScoped<VaultPassphraseLifecycle>();
builder.Services.AddScoped<IVaultChunkStore, IndexedDbVaultObjectStore>();
builder.Services.AddScoped<IVaultRecordIndex, IndexedDbVaultRecordIndex>();
builder.Services.AddScoped<VaultSessionWorkflow>();
builder.Services.AddScoped<VaultRecordWorkspace>();
builder.Services.AddScoped<VaultBinaryWorkspace>();

await builder.Build().RunAsync();
