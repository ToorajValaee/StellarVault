namespace VaultEngine.Core;

public sealed record TransactionObservationScope(string ProviderId, string NetworkId);

/// <summary>Provider evidence metadata; observing a transaction never confirms an object mutation.</summary>
public sealed record TransactionObservation(
    string TransactionId, string? SourceAccount, long? BlockNumber,
    string NetworkIdentity, DateTimeOffset ObservedAtUtc, string Evidence);

public interface ITransactionObservation
{
    Task<TransactionObservation?> InspectAsync(TransactionObservationScope scope,
        string transactionId, CancellationToken ct = default);
}

public interface ITransactionObservationProvider
{
    string ProviderId { get; }
    Task<TransactionObservation?> InspectAsync(string networkId,
        string transactionId, CancellationToken ct = default);
}

public sealed class TransactionObservationRouter : ITransactionObservation
{
    private readonly IReadOnlyDictionary<string, ITransactionObservationProvider> providers;
    public TransactionObservationRouter(IEnumerable<ITransactionObservationProvider> configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        var index = new Dictionary<string, ITransactionObservationProvider>(StringComparer.Ordinal);
        foreach (var provider in configured)
            if (provider is null || string.IsNullOrWhiteSpace(provider.ProviderId) ||
                !index.TryAdd(provider.ProviderId, provider))
                throw new ArgumentException("Observation providers require unique IDs.", nameof(configured));
        providers = index;
    }

    public Task<TransactionObservation?> InspectAsync(TransactionObservationScope scope,
        string transactionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.NetworkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        ct.ThrowIfCancellationRequested();
        return providers.TryGetValue(scope.ProviderId, out var provider)
            ? provider.InspectAsync(scope.NetworkId, transactionId, ct)
            : Task.FromException<TransactionObservation?>(new NotSupportedException("Selected observation provider is not registered."));
    }
}
