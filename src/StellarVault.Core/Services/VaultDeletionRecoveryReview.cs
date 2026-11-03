namespace StellarVault.Core.Services;

/// <summary>
/// Strict, host-neutral manual recovery boundary for a recorded-hash queued
/// delete. The host evidence reader is diagnostic only and can NEVER mark the
/// mutation Confirmed, Failed, Conflict, or otherwise modify the queue.
/// </summary>
public interface IVaultDeletionTombstoneReader
{
    Task<VaultDeletionTombstoneFinding> InspectAsync(
        string accountId, string networkId, string transactionHash,
        string recordId, CancellationToken ct = default);
}

public sealed record VaultDeletionTombstoneFinding(
    bool ManifestDeletionMatched,
    bool AuthenticatedHdSources,
    bool CurrentRecordAbsent,
    int DeletedManifestCount,
    string Explanation);

public sealed class VaultDeletionRecoveryReview(
    IVaultPendingMutationStore store,
    IVaultDeletionTombstoneReader reader,
    VaultDeviceRetentionGate retention,
    VaultSessionManager sessions,
    VaultNetworkSelection networks)
{
    public async Task<VaultDeletionTombstoneFinding> InspectAsync(
        string accountId, string mutationId, CancellationToken ct = default)
    {
        static VaultDeletionTombstoneFinding Reject(string reason) =>
            new(false, false, false, 0, reason);
        try
        {
            retention.RequireLocalPersistence();
            if (!sessions.IsUnlocked || sessions.ActiveAccountId != accountId)
                return Reject("Unlock the matching local vault to investigate deletion.");
            var epoch = sessions.Epoch;
            var selected = networks.Network;
            var passphrase = networks.CustomPassphrase;
            var endpoint = networks.CustomHorizonUrl;
            var scope = networks.CredentialKey(accountId);
            var original = await store.GetAsync(accountId, scope, mutationId, ct);
            if (original is null ||
                original.Kind is not (VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder) ||
                original.State is not (VaultSyncState.Uncertain or VaultSyncState.Submitting) ||
                original.TransactionHash is not { Length: 64 } hash ||
                !hash.All(Uri.IsHexDigit) ||
                original.ExpectedRemoteVersion is not { Length: 64 } baseline ||
                !baseline.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                return Reject("This is not an unresolved version-pinned deletion with a recorded transaction hash.");

            var finding = await reader.InspectAsync(accountId, scope,
                hash, original.EntityId, ct);
            // A network round trip cannot authorize evidence for a stale
            // queue snapshot or a changed unlocked session.
            var after = await store.GetAsync(accountId, scope, mutationId, ct);
            if (after is null || !SameRevision(original, after) ||
                !sessions.IsUnlocked || sessions.ActiveAccountId != accountId ||
                sessions.Epoch != epoch ||
                networks.Network != selected ||
                networks.CustomPassphrase != passphrase ||
                networks.CustomHorizonUrl != endpoint ||
                networks.CredentialKey(accountId) != scope)
                return Reject("Queue, account, network or session changed while inspecting deletion.");
            return finding;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Reject("Deletion history is unavailable. No queue changes were made."); }
    }

    private static bool SameRevision(VaultPendingMutation a, VaultPendingMutation b) =>
        a.Id == b.Id && a.AccountId == b.AccountId && a.NetworkId == b.NetworkId &&
        a.Kind == b.Kind && a.EntityId == b.EntityId &&
        a.ExpectedRemoteVersion == b.ExpectedRemoteVersion &&
        a.EncryptedPayload.AsSpan().SequenceEqual(b.EncryptedPayload) &&
        a.DependsOn.SequenceEqual(b.DependsOn, StringComparer.Ordinal) &&
        a.State == b.State && a.TransactionHash == b.TransactionHash &&
        a.CreatedUtc == b.CreatedUtc;
}
