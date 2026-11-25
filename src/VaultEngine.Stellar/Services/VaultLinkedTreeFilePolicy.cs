using System.Security.Cryptography;
using System.Text.Json;

namespace VaultEngine.Stellar.Services;

/// <summary>
/// S-key SVTREE3 ledger-node policy. Each independently encrypted Stellar
/// record is published through the existing explicit quote, signed-hash
/// checkpoint and historically verified linked queue. A parent record cannot
/// be published until its exact children are durably confirmed and visible.
/// </summary>
public static class VaultLinkedTreeFilePolicy
{
    public const string Prefix = "SVTREE3:";
    private const string LeafTag = "svtree3-leaf";
    private const string BranchTag = "svtree3-branch";
    private const string RootTag = "svtree3-root";

    public sealed record Node(string Kind, string Base64);

    public static LedgerRecordDraft Draft(VaultStreamingFileTree.Emission node,
        string title, string groupId, string? folderId = null)
    {
        if (groupId is not { Length: 16 } || !groupId.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid linked file group identity.", nameof(groupId));
        var tag = node.Kind switch
        {
            VaultStreamingFileTree.LeafKind => LeafTag,
            VaultStreamingFileTree.BranchKind => BranchTag,
            VaultStreamingFileTree.RootKind => RootTag,
            _ => throw new InvalidDataException("Unknown streaming file node kind.")
        };
        if (node.Kind != VaultStreamingFileTree.RootKind && folderId is not null)
            throw new InvalidDataException("Only file roots may have a folder.");
        return new LedgerRecordDraft(title,
            Prefix + JsonSerializer.Serialize(new Node(node.Kind,
                Convert.ToBase64String(node.Plaintext))),
            folderId, [tag, "svtree3-group:" + groupId]);
    }

    public static bool TryDecode(LedgerRecordDraft draft, out string kind,
        out byte[] payload)
    {
        kind = string.Empty;
        payload = [];
        if (draft.IsFolder || draft.Content is null ||
            !draft.Content.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        try
        {
            var metadata = JsonSerializer.Deserialize<Node>(draft.Content[Prefix.Length..]);
            if (metadata is null) return false;
            var expectedTag = metadata.Kind switch
            {
                VaultStreamingFileTree.LeafKind => LeafTag,
                VaultStreamingFileTree.BranchKind => BranchTag,
                VaultStreamingFileTree.RootKind => RootTag,
                _ => null
            };
            if (expectedTag is null || draft.Tags is not { Length: 2 } tags ||
                tags[0] != expectedTag ||
                tags[1] is null ||
                !tags[1].StartsWith("svtree3-group:", StringComparison.Ordinal) ||
                tags[1].Length != "svtree3-group:".Length + 16 ||
                !tags[1]["svtree3-group:".Length..].All(Uri.IsHexDigit) ||
                (metadata.Kind != VaultStreamingFileTree.RootKind &&
                 draft.FolderId is not null))
                return false;
            if (metadata.Base64 is null || metadata.Base64.Length > 5500)
                return false;
            var decoded = Convert.FromBase64String(metadata.Base64);
            if (decoded.Length is < 1 or > 4096)
            {
                CryptographicOperations.ZeroMemory(decoded);
                return false;
            }
            kind = metadata.Kind;
            payload = decoded;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    public static bool IsRootRecord(LedgerRecordItem record)
    {
        if (record.Content is null || !record.Content.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        var draft = new LedgerRecordDraft(record.Title, record.Content,
            record.FolderId, record.Tags, record.IsFolder,
            record.UserName, record.Url, record.Notes);
        if (!TryDecode(draft, out var kind, out var bytes)) return false;
        try { return kind == VaultStreamingFileTree.RootKind &&
            ValidateNode(kind, bytes, out _); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static bool IsTypedNode(LedgerRecordDraft draft)
    {
        if (!TryDecode(draft, out var kind, out var data)) return false;
        try { return ValidateNode(kind, data, out _); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    private static bool ValidateNode(string kind, byte[] data,
        out VaultStreamingFileTree.Reference[] children)
    {
        children = [];
        try
        {
            if (kind == VaultStreamingFileTree.LeafKind)
            {
                var leaf = JsonSerializer.Deserialize<VaultStreamingFileTree.Leaf>(data);
                try { return leaf?.Data is { Length: <= VaultStreamingFileTree.ChunkBytes }; }
                finally
                {
                    if (leaf?.Data is not null)
                        CryptographicOperations.ZeroMemory(leaf.Data);
                }
            }
            if (kind == VaultStreamingFileTree.BranchKind)
            {
                var branch = JsonSerializer.Deserialize<VaultStreamingFileTree.Branch>(data);
                if (branch?.Children is not { Length: >= 1 and <= VaultStreamingFileTree.Fanout } refs)
                    return false;
                children = refs;
            }
            else if (kind == VaultStreamingFileTree.RootKind)
            {
                var root = JsonSerializer.Deserialize<VaultStreamingFileTree.Root>(data);
                if (root is null || root.ChunkSize != VaultStreamingFileTree.ChunkBytes ||
                    root.Length < 0 || root.Tree is null ||
                    root.Tree.Length != root.Length ||
                    root.Sha256Hex is not { Length: 64 } digest ||
                    !digest.All(Uri.IsHexDigit) ||
                    root.ContentType is not { Length: > 0 and <= 128 } mime ||
                    !mime.All(c => c is >= '!' and <= '~'))
                    return false;
                StellarLedgerFileProtocol.ValidateName(root.FileName);
                children = [root.Tree];
            }
            else return false;

            return children.All(x => x is not null && x.Id is { Length: 16 } &&
                x.Id.All(Uri.IsHexDigit) && x.Length >= 0 &&
                x.Sha256Hex is { Length: 64 } && x.Sha256Hex.All(Uri.IsHexDigit)) &&
                children.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
                children.Length;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException)
        {
            return false;
        }
    }

    public static string[] Problems(VaultPendingMutation mutation,
        IReadOnlyList<VaultPendingMutation> all,
        IReadOnlyList<LedgerRecordItem>? remote = null)
    {
        var normal = VaultQueuedCreatePolicy.Problems(mutation, all);
        if (mutation.Kind != VaultMutationKind.CreateRecord ||
            mutation.OriginStrategyId != VaultMutationStrategyPolicy.Linked)
            return normal;
        LedgerRecordDraft? draft;
        try { draft = JsonSerializer.Deserialize<LedgerRecordDraft>(mutation.EncryptedPayload); }
        catch (JsonException) { return normal; }
        if (draft?.Content is null || !draft.Content.StartsWith(Prefix, StringComparison.Ordinal))
            return normal;
        if (!TryDecode(draft, out var kind, out var bytes))
            return ["tree-node-invalid"];
        VaultStreamingFileTree.Reference[] children;
        try
        {
            if (!ValidateNode(kind, bytes, out children))
                return ["tree-node-invalid"];
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }

        // Failed/interrupted staging may leave pending leaf records but no
        // completed root. Such orphan nodes must never be approved for an
        // on-chain transaction. A root belongs to exactly this file group.
        var groupTag = draft.Tags![1];
        if (kind != VaultStreamingFileTree.RootKind &&
            !all.Any(x => x.Kind == VaultMutationKind.CreateRecord &&
                x.State == VaultSyncState.Pending &&
                x.TransactionHash is null &&
                x.OriginStrategyId == VaultMutationStrategyPolicy.Linked &&
                HasRootTag(x, groupTag)))
            return ["tree-root-not-staged"];

        foreach (var child in children)
        {
            var entries = all.Where(x => x.EntityId == child.Id &&
                x.Kind == VaultMutationKind.CreateRecord &&
                x.OriginStrategyId == VaultMutationStrategyPolicy.Linked).ToArray();
            if (entries.Length != 1 || entries[0].State != VaultSyncState.Confirmed)
                return ["tree-child-not-historically-confirmed"];
            var original = entries[0];
            LedgerRecordDraft? staged;
            try { staged = JsonSerializer.Deserialize<LedgerRecordDraft>(original.EncryptedPayload); }
            catch (JsonException) { return ["tree-child-invalid"]; }
            if (staged is null ||
                staged.Tags is not { Length: 2 } childTags ||
                childTags[1] != groupTag ||
                !TryDecode(staged, out _, out var childBytes))
                return ["tree-child-invalid"];
            try
            {
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(childBytes)),
                    child.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                    return ["tree-child-digest-mismatch"];
            }
            finally { CryptographicOperations.ZeroMemory(childBytes); }
            if (remote is not null)
            {
                var published = remote.Where(x => x.Id == child.Id).ToArray();
                if (published.Length != 1 ||
                    published[0].Content != staged.Content ||
                    published[0].Title != staged.Title ||
                    published[0].FolderId != staged.FolderId ||
                    published[0].IsFolder != staged.IsFolder ||
                    (published[0].UserName ?? "") != (staged.UserName ?? "") ||
                    (published[0].Url ?? "") != (staged.Url ?? "") ||
                    (published[0].Notes ?? "") != (staged.Notes ?? "") ||
                    !(published[0].Tags ?? []).SequenceEqual(staged.Tags ?? [], StringComparer.Ordinal))
                    return ["tree-child-changed-on-ledger"];
            }
        }

        // Reuse the same full-depth remote graph check for both independent
        // signers. The HD path calls this exact verifier with its own strategy.
        if (remote is not null && children.Length > 0)
        {
            var problem = RemoteDescendantProblem(children, all, remote,
                groupTag, VaultMutationStrategyPolicy.Linked);
            if (problem is not null) return [problem];
        }

        return normal.Where(x => x != "file-protocol-needs-staged-review").ToArray();
    }

    /// <summary>
    /// Strategy-neutral read-only SVTREE3 descendant verification. Every
    /// referenced node must be historically Confirmed, authenticated against
    /// its exact draft, and unchanged in the same fresh remote snapshot.
    /// Legacy untagged queue entries are accepted only for the HD strategy.
    /// No signing, state transition or publication happens in this method.
    /// </summary>
    public static string? RemoteDescendantProblem(
        IReadOnlyList<VaultStreamingFileTree.Reference> children,
        IReadOnlyList<VaultPendingMutation> all,
        IReadOnlyCollection<LedgerRecordItem> remote,
        string groupTag, string strategyId)
    {
        if (strategyId != VaultMutationStrategyPolicy.Linked &&
            strategyId != VaultMutationStrategyPolicy.Hd)
            return "tree-descendant-invalid-strategy";
        if (groupTag is null || children is null || all is null || remote is null)
            return "tree-descendant-invalid";
        // Build bounded indexes once: a streamed multi-level tree can
        // contain thousands of nodes, so rescanning the entire journal
        // and ledger for every descendant would be quadratic.
        var localById = all.ToLookup(x => x.EntityId, StringComparer.OrdinalIgnoreCase);
        var remoteById = remote.ToLookup(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? CheckDescendant(VaultStreamingFileTree.Reference reference, int depth)
        {
            if (depth > 64 || !seen.Add(reference.Id))
                return "tree-descendant-cycle-or-reuse";
            var matches = localById[reference.Id].Where(x =>
                x.Kind == VaultMutationKind.CreateRecord &&
                (strategyId == VaultMutationStrategyPolicy.Hd
                    ? x.OriginStrategyId is null or VaultMutationStrategyPolicy.Hd
                    : x.OriginStrategyId == VaultMutationStrategyPolicy.Linked)).ToArray();
            if (matches.Length != 1 || matches[0].State != VaultSyncState.Confirmed)
                return "tree-descendant-not-historically-confirmed";
            LedgerRecordDraft? expected;
            try { expected = JsonSerializer.Deserialize<LedgerRecordDraft>(
                matches[0].EncryptedPayload); }
            catch (JsonException) { return "tree-descendant-invalid"; }
            if (expected?.Tags is not { Length: 2 } tags ||
                tags[1] != groupTag ||
                !TryDecode(expected, out var nodeKind, out var nodeBytes))
                return "tree-descendant-invalid";
            try
            {
                if (!ValidateNode(nodeKind, nodeBytes, out var nested) ||
                    !string.Equals(Convert.ToHexString(SHA256.HashData(nodeBytes)),
                        reference.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                    return "tree-descendant-invalid";

                // A content hash proves only the exact serialized node,
                // not that its claimed subtree length is truthful. Reject
                // malformed reference lengths before the final file root
                // can be quoted or committed on Stellar.
                if (nodeKind == VaultStreamingFileTree.LeafKind)
                {
                    var leaf = JsonSerializer.Deserialize<VaultStreamingFileTree.Leaf>(
                        nodeBytes);
                    try
                    {
                        if (leaf?.Data is null ||
                            leaf.Data.LongLength != reference.Length)
                            return "tree-descendant-length-mismatch";
                    }
                    finally
                    {
                        if (leaf?.Data is not null)
                            CryptographicOperations.ZeroMemory(leaf.Data);
                    }
                }
                else if (nodeKind == VaultStreamingFileTree.BranchKind)
                {
                    if (reference.Length == 0)
                        return "tree-descendant-length-mismatch";
                    long sum = 0;
                    foreach (var child in nested)
                    {
                        if (child.Length > reference.Length - sum)
                            return "tree-descendant-length-mismatch";
                        sum += child.Length;
                    }
                    if (sum != reference.Length)
                        return "tree-descendant-length-mismatch";
                }
                else
                    return "tree-descendant-invalid";

                var published = remoteById[reference.Id].ToArray();
                if (published.Length != 1 ||
                    published[0].Title != expected.Title ||
                    published[0].Content != expected.Content ||
                    published[0].FolderId != expected.FolderId ||
                    published[0].IsFolder != expected.IsFolder ||
                    (published[0].UserName ?? "") != (expected.UserName ?? "") ||
                    (published[0].Url ?? "") != (expected.Url ?? "") ||
                    (published[0].Notes ?? "") != (expected.Notes ?? "") ||
                    !(published[0].Tags ?? []).SequenceEqual(expected.Tags ?? [],
                        StringComparer.Ordinal))
                    return "tree-descendant-changed-on-ledger";
                foreach (var nestedReference in nested)
                {
                    var problem = CheckDescendant(nestedReference, depth + 1);
                    if (problem is not null) return problem;
                }
                return null;
            }
            finally { CryptographicOperations.ZeroMemory(nodeBytes); }
        }

        foreach (var child in children)
        {
            var problem = CheckDescendant(child, 0);
            if (problem is not null) return problem;
        }
        return null;
    }

    /// <summary>Immutable file-tree cleanup ordering: do not discard the
    /// root after any child has been historically confirmed, and do not
    /// discard a child still referenced by its pending root.</summary>
    public static bool BlocksDiscard(VaultPendingMutation candidate,
        IReadOnlyList<VaultPendingMutation> all)
    {
        LedgerRecordDraft? draft;
        try { draft = JsonSerializer.Deserialize<LedgerRecordDraft>(candidate.EncryptedPayload); }
        catch (JsonException) { return false; }
        if (draft?.Tags is not { Length: 2 } tags ||
            tags[1] is null ||
            !tags[1].StartsWith("svtree3-group:", StringComparison.Ordinal) ||
            !TryDecode(draft, out var kind, out var payload))
            return false;
        CryptographicOperations.ZeroMemory(payload);
        var groupTag = tags[1];
        if (kind == VaultStreamingFileTree.RootKind)
            return all.Any(x => x.Id != candidate.Id &&
                x.State == VaultSyncState.Confirmed && SameFileGroup(x, groupTag));
        return all.Any(x => x.Id != candidate.Id &&
            x.State == VaultSyncState.Pending && x.TransactionHash is null &&
            HasRootTag(x, groupTag));
    }

    private static bool SameFileGroup(VaultPendingMutation entry, string groupTag)
    {
        try
        {
            var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(entry.EncryptedPayload);
            return draft?.Tags is { Length: 2 } tags && tags[1] == groupTag &&
                TryDecodeGroup(draft);
        }
        catch (JsonException) { return false; }
    }

    private static bool TryDecodeGroup(LedgerRecordDraft draft)
    {
        if (!TryDecode(draft, out _, out var data)) return false;
        CryptographicOperations.ZeroMemory(data);
        return true;
    }

    private static bool HasRootTag(VaultPendingMutation entry, string tag)
    {
        try
        {
            var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(entry.EncryptedPayload);
            return draft is not null &&
                draft.Tags is [RootTag, var group] && group == tag &&
                IsTypedNode(draft);
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// A read-only, fully authenticated file inventory for future whole-tree
    /// deletion. It is NOT a transaction approval or a stable ledger lock.
    /// All nodes are listed only after complete SHA-256 verification.
    /// </summary>
    public sealed record VerifiedInventory(
        VaultStreamingFileTree.Root Manifest, string[] NodeIds, string GroupTag);

    public static async Task<VerifiedInventory> InspectVerifiedAsync(
        string rootId, IReadOnlyList<LedgerRecordItem> ledger,
        CancellationToken ct = default)
    {
        var ids = new List<string>();
        var (manifest, group) = await ReadScopedAsync(
            rootId, ledger, Stream.Null, ids, ct);
        // The root is first and every node is unique. A second root with this
        // group identity makes the ownership set ambiguous, even if its
        // current contents happen to be identical.
        if (ledger.Any(x => !x.Id.Equals(rootId, StringComparison.OrdinalIgnoreCase) &&
            x.Tags is ["svtree3-root", var otherGroup] && otherGroup == group))
            throw new InvalidDataException(
                "Another file root claims this streaming file group; deletion ownership is ambiguous.");
        return new VerifiedInventory(manifest, ids.ToArray(), group);
    }

    /// <summary>Authenticate all referenced on-chain nodes and stream only into
    /// a private temporary stream. The caller must expose it only on success.</summary>
    public static async Task<VaultStreamingFileTree.Root> ReadAsync(
        string rootId, IReadOnlyList<LedgerRecordItem> ledger,
        Stream privateStaging, CancellationToken ct = default)
    {
        var (manifest, _) = await ReadScopedAsync(rootId, ledger,
            privateStaging, null, ct);
        return manifest;
    }

    private static async Task<(VaultStreamingFileTree.Root Manifest, string GroupTag)>
        ReadScopedAsync(string rootId, IReadOnlyList<LedgerRecordItem> ledger,
            Stream privateStaging, List<string>? verifiedIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var byId = new Dictionary<string, LedgerRecordItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ledger)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) ||
                !byId.TryAdd(item.Id, item))
                throw new InvalidDataException("Duplicate or missing linked file ledger ID.");
        }
        if (!byId.TryGetValue(rootId, out var root) ||
            !IsRootRecord(root) ||
            root.Tags is not ["svtree3-root", _])
            throw new InvalidDataException("Missing or malformed authenticated file root.");
        var groupTag = root.Tags[1];
        // ReadVerifiedAsync checks the entire tree and content checksum; the
        // collector is never returned on a failure or cancellation.
        var manifest = await VaultStreamingFileTree.ReadVerifiedAsync(rootId, (id, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(id, out var record) ||
                record.Tags is not { Length: 2 } || record.Tags[1] != groupTag ||
                !TryDecode(new LedgerRecordDraft(record.Title, record.Content,
                    record.FolderId, record.Tags, record.IsFolder,
                    record.UserName, record.Url, record.Notes),
                    out var kind, out var bytes))
                throw new InvalidDataException(
                    "Missing, cross-group or invalid authenticated ledger tree node.");
            verifiedIds?.Add(id);
            return Task.FromResult(new VaultStreamingFileTree.Emission(id, kind, bytes));
        }, privateStaging, ct);
        return (manifest, groupTag);
    }
}
