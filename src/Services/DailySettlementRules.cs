namespace SVSAPME.Services;

internal enum DailySettlementStep
{
    FarmConsumptionAndGrowth,
    EnergyProduction
}

internal static class DailySettlementRules
{
    public static readonly DailySettlementStep[] DayStartedOrder =
        new[]
        {
            DailySettlementStep.FarmConsumptionAndGrowth,
            DailySettlementStep.EnergyProduction
        };

    public static bool FarmConsumesBeforeEnergyProduction()
    {
        return Array.IndexOf(DayStartedOrder, DailySettlementStep.FarmConsumptionAndGrowth)
            < Array.IndexOf(DayStartedOrder, DailySettlementStep.EnergyProduction);
    }

    public static bool CanFarmRunFromDayStartStorage(long startingStoredWh, long requiredFarmWh)
    {
        return Math.Max(0, startingStoredWh) >= Math.Max(0, requiredFarmWh);
    }
}
