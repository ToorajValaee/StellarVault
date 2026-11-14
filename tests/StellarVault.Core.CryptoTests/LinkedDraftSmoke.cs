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
            await publisher.SubmitAsync(folderReview.Quote, true);
            Require(ledger.Submissions == 1 &&
                    (await drafts.ListAsync()).Single(x => x.MutationId == folder.MutationId)
                        .State == VaultSyncState.Confirmed,
                "Folder publication did not persist a confirmed CAS revision.");

            var recordReview = await publisher.PrepareAsync(child.MutationId);
            await publisher.SubmitAsync(recordReview.Quote, true);
            var after = await drafts.ListAsync();
            Require(after.All(x => x.State == VaultSyncState.Confirmed) &&
                    ledger.Submissions == 2 && (await ledger.ReadAsync()).Count == 2,
                "Linked child was not published after its confirmed parent.");

            var pending = await drafts.CreateAsync(
                new LedgerRecordDraft("Uncertain", "opaque", null, [], false));
            var uncertainReview = await publisher.PrepareAsync(pending.MutationId);
            ledger.FailNextSubmission = true;
            await MustRejectAsync(() => publisher.SubmitAsync(uncertainReview.Quote, true),
                "Uncertain network transmission returned as a confirmed success.");
            var uncertain = (await drafts.ListAsync())
                .Single(x => x.MutationId == pending.MutationId);
            Require(uncertain.State is VaultSyncState.Uncertain or VaultSyncState.Submitting,
                "Interrupted network transmission silently returned to Pending.");
            await MustRejectAsync(() => publisher.PrepareAsync(pending.MutationId),
                "An uncertain linked submission was offered for automatic retry.");

            var recovery = new VaultLinkedDraftRecoveryReview(ledger, store, keys,
                linkedRetention, network, new DenyVaultLinkedReceiptEvidence());
            var finding = await recovery.InspectAsync(pending.MutationId);
            Require(finding.DurableState is VaultSyncState.Uncertain or VaultSyncState.Submitting &&
                    finding.CurrentRecordMatches is false &&
                    !finding.HistoricalReceiptObserved,
                "Read-only recovery falsely confirmed an unresolved S-key submission.");
            var afterRecovery = (await drafts.ListAsync())
                .Single(x => x.MutationId == pending.MutationId);
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

    private sealed class FakeLinkedLedger : IStellarSecretKeyLedger
    {
        private readonly List<LedgerRecordItem> _items = [];
        private string? _quotedId;
        private LedgerRecordDraft? _quotedDraft;
        private string? _quoteId;

        public bool IsConnected { get; private set; } = true;
        public string? RootAccountId => IsConnected ? Root : null;
        public string? ConnectedNetworkPassphrase => IsConnected ? Testnet : null;
        public long ConnectionEpoch { get; private set; } = 1;
        public int Submissions { get; private set; }
        public bool FailNextSubmission { get; set; }

        public Task ConnectAsync(string secretKey, string horizonUrl, string networkPassphrase,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException("Fake session is already connected."));

        public Task<IReadOnlyList<LedgerRecordItem>> ReadAsync(
            CancellationToken cancellationToken = default) =>
            IsConnected
                ? Task.FromResult<IReadOnlyList<LedgerRecordItem>>(_items.ToArray())
                : Task.FromException<IReadOnlyList<LedgerRecordItem>>(
                    new IOException("Horizon unavailable: offline test must not read Stellar."));

        public Task<LedgerAccountInfo> InspectAccountInfoAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LedgerAccountInfo(Root, Root, 1,
                _items.Count(x => !x.IsFolder), _items.Count(x => x.IsFolder),
                "500.0000000", "495.0000000", "0.0000000"));

        public Task<VaultSecretLedgerQuote> QuoteCreateAsync(string recordId, LedgerRecordDraft draft,
            CancellationToken cancellationToken = default)
        {
            _quotedId = recordId;
            _quotedDraft = draft;
            _quoteId = Guid.NewGuid().ToString("N");
            return Task.FromResult(new VaultSecretLedgerQuote(
                _quoteId, "create", 2, 1,
                "0.0000200", "0.5000000", "0.5000000",
                false, Root, "0.0000000"));
        }

        public Task<VaultSecretLedgerQuote> QuoteUpdateAsync(string recordId,
            LedgerRecordDraft draft, CancellationToken cancellationToken = default) =>
            Task.FromException<VaultSecretLedgerQuote>(new NotSupportedException());
        public Task<VaultSecretLedgerQuote> QuoteDeleteAsync(string recordId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<VaultSecretLedgerQuote>(new NotSupportedException());
        public Task<VaultSecretLedgerQuote> QuoteExtendChainAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromException<VaultSecretLedgerQuote>(new NotSupportedException());
        public Task<VaultSecretSubmissionDiagnostic?> InspectUncertainSubmissionAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<VaultSecretSubmissionDiagnostic?>(null);
        public Task CancelQuoteAsync(string quoteId)
        {
            if (_quoteId == quoteId) _quoteId = null;
            return Task.CompletedTask;
        }

        public Task<string> SubmitApprovedAsync(string quoteId,
            CancellationToken cancellationToken = default)
        {
            if (_quoteId != quoteId || _quotedId is null || _quotedDraft is null)
                throw new InvalidOperationException("Quote not prepared for exact record.");
            Submissions++;
            _quoteId = null;
            if (FailNextSubmission)
            {
                FailNextSubmission = false;
                throw new IOException("Simulated unknown Stellar transmission result.");
            }
            var draft = _quotedDraft;
            _items.Add(new LedgerRecordItem(
                _quotedId, draft.Title, draft.Content, draft.FolderId,
                draft.Tags, draft.IsFolder, draft.UserName, draft.Url, draft.Notes));
            return Task.FromResult(Submissions.ToString("x64"));
        }

        public void Reconnect()
        {
            IsConnected = true;
            ConnectionEpoch++;
        }

        public Task DisconnectAsync()
        {
            IsConnected = false;
            ConnectionEpoch++;
            return Task.CompletedTask;
        }
    }
}
