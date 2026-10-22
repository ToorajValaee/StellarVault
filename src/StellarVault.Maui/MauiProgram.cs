using Microsoft.Extensions.Logging;
using StellarVault.Core.Services;
using StellarVault.Core.Abstractions;

namespace StellarVault.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();
        // One application-owned session; never register an unlocked key as a scoped UI service.
        builder.Services.AddSingleton<VaultSessionManager>();
        builder.Services.AddSingleton<VaultNetworkSelection>();
        builder.Services.AddSingleton<IStellarLedgerRecords, UnavailableStellarLedgerRecords>();
        // Persist only public KDF parameters and AES-GCM-wrapped data keys.
        // The native UI remains gated until account ownership verification is available.
        builder.Services.AddSingleton<IVaultPassphraseCredentialStore>(_ =>
            new FileSystemVaultPassphraseCredentialStore(
                Path.Combine(FileSystem.AppDataDirectory, "vault-credentials")));
        builder.Services.AddSingleton<IVaultKeyProtector, NativeVaultKeyProtector>();
        builder.Services.AddSingleton<VaultPassphraseLifecycle>();
        builder.Services.AddSingleton<IVaultAccountAuthorizer, DenyVaultAccountAuthorizer>();
        builder.Services.AddSingleton<IVaultChunkStore>(_ =>
            new FileSystemVaultChunkStore(Path.Combine(FileSystem.AppDataDirectory, "vault-objects")));
        builder.Services.AddSingleton<IVaultRecordIndex>(_ =>
            new FileSystemVaultRecordIndex(Path.Combine(FileSystem.AppDataDirectory, "vault-index")));
        builder.Services.AddSingleton<VaultSessionWorkflow>();
        builder.Services.AddSingleton<VaultRecordWorkspace>();
        builder.Services.AddSingleton<VaultBinaryWorkspace>();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
