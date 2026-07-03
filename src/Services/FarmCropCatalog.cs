using StardewValley;
using StardewValley.GameData.Crops;

namespace SVSAPME.Services;

internal static class FarmCropCatalog
{
    private static readonly IReadOnlyDictionary<string, FarmCropSpec> FallbackCropsBySeed =
        new Dictionary<string, FarmCropSpec>(StringComparer.Ordinal)
        {
            ["(O)472"] = new(
                SeedQualifiedItemId: "(O)472",
                DisplayName: "Parsnip",
                HarvestQualifiedItemId: "(O)24",
                BaseGrowthDays: 4,
                RegrowDays: -1,
                HarvestMinStack: 1,
                HarvestMaxStack: 1,
                ExtraHarvestChance: 0m,
                Seasons: new[] { "spring" }),
            ["(O)481"] = new(
                SeedQualifiedItemId: "(O)481",
                DisplayName: "Blueberry",
                HarvestQualifiedItemId: "(O)258",
                BaseGrowthDays: 13,
                RegrowDays: 4,
                HarvestMinStack: 3,
                HarvestMaxStack: 3,
                ExtraHarvestChance: 0.02m,
                Seasons: new[] { "summer" }),
            ["(O)499"] = new(
                SeedQualifiedItemId: "(O)499",
                DisplayName: "Ancient Fruit",
                HarvestQualifiedItemId: "(O)454",
                BaseGrowthDays: 28,
                RegrowDays: 7,
                HarvestMinStack: 1,
                HarvestMaxStack: 1,
                ExtraHarvestChance: 0m,
                Seasons: new[] { "spring", "summer", "fall" }),
            ["(O)433"] = new(
                SeedQualifiedItemId: "(O)433",
                DisplayName: "Coffee Bean",
                HarvestQualifiedItemId: "(O)433",
                BaseGrowthDays: 10,
                RegrowDays: 2,
                HarvestMinStack: 4,
                HarvestMaxStack: 4,
                ExtraHarvestChance: 0m,
                Seasons: new[] { "spring", "summer" })
        };

    public static bool TryGetBySeed(string qualifiedSeedId, out FarmCropSpec crop)
    {
        if (TryLoadGameCrops(out var crops)
            && crops.TryGetValue(NormalizeQualifiedObjectId(qualifiedSeedId), out crop!))
        {
            return true;
        }

        return FallbackCropsBySeed.TryGetValue(qualifiedSeedId, out crop!);
    }

    public static IEnumerable<FarmCropSpec> AcceptanceCrops
        => TryLoadGameCrops(out var crops)
            ? crops.Values
            : FallbackCropsBySeed.Values;

    private static bool TryLoadGameCrops(out IReadOnlyDictionary<string, FarmCropSpec> crops)
    {
        crops = new Dictionary<string, FarmCropSpec>();
        try
        {
            if (Game1.content is null)
                return false;

            var raw = Game1.content.Load<Dictionary<string, CropData>>("Data/Crops");
            var parsed = new Dictionary<string, FarmCropSpec>(StringComparer.Ordinal);
            foreach (var pair in raw)
            {
                if (TryParseCrop(pair.Key, pair.Value, out var crop))
                    parsed[crop.SeedQualifiedItemId] = crop;
            }

            if (parsed.Count == 0)
                return false;

            crops = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseCrop(string seedItemId, CropData data, out FarmCropSpec crop)
    {
        crop = null!;
        var seedQualifiedId = NormalizeQualifiedObjectId(seedItemId);
        var harvestQualifiedId = NormalizeQualifiedObjectId(data.HarvestItemId);
        if (string.IsNullOrWhiteSpace(seedQualifiedId)
            || string.IsNullOrWhiteSpace(harvestQualifiedId)
            || data.DaysInPhase is null
            || data.DaysInPhase.Count == 0)
        {
            return false;
        }

        var baseGrowthDays = data.DaysInPhase.Sum(day => Math.Max(0, day));
        if (baseGrowthDays <= 0)
            return false;

        var seasons = (data.Seasons ?? new())
            .Select(season => season.ToString().ToLowerInvariant())
            .Where(season => !string.IsNullOrWhiteSpace(season))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (seasons.Count == 0)
            return false;

        crop = new FarmCropSpec(
            SeedQualifiedItemId: seedQualifiedId,
            DisplayName: seedQualifiedId,
            HarvestQualifiedItemId: harvestQualifiedId,
            BaseGrowthDays: baseGrowthDays,
            RegrowDays: data.RegrowDays,
            HarvestMinStack: Math.Max(1, data.HarvestMinStack),
            HarvestMaxStack: Math.Max(Math.Max(1, data.HarvestMinStack), data.HarvestMaxStack),
            ExtraHarvestChance: Math.Max(0m, (decimal)data.ExtraHarvestChance),
            Seasons: seasons);
        return true;
    }

    private static string NormalizeQualifiedObjectId(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return string.Empty;

        var trimmed = itemId.Trim();
        return trimmed.StartsWith("(O)", StringComparison.Ordinal)
            ? trimmed
            : "(O)" + trimmed;
    }
}

internal sealed record FarmCropSpec(
    string SeedQualifiedItemId,
    string DisplayName,
    string HarvestQualifiedItemId,
    int BaseGrowthDays,
    int RegrowDays,
    int HarvestMinStack,
    int HarvestMaxStack,
    decimal ExtraHarvestChance,
    IReadOnlyList<string> Seasons);

internal readonly record struct FarmModuleSnapshot(
    int SprinklerCoveredPlots,
    decimal LightFactorProduct,
    bool HasThermostat,
    decimal ThermostatFactor,
    long ChamberModuleWhPerPlot,
    int FertilizerCoveragePerFertilizer);

internal readonly record struct FarmDailyPlan(
    int ExistingOccupiedPlots,
    int PlannedSeedCount,
    int PlannedFertilizerCount,
    int ChargedOccupiedPlots,
    long RequiredWh,
    bool CanGrowToday);

internal readonly record struct FarmDailyResult(
    FarmDailyOutcome Outcome,
    int PlantedFromInternal,
    int PlantedFromNetwork,
    int HarvestedPlots,
    int OutputStacksAdded);

internal enum FarmDailyOutcome
{
    None,
    Frozen,
    Applied
}
