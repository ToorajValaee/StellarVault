using Microsoft.JSInterop;
using StellarVault.Core.Services;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Separately versioned S-key vault ledger. Unlike the HD adapter this never
/// derives child keys from a mnemonic and never routes mutations through HD.
/// Transaction submission requires a fresh quote ID issued in this session.
/// </summary>
public sealed class BrowserSecretKeyLedger(IJSRuntime js) :
    IStellarSecretKeyLedger, IAsyncDisposable
{
    private readonly string _sessionId = "sk-" + Guid.NewGuid().ToString("N");
    private bool _connected;
    private string? _root;
    public bool IsConnected => _connected;
    public string? RootAccountId => _root;
    private void RequireConnected()
    {
        if (!_connected || _root is null)
            throw new InvalidOperationException("Connect the independent Secret Key vault first.");
    }

    public async Task ConnectAsync(
        string secretKey, string horizonUrl, string networkPassphrase,
        CancellationToken cancellationToken = default)
    {
        if (_connected) throw new InvalidOperationException("A Secret Key vault is already open.");
        new VaultAccessCredential(VaultCredentialMode.StellarSecretKeyChain, secretKey).Validate();
        if (string.IsNullOrWhiteSpace(networkPassphrase))
            throw new ArgumentException("A network passphrase is required.", nameof(networkPassphrase));
        try
        {
            var result = await js.InvokeAsync<SecretRootInfo>(
                "stellarVaultSecretLedger.open", cancellationToken, _sessionId,
                secretKey, horizonUrl, networkPassphrase);
            if (result is null || !StellarAccountAddress.IsValid(result.RootAccountId))
                throw new InvalidDataException("Invalid root identity from Secret Key ledger.");
            _root = result.RootAccountId;
            _connected = true;
        }
        catch
        {
            try { await js.InvokeVoidAsync("stellarVaultSecretLedger.close", _sessionId); }
            catch { /* The original failure is more informative. */ }
            _root = null;
            _connected = false;
            throw;
        }
    }

    private sealed record SecretRootInfo(string RootAccountId);

    public async Task<IReadOnlyList<LedgerRecordItem>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        RequireConnected();
        var items = await js.InvokeAsync<LedgerRecordItem[]>(
            "stellarVaultSecretLedger.read", cancellationToken, _sessionId);
        if (items is null || items.Length > 10000 || items.Any(x =>
                x is null || x.Id.Length != 16 || !x.Id.All(Uri.IsHexDigit) ||
                string.IsNullOrWhiteSpace(x.Title)))
            throw new InvalidDataException("Invalid recovered Secret Key ledger records.");
        if (items.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new InvalidDataException("Duplicate Secret Key ledger record identifiers.");
        return items;
    }

    public async Task<LedgerAccountInfo> InspectAccountInfoAsync(
        CancellationToken cancellationToken = default)
    {
        RequireConnected();
        var info = await js.InvokeAsync<LedgerAccountInfo>(
            "stellarVaultSecretLedger.info", cancellationToken, _sessionId);
        if (info is null || info.RootAccountId != _root ||
            !StellarAccountAddress.IsValid(info.TailAccountId) ||
            info.TotalAccounts is < 1 or > 64 || info.Records < 0 || info.Folders < 0)
            throw new InvalidDataException("Invalid Secret Key chain account inventory.");
        return info;
    }

    private static void ValidateRecordId(string recordId)
    {
        if (recordId is not { Length: 16 } || !recordId.All(
                c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("Expected a 16-character lowercase record ID.",
                nameof(recordId));
    }

    private async Task<VaultSecretLedgerQuote> QuoteAsync(
        string kind, string? recordId, LedgerRecordDraft? draft, CancellationToken ct)
    {
        RequireConnected();
        if (kind != "expand") ValidateRecordId(recordId!);
        if (kind is "create" or "update")
        {
            ArgumentNullException.ThrowIfNull(draft);
            if (string.IsNullOrWhiteSpace(draft.Title) ||
                (!draft.IsFolder && string.IsNullOrEmpty(draft.Content)))
                throw new ArgumentException("Title and record content are required.");
        }
        VaultSecretLedgerQuote quote;
        try
        {
            quote = await js.InvokeAsync<VaultSecretLedgerQuote>(
                "stellarVaultSecretLedger.plan", ct, _sessionId, kind, recordId, draft);
        }
        catch (JSException ex) when (kind == "create" &&
            ex.Message.Contains("SV_ACCOUNT_CAPACITY_REQUIRED", StringComparison.Ordinal))
        {
            throw new VaultAccountCapacityRequiredException();
        }
        if (quote is null ||
            string.IsNullOrWhiteSpace(quote.QuoteId) || quote.QuoteId.Length != 32 ||
            !quote.QuoteId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            quote.Operations is < 1 or > 90 || quote.Transactions != 1 ||
            string.IsNullOrWhiteSpace(quote.TargetAccountId) ||
            !StellarAccountAddress.IsValid(quote.TargetAccountId) ||
            quote.Kind != kind ||
            !decimal.TryParse(quote.NetworkFeeXlm,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var networkFee) ||
            networkFee <= 0 ||
            !decimal.TryParse(quote.PeakReserveXlm,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var peakReserve) ||
            peakReserve < 0 ||
            !decimal.TryParse(quote.FundingTopUpXlm,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var topUp) ||
            topUp < 0 ||
            !decimal.TryParse(quote.NetReserveChangeXlm,
                System.Globalization.NumberStyles.AllowLeadingSign |
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            throw new InvalidDataException("Invalid or unreviewable Stellar fee quote.");
        return quote;
    }

    public Task<VaultSecretLedgerQuote> QuoteCreateAsync(
        string recordId, LedgerRecordDraft draft, CancellationToken cancellationToken = default) =>
        QuoteAsync("create", recordId, draft, cancellationToken);
    public Task<VaultSecretLedgerQuote> QuoteUpdateAsync(
        string recordId, LedgerRecordDraft draft, CancellationToken cancellationToken = default) =>
        QuoteAsync("update", recordId, draft, cancellationToken);
    public Task<VaultSecretLedgerQuote> QuoteDeleteAsync(
        string recordId, CancellationToken cancellationToken = default) =>
        QuoteAsync("delete", recordId, null, cancellationToken);
    public Task<VaultSecretLedgerQuote> QuoteExtendChainAsync(
        CancellationToken cancellationToken = default) =>
        QuoteAsync("expand", null, null, cancellationToken);

    public async Task<VaultSecretSubmissionDiagnostic?> InspectUncertainSubmissionAsync(
        CancellationToken cancellationToken = default)
    {
        RequireConnected();
        var finding = await js.InvokeAsync<VaultSecretSubmissionDiagnostic?>(
            "stellarVaultSecretLedger.inspectUncertain", cancellationToken, _sessionId);
        if (finding is null) return null;
        if (finding.RootAccountId != _root ||
            !StellarAccountAddress.IsValid(finding.TargetAccountId) ||
            finding.Kind is not ("create" or "update" or "delete" or "expand") ||
            (finding.Kind == "expand" && finding.RecordId is not null) ||
            (finding.Kind != "expand" && (finding.RecordId is not { Length: 16 } ||
                !finding.RecordId.All(Uri.IsHexDigit))) ||
            string.IsNullOrWhiteSpace(finding.NetworkPassphrase) ||
            (finding.TransactionHash is not null &&
                (finding.TransactionHash.Length != 64 ||
                 !finding.TransactionHash.All(Uri.IsHexDigit))))
            throw new InvalidDataException("Invalid volatile linked-ledger submission diagnostic.");
        return finding;
    }

    public async Task CancelQuoteAsync(string quoteId)
    {
        if (_connected && !string.IsNullOrWhiteSpace(quoteId))
            await js.InvokeVoidAsync("stellarVaultSecretLedger.cancelQuote", _sessionId, quoteId);
    }

    public async Task<string> SubmitApprovedAsync(string quoteId,
        CancellationToken cancellationToken = default)
    {
        RequireConnected();
        if (quoteId is not { Length: 32 } ||
            !quoteId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("Invalid reviewed transaction quote.", nameof(quoteId));
        var hash = await js.InvokeAsync<string>(
            "stellarVaultSecretLedger.submit", cancellationToken, _sessionId, quoteId);
        if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Stellar mutation returned no confirmed transaction hash. Reconcile before retry.");
        return hash;
    }

    public async Task DisconnectAsync()
    {
        _root = null;
        _connected = false;
        await js.InvokeVoidAsync("stellarVaultSecretLedger.close", _sessionId);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connected)
        {
            try { await DisconnectAsync(); }
            catch (JSDisconnectedException) { /* WebView or browser shutdown. */ }
        }
    }
}
