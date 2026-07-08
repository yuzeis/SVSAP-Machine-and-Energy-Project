using SVSAPME.Models;
using StardewModdingAPI;

namespace SVSAPME.Services;

internal sealed class MachineStateRepository
{
    private const string SaveKey = "machines";
    private const int CurrentSchemaVersion = 3;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private MachineSaveData data = new();

    public MachineStateRepository(IModHelper helper, IMonitor monitor)
    {
        this.helper = helper;
        this.monitor = monitor;
    }

    public MachineSaveData Data => this.data;

    public void Load()
    {
        this.data = this.helper.Data.ReadSaveData<MachineSaveData>(SaveKey) ?? CreateNewSaveData();
        var migrated = this.NormalizeLoadedData(this.data);
        this.monitor.Log($"Loaded {this.data.Machines.Count} SVSAPME machine state(s) and {this.data.PendingReclaims.Count} pending reclaim(s).", LogLevel.Trace);
        if (migrated)
            this.Save();
    }

    public void Save()
    {
        if (!Context.IsMainPlayer || !Context.IsWorldReady)
            return;

        this.helper.Data.WriteSaveData(SaveKey, this.data);
    }

    public MachineState GetOrCreate(Guid machineGuid)
    {
        if (!this.data.Machines.TryGetValue(machineGuid, out var state))
        {
            state = new MachineState
            {
                MachineGuid = machineGuid
            };
            this.data.Machines[machineGuid] = state;
        }

        return state;
    }

    public bool TryGet(Guid machineGuid, out MachineState state)
    {
        return this.data.Machines.TryGetValue(machineGuid, out state!);
    }

    private static MachineSaveData CreateNewSaveData()
    {
        return new MachineSaveData
        {
            SchemaVersion = CurrentSchemaVersion
        };
    }

    private bool NormalizeLoadedData(MachineSaveData loaded)
    {
        var changed = false;
        var originalSchemaVersion = loaded.SchemaVersion;
        if (loaded.SchemaVersion <= 0)
        {
            loaded.SchemaVersion = 1;
            changed = true;
        }

        if (loaded.SchemaVersion < CurrentSchemaVersion)
        {
            loaded.SchemaVersion = CurrentSchemaVersion;
            changed = true;
        }

        loaded.Machines ??= new();
        loaded.PendingReclaims ??= new();
        loaded.PendingRemoteDeliveries ??= new();
        foreach (var delivery in loaded.PendingRemoteDeliveries)
            delivery.ReturnedItems ??= new();

        foreach (var state in loaded.Machines.Values)
        {
            state.OutputBuffer ??= new();
            state.Farm ??= new();
            state.Farm.InputBuffer ??= new();
            state.Farm.SeedFilterQualifiedItemIds ??= new();
            state.Farm.FertilizerFilterQualifiedItemIds ??= new();
            NormalizeMachineFilterState(state.Farm);
            state.Processor ??= new();
            state.Processor.Slots ??= new();
            state.Processor.InputBuffer ??= new();
            state.Processor.OutputBuffer ??= new();
            state.Processor.FilterQualifiedItemIds ??= new();
            NormalizeMachineFilterState(state.Processor);
            if (state.Processor.LastKegUpdateTime <= 0)
            {
                state.Processor.LastKegUpdateTime = 600;
                changed = true;
            }

            if (originalSchemaVersion < 3
                && state.Processor.AutoPullFromNetwork
                && string.Equals(state.Processor.InputMode, MachineInputModes.AllEligible, StringComparison.Ordinal)
                && state.Processor.FilterQualifiedItemIds.Count == 0)
            {
                state.Processor.AutoPullFromNetwork = false;
                changed = true;
            }

            state.ModData ??= new();
        }

        return changed;
    }

    private static void NormalizeMachineFilterState(FarmMachineState farm)
    {
        if (string.IsNullOrWhiteSpace(farm.InputMode))
            farm.InputMode = MachineInputModes.AllEligible;
        if (string.IsNullOrWhiteSpace(farm.FilterMode))
            farm.FilterMode = MachineFilterModes.Whitelist;
        farm.SeedFilterQualifiedItemIds.RemoveAll(string.IsNullOrWhiteSpace);
        farm.FertilizerFilterQualifiedItemIds.RemoveAll(string.IsNullOrWhiteSpace);
    }

    private static void NormalizeMachineFilterState(SingleBlockProcessorMachineState processor)
    {
        if (string.IsNullOrWhiteSpace(processor.InputMode))
            processor.InputMode = MachineInputModes.AllEligible;
        if (string.IsNullOrWhiteSpace(processor.FilterMode))
            processor.FilterMode = MachineFilterModes.Whitelist;
        processor.FilterQualifiedItemIds.RemoveAll(string.IsNullOrWhiteSpace);
    }
}
