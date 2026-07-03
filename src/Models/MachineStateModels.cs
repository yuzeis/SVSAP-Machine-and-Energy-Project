namespace SVSAPME.Models;

internal sealed class MachineSaveData
{
    public Dictionary<Guid, MachineState> Machines { get; set; } = new();
    public List<PendingReclaimCrate> PendingReclaims { get; set; } = new();
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
    public Dictionary<string, string> ModData { get; set; } = new();
}

internal sealed class BufferedItemStack
{
    public string QualifiedItemId { get; set; } = string.Empty;
    public int Stack { get; set; }
    public int Quality { get; set; }
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

internal sealed class FarmMachineState
{
    public string BoundSeedQualifiedItemId { get; set; } = string.Empty;
    public string BoundFertilizerQualifiedItemId { get; set; } = string.Empty;
    public string HarvestQualifiedItemId { get; set; } = string.Empty;
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
}

internal sealed class FarmPlotState
{
    public long ProgressUnits { get; set; }
    public bool InRegrow { get; set; }
    public string FertilizerQualifiedItemId { get; set; } = string.Empty;
    public int ExtraHarvestChanceBasisPoints { get; set; }
    public int HarvestStackRollBasisPoints { get; set; }
    public int QualityRollBasisPoints { get; set; }
}
