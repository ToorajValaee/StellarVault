namespace StellarVault.Core.Services;

/// <summary>
/// Hybrid Testnet-only deletion transport with a durable signed-hash callback
/// for EACH HD manifest tombstone. No callback means no broadcast of that
/// individual tombstone. This is not atomic deletion or confirmation.
/// </summary>
public interface IVaultPreparedDeletionLedger
{
    Task<LedgerMutationResult> DeleteIfUnchangedWithJournalAsync(
        string recordId, string expectedRemoteVersion,
        IReadOnlyList<string> expectedManifestOwners,
        Func<string, string, Task> checkpointSignedTombstone,
        CancellationToken ct = default);
}
