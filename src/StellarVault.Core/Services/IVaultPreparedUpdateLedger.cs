namespace StellarVault.Core.Services;

/// <summary>
/// Testnet-only hybrid UPDATE commit transport. Persist the fully signed
/// primary manifest-write transaction hash before broadcasting it.
/// No ledger-level version CAS is provided: concurrent devices can still race.
/// Direct IStellarLedgerRecords.UpdateAsync does not use this callback.
/// </summary>
public interface IVaultPreparedUpdateLedger
{
    Task<LedgerMutationResult> UpdateIfUnchangedWithPreparedHashAsync(
        string recordId, LedgerRecordDraft draft, string expectedRemoteVersion,
        Func<string, Task> checkpointSignedManifestHash,
        CancellationToken ct = default);
}
