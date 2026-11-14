using System.Security.Cryptography;
using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// A deliberately manual linked S-key create-only publisher. Reuses the
/// existing encrypted queue CAS, S-key signer, and fresh Stellar fee quote.
/// A durable Submitting reservation is written BEFORE broadcasting. If the
/// transport or verification fails, the entry remains Uncertain and MUST NOT
/// be automatically retried or removed.
/// </summary>
public sealed class VaultLinkedDraftPublication(
    IStellarSecretKeyLedger ledger,
    IVaultLinkedDraftStore store,
    VaultLinkedQueueKeySession keys,
    VaultDeviceRetentionGate retention,
    VaultLinkedLocalRetentionGate linkedRetention,
    VaultNetworkSelection networks)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed record PendingReview(string MutationId, string AccountId,
        string NetworkScope, long Epoch, byte[] Fingerprint,
        VaultSecretLedgerQuote Quote, DateTimeOffset QuotedUtc);
    private PendingReview? _pending;

    public sealed record Review(string MutationId, string EntityId, string Title,
        bool IsFolder, VaultSecretLedgerQuote Quote, string Explanation);

    private (string Root, string Scope, long Epoch) RequireScope()
    {
        retention.RequireLocalPersistence();
        linkedRetention.RequireLocalPersistence();
        if (!keys.IsUnlocked || !ledger.IsConnected ||
            ledger.RootAccountId is not string root)
            throw new UnauthorizedAccessException(
                "Unlock the independent S-key signer and encrypted local drafts.");
        StellarAccountAddress.Validate(root);
        return (root, networks.CredentialKey(root), ledger.ConnectionEpoch);
    }

    private void RequireSameScope(string account, string scope, long epoch)
    {
        var current = RequireScope();
        if (current.Root != account || current.Scope != scope || current.Epoch != epoch)
            throw new InvalidOperationException("S-key signer, network, or local consent changed.");
    }

    private async Task<IReadOnlyList<VaultPendingMutation>> LoadAsync(
        string root, string scope, CancellationToken ct)
    {
        var all = await store.ListAsync(root, scope, ct);
        if (all.Any(x => x.AccountId != root ||
            x.NetworkId != VaultLinkedQueueStorageScope.FromNetworkScope(scope) ||
            x.OriginStrategyId != VaultMutationStrategyPolicy.Linked))
            throw new InvalidDataException("Foreign or legacy-HD mutation in linked encrypted queue.");
        return all;
    }

    private static void Clear(IEnumerable<VaultPendingMutation> all)
    {
        foreach (var x in all)
            if (x.EncryptedPayload is not null)
                CryptographicOperations.ZeroMemory(x.EncryptedPayload);
    }

    private static LedgerRecordDraft Decode(VaultPendingMutation item)
    {
        if (item.Kind is not (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder) ||
            item.State != VaultSyncState.Pending || item.TransactionHash is not null ||
            item.EntityId is not { Length: 16 } || !item.EntityId.All(Uri.IsHexDigit) ||
            item.DependsOn is null || item.DependsOn.Length > 1)
            throw new InvalidOperationException(
                "Only never-submitted linked record/folder creations can be reviewed.");

        var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(item.EncryptedPayload)
            ?? throw new InvalidDataException("Encrypted draft is missing.");
        if (draft.IsFolder != (item.Kind == VaultMutationKind.CreateFolder) ||
            string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 256 ||
            string.IsNullOrEmpty(draft.Content) ||
            draft.Tags is { Length: > 24 } ||
            draft.Tags?.Any(x => x is null || x.Length > 64) == true)
            throw new InvalidDataException("Encrypted draft validation failed.");
        return draft;
    }

    private static void CheckRemote(VaultPendingMutation item, LedgerRecordDraft draft,
        IReadOnlyList<VaultPendingMutation> local,
        IReadOnlyList<LedgerRecordItem> remote)
    {
        if (local.Any(x => x.Id != item.Id &&
                x.EntityId == item.EntityId &&
                x.State is not (VaultSyncState.Failed or VaultSyncState.Confirmed)) ||
            remote.Any(x => x.Id == item.EntityId))
            throw new InvalidOperationException(
                "The target record already exists locally or on Stellar.");

        if (local.Any(x => x.Id != item.Id &&
            x.State is VaultSyncState.Submitting or VaultSyncState.Uncertain or VaultSyncState.Conflict))
            throw new InvalidOperationException(
                "An earlier local submission needs reconciliation before publishing more drafts.");

        // Pending local parents cannot be published in the same transaction.
        // The existing Core dependency planner still controls local ordering.
        var issues = VaultQueuedCreatePolicy.Problems(item, local);
        if (issues.Length != 0)
            throw new InvalidOperationException(
                "Linked draft is not ready: " + string.Join(", ", issues));
        if (item.DependsOn.Any(id =>
            !local.Any(x => x.Id == id && x.State == VaultSyncState.Confirmed)))
            throw new InvalidOperationException(
                "Publish and verify the parent folder before publishing its children.");
        if (draft.FolderId is { Length: > 0 } parent &&
            !remote.Any(x => x.Id == parent && x.IsFolder))
            throw new InvalidOperationException("Parent folder is not published on Stellar.");
    }

    private async Task CancelUnsafeAsync()
    {
        var old = _pending;
        _pending = null;
        if (old is null) return;
        try { await ledger.CancelQuoteAsync(old.Quote.QuoteId); }
        catch { /* Never silently re-enable an abandoned reviewed quote. */ }
        CryptographicOperations.ZeroMemory(old.Fingerprint);
    }

    public async Task<Review> PrepareAsync(string mutationId,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await CancelUnsafeAsync();
            var (root, scope, epoch) = RequireScope();
            var all = await LoadAsync(root, scope, ct);
            try
            {
                var item = all.SingleOrDefault(x => x.Id == mutationId)
                    ?? throw new KeyNotFoundException("Local encrypted draft not found.");
                var draft = Decode(item);
                var remote = await ledger.ReadAsync(ct);
                RequireSameScope(root, scope, epoch);
                CheckRemote(item, draft, all, remote);
                var quote = await ledger.QuoteCreateAsync(item.EntityId, draft, ct);
                if (quote.Kind != "create" || quote.CreatesChildNode ||
                    quote.TargetAccountId is not { Length: 56 } ||
                    quote.QuoteId is not { Length: 32 })
                    throw new InvalidDataException("Invalid linked create quote.");
                RequireSameScope(root, scope, epoch);
                _pending = new(item.Id, root, scope, epoch,
                    SHA256.HashData(item.EncryptedPayload), quote, DateTimeOffset.UtcNow);
                return new(item.Id, item.EntityId, draft.Title, draft.IsFolder, quote,
                    "Review the exact XLM fee and reserve. Publishing is a separate explicit step.");
            }
            finally { Clear(all); }
        }
        finally { _gate.Release(); }
    }

    public async Task<string> SubmitAsync(
        VaultSecretLedgerQuote approvedQuote, bool userApproved,
        CancellationToken ct = default)
    {
        if (!userApproved)
            throw new UnauthorizedAccessException("Explicit approval of this exact Stellar quote is required.");
        await _gate.WaitAsync(ct);
        try
        {
            var pending = _pending;
            _pending = null; // Single-use, even if preflight or transport fails.
            if (pending is null || !Equals(pending.Quote, approvedQuote))
                throw new InvalidOperationException("Quote changed; review again.");
            try
            {
                RequireSameScope(pending.AccountId, pending.NetworkScope, pending.Epoch);
                if (DateTimeOffset.UtcNow - pending.QuotedUtc > TimeSpan.FromMinutes(2))
                    throw new InvalidOperationException("Linked quote expired. Review again.");

                var all = await LoadAsync(pending.AccountId, pending.NetworkScope, ct);
                try
                {
                    var original = all.SingleOrDefault(x => x.Id == pending.MutationId)
                        ?? throw new KeyNotFoundException("Approved draft no longer exists.");
                    var currentHash = SHA256.HashData(original.EncryptedPayload);
                    try
                    {
                        if (!CryptographicOperations.FixedTimeEquals(
                            pending.Fingerprint, currentHash))
                            throw new InvalidOperationException("Draft changed after approval.");
                    }
                    finally { CryptographicOperations.ZeroMemory(currentHash); }
                    var draft = Decode(original);
                    var remote = await ledger.ReadAsync(ct);
                    RequireSameScope(pending.AccountId, pending.NetworkScope, pending.Epoch);
                    CheckRemote(original, draft, all, remote);
                    ct.ThrowIfCancellationRequested();

                    // After this successful CAS, ANY transport or read failure is
                    // potentially an accepted network transaction. No re-queue.
                    var reserved = original with { State = VaultSyncState.Submitting };
                    if (!await store.TryReplaceAsync(original, reserved, ct))
                        throw new InvalidOperationException(
                            "Local queue was changed while reserving publication.");

                    string? hash = null;
                    try
                    {
                        hash = await ledger.SubmitApprovedAsync(pending.Quote.QuoteId, ct);
                        var observed = await ledger.ReadAsync(ct);
                        RequireSameScope(pending.AccountId, pending.NetworkScope, pending.Epoch);
                        var published = observed.SingleOrDefault(x => x.Id == original.EntityId);
                        if (published is null || published.IsFolder != draft.IsFolder ||
                            published.Title != draft.Title || published.Content != draft.Content ||
                            published.FolderId != draft.FolderId)
                            throw new InvalidDataException(
                                "Confirmed transaction hash did not match the published draft.");
                        var confirmed = reserved with
                        {
                            State = VaultSyncState.Confirmed,
                            TransactionHash = hash
                        };
                        if (!await store.TryReplaceAsync(reserved, confirmed, ct))
                            throw new InvalidOperationException(
                                "Published transaction verified but local confirmation CAS failed.");
                        return hash;
                    }
                    catch
                    {
                        var uncertain = reserved with
                        {
                            State = VaultSyncState.Uncertain,
                            TransactionHash = hash
                        };
                        try { await store.TryReplaceAsync(reserved, uncertain, CancellationToken.None); }
                        catch { /* Durable Submitting remains fail-closed. */ }
                        throw new InvalidOperationException(
                            "Linked transaction may have reached Stellar. The encrypted queue remains blocked; reconcile its exact operation before retrying.");
                    }
                }
                finally { Clear(all); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pending.Fingerprint);
                try { await ledger.CancelQuoteAsync(pending.Quote.QuoteId); }
                catch { /* Transport already consumed or invalidated the quote. */ }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task CancelAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await CancelUnsafeAsync(); }
        finally { _gate.Release(); }
    }
}
