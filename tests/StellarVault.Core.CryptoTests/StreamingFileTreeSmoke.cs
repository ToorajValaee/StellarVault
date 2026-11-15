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
            // The actual local encrypted object-store integration: no flat
            // file index, whole-file queue payload, or fixed file length.
            var vaultKey = RandomNumberGenerator.GetBytes(32);
            var scope = System.Text.Encoding.UTF8.GetBytes("stellar:testnet:root-a");
            var wrongScope = System.Text.Encoding.UTF8.GetBytes("stellar:testnet:root-b");
            var encryptedObjects = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            try
            {
                var store = new TestChunkStore(encryptedObjects);
                await using var source = new MemoryStream(data, writable: false);
                var encrypted = await VaultStreamingEncryptedFileStore.StageAsync(
                    source, "encrypted.bin", "application/octet-stream",
                    store, vaultKey, scope);
                Check(encrypted.RootId.Length == 64 &&
                    encryptedObjects.ContainsKey(encrypted.RootId) &&
                    encrypted.NodeCount == encryptedObjects.Count,
                    "Encrypted streaming tree failed to stage ciphertext-addressed root last.");
                await using var output = new MemoryStream();
                var restored = await VaultStreamingEncryptedFileStore.RestoreAsync(
                    encrypted.RootId, store, output, vaultKey, scope);
                Check(restored.Length == data.LongLength &&
                    output.ToArray().AsSpan().SequenceEqual(data),
                    "Encrypted streaming file reconstruction did not match source.");

                await using var isolatedOutput = new MemoryStream();
                try
                {
                    await VaultStreamingEncryptedFileStore.RestoreAsync(
                        encrypted.RootId, store, isolatedOutput, vaultKey, wrongScope);
                    throw new Exception("Cross-vault encrypted file lookup was permitted.");
                }
                catch (CryptographicException)
                {
                    Check(isolatedOutput.Length == 0,
                        "Wrong vault scope left a partial plaintext file.");
                }

                var damagedId = encryptedObjects.Keys.First();
                var damaged = encryptedObjects[damagedId];
                damaged[^1] ^= 1;
                try
                {
                    await using var damagedOutput = new MemoryStream();
                    try
                    {
                        await VaultStreamingEncryptedFileStore.RestoreAsync(
                            encrypted.RootId, store, damagedOutput, vaultKey, scope);
                        throw new Exception("Tampered encrypted file content was accepted.");
                    }
                    catch (CryptographicException)
                    {
                        Check(damagedOutput.Length == 0,
                            "Tampered file left published or staged plaintext.");
                    }
                }
                finally { damaged[^1] ^= 1; }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(vaultKey);
                CryptographicOperations.ZeroMemory(scope);
                CryptographicOperations.ZeroMemory(wrongScope);
                foreach (var value in encryptedObjects.Values)
                    CryptographicOperations.ZeroMemory(value);
            }

            // The same encrypted object and CAS pointer implementations used
            // by PWA/MAUI must handle the new streaming file root end to end.
            var databaseKey = RandomNumberGenerator.GetBytes(32);
            try
            {
                var localObjects = new InMemoryVaultChunkStore();
                var localPointers = new InMemoryVaultRecordIndex();
                const string account = "GAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAWHF";
                var database = new StellarAccountVaultDatabase(
                    localObjects, localPointers, account);
                await using var databaseInput = new MemoryStream(data, writable: false);
                var firstVersion = await database.PutIndexedStreamingFileAsync(
                    "archive.bin", null, databaseInput, databaseKey,
                    "application/octet-stream");
                Check(firstVersion.Generation == 0 &&
                    await database.GetVersionAsync("archive.bin", databaseKey) == firstVersion,
                    "Streaming file root failed to commit an atomic encrypted pointer.");
                await using var databaseResult = new MemoryStream();
                var metadata = await database.ReadStreamingFileAsync(
                    "archive.bin", databaseResult, databaseKey);
                Check(metadata.Length == data.LongLength &&
                    databaseResult.ToArray().AsSpan().SequenceEqual(data),
                    "Streaming vault file did not survive encrypted pointer read.");

                await using var staleInput = new MemoryStream(data, writable: false);
                try
                {
                    await database.PutIndexedStreamingFileAsync("archive.bin",
                        null, staleInput, databaseKey, "application/octet-stream");
                    throw new Exception("Stale streamed file record write was accepted.");
                }
                catch (VaultWriteConflictException) { }
                Check(await database.GetVersionAsync("archive.bin", databaseKey) ==
                    firstVersion, "Rejected streaming write replaced the committed file pointer.");
            }
            finally { CryptographicOperations.ZeroMemory(databaseKey); }

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
