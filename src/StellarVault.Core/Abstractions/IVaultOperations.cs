using StellarVaultPwa.Core;
namespace StellarVault.Core.Abstractions;
public interface IVaultOperations : IVaultSessionStatus
{
    bool NeedsRegistryInitialization { get; }
    VaultReadResult? Snapshot { get; }
    VaultNetworkSettings? Network { get; }
    string? RootAccountId { get; }
    Task UnlockAsync(VaultNetworkSettings settings, string secret, string bip39Passphrase = "");
    Task LockAsync();
    Task InitializeHdRegistryAsync();
    Task RefreshAsync();
    Task<RootTreasurySnapshot> InspectRootTreasuryAsync(int additionalSponsoredUnits = 0, long expectedFeeStroops = 0, long safetyBufferStroops = 0);
    Task<VaultCostEstimate> EstimateAddAsync(VaultRecord record);
    Task<VaultCostEstimate> EstimateUpdateAsync(VaultRecordLocation existing, VaultRecord record);
    Task<VaultCostEstimate> EstimateDeleteAsync(VaultRecordLocation existing);
    Task<string> AddAsync(VaultRecord record);
    Task<VaultMutationResult> UpdateAsync(VaultRecordLocation existing, VaultRecord record);
    Task<VaultMutationResult> DeleteAsync(VaultRecordLocation existing);
}
