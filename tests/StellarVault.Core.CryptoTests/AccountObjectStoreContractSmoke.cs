using VaultEngine.Core;

internal static class AccountObjectStoreContractSmoke
{
    private sealed class FakeProvider(string id) : IAccountObjectStoreProvider
    {
        public string ProviderId { get; } = id;
        public int Writes { get; private set; }
        public ObjectWriteOptions? LastOptions { get; private set; }

        public Task<ObjectStorageQuote> EstimatePutAsync(
            ObjectStoreScope scope, ObjectDescriptor descriptor, Stream source,
            ObjectWriteOptions options, CancellationToken ct = default)
        {
            LastOptions = options;
            return Task.FromResult(new ObjectStorageQuote(
                scope.ProviderId, scope.NetworkId, "native", "0.001",
                "1.0", source.CanSeek ? source.Length : null,
                options.CapacityPolicy == ObjectCapacityPolicy.ExpandWithExplicitApproval));
        }

        public Task<ObjectMutationIntent> PreparePutAsync(
            ObjectStoreScope scope, ObjectDescriptor descriptor, Stream source,
            ObjectWriteOptions options, CancellationToken ct = default)
        {
            Writes++;
            LastOptions = options;
            return Task.FromResult(new ObjectMutationIntent(
                "test-operation", ObjectMutationState.AwaitingApproval,
                new ObjectStorageQuote(scope.ProviderId, scope.NetworkId,
                    "native", "0.001", null, null, false)));
        }

        public Task<StoredObject?> GetAsync(
            ObjectStoreScope scope, string objectId, CancellationToken ct = default) =>
            Task.FromResult<StoredObject?>(new(
                new ObjectDescriptor(objectId, "text", "example"), "revision-1", 5, null));

        public Task<Stream> OpenReadAsync(
            ObjectStoreScope scope, string objectId, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream([1, 2, 3], writable: false));

        public Task<ObjectMutationIntent> PrepareDeleteAsync(
            ObjectStoreScope scope, string objectId, string expectedRevision,
            CancellationToken ct = default) =>
            Task.FromResult(new ObjectMutationIntent(
                "delete-op", ObjectMutationState.AwaitingApproval,
                new ObjectStorageQuote(scope.ProviderId, scope.NetworkId,
                    "native", "0.001", null, null, false)));
    }

    public static async Task RunAsync()
    {
        var stellar = new FakeProvider("stellar");
        var evm = new FakeProvider("evm");
        var storage = new AccountObjectStoreRouter([stellar, evm]);
        var stellarScope = new ObjectStoreScope("stellar", "testnet", "account-a", "linked");
        var evmScope = new ObjectStoreScope("evm", "eip155:11155111",
            "contract-a", "contract-wallet");
        var descriptor = new ObjectDescriptor("record-1", "text", "Example", "text/plain");
        using var body = new MemoryStream([1, 2, 3]);
        var quote = await storage.EstimatePutAsync(stellarScope, descriptor,
            body, new ObjectWriteOptions(ObjectCapacityPolicy.SingleAccount));
        Check(quote.ProviderId == "stellar" && quote.RequiresCapacityExpansion == false,
            "A single-account request incorrectly expanded capacity.");
        body.Position = 0;
        var intent = await storage.PutAsync(evmScope, descriptor,
            body, new ObjectWriteOptions(ObjectCapacityPolicy.ExpandWithExplicitApproval));
        Check(stellar.Writes == 0 && evm.Writes == 1 &&
              evm.LastOptions?.CapacityPolicy ==
                  ObjectCapacityPolicy.ExpandWithExplicitApproval &&
              intent.State == ObjectMutationState.AwaitingApproval,
            "Object store router confused providers or skipped approval state.");
        var item = await storage.GetAsync(stellarScope, "record-1");
        Check(item?.Revision == "revision-1", "Object read did not route correctly.");
        using var stream = await storage.OpenReadAsync(evmScope, "record-1");
        Check(stream.ReadByte() == 1, "Streaming object read did not route correctly.");

        try
        {
            await storage.PutAsync(stellarScope with { ProviderId = "unregistered" },
                descriptor, body, new ObjectWriteOptions());
            throw new Exception("Unregistered provider was accepted.");
        }
        catch (NotSupportedException) { }

        try
        {
            await storage.DeleteAsync(stellarScope, "record-1", "");
            throw new Exception("Unversioned delete was accepted.");
        }
        catch (ArgumentException) { }

        try
        {
            _ = new AccountObjectStoreRouter([stellar, stellar]);
            throw new Exception("Duplicate providers were accepted.");
        }
        catch (ArgumentException) { }

        Console.WriteLine("PASS: chain-neutral account object store contracts and routing");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
