using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// Human-readable, read-only workflow for encrypted offline folder trees.
/// Local Confirmed means the queue's independent verifier accepted the exact
/// recorded transaction. It is not proof of current remote folder existence;
/// publication still requires a fresh ledger inventory and explicit approval.
/// </summary>
public static class VaultHybridFolderSyncPlan
{
    public enum StepStatus
    {
        ReadyForReview,
        WaitingForParent,
        NeedsRepair,
        AwaitingVerification,
        Confirmed,
        NotPublishable
    }

    public sealed record Step(
        string MutationId, string EntityId, string Label, bool IsFolder,
        string? ParentEntityId, string? ParentMutationId, StepStatus Status,
        string Explanation, int Depth);

    public sealed record Plan(IReadOnlyList<Step> Steps, int Ready,
        int Waiting, int NeedsRepair, int AwaitingVerification, int Confirmed);

    public static Plan Build(
        IReadOnlyList<VaultPendingMutation> all, VaultSyncReview review)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(review);
        var creates = all.Where(x => x.Kind is
            VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder).ToArray();
        var byId = all.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var readyIds = review.Ready.Select(x => x.Id)
            .ToHashSet(StringComparer.Ordinal);
        var blockedIds = review.Blocked.Select(x => x.MutationId)
            .ToHashSet(StringComparer.Ordinal);
        var steps = new List<Step>();

        foreach (var item in creates)
        {
            LedgerRecordDraft? draft = null;
            try { draft = JsonSerializer.Deserialize<LedgerRecordDraft>(item.EncryptedPayload); }
            catch (JsonException) { /* Missing/corrupt payload is a repair state. */ }
            var label = draft?.Title is { Length: > 0 } title
                ? title : "(unreadable local draft)";
            var parentId = draft?.FolderId;
            var matchingParents = !string.IsNullOrEmpty(parentId)
                ? creates.Where(x => x.Kind == VaultMutationKind.CreateFolder &&
                    string.Equals(x.EntityId, parentId, StringComparison.OrdinalIgnoreCase))
                    .ToArray()
                : [];
            var parent = matchingParents.Length == 1 ? matchingParents[0] : null;
            var depth = 0;
            var visited = new HashSet<string>(StringComparer.Ordinal) { item.Id };
            var walk = parent;
            while (walk is not null)
            {
                if (!visited.Add(walk.Id) || ++depth > 64)
                {
                    depth = 0;
                    break;
                }
                LedgerRecordDraft? ancestorDraft;
                try
                {
                    ancestorDraft = JsonSerializer.Deserialize<LedgerRecordDraft>(walk.EncryptedPayload);
                }
                catch (JsonException) { break; }
                if (ancestorDraft?.FolderId is not { Length: > 0 } ancestorId)
                    break;
                var candidates = creates.Where(x => x.Kind == VaultMutationKind.CreateFolder &&
                    x.EntityId.Equals(ancestorId, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (candidates.Length != 1) break;
                walk = candidates[0];
            }

            var status = item.State switch
            {
                VaultSyncState.Confirmed => StepStatus.Confirmed,
                VaultSyncState.Submitting or VaultSyncState.Uncertain =>
                    StepStatus.AwaitingVerification,
                VaultSyncState.Conflict or VaultSyncState.Failed =>
                    StepStatus.NotPublishable,
                _ when draft is null => StepStatus.NeedsRepair,
                _ when blockedIds.Contains(item.Id) &&
                    (item.DependsOn?.Any(id => byId.TryGetValue(id, out var p) &&
                        p.Kind == VaultMutationKind.CreateFolder &&
                        p.State is VaultSyncState.Submitting or VaultSyncState.Uncertain) == true) =>
                    StepStatus.AwaitingVerification,
                _ when blockedIds.Contains(item.Id) &&
                    (item.DependsOn?.Any(id => byId.TryGetValue(id, out var p) &&
                        p.Kind == VaultMutationKind.CreateFolder &&
                        p.State == VaultSyncState.Pending) == true) &&
                    VaultQueuedCreatePolicy.Problems(item, all).All(x =>
                        x == "unpublished-local-parent-folder") =>
                    StepStatus.WaitingForParent,
                _ when readyIds.Contains(item.Id) => StepStatus.ReadyForReview,
                _ => StepStatus.NeedsRepair
            };
            var explanation = status switch
            {
                StepStatus.ReadyForReview =>
                    "Ready for fresh ledger inspection and an optional fee quote. Explicit approval is still required.",
                StepStatus.WaitingForParent =>
                    "The parent folder must be submitted and independently confirmed first.",
                StepStatus.AwaitingVerification =>
                    "A submission in this dependency chain is unresolved. Investigate its recorded transaction; never retry.",
                StepStatus.Confirmed =>
                    "The queued transaction was independently verified. Fresh remote inventory is still required for children.",
                StepStatus.NotPublishable =>
                    "This local mutation cannot be published in its current state. Investigate or preserve its contents.",
                _ => "The parent, draft or dependency graph needs correction before Sync."
            };
            steps.Add(new Step(item.Id, item.EntityId, label,
                item.Kind == VaultMutationKind.CreateFolder,
                parentId, parent?.Id, status, explanation, depth));
        }
        var ordered = steps.OrderBy(x => x.Depth)
            .ThenBy(x => x.IsFolder ? 0 : 1)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.MutationId, StringComparer.Ordinal).ToArray();
        return new(ordered,
            ordered.Count(x => x.Status == StepStatus.ReadyForReview),
            ordered.Count(x => x.Status == StepStatus.WaitingForParent),
            ordered.Count(x => x.Status == StepStatus.NeedsRepair),
            ordered.Count(x => x.Status == StepStatus.AwaitingVerification),
            ordered.Count(x => x.Status == StepStatus.Confirmed));
    }
}
