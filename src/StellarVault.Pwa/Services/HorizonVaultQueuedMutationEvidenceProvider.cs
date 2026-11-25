using System.Text.Json;
using VaultEngine.Stellar.Services;
using StellarVaultPwa.Core;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Shared PWA/MAUI host transport adapter supplying proof to the Core
/// EvidenceBasedVaultLedgerMutationVerifier. Only a recorded transaction hash
/// with authenticated historical manifest, chunks, HD registry and decrypted
/// approved draft can become independent confirmation evidence.
/// </summary>
public sealed class HorizonVaultQueuedMutationEvidenceProvider(
    VaultNetworkSelection networks,
    VaultSessionManager sessions,
    VaultDeviceRetentionGate retention,
    IStellarLedgerRecords ledger,
    BrowserStellarLedgerRecords connected,
    HorizonVaultManifestInspector manifests,
    HorizonVaultPublicationDiagnostic diagnostic)
    : IVaultLedgerEvidenceProvider
{
    public async Task<VaultLedgerEvidence?> InspectAsync(
        VaultPendingMutation mutation, string transactionHash,
        CancellationToken ct = default)
    {
        if (mutation is null ||
            mutation.Kind is not (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder or
                VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder) ||
            mutation.State is not (VaultSyncState.Uncertain or VaultSyncState.Submitting) ||
            mutation.TransactionHash is not { Length: 64 } stored ||
            !stored.All(Uri.IsHexDigit) ||
            transactionHash is not { Length: 64 } ||
            !transactionHash.All(Uri.IsHexDigit) ||
            !string.Equals(stored, transactionHash, StringComparison.OrdinalIgnoreCase) ||
            !StellarAccountAddress.IsValid(mutation.AccountId) ||
            mutation.EntityId is not { Length: 16 } ||
            !mutation.EntityId.All(Uri.IsHexDigit) ||
            !retention.AllowsLocalPersistence || !sessions.IsUnlocked ||
            sessions.ActiveAccountId != mutation.AccountId ||
            !ledger.IsConnected || !connected.IsConnected)
            return null;

        var sessionEpoch = sessions.Epoch;
        var selected = networks.Network;
        var customPassphrase = networks.CustomPassphrase;
        var customHorizon = networks.CustomHorizonUrl;
        string scope, endpoint, passphrase;
        try
        {
            scope = networks.CredentialKey(mutation.AccountId);
            if (scope != mutation.NetworkId) return null;
            (endpoint, passphrase) = selected switch
            {
                "testnet" => ("https://horizon-testnet.stellar.org", StellarNetworkPassphrases.Testnet),
                "public" => ("https://horizon.stellar.org", StellarNetworkPassphrases.Public),
                "custom" => (customHorizon, customPassphrase ?? ""),
                _ => ("", "")
            };
            if (string.IsNullOrWhiteSpace(passphrase) ||
                !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                uri.AbsolutePath != "/")
                return null;

            // Queued remote updates are experimental Testnet-only. A historical
            // update match proves its final value was written, but not that
            // the pinned predecessor was atomically checked by Stellar.
            if (mutation.Kind is VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder &&
                selected != "testnet") return null;
            var root = await ledger.InspectAccountInfoAsync();
            if (root.RootAccountId != mutation.AccountId) return null;
            var manifest = await manifests.InspectManifestAsync(
                endpoint, transactionHash, mutation.EntityId, ct);
            if (manifest is null) return null;
            var childIndex = await connected.ResolveAuthenticatedHdChildAsync(
                mutation.AccountId, manifest.OperationSourceAccount, endpoint, passphrase, ct);
            if (childIndex is null) return null;

            LedgerRecordDraft? draft;
            try { draft = JsonSerializer.Deserialize<LedgerRecordDraft>(mutation.EncryptedPayload); }
            catch (JsonException) { return null; }
            if (draft is null || string.IsNullOrEmpty(draft.Content) ||
                string.IsNullOrWhiteSpace(draft.Title) ||
                draft.IsFolder != (mutation.Kind is VaultMutationKind.CreateFolder or VaultMutationKind.UpdateFolder))
                return null;

            var historical = await diagnostic.InspectHistoricalRecordAsync(
                endpoint, passphrase, transactionHash, mutation.EntityId,
                mutation.AccountId, manifest.OperationSourceAccount,
                childIndex.Value, draft, ct);
            if (!historical.EndpointNetworkMatches ||
                !historical.HistoricalChunksAvailable ||
                !historical.HistoricalRecordMatches)
                return null;
            ct.ThrowIfCancellationRequested();
            if (!retention.AllowsLocalPersistence || !sessions.IsUnlocked ||
                sessions.ActiveAccountId != mutation.AccountId ||
                sessions.Epoch != sessionEpoch ||
                !connected.IsConnected || !ledger.IsConnected ||
                networks.Network != selected ||
                networks.CustomPassphrase != customPassphrase ||
                networks.CustomHorizonUrl != customHorizon ||
                networks.CredentialKey(mutation.AccountId) != scope)
                return null;

            // Historical comparison authenticated *this* exact approved draft
            // against ciphertext and manifest published by the transaction.
            // The returned record is a proof result, not a current-state claim:
            // a later ledger update/delete need not invalidate past publication.
            return new VaultLedgerEvidence(mutation.AccountId, scope,
                transactionHash.ToLowerInvariant(), true, mutation.EntityId,
                new LedgerRecordItem(mutation.EntityId, draft.Title, draft.Content,
                    draft.FolderId, draft.Tags, draft.IsFolder, draft.UserName,
                    draft.Url, draft.Notes), RecordLinkedToTransaction: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }
}
