namespace StellarVaultPwa.Core;

/// <summary>
/// Stellar native-balance spendability in stroops. num_sponsoring and
/// num_sponsored are reserve units, not XLM amounts.
/// Read these fields from the current Horizon account response.
/// </summary>
public static class SponsoredBalanceCalculator
{
    public sealed record Snapshot(
        long NativeBalanceStroops,
        long BaseReserveStroops,
        int SubentryCount,
        int NumSponsoring,
        int NumSponsored,
        long SellingLiabilitiesStroops);

    public sealed record Result(
        long MinimumBalanceStroops,
        long SpendableStroops,
        long AvailableForNewSponsorshipStroops);

    public static Result Calculate(
        Snapshot account,
        int AdditionalSponsoredReserveUnits = 0,
        long ExpectedFeesStroops = 0,
        long SafetyBufferStroops = 0)
    {
        if (account.NativeBalanceStroops < 0 ||
            account.BaseReserveStroops <= 0 ||
            account.SubentryCount < 0 ||
            account.NumSponsoring < 0 ||
            account.NumSponsored < 0 ||
            account.SellingLiabilitiesStroops < 0 ||
            AdditionalSponsoredReserveUnits < 0 ||
            ExpectedFeesStroops < 0 ||
            SafetyBufferStroops < 0)
            throw new ArgumentOutOfRangeException(nameof(account), "Negative balances, fees or reserve counts are invalid.");

        checked
        {
            var reserveUnits = 2L + account.SubentryCount +
                               account.NumSponsoring - account.NumSponsored;
            if (reserveUnits < 0)
                throw new InvalidDataException("Invalid sponsored reserve-unit counts.");

            var minimum = reserveUnits * account.BaseReserveStroops +
                          account.SellingLiabilitiesStroops;
            var spendable = Math.Max(0L, account.NativeBalanceStroops - minimum);
            var projectedCost = AdditionalSponsoredReserveUnits * account.BaseReserveStroops +
                                ExpectedFeesStroops + SafetyBufferStroops;
            return new Result(minimum, spendable,
                Math.Max(0L, spendable - projectedCost));
        }
    }
}
