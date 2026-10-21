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
// Desktop Freighter signs SEP-53 messages; verify signature and CURRENT signer
// policy against the pinned Stellar Testnet Horizon before unlocking.
builder.Services.AddScoped<IStellarEd25519SignatureVerifier>(sp =>
    new Sep53StellarEd25519Verifier(new NsecStellarEd25519Verifier()));
builder.Services.AddScoped<IStellarOwnershipProofVerifier>(sp =>
    new StellarLedgerOwnershipVerifier(
        sp.GetRequiredService<HttpClient>(),
        sp.GetRequiredService<IStellarEd25519SignatureVerifier>(),
        new Uri("https://horizon-testnet.stellar.org"),
        "Test SDF Network ; September 2015"));
builder.Services.AddScoped<IVaultAccountAuthorizer>(sp =>
    new StellarChallengeAccountAuthorizer(
        sp.GetRequiredService<IStellarChallengeSigner>(),
        sp.GetRequiredService<IStellarOwnershipProofVerifier>(),
        "Test SDF Network ; September 2015"));
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
