using System.Security.Cryptography;
using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// Streaming, bounded-memory file TREE PLANNING primitive for the next unlaunched
/// on-ledger format. It emits leaf records before branch records, and emits the
/// root manifest LAST. The sink MUST persist/encrypt its payload before returning.
/// This class does not sign, broadcast, grant consent, or reconcile transactions.
/// Integrate through the existing encrypted journal/fee/historical-proof pipeline,
/// never by submitting directly from this callback.
/// </summary>
public static class VaultStreamingFileTree
{
    // JSON/base64 + encryption metadata must leave headroom for ledger transport.
    public const int ChunkBytes = 1024;
    // Conservative ledger record envelope budget. Branches do not contain raw
    // file bytes; their bounded fanout is independent of total file length.
    public const int Fanout = 8;
    public const string LeafKind = "svfile-tree-leaf";
    public const string BranchKind = "svfile-tree-branch";
    public const string RootKind = "svfile-tree-root";

    public sealed record Reference(string Id, long Length, string Sha256Hex);
    public sealed record Leaf(byte[] Data);
    public sealed record Branch(Reference[] Children);
    public sealed record Root(string FileName, string ContentType,
        long Length, string Sha256Hex, Reference Tree);
    public sealed record Result(string RootId, Root Manifest, long NodeCount);

    /// <summary>
    /// Plaintext is ephemeral and is wiped after the sink completes. The sink
    /// MUST copy/encrypt synchronously before returning. A callback failure
    /// aborts the plan; its caller must preserve an incomplete local journal.
    /// </summary>
    public sealed record Emission(string Id, string Kind, byte[] Plaintext);

    public static Task<Result> WriteAsync(
        Stream input, string fileName, string contentType,
        Func<Emission, CancellationToken, Task> persistEncryptedNode,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(persistEncryptedNode);
        return WriteCoreAsync(input, fileName, contentType, async (node, token) =>
        {
            await persistEncryptedNode(node, token);
            return node.Id;
        }, ct);
    }

    /// <summary>
    /// The sink returns the durable ciphertext content address. Branches refer
    /// directly to these addresses; no file-wide node ID mapping is necessary.
    /// The sink must authenticate/encrypt, durably store, and return the actual
    /// object hash before this method emits another node.
    /// </summary>
    public static Task<Result> WriteContentAddressedAsync(
        Stream input, string fileName, string contentType,
        Func<Emission, CancellationToken, Task<string>> persistEncryptedNode,
        CancellationToken ct = default) =>
        WriteCoreAsync(input, fileName, contentType, persistEncryptedNode, ct);

    private static async Task<Result> WriteCoreAsync(
        Stream input, string fileName, string contentType,
        Func<Emission, CancellationToken, Task<string>> persistEncryptedNode,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(persistEncryptedNode);
        if (!input.CanRead) throw new ArgumentException("Readable stream required.", nameof(input));
        StellarLedgerFileProtocol.ValidateName(fileName);
        if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > 128 ||
            contentType.Any(c => c < '!' || c > '~'))
            throw new ArgumentException("Invalid file MIME type.", nameof(contentType));

        var pending = new List<List<Reference>> { new() };
        using var totalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkBytes];
        long total = 0;
        long emitted = 0;

        async Task<string> EmitAsync(string kind, byte[] payload)
        {
            try
            {
                var id = await persistEncryptedNode(
                    new Emission(NewId(), kind, payload), ct);
                RequireId(id);
                emitted = checked(emitted + 1);
                return id;
            }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }

        async Task AddAsync(int level, Reference reference)
        {
            while (pending.Count <= level) pending.Add(new List<Reference>());
            var list = pending[level];
            list.Add(reference);
            if (list.Count < Fanout) return;
            var group = list.ToArray();
            list.Clear();
            var parent = await WriteBranchAsync(group);
            await AddAsync(level + 1, parent);
        }

        async Task<Reference> WriteBranchAsync(Reference[] children)
        {
            if (children.Length is < 1 or > Fanout)
                throw new InvalidDataException("Invalid file tree branch fanout.");
            long length = 0;
            foreach (var child in children) length = checked(length + child.Length);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Branch(children));
            // Reference digests commit to the EXACT serialized branch payload.
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var id = await EmitAsync(BranchKind, bytes);
            return new Reference(id, length, hash);
        }

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // Streams may yield partial reads. Fill a leaf unless at EOF.
                int filled = 0;
                while (filled < buffer.Length)
                {
                    int n = await input.ReadAsync(buffer.AsMemory(filled), ct);
                    if (n == 0) break;
                    filled += n;
                }
                if (filled == 0) break;
                total = checked(total + filled);
                totalHash.AppendData(buffer.AsSpan(0, filled));
                var leafBytes = buffer.AsSpan(0, filled).ToArray();
                byte[] payload;
                try { payload = JsonSerializer.SerializeToUtf8Bytes(new Leaf(leafBytes)); }
                finally { CryptographicOperations.ZeroMemory(leafBytes); }
                var digest = Convert.ToHexString(SHA256.HashData(payload));
                var id = await EmitAsync(LeafKind, payload);
                await AddAsync(0, new Reference(id, filled, digest));
                if (filled != buffer.Length) break;
            }
            // Empty files are representable: the zero-byte leaf has a digest.
            if (total == 0)
            {
                var empty = JsonSerializer.SerializeToUtf8Bytes(new Leaf([]));
                var digest = Convert.ToHexString(SHA256.HashData(empty));
                var id = await EmitAsync(LeafKind, empty);
                await AddAsync(0, new Reference(id, 0, digest));
            }

            Reference? top = null;
            for (int level = 0; level < pending.Count; level++)
            {
                if (pending[level].Count == 0) continue;
                if (pending[level].Count == 1 &&
                    pending.Skip(level + 1).All(x => x.Count == 0))
                {
                    top = pending[level][0];
                    break;
                }
                var children = pending[level].ToArray();
                pending[level].Clear();
                var parent = await WriteBranchAsync(children);
                await AddAsync(level + 1, parent);
            }
            if (top is null || top.Length != total)
                throw new InvalidDataException("File tree length commitment is invalid.");

            var root = new Root(fileName, contentType, total,
                Convert.ToHexString(totalHash.GetHashAndReset()), top);
            var rootId = await EmitAsync(RootKind,
                JsonSerializer.SerializeToUtf8Bytes(root));
            return new Result(rootId, root, emitted);
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    /// <summary>
    /// Verify one authenticated root and stream its contents into a TEMPORARY
    /// destination. The caller MUST publish/rename that temporary destination
    /// only after this method returns successfully. On failure the destination
    /// may contain partial plaintext and must be destroyed by the caller.
    /// The loader must authenticate/decrypt its encrypted vault records.
    /// </summary>
    public static async Task<Root> ReadVerifiedAsync(
        string rootId, Func<string, CancellationToken, Task<Emission>> loadAuthenticatedNode,
        Stream temporaryDestination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(loadAuthenticatedNode);
        ArgumentNullException.ThrowIfNull(temporaryDestination);
        if (!temporaryDestination.CanWrite)
            throw new ArgumentException("Writable temporary stream required.", nameof(temporaryDestination));
        RequireId(rootId);
        var rootEmission = await loadAuthenticatedNode(rootId, ct);
        Root root;
        try
        {
            if (rootEmission.Id != rootId || rootEmission.Kind != RootKind)
                throw new InvalidDataException("Invalid authenticated file root identity.");
            root = JsonSerializer.Deserialize<Root>(rootEmission.Plaintext)
                ?? throw new InvalidDataException("Missing streaming file root.");
            StellarLedgerFileProtocol.ValidateName(root.FileName);
            if (string.IsNullOrWhiteSpace(root.ContentType) ||
                root.ContentType.Length > 128 ||
                root.ContentType.Any(c => c < '!' || c > '~') ||
                root.Length < 0 || !ValidDigest(root.Sha256Hex) ||
                root.Tree is null || root.Tree.Length != root.Length)
                throw new InvalidDataException("Invalid root file metadata.");
        }
        finally { CryptographicOperations.ZeroMemory(rootEmission.Plaintext); }

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;

        async Task VisitAsync(Reference node, int depth)
        {
            if (depth > 64)
                throw new InvalidDataException("Streaming file tree depth exceeded.");
            RequireId(node.Id);
            if (node.Length < 0 || !ValidDigest(node.Sha256Hex))
                throw new InvalidDataException("Invalid streaming file tree reference.");
            var emitted = await loadAuthenticatedNode(node.Id, ct);
            try
            {
                if (emitted.Id != node.Id ||
                    !string.Equals(Convert.ToHexString(SHA256.HashData(emitted.Plaintext)),
                        node.Sha256Hex, StringComparison.Ordinal))
                    throw new InvalidDataException("Streaming file node checksum mismatch.");
                if (emitted.Kind == LeafKind)
                {
                    var leaf = JsonSerializer.Deserialize<Leaf>(emitted.Plaintext)
                        ?? throw new InvalidDataException("Missing streaming file leaf.");
                    try
                    {
                        if (leaf.Data is null || leaf.Data.LongLength != node.Length ||
                            leaf.Data.Length > ChunkBytes)
                            throw new InvalidDataException("Invalid streaming file leaf length.");
                        written = checked(written + leaf.Data.LongLength);
                        if (written > root.Length)
                            throw new InvalidDataException("Streaming file exceeds root length.");
                        digest.AppendData(leaf.Data);
                        await temporaryDestination.WriteAsync(leaf.Data, ct);
                    }
                    finally
                    {
                        if (leaf.Data is not null) CryptographicOperations.ZeroMemory(leaf.Data);
                    }
                }
                else if (emitted.Kind == BranchKind)
                {
                    var branch = JsonSerializer.Deserialize<Branch>(emitted.Plaintext)
                        ?? throw new InvalidDataException("Missing streaming file branch.");
                    if (branch.Children is not { Length: >= 1 and <= Fanout } ||
                        node.Length == 0)
                        throw new InvalidDataException("Invalid streaming branch fanout or zero-length branch.");
                    long length = 0;
                    foreach (var child in branch.Children)
                    {
                        if (child is null)
                            throw new InvalidDataException("Null streaming branch reference.");
                        length = checked(length + child.Length);
                    }
                    if (length != node.Length)
                        throw new InvalidDataException("Streaming branch length mismatch.");
                    foreach (var child in branch.Children)
                        await VisitAsync(child, depth + 1);
                }
                else
                    throw new InvalidDataException("Unexpected streaming file node kind.");
            }
            finally { CryptographicOperations.ZeroMemory(emitted.Plaintext); }
        }

        await VisitAsync(root.Tree, 0);
        if (written != root.Length ||
            !string.Equals(Convert.ToHexString(digest.GetHashAndReset()),
                root.Sha256Hex, StringComparison.Ordinal))
            throw new InvalidDataException("Streaming file content checksum mismatch.");
        return root;
    }

    private static bool ValidDigest(string? hex) =>
        hex is { Length: 64 } && hex.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static void RequireId(string? id)
    {
        if (id is not { Length: 16 or 64 } || !id.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid streaming file node ID.");
    }

    private static string NewId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
}
