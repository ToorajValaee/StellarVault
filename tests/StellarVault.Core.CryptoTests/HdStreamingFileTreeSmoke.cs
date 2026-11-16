using System.Security.Cryptography;
using System.Text.Json;
using VaultEngine.Stellar.Services;

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

            var verified = await VaultLinkedTreeFilePolicy.InspectVerifiedAsync(
                result.RootId, remote);
            Check(verified.NodeIds.Length == remote.Count &&
                verified.NodeIds[0] == result.RootId &&
                verified.NodeIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
                    verified.NodeIds.Length &&
                verified.GroupTag == "svtree3-group:" + group &&
                verified.Manifest.Sha256Hex == manifest.Sha256Hex,
                "Authenticated SVTREE3 deletion inventory lost or duplicated nodes.");

            // Complete tree deletion planning is read-only: it pins every
            // authenticated node and must not misinterpret a missing node as
            // confirmed deletion after a crash or another device's write.
            var deletion = await VaultVerifiedTreeDeletionReview.BuildAsync(
                result.RootId, remote);
            Check(deletion.Nodes.Length == remote.Count &&
                deletion.Nodes[0].RecordId == result.RootId &&
                deletion.Nodes.Select(x => x.RecordId).Distinct(
                    StringComparer.OrdinalIgnoreCase).Count() == remote.Count,
                "SVTREE3 deletion inventory omitted or duplicated an authenticated node.");
            var pristine = await VaultVerifiedTreeDeletionReview.InspectAsync(deletion, remote);
            Check(pristine.Unchanged && pristine.Present == pristine.Total,
                "Unchanged authenticated file graph failed deletion preflight.");
            var changedNode = remote.Select(x => x.Id == leaf.EntityId
                ? x with { Title = "Changed on other device" } : x).ToArray();
            var drift = await VaultVerifiedTreeDeletionReview.InspectAsync(
                deletion, changedNode);
            Check(!drift.Unchanged && drift.Present == deletion.Nodes.Length,
                "Mutated published file node bypassed pinned deletion snapshot.");
            var missingNode = remote.Where(x => x.Id != leaf.EntityId).ToArray();
            var partial = await VaultVerifiedTreeDeletionReview.InspectAsync(
                deletion, missingNode);
            Check(!partial.Unchanged && partial.Present == deletion.Nodes.Length - 1,
                "Missing file node was mistaken for a historically confirmed deletion.");
            var hiddenGroupNode = remote.Append(remote[0] with {
                Id = "fedcba9876543210"
            }).ToArray();
            try
            {
                await VaultVerifiedTreeDeletionReview.BuildAsync(
                    result.RootId, hiddenGroupNode);
                throw new Exception("Orphan file group node was accepted for whole-file deletion.");
            }
            catch (InvalidDataException) { }
            var tamperedPlan = deletion with { Nodes = deletion.Nodes.Select((x, i) =>
                i == 1 ? x with { RecordSha256 = new string('0', 64) } : x).ToArray() };
            try
            {
                await VaultVerifiedTreeDeletionReview.InspectAsync(
                    tamperedPlan, remote);
                throw new Exception("Tampered deletion plan was accepted.");
            }
            catch (InvalidDataException) { }

            // A valid content hash is not sufficient ownership: mixing two
            // distinct file groups must fail even when the child bytes are
            // otherwise identical. The downloader must fail closed too.
            var crossGroup = remote.Select(x => x.Id == leaf.EntityId
                ? x with { Tags = ["svtree3-leaf", "svtree3-group:fedcba9876543210"] }
                : x).ToArray();
            try
            {
                await VaultLinkedTreeFilePolicy.InspectVerifiedAsync(result.RootId, crossGroup);
                throw new Exception("Cross-file SVTREE3 node ownership was accepted.");
            }
            catch (InvalidDataException) { }
            try
            {
                await VaultLinkedTreeFilePolicy.ReadAsync(result.RootId, crossGroup, Stream.Null);
                throw new Exception("Cross-file SVTREE3 download was accepted.");
            }
            catch (InvalidDataException) { }
            var duplicateRoot = remote.Append(remote[^1] with {
                Id = "fedcba9876543210"
            }).ToArray();
            try
            {
                await VaultLinkedTreeFilePolicy.InspectVerifiedAsync(result.RootId, duplicateRoot);
                throw new Exception("Ambiguous shared file group became a deletion inventory.");
            }
            catch (InvalidDataException) { }

            // The last root can have an unchanged immediate branch while an
            // earlier leaf was modified from another device. A fresh HD quote
            // must reject this deep drift exactly as the independent S-key
            // queue does, rather than only noticing it during later download.
            Check(VaultHdTreeFilePolicy.RemoteProblems(nodes[^1], nodes, remote).Length == 0,
                "Authentic HD file tree failed full-depth remote preflight.");
            var staleLeaf = remote.Select(x => x.Id == leaf.EntityId
                ? x with { Notes = "remote leaf changed after branch confirmation" }
                : x).ToArray();
            Check(VaultHdTreeFilePolicy.RemoteProblems(nodes[^1], nodes, staleLeaf)
                    .Contains("tree-descendant-changed-on-ledger"),
                "HD root accepted a changed grandchild under its unchanged branch.");
            var missingGrandchild = remote.Where(x => x.Id != leaf.EntityId).ToArray();
            Check(VaultHdTreeFilePolicy.RemoteProblems(nodes[^1], nodes, missingGrandchild)
                    .Contains("tree-descendant-changed-on-ledger"),
                "HD root accepted a missing grandchild under its unchanged branch.");

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
