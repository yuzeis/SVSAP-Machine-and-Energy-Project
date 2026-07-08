namespace SVSAPME.Models;

internal static class SvsapmeMultiplayerMessageTypes
{
    public const string MachineSnapshotRequest = "SvsapmeMachineSnapshotRequest";
    public const string MachineSnapshotResponse = "SvsapmeMachineSnapshotResponse";
    public const string MachineActionRequest = "SvsapmeMachineActionRequest";
    public const string MachineActionResponse = "SvsapmeMachineActionResponse";
    public const string MachineDeliveryAck = "SvsapmeMachineDeliveryAck";
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
    FuelCarbonGenerator,
    StartElectricFurnace,
    StartElectricGeodeCrusher,
    LoadProcessorInput,
    CollectProcessorOutput
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
    public string DisplayName { get; set; } = string.Empty;
    public string QualifiedItemId { get; set; } = string.Empty;
    public string LocationName { get; set; } = string.Empty;
    public float TileX { get; set; }
    public float TileY { get; set; }
    public long StoredWh { get; set; }
    public long CapacityWh { get; set; }
    public long ProgressWh { get; set; }
    public int OutputBufferStacks { get; set; }
    public bool CanCollectProcessorOutput { get; set; }
    public int ProcessorReadyStacks { get; set; }
    public List<string> Lines { get; set; } = new();
}

internal sealed class SvsapmeMachineActionRequest
{
    public Guid TransactionId { get; set; }
    public Guid MachineGuid { get; set; }
    public SvsapmeMachineActionKind ActionKind { get; set; }
    public string QualifiedItemId { get; set; } = string.Empty;
    public int Count { get; set; }
    public int FarmingLevel { get; set; }
    public int Quality { get; set; }
    public string PreservedParentSheetIndex { get; set; } = string.Empty;
    public int? PreserveType { get; set; }
    public int? Price { get; set; }
    public int? Edibility { get; set; }
    public int? Category { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public uint? Color { get; set; }
    public Dictionary<string, string> ModData { get; set; } = new();
}

internal sealed class SvsapmeMachineActionResponse
{
    public Guid TransactionId { get; set; }
    public Guid MachineGuid { get; set; }
    public bool Success { get; set; }
    public bool ConsumeEscrowedItem { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<BufferedItemStack> ReturnedItems { get; set; } = new();
}

internal sealed class SvsapmeMachineDeliveryAck
{
    public Guid TransactionId { get; set; }
    public Guid MachineGuid { get; set; }
}

internal sealed record SvsapmeMachineActionApplyResult(
    bool Success,
    bool ConsumeEscrowedItem,
    string Message)
{
    public List<BufferedItemStack> ReturnedItems { get; init; } = new();
}

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
