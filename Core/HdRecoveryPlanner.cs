namespace StellarVaultPwa.Core;

/// <summary>
/// Plans deterministic recovery scans. Account existence must be verified
/// through Horizon; missing accounts do not terminate the scan.
/// </summary>
public static class HdRecoveryPlanner
{
    public const int DefaultGapLimit = 20;
    public const int MaximumGapLimit = 10_000;

    public static IEnumerable<int> Enumerate(int highestRegistryIndex, int gapLimit = DefaultGapLimit)
    {
        if (highestRegistryIndex < 0 || highestRegistryIndex >= int.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(highestRegistryIndex));
        if (gapLimit < 1 || gapLimit > MaximumGapLimit ||
            (long)highestRegistryIndex + gapLimit > int.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(gapLimit));

        // Do not stop at the first missing index. Scan past the registry's
        // highest known index to reconcile interrupted allocations.
        var end = checked(highestRegistryIndex + gapLimit);
        for (var index = HdNodeAllocation.FirstStorageIndex; index <= end; index++)
            yield return index;
    }

    public static bool NeedsRegistryRepair(
        int highestRegistryIndex,
        IEnumerable<int> observedFundedIndexes)
    {
        if (highestRegistryIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(highestRegistryIndex));
        return observedFundedIndexes.Any(index => index > highestRegistryIndex);
    }
}
