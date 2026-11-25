using System.Security.Cryptography;
using System.Text;

namespace VaultEngine.Stellar.Services;

/// <summary>Immutable, short-lived approval snapshot. Never store signer material.</summary>
public sealed record VaultSyncApprovalSnapshot(
    string MutationId, string AccountId, string NetworkId, string DraftSha256,
    decimal QuotedFeeXlm, decimal QuotedPeakReserveXlm, DateTimeOffset QuotedUtc);

/// <summary>
/// Prevents approving stale drafts after user edits or network/account changes.
/// Does not submit anything; the caller must independently verify remote state.
/// </summary>
public static class VaultSyncApprovalGuard
{
    public static VaultSyncApprovalSnapshot Capture(VaultPendingMutation mutation,
        decimal feeXlm, decimal peakReserveXlm, DateTimeOffset now)
    {
        VaultMutationStrategyPolicy.RequireHd(mutation);
        if (mutation.State != VaultSyncState.Pending || mutation.TransactionHash is not null)
            throw new InvalidOperationException("Only an untouched pending draft can be quoted.");
        if (mutation.Kind is not (VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder or
            VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder or
            VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder))
            throw new InvalidOperationException("This mutation requires protocol-aware review.");
        if ((mutation.Kind is VaultMutationKind.UpdateRecord or VaultMutationKind.UpdateFolder or
            VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder) &&
            (mutation.ExpectedRemoteVersion is not { Length: 64 } version ||
             !version.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidOperationException("Remote mutation requires a pinned content version.");
        if (mutation.EntityId.Length != 16 || !mutation.EntityId.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Ledger quote requires a valid 16-character record ID.");
        if (feeXlm < 0 || peakReserveXlm < 0)
            throw new ArgumentOutOfRangeException(nameof(feeXlm));
        return new VaultSyncApprovalSnapshot(mutation.Id, mutation.AccountId,
            mutation.NetworkId, Digest(mutation), feeXlm, peakReserveXlm, now);
    }

    public static void RequireFresh(VaultSyncApprovalSnapshot snapshot,
        VaultPendingMutation current, string accountId, string networkId,
        DateTimeOffset now, TimeSpan maxAge)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(current);
        VaultMutationStrategyPolicy.RequireHd(current);
        // Share the account/network and lifetime boundary with independent
        // linked-account quote reviews, without sharing signing credentials.
        VaultApprovalScopeGuard.RequireFresh(snapshot.AccountId, snapshot.NetworkId,
            accountId, networkId, snapshot.QuotedUtc, now, maxAge);
        if (current.AccountId != accountId || current.NetworkId != networkId ||
            snapshot.MutationId != current.Id || current.State != VaultSyncState.Pending ||
            current.TransactionHash is not null ||
            !string.Equals(snapshot.DraftSha256, Digest(current), StringComparison.Ordinal))
            throw new InvalidOperationException("Draft changed after review. Review again.");
    }

    private static string Digest(VaultPendingMutation item)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(item.Id);
            writer.Write(item.AccountId);
            writer.Write(item.NetworkId);
            writer.Write((int)item.Kind);
            writer.Write(item.EntityId);
            writer.Write(item.ExpectedRemoteVersion ?? "");
            writer.Write(item.DependsOn.Length);
            foreach (var dep in item.DependsOn) writer.Write(dep);
            writer.Write(item.EncryptedPayload.Length);
            writer.Write(item.EncryptedPayload);
            writer.Write(VaultMutationStrategyPolicy.Resolve(item.OriginStrategyId));
            // Capacity is mandatory: any policy change invalidates approval.
            writer.Write((int)item.CapacityPolicy);
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
}
