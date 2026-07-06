using SVSAPME.Models;

namespace SVSAPME.Services;

internal static class MachineLifecycleRules
{
    public static bool IsSameTickReplayRemoval(Guid removedMachineGuid, IReadOnlySet<Guid> addedMachineGuids)
    {
        return removedMachineGuid != Guid.Empty && addedMachineGuids.Contains(removedMachineGuid);
    }

    public static bool ClearDisassembledMachineEnergy(MachineState state)
    {
        var changed = false;
        var preservedStoredWh = EnergyCellRules.IsEnergyCell(state.QualifiedItemId)
            ? Math.Max(0, state.StoredWh)
            : 0;

        if (state.StoredWh != preservedStoredWh)
        {
            state.StoredWh = preservedStoredWh;
            changed = true;
        }

        if (state.ProgressWh != 0)
        {
            state.ProgressWh = 0;
            changed = true;
        }

        return changed;
    }
}
