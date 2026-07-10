using SVSAPME.Models;

namespace SVSAPME.Services;

internal static class SvsapmeActionEscrowRules
{
    public static bool ActionMayEscrowHeldItem(SvsapmeMachineActionKind kind)
    {
        return kind is SvsapmeMachineActionKind.LoadFarmSeed
            or SvsapmeMachineActionKind.LoadFarmFertilizer
            or SvsapmeMachineActionKind.InstallFarmModule
            or SvsapmeMachineActionKind.PlantFarmPlot
            or SvsapmeMachineActionKind.FuelCarbonGenerator
            or SvsapmeMachineActionKind.StartElectricFurnace
            or SvsapmeMachineActionKind.StartElectricGeodeCrusher
            or SvsapmeMachineActionKind.LoadProcessorInput
            or SvsapmeMachineActionKind.InstallPoweredUpgrade;
    }

    public static int GetPrimaryEscrowCount(SvsapmeMachineActionKind kind, int requestedCount)
    {
        return kind is SvsapmeMachineActionKind.LoadFarmSeed
            or SvsapmeMachineActionKind.LoadFarmFertilizer
            or SvsapmeMachineActionKind.PlantFarmPlot
            or SvsapmeMachineActionKind.StartElectricFurnace
            or SvsapmeMachineActionKind.LoadProcessorInput
            ? Math.Clamp(requestedCount, 1, 999)
            : 1;
    }

    public static bool ShouldRestoreOnResponse(bool success, bool consumeEscrowedItem)
    {
        return !success || !consumeEscrowedItem;
    }
}
