using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// Read-only inspection of each signed manifest tombstone in an encrypted
/// multi-owner DELETE journal. Each entry is verified independently against
/// Horizon and its authenticated historical predecessor. Even complete
/// historical coverage does NOT authorize queue confirmation: there is no
/// ledger-enforced record-version CAS or proof of absence of later writes.
/// </summary>
public sealed class VaultDeletionJournalReview(
    IVaultPendingMutationStore store,
    IVaultDeletionTombstoneReader tombstones,
    VaultDeviceRetentionGate retention,
    VaultSessionManager sessions,
    VaultNetworkSelection networks)
{
    public enum OwnerStatus { VerifiedHistoricalTombstone, UnresolvedTransaction, NotCheckpointed }

    public sealed record OwnerFinding(
        string SourceAccountId, string? SignedTransactionHash,
        OwnerStatus Status, string Explanation,
        bool HistoricalPredecessorMatched);

    public sealed record Finding(
        IReadOnlyList<OwnerFinding> Owners, int Verified, int Unresolved,
        int NotCheckpointed, bool CurrentRecordAbsent,
        bool AllStagedOwnersHistoricallyVerified, string Explanation)
    {
        // A multi-transaction optimistic delete is never a queue-confirmation proof.
        public bool IsQueueConfirmationProof => false;
    }

    public async Task<Finding> InspectAsync(
        string accountId, string mutationId, CancellationToken ct = default)
    {
        static Finding Reject(string reason) => new(
            [], 0, 0, 0, false, false, reason);
        try
        {
            retention.RequireLocalPersistence();
            if (!sessions.IsUnlocked || sessions.ActiveAccountId != accountId)
                return Reject("Unlock the same encrypted queue account first.");
            var epoch = sessions.Epoch;
            var network = networks.Network;
            var horizon = networks.CustomHorizonUrl;
            var passphrase = networks.CustomPassphrase;
            var scope = networks.CredentialKey(accountId);
            var original = await store.GetAsync(accountId, scope, mutationId, ct);
            if (original is null ||
                original.Kind is not (VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder) ||
                original.State is not (VaultSyncState.Submitting or VaultSyncState.Uncertain) ||
                original.TransactionHash is not null ||
                original.SignedDeletionManifests is not { Length: > 0 and <= 63 } journal ||
                original.ExpectedRemoteVersion is not { Length: 64 } baseline ||
                !baseline.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                return Reject("No unresolved signed deletion journal is available.");

            VaultDeletionIntent? intent;
            try { intent = JsonSerializer.Deserialize<VaultDeletionIntent>(original.EncryptedPayload); }
            catch (JsonException) { intent = null; }
            if (intent?.Original is null ||
                intent.ManifestSourceAccounts is not { Length: > 0 and <= 63 } stagedOwners ||
                stagedOwners.Any(x => !StellarAccountAddress.IsValid(x)) ||
                stagedOwners.Distinct(StringComparer.Ordinal).Count() != stagedOwners.Length ||
                journal.Any(x => !StellarAccountAddress.IsValid(x.SourceAccountId) ||
                    x.TransactionHash is not { Length: 64 } ||
                    !x.TransactionHash.All(Uri.IsHexDigit) ||
                    !stagedOwners.Contains(x.SourceAccountId, StringComparer.Ordinal)) ||
                journal.Select(x => x.SourceAccountId).Distinct(StringComparer.Ordinal).Count() != journal.Length ||
                journal.Select(x => x.TransactionHash).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Length ||
                VaultHybridRemoteMutationWorkspace.Fingerprint(
                    new LedgerRecordItem(original.EntityId, intent.Original.Title,
                        intent.Original.Content, intent.Original.FolderId,
                        intent.Original.Tags, intent.Original.IsFolder,
                        intent.Original.UserName, intent.Original.Url, intent.Original.Notes)) != baseline)
                return Reject("Signed journal or original staged manifest-owner baseline is malformed.");

            var entries = journal.ToDictionary(x => x.SourceAccountId, StringComparer.Ordinal);
            var results = new List<OwnerFinding>(stagedOwners.Length);
            var allAbsent = true;
            foreach (var owner in stagedOwners.Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (!entries.TryGetValue(owner, out var signed))
                {
                    allAbsent = false;
                    results.Add(new(owner, null, OwnerStatus.NotCheckpointed,
                        "No signed tombstone identity was durably recorded for this HD owner. This does not prove the transaction was never sent.",
                        false));
                    continue;
                }
                // Scope the existing strict historical verifier to EXACTLY one
                // HD owner. It authenticates this transaction's operation(s),
                // connected derived HD child, historical predecessor manifest,
                // encrypted chunks, registry and the approved original draft.
                var finding = await tombstones.InspectAsync(
                    accountId, scope, signed.TransactionHash,
                    original.EntityId,
                    intent with { ManifestSourceAccounts = [owner] }, ct);
                var verified = finding.ManifestDeletionMatched &&
                    finding.AuthenticatedHdSources && finding.StagedSourcesMatched &&
                    finding.HistoricalPredecessorMatched &&
                    finding.DeletedManifestCount == 1;
                if (!finding.CurrentRecordAbsent) allAbsent = false;
                results.Add(new(owner, signed.TransactionHash,
                    verified ? OwnerStatus.VerifiedHistoricalTombstone :
                        OwnerStatus.UnresolvedTransaction,
                    finding.Explanation, finding.HistoricalPredecessorMatched));
            }

            var after = await store.GetAsync(accountId, scope, mutationId, ct);
            if (after is null || !SameRevision(original, after) ||
                !sessions.IsUnlocked || sessions.Epoch != epoch ||
                sessions.ActiveAccountId != accountId ||
                networks.Network != network ||
                networks.CustomHorizonUrl != horizon ||
                networks.CustomPassphrase != passphrase ||
                networks.CredentialKey(accountId) != scope)
                return Reject("Queue, network or unlocked session changed during inspection. Refresh the journal.");

            var verifiedCount = results.Count(x => x.Status == OwnerStatus.VerifiedHistoricalTombstone);
            var missingCount = results.Count(x => x.Status == OwnerStatus.NotCheckpointed);
            var unresolvedCount = results.Count - verifiedCount - missingCount;
            var allVerified = verifiedCount == stagedOwners.Length;
            return new(results, verifiedCount, unresolvedCount, missingCount,
                allAbsent && allVerified, allVerified,
                allVerified
                    ? "All staged manifest owners have matching authenticated historical tombstone transactions and predecessor records. This is NOT atomic deletion or queue confirmation: concurrent/later writes may exist. Do not retry."
                    : "Deletion journal is incomplete or has unresolved historical evidence. No queue state changed. Preserve every signed identity; do not retry.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return Reject("Historical journal inspection failed closed. No queue changes were made.");
        }
    }

    private static bool SameRevision(VaultPendingMutation a, VaultPendingMutation b) =>
        a.Id == b.Id && a.AccountId == b.AccountId &&
        a.NetworkId == b.NetworkId && a.Kind == b.Kind &&
        a.EntityId == b.EntityId && a.State == b.State &&
        a.TransactionHash == b.TransactionHash &&
        a.ExpectedRemoteVersion == b.ExpectedRemoteVersion &&
        a.CreatedUtc == b.CreatedUtc &&
        a.DependsOn.SequenceEqual(b.DependsOn, StringComparer.Ordinal) &&
        a.EncryptedPayload.AsSpan().SequenceEqual(b.EncryptedPayload) &&
        (a.SignedDeletionManifests ?? []).SequenceEqual(
            b.SignedDeletionManifests ?? []);
}
