using StardewValley;

namespace SVSAPME.Services;

internal static class EnergyProductionRules
{
    public static long GetSolarPanelWh(bool isOutdoors, bool isRaining, bool isLightning, Season season)
    {
        if (!isOutdoors)
            return 0;

        if (isRaining || isLightning)
            return 200;

        return season == Season.Winter ? 1000 : 1500;
    }

    public static long GetLightningCapacitorWh(bool isOutdoors, bool isLightning)
    {
        return isOutdoors && isLightning ? 6000 : 0;
    }
}
