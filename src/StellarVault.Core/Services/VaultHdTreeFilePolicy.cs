using System.Security.Cryptography;
using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// HD queue admission for immutable SVTREE3 nodes. Keeps the existing HD
/// create-record quote, signed-hash checkpoint, and historical reconciliation.
/// No alternate signing or network publication path exists here.
/// </summary>
public static class VaultHdTreeFilePolicy
{
    public static bool IsTreeDraft(LedgerRecordDraft? draft) =>
        draft?.Content?.StartsWith(VaultLinkedTreeFilePolicy.Prefix,
            StringComparison.Ordinal) == true;

    public static string[] Problems(VaultPendingMutation item,
        IReadOnlyList<VaultPendingMutation> all, LedgerRecordDraft draft)
    {
        if (item.OriginStrategyId is not null &&
            item.OriginStrategyId != VaultMutationStrategyPolicy.Hd)
            return ["hd-tree-strategy-mismatch"];
        if (item.Kind != VaultMutationKind.CreateRecord ||
            !VaultLinkedTreeFilePolicy.TryDecode(draft, out var kind, out var payload))
            return ["hd-tree-node-invalid"];
        try
        {
            if (!VaultLinkedTreeFilePolicy.IsTypedNode(draft))
                return ["hd-tree-node-invalid"];
        }
        finally { CryptographicOperations.ZeroMemory(payload); }

        var group = draft.Tags![1];
        if (kind != VaultStreamingFileTree.RootKind &&
            !all.Any(x => x.Id != item.Id && x.State == VaultSyncState.Pending &&
                x.TransactionHash is null &&
                x.Kind == VaultMutationKind.CreateRecord &&
                (x.OriginStrategyId is null or VaultMutationStrategyPolicy.Hd) &&
                IsPendingRoot(x, group)))
            return ["hd-tree-root-not-staged"];

        var references = References(kind, draft);
        if (references is null) return ["hd-tree-node-invalid"];
        foreach (var child in references)
        {
            var matches = all.Where(x => x.Id != item.Id &&
                x.EntityId.Equals(child.Id, StringComparison.OrdinalIgnoreCase) &&
                x.Kind == VaultMutationKind.CreateRecord &&
                (x.OriginStrategyId is null or VaultMutationStrategyPolicy.Hd)).ToArray();
            if (matches.Length != 1 || matches[0].State != VaultSyncState.Confirmed)
                return ["hd-tree-child-not-confirmed"];
            var staged = Decode(matches[0]);
            if (staged?.Tags is not { Length: 2 } tags || tags[1] != group ||
                !VaultLinkedTreeFilePolicy.TryDecode(staged, out _, out var bytes))
                return ["hd-tree-child-invalid"];
            try
            {
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)),
                    child.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                    return ["hd-tree-child-hash-mismatch"];
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        return [];
    }

    /// <summary>Even a locally Confirmed child must still exist and match on
    /// the authoritative ledger immediately before a parent's signed quote.</summary>
    public static string[] RemoteProblems(VaultPendingMutation item,
        IReadOnlyList<VaultPendingMutation> all,
        IReadOnlyCollection<LedgerRecordItem> remote)
    {
        var draft = Decode(item);
        if (!IsTreeDraft(draft)) return [];
        var local = Problems(item, all, draft!);
        if (local.Length > 0) return local;
        if (!VaultLinkedTreeFilePolicy.TryDecode(draft!, out var kind, out var payload))
            return ["hd-tree-node-invalid"];
        CryptographicOperations.ZeroMemory(payload);
        var refs = References(kind, draft!);
        if (refs is null) return ["hd-tree-node-invalid"];
        foreach (var child in refs)
        {
            var matchesInJournal = all.Where(x =>
                x.EntityId.Equals(child.Id, StringComparison.OrdinalIgnoreCase) &&
                x.Kind == VaultMutationKind.CreateRecord).ToArray();
            if (matchesInJournal.Length != 1)
                return ["hd-tree-child-journal-ambiguous"];
            var original = matchesInJournal[0];
            var expected = original is null ? null : Decode(original);
            var matches = remote.Where(x =>
                x.Id.Equals(child.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || expected is null ||
                !SameRecord(matches[0], expected))
                return ["hd-tree-child-remote-mismatch"];
        }
        return [];
    }

    private static bool SameRecord(LedgerRecordItem actual, LedgerRecordDraft draft) =>
        actual.Title == draft.Title && actual.Content == draft.Content &&
        actual.FolderId == draft.FolderId && actual.IsFolder == draft.IsFolder &&
        (actual.Tags ?? []).SequenceEqual(draft.Tags ?? [], StringComparer.Ordinal) &&
        (actual.UserName ?? "") == (draft.UserName ?? "") &&
        (actual.Url ?? "") == (draft.Url ?? "") &&
        (actual.Notes ?? "") == (draft.Notes ?? "");

    private static bool IsPendingRoot(VaultPendingMutation item, string group)
    {
        var draft = Decode(item);
        if (draft?.Tags is not ["svtree3-root", var tag] || tag != group)
            return false;
        return VaultLinkedTreeFilePolicy.IsTypedNode(draft);
    }

    private static LedgerRecordDraft? Decode(VaultPendingMutation item)
    {
        try { return JsonSerializer.Deserialize<LedgerRecordDraft>(item.EncryptedPayload); }
        catch (JsonException) { return null; }
    }

    private static VaultStreamingFileTree.Reference[]? References(
        string kind, LedgerRecordDraft draft)
    {
        if (!VaultLinkedTreeFilePolicy.TryDecode(draft, out _, out var data))
            return null;
        try
        {
            if (kind == VaultStreamingFileTree.LeafKind) return [];
            if (kind == VaultStreamingFileTree.BranchKind)
                return JsonSerializer.Deserialize<VaultStreamingFileTree.Branch>(data)?.Children;
            if (kind == VaultStreamingFileTree.RootKind)
            {
                var root = JsonSerializer.Deserialize<VaultStreamingFileTree.Root>(data);
                return root?.Tree is null ? null : [root.Tree];
            }
            return null;
        }
        catch (JsonException) { return null; }
        finally { CryptographicOperations.ZeroMemory(data); }
    }
}
