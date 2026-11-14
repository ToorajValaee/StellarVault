using System.Security.Cryptography;
using System.Text.Json;

namespace StellarVault.Core.Services;

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
        DateTimeOffset CreatedUtc, bool IsCorrupt);

    private sealed record Context(string Root, string Scope, long Epoch);

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
            keys.IsOfflineOnly ? -1 : ledger.ConnectionEpoch);
    }

    private void Check(Context original)
    {
        linkedRetention.DeviceRetention.RequireLocalPersistence();
        linkedRetention.RequireLocalPersistence();
        if (!keys.IsUnlocked || keys.ActiveRootAccountId != original.Root ||
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
        Check(c);
        if (all.Any(x => x.AccountId != c.Root ||
            x.NetworkId != VaultLinkedQueueStorageScope.FromNetworkScope(c.Scope) ||
            VaultMutationStrategyPolicy.Resolve(x.OriginStrategyId) !=
                VaultMutationStrategyPolicy.Linked))
            throw new InvalidDataException("Encrypted queue contains a foreign linked-account draft.");
        return all;
    }

    private static void ClearEntries(IEnumerable<VaultPendingMutation> entries)
    {
        foreach (var x in entries)
            if (x.EncryptedPayload is not null)
                CryptographicOperations.ZeroMemory(x.EncryptedPayload);
    }

    public async Task<DraftItem> CreateAsync(LedgerRecordDraft draft,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);
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
                with { OriginStrategyId = VaultMutationStrategyPolicy.Linked };
            try
            {
                Check(context);
                if (!await store.AddAsync(item, ct))
                    throw new InvalidOperationException(
                        "The draft changed concurrently; refresh local drafts.");
                return new(item.Id, item.EntityId, draft.IsFolder,
                    draft.Title, draft.FolderId, item.State, item.CreatedUtc, false);
            }
            finally { CryptographicOperations.ZeroMemory(item.EncryptedPayload); }
        }
        finally
        {
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            ClearEntries(local);
        }
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
                try
                {
                    var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(item.EncryptedPayload)
                        ?? throw new InvalidDataException("Missing encrypted local draft.");
                    ValidateDraft(draft);
                    if (draft.IsFolder != (item.Kind == VaultMutationKind.CreateFolder))
                        throw new InvalidDataException("Encrypted draft type mismatch.");
                    result.Add(new(item.Id, item.EntityId, draft.IsFolder,
                        draft.Title, draft.FolderId, item.State, item.CreatedUtc, false));
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or
                                           ArgumentException or InvalidOperationException)
                {
                    result.Add(new(item.Id, item.EntityId,
                        item.Kind == VaultMutationKind.CreateFolder,
                        "UNREADABLE ENCRYPTED DRAFT", null,
                        item.State, item.CreatedUtc, true));
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
