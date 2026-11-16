namespace VaultEngine.Stellar.Services;

/// <summary>
/// Transport-level last-chance optimistic version guard for queued hybrid
/// updates/deletes. This is deliberately separate from direct on-chain CRUD.
/// It does NOT establish an atomic Stellar account-data compare-and-swap:
/// another wallet can still race after the guarded read. Hybrid remote writes
/// remain Testnet-only until a ledger-enforced conditional protocol exists.
/// </summary>
public interface IVaultGuardedLedgerMutations
{
    Task<LedgerMutationResult> UpdateIfUnchangedAsync(
        string recordId, LedgerRecordDraft draft, string expectedVersion,
        CancellationToken ct = default);

    Task<LedgerMutationResult> DeleteIfUnchangedAsync(
        string recordId, string expectedVersion,
        IReadOnlyList<string> expectedManifestOwners,
        CancellationToken ct = default);
}
