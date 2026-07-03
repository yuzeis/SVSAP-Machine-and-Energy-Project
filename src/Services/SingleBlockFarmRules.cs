using SVSAPME.Models;

namespace SVSAPME.Services;

internal static class SingleBlockFarmRules
{
    public static FarmDailyPlan PlanDay(
        FarmMachineState farm,
        FarmTierInfo tier,
        FarmCropSpec crop,
        FarmModuleSnapshot modules,
        int availableNetworkSeeds,
        int availableNetworkFertilizer,
        string season,
        double energyMultiplier)
    {
        var existing = CountOccupied(farm);
        var canGrowToday = CanGrowToday(crop, modules, season);
        var emptyPlots = Math.Max(0, tier.Plots - existing);
        var availableSeeds = Math.Max(0, farm.InternalSeedCount) + Math.Max(0, availableNetworkSeeds);
        var plannedSeeds = canGrowToday ? Math.Min(emptyPlots, availableSeeds) : 0;
        var plannedFertilizer = 0;
        if (plannedSeeds > 0 && !string.IsNullOrWhiteSpace(farm.BoundFertilizerQualifiedItemId))
        {
            var desiredFertilizer = FarmModuleRules.CalculateFertilizerUnitsForPlots(plannedSeeds, modules.FertilizerCoveragePerFertilizer);
            var availableFertilizer = Math.Max(0, farm.InternalFertilizerCount) + Math.Max(0, availableNetworkFertilizer);
            plannedFertilizer = Math.Min(desiredFertilizer, availableFertilizer);
        }

        var chargedOccupied = existing + plannedSeeds;
        var covered = Math.Min(Math.Max(0, modules.SprinklerCoveredPlots), chargedOccupied);
        var uncovered = Math.Max(0, chargedOccupied - covered);
        var baseWh = FarmGrowthRules.GetDailyBaseEnergyWh(chargedOccupied);
        var waterWh = FarmGrowthRules.GetDailyWaterEnergyWh(covered, uncovered);
        var moduleWh = Math.Max(0, modules.ChamberModuleWhPerPlot) * chargedOccupied;
        var multiplier = energyMultiplier <= 0 ? 0 : energyMultiplier;
        var required = checked((long)Math.Round((baseWh + waterWh + moduleWh) * multiplier, MidpointRounding.AwayFromZero));
        return new FarmDailyPlan(existing, plannedSeeds, plannedFertilizer, chargedOccupied, required, canGrowToday);
    }

    public static FarmDailyResult ApplyFrozenDay()
    {
        return new FarmDailyResult(FarmDailyOutcome.Frozen, 0, 0, 0, 0);
    }

    public static FarmDailyResult ApplyPaidDay(
        FarmMachineState farm,
        IList<BufferedItemStack> outputBuffer,
        FarmTierInfo tier,
        FarmCropSpec crop,
        FarmModuleSnapshot modules,
        FarmDailyPlan plan)
    {
        var internalSeeds = Math.Min(Math.Max(0, farm.InternalSeedCount), plan.PlannedSeedCount);
        var networkSeeds = Math.Max(0, plan.PlannedSeedCount - internalSeeds);
        var internalFertilizer = Math.Min(Math.Max(0, farm.InternalFertilizerCount), plan.PlannedFertilizerCount);
        var fertilizerCoverage = plan.PlannedFertilizerCount * Math.Max(1, modules.FertilizerCoveragePerFertilizer);
        farm.InternalSeedCount -= internalSeeds;
        farm.InternalFertilizerCount -= internalFertilizer;

        for (var i = 0; i < plan.PlannedSeedCount && farm.Plots.Count < tier.Plots; i++)
        {
            farm.Plots.Add(new FarmPlotState
            {
                FertilizerQualifiedItemId = i < fertilizerCoverage ? farm.BoundFertilizerQualifiedItemId : string.Empty
            });
        }

        var harvested = 0;
        var outputStacks = 0;
        if (plan.CanGrowToday)
        {
            var dailyProgress = FarmGrowthRules.GetDailyProgressUnits(modules.LightFactorProduct, modules.HasThermostat ? modules.ThermostatFactor : 1.0m);
            foreach (var plot in farm.Plots.ToList())
            {
                plot.ProgressUnits += dailyProgress;
                var baseDays = plot.InRegrow ? crop.RegrowDays : crop.BaseGrowthDays;
                if (!FarmGrowthRules.IsMature(plot.ProgressUnits, baseDays))
                    continue;

                harvested++;
                outputStacks += AddHarvestOutput(outputBuffer, farm, crop, plot);
                if (crop.RegrowDays > 0)
                {
                    plot.ProgressUnits = 0;
                    plot.InRegrow = true;
                }
                else
                {
                    farm.Plots.Remove(plot);
                }
            }
        }

        return new FarmDailyResult(FarmDailyOutcome.Applied, internalSeeds, networkSeeds, harvested, outputStacks);
    }

    public static FarmTierInfo GetFarmTier(string qualifiedItemId)
    {
        var tier = qualifiedItemId switch
        {
            "(BC)" + Content.ModItemCatalog.SteelFarm => EnergyTier.Steel,
            "(BC)" + Content.ModItemCatalog.GoldFarm => EnergyTier.Gold,
            "(BC)" + Content.ModItemCatalog.IridiumFarm => EnergyTier.Iridium,
            _ => EnergyTier.Copper
        };

        return EnergyTierTable.Farms.Single(entry => entry.Tier == tier);
    }

    public static FarmModuleSnapshot GetModuleSnapshot(FarmMachineState farm)
    {
        return new FarmModuleSnapshot(
            Math.Max(0, farm.SprinklerCoveredPlots),
            Math.Max(0.01m, farm.LightFactorPermille / 1000m),
            farm.HasThermostat,
            Math.Max(0.01m, farm.ThermostatFactorPermille / 1000m),
            Math.Max(0, farm.ChamberModuleWhPerPlot),
            Math.Max(1, farm.SlowReleaseCoveragePerFertilizer));
    }

    public static bool CanBindSeed(FarmMachineState farm, string seedQualifiedItemId)
    {
        return string.IsNullOrWhiteSpace(farm.BoundSeedQualifiedItemId)
            || string.Equals(farm.BoundSeedQualifiedItemId, seedQualifiedItemId, StringComparison.Ordinal)
            || CountOccupied(farm) == 0;
    }

    public static void BindSeed(FarmMachineState farm, FarmCropSpec crop, int placedByFarmingLevel)
    {
        farm.BoundSeedQualifiedItemId = crop.SeedQualifiedItemId;
        farm.HarvestQualifiedItemId = crop.HarvestQualifiedItemId;
        farm.PlacedByFarmingLevel = placedByFarmingLevel;
    }

    public static int CountOccupied(FarmMachineState farm)
    {
        return farm.Plots.Count;
    }

    private static bool CanGrowToday(FarmCropSpec crop, FarmModuleSnapshot modules, string season)
    {
        return modules.HasThermostat
            || crop.Seasons.Contains(NormalizeSeason(season), StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeSeason(string season)
    {
        return season.Trim().ToLowerInvariant();
    }

    private static int AddHarvestOutput(IList<BufferedItemStack> outputBuffer, FarmMachineState farm, FarmCropSpec crop, FarmPlotState plot)
    {
        var total = GetHarvestStack(crop, plot);
        outputBuffer.Add(new BufferedItemStack
        {
            QualifiedItemId = crop.HarvestQualifiedItemId,
            Stack = total,
            Quality = FarmModuleRules.GetHarvestQuality(farm.PlacedByFarmingLevel, plot.FertilizerQualifiedItemId, plot)
        });
        return 1;
    }

    private static int GetHarvestStack(FarmCropSpec crop, FarmPlotState plot)
    {
        var min = Math.Max(1, crop.HarvestMinStack);
        var max = Math.Max(min, crop.HarvestMaxStack);
        var stack = min;
        if (max > min)
        {
            plot.HarvestStackRollBasisPoints = (plot.HarvestStackRollBasisPoints + 6_181) % 10_000;
            var offset = Math.Min(max - min, (int)(plot.HarvestStackRollBasisPoints / (10_000m / (max - min + 1))));
            stack += Math.Max(0, offset);
        }

        if (crop.ExtraHarvestChance <= 0)
            return stack;

        var basisPoints = (int)Math.Round(crop.ExtraHarvestChance * 10_000m, MidpointRounding.AwayFromZero);
        plot.ExtraHarvestChanceBasisPoints += Math.Max(0, basisPoints);
        while (plot.ExtraHarvestChanceBasisPoints >= 10_000)
        {
            stack++;
            plot.ExtraHarvestChanceBasisPoints -= 10_000;
        }

        return stack;
    }
}
