using System.Security.Cryptography;
using StellarVault.Core.Services;

internal static class StreamingFileTreeSmoke
{
    public static async Task RunAsync()
    {
        // Exceeds the old 1 MiB HD file ceiling and the 96 KiB S-key ceiling.
        // Payloads are synthetic. The in-memory test sink is deliberately NOT
        // an authorization to broadcast or persist plaintext in production.
        var data = Enumerable.Range(0, 1024 * 1024 + 19)
            .Select(x => (byte)(x * 17)).ToArray();
        try
        {
            var nodes = new Dictionary<string, (string Kind, byte[] Payload)>(
                StringComparer.Ordinal);
            var emittedKinds = new List<string>();
            await using var input = new MemoryStream(data, writable: false);
            var result = await VaultStreamingFileTree.WriteAsync(input, "archive.bin",
                "application/octet-stream", (node, ct) =>
                {
                    if (!nodes.TryAdd(node.Id, (node.Kind, node.Plaintext.ToArray())))
                        throw new Exception("A streaming file node ID was reused.");
                    emittedKinds.Add(node.Kind);
                    return Task.CompletedTask;
                });
            try
            {
                Check(result.Manifest.Length == data.LongLength &&
                    result.NodeCount == nodes.Count &&
                    emittedKinds[0] == VaultStreamingFileTree.LeafKind &&
                    emittedKinds[^1] == VaultStreamingFileTree.RootKind &&
                    nodes[result.RootId].Kind == VaultStreamingFileTree.RootKind &&
                    emittedKinds.Contains(VaultStreamingFileTree.BranchKind),
                    "Hierarchical streaming plan failed to commit all nodes root-last.");

                Task<VaultStreamingFileTree.Emission> Load(string id, CancellationToken _) =>
                    Task.FromResult(new VaultStreamingFileTree.Emission(id, nodes[id].Kind,
                        nodes[id].Payload.ToArray()));

                await using var verified = new MemoryStream();
                var root = await VaultStreamingFileTree.ReadVerifiedAsync(result.RootId,
                    Load, verified);
                Check(root.Length == data.LongLength &&
                    verified.ToArray().AsSpan().SequenceEqual(data),
                    "Streaming authenticated file reconstruction changed the bytes.");

                // Tampering with a leaf must fail BEFORE the verified destination
                // is promoted. The destination may contain partial bytes.
                var first = nodes.First(x => x.Value.Kind ==
                    VaultStreamingFileTree.LeafKind);
                first.Value.Payload[^1] ^= 1;
                try
                {
                    await using var corrupted = new MemoryStream();
                    try
                    {
                        await VaultStreamingFileTree.ReadVerifiedAsync(result.RootId,
                            Load, corrupted);
                        throw new Exception("Tampered streaming file node was accepted.");
                    }
                    catch (InvalidDataException) { }
                }
                finally { first.Value.Payload[^1] ^= 1; }

                // A sink error must abort planning before any root is emitted,
                // rather than pretending an incomplete upload is committed.
                await using var failSource = new MemoryStream(data, writable: false);
                var processed = 0;
                try
                {
                    await VaultStreamingFileTree.WriteAsync(failSource, "fail.bin",
                        "application/octet-stream", (node, _) =>
                        {
                            processed++;
                            if (processed == 2) throw new IOException("Simulated journal failure");
                            if (node.Kind == VaultStreamingFileTree.RootKind)
                                throw new Exception("Root emitted after staging failure.");
                            return Task.CompletedTask;
                        });
                    throw new Exception("Failed encrypted sink did not abort streaming.");
                }
                catch (IOException) { }
            }
            finally
            {
                foreach (var node in nodes.Values)
                    CryptographicOperations.ZeroMemory(node.Payload);
            }

            // Zero-length files have a deterministic authenticated empty hash.
            var emptyNodes = new Dictionary<string, (string Kind, byte[] Payload)>();
            await using var emptySource = new MemoryStream();
            var emptyPlan = await VaultStreamingFileTree.WriteAsync(emptySource,
                "empty.bin", "application/octet-stream", (node, _) =>
                {
                    emptyNodes.Add(node.Id, (node.Kind, node.Plaintext.ToArray()));
                    return Task.CompletedTask;
                });
            try
            {
                await using var emptyOutput = new MemoryStream();
                var emptyRoot = await VaultStreamingFileTree.ReadVerifiedAsync(
                    emptyPlan.RootId,
                    (id, _) => Task.FromResult(new VaultStreamingFileTree.Emission(
                        id, emptyNodes[id].Kind, emptyNodes[id].Payload.ToArray())),
                    emptyOutput);
                Check(emptyRoot.Length == 0 && emptyOutput.Length == 0,
                    "Empty streaming file reconstruction failed.");
            }
            finally
            {
                foreach (var node in emptyNodes.Values)
                    CryptographicOperations.ZeroMemory(node.Payload);
            }
        }
        finally { CryptographicOperations.ZeroMemory(data); }

        Console.WriteLine("PASS: arbitrary-length streaming tree planning, root-last and integrity checks");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
