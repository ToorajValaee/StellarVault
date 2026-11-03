using StellarVault.Core.Services;
using StellarVaultPwa.Core;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Binds the common read-only recovery contract to the same connected HD
/// session and selected Horizon passphrase used by the browser/native host.
/// Never implements IVaultLedgerMutationVerifier.
/// </summary>
public sealed class HorizonVaultDeletionTombstoneReader(
    VaultNetworkSelection networks,
    VaultDeviceRetentionGate retention,
    VaultSessionManager sessions,
    IStellarLedgerRecords ledger,
    HorizonVaultDeletionInspector inspector,
    HorizonVaultDeletionPredecessorInspector predecessors,
    HorizonVaultManifestInspector manifests,
    HorizonVaultPublicationDiagnostic historical,
    BrowserStellarLedgerRecords connected)
    : IVaultDeletionTombstoneReader
{
    public async Task<VaultDeletionTombstoneFinding> InspectAsync(
        string accountId, string networkId, string transactionHash,
        string recordId, VaultDeletionIntent expectedIntent,
        CancellationToken ct = default)
    {
        static VaultDeletionTombstoneFinding Reject(string explanation) =>
            new(false, false, false, 0, explanation);
        try
        {
            retention.RequireLocalPersistence();
            if (!sessions.IsUnlocked || sessions.ActiveAccountId != accountId ||
                !ledger.IsConnected || networks.CredentialKey(accountId) != networkId)
                return Reject("Deletion review requires a connected matching HD wallet and local vault.");
            var epoch = sessions.Epoch;
            var selected = networks.Network;
            var customEndpoint = networks.CustomHorizonUrl;
            var customPassphrase = networks.CustomPassphrase;
            var (endpoint, passphrase) = selected switch
            {
                "testnet" => ("https://horizon-testnet.stellar.org", StellarNetworkPassphrases.Testnet),
                "public" => ("https://horizon.stellar.org", StellarNetworkPassphrases.Public),
                "custom" => (customEndpoint, customPassphrase ?? string.Empty),
                _ => (string.Empty, string.Empty)
            };
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(passphrase))
                return Reject("Selected Stellar network does not support historical review.");
            var finding = await inspector.InspectAsync(
                endpoint, passphrase, accountId, transactionHash, recordId,
                expectedIntent.ManifestSourceAccounts, ct);
            var predecessorMatched = false;
            if (finding.StagedSourcesMatched && finding.SourcesBelongToAuthenticatedHdChain &&
                finding.DeletedManifestOperations is { Count: > 0 } deletions &&
                deletions.Count == expectedIntent.ManifestSourceAccounts.Length)
            {
                predecessorMatched = true;
                foreach (var deletion in deletions)
                {
                    ct.ThrowIfCancellationRequested();
                    var candidate = await predecessors.FindLatestManifestWriteAsync(
                        endpoint, deletion.AccountId, recordId, deletion.OperationId, ct);
                    if (candidate is null ||
                        string.Equals(candidate.TransactionHash, transactionHash,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        predecessorMatched = false;
                        break;
                    }
                    var manifest = await manifests.InspectManifestAsync(
                        endpoint, candidate.TransactionHash, recordId, ct);
                    if (manifest is null ||
                        manifest.ManifestOperationId != candidate.ManifestOperationId ||
                        manifest.OperationSourceAccount != deletion.AccountId)
                    {
                        predecessorMatched = false;
                        break;
                    }
                    var childIndex = await connected.ResolveAuthenticatedHdChildAsync(
                        accountId, deletion.AccountId, endpoint, passphrase, ct);
                    if (childIndex is null)
                    {
                        predecessorMatched = false;
                        break;
                    }
                    var match = await historical.InspectHistoricalRecordAsync(
                        endpoint, passphrase, candidate.TransactionHash, recordId,
                        accountId, deletion.AccountId, childIndex.Value,
                        expectedIntent.Original, ct);
                    if (!match.EndpointNetworkMatches ||
                        !match.HistoricalChunksAvailable ||
                        !match.HistoricalRecordMatches)
                    {
                        predecessorMatched = false;
                        break;
                    }
                }
            }
            if (sessions.Epoch != epoch || !sessions.IsUnlocked ||
                sessions.ActiveAccountId != accountId || !ledger.IsConnected ||
                networks.Network != selected ||
                networks.CustomHorizonUrl != customEndpoint ||
                networks.CustomPassphrase != customPassphrase ||
                networks.CredentialKey(accountId) != networkId)
                return Reject("Network or unlocked account changed during deletion review.");
            return new(finding.NetworkMatched && finding.TransactionSucceeded &&
                    finding.AllOperationsAreTargetManifestDeletes,
                finding.SourcesBelongToAuthenticatedHdChain,
                finding.RecordCurrentlyAbsent, finding.ManifestDeletes,
                predecessorMatched
                    ? finding.Explanation + " All staged sources had historically authenticated predecessor records matching the encrypted intent. Still not an atomic deletion confirmation."
                    : finding.Explanation + " Historical predecessor coverage is incomplete or differs from the staged record.",
                finding.StagedSourcesMatched, predecessorMatched);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Reject("Historical tombstone inspection failed closed."); }
    }
}
