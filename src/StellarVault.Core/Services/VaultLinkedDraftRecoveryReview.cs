using System.Security.Cryptography;
using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// Read-only assessment of durable independent S-key queue submissions whose
/// transport outcome is not verified. Record visibility, even with matching
/// fields, is not evidence of exact historical operations or inclusion.
/// This service NEVER moves an entry to Confirmed, Pending, or Failed.
/// </summary>
public sealed class VaultLinkedDraftRecoveryReview(
    IStellarSecretKeyLedger ledger,
    IVaultLinkedDraftStore store,
    VaultLinkedQueueKeySession keys,
    VaultLinkedLocalRetentionGate consent,
    VaultNetworkSelection network,
    IVaultLinkedReceiptEvidence receipts)
{
    public sealed record Finding(
        string MutationId, string EntityId, VaultSyncState DurableState,
        bool? CurrentRecordMatches, bool HistoricalReceiptObserved,
        string Explanation);

    public async Task<Finding> InspectAsync(
        string mutationId, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(mutationId, "N", out _))
            throw new ArgumentException("Invalid mutation identifier.", nameof(mutationId));
        consent.RequireLocalPersistence();
        if (!keys.IsUnlocked || !ledger.IsConnected ||
            ledger.RootAccountId is not { } root)
            throw new UnauthorizedAccessException(
                "Connect the independent S-key account and unlock its encrypted queue.");

        var epoch = ledger.ConnectionEpoch;
        var scope = network.CredentialKey(root);
        var entries = await store.ListAsync(root, scope, ct);
        try
        {
            var item = entries.SingleOrDefault(x => x.Id == mutationId)
                ?? throw new KeyNotFoundException("Encrypted submission journal not found.");
            if (item.AccountId != root ||
                item.NetworkId != VaultLinkedQueueStorageScope.FromNetworkScope(scope) ||
                item.OriginStrategyId != VaultMutationStrategyPolicy.Linked ||
                item.State is not (VaultSyncState.Submitting or VaultSyncState.Uncertain))
                throw new InvalidOperationException(
                    "This entry is not an unresolved independent linked submission.");

            bool? matches = null;
            var receipt = false;
            try
            {
                var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(item.EncryptedPayload);
                var current = await ledger.ReadAsync(ct);
                RequireSameScope(root, scope, epoch);
                if (draft is not null && item.Kind is
                    (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder))
                {
                    var found = current.SingleOrDefault(x => x.Id == item.EntityId);
                    matches = found is not null &&
                        found.IsFolder == draft.IsFolder &&
                        found.Title == draft.Title &&
                        found.Content == draft.Content &&
                        found.FolderId == draft.FolderId &&
                        (found.UserName ?? "") == (draft.UserName ?? "") &&
                        (found.Url ?? "") == (draft.Url ?? "") &&
                        (found.Notes ?? "") == (draft.Notes ?? "") &&
                        (found.Tags ?? []).SequenceEqual(
                            draft.Tags ?? [], StringComparer.Ordinal);
                }

                // Only a matching public-only diagnostic can supply advisory
                // receipt evidence. A hash by itself does not bind operations.
                var diagnostic = await ledger.InspectUncertainSubmissionAsync(ct);
                RequireSameScope(root, scope, epoch);
                if (diagnostic is not null && item.TransactionHash is { Length: 64 } hash &&
                    string.Equals(diagnostic.TransactionHash, hash,
                        StringComparison.OrdinalIgnoreCase) &&
                    diagnostic.RootAccountId == root &&
                    diagnostic.RecordId == item.EntityId)
                {
                    var evidence = await receipts.InspectAsync(diagnostic, ct);
                    receipt = evidence.ReceiptObserved;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                // A failed Horizon/decryption/receipt read must not change the
                // durable queue state or turn an unknown result into "absent".
                matches = null;
                receipt = false;
            }

            RequireSameScope(root, scope, epoch);
            return new(item.Id, item.EntityId, item.State, matches, receipt,
                matches switch
                {
                    true => "A matching record is visible now. This does NOT prove the exact submitted transaction or authorize retry, release or queue confirmation.",
                    false => "The expected record is not visible with matching fields. This does NOT prove the submission failed. Do not retry.",
                    _ => "The remote record could not be established. Submission outcome remains unknown; do not retry."
                });
        }
        finally
        {
            foreach (var entry in entries)
                CryptographicOperations.ZeroMemory(entry.EncryptedPayload);
        }
    }

    private void RequireSameScope(string root, string scope, long epoch)
    {
        consent.RequireLocalPersistence();
        if (!keys.IsUnlocked || !ledger.IsConnected ||
            ledger.RootAccountId != root ||
            ledger.ConnectionEpoch != epoch ||
            network.CredentialKey(root) != scope)
            throw new InvalidOperationException(
                "S-key session or encrypted queue scope changed during recovery assessment.");
    }
}
