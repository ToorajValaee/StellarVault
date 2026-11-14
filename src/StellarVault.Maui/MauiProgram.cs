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
        builder.Services.AddSingleton<NativeStartupStatus>();
        builder.Services.AddSingleton<IFileSaver>(FileSaver.Default);
        builder.Services.AddTransient<IVaultNativeFileAccess, MauiVaultNativeFileAccess>();
        // One application-owned session; never register an unlocked key as a scoped UI service.
        builder.Services.AddSingleton<VaultSessionManager>();
        builder.Services.AddSingleton<VaultNativeForegroundLifecycle>();
        builder.Services.AddSingleton<VaultNativeNavigationState>();
        builder.Services.AddSingleton<VaultNetworkSelection>();
        // BlazorWebView provides IJSRuntime within its component scope. Keep
        // the signing session, gate and upload services in that same scope.
        // Never retain a WebView JS runtime in an app-wide singleton.
        builder.Services.AddScoped<BrowserStellarLedgerRecords>();
        builder.Services.AddScoped<IStellarSecretKeyVaultDiscovery, BrowserSecretKeyVaultDiscovery>();
        builder.Services.AddScoped<IStellarSecretKeyLedger, BrowserSecretKeyLedger>();
        builder.Services.AddScoped<IStellarLedgerRecords>(
            services => services.GetRequiredService<BrowserStellarLedgerRecords>());
        builder.Services.AddScoped<IVaultLedgerManifestCopyInventory>(
            services => services.GetRequiredService<BrowserStellarLedgerRecords>());
        builder.Services.AddScoped<IVaultGuardedLedgerMutations>(
            services => services.GetRequiredService<BrowserStellarLedgerRecords>());
        builder.Services.AddScoped<IVaultPreparedCreateLedger>(
            services => services.GetRequiredService<BrowserStellarLedgerRecords>());
        builder.Services.AddScoped<IVaultPreparedUpdateLedger>(
            services => services.GetRequiredService<BrowserStellarLedgerRecords>());
        builder.Services.AddScoped<IVaultPreparedDeletionLedger>(
            services => services.GetRequiredService<BrowserStellarLedgerRecords>());
        builder.Services.AddScoped<VaultStorageModeGate>();
        // Hybrid queue mechanics are shared with PWA, but native account
        // authorization still fails closed until a signed challenge is enabled.
        // All queue payloads and metadata are encrypted with the unlocked
        // device vault key; no native plaintext queue files are written.
        builder.Services.AddScoped<VaultDeviceRetentionGate>();
        builder.Services.AddScoped<IVaultPendingMutationStore>(services =>
            new FileSystemVaultPendingMutationStore(
                services.GetRequiredService<VaultSessionManager>(),
                services.GetRequiredService<VaultDeviceRetentionGate>(),
                Path.Combine(FileSystem.AppDataDirectory, "vault-pending-queue")));
        // Historical evidence adapter shares the PWA implementation; only
        // recorded-hash create operations can ever be confirmed by Core CAS.
        builder.Services.AddScoped<HttpClient>();
        builder.Services.AddScoped<HorizonVaultTransactionInspector>();
        builder.Services.AddScoped<HorizonVaultDeletionInspector>();
        builder.Services.AddScoped<HorizonVaultDeletionPredecessorInspector>();
        builder.Services.AddScoped<IVaultDeletionTombstoneReader, HorizonVaultDeletionTombstoneReader>();
        builder.Services.AddScoped<VaultDeletionRecoveryReview>();
        builder.Services.AddScoped<VaultDeletionJournalReview>();
        builder.Services.AddScoped<HorizonVaultManifestInspector>();
        builder.Services.AddScoped<IVaultUnrecordedSubmissionReader, HorizonVaultUnrecordedSubmissionReader>();
        builder.Services.AddScoped<VaultUnrecordedSubmissionReview>();
        builder.Services.AddScoped<HorizonVaultCurrentChunkInspector>();
        builder.Services.AddScoped<HorizonVaultHistoricalChunkInspector>();
        builder.Services.AddScoped<HorizonVaultHistoricalRegistryInspector>();
        builder.Services.AddScoped<HorizonVaultNetworkInspector>();
        builder.Services.AddScoped<HorizonVaultPublicationDiagnostic>();
        builder.Services.AddScoped<IVaultLedgerEvidenceProvider, HorizonVaultQueuedMutationEvidenceProvider>();
        builder.Services.AddScoped<IVaultLedgerMutationVerifier, EvidenceBasedVaultLedgerMutationVerifier>();
        builder.Services.AddScoped<VaultPendingMutationQueue>();
        builder.Services.AddScoped<VaultManualSyncCoordinator>();
        builder.Services.AddScoped<VaultHybridSyncReviewService>();
        builder.Services.AddScoped<VaultHybridDraftWorkspace>();
        builder.Services.AddScoped<VaultHybridFileWorkspace>();
        builder.Services.AddScoped<VaultHybridFileDeletionWorkspace>();
        builder.Services.AddScoped<VaultHybridRemoteMutationWorkspace>();
        builder.Services.AddScoped<VaultConnectedStellarAccountInventory>();
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
        // Device-only local namespace, exactly as in PWA: NOT Stellar ownership.
        // Signing remains gated by the independent HD mnemonic ledger session.
        builder.Services.AddSingleton<IVaultAccountAuthorizer, LocalOnlyVaultNamespaceAuthorizer>();
        builder.Services.AddSingleton<IVaultChunkStore>(_ =>
            new FileSystemVaultChunkStore(Path.Combine(FileSystem.AppDataDirectory, "vault-objects")));
        builder.Services.AddSingleton<IVaultRecordIndex>(_ =>
            new FileSystemVaultRecordIndex(Path.Combine(FileSystem.AppDataDirectory, "vault-index")));
        builder.Services.AddSingleton<VaultSessionWorkflow>();
        builder.Services.AddSingleton<IVaultRecordTypeFactory, VaultRecordTypeFactory>();
        builder.Services.AddSingleton<VaultRecordWorkspace>();
        builder.Services.AddSingleton<VaultBinaryWorkspace>();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        // Debug logging provider is optional; do not require its separate package.
#endif
        return builder.Build();
    }
}
