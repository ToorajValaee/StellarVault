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
builder.Services.AddScoped<VaultPrivacyGate>();
builder.Services.AddScoped(_ => new HttpClient());
// SWallet integration is planned. Never treat an unverified account address as ownership.
// PWA local-only encrypted storage: G-address is a namespace, NOT an ownership proof.
// SWallet integration can replace this with real challenge verification later.
builder.Services.AddScoped<IVaultAccountAuthorizer, LocalOnlyVaultNamespaceAuthorizer>();
builder.Services.AddScoped<VaultNetworkSelection>();
builder.Services.AddScoped<BrowserStellarLedgerRecords>();
builder.Services.AddScoped<IStellarSecretKeyVaultDiscovery, BrowserSecretKeyVaultDiscovery>();
builder.Services.AddScoped<IStellarSecretKeyLedger, BrowserSecretKeyLedger>();
builder.Services.AddScoped<IStellarLedgerRecords>(sp => sp.GetRequiredService<BrowserStellarLedgerRecords>());
builder.Services.AddScoped<IVaultLedgerManifestCopyInventory>(sp => sp.GetRequiredService<BrowserStellarLedgerRecords>());
builder.Services.AddScoped<IVaultGuardedLedgerMutations>(
    sp => sp.GetRequiredService<BrowserStellarLedgerRecords>());
builder.Services.AddScoped<IVaultPreparedCreateLedger>(
    sp => sp.GetRequiredService<BrowserStellarLedgerRecords>());
builder.Services.AddScoped<IVaultPreparedUpdateLedger>(
    sp => sp.GetRequiredService<BrowserStellarLedgerRecords>());
builder.Services.AddScoped<IVaultPreparedDeletionLedger>(
    sp => sp.GetRequiredService<BrowserStellarLedgerRecords>());
builder.Services.AddScoped<VaultStorageModeGate>();
builder.Services.AddScoped<VaultDeviceRetentionGate>();
builder.Services.AddScoped<IVaultPendingMutationStore, IndexedDbVaultPendingMutationStore>();
builder.Services.AddScoped<IVaultLedgerEvidenceProvider, HorizonVaultQueuedMutationEvidenceProvider>();
builder.Services.AddScoped<IVaultLedgerMutationVerifier, EvidenceBasedVaultLedgerMutationVerifier>();
builder.Services.AddScoped<VaultPendingMutationQueue>();
builder.Services.AddScoped<VaultManualSyncCoordinator>();
builder.Services.AddScoped<VaultHybridSyncReviewService>();
builder.Services.AddScoped<VaultHybridDraftWorkspace>();
builder.Services.AddScoped<VaultHybridFileWorkspace>();
builder.Services.AddScoped<VaultHybridFileDeletionWorkspace>();
builder.Services.AddTransient<IVaultNativeFileAccess, BrowserVaultNativeFileAccess>();
builder.Services.AddScoped<VaultHybridRemoteMutationWorkspace>();
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
builder.Services.AddScoped<VaultQueuedCreateHistoricalReview>();
builder.Services.AddScoped<VaultUnrecordedSubmissionDiagnostic>();
builder.Services.AddScoped<VaultConnectedStellarAccountInventory>();
builder.Services.AddScoped<IVaultAccountProvisioningStrategy, StellarHdAccountStrategy>();
builder.Services.AddScoped<IVaultAccountProvisioningStrategy, StellarLinkedAccountStrategy>();
builder.Services.AddScoped<VaultAccountStrategyFactory>();
builder.Services.AddScoped<VaultLinkedAccountExpansion>();
builder.Services.AddScoped<VaultLedgerRecordWorkspace>();
builder.Services.AddScoped<StellarLedgerUploadCoordinator>();
builder.Services.AddScoped<IStellarUploadCheckpointStore, BrowserStellarUploadCheckpointStore>();
builder.Services.AddScoped<IVaultPassphraseCredentialStore, IndexedDbVaultPassphraseCredentialStore>();
builder.Services.AddScoped<IVaultKeyProtector, BrowserVaultKeyProtector>();
builder.Services.AddScoped<VaultPassphraseLifecycle>();
builder.Services.AddScoped<IVaultChunkStore, IndexedDbVaultObjectStore>();
builder.Services.AddScoped<IVaultRecordIndex, IndexedDbVaultRecordIndex>();
builder.Services.AddScoped<VaultSessionWorkflow>();
builder.Services.AddSingleton<IVaultRecordTypeFactory, VaultRecordTypeFactory>();
builder.Services.AddScoped<VaultRecordWorkspace>();
builder.Services.AddScoped<VaultBinaryWorkspace>();

await builder.Build().RunAsync();
