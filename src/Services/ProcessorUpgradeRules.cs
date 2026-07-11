using SVSAPME.Content;
using SVSAPME.Models;

namespace SVSAPME.Services;

internal static class ProcessorUpgradeRules
{
    public const int BaseSpeedPermille = 1000;
    public const int SpeedBonusPermillePerCard = 100;

    private static readonly string SpeedCardQualifiedItemId = "(O)" + ModItemCatalog.SvsapSpeedCard;
    private static readonly string CapacityCardQualifiedItemId = "(O)" + ModItemCatalog.SvsapCapacityCard;
    private static readonly string QualityCardQualifiedItemId = "(O)" + ModItemCatalog.SvsapQualityCard;

    public static int GetSlotCapacity(ProcessorTierInfo tier)
    {
        return tier.Tier switch
        {
            EnergyTier.Steel => 3,
            EnergyTier.Gold => 4,
            EnergyTier.Iridium => 5,
            _ => 2
        };
    }

    public static bool IsProcessorUpgradeCard(string qualifiedItemId)
    {
        return qualifiedItemId == SpeedCardQualifiedItemId
            || qualifiedItemId == CapacityCardQualifiedItemId
            || qualifiedItemId == QualityCardQualifiedItemId;
    }

    public static bool IsSupportedCard(SingleBlockProcessorKind kind, string qualifiedItemId)
    {
        return qualifiedItemId == SpeedCardQualifiedItemId
            || qualifiedItemId == CapacityCardQualifiedItemId
            || kind == SingleBlockProcessorKind.Keg && qualifiedItemId == QualityCardQualifiedItemId;
    }

    public static ProcessorUpgradeInstallResult TryInstall(
        SingleBlockProcessorMachineState processor,
        ProcessorTierInfo tier,
        SingleBlockProcessorKind kind,
        string qualifiedItemId)
    {
        NormalizeInstalledUpgrades(processor);
        if (!IsProcessorUpgradeCard(qualifiedItemId))
        {
            return new(false, false, ModText.Get(
                "hud.processor.upgrade.invalidCard",
                "This item is not a supported processor upgrade card."));
        }

        if (!IsSupportedCard(kind, qualifiedItemId))
        {
            return new(false, false, ModText.Get(
                "hud.processor.upgrade.unsupportedForMachine",
                "This upgrade card is not supported by this processor type."));
        }

        var capacity = GetSlotCapacity(tier);
        if (processor.InstalledUpgradeQualifiedItemIds.Count >= capacity)
        {
            return new(false, false, ModText.Get(
                "hud.processor.upgrade.full",
                "This processor has no free upgrade slot."));
        }

        if (qualifiedItemId == QualityCardQualifiedItemId
            && processor.InstalledUpgradeQualifiedItemIds.Contains(qualifiedItemId, StringComparer.Ordinal))
        {
            return new(false, false, ModText.Get(
                "hud.processor.upgrade.duplicateQuality",
                "A processor can install at most one quality card."));
        }

        processor.InstalledUpgradeQualifiedItemIds.Add(qualifiedItemId);
        return new(true, true, ModText.Get(
            "hud.processor.upgrade.installed",
            "Processor upgrade installed: {{item}}.",
            new { item = SingleBlockProcessorService.FormatItem(qualifiedItemId) }));
    }

    public static int GetSpeedPermille(SingleBlockProcessorMachineState processor)
    {
        NormalizeInstalledUpgrades(processor);
        var speedCards = processor.InstalledUpgradeQualifiedItemIds.Count(
            qualifiedItemId => qualifiedItemId == SpeedCardQualifiedItemId);
        return BaseSpeedPermille + speedCards * SpeedBonusPermillePerCard;
    }

    public static int GetOutputBufferCapacityItems(
        SingleBlockProcessorMachineState processor,
        ProcessorTierInfo tier)
    {
        NormalizeInstalledUpgrades(processor);
        var capacityCards = processor.InstalledUpgradeQualifiedItemIds.Count(
            qualifiedItemId => qualifiedItemId == CapacityCardQualifiedItemId);
        return checked(capacityCards * tier.Slots);
    }

    public static bool PreservesKegInputQuality(SingleBlockProcessorMachineState processor)
    {
        NormalizeInstalledUpgrades(processor);
        return processor.InstalledUpgradeQualifiedItemIds.Contains(QualityCardQualifiedItemId, StringComparer.Ordinal);
    }

    public static void ApplyJobModifiers(
        SingleBlockProcessorMachineState processor,
        SingleBlockProcessorKind kind,
        SingleBlockProcessorSlotState job)
    {
        if (kind != SingleBlockProcessorKind.Keg
            || !PreservesKegInputQuality(processor)
            || job.Input is null
            || job.Output is null)
        {
            return;
        }

        job.Output.Quality = job.Input.Quality;
    }

    public static int CalculateScaledWork(
        int baseUnits,
        int speedPermille,
        int currentRemainderPermille,
        out int nextRemainderPermille)
    {
        var totalPermille = (long)Math.Max(0, baseUnits) * Math.Max(BaseSpeedPermille, speedPermille)
            + Math.Clamp(currentRemainderPermille, 0, 999);
        nextRemainderPermille = (int)(totalPermille % BaseSpeedPermille);
        return (int)Math.Min(int.MaxValue, totalPermille / BaseSpeedPermille);
    }

    public static IReadOnlyList<string> GetInstalledUpgradeItems(SingleBlockProcessorMachineState processor)
    {
        NormalizeInstalledUpgrades(processor);
        return processor.InstalledUpgradeQualifiedItemIds.ToList();
    }

    public static void ClearInstalledUpgrades(SingleBlockProcessorMachineState processor)
    {
        processor.InstalledUpgradeQualifiedItemIds.Clear();
        processor.KegSpeedRemainderPermille = 0;
        processor.CaskSpeedRemainderPermille = 0;
    }

    public static bool NormalizeInstalledUpgrades(SingleBlockProcessorMachineState processor)
    {
        processor.InstalledUpgradeQualifiedItemIds ??= new();
        var normalized = processor.InstalledUpgradeQualifiedItemIds
            .Where(IsProcessorUpgradeCard)
            .ToList();
        var changed = normalized.Count != processor.InstalledUpgradeQualifiedItemIds.Count
            || !normalized.SequenceEqual(processor.InstalledUpgradeQualifiedItemIds, StringComparer.Ordinal);
        if (changed)
            processor.InstalledUpgradeQualifiedItemIds = normalized;
        return changed;
    }

    public static string GetEffectDescription(SingleBlockProcessorKind kind, string qualifiedItemId)
    {
        if (qualifiedItemId == SpeedCardQualifiedItemId)
        {
            return ModText.Get(
                "ui.processor.upgrade.speed",
                "Speed Card: +10% processing speed per card; energy use scales with completed work.");
        }

        if (qualifiedItemId == CapacityCardQualifiedItemId)
        {
            return ModText.Get(
                "ui.processor.upgrade.capacity",
                "Capacity Card: buffers one full machine batch of completed output when the network is full.");
        }

        if (qualifiedItemId == QualityCardQualifiedItemId && kind == SingleBlockProcessorKind.Keg)
        {
            return ModText.Get(
                "ui.processor.upgrade.quality",
                "Quality Card: newly loaded keg jobs preserve the quality of their input ingredient.");
        }

        return ModText.Get(
            "ui.processor.upgrade.unsupported",
            "This card has no processor effect here.");
    }
}

internal readonly record struct ProcessorUpgradeInstallResult(bool Success, bool ConsumesItem, string Message);
