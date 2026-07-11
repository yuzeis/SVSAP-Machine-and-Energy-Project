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
    SetPoweredFilterSlot,
    ClearPoweredFilterSlot,
    TogglePoweredFilterMode,
    TogglePoweredOreDictionaryMode,
    CyclePoweredQualityStrategy,
    ClearPoweredFilters,
    SetPoweredFacingDirection,
    InstallPoweredUpgrade,
    RemovePoweredUpgrade,
    LoadFarmSeed,
    LoadFarmFertilizer,
    ExtractFarmSeed,
    ExtractFarmFertilizer,
    InstallFarmModule,
    RemoveFarmModule,
    ToggleFarmAutoPull,
    ToggleFarmAutoPush,
    ToggleFarmInputMode,
    ToggleFarmFilterMode,
    AddFarmFilter,
    ClearFarmFilter,
    PlantFarmPlot,
    HarvestFarmPlot,
    ToggleFarmPlotLock,
    CollectFarmOutput,
    FuelCarbonGenerator,
    StartElectricFurnace,
    StartElectricGeodeCrusher,
    LoadProcessorInput,
    ExtractProcessorInput,
    ToggleProcessorAutoPull,
    ToggleProcessorAutoPush,
    ToggleProcessorInputMode,
    ToggleProcessorFilterMode,
    AddProcessorFilter,
    ClearProcessorFilter,
    CollectProcessorOutput,
    InstallProcessorUpgrade,
    RemoveProcessorUpgrade,
    UprootFarmPlot,
    ClearFarmPlots
}

internal enum SvsapmeMachineMenuKind
{
    Generic,
    Farm,
    Processor,
    PoweredTransfer,
    EnergyMonitor
}

internal sealed class SvsapmeMachineSnapshotRequest
{
    public Guid MenuSessionId { get; set; }
    public long RequestSequence { get; set; }
    public Guid MachineGuid { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; } = 64;
}

internal sealed class SvsapmeMachineSnapshotResponse
{
    public Guid MenuSessionId { get; set; }
    public long RequestSequence { get; set; }
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
    public long Revision { get; set; }
    public SvsapmeMachineMenuKind MenuKind { get; set; }
    public SvsapmeFarmMenuSnapshot? Farm { get; set; }
    public SvsapmeProcessorMenuSnapshot? Processor { get; set; }
    public SvsapmePoweredTransferMenuSnapshot? PoweredTransfer { get; set; }
    public SvsapmeEnergyMonitorSnapshot? EnergyMonitor { get; set; }
    public List<string> Lines { get; set; } = new();
}

internal sealed class SvsapmeMachineActionRequest
{
    public Guid TransactionId { get; set; }
    public Guid MenuSessionId { get; set; }
    public long RequestSequence { get; set; }
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
    public int SlotIndex { get; set; } = -1;
    public int Direction { get; set; } = -1;
    public int Offset { get; set; }
    public int Limit { get; set; } = 64;
    public Dictionary<string, string> ModData { get; set; } = new();
}

internal sealed class SvsapmeMachineActionResponse
{
    public Guid TransactionId { get; set; }
    public Guid MenuSessionId { get; set; }
    public long RequestSequence { get; set; }
    public Guid MachineGuid { get; set; }
    public bool Success { get; set; }
    public bool ConsumeEscrowedItem { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<BufferedItemStack> ReturnedItems { get; set; } = new();
    public SvsapmeMachineSnapshotResponse? Snapshot { get; set; }
}

internal sealed class SvsapmeFarmMenuSnapshot
{
    public int PlotCapacity { get; set; }
    public int OccupiedPlots { get; set; }
    public int LockedPlots { get; set; }
    public int Offset { get; set; }
    public bool AutoPullFromNetwork { get; set; }
    public bool AutoPushOutputToNetwork { get; set; }
    public bool AutoHarvest { get; set; } = true;
    public string InputMode { get; set; } = string.Empty;
    public string FilterMode { get; set; } = string.Empty;
    public List<string> SeedFilterQualifiedItemIds { get; set; } = new();
    public List<BufferedItemStack> InputBuffer { get; set; } = new();
    public string FertilizerQualifiedItemId { get; set; } = string.Empty;
    public int FertilizerCount { get; set; }
    public List<string> InstalledModuleQualifiedItemIds { get; set; } = new();
    public int ModuleSlotCapacity { get; set; }
    public List<SvsapmeFarmPlotSnapshot> Plots { get; set; } = new();
    public List<BufferedItemStack> OutputBuffer { get; set; } = new();
    public decimal EstimatedDailyValue { get; set; }
    public long EstimatedDailyEnergyWh { get; set; }
}

internal sealed class SvsapmeFarmPlotSnapshot
{
    public int PlotIndex { get; set; }
    public string SeedQualifiedItemId { get; set; } = string.Empty;
    public string HarvestQualifiedItemId { get; set; } = string.Empty;
    public string FertilizerQualifiedItemId { get; set; } = string.Empty;
    public string LockedSeedQualifiedItemId { get; set; } = string.Empty;
    public long ProgressUnits { get; set; }
    public long RequiredUnits { get; set; }
    public bool Ready { get; set; }
    public bool IsLocked { get; set; }
}

internal sealed class SvsapmeProcessorMenuSnapshot
{
    public int SlotCapacity { get; set; }
    public int Offset { get; set; }
    public bool NetworkOnline { get; set; }
    public bool EnergyOnline { get; set; }
    public long StoredWh { get; set; }
    public long CapacityWh { get; set; }
    public long RequiredWhForNextStep { get; set; }
    public bool AutoPullFromNetwork { get; set; }
    public bool AutoPushOutputToNetwork { get; set; }
    public string InputMode { get; set; } = string.Empty;
    public string FilterMode { get; set; } = string.Empty;
    public List<string> FilterQualifiedItemIds { get; set; } = new();
    public List<BufferedItemStack> InputBuffer { get; set; } = new();
    public List<BufferedItemStack> OutputBuffer { get; set; } = new();
    public List<string> InstalledUpgradeQualifiedItemIds { get; set; } = new();
    public int UpgradeSlotCapacity { get; set; }
    public int SpeedPermille { get; set; } = 1000;
    public int OutputBufferCapacityItems { get; set; }
    public List<SvsapmeProcessorSlotSnapshot> Slots { get; set; } = new();
    public decimal EstimatedDailyValue { get; set; }
}

internal sealed class SvsapmeProcessorSlotSnapshot
{
    public int SlotIndex { get; set; }
    public BufferedItemStack? Input { get; set; }
    public BufferedItemStack? Output { get; set; }
    public bool Ready { get; set; }
    public bool CanEject { get; set; }
    public bool CanCollect { get; set; }
    public int Remaining { get; set; }
    public int Total { get; set; }
}

internal sealed class SvsapmePoweredTransferMenuSnapshot
{
    public bool IsBlacklist { get; set; }
    public bool OreDictionaryEnabled { get; set; }
    public string QualityStrategy { get; set; } = string.Empty;
    public int FacingDirection { get; set; } = -1;
    public List<SvsapmeFilterSlotSnapshot> FilterSlots { get; set; } = new();
    public List<string> InstalledUpgradeQualifiedItemIds { get; set; } = new();
    public int UpgradeSlotCapacity { get; set; }
    public int Throughput { get; set; }
    public int TransferIntervalTicks { get; set; }
    public decimal EnergyPerActionWh { get; set; }
    public bool NetworkOnline { get; set; }
    public long StoredWh { get; set; }
    public long CapacityWh { get; set; }
}

internal sealed class SvsapmeFilterSlotSnapshot
{
    public int SlotIndex { get; set; }
    public string QualifiedItemId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> OreGroups { get; set; } = new();
}

internal sealed class SvsapmeEnergyMonitorSnapshot
{
    public Guid NetworkId { get; set; }
    public bool Online { get; set; }
    public string StatusText { get; set; } = string.Empty;
    public long StoredWh { get; set; }
    public long CapacityWh { get; set; }
    public long LastTickGeneratedWh { get; set; }
    public long LastTickConsumedWh { get; set; }
    public long TodayGeneratedWh { get; set; }
    public long TodayConsumedWh { get; set; }
    public string LastWarning { get; set; } = string.Empty;
    public List<SvsapmeEnergyDeviceSnapshot> Producers { get; set; } = new();
    public List<SvsapmeEnergyDeviceSnapshot> Consumers { get; set; } = new();
}

internal sealed class SvsapmeEnergyDeviceSnapshot
{
    public string DeviceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public long TotalWh { get; set; }
    public List<string> Details { get; set; } = new();
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
    public long LastTickGeneratedWh { get; set; }
    public long LastTickConsumedWh { get; set; }
    public long TodayGeneratedWh { get; set; }
    public long TodayConsumedWh { get; set; }
    public string LastWarning { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}
