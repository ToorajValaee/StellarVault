namespace StellarVault.Core.Services;

/// <summary>
/// Hybrid-only transport boundary. The callback MUST durably checkpoint the
/// exact fully signed manifest transaction hash BEFORE the JS signer sends
/// that transaction to Horizon. If checkpointing fails, no manifest broadcast.
/// Direct on-chain CRUD deliberately keeps its existing API.
/// </summary>
public interface IVaultPreparedCreateLedger
{
    Task<string> PublishWithPreparedHashAsync(
        string recordId, LedgerRecordDraft draft,
        Func<string, Task> checkpointSignedManifestHash);
}
