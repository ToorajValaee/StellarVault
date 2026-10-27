using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// Side-effect-free validation for pending record and folder creations.
/// Does not authenticate a remote ledger or authorize a submission.
/// </summary>
public static class VaultQueuedCreatePolicy
{
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
        if (draft.Content.StartsWith(StellarLedgerFileProtocol.SinglePrefix, StringComparison.Ordinal) ||
            draft.Content.StartsWith(StellarLedgerFileProtocol.ManifestPrefix, StringComparison.Ordinal) ||
            draft.Content.StartsWith(StellarLedgerFileProtocol.ChunkPrefix, StringComparison.Ordinal))
            problems.Add("file-protocol-needs-staged-review");
        if (draft.FolderId is { Length: > 0 } parent)
        {
            if (string.Equals(parent, item.EntityId, StringComparison.OrdinalIgnoreCase))
                problems.Add("self-parent-folder");
            if (all.Any(x => x.Id != item.Id &&
                string.Equals(x.EntityId, parent, StringComparison.OrdinalIgnoreCase) &&
                x.State != VaultSyncState.Confirmed))
                problems.Add("unpublished-local-parent-folder");
        }
        return problems.Distinct(StringComparer.Ordinal).ToArray();
    }
}
