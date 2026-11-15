using System.Security.Cryptography;
using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// Final, opt-in linked record and chain journal reconciliation. Re-verifies the exact
/// signed Horizon envelope, decrypted historical record and current snapshot
/// on the SAME connected S-key network before a single encrypted CAS commit.
/// A negative or unavailable proof never releases an Uncertain mutation.
/// Expansion journals require a separate dual-signature and parent-pointer proof.
/// </summary>
public sealed class VaultLinkedVerifiedReconciliation(
    IStellarSecretKeyLedger ledger,
    IVaultLinkedDraftStore store,
    VaultLinkedQueueKeySession keys,
    VaultLinkedLocalRetentionGate consent,
    VaultNetworkSelection network,
    VaultLinkedDraftRecoveryReview reviewer)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public sealed record Result(string MutationId, string TransactionHash,
        bool Confirmed, string Explanation);

    private (string Root, string Scope, long Epoch, string Passphrase) RequireScope()
    {
        consent.DeviceRetention.RequireLocalPersistence();
        consent.RequireLocalPersistence();
        if (!keys.CanPublish || !keys.IsUnlocked || !ledger.IsConnected ||
            ledger.RootAccountId is not { } root ||
            ledger.ConnectedNetworkPassphrase is not { Length: > 0 } passphrase ||
            ledger is not IStellarSecretKeyHistoricalRecordVerifier)
            throw new UnauthorizedAccessException(
                "Reconnect the matching independent S-key signer and unlock its encrypted queue.");
        return (root, network.CredentialKey(root), ledger.ConnectionEpoch, passphrase);
    }

    private void RequireSameScope(string root, string scope, long epoch, string passphrase)
    {
        var now = RequireScope();
        if (now.Root != root || now.Scope != scope ||
            now.Epoch != epoch || now.Passphrase != passphrase)
            throw new InvalidOperationException(
                "Signer, network or encrypted local queue changed during historical verification.");
    }

    private sealed record ExpansionIntent(string QuoteId, string TargetAddress,
        string RootAddress, string NetworkScope, long SignerEpoch,
        DateTimeOffset CreatedUtc);

    /// <summary>
    /// Finishes a linked child expansion only after independently checking a
    /// successful exact operation trace and both the root and newly created
    /// child signatures on the network-bound Stellar envelope.
    /// </summary>
    public async Task<Result> ConfirmVerifiedExpansionAsync(
        string mutationId, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(mutationId, "N", out _))
            throw new ArgumentException("Invalid linked expansion journal identifier.", nameof(mutationId));
        await _gate.WaitAsync(ct);
        try
        {
            var (root, scope, epoch, passphrase) = RequireScope();
            if (ledger is not IStellarSecretKeyHistoricalExpansionVerifier historical)
                throw new NotSupportedException(
                    "This S-key host cannot cryptographically verify signed child account expansion.");
            var entries = await store.ListAsync(root, scope, ct);
            try
            {
                var entry = entries.SingleOrDefault(x => x.Id == mutationId)
                    ?? throw new KeyNotFoundException("Linked expansion journal not found.");
                if (entry.AccountId != root ||
                    entry.NetworkId != VaultLinkedQueueStorageScope.FromNetworkScope(scope) ||
                    entry.OriginStrategyId != VaultMutationStrategyPolicy.Linked ||
                    entry.Kind != VaultMutationKind.ExtendLinkedChain ||
                    entry.State is not (VaultSyncState.Uncertain or VaultSyncState.Submitting) ||
                    entry.TransactionHash is not { Length: 64 } txHash ||
                    !txHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                    !StellarAccountAddress.IsValid(entry.EntityId))
                    throw new InvalidOperationException(
                        "Only a signed hash-pinned uncertain linked expansion can be finalized.");
                var intent = JsonSerializer.Deserialize<ExpansionIntent>(entry.EncryptedPayload)
                    ?? throw new InvalidDataException("Encrypted linked expansion intent is missing.");
                if (intent.RootAddress != root || intent.NetworkScope != scope ||
                    intent.TargetAddress != entry.EntityId ||
                    intent.QuoteId is not { Length: 32 } ||
                    !intent.QuoteId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                    throw new InvalidDataException("Linked expansion journal intent does not match its durable account scope.");

                var before = await reviewer.InspectAsync(mutationId, ct);
                RequireSameScope(root, scope, epoch, passphrase);
                if (before.DurableState != entry.State ||
                    before.TransactionHash != txHash ||
                    before.CurrentRecordMatches is not true ||
                    !before.HistoricalReceiptObserved ||
                    !before.HistoricalExpansionOperationsMatch ||
                    !StellarAccountAddress.IsValid(before.HistoricalParentAccount))
                    return new(mutationId, txHash, false,
                        "Historical expansion receipt, parent operation or live chain tail evidence is incomplete.");

                bool proven;
                try
                {
                    proven = await historical.VerifyExpansionAsync(txHash, entry.EntityId, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { proven = false; }
                RequireSameScope(root, scope, epoch, passphrase);
                if (!proven)
                    return new(mutationId, txHash, false,
                        "Exact network-bound expansion XDR did not prove both root/child signatures and the linked parent operation.");

                var after = await reviewer.InspectAsync(mutationId, ct);
                RequireSameScope(root, scope, epoch, passphrase);
                if (after.DurableState != entry.State ||
                    after.TransactionHash != txHash ||
                    after.CurrentRecordMatches is not true ||
                    !after.HistoricalReceiptObserved ||
                    !after.HistoricalExpansionOperationsMatch ||
                    after.HistoricalParentAccount != before.HistoricalParentAccount)
                    return new(mutationId, txHash, false,
                        "Parent lineage or historical expansion evidence changed during verification.");

                var confirmed = entry with { State = VaultSyncState.Confirmed };
                if (!await store.TryReplaceAsync(entry, confirmed, CancellationToken.None))
                    throw new InvalidOperationException(
                        "Expansion journal changed concurrently; confirmation was not persisted.");
                var released = false;
                if (ledger is IStellarSecretKeyVerifiedSessionRelease session)
                {
                    try
                    {
                        await session.ReleaseVerifiedAsync(txHash, CancellationToken.None);
                        released = true;
                    }
                    catch { /* The already committed journal cannot be rolled back. */ }
                }
                return new(mutationId, txHash, true,
                    "Dual-signed historical child creation and parent-pointer history were checked, then the encrypted expansion journal was confirmed without replay." +
                    (released ? "" : " Fully restart the app if its previous in-memory signer quarantine remains."));
            }
            finally
            {
                foreach (var mutation in entries)
                    CryptographicOperations.ZeroMemory(mutation.EncryptedPayload);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<Result> ConfirmVerifiedRecordAsync(
        string mutationId, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(mutationId, "N", out _))
            throw new ArgumentException("Invalid linked queue mutation ID.", nameof(mutationId));
        await _gate.WaitAsync(ct);
        try
        {
            var (root, scope, epoch, passphrase) = RequireScope();
            var all = await store.ListAsync(root, scope, ct);
            try
            {
                var entry = all.SingleOrDefault(x => x.Id == mutationId)
                    ?? throw new KeyNotFoundException("Linked encrypted journal was not found.");
                if (entry.AccountId != root ||
                    entry.NetworkId != VaultLinkedQueueStorageScope.FromNetworkScope(scope) ||
                    entry.OriginStrategyId != VaultMutationStrategyPolicy.Linked ||
                    entry.State is not (VaultSyncState.Uncertain or VaultSyncState.Submitting) ||
                    entry.TransactionHash is not { Length: 64 } txHash ||
                    !txHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                    entry.Kind is not (
                        VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder or
                        VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder or
                        VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder))
                    throw new InvalidOperationException(
                        "Only a hash-pinned, unresolved S-key record mutation can be reconciled.");
                var isCreate = entry.Kind is VaultMutationKind.CreateRecord or
                    VaultMutationKind.CreateFolder;
                if (isCreate
                    ? entry.ExpectedRemoteVersion is not null
                    : !VaultLinkedRemoteRevisionPolicy.IsValid(entry.ExpectedRemoteVersion))
                    throw new InvalidDataException(
                        "Original record manifest is not valid for the journal mutation.");
                var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(entry.EncryptedPayload)
                    ?? throw new InvalidDataException("Encrypted journal draft is unreadable.");
                if (draft.IsFolder != (entry.Kind is
                        VaultMutationKind.CreateFolder or VaultMutationKind.UpdateFolder or
                        VaultMutationKind.DeleteFolder))
                    throw new InvalidDataException("Linked mutation type does not match its draft.");

                // This independently checks network identity, root-sourced
                // successful receipt, and the expected current resulting state.
                var before = await reviewer.InspectAsync(mutationId, ct);
                RequireSameScope(root, scope, epoch, passphrase);
                if (before.DurableState != entry.State ||
                    before.TransactionHash != txHash ||
                    before.CurrentRecordMatches is not true ||
                    !before.HistoricalReceiptObserved ||
                    (isCreate && !before.HistoricalRecordOperationsMatch))
                    return new(mutationId, txHash, false,
                        "Transaction receipt, current state or independent historical operation evidence is incomplete.");

                var signer = (IStellarSecretKeyHistoricalRecordVerifier)ledger;
                bool proven;
                try
                {
                    proven = await signer.VerifyAsync(txHash, entry.Kind,
                        entry.EntityId, draft, entry.ExpectedRemoteVersion, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch
                {
                    proven = false;
                }
                RequireSameScope(root, scope, epoch, passphrase);
                if (!proven)
                    return new(mutationId, txHash, false,
                        "The signed transaction envelope, root signature or historical decrypted record did not independently match the original approved draft.");

                // Re-read the same on-chain result and receipt after XDR
                // verification. A changed local record state never grants
                // confirmation, even if an earlier read matched.
                var after = await reviewer.InspectAsync(mutationId, ct);
                RequireSameScope(root, scope, epoch, passphrase);
                if (after.DurableState != entry.State ||
                    after.TransactionHash != txHash ||
                    after.CurrentRecordMatches is not true ||
                    !after.HistoricalReceiptObserved ||
                    (isCreate && !after.HistoricalRecordOperationsMatch) ||
                    after.HistoricalStorageAccount != before.HistoricalStorageAccount)
                    return new(mutationId, txHash, false,
                        "Historical evidence or current-state confirmation changed during verification.");

                var confirmed = entry with { State = VaultSyncState.Confirmed };
                if (!await store.TryReplaceAsync(entry, confirmed, CancellationToken.None))
                    throw new InvalidOperationException(
                        "The encrypted journal changed concurrently; no confirmation was persisted.");
                var released = false;
                if (ledger is IStellarSecretKeyVerifiedSessionRelease session)
                {
                    try
                    {
                        await session.ReleaseVerifiedAsync(txHash, CancellationToken.None);
                        released = true;
                    }
                    catch
                    {
                        // Durable confirmation is final. A failed volatile
                        // marker release cannot roll it back or trigger retry.
                    }
                }
                return new(mutationId, txHash, true,
                    "Exact signed historical XDR, root signature and decrypted record were checked against the approved mutation. The encrypted journal was durably confirmed; no transaction was resubmitted." +
                    (released ? "" : " A full app restart may be required to clear the volatile signer quarantine."));
            }
            finally
            {
                foreach (var mutation in all)
                    CryptographicOperations.ZeroMemory(mutation.EncryptedPayload);
            }
        }
        finally { _gate.Release(); }
    }
}
