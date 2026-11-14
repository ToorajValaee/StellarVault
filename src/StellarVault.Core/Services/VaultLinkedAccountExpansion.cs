namespace StellarVault.Core.Services;

/// <summary>
/// Shared, single-use review boundary for independent Stellar linked-account expansion.
/// Quotes are scoped to one currently connected S-key root and selected network.
/// It never receives secret material, creates a child autonomously or retries an
/// uncertain transaction. The transport must enforce the approved quote atomically.
/// </summary>
public sealed class VaultLinkedAccountExpansion(
    IStellarSecretKeyLedger ledger,
    VaultNetworkSelection network,
    VaultAccountStrategyFactory strategies)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PendingExpansion? _pending;

    private sealed record PendingExpansion(
        string Root, string Network, string CredentialScope, VaultSecretLedgerQuote Quote,
        DateTimeOffset QuotedUtc, string PreviousTail, int PreviousCount);

    private VaultAccountRequest Request(string root, string selectedNetwork) =>
        new(ConnectedStellarAccountStrategy.StellarChain, selectedNetwork, root,
            ConnectedStellarAccountStrategy.LinkedMode);

    private (string Root, string Network) RequireSession()
    {
        if (!ledger.IsConnected || ledger.RootAccountId is not { } root ||
            !StellarAccountAddress.IsValid(root))
            throw new InvalidOperationException("Connect and verify a linked Stellar vault first.");
        return (root, network.Network);
    }

    private void RequireSameSession(PendingExpansion pending)
    {
        var (root, selectedNetwork) = RequireSession();
        if (pending.Network != selectedNetwork)
            throw new InvalidOperationException("Linked-account strategy changed. Review again.");
        VaultApprovalScopeGuard.RequireFresh(
            pending.Root, pending.CredentialScope, root, network.CredentialKey(root),
            pending.QuotedUtc, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2));
    }

    /// <summary>
    /// Prepare fee/reserve and authorization review without submitting a transaction.
    /// There may be only one outstanding expansion quote per workspace instance.
    /// </summary>
    public async Task<VaultSecretLedgerQuote> PrepareAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_pending is not null)
                throw new InvalidOperationException("Cancel the existing account expansion review first.");

            var (root, selectedNetwork) = RequireSession();
            var scope = network.CredentialKey(root);
            var before = await strategies.InspectConnectedAsync(Request(root, selectedNetwork), ct);
            ct.ThrowIfCancellationRequested();
            RequireSessionMatches(root, selectedNetwork, scope);

            var quote = await ledger.QuoteExtendChainAsync(ct)
                ?? throw new InvalidDataException("Linked-account ledger returned no expansion quote.");
            try
            {
                if (quote.Kind != "expand" || !quote.CreatesChildNode ||
                    quote.QuoteId is not { Length: 32 } ||
                    !quote.QuoteId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                    !StellarAccountAddress.IsValid(quote.TargetAccountId))
                    throw new InvalidDataException("Invalid linked-account expansion quote.");

                RequireSessionMatches(root, selectedNetwork, scope);
                var after = await strategies.InspectConnectedAsync(Request(root, selectedNetwork), ct);
                if (before.AccountCount != after.AccountCount ||
                    before.TailAddress != after.TailAddress)
                    throw new InvalidOperationException("Account chain changed while preparing the quote.");

                _pending = new(root, selectedNetwork, scope, quote,
                    DateTimeOffset.UtcNow, before.TailAddress, before.AccountCount);
                return quote;
            }
            catch
            {
                try { await ledger.CancelQuoteAsync(quote.QuoteId); }
                catch { /* Keep the primary failure; never submit this quote. */ }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Requires a fresh, explicit UI approval of the exact displayed quote.
    /// The pending intent is consumed before the network call: failures are
    /// uncertain and must be reconciled, never retried automatically.
    /// </summary>
    public async Task<string> SubmitApprovedAsync(
        VaultSecretLedgerQuote reviewedQuote, bool explicitlyApproved, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reviewedQuote);
        if (!explicitlyApproved)
            throw new InvalidOperationException("Explicit transaction approval is required.");

        await _gate.WaitAsync(ct);
        try
        {
            var pending = _pending ??
                throw new InvalidOperationException("There is no prepared account expansion to approve.");
            if (reviewedQuote.Kind != "expand" || reviewedQuote != pending.Quote ||
                !reviewedQuote.CreatesChildNode)
                throw new InvalidOperationException("Reviewed expansion quote differs from the prepared quote.");

            RequireSameSession(pending);
            var before = await strategies.InspectConnectedAsync(
                Request(pending.Root, pending.Network), ct);
            if (before.AccountCount != pending.PreviousCount ||
                before.TailAddress != pending.PreviousTail)
                throw new InvalidOperationException("The account chain changed. Cancel and review a new quote.");

            ct.ThrowIfCancellationRequested();
            RequireSameSession(pending);
            // This is the no-retry boundary even if network submission fails or is cancelled.
            _pending = null;
            var hash = await ledger.SubmitApprovedAsync(pending.Quote.QuoteId, ct);
            if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit))
                throw new InvalidDataException("Account expansion receipt is invalid; reconcile before retry.");

            // A hash alone is not an independent confirmation of child signer
            // authorization. Confirm the connected chain advanced; deeper signer
            // and historical proof still belongs to the linked ledger adapter.
            var after = await strategies.InspectConnectedAsync(
                Request(pending.Root, pending.Network), ct);
            if (after.AccountCount != pending.PreviousCount + 1 ||
                after.TailAddress == pending.PreviousTail)
                throw new InvalidDataException(
                    "Linked chain expansion is unverified; reconcile before another transaction.");
            return hash;
        }
        finally { _gate.Release(); }
    }

    public async Task CancelAsync(string quoteId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_pending is not { } pending || pending.Quote.QuoteId != quoteId)
                throw new InvalidOperationException("There is no matching account expansion review.");
            _pending = null;
            await ledger.CancelQuoteAsync(pending.Quote.QuoteId);
        }
        finally { _gate.Release(); }
    }

    private void RequireSessionMatches(string root, string selectedNetwork, string expectedScope)
    {
        var current = RequireSession();
        if (current.Root != root || current.Network != selectedNetwork ||
            network.CredentialKey(root) != expectedScope)
            throw new InvalidOperationException("Linked-account session or network changed.");
    }
}
