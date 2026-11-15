namespace StellarVault.Core.Services;

/// <summary>
/// Independently refetches the historically accepted signed Stellar envelope,
/// verifies the root signature, reconstructs all operation bytes, and decrypts
/// the exact historical record to compare against the approved local draft.
/// A true result is still scoped to the selected Horizon network's consensus
/// view; this service never changes a durable journal itself.
/// </summary>
public interface IStellarSecretKeyHistoricalRecordVerifier
{
    Task<bool> VerifyAsync(string transactionHash, VaultMutationKind kind,
        string recordId, LedgerRecordDraft expectedDraft,
        string? originalManifest, CancellationToken ct = default);
}
