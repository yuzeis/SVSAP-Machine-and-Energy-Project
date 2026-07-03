namespace SVSAPME.Services;

internal static class BatteryDischargerRules
{
    public const string BatteryPackQualifiedItemId = "(O)787";
    public const long DefaultOutputWh = 8_000;

    public static bool CanDischarge(bool enabled, int availableBatteryPacks, long storedWh, long capacityWh, long outputWh)
    {
        return enabled
            && availableBatteryPacks > 0
            && outputWh > 0
            && Math.Max(0, capacityWh) - Math.Clamp(storedWh, 0, Math.Max(0, capacityWh)) >= outputWh;
    }
}
