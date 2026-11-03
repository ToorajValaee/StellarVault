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
    HorizonVaultDeletionInspector inspector)
    : IVaultDeletionTombstoneReader
{
    public async Task<VaultDeletionTombstoneFinding> InspectAsync(
        string accountId, string networkId, string transactionHash,
        string recordId, IReadOnlyList<string> expectedManifestSources,
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
                expectedManifestSources, ct);
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
                finding.Explanation, finding.StagedSourcesMatched);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Reject("Historical tombstone inspection failed closed."); }
    }
}
