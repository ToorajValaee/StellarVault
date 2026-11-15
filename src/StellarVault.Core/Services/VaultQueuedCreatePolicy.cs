using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// Side-effect-free validation for pending record and folder creations.
/// Does not authenticate a remote ledger or authorize a submission.
/// </summary>
public static class VaultQueuedCreatePolicy
{
    /// <summary>
    /// Resolve a folder referenced by a local child. A confirmed original
    /// CreateFolder remains a valid dependency target for children created
    /// before it was published. Later confirmed UPDATE/DELETE entries for
    /// that same entity are historical mutations, not second parent folders.
    /// Unresolved changes still participate and block ambiguous publishing.
    /// </summary>
    public static VaultPendingMutation[] ParentCandidates(
        IReadOnlyList<VaultPendingMutation> all, string parentEntityId,
        string? excludedMutationId = null) =>
        all.Where(x => x.Id != excludedMutationId &&
            x.EntityId.Equals(parentEntityId, StringComparison.OrdinalIgnoreCase) &&
            (x.State is not (VaultSyncState.Confirmed or VaultSyncState.Failed) ||
             x.Kind == VaultMutationKind.CreateFolder &&
             x.State == VaultSyncState.Confirmed)).ToArray();

    public static string[] Problems(VaultPendingMutation item,
        IReadOnlyList<VaultPendingMutation> all)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(all);
        var problems = new List<string>();
        if (item.Kind is not (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder))
            return ["unsupported-mutation-kind"];
        if (item.EntityId.Length != 16 || !item.EntityId.All(Uri.IsHexDigit))
            problems.Add("invalid-ledger-record-id");
        if (all.Any(x => x.Id != item.Id &&
            string.Equals(x.EntityId, item.EntityId, StringComparison.OrdinalIgnoreCase)))
            problems.Add("duplicate-local-record-id");
        LedgerRecordDraft? draft;
        try { draft = JsonSerializer.Deserialize<LedgerRecordDraft>(item.EncryptedPayload); }
        catch (JsonException) { draft = null; }
        if (draft is null) return [..problems, "invalid-draft-payload"];
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 256)
            problems.Add("invalid-record-title");
        if (draft.IsFolder != (item.Kind == VaultMutationKind.CreateFolder))
            problems.Add("record-folder-kind-mismatch");
        if (string.IsNullOrEmpty(draft.Content))
            problems.Add("missing-record-content");
        if (draft.Tags is { Length: > 24 } ||
            draft.Tags?.Any(x => x is null || x.Length > 64) == true)
            problems.Add("invalid-record-tags");
        if (draft.Content is not null &&
            (draft.Content.StartsWith(StellarLedgerFileProtocol.SinglePrefix, StringComparison.Ordinal) ||
             draft.Content.StartsWith(StellarLedgerFileProtocol.ManifestPrefix, StringComparison.Ordinal) ||
             draft.Content.StartsWith(StellarLedgerFileProtocol.ChunkPrefix, StringComparison.Ordinal) ||
             draft.Content.StartsWith(VaultLinkedTreeFilePolicy.Prefix, StringComparison.Ordinal)))
            problems.Add("file-protocol-needs-staged-review");
        // The serialized FolderId and the durable dependency graph MUST
        // identify the same pending parent. Otherwise a stale or corrupt child
        // may appear Ready while its actual parent is still unpublished.
        if (item.DependsOn is null || item.DependsOn.Length > 1 ||
            item.DependsOn.Any(x => !Guid.TryParseExact(x, "N", out _)))
            problems.Add("invalid-parent-dependency");

        VaultPendingMutation? localParent = null;
        if (draft.FolderId is { Length: > 0 } parent)
        {
            if (parent.Length != 16 || !parent.All(Uri.IsHexDigit))
                problems.Add("invalid-parent-record-id");
            if (string.Equals(parent, item.EntityId, StringComparison.OrdinalIgnoreCase))
                problems.Add("self-parent-folder");
            var matches = ParentCandidates(all, parent, item.Id);
            if (matches.Length > 1)
                problems.Add("ambiguous-local-parent-id");
            else
                localParent = matches.SingleOrDefault();
            if (localParent is not null && localParent.Kind != VaultMutationKind.CreateFolder)
                problems.Add("local-parent-is-not-folder");
        }
        if (localParent is not null && localParent.State != VaultSyncState.Confirmed)
        {
            if (localParent.State == VaultSyncState.Failed ||
                localParent.State == VaultSyncState.Conflict)
                problems.Add("local-parent-unavailable");
            else
                problems.Add("unpublished-local-parent-folder");
            if (item.DependsOn is not { Length: 1 } requiredEdge ||
                requiredEdge[0] != localParent.Id)
                problems.Add("parent-dependency-mismatch");
        }
        else if (item.DependsOn is { Length: > 0 } existingEdges &&
            (localParent is null || existingEdges.Length != 1 ||
             existingEdges[0] != localParent.Id))
            problems.Add("orphaned-parent-dependency");

        // Reject ambiguous immutable journal identities even when the draft
        // has no folder dependency. A corrupt duplicate cannot become Ready.
        var duplicateMutationIds = all.Select(x => x.Id)
            .Distinct(StringComparer.Ordinal).Count() != all.Count;
        if (duplicateMutationIds)
            problems.Add("duplicate-local-mutation-id");

        // Never traverse malformed multi-edge dependency graphs or build a
        // dictionary over duplicate IDs: both previously threw during review.
        if (item.DependsOn is { Length: 1 } edge && !duplicateMutationIds)
        {
            var byId = all.ToDictionary(x => x.Id, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal) { item.Id };
            string? cursor = edge[0];
            while (cursor is not null)
            {
                if (!seen.Add(cursor))
                {
                    problems.Add("local-folder-dependency-cycle");
                    break;
                }
                if (!byId.TryGetValue(cursor, out var ancestor))
                {
                    problems.Add("missing-parent-dependency");
                    break;
                }
                if (ancestor.Kind != VaultMutationKind.CreateFolder)
                {
                    problems.Add("dependency-target-not-folder");
                    break;
                }
                if (ancestor.DependsOn is not { Length: <= 1 })
                {
                    problems.Add("invalid-parent-dependency");
                    break;
                }
                cursor = ancestor.DependsOn.SingleOrDefault();
            }
        }
        // The unlaunched HD SVTREE3 protocol is permitted ONLY through the
        // strongly typed encrypted queue, never as an arbitrary text record.
        // Individual parent/root nodes are eligible only after their children
        // have been historically confirmed by the existing HD journal.
        if (VaultHdTreeFilePolicy.IsTreeDraft(draft) &&
            (item.OriginStrategyId is null or VaultMutationStrategyPolicy.Hd))
        {
            var treeProblems = VaultHdTreeFilePolicy.Problems(item, all, draft);
            if (treeProblems.Length == 0)
                problems.RemoveAll(x => x == "file-protocol-needs-staged-review");
            else
                problems.AddRange(treeProblems);
        }
        return problems.Distinct(StringComparer.Ordinal).ToArray();
    }
}
