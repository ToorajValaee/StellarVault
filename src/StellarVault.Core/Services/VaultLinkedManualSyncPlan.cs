using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// Pure, read-only manual sync ordering for the existing S-key encrypted queue.
/// It does not calculate fees, sign, transition state, or transmit a transaction.
/// Every ready draft still needs its own fresh review and explicit fee approval.
/// </summary>
public static class VaultLinkedManualSyncPlan
{
    public sealed record Item(string MutationId, string EntityId,
        string Title, bool IsFolder, VaultSyncState State,
        DateTimeOffset CreatedUtc, string[] Blockers);

    public sealed record Review(IReadOnlyList<Item> Items,
        string? NextReadyMutationId, int PendingCount,
        int UnresolvedCount, bool CanReviewNext);

    public static Review Build(IReadOnlyList<VaultPendingMutation> entries,
        bool onlineSignerAuthorized)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var hasUnresolved = entries.Any(x =>
            (x.State is VaultSyncState.Uncertain or VaultSyncState.Submitting or VaultSyncState.Conflict) ||
            (x.Kind == VaultMutationKind.ExtendLinkedChain &&
             x.State != VaultSyncState.Confirmed));
        var result = new List<Item>(entries.Count);
        foreach (var mutation in entries.OrderBy(x => x.CreatedUtc)
                     .ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            var reasons = new List<string>();
            if (mutation.OriginStrategyId != VaultMutationStrategyPolicy.Linked)
                reasons.Add("foreign-origin");
            if (mutation.State != VaultSyncState.Pending)
                reasons.Add("not-pending");
            if (mutation.Kind is not (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder or
                VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder or
                VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder))
                reasons.Add("unsupported-mutation-kind");
            if (hasUnresolved)
                reasons.Add("unresolved-queue-submission");
            if (!onlineSignerAuthorized)
                reasons.Add("connected-signer-unlock-required");

            string title = "Unreadable encrypted draft";
            bool folder = mutation.Kind is VaultMutationKind.CreateFolder or
                VaultMutationKind.UpdateFolder or VaultMutationKind.DeleteFolder;
            try
            {
                var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(mutation.EncryptedPayload);
                if (mutation.Kind == VaultMutationKind.ExtendLinkedChain)
                {
                    title = "Linked account capacity expansion";
                }
                else if (draft is null)
                    reasons.Add("unreadable-draft");
                else
                {
                    title = draft.Title;
                    if (draft.IsFolder != folder)
                        reasons.Add("draft-kind-mismatch");
                }
            }
            catch (Exception e) when (e is JsonException or ArgumentException)
            {
                if (mutation.Kind == VaultMutationKind.ExtendLinkedChain)
                    title = "Linked account capacity expansion";
                else
                    reasons.Add("unreadable-draft");
            }

            if (mutation.State == VaultSyncState.Pending)
            {
                if (mutation.Kind is VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder)
                    reasons.AddRange(VaultLinkedSingleFilePolicy.Problems(mutation, entries));
                else if ((mutation.Kind is VaultMutationKind.UpdateRecord or
                    VaultMutationKind.UpdateFolder or VaultMutationKind.DeleteRecord or
                    VaultMutationKind.DeleteFolder) &&
                    mutation.ExpectedRemoteVersion is not { Length: 64 })
                    reasons.Add("missing-remote-revision");
            }

            result.Add(new(mutation.Id, mutation.EntityId, title, folder,
                mutation.State, mutation.CreatedUtc,
                reasons.Distinct(StringComparer.Ordinal).ToArray()));
        }
        // The existing folder dependency policy requires previously confirmed
        // parents, so only a currently publishable item is suggested. A successful
        // publication requires the user to refresh/review the NEXT queue snapshot.
        var ready = result.FirstOrDefault(x => x.State == VaultSyncState.Pending &&
            x.Blockers.Length == 0);
        return new(result, ready?.MutationId,
            result.Count(x => x.State == VaultSyncState.Pending),
            result.Count(x => x.State is VaultSyncState.Submitting or
                VaultSyncState.Uncertain or VaultSyncState.Conflict ||
                entries.Any(y => y.Id == x.MutationId &&
                    y.Kind == VaultMutationKind.ExtendLinkedChain &&
                    y.State != VaultSyncState.Confirmed)),
            ready is not null);
    }
}
