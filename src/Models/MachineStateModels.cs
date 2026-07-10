namespace SVSAPME.Models;

internal sealed class MachineSaveData
{
    public int SchemaVersion { get; set; }
    public Dictionary<Guid, MachineState> Machines { get; set; } = new();
    public List<PendingReclaimCrate> PendingReclaims { get; set; } = new();
    public List<PendingRemoteDelivery> PendingRemoteDeliveries { get; set; } = new();
}

internal sealed class MachineState
{
    public Guid MachineGuid { get; set; }
    public string QualifiedItemId { get; set; } = string.Empty;
    public string LocationName { get; set; } = string.Empty;
    public float TileX { get; set; }
    public float TileY { get; set; }
    public string MachineType { get; set; } = string.Empty;
    public long StoredWh { get; set; }
    public long CapacityWh { get; set; }
    public long ProgressWh { get; set; }
    public int MissingDays { get; set; }
    public List<BufferedItemStack> OutputBuffer { get; set; } = new();
    public FarmMachineState Farm { get; set; } = new();
    public SingleBlockProcessorMachineState Processor { get; set; } = new();
    public Dictionary<string, string> ModData { get; set; } = new();
}

internal sealed class BufferedItemStack
{
    public string QualifiedItemId { get; set; } = string.Empty;
    public int Stack { get; set; }
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

internal sealed class PendingReclaimCrate
{
    public Guid ReclaimId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string OriginalLocationName { get; set; } = string.Empty;
    public int TileX { get; set; }
    public int TileY { get; set; }
    public List<Guid> MachineGuids { get; set; } = new();
}

internal sealed class PendingRemoteDelivery
{
    public Guid TransactionId { get; set; }
    public Guid MachineGuid { get; set; }
    public long PlayerId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool ConsumeEscrowedItem { get; set; }
    public int CreatedDay { get; set; }
    public int CreatedTick { get; set; }
    public List<BufferedItemStack> ReturnedItems { get; set; } = new();
}

internal sealed class FarmMachineState
{
    public string BoundSeedQualifiedItemId { get; set; } = string.Empty;
    public string BoundFertilizerQualifiedItemId { get; set; } = string.Empty;
    public string HarvestQualifiedItemId { get; set; } = string.Empty;
    public string InputMode { get; set; } = MachineInputModes.AllEligible;
    public string FilterMode { get; set; } = MachineFilterModes.Whitelist;
    public List<string> SeedFilterQualifiedItemIds { get; set; } = new();
    public List<string> FertilizerFilterQualifiedItemIds { get; set; } = new();
    public bool AutoPullFromNetwork { get; set; } = false;
    public bool AutoPushOutputToNetwork { get; set; } = true;
    public List<BufferedItemStack> InputBuffer { get; set; } = new();
    public int InternalSeedCount { get; set; }
    public int InternalFertilizerCount { get; set; }
    public int PlacedByFarmingLevel { get; set; }
    public int SprinklerCoveredPlots { get; set; }
    public int LightFactorPermille { get; set; } = 1000;
    public List<int> GrowthLightFactorPermilles { get; set; } = new();
    public bool HasThermostat { get; set; }
    public int ThermostatFactorPermille { get; set; } = 1000;
    public string ThermostatModuleQualifiedItemId { get; set; } = string.Empty;
    public int SlowReleaseCoveragePerFertilizer { get; set; } = 1;
    public string SlowReleaseModuleQualifiedItemId { get; set; } = string.Empty;
    public long ChamberModuleWhPerPlot { get; set; }
    public List<string> InstalledModuleQualifiedItemIds { get; set; } = new();
    public List<FarmPlotState> Plots { get; set; } = new();
    public Dictionary<int, string> PlotLocks { get; set; } = new();
}

internal sealed class FarmPlotState
{
    public int PlotIndex { get; set; }
    public string SeedQualifiedItemId { get; set; } = string.Empty;
    public string HarvestQualifiedItemId { get; set; } = string.Empty;
    public int PlacedByFarmingLevel { get; set; }
    public long ProgressUnits { get; set; }
    public bool InRegrow { get; set; }
    public string FertilizerQualifiedItemId { get; set; } = string.Empty;
    public int ExtraHarvestChanceBasisPoints { get; set; }
    public int HarvestStackRollBasisPoints { get; set; }
    public int QualityRollBasisPoints { get; set; }
    public bool IsLocked { get; set; }
    public string LockedSeedQualifiedItemId { get; set; } = string.Empty;
}

internal sealed class SingleBlockProcessorMachineState
{
    public List<SingleBlockProcessorSlotState> Slots { get; set; } = new();
    public List<BufferedItemStack> InputBuffer { get; set; } = new();
    public List<BufferedItemStack> OutputBuffer { get; set; } = new();
    public string InputMode { get; set; } = MachineInputModes.AllEligible;
    public string FilterMode { get; set; } = MachineFilterModes.Whitelist;
    public List<string> FilterQualifiedItemIds { get; set; } = new();
    public bool AutoPullFromNetwork { get; set; }
    public bool AutoPushOutputToNetwork { get; set; } = true;
    public int LastKegUpdateTime { get; set; } = 600;
}

internal sealed class SingleBlockProcessorSlotState
{
    public BufferedItemStack? Input { get; set; }
    public BufferedItemStack? Output { get; set; }
    public int InputCount { get; set; }
    public int RemainingMinutes { get; set; }
    public int TotalMinutes { get; set; }
    public int RemainingDays { get; set; }
    public int TotalDays { get; set; }
    public int TargetQuality { get; set; } = 4;
}

internal static class MachineInputModes
{
    public const string AllEligible = "AllEligible";
    public const string Filter = "Filter";
}

internal static class MachineFilterModes
{
    public const string Whitelist = "Whitelist";
    public const string Blacklist = "Blacklist";
}
