namespace SVSAPME.Services;

internal static class BatterySynthesizerRules
{
    public const long RequiredWh = 10_000;
    public const string BatteryPackQualifiedItemId = "(O)787";
    public const int OutputBufferLimit = 1;

    public static readonly IReadOnlyList<MaterialRequirement> Materials =
        new List<MaterialRequirement>
        {
            new("(O)334", 2),
            new("(O)335", 2),
            new("(O)336", 1),
            new("(O)382", 10),
            new("(O)338", 5)
        };

    public static bool CanAssemble(long progressWh, int outputBufferCount, Func<string, int> getAvailableCount)
    {
        return progressWh >= RequiredWh
            && outputBufferCount < OutputBufferLimit
            && Materials.All(material => getAvailableCount(material.QualifiedItemId) >= material.Count);
    }
}

internal sealed record MaterialRequirement(string QualifiedItemId, int Count);
