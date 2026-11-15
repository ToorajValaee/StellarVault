using System.Security.Cryptography;
using System.Text.Json;

namespace StellarVault.Core.Services;

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
        string title, string? folderId = null)
    {
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
            folderId, [tag]);
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
            if (expectedTag is null || draft.Tags is not { Length: 1 } tags ||
                tags[0] != expectedTag ||
                (metadata.Kind != VaultStreamingFileTree.RootKind &&
                 draft.FolderId is not null))
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
                return leaf?.Data is { Length: <= VaultStreamingFileTree.ChunkBytes };
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
                    !digest.All(Uri.IsHexDigit))
                    return false;
                StellarLedgerFileProtocol.ValidateName(root.FileName);
                children = [root.Tree];
            }
            else return false;

            return children.All(x => x is not null && x.Id is { Length: 16 } &&
                x.Id.All(Uri.IsHexDigit) && x.Length >= 0 &&
                x.Sha256Hex is { Length: 64 } && x.Sha256Hex.All(Uri.IsHexDigit));
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
            if (staged is null || !TryDecode(staged, out _, out var childBytes))
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
                    !(published[0].Tags ?? []).SequenceEqual(staged.Tags ?? [], StringComparer.Ordinal))
                    return ["tree-child-changed-on-ledger"];
            }
        }

        return normal.Where(x => x != "file-protocol-needs-staged-review").ToArray();
    }

    /// <summary>Authenticate all referenced on-chain nodes and stream only into
    /// a private temporary stream. The caller must expose it only on success.</summary>
    public static Task<VaultStreamingFileTree.Root> ReadAsync(
        string rootId, IReadOnlyList<LedgerRecordItem> ledger,
        Stream privateStaging, CancellationToken ct = default)
    {
        var byId = new Dictionary<string, LedgerRecordItem>(StringComparer.Ordinal);
        foreach (var item in ledger)
        {
            if (!byId.TryAdd(item.Id, item))
                throw new InvalidDataException("Duplicate linked file ledger ID.");
        }
        return VaultStreamingFileTree.ReadVerifiedAsync(rootId, (id, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(id, out var record) ||
                !TryDecode(new LedgerRecordDraft(record.Title, record.Content,
                    record.FolderId, record.Tags, record.IsFolder,
                    record.UserName, record.Url, record.Notes),
                    out var kind, out var bytes))
                throw new InvalidDataException("Missing or invalid authenticated ledger tree node.");
            return Task.FromResult(new VaultStreamingFileTree.Emission(id, kind, bytes));
        }, privateStaging, ct);
    }
}
