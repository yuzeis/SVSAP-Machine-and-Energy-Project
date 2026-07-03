namespace SVSAPME.Services;

internal static class ElectricMachineRules
{
    public static readonly long FurnaceWhPerRun = 500;
    public static readonly long GeodeCrusherWhPerRun = 600;
    public const string CoalQualifiedItemId = "(O)382";

    public static readonly IReadOnlyList<ElectricFurnaceRecipe> FurnaceRecipes =
    new List<ElectricFurnaceRecipe>
    {
        new("(O)378", 5, "(O)334", 1, 30),
        new("(O)380", 5, "(O)335", 1, 120),
        new("(O)384", 5, "(O)336", 1, 300),
        new("(O)386", 5, "(O)337", 1, 480),
        new("(O)909", 5, "(O)910", 1, 600),
        new("(O)80", 1, "(O)338", 1, 90),
        new("(O)82", 1, "(O)338", 3, 90)
    };

    public static readonly IReadOnlyList<string> KnownGeodeQualifiedItemIds =
    new[]
    {
        "(O)535",
        "(O)536",
        "(O)537",
        "(O)749",
        "(O)275",
        "(O)791"
    };

    public static bool TryGetFurnaceRecipe(string qualifiedInputItemId, out ElectricFurnaceRecipe recipe)
    {
        recipe = FurnaceRecipes.FirstOrDefault(entry => entry.InputQualifiedItemId == qualifiedInputItemId)!;
        return recipe is not null;
    }

    public static int GetPoweredMinutes(int prototypeMinutes)
    {
        return Math.Max(1, (int)Math.Ceiling(Math.Max(1, prototypeMinutes) / 2m));
    }
}

internal sealed record ElectricFurnaceRecipe(
    string InputQualifiedItemId,
    int InputCount,
    string OutputQualifiedItemId,
    int OutputCount,
    int PrototypeMinutes);
