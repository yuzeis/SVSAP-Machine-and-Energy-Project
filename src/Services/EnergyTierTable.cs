namespace SVSAPME.Services;

internal static class EnergyTierTable
{
    public static readonly IReadOnlyList<EnergyCellTierInfo> EnergyCells =
    new List<EnergyCellTierInfo>
    {
        new(EnergyTier.Copper, 10_000, 50, 1),
        new(EnergyTier.Steel, 40_000, 200, 3),
        new(EnergyTier.Gold, 160_000, 800, 5),
        new(EnergyTier.Iridium, 640_000, 3_200, 8)
    };

    public static readonly IReadOnlyList<FarmTierInfo> Farms =
    new List<FarmTierInfo>
    {
        new(EnergyTier.Copper, 16, 2, 30, 2),
        new(EnergyTier.Steel, 64, 3, 30, 5),
        new(EnergyTier.Gold, 144, 4, 30, 8),
        new(EnergyTier.Iridium, 256, 5, 30, 10)
    };

    public static readonly IReadOnlyList<ProcessorTierInfo> Processors =
    new List<ProcessorTierInfo>
    {
        new(EnergyTier.Copper, 16, 18, 40),
        new(EnergyTier.Steel, 64, 18, 40),
        new(EnergyTier.Gold, 144, 18, 40),
        new(EnergyTier.Iridium, 256, 18, 40)
    };

    public static IReadOnlyList<string> Validate()
    {
        var failures = new List<string>();

        Expect(EnergyTier.Copper, 10_000, 50, 1);
        Expect(EnergyTier.Steel, 40_000, 200, 3);
        Expect(EnergyTier.Gold, 160_000, 800, 5);
        Expect(EnergyTier.Iridium, 640_000, 3_200, 8);

        ExpectFarm(EnergyTier.Copper, 16, 2, 30, 2);
        ExpectFarm(EnergyTier.Steel, 64, 3, 30, 5);
        ExpectFarm(EnergyTier.Gold, 144, 4, 30, 8);
        ExpectFarm(EnergyTier.Iridium, 256, 5, 30, 10);

        ExpectProcessor(EnergyTier.Copper, 16, 18, 40);
        ExpectProcessor(EnergyTier.Steel, 64, 18, 40);
        ExpectProcessor(EnergyTier.Gold, 144, 18, 40);
        ExpectProcessor(EnergyTier.Iridium, 256, 18, 40);

        return failures;

        void Expect(EnergyTier tier, long capacityWh, long routeLimitWh, int miningLevel)
        {
            var row = EnergyCells.FirstOrDefault(entry => entry.Tier == tier);
            if (row is null
                || row.CapacityWh != capacityWh
                || row.RouteTickIoLimitWh != routeLimitWh
                || row.RequiredMiningLevel != miningLevel)
            {
                failures.Add($"energy-cell:{tier}");
            }
        }

        void ExpectFarm(EnergyTier tier, int plots, int moduleSlots, long baseWhPerPlotPerDay, int farmingLevel)
        {
            var row = Farms.FirstOrDefault(entry => entry.Tier == tier);
            if (row is null
                || row.Plots != plots
                || row.ModuleSlots != moduleSlots
                || row.BaseWhPerPlotPerDay != baseWhPerPlotPerDay
                || row.RequiredFarmingLevel != farmingLevel)
            {
                failures.Add($"farm:{tier}");
            }
        }

        void ExpectProcessor(EnergyTier tier, int slots, long kegWhPerHour, long caskWhPerDay)
        {
            var row = Processors.FirstOrDefault(entry => entry.Tier == tier);
            if (row is null
                || row.Slots != slots
                || row.KegWhPerSlotPerHour != kegWhPerHour
                || row.CaskWhPerSlotPerDay != caskWhPerDay)
            {
                failures.Add($"processor:{tier}");
            }
        }
    }
}

internal sealed record EnergyCellTierInfo(
    EnergyTier Tier,
    long CapacityWh,
    long RouteTickIoLimitWh,
    int RequiredMiningLevel);

internal sealed record FarmTierInfo(
    EnergyTier Tier,
    int Plots,
    int ModuleSlots,
    long BaseWhPerPlotPerDay,
    int RequiredFarmingLevel);

internal sealed record ProcessorTierInfo(
    EnergyTier Tier,
    int Slots,
    long KegWhPerSlotPerHour,
    long CaskWhPerSlotPerDay);
