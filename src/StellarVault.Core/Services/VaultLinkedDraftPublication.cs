using System.Security.Cryptography;
using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// A deliberately manual linked S-key record mutation publisher. Reuses the
/// existing encrypted queue CAS, S-key signer, and fresh Stellar fee quote.
/// A durable Submitting reservation is written BEFORE broadcasting. If the
/// transport or verification fails, the entry remains Uncertain and MUST NOT
/// be automatically retried or removed.
/// </summary>
public sealed class VaultLinkedDraftPublication(
    IStellarSecretKeyLedger ledger,
    IVaultLinkedDraftStore store,
    VaultLinkedQueueKeySession keys,
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
        linkedRetention.DeviceRetention.RequireLocalPersistence();
        linkedRetention.RequireLocalPersistence();
        if (!keys.CanPublish || !ledger.IsConnected ||
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
        try
        {
            if (all.Any(x => x.AccountId != root ||
                x.NetworkId != VaultLinkedQueueStorageScope.FromNetworkScope(scope) ||
                x.OriginStrategyId != VaultMutationStrategyPolicy.Linked))
                throw new InvalidDataException(
                    "Foreign or legacy-HD mutation in linked encrypted queue.");
            return all;
        }
        catch
        {
            Clear(all);
            throw;
        }
    }

    private static byte[] Fingerprint(VaultPendingMutation mutation)
    {
        // Bind the review to every immutable queue field, including record ID,
        // origin, account, network, parent dependencies and full draft bytes.
        // Payload-only hashes would allow a parent/target change after review.
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(mutation);
        try { return SHA256.HashData(serialized); }
        finally { CryptographicOperations.ZeroMemory(serialized); }
    }

    private static void Clear(IEnumerable<VaultPendingMutation> all)
    {
        foreach (var x in all)
            if (x.EncryptedPayload is not null)
                CryptographicOperations.ZeroMemory(x.EncryptedPayload);
    }

    private static LedgerRecordDraft Decode(VaultPendingMutation item)
    {
        if (item.Kind is not (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder or
            VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder or
            VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder) ||
            item.State != VaultSyncState.Pending || item.TransactionHash is not null ||
            item.EntityId is not { Length: 16 } || !item.EntityId.All(Uri.IsHexDigit) ||
            item.DependsOn is null || item.DependsOn.Length > 1)
            throw new InvalidOperationException(
                "Only never-submitted linked record/folder mutations can be reviewed.");

        var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(item.EncryptedPayload)
            ?? throw new InvalidDataException("Encrypted draft is missing.");
        if (draft.IsFolder != (item.Kind is VaultMutationKind.CreateFolder or
            VaultMutationKind.UpdateFolder or VaultMutationKind.DeleteFolder) ||
            string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 256 ||
            string.IsNullOrEmpty(draft.Content) ||
            draft.Tags is { Length: > 24 } ||
            draft.Tags?.Any(x => x is null || x.Length > 64) == true)
            throw new InvalidDataException("Encrypted draft validation failed.");
        return draft;
    }

    private static bool ExistingMutation(VaultMutationKind kind) =>
        kind is VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder or
            VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder;

    private static bool Deletion(VaultMutationKind kind) =>
        kind is VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder;

    private static string QuoteKind(VaultMutationKind kind) =>
        Deletion(kind) ? "delete" : ExistingMutation(kind) ? "update" : "create";

    private static bool SameRecord(LedgerRecordItem found, LedgerRecordDraft draft) =>
        found.IsFolder == draft.IsFolder &&
        found.Title == draft.Title && found.Content == draft.Content &&
        found.FolderId == draft.FolderId &&
        (found.UserName ?? "") == (draft.UserName ?? "") &&
        (found.Url ?? "") == (draft.Url ?? "") &&
        (found.Notes ?? "") == (draft.Notes ?? "") &&
        (found.Tags ?? []).SequenceEqual(draft.Tags ?? [], StringComparer.Ordinal);

    private static void CheckRemote(VaultPendingMutation item, LedgerRecordDraft draft,
        IReadOnlyList<VaultPendingMutation> local,
        IReadOnlyList<LedgerRecordItem> remote)
    {
        if (local.Any(x => x.Id != item.Id &&
            x.EntityId == item.EntityId &&
            x.State is not (VaultSyncState.Failed or VaultSyncState.Confirmed)))
            throw new InvalidOperationException(
                "Overlapping encrypted mutations target the same linked record.");

        if (local.Any(x => x.Id != item.Id &&
            ((x.State is VaultSyncState.Submitting or VaultSyncState.Uncertain or VaultSyncState.Conflict) ||
             (x.Kind == VaultMutationKind.ExtendLinkedChain &&
              x.State != VaultSyncState.Confirmed))))
            throw new InvalidOperationException(
                "An earlier local submission needs reconciliation before publishing more drafts.");

        if (ExistingMutation(item.Kind))
        {
            if (item.ExpectedRemoteVersion is not { Length: 64 } ||
                remote.SingleOrDefault(x => x.Id == item.EntityId) is not { } old ||
                old.IsFolder != draft.IsFolder)
                throw new InvalidOperationException(
                    "Linked mutation lost its expected published record or version.");
            if (Deletion(item.Kind))
            {
                if (!SameRecord(old, draft))
                    throw new InvalidOperationException(
                        "The published record differs from the snapshot approved for deletion.");
                if (remote.Any(x => x.FolderId == item.EntityId))
                    throw new InvalidOperationException(
                        "Delete published child records before deleting a folder.");
            }
            else
                VaultLinkedFolderHierarchyPolicy.RequirePublishedAcyclicParent(
                    item.EntityId, draft.FolderId, remote);
        }
        else if (remote.Any(x => x.Id == item.EntityId))
            throw new InvalidOperationException("Target record already exists on Stellar.");

        if (!ExistingMutation(item.Kind))
        {
            var issues = VaultLinkedMultipartFilePolicy.Problems(item, local, remote);
            if (issues.Length != 0)
                throw new InvalidOperationException(
                    "Linked draft is not ready: " + string.Join(", ", issues));
            if (item.DependsOn.Any(id =>
                !local.Any(x => x.Id == id && x.State == VaultSyncState.Confirmed)))
                throw new InvalidOperationException(
                    "Publish and verify the parent folder before publishing its children.");
        }
        else if (item.DependsOn.Length != 0)
            throw new InvalidOperationException("Remote mutations cannot depend on unpublished parents.");

        if (draft.FolderId is { Length: > 0 } parent &&
            !remote.Any(x => x.Id == parent && x.IsFolder))
            throw new InvalidOperationException("Parent folder is not published on Stellar.");
    }

    private async Task RequireRemoteVersionAsync(VaultPendingMutation item,
        CancellationToken ct)
    {
        if (!ExistingMutation(item.Kind)) return;
        if (ledger is not IStellarSecretKeyRecordRevisionReader reader)
            throw new NotSupportedException(
                "This linked signer cannot verify stale published record revisions.");
        var now = await reader.ReadRecordRevisionAsync(item.EntityId, ct);
        if (now is null || now != item.ExpectedRemoteVersion)
            throw new InvalidOperationException(
                "Linked remote manifest changed since staging. Never overwrite or delete a stale record.");
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
                await RequireRemoteVersionAsync(item, ct);
                RequireSameScope(root, scope, epoch);
                var kind = QuoteKind(item.Kind);
                var quote = kind switch
                {
                    "update" => await ledger.QuoteUpdateAsync(item.EntityId, draft, ct),
                    "delete" => await ledger.QuoteDeleteAsync(item.EntityId, ct),
                    _ => await ledger.QuoteCreateAsync(item.EntityId, draft, ct)
                };
                try
                {
                    if (quote.Kind != kind || quote.CreatesChildNode ||
                        quote.TargetAccountId is not { Length: 56 } ||
                        quote.QuoteId is not { Length: 32 } ||
                        !quote.QuoteId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                        throw new InvalidDataException("Invalid linked record mutation quote.");
                    RequireSameScope(root, scope, epoch);
                    _pending = new(item.Id, root, scope, epoch,
                        Fingerprint(item), quote, DateTimeOffset.UtcNow);
                    return new(item.Id, item.EntityId, draft.Title, draft.IsFolder, quote,
                        "Review the exact XLM fee and reserve. Publishing is a separate explicit step.");
                }
                catch
                {
                    try { await ledger.CancelQuoteAsync(quote.QuoteId); }
                    catch { /* Preserve the original preflight failure. */ }
                    throw;
                }
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
            if (pending is null)
                throw new InvalidOperationException("No prepared quote exists. Review again.");
            // Even a mismatched approval must release the old one-shot quote
            // and wipe the draft digest. Never leave an orphaned SDK quote.
            if (!Equals(pending.Quote, approvedQuote))
            {
                try { await ledger.CancelQuoteAsync(pending.Quote.QuoteId); }
                finally { CryptographicOperations.ZeroMemory(pending.Fingerprint); }
                throw new InvalidOperationException("Quote changed; review again.");
            }
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
                    var currentHash = Fingerprint(original);
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
                    await RequireRemoteVersionAsync(original, ct);
                    RequireSameScope(pending.AccountId, pending.NetworkScope, pending.Epoch);
                    ct.ThrowIfCancellationRequested();

                    // After this successful CAS, ANY transport or read failure is
                    // potentially an accepted network transaction. No re-queue.
                    var reserved = original with { State = VaultSyncState.Submitting };
                    if (!await store.TryReplaceAsync(original, reserved, ct))
                        throw new InvalidOperationException(
                            "Local queue was changed while reserving publication.");

                    var submission = reserved;
                    string? hash = null;
                    try
                    {
                        if (ledger is not IStellarSecretKeySignedSubmission transport)
                            throw new InvalidOperationException(
                                "Linked queue transport cannot checkpoint a signed hash; no transaction was sent.");
                        hash = await transport.SubmitWithSignedHashAsync(
                            pending.Quote.QuoteId,
                            async signedHash =>
                            {
                                RequireSameScope(pending.AccountId, pending.NetworkScope, pending.Epoch);
                                if (submission.TransactionHash is not null)
                                    throw new InvalidOperationException("Signed hash already checkpointed.");
                                var pinned = submission with { TransactionHash = signedHash };
                                if (!await store.TryReplaceAsync(
                                        submission, pinned, CancellationToken.None))
                                    throw new InvalidOperationException(
                                        "Signed hash could not be persisted; refuse network transmission.");
                                submission = pinned;
                            }, ct);
                        if (submission.TransactionHash != hash)
                            throw new InvalidDataException(
                                "Transport result did not match the durably checkpointed signed hash.");
                        var observed = await ledger.ReadAsync(ct);
                        RequireSameScope(pending.AccountId, pending.NetworkScope, pending.Epoch);
                        var published = observed.SingleOrDefault(x => x.Id == original.EntityId);
                        if (Deletion(original.Kind)
                            ? published is not null
                            : published is null || !SameRecord(published, draft))
                            throw new InvalidDataException(
                                "Linked transaction response did not match the expected current record mutation.");
                        // A matching CURRENT account snapshot does not prove that
                        // this exact transaction wrote the encrypted manifest and
                        // chunks. Preserve the durable quarantine until an
                        // independent historical signer/operation verifier exists.
                        var uncertain = submission with
                        {
                            State = VaultSyncState.Uncertain
                        };
                        if (!await store.TryReplaceAsync(submission, uncertain, CancellationToken.None))
                            throw new InvalidOperationException(
                                "Submission was observed but queue quarantine CAS failed; do not retry.");
                        return hash;
                    }
                    catch
                    {
                        // A signed hash may already exist even when Horizon
                        // throws before returning its receipt. Bind the
                        // runtime's public diagnostic to the same signer,
                        // network, record, destination and session epoch.
                        hash = submission.TransactionHash ?? hash ??
                            await VaultLinkedSignedSubmissionIdentity.TryReadAsync(
                                ledger, pending.AccountId, QuoteKind(original.Kind),
                                pending.Quote.TargetAccountId, original.EntityId,
                                ledger.ConnectedNetworkPassphrase ?? "",
                                pending.Epoch);
                        var uncertain = submission with
                        {
                            State = VaultSyncState.Uncertain,
                            TransactionHash = hash
                        };
                        try { await store.TryReplaceAsync(submission, uncertain, CancellationToken.None); }
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
