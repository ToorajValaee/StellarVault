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
    public const int ChunkBytes = StellarLedgerFileProtocol.MaxChunkBytes;
    // Conservative ledger record envelope budget. Branches do not contain raw
    // file bytes; their bounded fanout is independent of total file length.
    public const int Fanout = 12;
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

    public static async Task<Result> WriteAsync(
        Stream input, string fileName, string contentType,
        Func<Emission, CancellationToken, Task> persistEncryptedNode,
        CancellationToken ct = default)
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

        async Task EmitAsync(string id, string kind, byte[] payload)
        {
            try
            {
                await persistEncryptedNode(new Emission(id, kind, payload), ct);
                emitted = checked(emitted + 1);
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
            var id = NewId();
            await EmitAsync(id, BranchKind, bytes);
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
                var id = NewId();
                await EmitAsync(id, LeafKind, payload);
                await AddAsync(0, new Reference(id, filled, digest));
                if (filled != buffer.Length) break;
            }
            // Empty files are representable: the zero-byte leaf has a digest.
            if (total == 0)
            {
                var empty = JsonSerializer.SerializeToUtf8Bytes(new Leaf([]));
                var digest = Convert.ToHexString(SHA256.HashData(empty));
                var id = NewId();
                await EmitAsync(id, LeafKind, empty);
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
            var rootId = NewId();
            await EmitAsync(rootId, RootKind, JsonSerializer.SerializeToUtf8Bytes(root));
            return new Result(rootId, root, emitted);
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static string NewId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
}
