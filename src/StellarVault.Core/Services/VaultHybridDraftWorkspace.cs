using VaultEngine.Core;
using System.Security.Cryptography;
using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// Cross-host encrypted offline record/folder draft workspace. The queue
/// adapter seals the serialized draft and all mutation metadata at rest;
/// no Stellar submission or signing occurs here.
/// </summary>
public sealed class VaultHybridDraftWorkspace(
    VaultPendingMutationQueue queue,
    VaultSessionManager sessions,
    VaultDeviceRetentionGate retention,
    VaultNetworkSelection networks)
{
    public sealed record DraftItem(
        string MutationId, string EntityId, VaultMutationKind Kind,
        VaultSyncState State, LedgerRecordDraft Draft, DateTimeOffset CreatedUtc,
        string PayloadSha256, bool IsCorrupt = false);

    public Task<VaultPendingMutation> CreateAsync(
        LedgerRecordDraft draft, CancellationToken ct = default) =>
        CreateAsync(draft, ObjectCapacityPolicy.SingleAccount, ct);

    public async Task<VaultPendingMutation> CreateAsync(
        LedgerRecordDraft draft, ObjectCapacityPolicy capacityPolicy,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(capacityPolicy))
            throw new ArgumentException("Unsupported storage capacity policy.", nameof(capacityPolicy));
        var context = RequireScope();
        var account = context.Account;
        var scope = context.NetworkId;
        ArgumentNullException.ThrowIfNull(draft);
        var kind = draft.IsFolder
            ? VaultMutationKind.CreateFolder : VaultMutationKind.CreateRecord;
        ValidateDraft(draft, kind);
        var entries = await queue.ReviewAllAsync(account, scope, ct);
        try
        {
            RequireSameScope(context);
            var dependencies = ParentDependencies(draft, entries, null);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(draft);
            try
            {
                RequireSameScope(context);
                var recordId = NewUnusedRecordId(entries);
                var item = VaultPendingMutation.Create(
                    account, scope, kind, recordId, payload,
                    dependsOn: dependencies) with { CapacityPolicy = capacityPolicy };
                // Local dependencies intentionally remain pending. The separate
                // preflight policy blocks them until their parent is published.
                var saved = await queue.EnqueueAsync(item, ct);
                RequireSameScope(context);
                return saved;
            }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }
        finally { ClearReviewPayloads(entries); }
    }

    /// <summary>
    /// Stage an arbitrarily long file as individually encrypted HD queue
    /// create-record mutations. The existing HD Sync review owns fees,
    /// signer authorization, signed-hash checkpoints and historical proof;
    /// this method NEVER submits transactions. All children are staged before
    /// the typed root and therefore cannot publish on partial staging failure.
    /// </summary>
    public async Task<VaultStreamingFileTree.Result> StageTreeFileAsync(
        string title, string fileName, string contentType, Stream input,
        string? folderId = null, CancellationToken ct = default,
        ObjectCapacityPolicy capacityPolicy = ObjectCapacityPolicy.SingleAccount)
    {
        if (!Enum.IsDefined(capacityPolicy))
            throw new ArgumentException("Invalid file capacity policy.", nameof(capacityPolicy));
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead) throw new ArgumentException("Readable stream required.", nameof(input));
        if (string.IsNullOrWhiteSpace(title) || title.Length > 256)
            throw new ArgumentException("Invalid file display title.", nameof(title));
        StellarLedgerFileProtocol.ValidateName(fileName);
        var scope = RequireScope();
        var local = await queue.ReviewAllAsync(scope.Account, scope.NetworkId, ct);
        var ids = new HashSet<string>(local.Select(x => x.EntityId),
            StringComparer.OrdinalIgnoreCase);
        var stagedMutationIds = new List<string>();
        var group = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        try
        {
            RequireSameScope(scope);
            if (local.Any(x => x.State is VaultSyncState.Submitting or
                    VaultSyncState.Uncertain or VaultSyncState.Conflict))
                throw new InvalidOperationException(
                    "Reconcile unresolved HD mutations before staging another file.");
            if (folderId is not null && (folderId.Length != 16 ||
                !folderId.All(Uri.IsHexDigit)))
                throw new InvalidDataException("Invalid parent folder ID.");
            // A pending folder is allowed, but its single dependency edge is
            // attached to the root using the original HD parent policy.
            VaultPendingMutation[] parent = folderId is null ? [] :
                VaultQueuedCreatePolicy.ParentCandidates(local, folderId);
            if (parent.Length > 1)
                throw new InvalidOperationException("Ambiguous local parent folder.");
            string[] rootDependencies = [];
            if (parent.Length == 1 && parent[0].State != VaultSyncState.Confirmed)
            {
                if (parent[0].Kind != VaultMutationKind.CreateFolder ||
                    parent[0].State != VaultSyncState.Pending)
                    throw new InvalidOperationException("Pending parent folder is unavailable.");
                rootDependencies = [parent[0].Id];
            }

            return await VaultStreamingFileTree.WriteContentAddressedAsync(input,
                fileName, contentType, async (node, token) =>
                {
                    RequireSameScope(scope);
                    token.ThrowIfCancellationRequested();
                    string recordId;
                    do { recordId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)); }
                    while (!ids.Add(recordId));
                    var isRoot = node.Kind == VaultStreamingFileTree.RootKind;
                    var draft = VaultLinkedTreeFilePolicy.Draft(node,
                        isRoot ? title : "FILE TREE NODE", group,
                        isRoot ? folderId : null);
                    if (!VaultLinkedTreeFilePolicy.IsTypedNode(draft))
                        throw new InvalidDataException("Invalid staged HD file tree node.");
                    var payload = JsonSerializer.SerializeToUtf8Bytes(draft);
                    try
                    {
                        var pending = VaultPendingMutation.Create(
                            scope.Account, scope.NetworkId,
                            VaultMutationKind.CreateRecord, recordId, payload,
                            dependsOn: isRoot ? rootDependencies : [])
                            with { CapacityPolicy = capacityPolicy };
                        try
                        {
                            await queue.EnqueueAsync(pending, token);
                            stagedMutationIds.Add(pending.Id);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(pending.EncryptedPayload);
                        }
                    }
                    finally { CryptographicOperations.ZeroMemory(payload); }
                    return recordId;
                }, VaultStreamingFileTree.ChunkBytes, ct);
        }
        catch
        {
            // An encrypted store can durably commit a node and then throw
            // cancellation BEFORE EnqueueAsync returns the new ID. Discover
            // that last node by this invocation's random group tag, rather
            // than relying only on the list of acknowledged mutation IDs.
            // Never touch confirmed, submitting or foreign-group mutations.
            bool cleanupFailed = false;
            var discovered = new List<string>();
            try
            {
                var snapshot = await queue.ReviewAllAsync(
                    scope.Account, scope.NetworkId, CancellationToken.None);
                try
                {
                    foreach (var entry in snapshot)
                        if (IsPendingTreeGroupNode(entry, group) &&
                            !stagedMutationIds.Contains(entry.Id, StringComparer.Ordinal))
                            discovered.Add(entry.Id);
                }
                finally
                {
                    foreach (var entry in snapshot)
                        CryptographicOperations.ZeroMemory(entry.EncryptedPayload);
                }
            }
            catch { cleanupFailed = true; }
            // Unacknowledged node was last to stage: discard it first. The
            // typed root is always emitted last and must be removed before
            // descendants to preserve the existing discard dependency guard.
            foreach (var mutationId in discovered.Concat(
                stagedMutationIds.AsEnumerable().Reverse()))
            {
                try
                {
                    await queue.DiscardPendingAsync(
                        scope.Account, scope.NetworkId, mutationId,
                        CancellationToken.None);
                }
                catch { cleanupFailed = true; }
            }
            if (cleanupFailed)
                throw new InvalidOperationException(
                    "Interrupted HD file staging left encrypted orphan drafts. Reopen and resolve the group; no transaction was sent.");
            throw;
        }
        finally
        {
            // ReviewAllAsync returns decrypted queue payload copies. Streaming
            // a file can take minutes; never retain those copies after staging
            // succeeds, fails, or rolls back.
            foreach (var entry in local)
                CryptographicOperations.ZeroMemory(entry.EncryptedPayload);
        }
    }

    private static bool IsPendingTreeGroupNode(
        VaultPendingMutation item, string group)
    {
        if (item.State != VaultSyncState.Pending ||
            item.Kind != VaultMutationKind.CreateRecord ||
            item.OriginStrategyId != VaultMutationStrategyPolicy.Hd ||
            item.TransactionHash is not null)
            return false;
        try
        {
            var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(
                item.EncryptedPayload);
            return draft is not null &&
                draft.Content?.StartsWith(VaultLinkedTreeFilePolicy.Prefix,
                    StringComparison.Ordinal) == true &&
                draft.Tags is [_, var tag] && tag == "svtree3-group:" + group;
        }
        catch (JsonException) { return false; }
    }

    public async Task<VaultPendingMutation> ReviseAsync(
        string mutationId, LedgerRecordDraft draft, string expectedPayloadSha256,
        CancellationToken ct = default)
    {
        var context = RequireScope();
        var account = context.Account;
        var scope = context.NetworkId;
        var entries = await queue.ReviewAllAsync(account, scope, ct);
        try
        {
            RequireSameScope(context);
            var current = entries.SingleOrDefault(x => x.Id == mutationId)
                ?? throw new KeyNotFoundException("Local draft does not exist.");
            if (current.State != VaultSyncState.Pending ||
                current.TransactionHash is not null ||
                current.Kind is not (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder))
                throw new InvalidOperationException("Only unpublished local creates can be revised.");
            if (expectedPayloadSha256 is not { Length: 64 } ||
                !expectedPayloadSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                !string.Equals(Convert.ToHexStringLower(
                    SHA256.HashData(current.EncryptedPayload)),
                    expectedPayloadSha256, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "This draft was modified in another tab. Reopen the latest version.");
            ValidateDraft(draft, current.Kind);
            var proposedDependencies = ParentDependencies(draft, entries, current.EntityId);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(draft);
            try
            {
                // The encrypted payload and dependency edge must be committed in
                // the SAME durable CAS revision; never leave the parent pointer and
                // queue dependency out of sync.
                RequireSameScope(context);
                return await queue.RevisePendingCreateAsync(
                    current, payload, proposedDependencies,
                    expectedPayloadSha256, ct);
            }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }
        finally { ClearReviewPayloads(entries); }
    }

    public async Task DiscardAsync(string mutationId, CancellationToken ct = default)
    {
        var context = RequireScope();
        var account = context.Account;
        var scope = context.NetworkId;
        await queue.DiscardPendingAsync(account, scope, mutationId, ct);
        RequireSameScope(context);
    }

    public async Task<IReadOnlyList<DraftItem>> ListAsync(CancellationToken ct = default)
    {
        var context = RequireScope();
        var account = context.Account;
        var scope = context.NetworkId;
        var entries = await queue.ReviewAllAsync(account, scope, ct);
        try
        {
            RequireSameScope(context);
            var result = new List<DraftItem>();
            foreach (var item in entries.Where(x =>
                x.Kind is VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder))
            {
                ct.ThrowIfCancellationRequested();
                LedgerRecordDraft draft;
                var corrupt = false;
                try
                {
                    draft = JsonSerializer.Deserialize<LedgerRecordDraft>(item.EncryptedPayload)
                        ?? throw new JsonException("Draft is empty.");
                    ValidateDraft(draft, item.Kind);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException
                                           or InvalidOperationException)
                {
                    // Keep the damaged mutation visible with its stable identity.
                    // Never offer an edit/parent action for untrusted draft content.
                    corrupt = true;
                    draft = new LedgerRecordDraft("UNREADABLE ENCRYPTED DRAFT", "");
                }
                result.Add(new(item.Id, item.EntityId, item.Kind,
                    item.State, draft, item.CreatedUtc,
                    Convert.ToHexStringLower(SHA256.HashData(item.EncryptedPayload)), corrupt));
            }
            RequireSameScope(context);
            return result.OrderBy(x => x.CreatedUtc).ToArray();
        }
        finally { ClearReviewPayloads(entries); }
    }

    private static void ClearReviewPayloads(IEnumerable<VaultPendingMutation> entries)
    {
        // ReviewAllAsync returns decrypted, disposable queue copies. The
        // durable encrypted store retains its own independent bytes.
        foreach (var entry in entries)
            CryptographicOperations.ZeroMemory(entry.EncryptedPayload);
    }

    private sealed record DraftScope(string Account, string NetworkId,
        long Epoch, string SelectedNetwork, string? CustomPassphrase,
        string CustomHorizonUrl);

    private DraftScope RequireScope()
    {
        retention.RequireLocalPersistence();
        if (!sessions.IsUnlocked || sessions.ActiveAccountId is not string account)
            throw new InvalidOperationException("Unlock the encrypted local vault.");
        return new(account, networks.CredentialKey(account), sessions.Epoch,
            networks.Network, networks.CustomPassphrase, networks.CustomHorizonUrl);
    }

    private void RequireSameScope(DraftScope scope)
    {
        retention.RequireLocalPersistence();
        if (!sessions.IsUnlocked || sessions.ActiveAccountId != scope.Account ||
            sessions.Epoch != scope.Epoch ||
            networks.CredentialKey(scope.Account) != scope.NetworkId ||
            networks.Network != scope.SelectedNetwork ||
            networks.CustomPassphrase != scope.CustomPassphrase ||
            networks.CustomHorizonUrl != scope.CustomHorizonUrl)
            throw new InvalidOperationException(
                "Vault session, account or network changed during the offline operation.");
    }

    private static void ValidateDraft(LedgerRecordDraft? draft, VaultMutationKind kind)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (kind is not (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder) ||
            draft.IsFolder != (kind == VaultMutationKind.CreateFolder) ||
            string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 256 ||
            string.IsNullOrEmpty(draft.Content) ||
            draft.Tags is { Length: > 24 } ||
            draft.Tags?.Any(x => x is null || x.Length > 64) == true ||
            draft.FolderId is { Length: > 0 } folder &&
            (folder.Length != 16 || !folder.All(Uri.IsHexDigit)))
            throw new ArgumentException("Offline record/folder draft is invalid.");
        if (draft.Content.StartsWith(StellarLedgerFileProtocol.SinglePrefix, StringComparison.Ordinal) ||
            draft.Content.StartsWith(StellarLedgerFileProtocol.ManifestPrefix, StringComparison.Ordinal) ||
            draft.Content.StartsWith(StellarLedgerFileProtocol.ChunkPrefix, StringComparison.Ordinal) ||
            draft.Content.StartsWith(VaultLinkedTreeFilePolicy.Prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("File protocol data requires staged file synchronization.");
    }

    private static string[] ParentDependencies(LedgerRecordDraft draft,
        IReadOnlyList<VaultPendingMutation> entries, string? editingEntityId)
    {
        if (string.IsNullOrWhiteSpace(draft.FolderId)) return [];
        if (editingEntityId is not null &&
            string.Equals(editingEntityId, draft.FolderId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Folder cannot be its own parent.");

        var parents = VaultQueuedCreatePolicy.ParentCandidates(entries, draft.FolderId);
        if (parents.Length > 1)
            throw new InvalidOperationException("Parent folder ID is ambiguous.");
        if (parents.Length == 0 || parents[0].State == VaultSyncState.Confirmed)
            return []; // Remote folder resolved by the ledger inventory at preflight.
        var parent = parents[0];
        if (parent.Kind != VaultMutationKind.CreateFolder ||
            parent.State != VaultSyncState.Pending ||
            parent.TransactionHash is not null)
            throw new InvalidOperationException(
                "Only a pending new local folder may be used as an unpublished parent.");
        if (editingEntityId is not null &&
            HasAncestorCycle(parent, editingEntityId, entries))
            throw new InvalidOperationException("Folder nesting would create a cycle.");
        return [parent.Id];
    }

    private static bool HasAncestorCycle(VaultPendingMutation start,
        string editingEntityId, IReadOnlyList<VaultPendingMutation> entries)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        VaultPendingMutation? current = start;
        while (current is not null)
        {
            if (!visited.Add(current.EntityId) ||
                string.Equals(current.EntityId, editingEntityId,
                    StringComparison.OrdinalIgnoreCase))
                return true;
            LedgerRecordDraft? folder;
            try { folder = JsonSerializer.Deserialize<LedgerRecordDraft>(current.EncryptedPayload); }
            catch (JsonException) { return true; }
            if (folder is null) return true;
            current = entries.FirstOrDefault(x => x.Kind == VaultMutationKind.CreateFolder &&
                x.State == VaultSyncState.Pending &&
                string.Equals(x.EntityId, folder.FolderId, StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }

    private static string NewUnusedRecordId(IReadOnlyList<VaultPendingMutation> entries)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            if (!entries.Any(x => x.EntityId.Equals(id, StringComparison.OrdinalIgnoreCase)))
                return id;
        }
        throw new InvalidOperationException("Could not allocate a distinct record identifier.");
    }
}
