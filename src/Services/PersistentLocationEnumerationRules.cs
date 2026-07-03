using StardewValley;

namespace SVSAPME.Services;

internal static class PersistentLocationEnumerationRules
{
    public static bool RebuildUsesUtilityForEachLocationDefaultOverload => true;
    public static bool DefaultForEachLocationIncludesInteriors => true;
    public static bool DefaultForEachLocationIncludesGeneratedLocations => false;
    public static bool RouteTickUsesMachinePositionCacheOnly => true;

    private static readonly string[] TemporaryLocationPrefixes =
    {
        "UndergroundMine",
        "VolcanoDungeon"
    };

    public static bool ShouldRegisterLocation(GameLocation? location)
    {
        return location is not null && ShouldRegisterLocationName(location.NameOrUniqueName);
    }

    public static bool ShouldRegisterLocationName(string? locationName)
    {
        if (string.IsNullOrWhiteSpace(locationName))
            return false;

        return !TemporaryLocationPrefixes.Any(prefix => locationName.StartsWith(prefix, StringComparison.Ordinal));
    }
}
