namespace SVSAPME.Services;

internal static class CarbonGeneratorFuelRules
{
    public const long CoalWh = 350;

    private static readonly IReadOnlyDictionary<string, long> ExtendedFuelWh =
        new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["(O)388"] = 40,
            ["(O)709"] = 250,
            ["(O)771"] = 10,
            ["(O)92"] = 5
        };

    public static bool TryGetFuelWh(string qualifiedItemId, bool allowExtended, out long wh)
    {
        if (qualifiedItemId == "(O)382")
        {
            wh = CoalWh;
            return true;
        }

        if (allowExtended && ExtendedFuelWh.TryGetValue(qualifiedItemId, out wh))
            return true;

        wh = 0;
        return false;
    }
}
