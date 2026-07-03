using SVSAPME.Models;
using StardewModdingAPI;

namespace SVSAPME.Services;

internal sealed class MachineStateRepository
{
    private const string SaveKey = "machines";

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
        this.data = this.helper.Data.ReadSaveData<MachineSaveData>(SaveKey) ?? new MachineSaveData();
        this.monitor.Log($"Loaded {this.data.Machines.Count} SVSAPME machine state(s) and {this.data.PendingReclaims.Count} pending reclaim(s).", LogLevel.Trace);
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
}
