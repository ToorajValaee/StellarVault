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
            await MustRejectAsync(() => drafts.DiscardAsync(folder.MutationId),
                "Deleting a pending parent with dependent records was permitted.");
            await MustRejectAsync(() => publisher.PrepareAsync(child.MutationId),
                "Pending child folder dependency was approved for publication.");

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

            await keys.LockAsync();
            Require(!keys.IsUnlocked, "Linked queue encryption key remained unlocked.");
            await MustRejectAsync(() => drafts.ListAsync(),
                "Linked encrypted queue was accessible after lock.");
            Console.WriteLine("PASS: linked encrypted folder/record drafts, dependencies, approval CAS and uncertain submission");
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
            Task.FromResult<IReadOnlyList<LedgerRecordItem>>(_items.ToArray());

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

        public Task DisconnectAsync()
        {
            IsConnected = false;
            ConnectionEpoch++;
            return Task.CompletedTask;
        }
    }
}
