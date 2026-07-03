namespace SVSAPME.Services;

internal static class FarmGrowthRules
{
    private const decimal Scale = 1_000_000m;

    public static int GetConstantModuleMaturityDays(int baseDays, decimal lightFactorProduct, decimal temperatureFactor)
    {
        if (baseDays <= 0)
            return 1;

        var adjusted = baseDays * lightFactorProduct * temperatureFactor;
        return Math.Max(1, (int)Math.Ceiling(adjusted));
    }

    public static long GetDailyProgressUnits(decimal lightFactorProduct, decimal temperatureFactor)
    {
        var divisor = lightFactorProduct * temperatureFactor;
        if (divisor <= 0)
            throw new ArgumentOutOfRangeException(nameof(lightFactorProduct), "Growth factors must be positive.");

        return (long)Math.Floor(Scale / divisor);
    }

    public static long GetRequiredProgressUnits(int baseDays)
    {
        return Math.Max(1, baseDays) * (long)Scale;
    }

    public static bool IsMature(long progressUnits, int baseDays)
    {
        return progressUnits >= GetRequiredProgressUnits(baseDays);
    }

    public static long GetDailyBaseEnergyWh(int occupiedPlots)
    {
        return Math.Max(0, occupiedPlots) * 30L;
    }

    public static long GetDailyWaterEnergyWh(int coveredOccupiedPlots, int uncoveredOccupiedPlots)
    {
        var coveredWh = Math.Max(0, coveredOccupiedPlots) * 5L;
        var uncoveredWh = Math.Max(0, uncoveredOccupiedPlots) * 20L;
        return coveredWh + uncoveredWh;
    }
}
