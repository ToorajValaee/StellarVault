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
        string Explanation, string? TransactionHash = null,
        bool HistoricalRecordOperationsMatch = false,
        string? HistoricalStorageAccount = null,
        bool HistoricalExpansionOperationsMatch = false,
        string? HistoricalParentAccount = null);

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
            var historicalRecordOperations = false;
            string? historicalStorageAccount = null;
            try
            {
                if (item.Kind == VaultMutationKind.ExtendLinkedChain)
                {
                    // Tail visibility is current-state evidence only. A later
                    // expansion could have moved the tail again, and even a
                    // matching tail is not historical operation proof.
                    var chain = await ledger.InspectAccountInfoAsync(ct);
                    matches = chain.TotalAccounts > 1 &&
                        string.Equals(chain.TailAccountId, item.EntityId,
                            StringComparison.Ordinal);
                }
                else
                {
                    var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(item.EncryptedPayload);
                    var current = await ledger.ReadAsync(ct);
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
                }
                RequireSameScope(root, scope, epoch);

                // The durable hash is the exact signed transaction identity
                // captured before broadcast. The volatile runtime diagnostic is
                // optional after an app restart. Reconstruct only a public-only
                // receipt query from the selected network, never from untrusted
                // queue payload or a caller-controlled Horizon URL.
                var diagnostic = BuildDurableReceiptQuery(root, item);
                if (diagnostic is not null)
                {
                    RequireSameScope(root, scope, epoch);
                    var evidence = await receipts.InspectAsync(diagnostic, ct);
                    receipt = evidence.ReceiptObserved;
                    historicalRecordOperations = evidence.HistoricalRecordOperationsMatch;
                    historicalStorageAccount = evidence.StorageAccount;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                // A failed Horizon/decryption/receipt read must not change the
                // durable queue state or turn an unknown result into "absent".
                matches = null;
                receipt = false;
                historicalRecordOperations = false;
                historicalStorageAccount = null;
            }

            RequireSameScope(root, scope, epoch);
            return new(item.Id, item.EntityId, item.State, matches, receipt,
                item.Kind == VaultMutationKind.ExtendLinkedChain
                    ? "Current linked tail inspection is advisory only. A matching or different tail does not prove the exact historical account-creation transaction, signer authorization, or permission to retry."
                    : matches switch
                {
                    true => "A matching record is visible now. This does NOT prove the exact submitted transaction or authorize retry, release or queue confirmation.",
                    false => "The expected record is not visible with matching fields. This does NOT prove the submission failed. Do not retry.",
                    _ => "The remote record could not be established. Submission outcome remains unknown; do not retry."
                }, item.TransactionHash, historicalRecordOperations,
                historicalStorageAccount);
        }
        finally
        {
            foreach (var entry in entries)
                CryptographicOperations.ZeroMemory(entry.EncryptedPayload);
        }
    }

    private VaultSecretSubmissionDiagnostic? BuildDurableReceiptQuery(
        string root, VaultPendingMutation item)
    {
        if (item.TransactionHash is not { Length: 64 } hash ||
            !hash.All(Uri.IsHexDigit))
            return null;

        // Resolve the horizon endpoint from the already-selected network,
        // not the encrypted draft and not volatile session diagnostics.
        var expectedPassphrase = network.Network switch
        {
            "testnet" => "Test SDF Network ; September 2015",
            "public" => "Public Global Stellar Network ; September 2015",
            "custom" => network.CustomPassphrase,
            _ => null
        };
        var horizon = network.Network switch
        {
            "testnet" => "https://horizon-testnet.stellar.org",
            "public" => "https://horizon.stellar.org",
            "custom" => network.CustomHorizonUrl,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(expectedPassphrase) ||
            ledger.ConnectedNetworkPassphrase != expectedPassphrase ||
            !Uri.TryCreate(horizon, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            return null;

        var expansion = item.Kind == VaultMutationKind.ExtendLinkedChain;
        if (!expansion && item.Kind is not
            (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder))
            return null;
        // The root is the signed transaction's source. The exact record
        // storage node cannot be established from a durable create envelope
        // alone; this diagnostic deliberately checks receipt existence only.
        return new VaultSecretSubmissionDiagnostic(
            expansion ? "expand" : "create", root,
            expansion ? item.EntityId : root,
            expansion ? null : item.EntityId, hash.ToLowerInvariant(),
            expectedPassphrase, uri.GetLeftPart(UriPartial.Authority));
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
