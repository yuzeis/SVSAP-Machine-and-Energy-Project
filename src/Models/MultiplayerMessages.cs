namespace SVSAPME.Models;

internal static class SvsapmeMultiplayerMessageTypes
{
    public const string MachineSnapshotRequest = "SvsapmeMachineSnapshotRequest";
    public const string MachineSnapshotResponse = "SvsapmeMachineSnapshotResponse";
    public const string MachineActionRequest = "SvsapmeMachineActionRequest";
    public const string MachineActionResponse = "SvsapmeMachineActionResponse";
    public const string MachineItemMovementReport = "SvsapmeMachineItemMovementReport";
    public const string EnergyDebugRequest = "SvsapmeEnergyDebugRequest";
    public const string EnergyDebugResponse = "SvsapmeEnergyDebugResponse";
}

internal enum SvsapmeMachineActionKind
{
    None,
    ConfigurePoweredFilter,
    LoadFarmSeed,
    LoadFarmFertilizer,
    InstallFarmModule,
    FuelCarbonGenerator
}

internal sealed class SvsapmeMachineSnapshotRequest
{
    public Guid MachineGuid { get; set; }
}

internal sealed class SvsapmeMachineSnapshotResponse
{
    public Guid MachineGuid { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string QualifiedItemId { get; set; } = string.Empty;
    public string LocationName { get; set; } = string.Empty;
    public float TileX { get; set; }
    public float TileY { get; set; }
    public long StoredWh { get; set; }
    public long CapacityWh { get; set; }
    public long ProgressWh { get; set; }
    public int OutputBufferStacks { get; set; }
}

internal sealed class SvsapmeMachineActionRequest
{
    public Guid TransactionId { get; set; }
    public Guid MachineGuid { get; set; }
    public SvsapmeMachineActionKind ActionKind { get; set; }
    public string QualifiedItemId { get; set; } = string.Empty;
    public int Count { get; set; }
    public int FarmingLevel { get; set; }
}

internal sealed class SvsapmeMachineActionResponse
{
    public Guid TransactionId { get; set; }
    public Guid MachineGuid { get; set; }
    public bool Success { get; set; }
    public bool ConsumeEscrowedItem { get; set; }
    public string Message { get; set; } = string.Empty;
}

internal sealed record SvsapmeMachineActionApplyResult(
    bool Success,
    bool ConsumeEscrowedItem,
    string Message);

internal sealed class SvsapmeMachineItemMovementReport
{
    public List<Guid> RemovedMachineGuids { get; set; } = new();
    public List<Guid> ObservedMachineGuids { get; set; } = new();
}

internal sealed class SvsapmeEnergyDebugRequest
{
    public Guid NetworkId { get; set; }
}

internal sealed class SvsapmeEnergyDebugResponse
{
    public Guid NetworkId { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public long StoredWh { get; set; }
    public long CapacityWh { get; set; }
    public string Code { get; set; } = string.Empty;
}
