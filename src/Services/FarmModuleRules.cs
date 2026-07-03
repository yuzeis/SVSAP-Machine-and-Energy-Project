using SVSAPME.Content;
using SVSAPME.Models;

namespace SVSAPME.Services;

internal static class FarmModuleRules
{
    public const string SprinklerQualifiedItemId = "(O)599";
    public const string QualitySprinklerQualifiedItemId = "(O)621";
    public const string IridiumSprinklerQualifiedItemId = "(O)645";
    public const string BasicFertilizerQualifiedItemId = "(O)368";
    public const string QualityFertilizerQualifiedItemId = "(O)369";
    public const string DeluxeFertilizerQualifiedItemId = "(O)919";

    private static readonly IReadOnlyDictionary<string, FarmModuleDefinition> Modules =
        new Dictionary<string, FarmModuleDefinition>(StringComparer.Ordinal)
        {
            [SprinklerQualifiedItemId] = new(SprinklerQualifiedItemId, FarmModuleKind.Sprinkler, 4, 1000, 0, 0, 0),
            [QualitySprinklerQualifiedItemId] = new(QualitySprinklerQualifiedItemId, FarmModuleKind.Sprinkler, 8, 1000, 0, 0, 0),
            [IridiumSprinklerQualifiedItemId] = new(IridiumSprinklerQualifiedItemId, FarmModuleKind.Sprinkler, 24, 1000, 0, 0, 0),
            ["(O)" + ModItemCatalog.BasicGrowthLightModule] = new("(O)" + ModItemCatalog.BasicGrowthLightModule, FarmModuleKind.GrowthLight, 0, 950, 10, 0, 0),
            ["(O)" + ModItemCatalog.AdvancedGrowthLightModule] = new("(O)" + ModItemCatalog.AdvancedGrowthLightModule, FarmModuleKind.GrowthLight, 0, 900, 30, 0, 0),
            ["(O)" + ModItemCatalog.IridiumGrowthLightModule] = new("(O)" + ModItemCatalog.IridiumGrowthLightModule, FarmModuleKind.GrowthLight, 0, 800, 80, 0, 0),
            ["(O)" + ModItemCatalog.BasicThermostatModule] = new("(O)" + ModItemCatalog.BasicThermostatModule, FarmModuleKind.Thermostat, 0, 1250, 50, 0, 0),
            ["(O)" + ModItemCatalog.AdvancedThermostatModule] = new("(O)" + ModItemCatalog.AdvancedThermostatModule, FarmModuleKind.Thermostat, 0, 1000, 80, 0, 0),
            ["(O)" + ModItemCatalog.IridiumThermostatModule] = new("(O)" + ModItemCatalog.IridiumThermostatModule, FarmModuleKind.Thermostat, 0, 900, 150, 0, 0),
            ["(O)" + ModItemCatalog.BasicSlowReleaseModule] = new("(O)" + ModItemCatalog.BasicSlowReleaseModule, FarmModuleKind.SlowRelease, 0, 1000, 0, 2, 0),
            ["(O)" + ModItemCatalog.AdvancedSlowReleaseModule] = new("(O)" + ModItemCatalog.AdvancedSlowReleaseModule, FarmModuleKind.SlowRelease, 0, 1000, 0, 4, 0),
            ["(O)" + ModItemCatalog.IridiumSlowReleaseModule] = new("(O)" + ModItemCatalog.IridiumSlowReleaseModule, FarmModuleKind.SlowRelease, 0, 1000, 0, 8, 0)
        };

    private static readonly HashSet<string> Fertilizers =
        new(StringComparer.Ordinal)
        {
            BasicFertilizerQualifiedItemId,
            QualityFertilizerQualifiedItemId,
            DeluxeFertilizerQualifiedItemId
        };

    public static bool TryGetModule(string qualifiedItemId, out FarmModuleDefinition module)
    {
        return Modules.TryGetValue(qualifiedItemId, out module);
    }

    public static bool IsFertilizer(string qualifiedItemId)
    {
        return Fertilizers.Contains(qualifiedItemId);
    }

    public static FarmModuleInstallResult TryInstallModule(FarmMachineState farm, FarmTierInfo tier, string qualifiedItemId)
    {
        if (!TryGetModule(qualifiedItemId, out var module))
            return new(false, false, "This item is not a farm module.");

        NormalizeInstalledModules(farm);
        if (module.Kind == FarmModuleKind.GrowthLight && CountModules(farm, FarmModuleKind.GrowthLight) >= 2)
            return new(false, false, "A farm can install at most two growth lights.");

        if (module.Kind == FarmModuleKind.Thermostat && CountModules(farm, FarmModuleKind.Thermostat) >= 1)
            return new(false, false, "A farm can install at most one thermostat.");

        if (module.Kind == FarmModuleKind.SlowRelease && CountModules(farm, FarmModuleKind.SlowRelease) >= 1)
            return new(false, false, "A farm can install at most one slow-release module.");

        var currentSlots = GetUsedSlots(farm);
        var needsNewSlot = module.Kind != FarmModuleKind.Sprinkler || CountModules(farm, FarmModuleKind.Sprinkler) == 0;
        if (needsNewSlot && currentSlots >= tier.ModuleSlots)
            return new(false, false, "This farm has no free module slot.");

        farm.InstalledModuleQualifiedItemIds.Add(qualifiedItemId);
        RecalculateModuleSnapshot(farm);
        return new(true, true, $"Installed {GetModuleDisplayName(qualifiedItemId)}. Slots used: {GetUsedSlots(farm):N0}/{tier.ModuleSlots:N0}.");
    }

    public static bool CanBindFertilizer(FarmMachineState farm, string fertilizerQualifiedItemId)
    {
        return string.IsNullOrWhiteSpace(farm.BoundFertilizerQualifiedItemId)
            || string.Equals(farm.BoundFertilizerQualifiedItemId, fertilizerQualifiedItemId, StringComparison.Ordinal)
            || SingleBlockFarmRules.CountOccupied(farm) == 0;
    }

    public static int GetUsedSlots(FarmMachineState farm)
    {
        NormalizeInstalledModules(farm);
        var slots = 0;
        if (CountModules(farm, FarmModuleKind.Sprinkler) > 0)
            slots++;

        slots += CountModules(farm, FarmModuleKind.GrowthLight);
        if (CountModules(farm, FarmModuleKind.Thermostat) > 0)
            slots++;

        if (CountModules(farm, FarmModuleKind.SlowRelease) > 0)
            slots++;

        return slots;
    }

    public static IEnumerable<string> GetInstalledModuleItems(FarmMachineState farm)
    {
        NormalizeInstalledModules(farm);
        return farm.InstalledModuleQualifiedItemIds.ToList();
    }

    public static void ClearInstalledModules(FarmMachineState farm)
    {
        farm.InstalledModuleQualifiedItemIds.Clear();
        RecalculateModuleSnapshot(farm);
    }

    public static int CalculateFertilizerUnitsForPlots(int plotCycles, int coveragePerFertilizer)
    {
        if (plotCycles <= 0)
            return 0;

        var coverage = Math.Max(1, coveragePerFertilizer);
        return (plotCycles + coverage - 1) / coverage;
    }

    public static int GetHarvestQuality(int farmingLevel, string fertilizerQualifiedItemId, FarmPlotState plot)
    {
        var chances = GetHarvestQualityChances(farmingLevel, fertilizerQualifiedItemId);
        if (chances.TotalBasisPoints <= 0)
            return 0;

        plot.QualityRollBasisPoints = (plot.QualityRollBasisPoints + 7_919) % 10_000;
        var roll = plot.QualityRollBasisPoints;
        if (roll < chances.IridiumBasisPoints)
            return 4;

        roll -= chances.IridiumBasisPoints;
        if (roll < chances.GoldBasisPoints)
            return 2;

        roll -= chances.GoldBasisPoints;
        return roll < chances.SilverBasisPoints ? 1 : 0;
    }

    public static FarmQualityChances GetHarvestQualityChances(int farmingLevel, string fertilizerQualifiedItemId)
    {
        var level = Math.Clamp(farmingLevel, 0, 10);
        var fertilizerBoost = GetFertilizerQualityBoostLevel(fertilizerQualifiedItemId);
        var goldChance = 0.2 * (level / 10.0)
            + 0.2 * fertilizerBoost * ((level + 2.0) / 12.0)
            + 0.01;
        var silverChance = Math.Min(0.75, goldChance * 2.0);
        var iridiumChance = fertilizerBoost >= 3 ? goldChance / 2.0 : 0.0;
        var effectiveGoldChance = (1.0 - iridiumChance) * goldChance;
        var effectiveSilverChance = (1.0 - iridiumChance)
            * (1.0 - goldChance)
            * (fertilizerBoost >= 3 ? 1.0 : silverChance);

        return new FarmQualityChances(
            ToBasisPoints(iridiumChance),
            ToBasisPoints(effectiveGoldChance),
            ToBasisPoints(effectiveSilverChance));
    }

    public static int GetFertilizerQualityBoostLevel(string fertilizerQualifiedItemId)
    {
        return fertilizerQualifiedItemId switch
        {
            DeluxeFertilizerQualifiedItemId => 3,
            QualityFertilizerQualifiedItemId => 2,
            BasicFertilizerQualifiedItemId => 1,
            _ => 0
        };
    }

    public static string DescribeModules(FarmMachineState farm, FarmTierInfo tier)
    {
        NormalizeInstalledModules(farm);
        var sprinklers = CountModules(farm, FarmModuleKind.Sprinkler);
        var lights = CountModules(farm, FarmModuleKind.GrowthLight);
        var thermostat = CountModules(farm, FarmModuleKind.Thermostat);
        var slow = CountModules(farm, FarmModuleKind.SlowRelease);
        return $"Farm modules {GetUsedSlots(farm):N0}/{tier.ModuleSlots:N0}: sprinklers={sprinklers:N0}, lights={lights:N0}, thermostat={thermostat:N0}, slow-release={slow:N0}; seeds={farm.InternalSeedCount:N0}, fertilizer={farm.InternalFertilizerCount:N0}.";
    }

    public static void RecalculateModuleSnapshot(FarmMachineState farm)
    {
        NormalizeInstalledModules(farm);
        var sprinklerCoveredPlots = 0;
        var lightFactors = new List<int>();
        var thermostatFactor = 1000;
        var thermostatWh = 0L;
        var hasThermostat = false;
        var slowCoverage = 1;
        var slowModule = string.Empty;

        foreach (var installed in farm.InstalledModuleQualifiedItemIds)
        {
            if (!TryGetModule(installed, out var module))
                continue;

            switch (module.Kind)
            {
                case FarmModuleKind.Sprinkler:
                    sprinklerCoveredPlots += module.CoveredPlots;
                    break;
                case FarmModuleKind.GrowthLight:
                    if (lightFactors.Count < 2)
                        lightFactors.Add(module.FactorPermille);
                    break;
                case FarmModuleKind.Thermostat:
                    if (!hasThermostat)
                    {
                        hasThermostat = true;
                        thermostatFactor = module.FactorPermille;
                        thermostatWh = module.WhPerPlot;
                        farm.ThermostatModuleQualifiedItemId = installed;
                    }

                    break;
                case FarmModuleKind.SlowRelease:
                    if (string.IsNullOrWhiteSpace(slowModule))
                    {
                        slowCoverage = Math.Max(1, module.FertilizerCoverage);
                        slowModule = installed;
                    }

                    break;
            }
        }

        var lightProduct = 1000L;
        foreach (var factor in lightFactors)
            lightProduct = Math.Max(1, lightProduct * factor / 1000);

        farm.SprinklerCoveredPlots = sprinklerCoveredPlots;
        farm.GrowthLightFactorPermilles = lightFactors;
        farm.LightFactorPermille = (int)Math.Clamp(lightProduct, 1, 1000);
        farm.HasThermostat = hasThermostat;
        farm.ThermostatFactorPermille = thermostatFactor;
        if (!hasThermostat)
            farm.ThermostatModuleQualifiedItemId = string.Empty;

        farm.SlowReleaseCoveragePerFertilizer = slowCoverage;
        farm.SlowReleaseModuleQualifiedItemId = slowModule;
        farm.ChamberModuleWhPerPlot = lightFactors.Sum(factor => GetGrowthLightWhPerPlot(factor)) + thermostatWh;
    }

    private static void NormalizeInstalledModules(FarmMachineState farm)
    {
        if (farm.InstalledModuleQualifiedItemIds.Count == 0)
        {
            if (farm.SprinklerCoveredPlots > 0)
            {
                farm.InstalledModuleQualifiedItemIds.AddRange(ExpandLegacySprinklers(farm.SprinklerCoveredPlots));
            }

            if (farm.LightFactorPermille != 1000)
            {
                farm.InstalledModuleQualifiedItemIds.Add("(O)" + ModItemCatalog.IridiumGrowthLightModule);
            }

            if (farm.HasThermostat)
            {
                farm.InstalledModuleQualifiedItemIds.Add(string.IsNullOrWhiteSpace(farm.ThermostatModuleQualifiedItemId)
                    ? "(O)" + ModItemCatalog.AdvancedThermostatModule
                    : farm.ThermostatModuleQualifiedItemId);
            }

            if (!string.IsNullOrWhiteSpace(farm.SlowReleaseModuleQualifiedItemId))
                farm.InstalledModuleQualifiedItemIds.Add(farm.SlowReleaseModuleQualifiedItemId);
        }

        farm.InstalledModuleQualifiedItemIds = farm.InstalledModuleQualifiedItemIds
            .Where(qualifiedItemId => Modules.ContainsKey(qualifiedItemId))
            .ToList();
    }

    private static IEnumerable<string> ExpandLegacySprinklers(int coveredPlots)
    {
        var remaining = Math.Max(0, coveredPlots);
        while (remaining >= 24)
        {
            yield return IridiumSprinklerQualifiedItemId;
            remaining -= 24;
        }

        while (remaining >= 8)
        {
            yield return QualitySprinklerQualifiedItemId;
            remaining -= 8;
        }

        while (remaining > 0)
        {
            yield return SprinklerQualifiedItemId;
            remaining -= 4;
        }
    }

    private static int CountModules(FarmMachineState farm, FarmModuleKind kind)
    {
        return farm.InstalledModuleQualifiedItemIds.Count(qualifiedItemId => TryGetModule(qualifiedItemId, out var module) && module.Kind == kind);
    }

    private static long GetGrowthLightWhPerPlot(int factorPermille)
    {
        return factorPermille switch
        {
            950 => 10,
            900 => 30,
            800 => 80,
            _ => 0
        };
    }

    private static int ToBasisPoints(double chance)
    {
        return Math.Clamp(
            (int)Math.Round(Math.Clamp(chance, 0.0, 1.0) * 10_000, MidpointRounding.AwayFromZero),
            0,
            10_000);
    }

    private static string GetModuleDisplayName(string qualifiedItemId)
    {
        return qualifiedItemId switch
        {
            SprinklerQualifiedItemId => "Sprinkler",
            QualitySprinklerQualifiedItemId => "Quality Sprinkler",
            IridiumSprinklerQualifiedItemId => "Iridium Sprinkler",
            "(O)" + ModItemCatalog.BasicGrowthLightModule => "Basic Growth Light",
            "(O)" + ModItemCatalog.AdvancedGrowthLightModule => "Advanced Growth Light",
            "(O)" + ModItemCatalog.IridiumGrowthLightModule => "Iridium Growth Light",
            "(O)" + ModItemCatalog.BasicThermostatModule => "Basic Thermostat",
            "(O)" + ModItemCatalog.AdvancedThermostatModule => "Advanced Thermostat",
            "(O)" + ModItemCatalog.IridiumThermostatModule => "Iridium Thermostat",
            "(O)" + ModItemCatalog.BasicSlowReleaseModule => "Basic Slow-Release",
            "(O)" + ModItemCatalog.AdvancedSlowReleaseModule => "Advanced Slow-Release",
            "(O)" + ModItemCatalog.IridiumSlowReleaseModule => "Iridium Slow-Release",
            _ => qualifiedItemId
        };
    }
}

internal enum FarmModuleKind
{
    Sprinkler,
    GrowthLight,
    Thermostat,
    SlowRelease
}

internal readonly record struct FarmModuleDefinition(
    string QualifiedItemId,
    FarmModuleKind Kind,
    int CoveredPlots,
    int FactorPermille,
    long WhPerPlot,
    int FertilizerCoverage,
    int Reserved);

internal readonly record struct FarmModuleInstallResult(
    bool Success,
    bool ConsumeHeldItem,
    string Message);

internal readonly record struct FarmQualityChances(
    int IridiumBasisPoints,
    int GoldBasisPoints,
    int SilverBasisPoints)
{
    public int TotalBasisPoints => Math.Clamp(IridiumBasisPoints + GoldBasisPoints + SilverBasisPoints, 0, 10_000);
}
