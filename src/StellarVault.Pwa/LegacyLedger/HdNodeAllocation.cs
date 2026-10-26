namespace StellarVaultPwa.Core;

public static class HdNodeAllocation
{
    public const int RegistryIndex = 0;
    public const int FirstStorageIndex = 1;

    public static int NextIndex(int highestAllocatedIndex)
    {
        if (highestAllocatedIndex < 0 || highestAllocatedIndex >= int.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(highestAllocatedIndex));
        return checked(highestAllocatedIndex + 1);
    }

    public static bool CanFit(int existingDataEntries, int existingSubentries,
        int newChunks, int dataEntryLimit, bool headerExists)
    {
        if (existingDataEntries < 0 || existingSubentries < 0 || newChunks < 1 || dataEntryLimit < 2)
            return false;
        long additionalEntries = (long)newChunks + (headerExists ? 0 : 1);
        return existingDataEntries + additionalEntries <= dataEntryLimit
            && existingSubentries + additionalEntries <= 1000;
    }
}
