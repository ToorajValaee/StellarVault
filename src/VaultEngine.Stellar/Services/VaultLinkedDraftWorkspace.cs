using VaultEngine.Core;
using System.Security.Cryptography;
using System.Text.Json;

namespace VaultEngine.Stellar.Services;

/// <summary>
/// Independent S-key record/folder *local staging* using the original encrypted
/// queue store and its atomic add/delete operations. Never signs or broadcasts.
/// The underlying key belongs to the connected S-key session, not the HD vault.
/// </summary>
public sealed class VaultLinkedDraftWorkspace(
    IStellarSecretKeyLedger ledger,
    VaultLinkedQueueKeySession keys,
    IVaultLinkedDraftStore store,
    VaultLinkedLocalRetentionGate linkedRetention,
    VaultNetworkSelection networks)
{
    public sealed record DraftItem(string MutationId, string EntityId,
        bool IsFolder, string Title, string? FolderId, VaultSyncState State,
        DateTimeOffset CreatedUtc, bool IsCorrupt, bool IsJournal = false,
        VaultMutationKind Kind = VaultMutationKind.CreateRecord, bool IsFile = false);

    public sealed record DraftDetail(string MutationId,
        LedgerRecordDraft Draft, string PayloadSha256);

    private sealed record Context(string Root, string Scope, long Epoch,
        long UnlockEpoch);

    private Context RequireScope()
    {
        linkedRetention.DeviceRetention.RequireLocalPersistence();
        linkedRetention.RequireLocalPersistence();
        if (!keys.IsUnlocked || keys.ActiveRootAccountId is not string root ||
            ledger.IsConnected && ledger.RootAccountId != root)
            throw new UnauthorizedAccessException(
                "Unlock the matching independent S-key encrypted local queue.");
        StellarAccountAddress.Validate(root);
        return new(root, networks.CredentialKey(root),
            keys.IsOfflineOnly ? -1 : ledger.ConnectionEpoch, keys.UnlockEpoch);
    }

    private void Check(Context original)
    {
        linkedRetention.DeviceRetention.RequireLocalPersistence();
        linkedRetention.RequireLocalPersistence();
        if (!keys.IsUnlocked || keys.ActiveRootAccountId != original.Root ||
            keys.UnlockEpoch != original.UnlockEpoch ||
            (original.Epoch == -1
                ? !keys.IsOfflineOnly
                : keys.IsOfflineOnly || !ledger.IsConnected ||
                  ledger.RootAccountId != original.Root ||
                  ledger.ConnectionEpoch != original.Epoch) ||
            networks.CredentialKey(original.Root) != original.Scope)
            throw new InvalidOperationException(
                "Independent S-key session, consent or selected network changed.");
    }

    private async Task<IReadOnlyList<VaultPendingMutation>> ReadEntriesAsync(
        Context c, CancellationToken ct)
    {
        var all = await store.ListAsync(c.Root, c.Scope, ct);
        try
        {
            Check(c);
            if (all.Any(x => x.AccountId != c.Root ||
                x.NetworkId != VaultLinkedQueueStorageScope.FromNetworkScope(c.Scope) ||
                VaultMutationStrategyPolicy.Resolve(x.OriginStrategyId) !=
                    VaultMutationStrategyPolicy.Linked))
                throw new InvalidDataException(
                    "Encrypted queue contains a foreign linked-account draft.");
            return all;
        }
        catch
        {
            ClearEntries(all);
            throw;
        }
    }

    private static void ClearEntries(IEnumerable<VaultPendingMutation> entries)
    {
        foreach (var x in entries)
            if (x.EncryptedPayload is not null)
                CryptographicOperations.ZeroMemory(x.EncryptedPayload);
    }

    public Task<DraftItem> CreateAsync(LedgerRecordDraft draft,
        CancellationToken ct = default) =>
        CreateCoreAsync(draft, allowSingleFile: false, ct, ObjectCapacityPolicy.SingleAccount);

    public Task<DraftItem> CreateAsync(LedgerRecordDraft draft,
        ObjectCapacityPolicy capacityPolicy, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(capacityPolicy))
            throw new ArgumentException("Unsupported capacity policy.", nameof(capacityPolicy));
        return CreateCoreAsync(draft, allowSingleFile: false, ct, capacityPolicy);
    }

    public Task<DraftItem> StageSmallFileAsync(
        string title, string fileName, string contentType,
        ReadOnlyMemory<byte> file, string? parentFolderId = null,
        CancellationToken ct = default)
    {
        var draft = VaultLinkedSingleFilePolicy.BuildDraft(
            title, fileName, contentType, file.Span, parentFolderId);
        return CreateCoreAsync(draft, allowSingleFile: true, ct, ObjectCapacityPolicy.SingleAccount);
    }

    private static bool IsPendingTreeGroupNode(
        VaultPendingMutation entry, string groupId)
    {
        if (entry.Kind != VaultMutationKind.CreateRecord ||
            entry.State != VaultSyncState.Pending ||
            entry.OriginStrategyId != VaultMutationStrategyPolicy.Linked ||
            entry.TransactionHash is not null)
            return false;
        try
        {
            var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(
                entry.EncryptedPayload);
            return draft is not null &&
                draft.Content?.StartsWith(VaultLinkedTreeFilePolicy.Prefix,
                    StringComparison.Ordinal) == true &&
                draft.Tags is [_, var groupTag] &&
                groupTag == "svtree3-group:" + groupId;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// Stream a file of any logical length into the existing encrypted S-key
    /// queue as bounded SVTREE3 ledger records. All child nodes are durably
    /// staged before the root is created. No fee is approved and nothing is
    /// signed or transmitted here. Each node requires later independent
    /// approval and historical reconciliation through the standard publisher.
    /// </summary>
    public async Task<VaultStreamingFileTree.Result> StageTreeFileAsync(
        string title, string fileName, string contentType,
        Stream input, string? parentFolderId = null, CancellationToken ct = default,
        ObjectCapacityPolicy capacityPolicy = ObjectCapacityPolicy.SingleAccount)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead) throw new ArgumentException("A readable file stream is required.", nameof(input));
        if (!Enum.IsDefined(capacityPolicy))
            throw new ArgumentException("Invalid file capacity policy.", nameof(capacityPolicy));
        if (string.IsNullOrWhiteSpace(title) || title.Length > 256)
            throw new ArgumentException("Invalid linked file title.", nameof(title));
        if (parentFolderId is { Length: > 0 } parent &&
            (parent.Length != 16 || !parent.All(Uri.IsHexDigit)))
            throw new ArgumentException("Invalid linked file folder.", nameof(parentFolderId));
        var context = RequireScope();
        var local = await ReadEntriesAsync(context, ct);
        var createdIds = new HashSet<string>(StringComparer.Ordinal);
        var usedIds = new HashSet<string>(local.Select(x => x.EntityId), StringComparer.OrdinalIgnoreCase);
        var groupId = Guid.NewGuid().ToString("N")[..16];
        try
        {
            if (local.Any(x => x.State is VaultSyncState.Submitting or
                    VaultSyncState.Uncertain or VaultSyncState.Conflict ||
                    (x.Kind == VaultMutationKind.ExtendLinkedChain &&
                     x.State != VaultSyncState.Confirmed)))
                throw new InvalidOperationException(
                    "Resolve uncertain S-key transactions before staging another file.");
            var remote = keys.IsOfflineOnly
                ? Array.Empty<LedgerRecordItem>() : (await ledger.ReadAsync(ct)).ToArray();
            Check(context);
            foreach (var item in remote) usedIds.Add(item.Id);
            string[] parentDependencies = [];
            if (parentFolderId is { Length: > 0 })
            {
                var parents = VaultQueuedCreatePolicy.ParentCandidates(local, parentFolderId);
                if (parents.Length > 1)
                    throw new InvalidOperationException("Linked file parent folder is ambiguous.");
                if (parents.Length == 1)
                {
                    var folder = parents[0];
                    if (folder.Kind != VaultMutationKind.CreateFolder ||
                        folder.State is not (VaultSyncState.Pending or VaultSyncState.Confirmed))
                        throw new InvalidOperationException("Linked file parent folder is unavailable.");
                    if (folder.State == VaultSyncState.Pending)
                        parentDependencies = [folder.Id];
                }
                else if (!keys.IsOfflineOnly &&
                    !remote.Any(x => x.Id == parentFolderId && x.IsFolder))
                    throw new InvalidOperationException("Linked file parent folder does not exist.");
            }

            return await VaultStreamingFileTree.WriteAsync(input, fileName, contentType,
                async (node, token) =>
                {
                    Check(context);
                    token.ThrowIfCancellationRequested();
                    if (!usedIds.Add(node.Id))
                        throw new InvalidOperationException(
                            "Streaming file tree node ID collided with an existing ledger record.");
                    var isRoot = node.Kind == VaultStreamingFileTree.RootKind;
                    var draft = VaultLinkedTreeFilePolicy.Draft(node,
                        isRoot ? title : "FILE TREE NODE", groupId,
                        isRoot ? parentFolderId : null);
                    if (!VaultLinkedTreeFilePolicy.IsTypedNode(draft))
                        throw new InvalidDataException("Streaming file tree node failed validation.");
                    var payload = JsonSerializer.SerializeToUtf8Bytes(draft);
                    try
                    {
                        var staged = VaultPendingMutation.Create(context.Root,
                            VaultLinkedQueueStorageScope.FromNetworkScope(context.Scope),
                            VaultMutationKind.CreateRecord, node.Id, payload,
                            dependsOn: isRoot ? parentDependencies : [])
                            with { OriginStrategyId = VaultMutationStrategyPolicy.Linked,
                                CapacityPolicy = capacityPolicy };
                        try
                        {
                            if (!await store.AddAsync(staged, token))
                                throw new InvalidOperationException(
                                    "Concurrent encrypted queue modification during file staging.");
                            createdIds.Add(staged.Id);
                        }
                        finally { CryptographicOperations.ZeroMemory(staged.EncryptedPayload); }
                    }
                    finally { CryptographicOperations.ZeroMemory(payload); }
                }, ct);
        }
        catch
        {
            // Fail closed: rollback all unsent nodes from this staging attempt.
            // Only short mutation IDs are held during normal streaming, never
            // a whole-file list of plaintext/ciphertext payloads.
            var cleanupFailed = false;
            try
            {
                var entries = await store.ListAsync(context.Root, context.Scope,
                    CancellationToken.None);
                try
                {
                    foreach (var entry in entries.Reverse())
                    {
                        // Recover an encrypted commit that succeeded before
                        // AddAsync could acknowledge the mutation ID.
                        var recognized = createdIds.Contains(entry.Id);
                        var belongsToThisFile = IsPendingTreeGroupNode(entry, groupId);
                        if (!recognized && !belongsToThisFile)
                            continue;
                        // An acknowledged ID whose encrypted contents changed
                        // is no longer this upload's exact draft. Preserve it.
                        // A concurrent tab may also have moved the journal
                        // beyond Pending. NEVER delete an attempted transaction.
                        if (!belongsToThisFile ||
                            entry.SignedFileEntries is { Length: > 0 } ||
                            entry.SignedDeletionManifests is { Length: > 0 } ||
                            !await store.TryDeleteAsync(entry, CancellationToken.None))
                            cleanupFailed = true;
                    }
                }
                finally { ClearEntries(entries); }
            }
            catch { cleanupFailed = true; }
            if (cleanupFailed)
                throw new InvalidOperationException(
                    "Tree file staging was interrupted and orphan encrypted drafts remain. Do not publish them; inspect and discard before retrying.");
            throw;
        }
        finally { ClearEntries(local); }
    }

    /// <summary>
    /// Stages a bounded, multipart SVFILE2 group. Parts are independent
    /// encrypted queue entries; the final manifest is staged LAST. This
    /// method never signs or submits and rejects overlapping unresolved writes.
    /// Confirmation of each part is required before the next part can be
    /// fee-reviewed, and all remote bytes must match before manifest review.
    /// </summary>
    public async Task<IReadOnlyList<DraftItem>> StageMultipartFileAsync(
        string title, string fileName, string contentType,
        ReadOnlyMemory<byte> file, string? parentFolderId = null,
        CancellationToken ct = default)
    {
        var plan = VaultLinkedMultipartFilePolicy.BuildUpload(
            title, fileName, contentType, file.Span, parentFolderId);
        if (parentFolderId is { Length: > 0 } parent &&
            (parent.Length != 16 || !parent.All(Uri.IsHexDigit)))
            throw new ArgumentException("Invalid linked multipart parent folder.", nameof(parentFolderId));
        var context = RequireScope();
        var local = await ReadEntriesAsync(context, ct);
        var created = new List<VaultPendingMutation>(plan.Count);
        var pending = new List<VaultPendingMutation>(plan.Count);
        try
        {
            if (local.Any(x => x.State is VaultSyncState.Submitting or
                    VaultSyncState.Uncertain or VaultSyncState.Conflict ||
                    (x.Kind == VaultMutationKind.ExtendLinkedChain &&
                     x.State != VaultSyncState.Confirmed)))
                throw new InvalidOperationException(
                    "Reconcile all unresolved S-key transactions before staging a multipart file.");

            IReadOnlyList<LedgerRecordItem> remote = keys.IsOfflineOnly
                ? [] : await ledger.ReadAsync(ct);
            Check(context);
            if (plan.Any(x =>
                local.Any(y => y.EntityId == x.Id) ||
                remote.Any(y => y.Id == x.Id)))
                throw new InvalidOperationException(
                    "A generated linked file record identifier already exists.");
            string[] parentDependencies = [];
            if (parentFolderId is { Length: > 0 })
            {
                var parents = VaultQueuedCreatePolicy.ParentCandidates(
                    local, parentFolderId);
                if (parents.Length > 1)
                    throw new InvalidOperationException("Multipart file parent folder is ambiguous.");
                if (parents.Length == 1)
                {
                    var localParent = parents[0];
                    if (localParent.Kind != VaultMutationKind.CreateFolder ||
                        localParent.State is not (VaultSyncState.Pending or VaultSyncState.Confirmed))
                        throw new InvalidOperationException(
                            "Selected local folder is not available.");
                    if (localParent.State == VaultSyncState.Pending)
                        parentDependencies = [localParent.Id];
                }
                else if (!keys.IsOfflineOnly &&
                    !remote.Any(x => x.Id == parentFolderId && x.IsFolder))
                    throw new InvalidOperationException(
                        "The file's parent folder is not published.");
            }

            for (int i = 0; i < plan.Count; i++)
            {
                var step = plan[i];
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(step.Draft);
                try
                {
                    var item = VaultPendingMutation.Create(context.Root,
                        VaultLinkedQueueStorageScope.FromNetworkScope(context.Scope),
                        VaultMutationKind.CreateRecord, step.Id, payload,
                        dependsOn: i == plan.Count - 1 ? parentDependencies : [])
                        with { OriginStrategyId = VaultMutationStrategyPolicy.Linked };
                    pending.Add(item);
                }
                finally { CryptographicOperations.ZeroMemory(payload); }
            }
            // The manifest is the final queue entry. A crash during local
            // staging may leave only pending parts; rollback below removes
            // anything written in this invocation without on-chain effects.
            foreach (var item in pending)
            {
                Check(context);
                ct.ThrowIfCancellationRequested();
                if (!await store.AddAsync(item, ct))
                    throw new InvalidOperationException(
                        "Encrypted multipart staging collided with another device change.");
                created.Add(item);
            }
            return pending.Select((item, index) => new DraftItem(
                item.Id, item.EntityId, false, plan[index].Draft.Title,
                plan[index].Draft.FolderId, VaultSyncState.Pending,
                item.CreatedUtc, false, false, item.Kind, true)).ToArray();
        }
        catch
        {
            var cleanupFailed = false;
            foreach (var item in created.AsEnumerable().Reverse())
            {
                try
                {
                    if (!await store.TryDeleteAsync(item, CancellationToken.None))
                        cleanupFailed = true;
                }
                catch { cleanupFailed = true; }
            }
            if (cleanupFailed)
                throw new InvalidOperationException(
                    "Multipart staging was interrupted; inspect and discard any unsent encrypted parts before retrying. Never publish an incomplete manifest.");
            throw;
        }
        finally
        {
            foreach (var item in pending)
                CryptographicOperations.ZeroMemory(item.EncryptedPayload);
            ClearEntries(local);
        }
    }

    private async Task<DraftItem> CreateCoreAsync(LedgerRecordDraft draft,
        bool allowSingleFile, CancellationToken ct, ObjectCapacityPolicy capacityPolicy)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (allowSingleFile)
        {
            if (!VaultLinkedSingleFilePolicy.IsSupportedDraft(draft))
                throw new InvalidDataException("Invalid S-key single-file draft.");
        }
        else ValidateDraft(draft);
        var context = RequireScope();
        var local = await ReadEntriesAsync(context, ct);
        byte[]? payload = null;
        try
        {
            // Do not mistake a pending local parent for an already published
            // parent. The dependency edge is stored atomically with the draft.
            // Existing queue credentials can be unlocked with the separate
            // passphrase while Horizon is unavailable. Remote IDs/parents are
            // verified again before any later manual publication.
            IReadOnlyList<LedgerRecordItem> remote = keys.IsOfflineOnly
                ? []
                : await ledger.ReadAsync(ct);
            Check(context);
            var folderId = draft.FolderId;
            string[] deps = [];
            if (!string.IsNullOrWhiteSpace(folderId))
            {
                var parents = VaultQueuedCreatePolicy.ParentCandidates(local, folderId);
                if (parents.Length > 1)
                    throw new InvalidOperationException("The local parent folder is ambiguous.");
                if (parents.Length == 1)
                {
                    var parent = parents[0];
                    if (parent.Kind != VaultMutationKind.CreateFolder ||
                        parent.State is not (VaultSyncState.Pending or VaultSyncState.Confirmed))
                        throw new InvalidOperationException("The local parent folder is unavailable.");
                    if (parent.State == VaultSyncState.Pending) deps = [parent.Id];
                }
                else if (!keys.IsOfflineOnly &&
                         !remote.Any(x => x.Id == folderId && x.IsFolder))
                    throw new InvalidOperationException(
                        "Parent folder does not exist on Stellar or in pending local folders.");
                // In offline mode an unknown remote parent is merely a claimed
                // parent ID. Publication must verify that it exists and is a
                // folder on the connected ledger before broadcasting.
            }

            string entityId;
            do
            {
                entityId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            } while (local.Any(x => x.EntityId == entityId) ||
                     remote.Any(x => x.Id == entityId));

            payload = JsonSerializer.SerializeToUtf8Bytes(draft);
            var kind = draft.IsFolder
                ? VaultMutationKind.CreateFolder : VaultMutationKind.CreateRecord;
            var item = VaultPendingMutation.Create(context.Root,
                VaultLinkedQueueStorageScope.FromNetworkScope(context.Scope),
                kind, entityId, payload, dependsOn: deps)
                with { OriginStrategyId = VaultMutationStrategyPolicy.Linked,
                    CapacityPolicy = capacityPolicy };
            try
            {
                Check(context);
                if (!await store.AddAsync(item, ct))
                    throw new InvalidOperationException(
                        "The draft changed concurrently; refresh local drafts.");
                return new(item.Id, item.EntityId, draft.IsFolder,
                    draft.Title, draft.FolderId, item.State, item.CreatedUtc, false, false, kind);
            }
            finally { CryptographicOperations.ZeroMemory(item.EncryptedPayload); }
        }
        finally
        {
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            ClearEntries(local);
        }
    }

    /// <summary>
    /// Stage an existing remote S-key record update with the exact original
    /// manifest revision. No signing/network mutations occur here.
    /// </summary>
    public Task<DraftItem> StageUpdateAsync(string entityId,
        LedgerRecordDraft draft, string expectedRemoteRevision,
        CancellationToken ct = default) =>
        StageRemoteMutationAsync(entityId, draft, expectedRemoteRevision, false, ct);

    public Task<DraftItem> StageUpdateAsync(string entityId,
        LedgerRecordDraft draft, string expectedRemoteRevision,
        ObjectCapacityPolicy capacityPolicy, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(capacityPolicy))
            throw new ArgumentException("Unknown update capacity policy.", nameof(capacityPolicy));
        return StageRemoteMutationAsync(entityId, draft, expectedRemoteRevision,
            false, ct, capacityPolicy);
    }

    public Task<DraftItem> StageDeleteAsync(string entityId,
        string expectedRemoteRevision, CancellationToken ct = default) =>
        StageRemoteMutationAsync(entityId, null, expectedRemoteRevision, true, ct);

    private async Task<DraftItem> StageRemoteMutationAsync(string entityId,
        LedgerRecordDraft? proposed, string expectedRevision, bool delete,
        CancellationToken ct,
        ObjectCapacityPolicy capacityPolicy = ObjectCapacityPolicy.SingleAccount)
    {
        if (entityId is not { Length: 16 } ||
            !entityId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !VaultLinkedRemoteRevisionPolicy.IsValid(expectedRevision))
            throw new ArgumentException("A valid remote S-key record revision is required.");
        if (ledger is not IStellarSecretKeyRecordRevisionReader revisionReader)
            throw new NotSupportedException("This S-key host cannot verify remote revision identity.");
        var context = RequireScope();
        if (keys.IsOfflineOnly || !keys.CanPublish)
            throw new InvalidOperationException(
                "An online matching S-key signer is required to capture the existing record revision.");

        var all = await ReadEntriesAsync(context, ct);
        byte[]? payload = null;
        try
        {
            if (all.Any(x => x.EntityId == entityId &&
                x.State is not (VaultSyncState.Confirmed or VaultSyncState.Failed)) ||
                all.Any(x => x.State is VaultSyncState.Uncertain or
                    VaultSyncState.Submitting or VaultSyncState.Conflict ||
                    (x.Kind == VaultMutationKind.ExtendLinkedChain &&
                     x.State != VaultSyncState.Confirmed)))
                throw new InvalidOperationException(
                    "An unresolved or overlapping S-key mutation must be reconciled first.");
            var beforeRevision = await revisionReader.ReadRecordRevisionAsync(entityId, ct);
            Check(context);
            if (beforeRevision != expectedRevision)
                throw new InvalidOperationException(
                    "Published record changed before staging. Reload its current revision.");
            var remote = await ledger.ReadAsync(ct);
            Check(context);
            var original = remote.SingleOrDefault(x => x.Id == entityId)
                ?? throw new InvalidOperationException("Published record no longer exists.");
            var currentRevision = await revisionReader.ReadRecordRevisionAsync(entityId, ct);
            Check(context);
            if (currentRevision != expectedRevision)
                throw new InvalidOperationException(
                    "Published record changed during the snapshot read. Reload before editing or deleting.");

            // Tree files are immutable groups of individually published
            // ledger records. Editing/deleting one node would strand the root
            // or silently corrupt its authenticated descendants.
            if (original.Content?.StartsWith(VaultLinkedTreeFilePolicy.Prefix,
                    StringComparison.Ordinal) == true)
                throw new NotSupportedException(
                    "Tree file updates and deletion require a separately verified whole-tree operation.");

            LedgerRecordDraft draft;
            VaultMutationKind kind;
            if (delete)
            {
                if (remote.Any(x => x.FolderId == entityId))
                    throw new InvalidOperationException(
                        "Delete the published child records and folders before deleting their parent.");
                if (all.Any(x =>
                {
                    if (x.State != VaultSyncState.Pending ||
                        x.Kind == VaultMutationKind.ExtendLinkedChain) return false;
                    var dependent = JsonSerializer.Deserialize<LedgerRecordDraft>(x.EncryptedPayload)
                        ?? throw new InvalidDataException("A staged linked draft cannot be decoded.");
                    return dependent.FolderId == entityId;
                }))
                    throw new InvalidOperationException(
                        "A pending local record still references this published folder. Resolve it before staging deletion.");
                draft = new LedgerRecordDraft(original.Title, original.Content ?? throw new InvalidDataException("Published linked record content is missing."),
                    original.FolderId, original.Tags, original.IsFolder,
                    original.UserName, original.Url, original.Notes);
                // A deletion is immutable once queued. Reject unsupported or
                // malformed file formats BEFORE sealing the original snapshot,
                // otherwise later queue inspection would render a durable
                // Pending entry corrupt with no safe publication path.
                if (draft.Content is null)
                    throw new InvalidDataException("Published linked record content is missing.");
                if (draft.Content.StartsWith(StellarLedgerFileProtocol.SinglePrefix,
                        StringComparison.Ordinal))
                {
                    if (!VaultLinkedSingleFilePolicy.IsSupportedDraft(draft))
                        throw new InvalidDataException(
                            "Cannot stage deletion of a malformed or legacy unchecksummed S-key file.");
                }
                else if (draft.Content.StartsWith(StellarLedgerFileProtocol.ManifestPrefix,
                             StringComparison.Ordinal) ||
                         draft.Content.StartsWith(StellarLedgerFileProtocol.ChunkPrefix,
                             StringComparison.Ordinal))
                    throw new NotSupportedException(
                        "Multipart S-key file deletion requires the dedicated verified multipart workflow.");
                kind = original.IsFolder
                    ? VaultMutationKind.DeleteFolder : VaultMutationKind.DeleteRecord;
            }
            else
            {
                draft = proposed ?? throw new ArgumentNullException(nameof(proposed));
                ValidateDraft(draft);
                if (draft.IsFolder != original.IsFolder)
                    throw new InvalidOperationException("Record type cannot change during an update.");
                VaultLinkedFolderHierarchyPolicy.RequirePublishedAcyclicParent(
                    entityId, draft.FolderId, remote);
                if (original.IsFolder && original.FolderId != draft.FolderId &&
                    remote.Any(x => x.FolderId == entityId) &&
                    draft.FolderId is not null)
                    throw new InvalidOperationException(
                        "Move a folder with published children only through an independently verified tree update.");
                kind = original.IsFolder
                    ? VaultMutationKind.UpdateFolder : VaultMutationKind.UpdateRecord;
            }
            payload = JsonSerializer.SerializeToUtf8Bytes(draft);
            var mutation = VaultPendingMutation.Create(context.Root,
                VaultLinkedQueueStorageScope.FromNetworkScope(context.Scope),
                kind, entityId, payload, expectedRemoteVersion: expectedRevision)
                with { OriginStrategyId = VaultMutationStrategyPolicy.Linked,
                    CapacityPolicy = capacityPolicy };
            try
            {
                Check(context);
                if (!await store.AddAsync(mutation, ct))
                    throw new InvalidOperationException("Encrypted mutation was changed concurrently.");
                return new(mutation.Id, entityId, original.IsFolder, draft.Title,
                    draft.FolderId, mutation.State, mutation.CreatedUtc, false, false, kind);
            }
            finally { CryptographicOperations.ZeroMemory(mutation.EncryptedPayload); }
        }
        finally
        {
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            ClearEntries(all);
        }
    }

    /// <summary>
    /// Explicit user action to decrypt one unpublished draft for editing.
    /// This never alters the queue or supplies signing authorization.
    /// </summary>
    public async Task<DraftDetail> OpenDraftAsync(string mutationId,
        CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(mutationId, "N", out _))
            throw new ArgumentException("Invalid draft ID.", nameof(mutationId));
        var context = RequireScope();
        var entries = await ReadEntriesAsync(context, ct);
        try
        {
            var current = entries.SingleOrDefault(x => x.Id == mutationId)
                ?? throw new KeyNotFoundException("Local draft does not exist.");
            if (current.State != VaultSyncState.Pending ||
                current.TransactionHash is not null ||
                current.Kind is not (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder or
                    VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder))
                throw new InvalidOperationException("Only unpublished local drafts are editable.");
            var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(current.EncryptedPayload)
                ?? throw new InvalidDataException("Encrypted draft cannot be decoded.");
            ValidateDraft(draft);
            if (draft.IsFolder != (current.Kind is VaultMutationKind.CreateFolder or VaultMutationKind.UpdateFolder))
                throw new InvalidDataException("Encrypted draft kind is inconsistent.");
            Check(context);
            return new(current.Id, draft, Convert.ToHexStringLower(
                SHA256.HashData(current.EncryptedPayload)));
        }
        finally { ClearEntries(entries); }
    }

    /// <summary>
    /// Atomically revise an unpublished create without changing its record ID,
    /// origin or queue state. A parent change and its dependency edge are
    /// committed together. A stale editor fingerprint always fails closed.
    /// </summary>
    public async Task<DraftItem> ReviseAsync(
        string mutationId, LedgerRecordDraft draft, string expectedPayloadSha256,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);
        if (!Guid.TryParseExact(mutationId, "N", out _) ||
            expectedPayloadSha256 is not { Length: 64 } ||
            !expectedPayloadSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("Invalid local draft revision.");
        var context = RequireScope();
        var all = await ReadEntriesAsync(context, ct);
        byte[]? payload = null;
        try
        {
            var current = all.SingleOrDefault(x => x.Id == mutationId)
                ?? throw new KeyNotFoundException("Local draft does not exist.");
            if (current.State != VaultSyncState.Pending ||
                current.TransactionHash is not null ||
                current.SignedFileEntries is { Length: > 0 } ||
                current.Kind is not (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder or
                    VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder) ||
                draft.IsFolder != (current.Kind is VaultMutationKind.CreateFolder or VaultMutationKind.UpdateFolder))
                throw new InvalidOperationException("Only unpublished drafts of the same type may be revised.");
            var actual = SHA256.HashData(current.EncryptedPayload);
            try
            {
                var expected = Convert.FromHexString(expectedPayloadSha256);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                        throw new InvalidOperationException(
                            "This draft was edited elsewhere; reopen its latest revision.");
                }
                finally { CryptographicOperations.ZeroMemory(expected); }
            }
            finally { CryptographicOperations.ZeroMemory(actual); }

            string[] dependencies = [];
            if (draft.FolderId is { Length: > 0 } parentId)
            {
                if (string.Equals(parentId, current.EntityId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A folder cannot contain itself.");
                var parents = VaultQueuedCreatePolicy.ParentCandidates(all, parentId, current.Id);
                if (parents.Length > 1)
                    throw new InvalidOperationException("The parent folder is ambiguous.");
                if (parents.Length == 1)
                {
                    var parent = parents[0];
                    if (parent.Kind != VaultMutationKind.CreateFolder ||
                        parent.State is not (VaultSyncState.Pending or VaultSyncState.Confirmed))
                        throw new InvalidOperationException("The selected parent folder is unavailable.");
                    if (parent.State == VaultSyncState.Pending)
                    {
                        var byId = all.ToDictionary(x => x.Id, StringComparer.Ordinal);
                        var seen = new HashSet<string>(StringComparer.Ordinal) { current.Id };
                        VaultPendingMutation? walker = parent;
                        while (walker is not null)
                        {
                            if (!seen.Add(walker.Id) || walker.DependsOn is not { Length: <= 1 })
                                throw new InvalidOperationException(
                                    "The proposed folder move creates a dependency cycle.");
                            walker = walker.DependsOn.Length == 1 &&
                                byId.TryGetValue(walker.DependsOn[0], out var ancestor)
                                ? ancestor : null;
                        }
                        dependencies = [parent.Id];
                    }
                }
                else if (!keys.IsOfflineOnly)
                {
                    var remote = await ledger.ReadAsync(ct);
                    Check(context);
                    if (!remote.Any(x => x.Id == parentId && x.IsFolder))
                        throw new InvalidOperationException(
                            "The selected parent folder is absent on Stellar.");
                }
                // Offline remote parent references remain unverified until the
                // later online publication preflight.
            }

            if (current.Kind is VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder)
            {
                if (dependencies.Length != 0)
                    throw new InvalidOperationException(
                        "A published record update cannot depend on an unpublished local folder.");
                if (!keys.IsOfflineOnly)
                {
                    var remote = await ledger.ReadAsync(ct);
                    Check(context);
                    VaultLinkedFolderHierarchyPolicy.RequirePublishedAcyclicParent(
                        current.EntityId, draft.FolderId, remote);
                }
            }

            payload = JsonSerializer.SerializeToUtf8Bytes(draft);
            var replacement = current with
            {
                EncryptedPayload = payload,
                DependsOn = dependencies
            };
            Check(context);
            if (!await store.TryReplaceAsync(current, replacement, ct))
                throw new InvalidOperationException(
                    "Local draft changed concurrently. Reopen its latest revision.");
            return new(current.Id, current.EntityId, draft.IsFolder, draft.Title,
                draft.FolderId, VaultSyncState.Pending, current.CreatedUtc, false,
                false, current.Kind);
        }
        finally
        {
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            ClearEntries(all);
        }
    }

    /// <summary>
    /// One authenticated encrypted queue snapshot drives both dependency
    /// ordering and the UI's next-review suggestion. This does not publish.
    /// </summary>
    public async Task<VaultLinkedManualSyncPlan.Review> ReviewManualSyncAsync(
        CancellationToken ct = default)
    {
        var context = RequireScope();
        var all = await ReadEntriesAsync(context, ct);
        try
        {
            Check(context);
            return VaultLinkedManualSyncPlan.Build(all, keys.CanPublish);
        }
        finally { ClearEntries(all); }
    }

    public async Task<IReadOnlyList<DraftItem>> ListAsync(CancellationToken ct = default)
    {
        var context = RequireScope();
        var items = await ReadEntriesAsync(context, ct);
        try
        {
            var result = new List<DraftItem>();
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (item.Kind == VaultMutationKind.ExtendLinkedChain)
                {
                    // The journal is a public-only account-expansion intent,
                    // not an encrypted record or an editable local folder.
                    result.Add(new(item.Id, item.EntityId, false,
                        "Linked account capacity expansion", null,
                        item.State, item.CreatedUtc, false, true, item.Kind));
                    continue;
                }
                try
                {
                    var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(item.EncryptedPayload)
                        ?? throw new InvalidDataException("Missing encrypted local draft.");
                    // A damaged authenticated envelope can still deserialize with
                    // absent content. Show it as corrupt, never crash the queue UI.
                    if (draft.Content is null)
                        throw new InvalidDataException("Linked encrypted draft content is missing.");
                    if (draft.Content.StartsWith(StellarLedgerFileProtocol.SinglePrefix,
                            StringComparison.Ordinal))
                    {
                        // A published SVFILE1 can be deleted using the exact
                        // original encrypted record snapshot. File edits remain
                        // prohibited: only typed creates and pinned deletes
                        // may carry this protocol in the linked queue.
                        if (!VaultLinkedSingleFilePolicy.IsSupportedDraft(draft) ||
                            item.Kind is not (VaultMutationKind.CreateRecord or
                                VaultMutationKind.DeleteRecord))
                            throw new InvalidDataException("Corrupt linked encrypted file draft.");
                    }
                    else if (draft.Content.StartsWith(StellarLedgerFileProtocol.ManifestPrefix,
                                 StringComparison.Ordinal))
                    {
                        if (item.Kind != VaultMutationKind.CreateRecord ||
                            !VaultLinkedMultipartFilePolicy.IsManifest(draft))
                            throw new InvalidDataException("Invalid linked multipart file manifest.");
                    }
                    else if (draft.Content.StartsWith(StellarLedgerFileProtocol.ChunkPrefix,
                                 StringComparison.Ordinal))
                    {
                        if (item.Kind != VaultMutationKind.CreateRecord)
                            throw new InvalidDataException("Invalid multipart file part mutation.");
                        var part = JsonSerializer.Deserialize<StellarLedgerFileProtocol.Part>(
                            draft.Content[StellarLedgerFileProtocol.ChunkPrefix.Length..]);
                        if (part is null || !VaultLinkedMultipartFilePolicy.IsPart(
                                draft, part.FileId, part.Index))
                            throw new InvalidDataException("Invalid encrypted multipart part.");
                    }
                    else if (draft.Content.StartsWith(VaultLinkedTreeFilePolicy.Prefix,
                                 StringComparison.Ordinal))
                    {
                        if (item.Kind != VaultMutationKind.CreateRecord ||
                            !VaultLinkedTreeFilePolicy.IsTypedNode(draft))
                            throw new InvalidDataException("Invalid linked file tree node.");
                    }
                    else ValidateDraft(draft);
                    if (draft.IsFolder != (item.Kind is VaultMutationKind.CreateFolder or
                        VaultMutationKind.UpdateFolder or VaultMutationKind.DeleteFolder))
                        throw new InvalidDataException("Encrypted draft type mismatch.");
                    result.Add(new(item.Id, item.EntityId, draft.IsFolder,
                        draft.Title, draft.FolderId, item.State, item.CreatedUtc, false, false, item.Kind,
                        (draft.Content.StartsWith(StellarLedgerFileProtocol.SinglePrefix, StringComparison.Ordinal) ||
                         VaultLinkedMultipartFilePolicy.IsTypedFileDraft(draft))));
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or
                                           ArgumentException or InvalidOperationException)
                {
                    result.Add(new(item.Id, item.EntityId,
                        item.Kind is VaultMutationKind.CreateFolder or
                            VaultMutationKind.UpdateFolder or VaultMutationKind.DeleteFolder,
                        "UNREADABLE ENCRYPTED DRAFT", null,
                        item.State, item.CreatedUtc, true, false, item.Kind));
                }
            }
            Check(context);
            return result.OrderBy(x => x.CreatedUtc).ToArray();
        }
        finally { ClearEntries(items); }
    }

    public async Task DiscardAsync(string mutationId, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(mutationId, "N", out _))
            throw new ArgumentException("Invalid local draft identifier.", nameof(mutationId));
        var context = RequireScope();
        var items = await ReadEntriesAsync(context, ct);
        try
        {
            var item = items.SingleOrDefault(x => x.Id == mutationId)
                ?? throw new KeyNotFoundException("Local draft does not exist.");
            if (item.State != VaultSyncState.Pending || item.TransactionHash is not null ||
                item.SignedFileEntries is { Length: > 0 } ||
                item.SignedDeletionManifests is { Length: > 0 })
                throw new InvalidOperationException("Submitted or unresolved drafts cannot be discarded.");
            if (items.Any(x => x.Id != item.Id &&
                x.State is not (VaultSyncState.Confirmed or VaultSyncState.Failed) &&
                x.DependsOn.Contains(item.Id, StringComparer.Ordinal)))
                throw new InvalidOperationException(
                    "Discard dependent local drafts before deleting their parent folder.");
            if (VaultLinkedMultipartFilePolicy.IsReferencedByPendingManifest(
                    items.Where(x => x.Id != item.Id).ToArray(), item.EntityId))
                throw new InvalidOperationException(
                    "Discard the unpublished multipart manifest before discarding a referenced file part.");
            if (VaultLinkedTreeFilePolicy.BlocksDiscard(item, items))
                throw new InvalidOperationException(
                    "A published file-tree child still needs its root, or an unpublished root still references this node. Resolve the complete file group before discarding.");
            Check(context);
            if (!await store.TryDeleteAsync(item, ct))
                throw new InvalidOperationException(
                    "The draft was modified concurrently; review the queue again.");
        }
        finally { ClearEntries(items); }
    }

    private static void ValidateDraft(LedgerRecordDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 256 ||
            string.IsNullOrEmpty(draft.Content) ||
            draft.Tags is { Length: > 24 } ||
            draft.Tags?.Any(x => x is null || x.Length > 64) == true ||
            draft.FolderId is { Length: > 0 } folder &&
                (folder.Length != 16 || !folder.All(Uri.IsHexDigit)) ||
            draft.Content.StartsWith(StellarLedgerFileProtocol.SinglePrefix, StringComparison.Ordinal) ||
            draft.Content.StartsWith(StellarLedgerFileProtocol.ManifestPrefix, StringComparison.Ordinal) ||
            draft.Content.StartsWith(StellarLedgerFileProtocol.ChunkPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Invalid local linked record/folder draft.");
    }
}
