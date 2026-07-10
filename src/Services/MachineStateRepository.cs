using System.Linq;
using SVSAPME.Models;
using StardewModdingAPI;

namespace SVSAPME.Services;

internal sealed class MachineStateRepository
{
    private const string SaveKey = "machines";
    private const int CurrentSchemaVersion = 5;

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
            state.Farm.Plots ??= new();
            state.Farm.PlotLocks ??= new();
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

            if (originalSchemaVersion < 5)
            {
                changed |= NormalizeFarmPlots(state.Farm);
            }

            foreach (var plot in state.Farm.Plots.Where(plot => plot.IsLocked && !string.IsNullOrWhiteSpace(plot.LockedSeedQualifiedItemId)))
                state.Farm.PlotLocks[plot.PlotIndex] = plot.LockedSeedQualifiedItemId;

            if (originalSchemaVersion < 4
                && state.Farm.AutoPullFromNetwork
                && string.Equals(state.Farm.InputMode, MachineInputModes.AllEligible, StringComparison.Ordinal)
                && state.Farm.SeedFilterQualifiedItemIds.Count == 0)
            {
                state.Farm.AutoPullFromNetwork = false;
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

    private static bool NormalizeFarmPlots(FarmMachineState farm)
    {
        if (farm.Plots is null)
        {
            farm.Plots = new();
            return true;
        }

        var changed = false;
        var normalized = new List<FarmPlotState>(farm.Plots.Count);
        var usedIndexes = new HashSet<int>();
        var nextIndex = 0;

        foreach (var plot in farm.Plots)
        {
            if (plot is null)
            {
                changed = true;
                continue;
            }

            plot.SeedQualifiedItemId ??= string.Empty;
            plot.HarvestQualifiedItemId ??= string.Empty;
            plot.FertilizerQualifiedItemId ??= string.Empty;
            plot.LockedSeedQualifiedItemId ??= string.Empty;

            var assignedIndex = plot.PlotIndex;
            if (assignedIndex < 0 || !usedIndexes.Add(assignedIndex))
            {
                while (usedIndexes.Contains(nextIndex))
                    nextIndex++;

                assignedIndex = nextIndex++;
                if (plot.PlotIndex != assignedIndex)
                {
                    plot.PlotIndex = assignedIndex;
                    changed = true;
                }
            }
            else
            {
                nextIndex = Math.Max(nextIndex, assignedIndex + 1);
            }

            normalized.Add(plot);
        }

        normalized.Sort((left, right) => left.PlotIndex.CompareTo(right.PlotIndex));
        if (normalized.Count != farm.Plots.Count || !normalized.SequenceEqual(farm.Plots))
        {
            farm.Plots = normalized;
            changed = true;
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
