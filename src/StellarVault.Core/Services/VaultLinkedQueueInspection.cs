using System.Security.Cryptography;

namespace StellarVault.Core.Services;

/// <summary>
/// Inspect the existing encrypted queue format under an independently unlocked,
/// consented S-key key and storage namespace. No HD queue credentials, signing
/// session or ciphertext are ever passed into this strategy-specific inspection.
/// This is NOT permission to publish or automatically migrate local drafts.
/// </summary>
public sealed class VaultLinkedQueueInspection(
    IStellarSecretKeyLedger linked,
    IVaultLinkedQueueSnapshotStore store,
    VaultLinkedQueueKeySession keys,
    VaultLinkedLocalRetentionGate linkedRetention,
    VaultNetworkSelection networks)
{
    public sealed record Report(
        string RootAccountId, string NetworkId,
        int Pending, int UncertainOrSubmitting, int Conflicts,
        int FolderEntries,
        int LegacyHdEntries, int LinkedTaggedEntries,
        bool CanPublish,
        string Explanation);

    public async Task<Report> InspectAsync(CancellationToken ct = default)
    {
        linkedRetention.DeviceRetention.RequireLocalPersistence();
        linkedRetention.RequireLocalPersistence();
        if (!keys.IsUnlocked)
            throw new UnauthorizedAccessException(
                "Unlock the independent encrypted S-key queue before inspecting local drafts.");
        if (keys.ActiveRootAccountId is not { } root ||
            linked.IsConnected && linked.RootAccountId != root)
            throw new InvalidOperationException("Unlock the matching imported S-key encrypted queue.");
        StellarAccountAddress.Validate(root);
        var scope = networks.CredentialKey(root);
        var offline = keys.IsOfflineOnly;
        var epoch = linked.ConnectionEpoch;
        var expectedPassphrase = linked.ConnectedNetworkPassphrase;

        LedgerAccountInfo? account = null;
        if (!offline)
        {
            account = await linked.InspectAccountInfoAsync(ct);
            if (account.RootAccountId != root)
                throw new UnauthorizedAccessException("Linked signing root changed during queue inspection.");
        }

        // The host-specific read-only adapter uses the ORIGINAL encrypted queue
        // implementation, but exclusively with its dedicated S-key key session
        // and prefixed namespace. It rejects any untagged/HD-origin entry.
        var mutations = await store.ListAsync(root, scope, ct);
        try
        {
        ct.ThrowIfCancellationRequested();
        linkedRetention.DeviceRetention.RequireLocalPersistence();
        if (!keys.IsUnlocked || keys.ActiveRootAccountId != root ||
            keys.IsOfflineOnly != offline ||
            networks.CredentialKey(root) != scope ||
            linked.IsConnected && linked.RootAccountId != root)
            throw new InvalidOperationException(
                "Independent S-key queue context changed during read-only inspection.");

        if (!offline)
        {
            var after = await linked.InspectAccountInfoAsync(ct);
            if (account is null || after.RootAccountId != root ||
                after.TailAccountId != account.TailAccountId ||
                after.TotalAccounts != account.TotalAccounts ||
                linked.ConnectionEpoch != epoch ||
                linked.ConnectedNetworkPassphrase != expectedPassphrase ||
                networks.CredentialKey(root) != scope)
                throw new InvalidOperationException(
                    "Linked chain or selected network changed during inspection.");
        }

        var linkedCount = mutations.Count;
        return new(root, scope,
            mutations.Count(x => x.State == VaultSyncState.Pending),
            mutations.Count(x => x.State is VaultSyncState.Submitting or VaultSyncState.Uncertain),
            mutations.Count(x => x.State == VaultSyncState.Conflict),
            mutations.Count(x =>
                x.Kind is VaultMutationKind.CreateFolder or
                    VaultMutationKind.UpdateFolder or VaultMutationKind.DeleteFolder),
            0, linkedCount, false,
            offline
                ? "Offline-only encrypted queue inspection used the local passphrase and selected public root/network. No signer authorization, ledger state or transaction publication was checked."
                : "Read-only inspection used a separately unlocked S-key encryption key and isolated queue namespace. No HD drafts are read. Publication requires a fresh fee quote, explicit approval and durable reservation.");
        }
        finally
        {
            // ListAsync returns decrypted queue envelopes. This report returns
            // counts only, so retaining each plaintext payload after inspection
            // would needlessly expose secrets in the managed heap.
            foreach (var mutation in mutations)
                if (mutation.EncryptedPayload is not null)
                    CryptographicOperations.ZeroMemory(mutation.EncryptedPayload);
        }
    }
}
