namespace StellarVault.Core.Services;

/// <summary>
/// Pure, single-snapshot queue readiness calculation shared by encrypted queue
/// services and read-only folder reviews. This is HD publication readiness only:
/// tagged independent S-key entries remain blocked until their own authenticated
/// key lifecycle, protocol preflight and publishing adapter are enabled.
/// </summary>
public static class VaultPendingMutationReviewPolicy
{
    public static VaultSyncReview Build(IReadOnlyList<VaultPendingMutation> all)
    {
        ArgumentNullException.ThrowIfNull(all);
        var byId = all.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var ready = new List<VaultPendingMutation>();
        var blocked = new List<VaultSyncBlockedMutation>();
        foreach (var item in all.Where(x => x.State == VaultSyncState.Pending))
        {
            var dependencies = item.DependsOn ?? [];
            var missing = dependencies.Where(id => !byId.ContainsKey(id)).ToArray();
            var waiting = dependencies.Where(id => byId.TryGetValue(id, out var parent) &&
                parent.State != VaultSyncState.Confirmed).ToArray();
            var problems = VaultMutationStrategyPolicy.Resolve(item.OriginStrategyId) !=
                VaultMutationStrategyPolicy.Hd
                ? new[] { "Independent linked-key queue intent cannot be published through the HD transport." }
                : item.Kind is VaultMutationKind.UploadFile or VaultMutationKind.DeleteFile
                    ? new[] { "Hybrid file operations are disabled. Keep any existing signed journal for read-only reconciliation; use direct on-chain files for new work." }
                    : item.Kind is VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder
                        ? VaultQueuedCreatePolicy.Problems(item, all) : [];
            if (problems.Length == 0 && missing.Length == 0 && waiting.Length == 0)
                ready.Add(item);
            else
                blocked.Add(new VaultSyncBlockedMutation(item.Id, [..missing, ..problems], waiting));
        }
        return new VaultSyncReview(ready.OrderBy(x => x.CreatedUtc).ToArray(),
            blocked.ToArray(), all.Count(x => x.State is VaultSyncState.Uncertain or VaultSyncState.Submitting),
            all.Count(x => x.State == VaultSyncState.Conflict));
    }
}
