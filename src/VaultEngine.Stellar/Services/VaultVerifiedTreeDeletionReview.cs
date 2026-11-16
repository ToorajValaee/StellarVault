using System.Security.Cryptography;
using System.Text;

namespace VaultEngine.Stellar.Services;

/// <summary>
/// Read-only authenticated SVTREE3 deletion inventory shared by HD and linked
/// account strategies. This is NOT a signer, tombstone publisher, or proof of
/// historical deletion. Node receipts and durable no-retry journals must be
/// implemented before any caller may delete an inventoried node.
/// </summary>
public static class VaultVerifiedTreeDeletionReview
{
    public sealed record Node(string RecordId, string RecordSha256);
    public sealed record Plan(string RootId, string FileName, string ContentSha256,
        string GroupTag, Node[] Nodes, string InventorySha256);
    public sealed record Inspection(bool Unchanged, int Present, int Total,
        string Explanation);

    public static async Task<Plan> BuildAsync(string rootId,
        IReadOnlyList<LedgerRecordItem> remote, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        if (rootId is not { Length: 16 } || !rootId.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid file root ID.", nameof(rootId));

        var verified = await VaultLinkedTreeFilePolicy.InspectVerifiedAsync(
            rootId, remote, ct);
        ct.ThrowIfCancellationRequested();

        // A verified tree graph is necessary but not sufficient to authorize
        // deletion: hidden/orphan records with the same group may exist, or
        // another root may claim the group. Reject such ambiguous ownership.
        var ids = verified.NodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (verified.NodeIds.Length == 0 ||
            !verified.NodeIds[0].Equals(rootId, StringComparison.OrdinalIgnoreCase) ||
            ids.Count != verified.NodeIds.Length)
            throw new InvalidDataException("Verified file graph is ambiguous.");
        if (remote.Any(x => x.Tags is [_, var group] &&
                string.Equals(group, verified.GroupTag, StringComparison.Ordinal) &&
                !ids.Contains(x.Id)))
            throw new InvalidDataException(
                "File group contains unreferenced records; deletion ownership is ambiguous.");

        var byId = new Dictionary<string, LedgerRecordItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in remote)
            if (item is null || !byId.TryAdd(item.Id, item))
                throw new InvalidDataException("Duplicate or missing file record identity.");

        var nodes = verified.NodeIds.Select(id =>
        {
            var item = byId[id];
            if (item.IsFolder || item.Tags is not [_, var group] ||
                group != verified.GroupTag ||
                item.Content?.StartsWith(VaultLinkedTreeFilePolicy.Prefix,
                    StringComparison.Ordinal) != true)
                throw new InvalidDataException("File node is not a typed group member.");
            return new Node(id, VaultHybridRemoteMutationWorkspace.Fingerprint(item));
        }).ToArray();

        var fingerprint = InventoryFingerprint(rootId, verified.GroupTag,
            verified.Manifest.Sha256Hex, nodes);
        return new Plan(rootId, verified.Manifest.FileName,
            verified.Manifest.Sha256Hex, verified.GroupTag, nodes, fingerprint);
    }

    /// <summary>
    /// Compare to a freshly authenticated ledger snapshot. A missing or
    /// changed node is an unresolved condition, NOT evidence that deletion
    /// succeeded. Never use this result to retry, skip or confirm tombstones.
    /// </summary>
    public static async Task<Inspection> InspectAsync(Plan pinned,
        IReadOnlyList<LedgerRecordItem> current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(current);
        if (pinned.Nodes is not { Length: > 0 } ||
            !pinned.Nodes[0].RecordId.Equals(pinned.RootId, StringComparison.OrdinalIgnoreCase) ||
            pinned.Nodes.Select(x => x.RecordId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != pinned.Nodes.Length ||
            !string.Equals(pinned.InventorySha256,
                InventoryFingerprint(pinned.RootId, pinned.GroupTag,
                    pinned.ContentSha256, pinned.Nodes), StringComparison.Ordinal))
            throw new InvalidDataException("Pinned file deletion plan was modified.");

        ct.ThrowIfCancellationRequested();
        var present = pinned.Nodes.Count(node => current.Any(record =>
            record.Id.Equals(node.RecordId, StringComparison.OrdinalIgnoreCase)));
        try
        {
            var fresh = await BuildAsync(pinned.RootId, current, ct);
            if (fresh.InventorySha256 == pinned.InventorySha256 &&
                fresh.Nodes.Length == pinned.Nodes.Length &&
                fresh.Nodes.Select(x => x.RecordId).SequenceEqual(
                    pinned.Nodes.Select(x => x.RecordId), StringComparer.OrdinalIgnoreCase))
                return new(true, present, pinned.Nodes.Length,
                    "Every authenticated file node remains unchanged. Read-only review; deletion is NOT authorized.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or
                                   ArgumentException or KeyNotFoundException)
        {
            // Missing/cross-group/altered nodes are never interpreted as a
            // successful deletion without independent signed historical proof.
        }
        return new(false, present, pinned.Nodes.Length,
            "File graph changed, contains ambiguous ownership or has missing nodes. Do not delete, retry or assume success; investigate historical transaction proofs.");
    }

    private static string InventoryFingerprint(string rootId, string group,
        string contentDigest, IReadOnlyList<Node> nodes)
    {
        if (rootId is not { Length: 16 } || !rootId.All(Uri.IsHexDigit) ||
            group is not { Length: > 0 and <= 128 } ||
            contentDigest is not { Length: 64 } || !contentDigest.All(Uri.IsHexDigit) ||
            nodes.Count == 0 || nodes.Any(n => n.RecordId is not { Length: 16 } ||
                !n.RecordId.All(Uri.IsHexDigit) || n.RecordSha256 is not { Length: 64 } ||
                !n.RecordSha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Incomplete pinned file deletion inventory.");

        // Unambiguous versioned, length-prefixed canonical serialization.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        static void Append(IncrementalHash h, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            h.AppendData(length);
            h.AppendData(bytes);
            CryptographicOperations.ZeroMemory(bytes);
        }
        Append(hash, "svtree3-delete-review-v1");
        Append(hash, rootId.ToLowerInvariant());
        Append(hash, group);
        Append(hash, contentDigest.ToUpperInvariant());
        foreach (var node in nodes)
        {
            Append(hash, node.RecordId.ToLowerInvariant());
            Append(hash, node.RecordSha256.ToLowerInvariant());
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
