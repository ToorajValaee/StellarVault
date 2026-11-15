namespace StellarVault.Core.Services;

/// <summary>
/// Conservative parent-graph policy for an existing linked S-key record move.
/// A parent must already be a published folder and must not lead back to the
/// record itself. This check is repeated by the JS signer before quotation.
/// </summary>
public static class VaultLinkedFolderHierarchyPolicy
{
    public static void RequirePublishedAcyclicParent(
        string recordId, string? proposedParent,
        IReadOnlyList<LedgerRecordItem> remote)
    {
        if (string.IsNullOrEmpty(proposedParent)) return;
        if (recordId == proposedParent)
            throw new InvalidOperationException("A record cannot be its own parent.");

        var byId = remote.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { recordId };
        var parent = proposedParent;
        while (parent is not null)
        {
            if (!seen.Add(parent))
                throw new InvalidOperationException(
                    "The proposed folder move would create a cycle.");
            if (!byId.TryGetValue(parent, out var node) || !node.IsFolder)
                throw new InvalidOperationException(
                    "The proposed parent must be a published S-key folder.");
            parent = node.FolderId;
        }
    }
}
