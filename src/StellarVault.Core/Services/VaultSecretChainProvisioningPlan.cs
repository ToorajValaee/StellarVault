namespace StellarVault.Core.Services;

/// <summary>
/// Quote for a future atomic, funded-child S-key chain extension.
/// This is a reserve model, not a signed transaction, authorization grant,
/// network submission, or guarantee that funds are presently available.
/// All values are integer stroops and must be populated using fresh network
/// base-reserve and fee observations immediately before the user's approval.
/// </summary>
public sealed record VaultSecretChainProvisioningPlan(
    long BaseReserveStroops,
    long BaseFeeStroops,
    long NewChildStartingBalanceStroops,
    long PreviousTailTopUpStroops,
    long AdditionalTailReserveStroops,
    int OperationCount,
    long EstimatedNetworkFeeStroops,
    long MinimumRootSpendableStroops,
    long IncrementalLedgerReserveStroops,
    bool PreviousTailIsRoot)
{
    public decimal NewChildStartingBalanceXlm => ToXlm(NewChildStartingBalanceStroops);
    public decimal PreviousTailTopUpXlm => ToXlm(PreviousTailTopUpStroops);
    public decimal EstimatedFeeXlm => ToXlm(EstimatedNetworkFeeStroops);
    public decimal MinimumRootSpendableXlm => ToXlm(MinimumRootSpendableStroops);
    public decimal IncrementalLedgerReserveXlm => ToXlm(IncrementalLedgerReserveStroops);
    private static decimal ToXlm(long stroops) => stroops / 10_000_000m;
}

/// <summary>
/// Explicitly plans one child with the root key authorized as signer and the
/// child's default master key disabled. Link publication consumes one new
/// ManageData entry on the previous tail. A child tail is topped up to cover
/// that reserve; other existing obligations must still be checked on-chain.
/// This does not submit or derive any account keys.
/// </summary>
public static class VaultSecretChainProvisioningPlanner
{
    public static VaultSecretChainProvisioningPlan PlanFundedChild(
        long baseReserveStroops, long baseFeeStroops,
        bool previousTailIsRoot, long safetyBufferStroops = 0)
    {
        if (baseReserveStroops <= 0 || baseFeeStroops <= 0 || safetyBufferStroops < 0)
            throw new ArgumentOutOfRangeException(nameof(baseReserveStroops),
                "Reserve, fee and safety buffer must be valid nonnegative network values.");

        checked
        {
            // New account minimum = 2 reserve units, authorized extra signer = 1.
            long childFunding = 3 * baseReserveStroops;
            // A new root-authenticated link is a distinct account data entry.
            long pointerReserve = baseReserveStroops;
            long tailTopUp = previousTailIsRoot ? 0 : pointerReserve;
            int operations = previousTailIsRoot ? 3 : 4;
            long fee = operations * baseFeeStroops;
            long rootSpendable = childFunding + tailTopUp +
                (previousTailIsRoot ? pointerReserve : 0) + fee + safetyBufferStroops;
            long incrementalReserve = childFunding + pointerReserve;
            return new VaultSecretChainProvisioningPlan(
                baseReserveStroops, baseFeeStroops, childFunding, tailTopUp,
                pointerReserve, operations, fee, rootSpendable,
                incrementalReserve, previousTailIsRoot);
        }
    }
}
