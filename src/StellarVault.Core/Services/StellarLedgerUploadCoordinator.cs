namespace StellarVault.Core.Services;

/// <summary>
/// Immutable upload plan with stable record IDs. Callers must persist this plan
/// securely before the first transaction; this class does not persist secrets.
/// </summary>
public sealed class StellarLedgerUploadPlan
{
    public IReadOnlyList<(string Id, LedgerRecordDraft Draft)> Entries { get; }
    public string ManifestId => Entries[^1].Id;

    public StellarLedgerUploadPlan(string title, string fileName, string contentType,
        ReadOnlySpan<byte> content, string? folderId = null, string[]? checkpointIds = null)
    {
        Entries = StellarLedgerFileProtocol.BuildUpload(
            title, fileName, contentType, content, folderId, checkpointIds).ToArray();
    }

    /// <summary>
    /// Reconcile every planned entry against an authoritative ledger snapshot.
    /// Any mismatch or duplicate ID fails closed, including a manifest published
    /// without all referenced parts. This never assumes a missing entry was lost
    /// merely because a previous submission failed.
    /// </summary>
    public IReadOnlyList<(string Id, LedgerRecordDraft Draft)> Remaining(
        IReadOnlyList<LedgerRecordItem> ledgerSnapshot)
    {
        ArgumentNullException.ThrowIfNull(ledgerSnapshot);
        var byId = new Dictionary<string, LedgerRecordItem>(StringComparer.Ordinal);
        foreach (var item in ledgerSnapshot)
        {
            if (item is null || !byId.TryAdd(item.Id, item))
                throw new InvalidDataException("Ledger snapshot contains invalid or duplicate IDs.");
        }

        var remaining = new List<(string Id, LedgerRecordDraft Draft)>();
        foreach (var entry in Entries)
        {
            if (!byId.TryGetValue(entry.Id, out var existing))
            {
                remaining.Add(entry);
                continue;
            }
            if (!Same(existing, entry.Draft))
                throw new InvalidDataException("Existing ledger record does not match the saved upload plan.");
        }
        if (byId.ContainsKey(ManifestId) && remaining.Count > 0)
            throw new InvalidDataException("File manifest exists before all planned parts are confirmed.");
        return remaining;
    }

    private static bool Same(LedgerRecordItem item, LedgerRecordDraft draft) =>
        string.Equals(item.Title, draft.Title, StringComparison.Ordinal) &&
        string.Equals(item.Content, draft.Content, StringComparison.Ordinal) &&
        string.Equals(item.FolderId, draft.FolderId, StringComparison.Ordinal) &&
        item.IsFolder == draft.IsFolder &&
        (item.Tags ?? []).SequenceEqual(draft.Tags ?? [], StringComparer.Ordinal) &&
        string.Equals(item.UserName ?? "", draft.UserName ?? "", StringComparison.Ordinal) &&
        string.Equals(item.Url ?? "", draft.Url ?? "", StringComparison.Ordinal) &&
        string.Equals(item.Notes ?? "", draft.Notes ?? "", StringComparison.Ordinal);
}

/// <summary>
/// Reconciliation-first coordinator. Each call reads ledger state before making
/// any publication; the last plan entry (manifest) is always published last.
/// The caller owns durable encrypted checkpoint storage and approval UI.
/// </summary>
public sealed class StellarLedgerUploadCoordinator(
    IStellarLedgerRecords ledger, VaultStorageModeGate mode)
{
    public async Task<IReadOnlyList<(string Id, LedgerRecordDraft Draft)>> ReviewAsync(
        StellarLedgerUploadPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        mode.Require(VaultStorageMode.StellarLedger);
        var snapshot = await ledger.ReadAsync();
        mode.Require(VaultStorageMode.StellarLedger);
        return plan.Remaining(snapshot);
    }

    /// <summary>
    /// Publish one approved entry, after fresh reconciliation. On an ambiguous
    /// submission, propagate the error; the caller must review again before retry.
    /// Returns false when the entry is already confirmed on the ledger.
    /// </summary>
    public async Task<bool> PublishNextApprovedAsync(StellarLedgerUploadPlan plan,
        Func<LedgerRecordDraft, Task<bool>> requestApproval)
    {
        ArgumentNullException.ThrowIfNull(requestApproval);
        var remaining = await ReviewAsync(plan);
        if (remaining.Count == 0) return false;
        var next = remaining[0];
        if (!await requestApproval(next.Draft)) return false;
        // Recheck immediately after approval: network state may have changed.
        remaining = await ReviewAsync(plan);
        if (remaining.Count == 0) return false;
        // Approval applies only to the exact record just reviewed. If a second
        // client committed it meanwhile, do not publish the next part without
        // separately obtaining approval for that part.
        if (remaining[0].Id != next.Id)
            return false;
        mode.Require(VaultStorageMode.StellarLedger);
        var receipt = await ledger.PublishWithIdAsync(next.Id, next.Draft);
        mode.Require(VaultStorageMode.StellarLedger);
        LedgerMutationResult.RequireConfirmed(receipt);
        // Do not claim confirmation solely from the receipt; verify ledger visibility.
        var finalSnapshot = await ledger.ReadAsync();
        mode.Require(VaultStorageMode.StellarLedger);
        if (plan.Remaining(finalSnapshot).Any(x => x.Id == next.Id))
            throw new InvalidOperationException("Submitted record is not yet visible on Stellar. Reconcile before retry.");
        return true;
    }
}
