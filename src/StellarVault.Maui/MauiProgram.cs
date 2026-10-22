using Microsoft.Extensions.Logging;
using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Storage;
using StellarVault.Core.Services;
using StellarVault.Core.Abstractions;
using StellarVault.Pwa.Services;

namespace StellarVault.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>().UseMauiCommunityToolkit();
        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSingleton<IFileSaver>(FileSaver.Default);
        // One application-owned session; never register an unlocked key as a scoped UI service.
        builder.Services.AddSingleton<VaultSessionManager>();
        builder.Services.AddSingleton<VaultNetworkSelection>();
        // BlazorWebView provides IJSRuntime within its component scope. Keep
        // the signing session, gate and upload services in that same scope.
        // Never retain a WebView JS runtime in an app-wide singleton.
        builder.Services.AddScoped<IStellarLedgerRecords, BrowserStellarLedgerRecords>();
        builder.Services.AddScoped<VaultStorageModeGate>();
        builder.Services.AddScoped<VaultLedgerRecordWorkspace>();
        builder.Services.AddScoped<StellarLedgerUploadCoordinator>();
        // Upload checkpoints hold stable IDs and fingerprints, never file payloads.
        // App-private filesystem storage is not cryptographically encrypted.
        builder.Services.AddSingleton<IStellarUploadCheckpointStore>(services =>
            new FileSystemStellarUploadCheckpointStore(
                Path.Combine(FileSystem.AppDataDirectory, "vault-upload-checkpoints"),
                services.GetRequiredService<VaultNetworkSelection>()));
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
        // Debug logging provider is optional; do not require its separate package.
#endif
        return builder.Build();
    }
}
