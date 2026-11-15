using System.Text;
using StellarVault.Core.Services;
using VaultEngine.Core;

internal static class StellarObjectEstimateSmoke
{
    private const string Root =
        "GAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAWHF";

    private sealed class QuoteOnlyLedger : IStellarLedgerRecords
    {
        public bool IsConnected { get; set; } = true;
        public string Account { get; set; } = Root;
        public int EstimateCalls { get; private set; }
        public int MutationCalls { get; private set; }
        public IReadOnlyList<LedgerRecordItem> Published { get; set; } = [];
        public bool SwitchRootDuringEstimate { get; set; }

        public Task<LedgerAccountInfo> InspectAccountInfoAsync() =>
            Task.FromResult(new LedgerAccountInfo(Account, Account,
                1, 0, 0, "10.0000000", "8.0000000", "0.0000000"));

        public Task<LedgerCostPreview> EstimateAsync(
            LedgerRecordDraft draft, string? recordId = null)
        {
            if (draft.Title != "Estimate me" || draft.Content != "sensitive")
                throw new Exception("Engine estimate changed the record payload.");
            EstimateCalls++;
            if (SwitchRootDuringEstimate)
                Account = "different-root";
            return Task.FromResult(new LedgerCostPreview(
                2, 1, 0.0000200m, 1.5000000m, 0.5000000m, true));
        }

        private Task<T> Unsupported<T>()
        {
            MutationCalls++;
            return Task.FromException<T>(new NotSupportedException(
                "Estimating an object must never publish a transaction."));
        }

        private Task Unsupported()
        {
            MutationCalls++;
            return Task.FromException(new NotSupportedException(
                "Estimating an object must never publish a transaction."));
        }

        public Task ConnectAsync(string mnemonic, string horizonUrl, string passphrase) => Unsupported();
        public Task<bool> HasRegistryAsync() => Unsupported<bool>();
        public Task<LedgerFundingStatus> InspectRegistryFundingAsync() =>
            Unsupported<LedgerFundingStatus>();
        public Task InitializeRegistryAsync() => Unsupported();
        public Task<IReadOnlyList<LedgerRecordItem>> ReadAsync() =>
            Task.FromResult(Published);
        public Task<string> PublishAsync(string title, string content) => Unsupported<string>();
        public Task<string> PublishDetailedAsync(LedgerRecordDraft draft) => Unsupported<string>();
        public Task<string> PublishWithIdAsync(string id, LedgerRecordDraft draft) =>
            Unsupported<string>();
        public Task<LedgerCostPreview> EstimateDeleteAsync(string id) =>
            Unsupported<LedgerCostPreview>();
        public Task<LedgerMutationResult> UpdateAsync(string id, LedgerRecordDraft draft) =>
            Unsupported<LedgerMutationResult>();
        public Task<LedgerMutationResult> DeleteAsync(string id) => Unsupported<LedgerMutationResult>();
        public Task DisconnectAsync() => Unsupported();
    }

    public static async Task RunAsync()
    {
        var ledger = new QuoteOnlyLedger();
        // Only the HD ledger is required for this read-only estimate path.
        // No queue, signer or linked account can be instantiated accidentally.
        var provider = new StellarAccountObjectStoreProvider(
            ledger, null!, null!, null!, null!, null!,
            new VaultNetworkSelection(), new VaultRecordTypeFactory());
        var engine = new AccountObjectStoreRouter([provider]);
        var scope = new ObjectStoreScope(
            "stellar", "testnet", Root, VaultMutationStrategyPolicy.Hd);
        var descriptor = new ObjectDescriptor(
            "new", VaultRecordTypeFactory.Credential, "Estimate me");
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes("sensitive")))
        {
            var quote = await engine.EstimatePutAsync(scope, descriptor, input,
                new ObjectWriteOptions(ObjectCapacityPolicy.SingleAccount));
            Check(quote.ProviderId == "stellar" &&
                quote.NetworkId == "testnet" &&
                quote.FeeUnit == "XLM" &&
                quote.EstimatedFee == "0.0000200" &&
                quote.RequiredReserve == "1.5000000" &&
                quote.RequiresCapacityExpansion == true &&
                quote.EstimatedStoredBytes == 9,
                "HD engine quote lost fee, reserve or account-capacity warning.");
        }
        Check(ledger.EstimateCalls == 1 && ledger.MutationCalls == 0,
            "Read-only object estimate performed unexpected network mutations.");
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes("sensitive")))
        {
            ledger.SwitchRootDuringEstimate = true;
            try
            {
                await engine.EstimatePutAsync(scope, descriptor, input,
                    new ObjectWriteOptions());
                throw new Exception("HD root changed while estimating without rejection.");
            }
            catch (InvalidOperationException) { }
        }
        Check(ledger.MutationCalls == 0,
            "A rejected account-switch estimate attempted a mutation.");
        // Exercise the actual SVTREE3 writer/reader through the neutral provider,
        // not a mocked file reconstruction or a string-only source check.
        var fileLedger = new QuoteOnlyLedger();
        var fileProvider = new StellarAccountObjectStoreProvider(
            fileLedger, null!, null!, null!, null!, null!,
            new VaultNetworkSelection(), new VaultRecordTypeFactory());
        var fileEngine = new AccountObjectStoreRouter([fileProvider]);
        byte[] original = Encoding.UTF8.GetBytes(new string('X', 4096) + "tail");
        var published = new List<LedgerRecordItem>();
        using (var source = new MemoryStream(original, writable: false))
        {
            await VaultStreamingFileTree.WriteAsync(source,
                "test.bin", "application/octet-stream",
                (emission, _) =>
                {
                    var draft = VaultLinkedTreeFilePolicy.Draft(
                        emission, "test.bin", "0123456789abcdef");
                    published.Add(new LedgerRecordItem(emission.Id, draft.Title,
                        draft.Content, draft.FolderId, draft.Tags, draft.IsFolder));
                    return Task.CompletedTask;
                });
        }
        fileLedger.Published = published;
        var rootRecord = published.Single(x => VaultLinkedTreeFilePolicy.IsRootRecord(x));
        var fileMetadata = await fileEngine.GetAsync(scope, rootRecord.Id);
        Check(fileMetadata?.Descriptor.TypeId == VaultRecordTypeFactory.File &&
              fileMetadata.Descriptor.Name == "test.bin" &&
              fileMetadata.Length == original.Length &&
              fileMetadata.IntegrityHash?.Length == 64,
            "Engine failed to return verified streaming file metadata.");
        await using (var opened = await fileEngine.OpenReadAsync(scope, rootRecord.Id))
        {
            using var received = new MemoryStream();
            await opened.CopyToAsync(received);
            Check(received.ToArray().AsSpan().SequenceEqual(original),
                "Engine verified file read did not reconstruct exact content.");
        }

        // Corrupted published file content must never escape the private
        // verification stream as an apparently valid plaintext object.
        var damaged = published.First(x =>
            !VaultLinkedTreeFilePolicy.IsRootRecord(x));
        fileLedger.Published = published.Select(x => x.Id == damaged.Id
            ? x with { Content = x.Content + "tampered" } : x).ToArray();
        try
        {
            await using var unsafeRead = await fileEngine.OpenReadAsync(scope, rootRecord.Id);
            throw new Exception("Engine returned a corrupted file as verified plaintext.");
        }
        catch (InvalidDataException) { }

        Check(fileLedger.MutationCalls == 0,
            "Verified file read unexpectedly invoked a mutation operation.");
        Console.WriteLine("PASS: neutral verified SVTREE3 metadata, reads and tamper rejection");
        Console.WriteLine("PASS: chain-neutral HD fee/reserve estimate and account isolation");
    }

    private static void Check(bool result, string message)
    {
        if (!result) throw new Exception(message);
    }
}
