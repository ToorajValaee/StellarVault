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

    public async Task<VaultPendingMutation> CreateAsync(
        LedgerRecordDraft draft, CancellationToken ct = default)
    {
        var context = RequireScope();
        var account = context.Account;
        var scope = context.NetworkId;
        var kind = draft?.IsFolder == true
            ? VaultMutationKind.CreateFolder : VaultMutationKind.CreateRecord;
        ValidateDraft(draft, kind);
        var entries = await queue.ReviewAllAsync(account, scope, ct);
        RequireSameScope(context);
        var dependencies = ParentDependencies(draft, entries, null);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(draft);
        try
        {
            RequireSameScope(context);
            var recordId = NewUnusedRecordId(entries);
            var item = VaultPendingMutation.Create(
                account, scope, kind, recordId, payload,
                dependsOn: dependencies);
            // Local dependencies intentionally remain pending. The separate
            // preflight policy blocks them until their parent is published.
            var saved = await queue.EnqueueAsync(item, ct);
            RequireSameScope(context);
            return saved;
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    public async Task<VaultPendingMutation> ReviseAsync(
        string mutationId, LedgerRecordDraft draft, string expectedPayloadSha256,
        CancellationToken ct = default)
    {
        var context = RequireScope();
        var account = context.Account;
        var scope = context.NetworkId;
        var entries = await queue.ReviewAllAsync(account, scope, ct);
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

    private static void ValidateDraft(LedgerRecordDraft draft, VaultMutationKind kind)
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
            draft.Content.StartsWith(StellarLedgerFileProtocol.ChunkPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("File protocol data requires staged file synchronization.");
    }

    private static string[] ParentDependencies(LedgerRecordDraft draft,
        IReadOnlyList<VaultPendingMutation> entries, string? editingEntityId)
    {
        if (string.IsNullOrWhiteSpace(draft.FolderId)) return [];
        if (editingEntityId is not null &&
            string.Equals(editingEntityId, draft.FolderId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Folder cannot be its own parent.");

        var parents = VaultQueuedCreatePolicy.ParentCandidates(
            entries, draft.FolderId, editingEntityId is null ? null :
                entries.FirstOrDefault(x => x.EntityId.Equals(editingEntityId,
                    StringComparison.OrdinalIgnoreCase) &&
                    x.Kind is VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder)?.Id);
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
