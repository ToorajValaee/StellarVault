using System.Security.Cryptography;
using System.Text;
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

    public async Task<VaultSecretLedgerQuote> PrepareAsync(CancellationToken ct = default)
    {
        consent.RequireLocalPersistence();
        if (!keys.CanPublish || !ledger.IsConnected ||
            ledger.RootAccountId is not string root)
            throw new UnauthorizedAccessException(
                "A fresh live signer-backed S-key queue unlock is required.");
        var scope = network.CredentialKey(root);
        var epoch = ledger.ConnectionEpoch;
        await RequireNoUnresolvedAsync(root, scope, ct);
        RequireSession(root, scope, epoch);
        return await expansion.PrepareAsync(ct);
    }

    private async Task RequireNoUnresolvedAsync(
        string root, string scope, CancellationToken ct)
    {
        var entries = await store.ListAsync(root, scope, ct);
        try
        {
            if (entries.Any(x =>
                (x.State is VaultSyncState.Submitting or
                    VaultSyncState.Uncertain or VaultSyncState.Conflict) ||
                (x.Kind == VaultMutationKind.ExtendLinkedChain &&
                 x.State != VaultSyncState.Confirmed)))
                throw new InvalidOperationException(
                    "An earlier linked submission or expansion needs verified reconciliation.");
        }
        finally
        {
            foreach (var entry in entries)
                CryptographicOperations.ZeroMemory(entry.EncryptedPayload);
        }
    }

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

            await RequireNoUnresolvedAsync(root, scope, ct);
            RequireSession(root, scope, epoch);
            var chain = await ledger.InspectAccountInfoAsync(ct);
            RequireSession(root, scope, epoch);
            if (chain.RootAccountId != root || !StellarAccountAddress.IsValid(chain.TailAccountId))
                throw new InvalidOperationException("Unable to establish the linked tail before journaling.");
            // Different tabs planning expansion of the SAME tail must race for
            // exactly one durable CAS slot. A random mutation ID would allow
            // concurrent, independently submitted account creations.
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(
                "sv:linked-expansion-intent:v1:" + scope + ":" + root + ":" + chain.TailAccountId));
            string reservationId;
            try { reservationId = Convert.ToHexStringLower(digest.AsSpan(0, 16)); }
            finally { CryptographicOperations.ZeroMemory(digest); }
            var intent = new Intent(approvedQuote.QuoteId,
                approvedQuote.TargetAccountId, root, scope, epoch,
                DateTimeOffset.UtcNow);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(intent);
            VaultPendingMutation? draft = null;
            // This flag flips immediately before handing the approved quote to
            // the ledger-backed expansion transport. Earlier failures may safely
            // abandon the unused quote; later failures MUST stay quarantined.
            var transportInvoked = false;
            try
            {
                draft = VaultPendingMutation.Create(root,
                    VaultLinkedQueueStorageScope.FromNetworkScope(scope),
                    VaultMutationKind.ExtendLinkedChain,
                    approvedQuote.TargetAccountId, payload) with
                {
                    Id = reservationId,
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
                    transportInvoked = true;
                    hash = await expansion.SubmitApprovedAsync(approvedQuote, true, ct);
                    RequireSession(root, scope, epoch);
                    // A successful transport result and a visible linked tail
                    // are not independent proof of the exact historical
                    // CreateAccount/SetOptions/pointer operations or signer policy.
                    // Keep the original durable reservation quarantined until
                    // independent operation history can confirm its provenance.
                    var uncertain = reserved with
                    {
                        State = VaultSyncState.Uncertain,
                        TransactionHash = hash
                    };
                    if (!await store.TryReplaceAsync(reserved, uncertain, CancellationToken.None))
                        throw new InvalidOperationException(
                            "Expansion was observed but encrypted journal quarantine could not be persisted. Do not retry.");
                    return hash;
                }
                catch
                {
                    // The transport may have signed and transmitted the
                    // expansion before a Horizon timeout. Preserve that exact
                    // pre-broadcast hash as a public reconciliation handle.
                    hash ??= await VaultLinkedSignedSubmissionIdentity.TryReadAsync(
                        ledger, root, "expand", approvedQuote.TargetAccountId,
                        null, ledger.ConnectedNetworkPassphrase ?? "", epoch);
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
            catch
            {
                if (!transportInvoked)
                {
                    // Persisting a Pending intent or failing its CAS reservation
                    // happened before any transport call. Release only this
                    // never-submitted quote; do not erase its durable journal
                    // because another tab may have observed the reservation.
                    try { await expansion.AbandonUnsubmittedAsync(CancellationToken.None); }
                    catch { /* Preserve the original journal failure. */ }
                }
                throw;
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

    /// <summary>
    /// Explicitly discard ONLY an intent still in Pending before it was
    /// reserved for broadcast. Atomic TryDelete fails if a sibling tab has
    /// changed it to Submitting. Never touches uncertain or confirmed journals.
    /// </summary>
    public async Task DiscardNeverSubmittedIntentAsync(
        string mutationId, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(mutationId, "N", out _))
            throw new ArgumentException("Invalid expansion journal identifier.",
                nameof(mutationId));
        await _gate.WaitAsync(ct);
        try
        {
            if (!keys.CanPublish || !ledger.IsConnected ||
                ledger.RootAccountId is not string root)
                throw new UnauthorizedAccessException(
                    "A fresh live signer-backed S-key queue unlock is required.");
            var scope = network.CredentialKey(root);
            var epoch = ledger.ConnectionEpoch;
            var entries = await store.ListAsync(root, scope, ct);
            try
            {
                var intent = entries.SingleOrDefault(x => x.Id == mutationId)
                    ?? throw new KeyNotFoundException("Linked expansion journal not found.");
                if (intent.Kind != VaultMutationKind.ExtendLinkedChain ||
                    intent.State != VaultSyncState.Pending ||
                    intent.TransactionHash is not null ||
                    intent.SignedFileEntries is { Length: > 0 } ||
                    intent.SignedDeletionManifests is { Length: > 0 })
                    throw new InvalidOperationException(
                        "Only never-reserved expansion intents can be discarded. An uncertain/submitting journal is immutable.");
                RequireSession(root, scope, epoch);
                if (!await store.TryDeleteAsync(intent, ct))
                    throw new InvalidOperationException(
                        "The expansion intent changed concurrently. Refresh the encrypted journal.");
            }
            finally
            {
                foreach (var entry in entries)
                    CryptographicOperations.ZeroMemory(entry.EncryptedPayload);
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
