using StellarVault.Core.Services;

internal static class LinkedDraftSmoke
{
    private const string Root = "GAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAWHF";
    private const string Testnet = "Test SDF Network ; September 2015";

    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sv-linked-draft-test-" +
            Guid.NewGuid().ToString("N"));
        try
        {
            var network = new VaultNetworkSelection();
            var hdRetention = new VaultDeviceRetentionGate();
            var linkedRetention = new VaultLinkedLocalRetentionGate();
            linkedRetention.GrantExplicitConsent();
            Require(hdRetention.Policy == VaultDeviceRetentionPolicy.OnChainOnly &&
                    linkedRetention.DeviceRetention.AllowsLocalPersistence,
                "S-key consent incorrectly enabled the independent HD device-storage gate.");
            var ledger = new FakeLinkedLedger();
            var credentials = new FileSystemVaultLinkedPassphraseCredentialStore(
                Path.Combine(directory, "credentials"), network, linkedRetention);
            await using var keys = new VaultLinkedQueueKeySession(
                ledger, network, linkedRetention, credentials,
                new NativeVaultKeyProtector());
            await keys.CreateAsync("local S-key test passphrase 12345");

            var store = new FileSystemVaultLinkedQueueSnapshotStore(keys,
                linkedRetention, Path.Combine(directory, "queue"), network);
            var drafts = new VaultLinkedDraftWorkspace(
                ledger, keys, store, linkedRetention, network);
            var publisher = new VaultLinkedDraftPublication(
                ledger, store, keys, linkedRetention, network);

            var folder = await drafts.CreateAsync(
                new LedgerRecordDraft("Shared Folder", "folder", IsFolder: true));
            var child = await drafts.CreateAsync(new LedgerRecordDraft(
                "Credentials", "unique secret value", folder.EntityId,
                ["personal"], false, "login", "https://example.org", "private notes"));

            Require((await drafts.ListAsync()).Count == 2,
                "Independent encrypted queue failed to persist local drafts.");
            var childRevision = await drafts.OpenDraftAsync(child.MutationId);
            await drafts.ReviseAsync(child.MutationId,
                childRevision.Draft with { Notes = "updated encrypted note" },
                childRevision.PayloadSha256);
            await MustRejectAsync(
                () => drafts.ReviseAsync(child.MutationId,
                    childRevision.Draft with { Notes = "stale conflicting change" },
                    childRevision.PayloadSha256),
                "A stale linked draft edit incorrectly overwrote a newer CAS revision.");
            Require((await drafts.OpenDraftAsync(child.MutationId)).Draft.Notes ==
                "updated encrypted note",
                "Revised encrypted record draft content was not retained.");
            await MustRejectAsync(() => drafts.DiscardAsync(folder.MutationId),
                "Deleting a pending parent with dependent records was permitted.");
            await MustRejectAsync(() => publisher.PrepareAsync(child.MutationId),
                "Pending child folder dependency was approved for publication.");

            var initialPlan = await drafts.ReviewManualSyncAsync();
            Require(initialPlan.PendingCount == 2 && initialPlan.CanReviewNext &&
                    initialPlan.NextReadyMutationId == folder.MutationId &&
                    initialPlan.Items.Single(x => x.MutationId == child.MutationId)
                        .Blockers.Contains("unpublished-local-parent-folder"),
                "Manual sync plan did not put the parent folder before its child.");

            var rejectedQuote = await publisher.PrepareAsync(folder.MutationId);
            await MustRejectAsync(
                () => publisher.SubmitAsync(rejectedQuote.Quote with
                {
                    NetworkFeeXlm = "999.0000000"
                }, true),
                "A changed linked fee quote was accepted.");
            Require(ledger.Submissions == 0,
                "Rejected changed quote sent a network transaction.");

            var folderReview = await publisher.PrepareAsync(folder.MutationId);
            Require(ledger.Submissions == 0,
                "Preparing a linked draft quote unexpectedly broadcast a transaction.");
            var submittedHash = await publisher.SubmitAsync(folderReview.Quote, true);
            Require(submittedHash.Length > 0 && ledger.Submissions == 1 &&
                    (await drafts.ListAsync()).Single(x => x.MutationId == folder.MutationId)
                        .State == VaultSyncState.Uncertain,
                "A matching current record was incorrectly treated as historical transaction proof.");
            Require((await ledger.ReadAsync()).Any(x => x.Id == folder.EntityId),
                "Mock ledger did not persist the current-state record.");
            var afterParentPlan = await drafts.ReviewManualSyncAsync();
            Require(afterParentPlan.UnresolvedCount > 0 && !afterParentPlan.CanReviewNext,
                "Unverified parent submission incorrectly unblocked its child.");
            await MustRejectAsync(() => publisher.PrepareAsync(child.MutationId),
                "Child was offered for publication before independent parent proof.");
            var blockedExpansion = new VaultLinkedExpansionJournal(
                new VaultLinkedAccountExpansion(ledger, network,
                    new VaultAccountStrategyFactory([new FakeLinkedAccountStrategy(ledger)])),
                ledger, store, keys, linkedRetention, network);
            await MustRejectAsync(() => blockedExpansion.PrepareAsync(),
                "Linked expansion was offered while an unresolved record submission exists.");

            var recovery = new VaultLinkedDraftRecoveryReview(ledger, store, keys,
                linkedRetention, network, new DenyVaultLinkedReceiptEvidence());
            var finding = await recovery.InspectAsync(folder.MutationId);
            Require(finding.DurableState is VaultSyncState.Uncertain or VaultSyncState.Submitting &&
                    finding.CurrentRecordMatches is true &&
                    !finding.HistoricalReceiptObserved,
                "Read-only recovery falsely confirmed a snapshot-matching linked submission.");
            var afterRecovery = (await drafts.ListAsync())
                .Single(x => x.MutationId == folder.MutationId);
            Require(afterRecovery.State is VaultSyncState.Uncertain or VaultSyncState.Submitting,
                "Read-only recovery modified the durable uncertain queue state.");

            await keys.LockAsync();
            Require(!keys.IsUnlocked, "Linked queue encryption key remained unlocked.");
            await MustRejectAsync(() => drafts.ListAsync(),
                "Linked encrypted queue was accessible after lock.");

            // Local-only access must use an existing enrolled wrapped key and
            // must not need the imported S-key or Horizon connectivity.
            await ledger.DisconnectAsync();
            Require(await keys.HasLocalCredentialsOfflineAsync(Root),
                "Existing encrypted S-key device credentials were lost.");
            await keys.UnlockOfflineAsync(Root, "local S-key test passphrase 12345");
            Require(keys.IsUnlocked && keys.IsOfflineOnly &&
                keys.ActiveRootAccountId == Root,
                "Offline local-only authentication failed.");
            network.ConfigureCustom("An unrelated unverified network passphrase");
            Require(!keys.IsUnlocked && !keys.CanPublish,
                "Offline encrypted queue authorization crossed a network-selection change.");
            network.Select("testnet");
            Require(keys.IsUnlocked && keys.IsOfflineOnly,
                "Returning to the original verified local account/network scope failed.");
            var offlineRecord = await drafts.CreateAsync(
                new LedgerRecordDraft("Offline personal draft", "local secret"));
            var editOffline = await drafts.OpenDraftAsync(offlineRecord.MutationId);
            await drafts.ReviseAsync(offlineRecord.MutationId,
                editOffline.Draft with { Content = "edited without Horizon" },
                editOffline.PayloadSha256);
            Require((await drafts.OpenDraftAsync(offlineRecord.MutationId)).Draft.Content ==
                "edited without Horizon",
                "Offline encrypted draft revision depended on Horizon.");
            var folderA = await drafts.CreateAsync(
                new LedgerRecordDraft("Local folder A", "folder", IsFolder: true));
            var folderB = await drafts.CreateAsync(
                new LedgerRecordDraft("Local folder B", "folder",
                    folderA.EntityId, IsFolder: true));
            var folderAOpen = await drafts.OpenDraftAsync(folderA.MutationId);
            await MustRejectAsync(
                () => drafts.ReviseAsync(folderA.MutationId,
                    folderAOpen.Draft with { FolderId = folderB.EntityId },
                    folderAOpen.PayloadSha256),
                "Editing a local folder created a parent-dependency cycle.");
            Require((await drafts.ListAsync()).Any(x => x.MutationId == offlineRecord.MutationId),
                "Encrypted local draft failed with disconnected network.");
            var offlineReport = new VaultLinkedQueueInspection(ledger, store, keys,
                linkedRetention, network);
            var report = await offlineReport.InspectAsync();
            Require(report.Pending > 0 && !report.CanPublish,
                "Offline queue inspection authorized publication or omitted local drafts.");
            await MustRejectAsync(() => publisher.PrepareAsync(offlineRecord.MutationId),
                "An offline encrypted queue session authorized an on-chain transaction.");
            ledger.Reconnect();
            Require(keys.IsUnlocked && keys.IsOfflineOnly && !keys.CanPublish,
                "Reconnecting a signer improperly upgraded an offline-only queue unlock.");
            await MustRejectAsync(() => publisher.PrepareAsync(offlineRecord.MutationId),
                "An offline-only queue key could publish merely because a signer reconnected.");
            await keys.UnlockAsync("local S-key test passphrase 12345");
            Require(keys.CanPublish && !keys.IsOfflineOnly,
                "A fresh live signer-backed queue unlock did not establish publication authorization.");
            await keys.LockAsync();
            Require(!keys.IsUnlocked && !keys.CanPublish,
                "Local queue key or publication authorization remained after lock.");
            Console.WriteLine("PASS: linked encrypted drafts, dependency CAS, uncertain outcomes and independent offline-only access");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        await RunSingleFileLinkedSmokeAsync();
        await RunMultipartLinkedSmokeAsync();
        await RunLinkedTreeFileSmokeAsync();
        await RunVerifiedCreateReconciliationSmokeAsync();
        await RunRemoteMutationSmokeAsync(false);
        await RunRemoteMutationSmokeAsync(true);
        await RunPrebroadcastCheckpointFailureSmokeAsync();
        await RunSuccessfulExpansionQuarantineSmokeAsync();
        await RunUncertainExpansionSmokeAsync();
    }

    private static async Task RunLinkedTreeFileSmokeAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "sv-linked-tree-" + Guid.NewGuid().ToString("N"));
        try
        {
            var network = new VaultNetworkSelection();
            var consent = new VaultLinkedLocalRetentionGate();
            consent.GrantExplicitConsent();
            var ledger = new FakeLinkedLedger();
            var credentials = new FileSystemVaultLinkedPassphraseCredentialStore(
                Path.Combine(directory, "credentials"), network, consent);
            await using var keys = new VaultLinkedQueueKeySession(
                ledger, network, consent, credentials, new NativeVaultKeyProtector());
            await keys.CreateAsync("linked streaming tree passphrase");
            var store = new FileSystemVaultLinkedQueueSnapshotStore(
                keys, consent, Path.Combine(directory, "queue"), network);
            var workspace = new VaultLinkedDraftWorkspace(ledger, keys, store, consent, network);
            var publisher = new VaultLinkedDraftPublication(
                ledger, store, keys, consent, network);
            var witness = new CapturingLinkedReceiptEvidence { AllowRecordOperations = true };
            var reviewer = new VaultLinkedDraftRecoveryReview(
                ledger, store, keys, consent, network, witness);
            var reconciler = new VaultLinkedVerifiedReconciliation(
                ledger, store, keys, consent, network, reviewer);

            var bytes = Enumerable.Range(0, 4096)
                .Select(x => (byte)(x * 19)).ToArray();
            await using var source = new MemoryStream(bytes, writable: false);
            var tree = await workspace.StageTreeFileAsync("Encrypted file tree",
                "large.bin", "application/octet-stream", source);
            Require(tree.Manifest.Length == bytes.Length &&
                tree.NodeCount >= 6 && tree.RootId.Length == 16,
                "Linked file tree did not stage all bounded nodes.");
            var local = await workspace.ListAsync();
            Require(local.Count == tree.NodeCount && local.All(x => x.IsFile),
                "Staged file tree was not recognized as encrypted typed records.");

            var first = await workspace.ReviewManualSyncAsync();
            var root = local.Single(x => x.EntityId == tree.RootId);
            var stagedSnapshot = await store.ListAsync(Root, network.CredentialKey(Root));
            try
            {
                var leaf = stagedSnapshot.First(x => x.EntityId != tree.RootId);
                Require(VaultLinkedTreeFilePolicy.Problems(leaf,
                        stagedSnapshot.Where(x => x.EntityId != tree.RootId).ToArray())
                        .Contains("tree-root-not-staged"),
                    "A file node from interrupted staging was publishable without its root.");
            }
            finally
            {
                foreach (var entry in stagedSnapshot)
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(entry.EncryptedPayload);
            }
            Require(first.Items.Single(x => x.MutationId == root.MutationId)
                .Blockers.Contains("tree-child-not-historically-confirmed"),
                "Streaming file root was ready before its children.");
            await MustRejectAsync(() => publisher.PrepareAsync(root.MutationId),
                "Streaming file root bypassed child historical verification.");
            var stagedLeaf = local.First(x => x.EntityId != tree.RootId);
            await MustRejectAsync(() => workspace.DiscardAsync(stagedLeaf.MutationId),
                "A referenced tree leaf was discarded while its root was pending.");
            var sent = 0;
            while (sent < tree.NodeCount)
            {
                var review = await workspace.ReviewManualSyncAsync();
                var next = review.NextReadyMutationId ??
                    throw new Exception("Linked file tree queue stopped before its root.");
                var nextItem = local.Single(x => x.MutationId == next);
                if (sent != tree.NodeCount - 1 &&
                    nextItem.EntityId == tree.RootId)
                    throw new Exception("Linked file root was published before its descendants.");
                var quote = await publisher.PrepareAsync(next);
                var hash = await publisher.SubmitAsync(quote.Quote, true);
                Require((await workspace.ListAsync()).Single(x => x.MutationId == next)
                    .State == VaultSyncState.Uncertain,
                    "Tree file node was confirmed without independent signed history.");
                ledger.AllowHistoricalProof = true;
                var confirmed = await reconciler.ConfirmVerifiedRecordAsync(next);
                Require(confirmed.Confirmed &&
                    ledger.ReleasedVerifiedHash == hash,
                    "Linked file node failed signed XDR history reconciliation.");
                if (sent == 0)
                {
                    await MustRejectAsync(
                        () => workspace.DiscardAsync(root.MutationId),
                        "Pending file root was discarded after a child was published.");
                    // A connection epoch change must not erase the durable
                    // node queue or accidentally mark the root complete.
                    ledger.Reconnect();
                    var restoredWorkspace = new VaultLinkedDraftWorkspace(
                        ledger, keys, store, consent, network);
                    Require((await restoredWorkspace.ReviewManualSyncAsync())
                            .Items.Any(x => x.MutationId == root.MutationId &&
                                x.State == VaultSyncState.Pending),
                        "Linked file tree root was lost across signer reconnect.");
                }
                sent++;
            }
            var onChain = await ledger.ReadAsync();
            await using var reconstructed = new MemoryStream();
            var file = await VaultLinkedTreeFilePolicy.ReadAsync(
                tree.RootId, onChain, reconstructed);
            Require(file.Length == bytes.Length &&
                reconstructed.ToArray().AsSpan().SequenceEqual(bytes) &&
                ledger.Submissions == tree.NodeCount,
                "Linked tree file failed manifest-last authenticated reconstruction.");
            var changed = onChain.Select(x => x.Id == tree.RootId
                ? x with { Content = VaultLinkedTreeFilePolicy.Prefix + "{}" }
                : x).ToArray();
            await MustRejectAsync(async () =>
            {
                await using var failedDownload = new MemoryStream();
                await VaultLinkedTreeFilePolicy.ReadAsync(
                    tree.RootId, changed, failedDownload);
            }, "Tampered linked tree root was accepted for download.");
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            Console.WriteLine("PASS: linked SVTREE3 nodes require individual quotes, history confirmation and root-last verified reconstruction");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RunMultipartLinkedSmokeAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "sv-linked-multipart-" + Guid.NewGuid().ToString("N"));
        try
        {
            var network = new VaultNetworkSelection();
            var consent = new VaultLinkedLocalRetentionGate();
            consent.GrantExplicitConsent();
            var ledger = new FakeLinkedLedger();
            var credentials = new FileSystemVaultLinkedPassphraseCredentialStore(
                Path.Combine(directory, "credentials"), network, consent);
            await using var keys = new VaultLinkedQueueKeySession(
                ledger, network, consent, credentials, new NativeVaultKeyProtector());
            await keys.CreateAsync("linked multipart test passphrase");
            var store = new FileSystemVaultLinkedQueueSnapshotStore(
                keys, consent, Path.Combine(directory, "queue"), network);
            var drafts = new VaultLinkedDraftWorkspace(ledger, keys, store, consent, network);
            var publisher = new VaultLinkedDraftPublication(ledger, store, keys, consent, network);
            var witness = new CapturingLinkedReceiptEvidence { AllowRecordOperations = true };
            var reviewer = new VaultLinkedDraftRecoveryReview(
                ledger, store, keys, consent, network, witness);
            var reconciler = new VaultLinkedVerifiedReconciliation(
                ledger, store, keys, consent, network, reviewer);

            // A corrupted authenticated queue payload may deserialize into a
            // draft with null content. Both HD and linked policy review must
            // report a blocker, never crash or authorize publication.
            var malformedBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new LedgerRecordDraft("Missing content", null!));
            var malformed = VaultPendingMutation.Create(Root,
                VaultLinkedQueueStorageScope.FromNetworkScope(network.CredentialKey(Root)),
                VaultMutationKind.CreateRecord, "0123456789ABCDEF", malformedBytes)
                with { OriginStrategyId = VaultMutationStrategyPolicy.Linked };
            try
            {
                var failures = VaultLinkedMultipartFilePolicy.Problems(malformed, [malformed]);
                Require(failures.Contains("missing-record-content"),
                    "Null-content linked draft was not safely blocked.");
                Require(await store.AddAsync(malformed),
                    "Could not stage synthetic malformed linked queue fixture.");
                try
                {
                    Require((await drafts.ListAsync()).Single(x => x.MutationId == malformed.Id)
                            .IsCorrupt,
                        "Missing linked draft content crashed the queue or appeared healthy.");
                }
                finally
                {
                    Require(await store.TryDeleteAsync(malformed),
                        "Malformed linked queue fixture was not removed.");
                }
                var edge1 = Guid.NewGuid().ToString("N");
                var edge2 = Guid.NewGuid().ToString("N");
                var invalidEdges = malformed with { DependsOn = [edge1, edge2] };
                Require(VaultQueuedCreatePolicy.Problems(invalidEdges, [invalidEdges])
                        .Contains("invalid-parent-dependency"),
                    "Multiple corrupt queue dependencies caused an unsafe review.");
                var duplicateEdge = malformed with { DependsOn = [edge1] };
                Require(VaultQueuedCreatePolicy.Problems(duplicateEdge,
                        [duplicateEdge, duplicateEdge])
                        .Contains("duplicate-local-mutation-id") &&
                    VaultQueuedCreatePolicy.Problems(malformed, [malformed, malformed])
                        .Contains("duplicate-local-mutation-id"),
                    "Duplicate encrypted queue IDs caused an unsafe review.");
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(malformedBytes);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(malformed.EncryptedPayload);
            }

            await MustRejectAsync(
                () => drafts.StageMultipartFileAsync("Too large", "big.bin",
                    "application/octet-stream",
                    new byte[VaultLinkedMultipartFilePolicy.MaxBytes + 1]),
                "Oversized linked multipart manifest was staged without hierarchy.");
            var bytes = Enumerable.Range(0, 4096).Select(x => (byte)x).ToArray();
            var stages = await drafts.StageMultipartFileAsync(
                "Multipart backup", "backup.bin", "application/octet-stream", bytes);
            Require(stages.Count == 3 && stages.All(x => x.IsFile &&
                    x.Kind == VaultMutationKind.CreateRecord) &&
                    (await drafts.ListAsync()).Count == 3,
                "Linked SVFILE2 parts and manifest did not stage as typed encrypted entries.");

            // A local manifest that was discarded, failed, confirmed or had a
            // hash checkpoint must not authorize any further file part writes.
            var journalSnapshot = await store.ListAsync(Root, network.CredentialKey(Root));
            try
            {
                var firstPart = journalSnapshot.Single(x => x.Id == stages[0].MutationId);
                var finalManifest = journalSnapshot.Single(x => x.Id == stages[^1].MutationId);
                foreach (var invalidManifest in new[]
                {
                    finalManifest with { State = VaultSyncState.Failed },
                    finalManifest with { State = VaultSyncState.Confirmed },
                    finalManifest with { State = VaultSyncState.Uncertain },
                    finalManifest with { TransactionHash = new string('a', 64) }
                })
                {
                    var corruptSnapshot = journalSnapshot.Select(x =>
                        x.Id == finalManifest.Id ? invalidManifest : x).ToArray();
                    Require(VaultLinkedMultipartFilePolicy.Problems(firstPart, corruptSnapshot)
                            .Contains("multipart-manifest-missing-or-invalid"),
                        "A non-pending or signed multipart manifest authorized another file part.");
                }
            }
            finally
            {
                foreach (var entry in journalSnapshot)
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(entry.EncryptedPayload);
            }

            var initial = await drafts.ReviewManualSyncAsync();
            Require(initial.NextReadyMutationId == stages[0].MutationId &&
                    initial.Items.Single(x => x.MutationId == stages[1].MutationId)
                        .Blockers.Contains("multipart-previous-part-not-confirmed") &&
                    initial.Items.Single(x => x.MutationId == stages[2].MutationId)
                        .Blockers.Contains("multipart-all-parts-must-be-historically-confirmed"),
                "Multipart planner offered a later part or final manifest too early.");
            await MustRejectAsync(
                () => publisher.PrepareAsync(stages[2].MutationId),
                "Multipart manifest could publish before its file parts.");
            await MustRejectAsync(
                () => publisher.PrepareAsync(stages[1].MutationId),
                "Multipart second part could publish before the first part was verified.");
            await MustRejectAsync(
                () => drafts.DiscardAsync(stages[0].MutationId),
                "A pending multipart manifest lost its still-referenced part.");

            for (int i = 0; i < stages.Count; i++)
            {
                var state = await drafts.ReviewManualSyncAsync();
                Require(state.NextReadyMutationId == stages[i].MutationId,
                    "The next verified multipart part/manifest was not the only next review.");
                var reviewed = await publisher.PrepareAsync(stages[i].MutationId);
                Require(reviewed.Quote.Kind == "create",
                    "A multipart file stage bypassed the reviewed S-key create quote.");
                var hash = await publisher.SubmitAsync(reviewed.Quote, true);
                Require((await drafts.ListAsync()).Single(
                            x => x.MutationId == stages[i].MutationId).State ==
                        VaultSyncState.Uncertain,
                    "Multipart stage was marked confirmed without historic signature proof.");
                ledger.AllowHistoricalProof = true;
                var confirmed = await reconciler.ConfirmVerifiedRecordAsync(
                    stages[i].MutationId);
                Require(confirmed.Confirmed && ledger.ReleasedVerifiedHash == hash &&
                    (await drafts.ListAsync()).Single(
                        x => x.MutationId == stages[i].MutationId).State ==
                        VaultSyncState.Confirmed,
                    "Multipart stage did not complete signed-history encrypted CAS.");
            }

            var all = await ledger.ReadAsync();
            var manifest = all.Single(x => x.Id == stages[^1].EntityId);
            var decoded = StellarLedgerFileProtocol.DecodeFile(manifest, all);
            try
            {
                Require(decoded.SequenceEqual(bytes) &&
                    ledger.Submissions == stages.Count,
                    "Manifest-last S-key multipart publication failed authenticated download or replayed a transaction.");
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(decoded); }
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            Console.WriteLine("PASS: linked SVFILE2 parts require individual quote, history proof and manifest-last authenticated download");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RunSingleFileLinkedSmokeAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "sv-linked-single-file-" + Guid.NewGuid().ToString("N"));
        try
        {
            var network = new VaultNetworkSelection();
            var consent = new VaultLinkedLocalRetentionGate();
            consent.GrantExplicitConsent();
            var ledger = new FakeLinkedLedger();
            var credentials = new FileSystemVaultLinkedPassphraseCredentialStore(
                Path.Combine(directory, "credentials"), network, consent);
            await using var keys = new VaultLinkedQueueKeySession(
                ledger, network, consent, credentials, new NativeVaultKeyProtector());
            await keys.CreateAsync("linked small file staging passphrase");
            var store = new FileSystemVaultLinkedQueueSnapshotStore(
                keys, consent, Path.Combine(directory, "queue"), network);
            var drafts = new VaultLinkedDraftWorkspace(ledger, keys, store, consent, network);
            var publisher = new VaultLinkedDraftPublication(ledger, store, keys, consent, network);
            var bytes = Enumerable.Range(0, 512).Select(x => (byte)x).ToArray();

            const string corruptId = "ffffffffffffffff";
            ledger.SeedRecord(corruptId, new LedgerRecordDraft(
                "Broken file", StellarLedgerFileProtocol.SinglePrefix + "{}",
                null, ["file", "svfile-v1"]));
            var corruptRevision = (await ledger.ReadRecordRevisionAsync(corruptId))!;
            await MustRejectAsync(
                () => drafts.StageDeleteAsync(corruptId, corruptRevision),
                "A malformed SVFILE1 record was allowed to poison the encrypted deletion queue.");
            Require((await drafts.ListAsync()).Count == 0,
                "Rejected malformed file deletion left a durable pending mutation.");

            const string multipartId = "eeeeeeeeeeeeeeee";
            ledger.SeedRecord(multipartId, new LedgerRecordDraft(
                "Unsupported multipart file", StellarLedgerFileProtocol.ManifestPrefix + "{}",
                null, ["file", "svfile-v2"]));
            var multipartRevision = (await ledger.ReadRecordRevisionAsync(multipartId))!;
            await MustRejectAsync(
                () => drafts.StageDeleteAsync(multipartId, multipartRevision),
                "Unsupported multipart file deletion bypassed the explicit capability gate.");
            Require((await drafts.ListAsync()).Count == 0,
                "Unsupported multipart deletion created an unrecoverable encrypted queue entry.");

            await MustRejectAsync(
                () => drafts.StageSmallFileAsync("Oversize", "oversize.bin",
                    "application/octet-stream", new byte[VaultLinkedSingleFilePolicy.MaxBytes + 1]),
                "Multipart S-key file unexpectedly bypassed the single-record limit.");
            await MustRejectAsync(
                () => drafts.StageSmallFileAsync("Empty", "empty.bin",
                    "application/octet-stream", Array.Empty<byte>()),
                "Zero-length linked file was incorrectly staged.");

            var file = await drafts.StageSmallFileAsync(
                "Backup file", "backup.bin", "application/octet-stream", bytes);
            Require((await drafts.ListAsync()).Single(x => x.MutationId == file.MutationId)
                        .IsFile,
                "Encrypted linked file was not identified as a typed single-record draft.");
            var entries = await store.ListAsync(Root, network.CredentialKey(Root));
            try
            {
                var raw = entries.Single(x => x.Id == file.MutationId);
                var draft = System.Text.Json.JsonSerializer.Deserialize<LedgerRecordDraft>(
                    raw.EncryptedPayload)!;
                var metadata = System.Text.Json.JsonSerializer.Deserialize<
                    StellarLedgerFileProtocol.Single>(
                    draft.Content[StellarLedgerFileProtocol.SinglePrefix.Length..])!;
                var missingChecksum = draft with
                {
                    Content = StellarLedgerFileProtocol.SinglePrefix +
                        System.Text.Json.JsonSerializer.Serialize(metadata with { Sha256Hex = null })
                };
                Require(!VaultLinkedSingleFilePolicy.IsSupportedDraft(missingChecksum),
                    "A legacy unchecksummed SVFILE1 envelope bypassed typed S-key file policy.");
                Require(VaultLinkedSingleFilePolicy.IsSupportedDraft(draft) &&
                    VaultQueuedCreatePolicy.Problems(raw, entries).Contains(
                        "file-protocol-needs-staged-review") &&
                    VaultLinkedSingleFilePolicy.Problems(raw, entries).Length == 0,
                    "Linked file policy incorrectly allowed generic file creation or rejected a typed staged file.");
                var stored = new LedgerRecordItem(raw.EntityId, draft.Title,
                    draft.Content, draft.FolderId, draft.Tags);
                var decoded = StellarLedgerFileProtocol.DecodeFile(stored, [stored]);
                try { Require(decoded.SequenceEqual(bytes),
                        "Encrypted staged linked file did not preserve binary contents."); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(decoded); }
            }
            finally
            {
                foreach (var entry in entries)
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(entry.EncryptedPayload);
            }
            var review = await publisher.PrepareAsync(file.MutationId);
            Require(review.Quote.Kind == "create",
                "Single-record S-key file used a different or unreviewed signer protocol.");
            var hash = await publisher.SubmitAsync(review.Quote, true);
            var onChain = await ledger.ReadAsync();
            var actual = onChain.Single(x => x.Id == file.EntityId);
            var reconstructed = StellarLedgerFileProtocol.DecodeFile(actual, onChain);
            try { Require(reconstructed.SequenceEqual(bytes),
                    "Published S-key SVFILE1 record did not round-trip authenticated bytes."); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(reconstructed); }
            Require((await drafts.ListAsync()).Single(x => x.MutationId == file.MutationId)
                    .State == VaultSyncState.Uncertain,
                "A freshly uploaded S-key file was confirmed without independent history proof.");
            var witness = new CapturingLinkedReceiptEvidence { AllowRecordOperations = true };
            var reviewer = new VaultLinkedDraftRecoveryReview(
                ledger, store, keys, consent, network, witness);
            var reconciler = new VaultLinkedVerifiedReconciliation(
                ledger, store, keys, consent, network, reviewer);
            ledger.AllowHistoricalProof = true;
            var proven = await reconciler.ConfirmVerifiedRecordAsync(file.MutationId);
            Require(proven.Confirmed && hash == ledger.ReleasedVerifiedHash &&
                    (await drafts.ListAsync()).Single(x => x.MutationId == file.MutationId)
                        .State == VaultSyncState.Confirmed &&
                    ledger.Submissions == 1,
                "Signed-history verified single-record file was not durably finalized.");
            // A file's original encrypted snapshot is still SVFILE1 when it
            // becomes a pending deletion. Keep it typed and reviewable rather
            // than classifying the valid deletion journal as a corrupt draft.
            var originalRevision = await ledger.ReadRecordRevisionAsync(file.EntityId);
            await MustRejectAsync(
                () => drafts.StageUpdateAsync(file.EntityId,
                    new LedgerRecordDraft(actual.Title, actual.Content,
                        actual.FolderId, actual.Tags, actual.IsFolder,
                        actual.UserName, actual.Url, actual.Notes),
                    originalRevision!),
                "A generic record update bypassed the separate typed S-key file protocol.");
            Require(VaultLinkedRemoteRevisionPolicy.IsValid(originalRevision),
                "Published single-file record had no canonical remote revision.");
            var deletion = await drafts.StageDeleteAsync(file.EntityId, originalRevision!);
            var deletionEntry = (await drafts.ListAsync())
                .Single(x => x.MutationId == deletion.MutationId);
            Require(!deletionEntry.IsCorrupt && deletionEntry.IsFile &&
                    deletionEntry.Kind == VaultMutationKind.DeleteRecord &&
                    deletionEntry.State == VaultSyncState.Pending,
                "Queued SVFILE1 deletion was misclassified as corrupt or editable.");
            await MustRejectAsync(
                () => drafts.OpenDraftAsync(deletion.MutationId),
                "An immutable linked file deletion was reopened for editing.");
            var deleteReview = await publisher.PrepareAsync(deletion.MutationId);
            Require(deleteReview.Quote.Kind == "delete" && ledger.Submissions == 1,
                "S-key file deletion was not separately fee reviewed.");
            var deleteHash = await publisher.SubmitAsync(deleteReview.Quote, true);
            Require(deleteHash != hash &&
                    (await ledger.ReadAsync()).All(x => x.Id != file.EntityId) &&
                    (await drafts.ListAsync()).Single(x => x.MutationId == deletion.MutationId)
                        .State == VaultSyncState.Uncertain,
                "Submitted file deletion did not remain quarantined.");
            var confirmedDeletion =
                await reconciler.ConfirmVerifiedRecordAsync(deletion.MutationId);
            Require(confirmedDeletion.Confirmed &&
                    (await drafts.ListAsync()).Single(x => x.MutationId == deletion.MutationId)
                        .State == VaultSyncState.Confirmed &&
                    ledger.Submissions == 2 &&
                    ledger.ReleasedVerifiedHash == deleteHash,
                "Historically verified file deletion did not finalize without replay.");
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            Console.WriteLine("PASS: S-key SVFILE1 create/delete retain typed encrypted queues and signed-history reconciliation");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RunVerifiedCreateReconciliationSmokeAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "sv-linked-verified-create-" + Guid.NewGuid().ToString("N"));
        try
        {
            var network = new VaultNetworkSelection();
            var consent = new VaultLinkedLocalRetentionGate();
            consent.GrantExplicitConsent();
            var ledger = new FakeLinkedLedger();
            var credentials = new FileSystemVaultLinkedPassphraseCredentialStore(
                Path.Combine(directory, "credentials"), network, consent);
            await using var keys = new VaultLinkedQueueKeySession(
                ledger, network, consent, credentials, new NativeVaultKeyProtector());
            await keys.CreateAsync("linked verified create test passphrase");
            var store = new FileSystemVaultLinkedQueueSnapshotStore(
                keys, consent, Path.Combine(directory, "queue"), network);
            var drafts = new VaultLinkedDraftWorkspace(ledger, keys, store, consent, network);
            var publisher = new VaultLinkedDraftPublication(ledger, store, keys, consent, network);
            var first = await drafts.CreateAsync(
                new LedgerRecordDraft("First credential", "fake private field"));
            var second = await drafts.CreateAsync(
                new LedgerRecordDraft("Next credential", "synthetic only"));
            var quote = await publisher.PrepareAsync(first.MutationId);
            var hash = await publisher.SubmitAsync(quote.Quote, true);
            Require((await drafts.ReviewManualSyncAsync()).CanReviewNext == false,
                "An unproven linked create did not block dependent manual publication.");
            var witness = new CapturingLinkedReceiptEvidence();
            var reviewer = new VaultLinkedDraftRecoveryReview(
                ledger, store, keys, consent, network, witness);
            var reconciler = new VaultLinkedVerifiedReconciliation(
                ledger, store, keys, consent, network, reviewer);
            var missingReceiptOperations =
                await reconciler.ConfirmVerifiedRecordAsync(first.MutationId);
            Require(!missingReceiptOperations.Confirmed,
                "A receipt without independently matching historical ciphertext was accepted.");
            witness.AllowRecordOperations = true;
            var unsigned = await reconciler.ConfirmVerifiedRecordAsync(first.MutationId);
            Require(!unsigned.Confirmed,
                "Matching ciphertext without an independently validated signer was accepted.");
            ledger.AllowHistoricalProof = true;
            var proven = await reconciler.ConfirmVerifiedRecordAsync(first.MutationId);
            Require(proven.Confirmed &&
                    (await drafts.ListAsync()).Single(x => x.MutationId == first.MutationId)
                        .State == VaultSyncState.Confirmed &&
                    ledger.Submissions == 1 &&
                    ledger.ReleasedVerifiedHash == hash,
                "Verified linked creation was not durably confirmed without duplicate broadcast.");
            Require((await drafts.ReviewManualSyncAsync()).NextReadyMutationId ==
                    second.MutationId,
                "Independent historical proof did not unblock the next queued draft for fresh review.");
            var nextReview = await publisher.PrepareAsync(second.MutationId);
            Require(nextReview.MutationId == second.MutationId && ledger.Submissions == 1,
                "Next encrypted draft was not reviewable without automatic publication.");
            await publisher.CancelAsync();
            Console.WriteLine("PASS: linked create requires operation, signature and ciphertext proof before unblocking the next manual draft");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RunRemoteMutationSmokeAsync(bool deleting)
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "sv-linked-remote-edit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var network = new VaultNetworkSelection();
            var consent = new VaultLinkedLocalRetentionGate();
            consent.GrantExplicitConsent();
            var ledger = new FakeLinkedLedger();
            var credentials = new FileSystemVaultLinkedPassphraseCredentialStore(
                Path.Combine(directory, "credentials"), network, consent);
            await using var keys = new VaultLinkedQueueKeySession(
                ledger, network, consent, credentials, new NativeVaultKeyProtector());
            await keys.CreateAsync("linked remote revision passphrase");
            var store = new FileSystemVaultLinkedQueueSnapshotStore(
                keys, consent, Path.Combine(directory, "queue"), network);
            var drafts = new VaultLinkedDraftWorkspace(
                ledger, keys, store, consent, network);
            var publisher = new VaultLinkedDraftPublication(
                ledger, store, keys, consent, network);

            const string id = "0123456789abcdef";
            var original = new LedgerRecordDraft(
                "Existing", "original secret", null, ["safe"], false);
            var updated = original with { Title = "Updated", Content = "new secret" };
            ledger.SeedRecord(id, original);
            var staleRevision = (await ledger.ReadRecordRevisionAsync(id))!;
            Require(VaultLinkedRemoteRevisionPolicy.IsValid(staleRevision),
                "A valid v1 manifest revision was rejected.");
            var corruptManifest = Convert.FromBase64String(staleRevision);
            corruptManifest[0] = 2;
            Require(!VaultLinkedRemoteRevisionPolicy.IsValid(
                    Convert.ToBase64String(corruptManifest)) &&
                    !VaultLinkedRemoteRevisionPolicy.IsValid("invalid"),
                "Malformed or non-v1 manifest revision was accepted.");
            var staged = deleting
                ? await drafts.StageDeleteAsync(id, staleRevision)
                : await drafts.StageUpdateAsync(id, updated, staleRevision);
            Require(staged.State == VaultSyncState.Pending &&
                    (await drafts.ReviewManualSyncAsync()).NextReadyMutationId == staged.MutationId,
                "Revision-bound linked update/delete was not offered as a separate manual review.");

            ledger.BumpRemoteRevision(id);
            await MustRejectAsync(() => publisher.PrepareAsync(staged.MutationId),
                "A changed published record manifest was permitted to overwrite stale queue state.");
            Require(ledger.Submissions == 0,
                "Stale linked remote mutation was sent before an approved quote.");
            await drafts.DiscardAsync(staged.MutationId);
            var revision = (await ledger.ReadRecordRevisionAsync(id))!;
            staged = deleting
                ? await drafts.StageDeleteAsync(id, revision)
                : await drafts.StageUpdateAsync(id, updated, revision);

            var review = await publisher.PrepareAsync(staged.MutationId);
            Require(review.Quote.Kind == (deleting ? "delete" : "update"),
                "Wrong S-key ledger transaction kind was quoted for revision-pinned mutation.");
            ledger.BumpRemoteRevision(id);
            await MustRejectAsync(() => publisher.SubmitAsync(review.Quote, true),
                "Remote manifest changed after the approved quote but before durable reservation.");
            Require(ledger.Submissions == 0 &&
                    (await drafts.ListAsync()).Single(x => x.MutationId == staged.MutationId)
                        .State == VaultSyncState.Pending,
                "Stale last-mile approval was broadcast or removed a still-pending draft.");
            await drafts.DiscardAsync(staged.MutationId);
            revision = (await ledger.ReadRecordRevisionAsync(id))!;
            staged = deleting
                ? await drafts.StageDeleteAsync(id, revision)
                : await drafts.StageUpdateAsync(id, updated, revision);
            review = await publisher.PrepareAsync(staged.MutationId);
            var transaction = await publisher.SubmitAsync(review.Quote, true);
            Require(transaction.Length == 64 && ledger.Submissions == 1 &&
                    (await drafts.ListAsync()).Single(x => x.MutationId == staged.MutationId)
                        .State == VaultSyncState.Uncertain,
                "Published linked update/delete did not stay durably quarantined.");
            var remote = await ledger.ReadAsync();
            Require(deleting ? remote.All(x => x.Id != id) :
                    remote.Any(x => x.Id == id && x.Title == updated.Title &&
                        x.Content == updated.Content),
                "The fake linked ledger did not apply the approved update/delete.");
            await MustRejectAsync(() => publisher.PrepareAsync(staged.MutationId),
                "A submitted linked remote mutation was offered for duplicate publication.");

            var evidence = new CapturingLinkedReceiptEvidence();
            var recovery = new VaultLinkedDraftRecoveryReview(
                ledger, store, keys, consent, network, evidence);
            var reconciler = new VaultLinkedVerifiedReconciliation(
                ledger, store, keys, consent, network, recovery);
            var unresolved = await reconciler.ConfirmVerifiedRecordAsync(staged.MutationId);
            Require(!unresolved.Confirmed &&
                    (await drafts.ListAsync()).Single(x => x.MutationId == staged.MutationId)
                        .State == VaultSyncState.Uncertain,
                "Unproven historical S-key update/delete was released.");
            ledger.AllowHistoricalProof = true;
            var reconciled = await reconciler.ConfirmVerifiedRecordAsync(staged.MutationId);
            Require(reconciled.Confirmed &&
                    (await drafts.ListAsync()).Single(x => x.MutationId == staged.MutationId)
                        .State == VaultSyncState.Confirmed &&
                    ledger.Submissions == 1 &&
                    ledger.ReleasedVerifiedHash == transaction,
                "Proof-bound linked mutation finalization did not atomically preserve its signed identity.");
            Console.WriteLine(deleting
                ? "PASS: revision-pinned S-key deletion remains uncertain without historical proof"
                : "PASS: revision-pinned S-key update rejects stale manifest and remains uncertain");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RunPrebroadcastCheckpointFailureSmokeAsync()
    {
        var ledger = new FakeLinkedLedger();
        var quote = await ledger.QuoteExtendChainAsync();
        await MustRejectAsync(
            () => ledger.SubmitWithSignedHashAsync(quote.QuoteId,
                _ => Task.FromException(new IOException("Durable CAS checkpoint failed."))),
            "Checkpoint refusal unexpectedly returned successful publication.");
        Require(ledger.Submissions == 0,
            "The simulated network was contacted despite a failed durable hash checkpoint.");
        Console.WriteLine("PASS: linked network transport refuses to broadcast before durable checkpoint");
    }

    private static async Task RunSuccessfulExpansionQuarantineSmokeAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "sv-linked-expansion-success-" + Guid.NewGuid().ToString("N"));
        try
        {
            var network = new VaultNetworkSelection();
            var consent = new VaultLinkedLocalRetentionGate();
            consent.GrantExplicitConsent();
            var ledger = new FakeLinkedLedger();
            var credentials = new FileSystemVaultLinkedPassphraseCredentialStore(
                Path.Combine(directory, "credentials"), network, consent);
            await using var keys = new VaultLinkedQueueKeySession(
                ledger, network, consent, credentials, new NativeVaultKeyProtector());
            const string passphrase = "linked expansion success passphrase";
            await keys.CreateAsync(passphrase);
            var queuePath = Path.Combine(directory, "queue");
            var store = new FileSystemVaultLinkedQueueSnapshotStore(
                keys, consent, queuePath, network);
            var drafts = new VaultLinkedDraftWorkspace(
                ledger, keys, store, consent, network);
            var expansion = new VaultLinkedAccountExpansion(ledger, network,
                new VaultAccountStrategyFactory([new FakeLinkedAccountStrategy(ledger)]));
            var journal = new VaultLinkedExpansionJournal(
                expansion, ledger, store, keys, consent, network);

            var approved = await journal.PrepareAsync();
            Require(ledger.Submissions == 0, "Preparing linked expansion caused a broadcast.");
            var hash = await journal.SubmitApprovedAsync(approved, true);
            var entries = await drafts.ListAsync();
            Require(hash.Length == 64 && ledger.AccountCount == 2 &&
                    entries.Count(x => x.IsJournal &&
                        x.State == VaultSyncState.Uncertain) == 1,
                "Successful linked expansion was confirmed without historical signer proof.");
            await MustRejectAsync(() => journal.PrepareAsync(),
                "Snapshot-visible expansion bypassed the durable quarantine.");

            await keys.LockAsync();
            ledger.Reconnect(); // The volatile JS diagnostic is absent after a new session.
            await keys.UnlockAsync(passphrase);
            Require(await ledger.InspectUncertainSubmissionAsync() is null,
                "Fake transport retained volatile diagnostic across session restart.");
            var reopenedStore = new FileSystemVaultLinkedQueueSnapshotStore(
                keys, consent, queuePath, network);
            var durable = await reopenedStore.ListAsync(Root, network.CredentialKey(Root));
            try
            {
                Require(durable.Any(x => x.Kind == VaultMutationKind.ExtendLinkedChain &&
                    x.State == VaultSyncState.Uncertain && x.TransactionHash == hash),
                    "A successfully submitted expansion lost its quarantine after reopening.");
            }
            finally
            {
                foreach (var item in durable)
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(item.EncryptedPayload);
            }
            var witness = new CapturingLinkedReceiptEvidence();
            var reviewer = new VaultLinkedDraftRecoveryReview(
                ledger, reopenedStore, keys, consent, network, witness);
            var journalId = (await drafts.ListAsync()).Single(x => x.IsJournal).MutationId;
            var recovery = await reviewer.InspectAsync(journalId);
            Require(recovery.HistoricalReceiptObserved &&
                    recovery.DurableState == VaultSyncState.Uncertain &&
                    witness.Observed?.TransactionHash == hash &&
                    witness.Observed?.NetworkPassphrase == Testnet &&
                    witness.Observed?.RootAccountId == Root &&
                    witness.Observed?.TargetAccountId == FakeLinkedLedger.ExpansionTarget,
                "Restart recovery did not inspect the exact durable hash on the selected network.");
            await MustRejectAsync(() => journal.PrepareAsync(),
                "A mocked observed receipt wrongly released an unproven linked expansion.");
            var reconciler = new VaultLinkedVerifiedReconciliation(
                ledger, reopenedStore, keys, consent, network, reviewer);
            var first = await reconciler.ConfirmVerifiedExpansionAsync(journalId);
            Require(!first.Confirmed,
                "An advisory receipt alone released the unproven linked account expansion.");
            witness.AllowExpansionOperations = true;
            var second = await reconciler.ConfirmVerifiedExpansionAsync(journalId);
            Require(!second.Confirmed,
                "A valid operation trace without root and child signatures released expansion.");
            ledger.AllowHistoricalProof = true;
            var proven = await reconciler.ConfirmVerifiedExpansionAsync(journalId);
            Require(proven.Confirmed &&
                    (await drafts.ListAsync()).Single(x => x.IsJournal).State ==
                        VaultSyncState.Confirmed &&
                    ledger.Submissions == 1 &&
                    ledger.ReleasedVerifiedHash == hash,
                "Cryptographically verified linked expansion failed to finalize a single durable journal.");
            Console.WriteLine("PASS: successful linked expansion remains unconfirmed pending historical proof");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RunUncertainExpansionSmokeAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "sv-linked-expansion-journal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var network = new VaultNetworkSelection();
            var consent = new VaultLinkedLocalRetentionGate();
            consent.GrantExplicitConsent();
            var ledger = new FakeLinkedLedger();
            var credentials = new FileSystemVaultLinkedPassphraseCredentialStore(
                Path.Combine(directory, "credentials"), network, consent);
            await using var keys = new VaultLinkedQueueKeySession(
                ledger, network, consent, credentials, new NativeVaultKeyProtector());
            const string passphrase = "independent expansion journal passphrase";
            await keys.CreateAsync(passphrase);
            var queuePath = Path.Combine(directory, "encrypted-queue");
            var store = new FileSystemVaultLinkedQueueSnapshotStore(
                keys, consent, queuePath, network);
            var expansion = new VaultLinkedAccountExpansion(ledger, network,
                new VaultAccountStrategyFactory([new FakeLinkedAccountStrategy(ledger)]));
            var journal = new VaultLinkedExpansionJournal(
                expansion, ledger, store, keys, consent, network);

            var abandonedPayload = System.Text.Encoding.UTF8.GetBytes("unreserved intent");
            var abandoned = VaultPendingMutation.Create(Root,
                VaultLinkedQueueStorageScope.FromNetworkScope(network.CredentialKey(Root)),
                VaultMutationKind.ExtendLinkedChain, FakeLinkedLedger.ExpansionTarget,
                abandonedPayload) with
            {
                OriginStrategyId = VaultMutationStrategyPolicy.Linked
            };
            try
            {
                Require(await store.AddAsync(abandoned),
                    "Unable to stage the simulated never-reserved expansion journal.");
                await journal.DiscardNeverSubmittedIntentAsync(abandoned.Id);
                Require((await store.ListAsync(Root, network.CredentialKey(Root))).Count == 0 &&
                        ledger.Submissions == 0,
                    "Never-reserved expansion journal cleanup broadcast or left a durable blocker.");
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(abandonedPayload);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(abandoned.EncryptedPayload);
            }

            var approved = await journal.PrepareAsync();
            ledger.FailNextSubmission = true;
            await MustRejectAsync(
                () => journal.SubmitApprovedAsync(approved, true),
                "Unknown linked expansion outcome incorrectly returned success.");
            Require(ledger.Submissions == 1,
                "Unknown expansion outcome was automatically retried.");

            var signedHash = await VaultLinkedSignedSubmissionIdentity.TryReadAsync(
                ledger, Root, "expand", FakeLinkedLedger.ExpansionTarget,
                null, Testnet, ledger.ConnectionEpoch);
            Require(signedHash == 1.ToString("x64"),
                "Unknown submission lost its signed transaction search identifier.");
            Require(await VaultLinkedSignedSubmissionIdentity.TryReadAsync(
                ledger, Root, "expand", FakeLinkedLedger.ExpansionTarget,
                null, "wrong network", ledger.ConnectionEpoch) is null &&
                await VaultLinkedSignedSubmissionIdentity.TryReadAsync(
                ledger, Root, "create", Root, null,
                Testnet, ledger.ConnectionEpoch) is null &&
                await VaultLinkedSignedSubmissionIdentity.TryReadAsync(
                ledger, Root, "expand", FakeLinkedLedger.ExpansionTarget,
                null, Testnet, ledger.ConnectionEpoch + 1) is null,
                "Signed transaction identity crossed a network, operation or session boundary.");

            var reopenedStore = new FileSystemVaultLinkedQueueSnapshotStore(
                keys, consent, queuePath, network);
            var durable = await reopenedStore.ListAsync(Root, network.CredentialKey(Root));
            Require(durable.Count(x => x.Kind == VaultMutationKind.ExtendLinkedChain &&
                (x.State is VaultSyncState.Uncertain or VaultSyncState.Submitting) &&
                x.TransactionHash == 1.ToString("x64")) == 1,
                "Unknown account expansion was not preserved with its exact signed hash in encrypted queue.");
            Require(!VaultLinkedManualSyncPlan.Build(durable, true).CanReviewNext,
                "Unresolved expansion failed to block manual record publication.");
            foreach (var entry in durable)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(entry.EncryptedPayload);

            await keys.LockAsync();
            await keys.UnlockAsync(passphrase);
            await MustRejectAsync(
                () => journal.PrepareAsync(),
                "Restarted signer and encrypted queue permitted another account expansion despite an unresolved journal.");
            Require(ledger.Submissions == 1,
                "Linked account expansion journal caused duplicate network transmission.");
            Console.WriteLine("PASS: durable encrypted expansion journal blocks unsafe retry after reconnect");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static async Task MustRejectAsync(Func<Task> operation, string message)
    {
        try { await operation(); }
        catch (Exception) { return; }
        throw new Exception(message);
    }

    private sealed class CapturingLinkedReceiptEvidence : IVaultLinkedReceiptEvidence
    {
        public VaultSecretSubmissionDiagnostic? Observed { get; private set; }
        public bool AllowExpansionOperations { get; set; }
        public bool AllowRecordOperations { get; set; }
        public Task<VaultLinkedReceiptObservation> InspectAsync(
            VaultSecretSubmissionDiagnostic diagnostic,
            CancellationToken ct = default)
        {
            Observed = diagnostic;
            return Task.FromResult(new VaultLinkedReceiptObservation(
                true, "Mocked receipt; historical proof must still come from the signer.",
                HistoricalRecordOperationsMatch: AllowRecordOperations &&
                    diagnostic.Kind == "create",
                StorageAccount: AllowRecordOperations &&
                    diagnostic.Kind == "create" ? Root : null,
                HistoricalExpansionOperationsMatch: AllowExpansionOperations &&
                    diagnostic.Kind == "expand",
                HistoricalParentAccount: AllowExpansionOperations &&
                    diagnostic.Kind == "expand" ? Root : null));
        }
    }

    private sealed class FakeLinkedAccountStrategy(FakeLinkedLedger ledger)
        : IVaultAccountProvisioningStrategy
    {
        public string BlockchainId => ConnectedStellarAccountStrategy.StellarChain;
        public string StrategyId => ConnectedStellarAccountStrategy.LinkedMode;

        public Task<VaultAccountHandle> CreateNextAsync(
            VaultAccountRequest request, CancellationToken cancellationToken = default) =>
            Task.FromException<VaultAccountHandle>(
                new NotSupportedException("Fake account provisioning never signs."));

        public Task<VaultAccountInspection> InspectConnectedAsync(
            VaultAccountRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VaultAccountInspection(
                BlockchainId, request.NetworkId, request.VaultId, StrategyId,
                FakeLinkedLedger.RootAddress, ledger.Tail, ledger.AccountCount));

        public Task<IReadOnlyList<VaultAccountHandle>> DiscoverAsync(
            VaultAccountRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VaultAccountHandle>>([]);
    }

    private sealed class FakeLinkedLedger :
        IStellarSecretKeyLedger, IStellarSecretKeySignedSubmission,
        IStellarSecretKeyRecordRevisionReader,
        IStellarSecretKeyHistoricalRecordVerifier,
        IStellarSecretKeyHistoricalExpansionVerifier,
        IStellarSecretKeyVerifiedSessionRelease
    {
        public const string RootAddress = Root;
        private readonly List<LedgerRecordItem> _items = [];
        private readonly Dictionary<string, int> _revisions = new(StringComparer.Ordinal);
        private string? _quotedId;
        private LedgerRecordDraft? _quotedDraft;
        private string? _quoteId;
        private string? _quoteKind;
        private VaultSecretSubmissionDiagnostic? _signedSubmission;
        public string Tail { get; private set; } = Root;
        public int AccountCount { get; private set; } = 1;
        public const string ExpansionTarget = "GAAQCAIBAEAQCAIBAEAQCAIBAEAQCAIBAEAQCAIBAEAQCAIBAEAQDZ7H";

        public bool IsConnected { get; private set; } = true;
        public string? RootAccountId => IsConnected ? Root : null;
        public string? ConnectedNetworkPassphrase => IsConnected ? Testnet : null;
        public long ConnectionEpoch { get; private set; } = 1;
        public int Submissions { get; private set; }
        public bool FailNextSubmission { get; set; }
        public bool AllowHistoricalProof { get; set; }
        public string? ReleasedVerifiedHash { get; private set; }

        public Task<bool> VerifyAsync(string transactionHash, VaultMutationKind kind,
            string recordId, LedgerRecordDraft expectedDraft,
            string? originalManifest, CancellationToken ct = default)
        {
            var valid = AllowHistoricalProof &&
                transactionHash == Submissions.ToString("x64") &&
                (kind is VaultMutationKind.CreateRecord or VaultMutationKind.CreateFolder
                    ? originalManifest is null
                    : VaultLinkedRemoteRevisionPolicy.IsValid(originalManifest)) &&
                (kind is VaultMutationKind.DeleteRecord or VaultMutationKind.DeleteFolder
                    ? _items.All(x => x.Id != recordId)
                    : _items.Any(x => x.Id == recordId &&
                        x.Title == expectedDraft.Title &&
                        x.Content == expectedDraft.Content));
            return Task.FromResult(valid);
        }

        public Task<bool> VerifyExpansionAsync(string transactionHash,
            string childAccountId, CancellationToken ct = default) =>
            Task.FromResult(AllowHistoricalProof &&
                transactionHash == Submissions.ToString("x64") &&
                childAccountId == ExpansionTarget && Tail == childAccountId);

        public Task ReleaseVerifiedAsync(string transactionHash, CancellationToken ct = default)
        {
            ReleasedVerifiedHash = transactionHash;
            return Task.CompletedTask;
        }

        public Task ConnectAsync(string secretKey, string horizonUrl, string networkPassphrase,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException("Fake session is already connected."));

        public Task<IReadOnlyList<LedgerRecordItem>> ReadAsync(
            CancellationToken cancellationToken = default) =>
            IsConnected
                ? Task.FromResult<IReadOnlyList<LedgerRecordItem>>(_items.ToArray())
                : Task.FromException<IReadOnlyList<LedgerRecordItem>>(
                    new IOException("Horizon unavailable: offline test must not read Stellar."));

        public void SeedRecord(string recordId, LedgerRecordDraft draft)
        {
            _items.Add(new LedgerRecordItem(recordId, draft.Title, draft.Content,
                draft.FolderId, draft.Tags, draft.IsFolder, draft.UserName, draft.Url, draft.Notes));
            _revisions[recordId] = 1;
        }

        public void BumpRemoteRevision(string recordId)
        {
            if (_revisions.TryGetValue(recordId, out var current))
                _revisions[recordId] = current + 1;
        }

        public Task<string?> ReadRecordRevisionAsync(string recordId,
            CancellationToken ct = default)
        {
            if (!_revisions.TryGetValue(recordId, out var generation))
                return Task.FromResult<string?>(null);
            var manifest = new byte[47];
            manifest[0] = 1;
            System.Text.Encoding.ASCII.GetBytes(generation.ToString("x8"))
                .CopyTo(manifest, 1);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(
                manifest.AsSpan(9, 2), 1);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
                manifest.AsSpan(11, 4), 32);
            System.Security.Cryptography.SHA256.HashData(
                BitConverter.GetBytes(generation)).CopyTo(manifest, 15);
            return Task.FromResult<string?>(Convert.ToBase64String(manifest));
        }

        public Task<LedgerAccountInfo> InspectAccountInfoAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LedgerAccountInfo(Root, Tail, AccountCount,
                _items.Count(x => !x.IsFolder), _items.Count(x => x.IsFolder),
                "500.0000000", "495.0000000", "0.0000000"));

        public Task<VaultSecretLedgerQuote> QuoteCreateAsync(string recordId, LedgerRecordDraft draft,
            CancellationToken cancellationToken = default)
        {
            _quotedId = recordId;
            _quotedDraft = draft;
            _quoteKind = "create";
            _quoteId = Guid.NewGuid().ToString("N");
            return Task.FromResult(new VaultSecretLedgerQuote(
                _quoteId, "create", 2, 1,
                "0.0000200", "0.5000000", "0.5000000",
                false, Root, "0.0000000"));
        }

        public Task<VaultSecretLedgerQuote> QuoteUpdateAsync(string recordId,
            LedgerRecordDraft draft, CancellationToken cancellationToken = default)
        {
            _quotedId = recordId;
            _quotedDraft = draft;
            _quoteKind = "update";
            _quoteId = Guid.NewGuid().ToString("N");
            return Task.FromResult(new VaultSecretLedgerQuote(
                _quoteId, "update", 3, 1, "0.0000300", "0.5000000", "0.0000000",
                false, Root, "0.0000000"));
        }
        public Task<VaultSecretLedgerQuote> QuoteDeleteAsync(string recordId,
            CancellationToken cancellationToken = default)
        {
            _quotedId = recordId;
            _quotedDraft = null;
            _quoteKind = "delete";
            _quoteId = Guid.NewGuid().ToString("N");
            return Task.FromResult(new VaultSecretLedgerQuote(
                _quoteId, "delete", 2, 1, "0.0000200", "0.0000000", "-0.5000000",
                false, Root, "0.0000000"));
        }
        public Task<VaultSecretLedgerQuote> QuoteExtendChainAsync(
            CancellationToken cancellationToken = default)
        {
            _quoteKind = "expand";
            _quoteId = Guid.NewGuid().ToString("N");
            return Task.FromResult(new VaultSecretLedgerQuote(
                _quoteId, "expand", 4, 1, "0.0000400", "1.5000000",
                "1.5000000", true, ExpansionTarget, "2.0000000"));
        }
        public Task<VaultSecretSubmissionDiagnostic?> InspectUncertainSubmissionAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_signedSubmission);
        public Task CancelQuoteAsync(string quoteId)
        {
            if (_quoteId == quoteId) _quoteId = null;
            return Task.CompletedTask;
        }

        public async Task<string> SubmitWithSignedHashAsync(
            string quoteId, Func<string, Task> durableCheckpoint,
            CancellationToken ct = default)
        {
            if (_quoteId != quoteId)
                throw new InvalidOperationException("No matching fake signed quote.");
            // Mirrors JS: the exact network-bound hash is available before
            // broadcast and the callback must persist it synchronously.
            await durableCheckpoint((Submissions + 1).ToString("x64"));
            return await SubmitApprovedAsync(quoteId, ct);
        }

        public Task<string> SubmitApprovedAsync(string quoteId,
            CancellationToken cancellationToken = default)
        {
            if (_quoteId != quoteId ||
                (_quoteKind == "create" && (_quotedId is null || _quotedDraft is null)))
                throw new InvalidOperationException("Quote not prepared for exact operation.");
            Submissions++;
            _quoteId = null;
            _signedSubmission = new VaultSecretSubmissionDiagnostic(
                _quoteKind!, Root,
                _quoteKind == "expand" ? ExpansionTarget : Root,
                _quoteKind == "expand" ? null : _quotedId,
                Submissions.ToString("x64"), Testnet,
                "https://horizon-testnet.stellar.org");
            if (FailNextSubmission)
            {
                FailNextSubmission = false;
                throw new IOException("Simulated unknown Stellar transmission result.");
            }
            if (_quoteKind == "expand")
            {
                Tail = ExpansionTarget;
                AccountCount++;
                return Task.FromResult(Submissions.ToString("x64"));
            }
            if (_quoteKind == "delete")
            {
                _items.RemoveAll(x => x.Id == _quotedId);
                _revisions.Remove(_quotedId!);
                return Task.FromResult(Submissions.ToString("x64"));
            }
            if (_quoteKind == "update")
                _items.RemoveAll(x => x.Id == _quotedId);
            var draft = _quotedDraft!;
            _items.Add(new LedgerRecordItem(
                _quotedId, draft.Title, draft.Content, draft.FolderId,
                draft.Tags, draft.IsFolder, draft.UserName, draft.Url, draft.Notes));
            _revisions[_quotedId!] = _revisions.GetValueOrDefault(_quotedId!) + 1;
            return Task.FromResult(Submissions.ToString("x64"));
        }

        public void Reconnect()
        {
            IsConnected = true;
            ConnectionEpoch++;
            _signedSubmission = null;
        }

        public Task DisconnectAsync()
        {
            IsConnected = false;
            ConnectionEpoch++;
            return Task.CompletedTask;
        }
    }
}
