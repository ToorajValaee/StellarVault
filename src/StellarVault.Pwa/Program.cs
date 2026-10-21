using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using StellarVault.Pwa;
using StellarVault.Pwa.Services;
using StellarVault.Core.Services;
using StellarVault.Core.Abstractions;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// New vault architecture only. No legacy HD-account store/session is injected.
builder.Services.AddScoped<VaultSessionManager>();
builder.Services.AddScoped(_ => new HttpClient());
// SWallet integration is planned. Never treat an unverified account address as ownership.
// PWA local-only encrypted storage: G-address is a namespace, NOT an ownership proof.
// SWallet integration can replace this with real challenge verification later.
builder.Services.AddScoped<IVaultAccountAuthorizer, LocalOnlyVaultNamespaceAuthorizer>();
builder.Services.AddScoped<VaultNetworkSelection>();
builder.Services.AddScoped<IVaultPassphraseCredentialStore, IndexedDbVaultPassphraseCredentialStore>();
builder.Services.AddScoped<VaultPassphraseLifecycle>();
builder.Services.AddScoped<IVaultChunkStore, IndexedDbVaultObjectStore>();
builder.Services.AddScoped<IVaultRecordIndex, IndexedDbVaultRecordIndex>();
builder.Services.AddScoped<VaultSessionWorkflow>();
builder.Services.AddScoped<VaultRecordWorkspace>();
builder.Services.AddScoped<VaultBinaryWorkspace>();

await builder.Build().RunAsync();
