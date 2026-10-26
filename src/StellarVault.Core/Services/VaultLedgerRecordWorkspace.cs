namespace StellarVault.Core.Services;

/// <summary>
/// Cross-platform on-chain record operations. This service never opens a local
/// object store or falls back to device persistence when ledger access fails.
/// The ledger transport remains responsible for verifying transaction inclusion.
/// </summary>
public sealed class VaultLedgerRecordWorkspace(
    IStellarLedgerRecords ledger, VaultStorageModeGate storageMode)
{
    public async Task<IReadOnlyList<LedgerRecordItem>> ListAsync()
    {
        storageMode.Require(VaultStorageMode.StellarLedger);
        var records = await ledger.ReadAsync();
        storageMode.Require(VaultStorageMode.StellarLedger);
        return records;
    }

    public async Task<LedgerCostPreview> EstimateAsync(LedgerRecordDraft draft, string? recordId = null)
    {
        storageMode.Require(VaultStorageMode.StellarLedger);
        var result = await ledger.EstimateAsync(draft, recordId);
        storageMode.Require(VaultStorageMode.StellarLedger);
        return result;
    }

    public async Task<string> PublishAsync(LedgerRecordDraft draft)
    {
        storageMode.Require(VaultStorageMode.StellarLedger);
        ArgumentNullException.ThrowIfNull(draft);
        var hash = await ledger.PublishDetailedAsync(draft);
        // A disconnected or switched workspace must not report a confirmed save.
        storageMode.Require(VaultStorageMode.StellarLedger);
        return LedgerMutationResult.RequireConfirmed(hash).TransactionHash;
    }

    public async Task<LedgerMutationResult> UpdateAsync(string id, LedgerRecordDraft draft)
    {
        storageMode.Require(VaultStorageMode.StellarLedger);
        ArgumentNullException.ThrowIfNull(draft);
        var result = await ledger.UpdateAsync(id, draft);
        storageMode.Require(VaultStorageMode.StellarLedger);
        return LedgerMutationResult.RequireConfirmed(result.TransactionHash, result.Warning);
    }

    public async Task<LedgerMutationResult> DeleteAsync(string id)
    {
        storageMode.Require(VaultStorageMode.StellarLedger);
        var result = await ledger.DeleteAsync(id);
        storageMode.Require(VaultStorageMode.StellarLedger);
        return LedgerMutationResult.RequireConfirmed(result.TransactionHash, result.Warning);
    }
}
