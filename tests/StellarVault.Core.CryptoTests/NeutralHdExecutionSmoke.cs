using System.Security.Cryptography;
using VaultEngine.Stellar.Services;
using VaultEngine.Core;

internal static class NeutralHdExecutionSmoke
{
    private const string Root = "GAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAWHF";
    public static async Task RunAsync()
    {
        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sv-neutral-hd-" + Guid.NewGuid().ToString("N")));
        var wrappingKey = RandomNumberGenerator.GetBytes(32);
        using var sessions = new VaultSessionManager();
        try
        {
            var local = new LocalStellarVault(Path.Combine(directory, "vault"));
            sessions.Activate(await local.CreateSessionAsync(Root, wrappingKey));
            var retention = new VaultDeviceRetentionGate();
            retention.Select(VaultDeviceRetentionPolicy.EncryptedLocalWithManualSync);
            var network = new VaultNetworkSelection();
            var store = new FileSystemVaultPendingMutationStore(new VaultLocalQueueKeySession(sessions), retention, Path.Combine(directory, "queue"));
            var queue = new VaultPendingMutationQueue(store, retention, new DenyVaultLedgerMutationVerifier());
            var drafts = new VaultHybridDraftWorkspace(queue, sessions, retention, network);
            var ledger = new Ledger();
            var reviewer = new VaultHybridSyncReviewService(queue, ledger, network, retention, sessions,
                new VaultScopedQueueReview(queue, sessions, retention, network));
            var publisher = new VaultManualSyncCoordinator(queue, ledger, retention, network, sessions, ledger, ledger, ledger, ledger);
            var provider = new StellarAccountObjectStoreProvider(ledger, null!, drafts, null!, sessions, null!, network,
                new VaultRecordTypeFactory(), hdReview: reviewer, hdPublication: publisher, hdQueue: queue);
            var engine = new AccountObjectStoreRouter([provider]);
            var scope = new ObjectStoreScope("stellar", "testnet", Root, VaultMutationStrategyPolicy.Hd);
            var mutation = await drafts.CreateAsync(new LedgerRecordDraft("HD neutral", "secret"));
            var review = await engine.ReviewAsync(scope, mutation.Id);
            await Reject(() => provider.SubmitApprovedAsync(scope, new(review.ReviewReference!, false, true)));
            await Reject(() => engine.SubmitApprovedAsync(scope, new(review.ReviewReference!, true, true)));
            review = await engine.ReviewAsync(scope, mutation.Id);
            sessions.Lock();
            sessions.Activate(await local.UnlockSessionAsync(Root, wrappingKey));
            await Reject(() => engine.SubmitApprovedAsync(scope, new(review.ReviewReference!, true, true)));
            review = await engine.ReviewAsync(scope, mutation.Id);
            ledger.RequiresExpansion = true;
            await Reject(() => engine.SubmitApprovedAsync(scope, new(review.ReviewReference!, true, true)));
            ledger.RequiresExpansion = false;
            Check(ledger.Broadcasts == 0, "Rejected neutral HD approval reached transport");
            review = await engine.ReviewAsync(scope, mutation.Id);
            var result = await engine.SubmitApprovedAsync(scope, new(review.ReviewReference!, true, true));
            Check(result.State == ObjectMutationState.SubmittedUncertain && result.TransactionHash == Ledger.Hash && ledger.Broadcasts == 1,
                "Neutral HD execution failed to preserve unknown-outcome semantics");
            var durable = (await store.GetAsync(Root, Root, mutation.Id))!;
            try { Check(durable.State == VaultSyncState.Uncertain && durable.TransactionHash == Ledger.Hash, "Signed identity was not durably retained"); }
            finally { CryptographicOperations.ZeroMemory(durable.EncryptedPayload); }
            await Reject(() => engine.SubmitApprovedAsync(scope, new(review.ReviewReference!, true, true)));
            Check(ledger.Broadcasts == 1, "Neutral HD approval replayed transport");
            var unverified = await engine.ReconcileAsync(scope, mutation.Id);
            Check(!unverified.Verified && unverified.State == ObjectMutationState.SubmittedUncertain && ledger.Broadcasts == 1, "Neutral reconciliation bypassed the verifier or replayed transport");
            CryptographicOperations.ZeroMemory(mutation.EncryptedPayload);
            Console.WriteLine("PASS: neutral HD approval, session epochs, capacity recheck, signed-hash checkpoint and no replay.");
        }
        finally
        {
            sessions.Lock();
            CryptographicOperations.ZeroMemory(wrappingKey);
            if (!directory.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(directory).StartsWith("sv-neutral-hd-", StringComparison.Ordinal)) throw new Exception("Unexpected test directory");
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        catch (UnauthorizedAccessException) { return; }
        throw new Exception("Unsafe neutral execution was accepted");
    }
    private sealed class Ledger : IStellarLedgerRecords, IVaultPreparedCreateLedger,
        IVaultPreparedUpdateLedger, IVaultPreparedDeletionLedger, IVaultLedgerManifestCopyInventory
    {
        public static string Hash => new('b', 64);
        public bool IsConnected => true;
        public bool RequiresExpansion { get; set; }
        public int Broadcasts { get; private set; }
        private readonly List<LedgerRecordItem> records = [];
        public Task<LedgerAccountInfo> InspectAccountInfoAsync() => Task.FromResult(new LedgerAccountInfo(Root, Root, 1, 0, 0, "10", "8", "0"));
        public Task<IReadOnlyList<LedgerRecordItem>> ReadAsync() => Task.FromResult<IReadOnlyList<LedgerRecordItem>>(records.ToArray());
        public Task<LedgerCostPreview> EstimateAsync(LedgerRecordDraft draft, string? recordId = null) => Task.FromResult(new LedgerCostPreview(1, 1, .00001m, .5m, .5m, RequiresExpansion));
        public async Task<string> PublishWithPreparedHashAsync(string id, LedgerRecordDraft draft, Func<string, Task> checkpoint)
        {
            await checkpoint(Hash); // Required durable callback before the simulated broadcast.
            Broadcasts++;
            records.Add(new(id, draft.Title, draft.Content));
            return Hash;
        }
        public Task<IReadOnlyList<string>> InspectOwnersAsync(string root, string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([Root]);
        public Task<LedgerMutationResult> UpdateIfUnchangedWithPreparedHashAsync(string id, LedgerRecordDraft draft, string version, Func<string, Task> checkpoint, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LedgerMutationResult> DeleteIfUnchangedWithJournalAsync(string id, string version, IReadOnlyList<string> owners, Func<string, string, Task> checkpoint, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ConnectAsync(string mnemonic, string horizon, string passphrase) => throw new NotSupportedException();
        public Task<bool> HasRegistryAsync() => Task.FromResult(true);
        public Task<LedgerFundingStatus> InspectRegistryFundingAsync() => throw new NotSupportedException();
        public Task InitializeRegistryAsync() => throw new NotSupportedException();
        public Task<string> PublishAsync(string title, string content) => throw new NotSupportedException();
        public Task<string> PublishDetailedAsync(LedgerRecordDraft draft) => throw new NotSupportedException();
        public Task<string> PublishWithIdAsync(string id, LedgerRecordDraft draft) => throw new NotSupportedException();
        public Task<LedgerCostPreview> EstimateDeleteAsync(string id) => throw new NotSupportedException();
        public Task<LedgerMutationResult> UpdateAsync(string id, LedgerRecordDraft draft) => throw new NotSupportedException();
        public Task<LedgerMutationResult> DeleteAsync(string id) => throw new NotSupportedException();
        public Task DisconnectAsync() => Task.CompletedTask;
    }
}
