namespace VaultEngine.Stellar.Services;

/// <summary>
/// Implements the account-strategy dispatch over the existing, independent Stellar
/// ledger sessions. Inspection is read-only. Neither strategy invents a "new account"
/// operation: HD child allocation is part of the approved record-publication plan,
/// whereas linked-account expansion requires its own fee quote and approval.
/// </summary>
public abstract class ConnectedStellarAccountStrategy(
    VaultConnectedStellarAccountInventory inventory,
    VaultNetworkSelection network)
    : IVaultAccountProvisioningStrategy
{
    public const string StellarChain = "stellar";
    public const string HdMode = "mnemonic-hd";
    public const string LinkedMode = "secret-key-chain";

    public string BlockchainId => StellarChain;
    public abstract string StrategyId { get; }
    protected abstract VaultCredentialMode CredentialMode { get; }

    public async Task<VaultAccountInspection> InspectConnectedAsync(
        VaultAccountRequest request, CancellationToken cancellationToken = default)
    {
        ValidateScope(request);
        var result = await inventory.InspectAsync(CredentialMode, cancellationToken);
        if (!string.Equals(result.RootAccountId, request.VaultId, StringComparison.Ordinal))
            throw new InvalidOperationException("Connected account root does not match requested vault.");
        // Recheck selection after asynchronous inspection to avoid returning state from
        // another selection. The underlying ledger session must independently verify
        // its network endpoint/passphrase; this UI selection is not that proof.
        ValidateScope(request);
        return new VaultAccountInspection(
            BlockchainId, request.NetworkId, request.VaultId, StrategyId,
            result.RootAccountId, result.TailAccountId, result.AccountCount);
    }

    protected void ValidateScope(VaultAccountRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.BlockchainId != BlockchainId || request.StrategyId != StrategyId ||
            string.IsNullOrWhiteSpace(request.NetworkId) ||
            !string.Equals(request.NetworkId, network.Network, StringComparison.Ordinal) ||
            !StellarAccountAddress.IsValid(request.VaultId))
            throw new InvalidOperationException("Account request strategy, selected network or root does not match.");
    }

    public Task<VaultAccountHandle> CreateNextAsync(
        VaultAccountRequest request, CancellationToken cancellationToken = default)
    {
        ValidateScope(request);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            "Account provisioning requires its existing explicit Stellar quote, authorization and confirmed transaction workflow.");
    }

    public Task<IReadOnlyList<VaultAccountHandle>> DiscoverAsync(
        VaultAccountRequest request, CancellationToken cancellationToken = default)
    {
        ValidateScope(request);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            "Individual chain accounts cannot be enumerated from the connected root/tail summary. An authenticated per-node discovery adapter is required.");
    }
}

public sealed class StellarHdAccountStrategy(
    VaultConnectedStellarAccountInventory inventory, VaultNetworkSelection network)
    : ConnectedStellarAccountStrategy(inventory, network)
{
    public override string StrategyId => HdMode;
    protected override VaultCredentialMode CredentialMode => VaultCredentialMode.MnemonicHd;
}

public sealed class StellarLinkedAccountStrategy(
    VaultConnectedStellarAccountInventory inventory, VaultNetworkSelection network)
    : ConnectedStellarAccountStrategy(inventory, network)
{
    public override string StrategyId => LinkedMode;
    protected override VaultCredentialMode CredentialMode => VaultCredentialMode.StellarSecretKeyChain;
}
