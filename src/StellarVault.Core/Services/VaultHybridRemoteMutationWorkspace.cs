using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// Shared encrypted offline staging for updates and deletes of published
/// Stellar records/folders. An authenticated remote content fingerprint pins
/// the source revision. Stage/revise/discard never broadcast transactions.
/// </summary>
public sealed class VaultHybridRemoteMutationWorkspace(
    VaultPendingMutationQueue queue,
    VaultSessionManager sessions,
    VaultDeviceRetentionGate retention,
    VaultNetworkSelection networks,
    IStellarLedgerRecords ledger,
    IVaultLedgerManifestCopyInventory copies,
    VaultHybridDraftWorkspace drafts)
{
    public sealed record RemoteItem(string Id, string Title, bool IsFolder,
        string? FolderId, string Content, string? UserName, string? Url,
        string? Notes, string[]? Tags);

    public sealed record StagedItem(
        string MutationId, string EntityId, VaultMutationKind Kind,
        VaultSyncState State, LedgerRecordDraft? UpdatedDraft,
        string BaselineSha256, DateTimeOffset CreatedUtc, string PayloadSha256);

    private sealed record Scope(string Account, string Network, string SelectedNetwork,
        string? Passphrase, string Horizon, long Epoch);

    private Scope Capture()
    {
        retention.RequireLocalPersistence();
        var account = sessions.ActiveAccountId
            ?? throw new InvalidOperationException("Unlock the encrypted local vault.");
        return new Scope(account, networks.CredentialKey(account), networks.Network,
            networks.CustomPassphrase, networks.CustomHorizonUrl, sessions.Epoch);
    }

    private void EnsureScope(Scope scope)
    {
        retention.RequireLocalPersistence();
        if (!sessions.IsUnlocked || sessions.ActiveAccountId != scope.Account ||
            sessions.Epoch != scope.Epoch || networks.Network != scope.SelectedNetwork ||
            networks.CustomPassphrase != scope.Passphrase ||
            networks.CustomHorizonUrl != scope.Horizon ||
            networks.CredentialKey(scope.Account) != scope.Network)
            throw new InvalidOperationException("Account, unlocked session or network changed.");
    }

    private async Task<IReadOnlyList<LedgerRecordItem>> ReadBoundRemoteAsync(
        Scope scope, CancellationToken ct)
    {
        EnsureScope(scope);
        if (!ledger.IsConnected)
            throw new InvalidOperationException("Connect the matching Stellar wallet to stage remote changes.");
        var info = await ledger.InspectAccountInfoAsync();
        if (info.RootAccountId != scope.Account)
            throw new UnauthorizedAccessException("Connected Stellar root differs from the local vault.");
        var inventory = await ledger.ReadAsync();
        ct.ThrowIfCancellationRequested();
        EnsureScope(scope);
        if (!ledger.IsConnected || (await ledger.InspectAccountInfoAsync()).RootAccountId != scope.Account)
            throw new InvalidOperationException("Connected signing session changed.");
        if (inventory.Any(x => x is null || x.Id is not { Length: 16 } ||
            !x.Id.All(Uri.IsHexDigit)) ||
            inventory.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Any(x => x.Count() != 1))
            throw new InvalidDataException("Ambiguous or invalid ledger record inventory.");
        return inventory;
    }

    public async Task<IReadOnlyList<RemoteItem>> BrowseRemoteAsync(CancellationToken ct = default)
    {
        var scope = Capture();
        var records = await ReadBoundRemoteAsync(scope, ct);
        return records.Where(IsEditableRecord)
            .Select(x => new RemoteItem(x.Id, x.Title, x.IsFolder, x.FolderId,
                x.Content, x.UserName, x.Url, x.Notes, x.Tags?.ToArray()))
            .OrderBy(x => x.IsFolder ? 0 : 1)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<VaultPendingMutation> StageUpdateAsync(
        string recordId, LedgerRecordDraft proposed, CancellationToken ct = default) =>
        await StageAsync(recordId, proposed, delete: false, ct);

    public async Task<VaultPendingMutation> StageDeleteAsync(
        string recordId, CancellationToken ct = default) =>
        await StageAsync(recordId, null, delete: true, ct);

    private async Task<VaultPendingMutation> StageAsync(
        string recordId, LedgerRecordDraft? proposed, bool delete, CancellationToken ct)
    {
        if (!ValidRecordId(recordId)) throw new ArgumentException("Invalid record ID.", nameof(recordId));
        var scope = Capture();
        var remote = await ReadBoundRemoteAsync(scope, ct);
        var original = remote.SingleOrDefault(x =>
            string.Equals(x.Id, recordId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Remote record no longer exists.");
        if (!IsEditableRecord(original))
            throw new InvalidOperationException("File protocol records require staged file synchronization.");
        if (delete)
        {
            // Never delete a folder with children. Even empty-folder deletions
            // require a second fresh check before any future publication.
            if (original.IsFolder && remote.Any(x =>
                string.Equals(x.FolderId, original.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Cannot stage deletion of a nonempty remote folder.");
        }
        else
        {
            ValidateUpdate(original, proposed!, remote);
        }

        var previous = await queue.ReviewAllAsync(scope.Account, scope.Network, ct);
        if (previous.Any(x =>
            string.Equals(x.EntityId, recordId, StringComparison.OrdinalIgnoreCase) &&
            x.State is not (VaultSyncState.Failed or VaultSyncState.Confirmed)))
            throw new InvalidOperationException("Another local change targets this remote record. Review it first.");

        var kind = delete
            ? original.IsFolder ? VaultMutationKind.DeleteFolder : VaultMutationKind.DeleteRecord
            : original.IsFolder ? VaultMutationKind.UpdateFolder : VaultMutationKind.UpdateRecord;
        var baseline = Fingerprint(original);
        string[]? manifestOwners = null;
        if (delete)
        {
            manifestOwners = (await copies.InspectOwnersAsync(scope.Account,
                original.Id, ct)).ToArray();
            if (manifestOwners.Length == 0 || manifestOwners.Length > 63 ||
                manifestOwners.Distinct(StringComparer.Ordinal).Count() != manifestOwners.Length)
                throw new InvalidDataException("Cannot pin a complete, unique HD manifest-owner inventory.");
        }
        byte[] payload = delete
            ? JsonSerializer.SerializeToUtf8Bytes(
                new VaultDeletionIntent(ToDraft(original), manifestOwners!))
            : JsonSerializer.SerializeToUtf8Bytes(proposed!);
        try
        {
            EnsureScope(scope);
            // The original remote record is read again before enqueue so no
            // already-observed change can masquerade as the baseline.
            var fresh = await ReadBoundRemoteAsync(scope, ct);
            var now = fresh.SingleOrDefault(x =>
                string.Equals(x.Id, recordId, StringComparison.OrdinalIgnoreCase));
            if (now is null || Fingerprint(now) != baseline)
                throw new InvalidOperationException("Remote record changed during staging; refresh.");
            if (delete)
            {
                var latestOwners = (await copies.InspectOwnersAsync(scope.Account,
                    original.Id, ct)).ToArray();
                if (!latestOwners.Order(StringComparer.Ordinal)
                        .SequenceEqual(manifestOwners!, StringComparer.Ordinal))
                    throw new InvalidOperationException(
                        "HD manifest-copy inventory changed while staging the deletion.");
            }
            // Recheck the scoped queue after network I/O as well; concurrent
            // tabs can add a competing mutation while Horizon is queried.
            var latest = await queue.ReviewAllAsync(scope.Account, scope.Network, ct);
            if (latest.Any(x =>
                x.EntityId.Equals(recordId, StringComparison.OrdinalIgnoreCase) &&
                x.State is not (VaultSyncState.Failed or VaultSyncState.Confirmed)))
                throw new InvalidOperationException("A concurrent draft now targets this remote record.");
            var mutation = VaultPendingMutation.Create(scope.Account,
                scope.Network, kind, original.Id, payload,
                expectedRemoteVersion: baseline);
            EnsureScope(scope);
            return await queue.EnqueueAsync(mutation, ct);
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    public async Task<VaultPendingMutation> ReviseUpdateAsync(
        string mutationId, LedgerRecordDraft proposed, string expectedPayloadSha256,
        CancellationToken ct = default)
    {
        var scope = Capture();
        var entries = await queue.ReviewAllAsync(scope.Account, scope.Network, ct);
        var current = entries.SingleOrDefault(x => x.Id == mutationId)
            ?? throw new KeyNotFoundException("Staged remote change not found.");
        if (current.Kind is not (VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder) ||
            current.State != VaultSyncState.Pending || current.TransactionHash is not null ||
            !ValidDigest(current.ExpectedRemoteVersion))
            throw new InvalidOperationException("Only pending versioned updates can be edited.");
        if (expectedPayloadSha256 is not { Length: 64 } ||
            !expectedPayloadSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(current.EncryptedPayload)),
                expectedPayloadSha256, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Offline update changed in another tab. Reopen the latest version.");
        // This operation can work offline. The immutable ExpectedRemoteVersion
        // remains pinned until the user explicitly discards and restages.
        ValidateProposed(proposed, current.Kind == VaultMutationKind.UpdateFolder,
            current.EntityId);
        if (entries.Any(x => x.Id != mutationId &&
            x.State is not (VaultSyncState.Failed or VaultSyncState.Confirmed) &&
            x.EntityId.Equals(current.EntityId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Another queued mutation targets this record.");
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(proposed);
        try
        {
            EnsureScope(scope);
            return await queue.RevisePendingUpdateIfUnchangedAsync(
                scope.Account, scope.Network, mutationId,
                expectedPayloadSha256, payload, ct);
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    /// <summary>
    /// Preserve a conflicted or otherwise unsafe *record update* as a brand-new
    /// encrypted local CREATE. This never overwrites the published original,
    /// submits no transaction, and DOES NOT erase the source update. The user
    /// must review/discard that update separately to clear the Sync queue.
    /// Folder moves and deletions are excluded to avoid silently changing
    /// the meaning of existing folder dependencies.
    /// </summary>
    public async Task<VaultPendingMutation> PreserveUpdateAsNewRecordAsync(
        string mutationId, CancellationToken ct = default)
    {
        var scope = Capture();
        var entries = await queue.ReviewAllAsync(scope.Account, scope.Network, ct);
        var source = entries.SingleOrDefault(x => x.Id == mutationId)
            ?? throw new KeyNotFoundException("Offline update is missing.");
        if (source.Kind != VaultMutationKind.UpdateRecord ||
            source.State != VaultSyncState.Pending ||
            source.TransactionHash is not null ||
            !ValidRecordId(source.EntityId) ||
            !ValidDigest(source.ExpectedRemoteVersion))
            throw new InvalidOperationException(
                "Only an unsubmitted, version-pinned record update can be preserved as a new record.");

        LedgerRecordDraft draft;
        try
        {
            draft = JsonSerializer.Deserialize<LedgerRecordDraft>(source.EncryptedPayload)
                ?? throw new InvalidDataException("Offline update payload is missing.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Offline update payload cannot be recovered.", ex);
        }
        ValidateProposed(draft, isFolder: false, source.EntityId);
        // The title identifies the fork without changing its secret/content or
        // metadata. The original staged mutation stays intact for recovery.
        var suffix = " (offline copy)";
        var trimmed = draft.Title.Trim();
        var copyTitle = trimmed.Length + suffix.Length <= 256
            ? trimmed + suffix
            : trimmed[..(256 - suffix.Length)] + suffix;
        var copy = draft with { Title = copyTitle };
        EnsureScope(scope);
        var created = await drafts.CreateAsync(copy, ct);
        EnsureScope(scope);
        return created;
    }

    public async Task DiscardAsync(string mutationId, CancellationToken ct = default)
    {
        var scope = Capture();
        var entries = await queue.ReviewAllAsync(scope.Account, scope.Network, ct);
        var item = entries.SingleOrDefault(x => x.Id == mutationId);
        if (item is null || item.Kind is not
            (VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder or
             VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder))
            throw new InvalidOperationException("Not a staged record/folder modification.");
        EnsureScope(scope);
        await queue.DiscardPendingAsync(scope.Account, scope.Network, mutationId, ct);
    }

    public async Task<IReadOnlyList<StagedItem>> ListAsync(CancellationToken ct = default)
    {
        var scope = Capture();
        var entries = await queue.ReviewAllAsync(scope.Account, scope.Network, ct);
        var results = new List<StagedItem>();
        foreach (var item in entries.Where(x => x.Kind is
            VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder or
            VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder))
        {
            ct.ThrowIfCancellationRequested();
            LedgerRecordDraft? draft = null;
            if (item.Kind is VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder)
            {
                try { draft = JsonSerializer.Deserialize<LedgerRecordDraft>(item.EncryptedPayload); }
                catch (JsonException) { /* Corrupt drafts remain in queue for investigation. */ }
            }
            results.Add(new(item.Id, item.EntityId, item.Kind, item.State,
                draft, item.ExpectedRemoteVersion ?? "", item.CreatedUtc,
                Convert.ToHexStringLower(SHA256.HashData(item.EncryptedPayload))));
        }
        EnsureScope(scope);
        return results.OrderBy(x => x.CreatedUtc).ToArray();
    }

    /// <summary>Read-only drift report; never changes queue state or broadcasts.</summary>
    public async Task<IReadOnlyDictionary<string, string>> InspectRemoteVersionsAsync(
        CancellationToken ct = default)
    {
        var scope = Capture();
        var remote = await ReadBoundRemoteAsync(scope, ct);
        var items = await ListAsync(ct);
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items.Where(x => x.State == VaultSyncState.Pending))
        {
            var current = remote.SingleOrDefault(x =>
                x.Id.Equals(item.EntityId, StringComparison.OrdinalIgnoreCase));
            states[item.MutationId] = !ValidDigest(item.BaselineSha256)
                ? "Invalid pinned remote version; cannot publish."
                : current is null ? "Remote record was removed: CONFLICT."
                : Fingerprint(current) != item.BaselineSha256
                    ? "Remote record has changed: CONFLICT. Do not overwrite."
                    : item.Kind == VaultMutationKind.DeleteFolder &&
                      remote.Any(x => x.FolderId == item.EntityId)
                        ? "Folder has children: cannot delete."
                        : "Remote baseline matches. Review a fresh fee quote and explicitly approve a manual Testnet Sync; Mainnet remote updates/deletes remain blocked.";
        }
        EnsureScope(scope);
        return states;
    }

    private static LedgerRecordDraft ToDraft(LedgerRecordItem item) =>
        new(item.Title, item.Content, item.FolderId, item.Tags?.ToArray(),
            item.IsFolder, item.UserName, item.Url, item.Notes);

    public static string Fingerprint(LedgerRecordItem item)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Id = item.Id.ToLowerInvariant(),
            item.Title, item.Content, item.FolderId,
            Tags = item.Tags ?? Array.Empty<string>(), item.IsFolder,
            item.UserName, item.Url, item.Notes
        });
        try { return Convert.ToHexStringLower(SHA256.HashData(content)); }
        finally { CryptographicOperations.ZeroMemory(content); }
    }

    private static bool ValidDigest(string? value) =>
        value is { Length: 64 } && value.All(c =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidRecordId(string? value) =>
        value is { Length: 16 } && value.All(Uri.IsHexDigit);

    private static bool IsEditableRecord(LedgerRecordItem item) =>
        item.Id is { Length: 16 } && item.Id.All(Uri.IsHexDigit) &&
        item.Title is { Length: > 0 and <= 256 } &&
        !item.Content.StartsWith(StellarLedgerFileProtocol.SinglePrefix, StringComparison.Ordinal) &&
        !item.Content.StartsWith(StellarLedgerFileProtocol.ManifestPrefix, StringComparison.Ordinal) &&
        !item.Content.StartsWith(StellarLedgerFileProtocol.ChunkPrefix, StringComparison.Ordinal) &&
        !item.Content.StartsWith(VaultLinkedTreeFilePolicy.Prefix, StringComparison.Ordinal);

    private static void ValidateUpdate(LedgerRecordItem previous,
        LedgerRecordDraft proposed, IReadOnlyList<LedgerRecordItem> inventory)
    {
        ValidateProposed(proposed, previous.IsFolder, previous.Id);
        if (!string.IsNullOrEmpty(proposed.FolderId) &&
            !inventory.Any(x => x.IsFolder &&
                x.Id.Equals(proposed.FolderId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Destination folder is not on the ledger.");
        if (previous.IsFolder && !string.IsNullOrEmpty(proposed.FolderId))
        {
            // Walk the full remote ancestry; checking only direct children
            // would allow a cycle after moving a folder into a grandchild.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { previous.Id };
            var ancestor = proposed.FolderId;
            while (!string.IsNullOrEmpty(ancestor))
            {
                if (!seen.Add(ancestor))
                    throw new InvalidOperationException("Folder move would create an ancestry cycle.");
                var parent = inventory.SingleOrDefault(x =>
                    x.Id.Equals(ancestor, StringComparison.OrdinalIgnoreCase) && x.IsFolder)
                    ?? throw new InvalidOperationException("Folder ancestry is unavailable.");
                ancestor = parent.FolderId;
            }
        }
    }

    private static void ValidateProposed(LedgerRecordDraft draft, bool isFolder, string entityId)
    {
        if (draft is null || draft.IsFolder != isFolder ||
            draft.Title is not { Length: > 0 and <= 256 } ||
            string.IsNullOrEmpty(draft.Content) ||
            draft.Tags is { Length: > 24 } ||
            draft.Tags?.Any(t => t is null || t.Length > 64) == true ||
            draft.FolderId is { Length: > 0 } folder &&
            (!ValidRecordId(folder) || string.Equals(folder, entityId, StringComparison.OrdinalIgnoreCase)) ||
            draft.Content.StartsWith(StellarLedgerFileProtocol.SinglePrefix, StringComparison.Ordinal) ||
            draft.Content.StartsWith(StellarLedgerFileProtocol.ManifestPrefix, StringComparison.Ordinal) ||
            draft.Content.StartsWith(StellarLedgerFileProtocol.ChunkPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Invalid remote update fields or file payload.");
    }
}
