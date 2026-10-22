namespace StellarVault.Core.Services;

/// <summary>Separate HD-v2 on-chain record workflow. A successful Publish must
/// include a confirmed Stellar transaction; no IndexedDB fallback is allowed.</summary>
public interface IStellarLedgerRecords
{
    bool IsConnected { get; }
    Task ConnectAsync(string mnemonic, string horizonUrl, string networkPassphrase);
    Task<bool> HasRegistryAsync();
    Task<LedgerFundingStatus> InspectRegistryFundingAsync();
    Task<LedgerAccountInfo> InspectAccountInfoAsync();
    Task InitializeRegistryAsync();
    Task<IReadOnlyList<LedgerRecordItem>> ReadAsync();
    Task<string> PublishAsync(string title, string content);
    Task<string> PublishDetailedAsync(LedgerRecordDraft draft);
    Task<LedgerCostPreview> EstimateAsync(LedgerRecordDraft draft, string? recordId = null);
    Task<LedgerCostPreview> EstimateDeleteAsync(string id);
    Task<LedgerMutationResult> UpdateAsync(string id, LedgerRecordDraft draft);
    Task<LedgerMutationResult> DeleteAsync(string id);
    Task DisconnectAsync();
}
public sealed record LedgerAccountInfo(string RootAccountId, string TailAccountId, int TotalAccounts, int Records, int Folders, string BalanceNative, string SpendableNative, string SponsoredReserveNative);
public sealed record LedgerCostPreview(int Operations, int Transactions, decimal NetworkFeeXlm, decimal PeakReserveXlm, decimal NetReserveChangeXlm, bool CreatesChildNode);
public sealed record LedgerFundingStatus(string RootAccountId, string BalanceXlm, string SpendableXlm, string RequiredAdditionalXlm, bool CanInitialize);
public sealed record LedgerMutationResult(string TransactionHash, string? Warning = null);
public sealed record LedgerRecordItem(string Id, string Title, string Content, string? FolderId = null, string[]? Tags = null, bool IsFolder = false, string? UserName = null, string? Url = null, string? Notes = null);
public sealed record LedgerRecordDraft(string Title, string Content, string? FolderId = null, string[]? Tags = null, bool IsFolder = false, string? UserName = null, string? Url = null, string? Notes = null);

/// <summary>Native hosts fail closed until their signing transport is implemented.</summary>
public sealed class UnavailableStellarLedgerRecords : IStellarLedgerRecords
{
    public bool IsConnected => false;
    private static Exception Unavailable() => new NotSupportedException("Stellar ledger transport is not configured on this platform.");
    public Task ConnectAsync(string mnemonic, string horizonUrl, string networkPassphrase) => Task.FromException(Unavailable());
    public Task<bool> HasRegistryAsync() => Task.FromException<bool>(Unavailable());
    public Task<LedgerFundingStatus> InspectRegistryFundingAsync() => Task.FromException<LedgerFundingStatus>(Unavailable());
    public Task<LedgerAccountInfo> InspectAccountInfoAsync() => Task.FromException<LedgerAccountInfo>(Unavailable());
    public Task InitializeRegistryAsync() => Task.FromException(Unavailable());
    public Task<IReadOnlyList<LedgerRecordItem>> ReadAsync() => Task.FromException<IReadOnlyList<LedgerRecordItem>>(Unavailable());
    public Task<string> PublishAsync(string title, string content) => Task.FromException<string>(Unavailable());
    public Task<string> PublishDetailedAsync(LedgerRecordDraft draft) => Task.FromException<string>(Unavailable());
    public Task<LedgerCostPreview> EstimateAsync(LedgerRecordDraft draft, string? recordId = null) => Task.FromException<LedgerCostPreview>(Unavailable());
    public Task<LedgerCostPreview> EstimateDeleteAsync(string id) => Task.FromException<LedgerCostPreview>(Unavailable());
    public Task<LedgerMutationResult> UpdateAsync(string id, LedgerRecordDraft draft) => Task.FromException<LedgerMutationResult>(Unavailable());
    public Task<LedgerMutationResult> DeleteAsync(string id) => Task.FromException<LedgerMutationResult>(Unavailable());
    public Task DisconnectAsync() => Task.CompletedTask;
}
