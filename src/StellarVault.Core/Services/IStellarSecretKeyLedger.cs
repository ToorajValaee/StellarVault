namespace StellarVault.Core.Services;

/// <summary>
/// Independent linked-account ledger backend. Authentication uses an imported
/// Stellar S-key, never a BIP-39 mnemonic. There is no automatic transaction
/// submission or fallback to device storage. Callers must quote and obtain
/// explicit approval immediately before every mutating SubmitAsync.
/// </summary>
public interface IStellarSecretKeyLedger
{
    bool IsConnected { get; }
    string? RootAccountId { get; }
    Task ConnectAsync(string secretKey, string horizonUrl, string networkPassphrase,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LedgerRecordItem>> ReadAsync(CancellationToken cancellationToken = default);
    Task<LedgerAccountInfo> InspectAccountInfoAsync(CancellationToken cancellationToken = default);
    Task<VaultSecretLedgerQuote> QuoteCreateAsync(string recordId, LedgerRecordDraft draft,
        CancellationToken cancellationToken = default);
    Task<VaultSecretLedgerQuote> QuoteUpdateAsync(string recordId, LedgerRecordDraft draft,
        CancellationToken cancellationToken = default);
    Task<VaultSecretLedgerQuote> QuoteDeleteAsync(string recordId,
        CancellationToken cancellationToken = default);
    Task<VaultSecretLedgerQuote> QuoteExtendChainAsync(
        CancellationToken cancellationToken = default);
    Task CancelQuoteAsync(string quoteId);
    Task<string> SubmitApprovedAsync(string quoteId,
        CancellationToken cancellationToken = default);
    Task DisconnectAsync();
}

public sealed record VaultSecretLedgerQuote(
    string QuoteId, string Kind, int Operations, int Transactions,
    string NetworkFeeXlm, string PeakReserveXlm, string NetReserveChangeXlm,
    bool CreatesChildNode, string TargetAccountId);

/// <summary>
/// MAUI and PWA bridge to the same JavaScript ledger implementation.
/// Without a connected session operations fail closed.
/// </summary>
public sealed class UnavailableStellarSecretKeyLedger : IStellarSecretKeyLedger
{
    public bool IsConnected => false;
    public string? RootAccountId => null;
    private static Exception NotAvailable() => new NotSupportedException(
        "Secret Key ledger transport is unavailable on this host.");
    public Task ConnectAsync(string secretKey,string horizonUrl,string networkPassphrase,
        CancellationToken cancellationToken=default) => Task.FromException(NotAvailable());
    public Task<IReadOnlyList<LedgerRecordItem>> ReadAsync(CancellationToken cancellationToken=default) =>
        Task.FromException<IReadOnlyList<LedgerRecordItem>>(NotAvailable());
    public Task<LedgerAccountInfo> InspectAccountInfoAsync(CancellationToken cancellationToken=default) =>
        Task.FromException<LedgerAccountInfo>(NotAvailable());
    public Task<VaultSecretLedgerQuote> QuoteCreateAsync(string recordId,LedgerRecordDraft draft,CancellationToken cancellationToken=default) =>
        Task.FromException<VaultSecretLedgerQuote>(NotAvailable());
    public Task<VaultSecretLedgerQuote> QuoteUpdateAsync(string recordId,LedgerRecordDraft draft,CancellationToken cancellationToken=default) =>
        Task.FromException<VaultSecretLedgerQuote>(NotAvailable());
    public Task<VaultSecretLedgerQuote> QuoteDeleteAsync(string recordId,CancellationToken cancellationToken=default) =>
        Task.FromException<VaultSecretLedgerQuote>(NotAvailable());
    public Task<VaultSecretLedgerQuote> QuoteExtendChainAsync(CancellationToken cancellationToken=default) =>
        Task.FromException<VaultSecretLedgerQuote>(NotAvailable());
    public Task CancelQuoteAsync(string quoteId) => Task.FromException(NotAvailable());
    public Task<string> SubmitApprovedAsync(string quoteId,CancellationToken cancellationToken=default) =>
        Task.FromException<string>(NotAvailable());
    public Task DisconnectAsync() => Task.CompletedTask;
}
