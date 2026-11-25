using System.Security.Cryptography;
using System.Text.Json;
using VaultEngine.Stellar.Services;

internal static class ReconciliationCleanupSmoke
{
    private const string Root = "GAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAWHF";
    public static async Task RunAsync()
    {
        var retention = new VaultDeviceRetentionGate();
        retention.Select(VaultDeviceRetentionPolicy.EncryptedLocalWithManualSync);
        var template = VaultPendingMutation.Create(Root, Root, VaultMutationKind.CreateRecord,
            "0123456789abcdef", JsonSerializer.SerializeToUtf8Bytes(new LedgerRecordDraft("record", "secret")))
            with { State = VaultSyncState.Uncertain, TransactionHash = new string('a', 64) };
        var store = new TrackingStore(template);
        var verifier = new Verifier();
        var queue = new VaultPendingMutationQueue(store, retention, verifier);
        var denied = await queue.ReconcileAsync(Root, Root, template.Id);
        Check(!denied.Confirmed && denied.State == VaultSyncState.Uncertain, "Denied proof changed state");
        store.CheckCleared();
        verifier.Permit = true;
        store.RejectCas = true;
        await Reject(() => queue.ReconcileAsync(Root, Root, template.Id));
        store.CheckCleared();
        store.RejectCas = false;
        store.ForeignList = true;
        await Reject(() => queue.ReconcileAsync(Root, Root, template.Id));
        store.CheckCleared();
        store.ForeignList = false;
        var verified = await queue.ReconcileAsync(Root, Root, template.Id);
        Check(verified.Confirmed && verified.State == VaultSyncState.Confirmed, "Verified CAS did not finalize");
        store.CheckCleared();
        Check(store.Durable.EncryptedPayload.Any(b => b != 0), "Cleanup damaged durable queue content");
        CryptographicOperations.ZeroMemory(template.EncryptedPayload);
        CryptographicOperations.ZeroMemory(store.Durable.EncryptedPayload);
        Console.WriteLine("PASS: reconciliation clears decrypted snapshots after rejected proof, CAS conflict, foreign scope and success.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (InvalidOperationException) { return; } catch (InvalidDataException) { return; }
        throw new Exception("Invalid reconciliation succeeded");
    }
    private sealed class Verifier : IVaultLedgerMutationVerifier
    {
        public bool Permit { get; set; }
        public Task<bool> IsConfirmedAsync(VaultPendingMutation mutation, string hash, CancellationToken ct = default) => Task.FromResult(Permit);
    }
    private sealed class TrackingStore(VaultPendingMutation initial) : IVaultPendingMutationStore
    {
        public VaultPendingMutation Durable { get; private set; } = initial with { EncryptedPayload = initial.EncryptedPayload.ToArray() };
        public bool RejectCas { get; set; }
        public bool ForeignList { get; set; }
        private readonly List<byte[]> issued = [];
        private VaultPendingMutation Copy()
        {
            var copy = Durable with { EncryptedPayload = Durable.EncryptedPayload.ToArray() };
            issued.Add(copy.EncryptedPayload);
            return copy;
        }
        public Task<VaultPendingMutation?> GetAsync(string account, string network, string id, CancellationToken ct = default) => Task.FromResult<VaultPendingMutation?>(Copy());
        public Task<IReadOnlyList<VaultPendingMutation>> ListAsync(string account, string network, CancellationToken ct = default)
        {
            var entry = Copy();
            if (ForeignList) entry = entry with { NetworkId = "foreign" };
            return Task.FromResult<IReadOnlyList<VaultPendingMutation>>([entry]);
        }
        public Task<bool> TryReplaceAsync(VaultPendingMutation previous, VaultPendingMutation next, CancellationToken ct = default)
        {
            if (RejectCas) { issued.Add(next.EncryptedPayload); return Task.FromResult(false); }
            CryptographicOperations.ZeroMemory(Durable.EncryptedPayload);
            Durable = next with { EncryptedPayload = next.EncryptedPayload.ToArray() };
            return Task.FromResult(true);
        }
        public Task<bool> AddAsync(VaultPendingMutation entry, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> TryDeleteAsync(VaultPendingMutation expected, CancellationToken ct = default) => throw new NotSupportedException();
        public void CheckCleared()
        {
            Check(issued.All(b => b.All(value => value == 0)), "A decrypted queue snapshot survived reconciliation");
            issued.Clear();
        }
    }
}
