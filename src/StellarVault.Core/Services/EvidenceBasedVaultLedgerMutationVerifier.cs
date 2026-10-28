using System.Security.Cryptography;
using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>Verified transaction receipt and independently read record state.
 /// Providers must bind both to the selected Stellar network.</summary>
public sealed record VaultLedgerEvidence(
    string AccountId, string NetworkId, string TransactionHash,
    bool TransactionSucceeded, string RecordId, LedgerRecordItem? Record,
    bool RecordLinkedToTransaction = false);

public interface IVaultLedgerEvidenceProvider
{
    Task<VaultLedgerEvidence?> InspectAsync(VaultPendingMutation mutation,
        string transactionHash, CancellationToken ct = default);
}

/// <summary>
/// Strict content-and-receipt verifier for record and folder creation.
/// Does not claim a queue mutation is confirmed merely because its hash exists.
/// Unsupported kinds fail closed.
/// </summary>
public sealed class EvidenceBasedVaultLedgerMutationVerifier(IVaultLedgerEvidenceProvider provider)
    : IVaultLedgerMutationVerifier
{
    public async Task<bool> IsConfirmedAsync(VaultPendingMutation mutation,
        string transactionHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(transactionHash) ||
            mutation.Kind is not (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder) ||
            transactionHash.Length != 64 || !transactionHash.All(Uri.IsHexDigit) ||
            mutation.State is not (VaultSyncState.Uncertain or VaultSyncState.Submitting) ||
            mutation.EntityId.Length != 16 || !mutation.EntityId.All(Uri.IsHexDigit))
            return false;
        if (mutation.TransactionHash is not null &&
            !string.Equals(mutation.TransactionHash, transactionHash, StringComparison.OrdinalIgnoreCase))
            return false;
        VaultLedgerEvidence? evidence;
        try { evidence = await provider.InspectAsync(mutation, transactionHash, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; } // Evidence unavailable or invalid must never confirm a mutation.
        if (evidence is null || !evidence.TransactionSucceeded ||
            !evidence.RecordLinkedToTransaction ||
            evidence.AccountId != mutation.AccountId ||
            evidence.NetworkId != mutation.NetworkId ||
            !string.Equals(evidence.TransactionHash, transactionHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(evidence.RecordId, mutation.EntityId, StringComparison.OrdinalIgnoreCase) || evidence.Record is null)
            return false;
        LedgerRecordDraft? draft;
        try { draft = JsonSerializer.Deserialize<LedgerRecordDraft>(mutation.EncryptedPayload); }
        catch (JsonException) { return false; }
        if (draft is null || string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 256 ||
            string.IsNullOrEmpty(draft.Content)) return false;
        if (draft.IsFolder != (mutation.Kind == VaultMutationKind.CreateFolder) ||
            draft.Tags is { Length: > 24 } ||
            draft.Tags?.Any(tag => tag is null || tag.Length > 64) == true)
            return false;
        var actual = evidence.Record;
        return string.Equals(actual.Id, mutation.EntityId, StringComparison.OrdinalIgnoreCase) &&
            actual.Title == draft.Title && actual.Content == draft.Content &&
            actual.FolderId == draft.FolderId && actual.IsFolder == draft.IsFolder &&
            actual.UserName == draft.UserName && actual.Url == draft.Url &&
            actual.Notes == draft.Notes &&
            (actual.Tags ?? []).SequenceEqual(draft.Tags ?? [], StringComparer.Ordinal);
    }
}
