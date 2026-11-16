namespace VaultEngine.Stellar.Services;

/// <summary>
/// Logical account handle: never carries an S-key, mnemonic, derived seed, or signer material.
/// Network and chain scope must be validated before using an account for any operation.
/// </summary>
public sealed record VaultAccountHandle(
    string BlockchainId,
    string NetworkId,
    string Address,
    string StrategyId,
    long? Sequence);

/// <summary>Chain-neutral, read-only inspection result; contains no signing authority.</summary>
public sealed record VaultAccountInspection(
    string BlockchainId, string NetworkId, string VaultId, string StrategyId,
    string RootAddress, string TailAddress, int AccountCount);

public sealed record VaultAccountRequest(
    string BlockchainId,
    string NetworkId,
    string VaultId,
    string StrategyId);

/// <summary>
/// Creates or discovers accounts for a specific chain and account strategy.
/// An implementation must not return a linked child as ready until the requisite
/// on-chain creation, signer authorization, and confirmations have been checked.
/// </summary>
public interface IVaultAccountProvisioningStrategy
{
    string BlockchainId { get; }
    string StrategyId { get; }
    Task<VaultAccountHandle> CreateNextAsync(VaultAccountRequest request, CancellationToken cancellationToken = default);
    Task<VaultAccountInspection> InspectConnectedAsync(VaultAccountRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VaultAccountHandle>> DiscoverAsync(VaultAccountRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Routes account requests by explicit chain and strategy; no mnemonic/secret-key guessing.</summary>
public sealed class VaultAccountStrategyFactory
{
    private readonly IReadOnlyDictionary<(string Chain, string Strategy), IVaultAccountProvisioningStrategy> _strategies;

    public VaultAccountStrategyFactory(IEnumerable<IVaultAccountProvisioningStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        var index = new Dictionary<(string, string), IVaultAccountProvisioningStrategy>();
        foreach (var strategy in strategies)
        {
            if (string.IsNullOrWhiteSpace(strategy.BlockchainId) || string.IsNullOrWhiteSpace(strategy.StrategyId) ||
                !index.TryAdd((strategy.BlockchainId, strategy.StrategyId), strategy))
                throw new ArgumentException("Account strategies require unique nonempty blockchain/strategy IDs.");
        }
        _strategies = index;
    }

    public IVaultAccountProvisioningStrategy Resolve(VaultAccountRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.BlockchainId) ||
            string.IsNullOrWhiteSpace(request.NetworkId) ||
            string.IsNullOrWhiteSpace(request.VaultId) ||
            string.IsNullOrWhiteSpace(request.StrategyId))
            throw new ArgumentException("Account request must specify chain, network, vault and strategy.");
        return _strategies.TryGetValue((request.BlockchainId, request.StrategyId), out var strategy)
            ? strategy
            : throw new NotSupportedException("Account strategy is not registered for the requested blockchain.");
    }

    public Task<VaultAccountInspection> InspectConnectedAsync(VaultAccountRequest request, CancellationToken ct = default) =>
        Resolve(request).InspectConnectedAsync(request, ct);

    public Task<VaultAccountHandle> CreateNextAsync(VaultAccountRequest request, CancellationToken ct = default) =>
        Resolve(request).CreateNextAsync(request, ct);

    public Task<IReadOnlyList<VaultAccountHandle>> DiscoverAsync(VaultAccountRequest request, CancellationToken ct = default) =>
        Resolve(request).DiscoverAsync(request, ct);
}

/// <summary>
/// Chain-specific operations are deliberately separate from account generation.
/// EVM and Stellar transactions, permissions, finality, and storage are not interchangeable.
/// </summary>
public interface IVaultBlockchainAdapter
{
    string BlockchainId { get; }
    Task<VaultChainCapabilities> GetCapabilitiesAsync(string networkId, CancellationToken cancellationToken = default);
}

public sealed record VaultChainCapabilities(
    bool SupportsNativeAccountSigners,
    bool SupportsNativeAccountData,
    bool RequiresContractForProgrammableAuthorization);
