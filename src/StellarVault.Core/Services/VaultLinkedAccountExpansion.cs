namespace StellarVault.Core.Services;

/// <summary>
/// A linked expansion was rejected before the underlying transport was called.
/// The reviewed quote was discarded, so the user must request a new review.
/// This does NOT attest to any previous or concurrent network submission.
/// </summary>
public sealed class VaultLinkedExpansionPreflightException(string message, Exception innerException)
    : InvalidOperationException(message, innerException)
{
}

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
    public Task<string> SubmitApprovedAsync(
        VaultSecretLedgerQuote reviewedQuote, bool explicitlyApproved,
        CancellationToken ct = default) =>
        SubmitCoreAsync(reviewedQuote, explicitlyApproved, null, ct);

    /// <summary>
    /// Journaled expansion: checkpoint the exact signed hash before any
    /// network transmission. The legacy direct workspace remains separate.
    /// </summary>
    public Task<string> SubmitWithSignedHashAsync(
        VaultSecretLedgerQuote reviewedQuote, bool explicitlyApproved,
        Func<string, Task> durableCheckpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(durableCheckpoint);
        return SubmitCoreAsync(reviewedQuote, explicitlyApproved,
            durableCheckpoint, ct);
    }

    private async Task<string> SubmitCoreAsync(
        VaultSecretLedgerQuote reviewedQuote, bool explicitlyApproved,
        Func<string, Task>? durableCheckpoint, CancellationToken ct)
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

            // No network submission is initiated until all context, chain
            // inventory and quote freshness checks have completed. Any error
            // in this block can safely discard this PARTICULAR approval, but
            // never clears an older or concurrent unresolved ledger submission.
            try
            {
                RequireSameSession(pending);
                var before = await strategies.InspectConnectedAsync(
                    Request(pending.Root, pending.Network), ct);
                if (before.AccountCount != pending.PreviousCount ||
                    before.TailAddress != pending.PreviousTail)
                    throw new InvalidOperationException(
                        "The account chain changed. Cancel and review a new quote.");

                ct.ThrowIfCancellationRequested();
                RequireSameSession(pending);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _pending = null;
                await TryCancelUnsubmittedAsync(pending.Quote.QuoteId);
                throw;
            }
            catch (Exception ex)
            {
                _pending = null;
                await TryCancelUnsubmittedAsync(pending.Quote.QuoteId);
                throw new VaultLinkedExpansionPreflightException(
                    "Linked account expansion was not submitted. Refresh the vault and request a new review.", ex);
            }

            // The preflight quote is single-use. From here onward a transport
            // failure may be uncertain and MUST NOT be reclassified as preflight.
            _pending = null;
            string hash;
            if (durableCheckpoint is null)
            {
                hash = await ledger.SubmitApprovedAsync(pending.Quote.QuoteId, ct);
            }
            else
            {
                if (ledger is not IStellarSecretKeySignedSubmission prepared)
                    throw new InvalidOperationException(
                        "Linked expansion transport cannot durably checkpoint its signed hash.");
                hash = await prepared.SubmitWithSignedHashAsync(
                    pending.Quote.QuoteId, durableCheckpoint, ct);
            }
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

    private async Task TryCancelUnsubmittedAsync(string quoteId)
    {
        try { await ledger.CancelQuoteAsync(quoteId); }
        catch
        {
            // The Core quote has already been invalidated. The transport
            // remains single-use and must not submit an abandoned quote.
        }
    }

    /// <summary>
    /// Abandon any never-submitted expansion review when its owning UI session
    /// locks or disconnects. Once transport was invoked, _pending is already
    /// null, so this method cannot undo or authorize retry of an uncertain write.
    /// </summary>
    public async Task AbandonUnsubmittedAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var pending = _pending;
            _pending = null;
            if (pending is not null)
                await TryCancelUnsubmittedAsync(pending.Quote.QuoteId);
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
