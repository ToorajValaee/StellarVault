namespace VaultEngine.Core;

/// <summary>
/// Chain-neutral logical scope of an object store. The provider owns the
/// meaning of network, owner and storage account addresses; no private
/// signing material is part of the contract.
/// </summary>
public sealed record ObjectStoreScope(
    string ProviderId, string NetworkId, string StoreId,
    string AccountStrategyId);

/// <summary>Capacity policy is not an account identity or a blockchain.</summary>
public enum ObjectCapacityPolicy
{
    SingleAccount,
    ExpandWithExplicitApproval
}

public sealed record ObjectWriteOptions(
    ObjectCapacityPolicy CapacityPolicy = ObjectCapacityPolicy.SingleAccount,
    string? ExpectedRevision = null);

/// <summary>
/// Typed logical object metadata. Content type describes bytes, while TypeId
/// describes their semantics. An unknown type is not implicitly executable.
/// </summary>
public sealed record ObjectDescriptor(
    string ObjectId, string TypeId, string Name,
    string? ContentType = null, string? ParentId = null,
    ObjectRecordFields? Fields = null);

/// <summary>
/// Optional structured record fields separate from content bytes. Null means
/// the caller did not supply the full field set; empty values deliberately
/// clear fields when supplied. Providers must not silently discard them.
/// </summary>
public sealed record ObjectRecordFields(
    string? UserName = null, string? Url = null,
    string? Notes = null, string[]? Tags = null);

/// <summary>
/// Provider-neutral estimate. Costs are quoted in the provider's asset/unit,
/// never assumed to be XLM, gas or a convertible fiat amount. A null fee
/// explicitly means no network quote exists yet; it is NOT a zero-fee quote.
/// A null capacity-expansion flag means capacity has not been evaluated.
/// </summary>
public sealed record ObjectStorageQuote(
    string ProviderId, string NetworkId, string FeeUnit,
    string? EstimatedFee, string? RequiredReserve,
    long? EstimatedStoredBytes, bool? RequiresCapacityExpansion);

/// <summary>
/// A mutation is never interpreted as published merely because it was
/// prepared or submitted. User consent, cryptographic signatures and proof
/// of finality are separate, provider-specific steps.
/// </summary>
public enum ObjectMutationState
{
    Prepared,
    AwaitingApproval,
    CapacityRequired,
    SubmittedUncertain,
    Verified
}

public sealed record ObjectMutationIntent(
    string OperationId, ObjectMutationState State,
    ObjectStorageQuote Quote, string? ApprovalRequestId = null,
    string? ObjectId = null);

public sealed record StoredObject(
    ObjectDescriptor Descriptor, string Revision,
    long? Length, string? IntegrityHash);

/// <summary>
/// Application-facing object database, independent of Stellar or EVM.
/// The engine does NOT hold credentials or silently sign/send transactions.
/// Put/Delete prepare a durable, explicit-approval mutation intent; they do
/// not imply completion. Get/OpenRead require provider integrity checks.
/// </summary>
public interface IAccountObjectStore
{
    Task<ObjectStorageQuote> EstimatePutAsync(
        ObjectStoreScope scope, ObjectDescriptor descriptor,
        Stream source, ObjectWriteOptions options,
        CancellationToken ct = default);

    Task<ObjectMutationIntent> PutAsync(
        ObjectStoreScope scope, ObjectDescriptor descriptor,
        Stream source, ObjectWriteOptions options,
        CancellationToken ct = default);

    Task<StoredObject?> GetAsync(
        ObjectStoreScope scope, string objectId,
        CancellationToken ct = default);

    Task<Stream> OpenReadAsync(
        ObjectStoreScope scope, string objectId,
        CancellationToken ct = default);

    Task<ObjectMutationIntent> DeleteAsync(
        ObjectStoreScope scope, string objectId, string expectedRevision,
        CancellationToken ct = default);
}

/// <summary>
/// Explicit optimistic-concurrency update, distinct from create. The provider
/// must verify ExpectedRevision against its own authoritative remote format
/// before queuing any mutation. An update never implies a published receipt.
/// </summary>
public interface IAccountObjectUpdate
{
    Task<ObjectMutationIntent> UpdateAsync(
        ObjectStoreScope scope, ObjectDescriptor descriptor,
        Stream source, string expectedRevision,
        ObjectWriteOptions options, CancellationToken ct = default);
}

public interface IAccountObjectUpdateProvider
{
    Task<ObjectMutationIntent> PrepareUpdateAsync(
        ObjectStoreScope scope, ObjectDescriptor descriptor,
        Stream source, string expectedRevision,
        ObjectWriteOptions options, CancellationToken ct = default);
}

/// <summary>
/// A chain-specific adapter implements this interface in the engine's
/// composition layer. It maps record/file layouts and capacity policy into
/// that chain's signing, ledger verification and receipt semantics.
/// </summary>
public interface IAccountObjectStoreProvider
{
    string ProviderId { get; }
    Task<ObjectStorageQuote> EstimatePutAsync(
        ObjectStoreScope scope, ObjectDescriptor descriptor,
        Stream source, ObjectWriteOptions options,
        CancellationToken ct = default);
    Task<ObjectMutationIntent> PreparePutAsync(
        ObjectStoreScope scope, ObjectDescriptor descriptor,
        Stream source, ObjectWriteOptions options,
        CancellationToken ct = default);
    Task<StoredObject?> GetAsync(
        ObjectStoreScope scope, string objectId,
        CancellationToken ct = default);
    Task<Stream> OpenReadAsync(
        ObjectStoreScope scope, string objectId,
        CancellationToken ct = default);
    Task<ObjectMutationIntent> PrepareDeleteAsync(
        ObjectStoreScope scope, string objectId, string expectedRevision,
        CancellationToken ct = default);
}

/// <summary>
/// A destination owns a PRIVATE, unobservable staging stream until CommitAsync
/// succeeds. AbortAsync must destroy partial plaintext. Implementations must
/// not expose the staging path or bytes in an API response before commit.
/// </summary>
public interface IVerifiedObjectDestination
{
    Task<Stream> OpenTemporaryAsync(CancellationToken ct = default);
    Task CommitAsync(CancellationToken ct = default);
    Task AbortAsync(CancellationToken ct = default);
}

/// <summary>
/// Optional verified large-object streaming capability. It does not weaken
/// OpenReadAsync's small-buffer guarantee or imply every provider supports it.
/// </summary>
public interface IVerifiedObjectExport
{
    Task<StoredObject> ExportVerifiedAsync(
        ObjectStoreScope scope, string objectId,
        IVerifiedObjectDestination destination, CancellationToken ct = default);
}

/// <summary>Provider-specific verified streaming support.</summary>
public interface IVerifiedObjectExportProvider
{
    Task<StoredObject> ExportVerifiedAsync(
        ObjectStoreScope scope, string objectId,
        IVerifiedObjectDestination destination, CancellationToken ct = default);
}

/// <summary>
/// Read-only planning result for a previously staged mutation. An approval
/// reference is NOT authorization to sign, spend or submit a transaction.
/// </summary>
public sealed record ObjectMutationReview(
    string OperationId, string ObjectId, ObjectMutationState State,
    ObjectStorageQuote Quote, string Explanation,
    string? ReviewReference = null);

/// <summary>
/// Optional planning capability: the existing provider-specific publisher
/// retains one-shot signed approval and historical verification responsibilities.
/// </summary>
public interface IObjectMutationReview
{
    Task<ObjectMutationReview> ReviewAsync(
        ObjectStoreScope scope, string operationId,
        CancellationToken ct = default);
    Task CancelReviewAsync(
        ObjectStoreScope scope, CancellationToken ct = default);
}

public interface IObjectMutationReviewProvider
{
    Task<ObjectMutationReview> ReviewAsync(
        ObjectStoreScope scope, string operationId,
        CancellationToken ct = default);
    Task CancelReviewAsync(
        ObjectStoreScope scope, CancellationToken ct = default);
}

/// <summary>
/// Strict router: no implicit default chain, network, account, provider or
/// authorization strategy. A missing provider fails closed.
/// </summary>
public sealed class AccountObjectStoreRouter : IAccountObjectStore, IAccountObjectUpdate, IVerifiedObjectExport, IObjectMutationReview
{
    private readonly IReadOnlyDictionary<string, IAccountObjectStoreProvider> _providers;

    public AccountObjectStoreRouter(IEnumerable<IAccountObjectStoreProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var index = new Dictionary<string, IAccountObjectStoreProvider>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            if (provider is null || string.IsNullOrWhiteSpace(provider.ProviderId) ||
                !index.TryAdd(provider.ProviderId, provider))
                throw new ArgumentException("Object storage providers require unique IDs.", nameof(providers));
        }
        _providers = index;
    }

    private IAccountObjectStoreProvider Resolve(ObjectStoreScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(scope.ProviderId) ||
            string.IsNullOrWhiteSpace(scope.NetworkId) ||
            string.IsNullOrWhiteSpace(scope.StoreId) ||
            string.IsNullOrWhiteSpace(scope.AccountStrategyId))
            throw new ArgumentException(
                "Object storage requests require provider, network, store and account strategy.",
                nameof(scope));
        return _providers.TryGetValue(scope.ProviderId, out var provider)
            ? provider : throw new NotSupportedException(
                "The selected account-backed object storage provider is not registered.");
    }

    private static void ValidateDescriptor(ObjectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(descriptor.ObjectId) ||
            string.IsNullOrWhiteSpace(descriptor.TypeId) ||
            string.IsNullOrWhiteSpace(descriptor.Name))
            throw new ArgumentException("Object ID, type and name are required.");
    }

    private static void ValidateSource(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("Object content must be readable.", nameof(source));
    }

    public Task<ObjectStorageQuote> EstimatePutAsync(
        ObjectStoreScope scope, ObjectDescriptor descriptor,
        Stream source, ObjectWriteOptions options, CancellationToken ct = default)
    {
        var provider = Resolve(scope);
        ValidateDescriptor(descriptor);
        ValidateSource(source);
        ArgumentNullException.ThrowIfNull(options);
        return provider.EstimatePutAsync(scope, descriptor, source, options, ct);
    }

    public Task<ObjectMutationIntent> PutAsync(
        ObjectStoreScope scope, ObjectDescriptor descriptor,
        Stream source, ObjectWriteOptions options, CancellationToken ct = default)
    {
        var provider = Resolve(scope);
        ValidateDescriptor(descriptor);
        ValidateSource(source);
        ArgumentNullException.ThrowIfNull(options);
        return provider.PreparePutAsync(scope, descriptor, source, options, ct);
    }

    public Task<ObjectMutationIntent> UpdateAsync(
        ObjectStoreScope scope, ObjectDescriptor descriptor,
        Stream source, string expectedRevision,
        ObjectWriteOptions options, CancellationToken ct = default)
    {
        var provider = Resolve(scope);
        ValidateDescriptor(descriptor);
        ValidateSource(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        ArgumentNullException.ThrowIfNull(options);
        if (options.ExpectedRevision is not null &&
            options.ExpectedRevision != expectedRevision)
            throw new ArgumentException(
                "Conflicting optimistic concurrency revisions.", nameof(options));
        return provider is IAccountObjectUpdateProvider updater
            ? updater.PrepareUpdateAsync(scope, descriptor, source, expectedRevision, options, ct)
            : Task.FromException<ObjectMutationIntent>(
                new NotSupportedException("Selected provider does not support revision-pinned updates."));
    }

    public Task<StoredObject?> GetAsync(
        ObjectStoreScope scope, string objectId, CancellationToken ct = default)
    {
        var provider = Resolve(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        return provider.GetAsync(scope, objectId, ct);
    }

    public Task<Stream> OpenReadAsync(
        ObjectStoreScope scope, string objectId, CancellationToken ct = default)
    {
        var provider = Resolve(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        return provider.OpenReadAsync(scope, objectId, ct);
    }

    public Task<StoredObject> ExportVerifiedAsync(
        ObjectStoreScope scope, string objectId,
        IVerifiedObjectDestination destination, CancellationToken ct = default)
    {
        var provider = Resolve(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentNullException.ThrowIfNull(destination);
        return provider is IVerifiedObjectExportProvider verified
            ? verified.ExportVerifiedAsync(scope, objectId, destination, ct)
            : Task.FromException<StoredObject>(
                new NotSupportedException("Selected storage provider has no verified streaming export."));
    }

    public Task<ObjectMutationReview> ReviewAsync(
        ObjectStoreScope scope, string operationId, CancellationToken ct = default)
    {
        var provider = Resolve(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        return provider is IObjectMutationReviewProvider reviewer
            ? reviewer.ReviewAsync(scope, operationId, ct)
            : Task.FromException<ObjectMutationReview>(
                new NotSupportedException("Selected provider has no reviewed publication planning."));
    }

    public Task CancelReviewAsync(
        ObjectStoreScope scope, CancellationToken ct = default)
    {
        var provider = Resolve(scope);
        return provider is IObjectMutationReviewProvider reviewer
            ? reviewer.CancelReviewAsync(scope, ct)
            : Task.FromException(
                new NotSupportedException("Selected provider has no reviewed publication planning."));
    }

    public Task<ObjectMutationIntent> DeleteAsync(
        ObjectStoreScope scope, string objectId, string expectedRevision,
        CancellationToken ct = default)
    {
        var provider = Resolve(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        return provider.PrepareDeleteAsync(scope, objectId, expectedRevision, ct);
    }
}
