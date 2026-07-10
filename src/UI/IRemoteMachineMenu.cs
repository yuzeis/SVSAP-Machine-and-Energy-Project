using SVSAPME.Models;

namespace SVSAPME.UI;

internal interface IRemoteMachineMenu
{
    Guid MachineGuid { get; }

    int SnapshotOffset { get; }

    int SnapshotLimit { get; }

    void ApplySnapshot(SvsapmeMachineSnapshotResponse snapshot);
}
