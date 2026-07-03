using SVSAPME.Models;

namespace SVSAPME.Services;

internal static class SvsapmeActionEscrowRules
{
    public static bool ActionMayEscrowHeldItem(SvsapmeMachineActionKind kind)
    {
        return kind is SvsapmeMachineActionKind.ConfigurePoweredFilter
            or SvsapmeMachineActionKind.LoadFarmSeed
            or SvsapmeMachineActionKind.LoadFarmFertilizer
            or SvsapmeMachineActionKind.InstallFarmModule
            or SvsapmeMachineActionKind.FuelCarbonGenerator;
    }

    public static bool ShouldRestoreOnResponse(bool success, bool consumeEscrowedItem)
    {
        return !success || !consumeEscrowedItem;
    }
}
