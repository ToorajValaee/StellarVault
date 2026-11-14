using System.Security.Cryptography;
using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// Durable, consented journal for manual linked-account capacity expansion
/// initiated from the existing encrypted draft queue. A fresh approved quote
/// is reserved in the original AES-GCM queue before any network transmission.
/// This coordinator does not sign independently, retry or clear uncertainty.
/// </summary>
public sealed class VaultLinkedExpansionJournal(
    VaultLinkedAccountExpansion expansion,
    IStellarSecretKeyLedger ledger,
    IVaultLinkedDraftStore store,
    VaultLinkedQueueKeySession keys,
    VaultLinkedLocalRetentionGate consent,
    VaultNetworkSelection network)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed record Intent(string QuoteId, string TargetAddress,
        string RootAddress, string NetworkScope, long SignerEpoch,
        DateTimeOffset CreatedUtc);

    public async Task<string> SubmitApprovedAsync(
        VaultSecretLedgerQuote approvedQuote, bool userApproved,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(approvedQuote);
        if (!userApproved || approvedQuote.Kind != "expand" ||
            !approvedQuote.CreatesChildNode ||
            approvedQuote.QuoteId is not { Length: 32 } ||
            !approvedQuote.QuoteId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !StellarAccountAddress.IsValid(approvedQuote.TargetAccountId))
            throw new UnauthorizedAccessException(
                "A fresh, explicitly approved linked-account expansion quote is required.");

        await _gate.WaitAsync(ct);
        try
        {
            consent.RequireLocalPersistence();
            if (!keys.CanPublish || !ledger.IsConnected ||
                ledger.RootAccountId is not string root)
                throw new UnauthorizedAccessException(
                    "Unlock this independent S-key device queue under its live signer.");
            var scope = network.CredentialKey(root);
            var epoch = ledger.ConnectionEpoch;

            var before = await store.ListAsync(root, scope, ct);
            try
            {
                if (before.Any(x => x.State is VaultSyncState.Submitting or
                    VaultSyncState.Uncertain or VaultSyncState.Conflict ||
                    x.Kind == VaultMutationKind.ExtendLinkedChain &&
                    x.State != VaultSyncState.Confirmed))
                    throw new InvalidOperationException(
                        "An earlier linked submission or expansion needs verified reconciliation.");
            }
            finally
            {
                foreach (var entry in before)
                    CryptographicOperations.ZeroMemory(entry.EncryptedPayload);
            }
            RequireSession(root, scope, epoch);
            var intent = new Intent(approvedQuote.QuoteId,
                approvedQuote.TargetAccountId, root, scope, epoch,
                DateTimeOffset.UtcNow);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(intent);
            VaultPendingMutation? draft = null;
            try
            {
                draft = VaultPendingMutation.Create(root,
                    VaultLinkedQueueStorageScope.FromNetworkScope(scope),
                    VaultMutationKind.ExtendLinkedChain,
                    approvedQuote.TargetAccountId, payload) with
                {
                    OriginStrategyId = VaultMutationStrategyPolicy.Linked
                };
                RequireSession(root, scope, epoch);
                if (!await store.AddAsync(draft, ct))
                    throw new InvalidOperationException(
                        "Unable to create the encrypted expansion journal. No transaction was sent.");

                // A crash between Add and this CAS leaves a Pending journal
                // intent that STILL blocks any later linked publication.
                var reserved = draft with { State = VaultSyncState.Submitting };
                if (!await store.TryReplaceAsync(draft, reserved, ct))
                    throw new InvalidOperationException(
                        "Encrypted expansion reservation failed. Inspect the durable journal before trying again.");

                string? hash = null;
                try
                {
                    hash = await expansion.SubmitApprovedAsync(approvedQuote, true, ct);
                    RequireSession(root, scope, epoch);
                    var confirmed = reserved with
                    {
                        State = VaultSyncState.Confirmed,
                        TransactionHash = hash
                    };
                    if (!await store.TryReplaceAsync(reserved, confirmed, ct))
                        throw new InvalidOperationException(
                            "Expansion was observed but encrypted journal confirmation failed.");
                    return hash;
                }
                catch
                {
                    var uncertain = reserved with
                    {
                        State = VaultSyncState.Uncertain,
                        TransactionHash = hash
                    };
                    try
                    {
                        await store.TryReplaceAsync(reserved, uncertain, CancellationToken.None);
                    }
                    catch
                    {
                        // Reserved Submitting remains durable and blocks later
                        // writes if the uncertain CAS cannot be persisted.
                    }
                    throw new InvalidOperationException(
                        "Linked account expansion may have reached Stellar. Do not retry. Review the durable journal and historical transaction evidence.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
                if (draft is not null)
                    CryptographicOperations.ZeroMemory(draft.EncryptedPayload);
            }
        }
        finally { _gate.Release(); }
    }

    private void RequireSession(string root, string scope, long epoch)
    {
        consent.RequireLocalPersistence();
        if (!keys.CanPublish || !ledger.IsConnected ||
            ledger.RootAccountId != root ||
            ledger.ConnectionEpoch != epoch ||
            network.CredentialKey(root) != scope)
            throw new InvalidOperationException(
                "Signer, account, network or local consent changed during linked expansion.");
    }
}
