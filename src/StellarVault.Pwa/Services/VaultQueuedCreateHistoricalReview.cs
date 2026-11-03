using System.Text.Json;
using StellarVault.Core.Services;
using StellarVaultPwa.Core;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Read-only evidence review for an already-reserved queued create. Neither
/// authorizes submission nor implements IVaultLedgerMutationVerifier.
/// No draft contents or signing material are returned in the result.
/// </summary>
public sealed class VaultQueuedCreateHistoricalReview(
    VaultNetworkSelection selection,
    HorizonVaultPublicationDiagnostic diagnostic,
    IVaultPendingMutationStore store,
    VaultDeviceRetentionGate retention)
{
    public sealed record Finding(
        bool ScopeMatched,
        bool HistoricalRecordMatched,
        string Explanation);

    public Task<Finding> InspectAsync(
        VaultPendingMutation mutation, string transactionHash,
        string expectedNodeAccount, int expectedNodeIndex,
        CancellationToken ct = default) =>
        InspectCoreAsync(mutation, transactionHash, expectedNodeAccount,
            expectedNodeIndex, false, ct);

    public Task<Finding> InspectUnrecordedCandidateAsync(
        VaultPendingMutation mutation, string candidateHash,
        string expectedNodeAccount, int expectedNodeIndex,
        CancellationToken ct = default) =>
        InspectCoreAsync(mutation, candidateHash, expectedNodeAccount,
            expectedNodeIndex, true, ct);

    private async Task<Finding> InspectCoreAsync(
        VaultPendingMutation mutation, string transactionHash,
        string expectedNodeAccount, int expectedNodeIndex,
        bool allowUnrecordedCandidate, CancellationToken ct)
    {
        static Finding Reject(string reason) => new(false, false, reason);

        if (mutation is null ||
            mutation.State is not (VaultSyncState.Submitting or VaultSyncState.Uncertain) ||
            mutation.Kind is not (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder) ||
            transactionHash is not { Length: 64 } || !transactionHash.All(Uri.IsHexDigit) ||
            (allowUnrecordedCandidate
                ? mutation.TransactionHash is not null
                : mutation.TransactionHash is not { Length: 64 } storedHash ||
                  !storedHash.All(Uri.IsHexDigit) ||
                  !string.Equals(storedHash, transactionHash, StringComparison.OrdinalIgnoreCase)) ||
            mutation.EntityId is not { Length: 16 } || !mutation.EntityId.All(Uri.IsHexDigit) ||
            mutation.AccountId is not { Length: 56 } || mutation.AccountId[0] != 'G' ||
            !mutation.AccountId.All(c => c is >= 'A' and <= 'Z' or >= '2' and <= '7') ||
            mutation.Id is not { Length: 32 } || !Guid.TryParseExact(mutation.Id, "N", out _) ||
            mutation.EncryptedPayload is not { Length: > 0 and <= 8_388_608 } ||
            expectedNodeAccount is not { Length: 56 } || expectedNodeAccount[0] != 'G' ||
            !expectedNodeAccount.All(c => c is >= 'A' and <= 'Z' or >= '2' and <= '7') ||
            expectedNodeIndex is <= 0 or >= 64)
            return Reject("Mutation identity, final hash, or HD child inputs are invalid.");

        // The supplied object may be stale or fabricated. Compare with the
        // durable, authenticated queue entry before any Horizon network access.
        VaultPendingMutation? durable;
        try
        {
            retention.RequireLocalPersistence();
            durable = await store.GetAsync(mutation.AccountId, mutation.NetworkId, mutation.Id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Reject("Durable queued mutation is unavailable."); }
        if (durable is null || !SameRevision(durable, mutation))
            return Reject("Queued mutation changed or is not the current durable revision.");

        string scopedNetwork;
        try { scopedNetwork = selection.CredentialKey(mutation.AccountId); }
        catch (Exception) { return Reject("The currently selected network has no valid account scope."); }
        if (!string.Equals(mutation.NetworkId, scopedNetwork, StringComparison.Ordinal))
            return Reject("Queued mutation belongs to a different account or Stellar network.");

        string endpoint;
        string passphrase;
        switch (selection.Network)
        {
            case "testnet":
                endpoint = "https://horizon-testnet.stellar.org";
                passphrase = StellarNetworkPassphrases.Testnet;
                break;
            case "public":
                endpoint = "https://horizon.stellar.org";
                passphrase = StellarNetworkPassphrases.Public;
                break;
            case "custom":
                endpoint = selection.CustomHorizonUrl;
                passphrase = selection.CustomPassphrase ?? string.Empty;
                break;
            default:
                return Reject("Unsupported selected Stellar network.");
        }

        var selectedNetwork = selection.Network;
        if (string.IsNullOrWhiteSpace(passphrase) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var address) ||
            address.Scheme != Uri.UriSchemeHttps || address.UserInfo.Length != 0 ||
            address.Query.Length != 0 || address.Fragment.Length != 0 ||
            address.AbsolutePath != "/")
            return Reject("Selected Horizon endpoint is not an explicit HTTPS root.");

        LedgerRecordDraft draft;
        try
        {
            draft = JsonSerializer.Deserialize<LedgerRecordDraft>(mutation.EncryptedPayload)
                ?? throw new JsonException("Missing draft.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return Reject("Stored queued create payload cannot be decoded.");
        }

        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 256 ||
            string.IsNullOrEmpty(draft.Content) ||
            draft.IsFolder != (mutation.Kind == VaultMutationKind.CreateFolder) ||
            draft.Tags is { Length: > 24 } ||
            draft.Tags?.Any(tag => tag is null || tag.Length > 64) == true ||
            string.Equals(draft.FolderId, mutation.EntityId, StringComparison.OrdinalIgnoreCase))
            return Reject("Stored draft is not a supported record or folder creation.");

        try
        {
            ct.ThrowIfCancellationRequested();
            var finding = await diagnostic.InspectHistoricalRecordAsync(
                endpoint, passphrase, transactionHash, mutation.EntityId,
                mutation.AccountId, expectedNodeAccount, expectedNodeIndex, draft, ct);
            // A long-running history scan can race edits, lock/unlock, a
            // network switch or reconciliation in another tab. Never report a
            // positive diagnostic for a stale queue revision.
            var after = await store.GetAsync(mutation.AccountId, mutation.NetworkId, mutation.Id, ct);
            if (after is null || !SameRevision(durable, after) ||
                !string.Equals(selection.CredentialKey(mutation.AccountId),
                    mutation.NetworkId, StringComparison.Ordinal) ||
                selection.Network != selectedNetwork ||
                (selectedNetwork == "custom" &&
                 (!string.Equals(selection.CustomHorizonUrl, endpoint, StringComparison.Ordinal) ||
                  !string.Equals(selection.CustomPassphrase, passphrase, StringComparison.Ordinal))))
                return Reject("Queue revision, endpoint or selected network changed during inspection.");
            var matched = finding.EndpointNetworkMatches &&
                finding.HistoricalChunksAvailable && finding.HistoricalRecordMatches;
            return new(true, matched, allowUnrecordedCandidate
                ? matched
                    ? "Matching candidate content is NOT evidence that the interrupted local submission sent this transaction. No queue state or hash was changed."
                    : "Candidate history did not match. No queue state or hash was changed."
                : finding.Explanation);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Reject("Queued-create historical review failed closed."); }
    }

    private static bool SameRevision(VaultPendingMutation a, VaultPendingMutation b) =>
        a.Id == b.Id && a.AccountId == b.AccountId && a.NetworkId == b.NetworkId &&
        a.Kind == b.Kind && a.EntityId == b.EntityId &&
        a.EncryptedPayload is not null && b.EncryptedPayload is not null &&
        a.EncryptedPayload.AsSpan().SequenceEqual(b.EncryptedPayload) &&
        a.ExpectedRemoteVersion == b.ExpectedRemoteVersion &&
        a.DependsOn is not null && b.DependsOn is not null &&
        a.DependsOn.SequenceEqual(b.DependsOn, StringComparer.Ordinal) &&
        a.State == b.State && a.TransactionHash == b.TransactionHash &&
        a.CreatedUtc == b.CreatedUtc;
}
