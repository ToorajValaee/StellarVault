namespace StellarVault.Core.Services;

/// <summary>
/// Storage-driven planning for linked-account records. The vault requests a
/// record write; its on-chain planner decides when the tail account cannot
/// accommodate a new record. Capacity expansion requires a separate reviewed
/// quote; this coordinator never broadcasts or persists plaintext.
/// </summary>
public sealed class VaultLinkedRecordWritePlanner(
    IStellarSecretKeyLedger ledger,
    IVaultRecordTypeFactory recordTypes,
    VaultLinkedAccountExpansion expansion)
{
    public sealed record ReviewPlan(VaultSecretLedgerQuote Quote, bool RequiresCapacityExpansion);

    public async Task<ReviewPlan> PrepareAsync(
        string recordId, LedgerRecordDraft draft, bool isCreate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        _ = recordTypes.Create(draft.IsFolder ? VaultRecordTypeFactory.Folder :
            VaultRecordTypeFactory.Credential, draft.Title);
        try
        {
            var quote = isCreate
                ? await ledger.QuoteCreateAsync(recordId, draft, ct)
                : await ledger.QuoteUpdateAsync(recordId, draft, ct);
            return new ReviewPlan(quote, false);
        }
        catch (VaultAccountCapacityRequiredException) when (isCreate)
        {
            // Never retry the same record write automatically: first present the
            // account-creation review and require confirmation of its outcome.
            return new ReviewPlan(await expansion.PrepareAsync(ct), true);
        }
    }

    /// <summary>Call only after approved expansion was submitted and verified.</summary>
    public async Task<VaultSecretLedgerQuote> PrepareDeferredCreateAsync(
        string recordId, LedgerRecordDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        _ = recordTypes.Create(draft.IsFolder ? VaultRecordTypeFactory.Folder :
            VaultRecordTypeFactory.Credential, draft.Title);
        return await ledger.QuoteCreateAsync(recordId, draft, ct);
    }
}
