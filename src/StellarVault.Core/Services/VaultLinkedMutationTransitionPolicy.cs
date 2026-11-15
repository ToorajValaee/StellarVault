namespace StellarVault.Core.Services;

/// <summary>
/// All authorized state transitions for the isolated independent S-key queue.
/// Existing AES-GCM stores perform the actual atomic compare-and-swap; this
/// policy only validates the immutable queue envelope and revision shape.
/// Never permit an uncertain or submitted entry to become Pending again.
/// </summary>
public static class VaultLinkedMutationTransitionPolicy
{
    public static void RequireValid(VaultPendingMutation previous, VaultPendingMutation next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        if (previous.OriginStrategyId != VaultMutationStrategyPolicy.Linked ||
            next.OriginStrategyId != VaultMutationStrategyPolicy.Linked ||
            previous.Kind is not (VaultMutationKind.CreateRecord or
                VaultMutationKind.CreateFolder or VaultMutationKind.UpdateRecord or
                VaultMutationKind.UpdateFolder or VaultMutationKind.DeleteRecord or
                VaultMutationKind.DeleteFolder or VaultMutationKind.ExtendLinkedChain) ||
            previous.Id != next.Id || previous.AccountId != next.AccountId ||
            previous.NetworkId != next.NetworkId || previous.EntityId != next.EntityId ||
            previous.Kind != next.Kind || previous.CreatedUtc != next.CreatedUtc ||
            previous.ExpectedRemoteVersion != next.ExpectedRemoteVersion)
            throw new InvalidOperationException("Linked queue identity or strategy changed.");

        if (previous.Kind == VaultMutationKind.ExtendLinkedChain &&
            next.State == VaultSyncState.Pending)
            throw new InvalidOperationException(
                "Linked expansion intent cannot be edited after it was journaled.");

        if (previous.State == VaultSyncState.Pending && next.State == VaultSyncState.Pending)
        {
            if (previous.Kind is VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder)
                throw new InvalidOperationException(
                    "A staged linked deletion must retain its original encrypted record snapshot.");
            // Edit a never-submitted create/update using original durable CAS. The
            // record ID and creation timestamp cannot change; only payload
            // and parent dependency may change in the same atomic revision.
            if (previous.TransactionHash is not null || next.TransactionHash is not null ||
                previous.SignedFileEntries is { Length: > 0 } ||
                previous.SignedDeletionManifests is { Length: > 0 } ||
                next.EncryptedPayload is not { Length: > 0 and <= 8388608 } ||
                next.DependsOn is null || next.DependsOn.Length > 1 ||
                next.DependsOn.Any(id => !Guid.TryParseExact(id, "N", out _) ||
                    id == previous.Id) ||
                !Equals(previous with
                {
                    EncryptedPayload = next.EncryptedPayload,
                    DependsOn = next.DependsOn
                }, next))
                throw new InvalidOperationException(
                    "Only unpublished linked draft content and parent dependency may be revised.");
            return;
        }

        if (previous.DependsOn is null || next.DependsOn is null ||
            !previous.DependsOn.SequenceEqual(next.DependsOn) ||
            !previous.EncryptedPayload.AsSpan().SequenceEqual(next.EncryptedPayload) ||
            !((previous.State == VaultSyncState.Pending &&
               next.State == VaultSyncState.Submitting) ||
              // Single-use public signed-hash checkpoint BEFORE broadcast.
              // The original state remains Submitting, with its CAS identity
              // preserved; no second checkpoint or hash replacement is valid.
              (previous.State == VaultSyncState.Submitting &&
               next.State == VaultSyncState.Submitting &&
               previous.TransactionHash is null && next.TransactionHash is not null) ||
              (previous.State == VaultSyncState.Submitting &&
               next.State is VaultSyncState.Uncertain or VaultSyncState.Confirmed) ||
              // Only the separately proof-gated reconciler may invoke this
              // final immutable CAS transition. Never return to Pending.
              (previous.State == VaultSyncState.Uncertain &&
               next.State == VaultSyncState.Confirmed &&
               previous.TransactionHash is not null)) ||
            !Equals(previous with
            {
                State = next.State,
                TransactionHash = next.TransactionHash
            }, next) ||
            (next.State == VaultSyncState.Pending && next.TransactionHash is not null) ||
            (previous.State == VaultSyncState.Pending &&
             next.State == VaultSyncState.Submitting && next.TransactionHash is not null) ||
            (previous.TransactionHash is { } pinnedHash &&
             next.TransactionHash != pinnedHash) ||
            (next.State == VaultSyncState.Confirmed &&
             (previous.TransactionHash is null || next.TransactionHash is null)) ||
            (next.TransactionHash is { } hash &&
             (hash.Length != 64 || !hash.All(Uri.IsHexDigit))))
            throw new InvalidOperationException(
                "Invalid linked queue state transition or changed immutable draft metadata.");
    }
}
