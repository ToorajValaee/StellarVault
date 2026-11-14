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
        await RunSuccessfulExpansionQuarantineSmokeAsync();
        await RunUncertainExpansionSmokeAsync();
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
            await keys.UnlockAsync(passphrase);
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
            await MustRejectAsync(() => journal.PrepareAsync(),
                "A reopened linked expansion was offered for unsafe retry.");
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

            var reopenedStore = new FileSystemVaultLinkedQueueSnapshotStore(
                keys, consent, queuePath, network);
            var durable = await reopenedStore.ListAsync(Root, network.CredentialKey(Root));
            Require(durable.Count(x => x.Kind == VaultMutationKind.ExtendLinkedChain &&
                (x.State is VaultSyncState.Uncertain or VaultSyncState.Submitting)) == 1,
                "Unknown account expansion was not preserved in encrypted queue.");
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

    private sealed class FakeLinkedLedger : IStellarSecretKeyLedger
    {
        public const string RootAddress = Root;
        private readonly List<LedgerRecordItem> _items = [];
        private string? _quotedId;
        private LedgerRecordDraft? _quotedDraft;
        private string? _quoteId;
        private string? _quoteKind;
        public string Tail { get; private set; } = Root;
        public int AccountCount { get; private set; } = 1;
        public const string ExpansionTarget = "GAAQCAIBAEAQCAIBAEAQCAIBAEAQCAIBAEAQCAIBAEAQCAIBAEAQDZ7H";

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
            LedgerRecordDraft draft, CancellationToken cancellationToken = default) =>
            Task.FromException<VaultSecretLedgerQuote>(new NotSupportedException());
        public Task<VaultSecretLedgerQuote> QuoteDeleteAsync(string recordId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<VaultSecretLedgerQuote>(new NotSupportedException());
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
            Task.FromResult<VaultSecretSubmissionDiagnostic?>(null);
        public Task CancelQuoteAsync(string quoteId)
        {
            if (_quoteId == quoteId) _quoteId = null;
            return Task.CompletedTask;
        }

        public Task<string> SubmitApprovedAsync(string quoteId,
            CancellationToken cancellationToken = default)
        {
            if (_quoteId != quoteId ||
                (_quoteKind == "create" && (_quotedId is null || _quotedDraft is null)))
                throw new InvalidOperationException("Quote not prepared for exact operation.");
            Submissions++;
            _quoteId = null;
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
            var draft = _quotedDraft!;
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
