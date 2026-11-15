using System.Security.Cryptography;
using System.Text.Json;
using StellarVault.Core.Services;

/// <summary>Signed-queue policy regression without a network, signer, or
/// plaintext persistence. Publication itself remains an independent host flow.</summary>
internal static class HdStreamingFileTreeSmoke
{
    private const string Account = "GAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAWHF";
    public static async Task RunAsync()
    {
        byte[] input = Enumerable.Range(0, 4_700).Select(x => (byte)(x * 13)).ToArray();
        var nodes = new List<VaultPendingMutation>();
        var group = "0123456789abcdef";
        try
        {
            await using var source = new MemoryStream(input, writable: false);
            var result = await VaultStreamingFileTree.WriteContentAddressedAsync(
                source, "large.bin", "application/octet-stream",
                (node, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var entityId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
                    var draft = VaultLinkedTreeFilePolicy.Draft(node,
                        node.Kind == VaultStreamingFileTree.RootKind ?
                            "large.bin" : "FILE TREE NODE", group);
                    var payload = JsonSerializer.SerializeToUtf8Bytes(draft);
                    try
                    {
                        nodes.Add(VaultPendingMutation.Create(Account, "testnet",
                            VaultMutationKind.CreateRecord, entityId, payload)
                            with { OriginStrategyId = VaultMutationStrategyPolicy.Hd });
                    }
                    finally { CryptographicOperations.ZeroMemory(payload); }
                    return Task.FromResult(entityId);
                });
            Check(nodes.Count == result.NodeCount &&
                nodes[^1].EntityId == result.RootId && nodes.Count >= 6,
                "HD file nodes were not durably ordered root last.");

            var root = nodes[^1];
            var leaf = nodes[0];
            Check(VaultQueuedCreatePolicy.Problems(root, nodes)
                    .Contains("hd-tree-child-not-confirmed"),
                "Unconfirmed HD file tree root became publishable.");
            Check(VaultLinkedTreeFilePolicy.BlocksDiscard(leaf, nodes),
                "Pending HD tree root did not protect a referenced leaf.");
            var stranded = nodes.Where(x => x.Id != root.Id).ToArray();
            Check(VaultQueuedCreatePolicy.Problems(leaf, stranded)
                    .Contains("hd-tree-root-not-staged"),
                "Interrupted HD node staging allowed orphan publication.");

            var remote = new List<LedgerRecordItem>();
            for (var i = 0; i < nodes.Count; i++)
            {
                var item = nodes[i];
                Check(VaultQueuedCreatePolicy.Problems(item, nodes).Length == 0,
                    "HD file-tree dependency review rejected a historically confirmed prefix.");
                Check(VaultHdTreeFilePolicy.RemoteProblems(item, nodes, remote).Length == 0,
                    "HD file-tree remote review rejected a valid confirmed prefix.");
                var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(item.EncryptedPayload)
                    ?? throw new Exception("Staged typed HD file draft vanished.");
                remote.Add(new LedgerRecordItem(item.EntityId, draft.Title,
                    draft.Content, draft.FolderId, draft.Tags, draft.IsFolder,
                    draft.UserName, draft.Url, draft.Notes));
                nodes[i] = item with { State = VaultSyncState.Confirmed };
            }
            await using var restored = new MemoryStream();
            var manifest = await VaultLinkedTreeFilePolicy.ReadAsync(
                result.RootId, remote, restored);
            Check(manifest.Length == input.Length &&
                restored.ToArray().AsSpan().SequenceEqual(input),
                "HD file tree did not reconstruct authenticated bytes.");

            // A missing/replaced remote child must fail last-mile preflight,
            // even when the local journal says that child was confirmed.
            var fakeRoot = root with { State = VaultSyncState.Pending };
            nodes[^1] = fakeRoot;
            var missing = remote.Where(x => x.Id != leaf.EntityId).ToArray();
            // Root may reference a branch rather than this leaf, so test the
            // parent of the missing leaf using the same observed remote data.
            var parent = nodes.First(x => x.EntityId != leaf.EntityId &&
                JsonSerializer.Deserialize<LedgerRecordDraft>(x.EncryptedPayload)!
                    .Tags![0] == "svtree3-branch");
            nodes[nodes.FindIndex(x => x.Id == parent.Id)] =
                parent with { State = VaultSyncState.Pending };
            Check(VaultHdTreeFilePolicy.RemoteProblems(parent, nodes, missing).Length > 0 ||
                VaultHdTreeFilePolicy.Problems(parent, nodes,
                    JsonSerializer.Deserialize<LedgerRecordDraft>(parent.EncryptedPayload)!).Length > 0,
                "Missing child did not block a parent tree publication.");
            Check(VaultLinkedTreeFilePolicy.BlocksDiscard(fakeRoot, nodes),
                "Confirmed HD descendants did not protect their pending root.");
            Console.WriteLine("PASS: HD SVTREE3 typed staging, root-last review and ledger tamper rejection");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            foreach (var mutation in nodes)
                CryptographicOperations.ZeroMemory(mutation.EncryptedPayload);
        }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
