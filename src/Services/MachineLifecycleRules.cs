namespace SVSAPME.Services;

internal static class MachineLifecycleRules
{
    public static bool IsSameTickReplayRemoval(Guid removedMachineGuid, IReadOnlySet<Guid> addedMachineGuids)
    {
        return removedMachineGuid != Guid.Empty && addedMachineGuids.Contains(removedMachineGuid);
    }
}
