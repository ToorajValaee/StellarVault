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

    private sealed class RevisionedProvider : IAccountObjectStoreProvider,
        IAccountObjectUpdateProvider
    {
        private readonly FakeProvider _inner = new("revisioned");
        public string ProviderId => _inner.ProviderId;
        public string? SeenRevision { get; private set; }
        public Task<ObjectMutationIntent> PrepareUpdateAsync(
            ObjectStoreScope scope, ObjectDescriptor descriptor, Stream source,
            string expectedRevision, ObjectWriteOptions options,
            CancellationToken ct = default)
        {
            SeenRevision = expectedRevision;
            return Task.FromResult(new ObjectMutationIntent(
                "staged-update", ObjectMutationState.Prepared,
                new ObjectStorageQuote(scope.ProviderId, scope.NetworkId,
                    "native", null, null, null, null), ObjectId: descriptor.ObjectId));
        }
        public Task<ObjectStorageQuote> EstimatePutAsync(
            ObjectStoreScope scope, ObjectDescriptor descriptor, Stream source,
            ObjectWriteOptions options, CancellationToken ct = default) =>
            _inner.EstimatePutAsync(scope, descriptor, source, options, ct);
        public Task<ObjectMutationIntent> PreparePutAsync(
            ObjectStoreScope scope, ObjectDescriptor descriptor, Stream source,
            ObjectWriteOptions options, CancellationToken ct = default) =>
            _inner.PreparePutAsync(scope, descriptor, source, options, ct);
        public Task<StoredObject?> GetAsync(
            ObjectStoreScope scope, string objectId, CancellationToken ct = default) =>
            _inner.GetAsync(scope, objectId, ct);
        public Task<Stream> OpenReadAsync(
            ObjectStoreScope scope, string objectId, CancellationToken ct = default) =>
            _inner.OpenReadAsync(scope, objectId, ct);
        public Task<ObjectMutationIntent> PrepareDeleteAsync(
            ObjectStoreScope scope, string objectId, string expectedRevision,
            CancellationToken ct = default) =>
            _inner.PrepareDeleteAsync(scope, objectId, expectedRevision, ct);
    }

    private sealed class UnsupportedExportDestination : IVerifiedObjectDestination
    {
        public Task<Stream> OpenTemporaryAsync(CancellationToken ct = default) =>
            throw new Exception("Unsupported provider opened a private destination.");
        public Task CommitAsync(CancellationToken ct = default) =>
            throw new Exception("Unsupported provider attempted to commit.");
        public Task AbortAsync(CancellationToken ct = default) =>
            Task.CompletedTask;
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

        try
        {
            await ((IVerifiedObjectExport)storage).ExportVerifiedAsync(
                stellarScope, "record-1", null!);
            throw new Exception("Null export destination was accepted.");
        }
        catch (ArgumentNullException) { }

        try
        {
            await ((IVerifiedObjectExport)storage).ExportVerifiedAsync(
                stellarScope, "record-1", new UnsupportedExportDestination());
            throw new Exception("Provider without verified streaming was accepted.");
        }
        catch (NotSupportedException) { }

        // The neutral router must never invent an approval path for a provider
        // that has not registered its existing signing/review workflow.
        try
        {
            await ((IObjectMutationReview)storage).ReviewAsync(
                stellarScope, "pending-mutation");
            throw new Exception("Unsupported publication review was permitted.");
        }
        catch (NotSupportedException) { }
        try
        {
            await ((IObjectMutationReview)storage).CancelReviewAsync(stellarScope);
            throw new Exception("Unsupported publication cancellation was permitted.");
        }
        catch (NotSupportedException) { }

        var revisioned = new RevisionedProvider();
        var updateRouter = (IAccountObjectUpdate)new AccountObjectStoreRouter([revisioned]);
        var revisionScope = stellarScope with { ProviderId = "revisioned" };
        using (var updated = new MemoryStream([7, 8, 9]))
        {
            var pending = await updateRouter.UpdateAsync(revisionScope, descriptor,
                updated, "original-version", new ObjectWriteOptions());
            Check(pending.State == ObjectMutationState.Prepared &&
                pending.ObjectId == descriptor.ObjectId &&
                revisioned.SeenRevision == "original-version",
                "Revision-pinned update was not routed to the requested provider.");
        }
        using (var updated = new MemoryStream([7]))
        {
            try
            {
                await updateRouter.UpdateAsync(revisionScope, descriptor,
                    updated, "original-version",
                    new ObjectWriteOptions(ExpectedRevision: "different-version"));
                throw new Exception("Conflicting update revision was accepted.");
            }
            catch (ArgumentException) { }
        }
        using (var updated = new MemoryStream([7]))
        {
            try
            {
                await ((IAccountObjectUpdate)storage).UpdateAsync(
                    stellarScope, descriptor, updated, "original-version",
                    new ObjectWriteOptions());
                throw new Exception("Provider without a revisioned edit path was accepted.");
            }
            catch (NotSupportedException) { }
        }

        Console.WriteLine("PASS: chain-neutral account object store contracts and routing");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
