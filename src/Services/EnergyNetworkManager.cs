using Koizumi.SVSAP.Api;
using Koizumi.SVSAPME.Api;
using SVSAPME.Content;
using SVSAPME.Models;
using StardewModdingAPI;
using StardewValley;

namespace SVSAPME.Services;

internal sealed class EnergyNetworkManager
{
    private readonly MachineStateRepository repository;
    private readonly MachineRegistryService registry;
    private readonly Func<ISvsapApi?> getSvsapApi;
    private readonly EnergyTelemetryService telemetry;
    private readonly IMonitor monitor;
    private int linkedEnergyCellCacheScopeDepth;
    private Dictionary<Guid, List<MachineState>>? linkedEnergyCellCache;

    public EnergyNetworkManager(
        MachineStateRepository repository,
        MachineRegistryService registry,
        Func<ISvsapApi?> getSvsapApi,
        EnergyTelemetryService telemetry,
        IMonitor monitor)
    {
        this.repository = repository;
        this.registry = registry;
        this.getSvsapApi = getSvsapApi;
        this.telemetry = telemetry;
        this.monitor = monitor;
    }

    public bool IsHostAuthority => Context.IsWorldReady && Context.IsMainPlayer;

    public IDisposable BeginLinkedEnergyCellCacheScope()
    {
        if (this.linkedEnergyCellCacheScopeDepth == 0)
            this.linkedEnergyCellCache = this.BuildLinkedEnergyCellIndex();

        this.linkedEnergyCellCacheScopeDepth++;
        return new LinkedEnergyCellCacheScope(this);
    }

    public bool TryGetNetworkEnergy(Guid svsapNetworkId, out long storedWh, out long capacityWh, out SvsapmeEnergyErrorCode code)
    {
        storedWh = 0;
        capacityWh = 0;

        var cells = this.GetLinkedEnergyCells(svsapNetworkId).ToList();
        if (cells.Count == 0)
        {
            code = SvsapmeEnergyErrorCode.NoEnergyCell;
            return false;
        }

        foreach (var cell in cells)
        {
            capacityWh += Math.Max(0, cell.CapacityWh);
            storedWh += Math.Clamp(cell.StoredWh, 0, Math.Max(0, cell.CapacityWh));
        }

        code = SvsapmeEnergyErrorCode.None;
        return true;
    }

    public bool TryDepositWh(
        Guid svsapNetworkId,
        long amountWh,
        string ownerModId,
        string reason,
        out long acceptedWh,
        out SvsapmeEnergyErrorCode code,
        out string message)
    {
        acceptedWh = 0;
        if (!this.IsHostAuthority)
        {
            var result = Fail(SvsapmeEnergyErrorCode.NotHost, "Energy writes are host-authoritative.", out code, out message);
            this.telemetry.RecordDeposit(svsapNetworkId, ownerModId, reason, amountWh, 0, code, message);
            return result;
        }

        if (amountWh <= 0)
        {
            var result = Fail(SvsapmeEnergyErrorCode.InternalError, "Deposit amount must be positive.", out code, out message);
            this.telemetry.RecordDeposit(svsapNetworkId, ownerModId, reason, amountWh, 0, code, message);
            return result;
        }

        var cells = this.GetLinkedEnergyCells(svsapNetworkId)
            .OrderBy(cell => cell.CapacityWh)
            .ThenBy(cell => cell.MachineGuid)
            .ToList();
        if (cells.Count == 0)
        {
            var result = Fail(SvsapmeEnergyErrorCode.NoEnergyCell, "No active SVSAPME energy cell is linked to this network.", out code, out message);
            this.telemetry.RecordDeposit(svsapNetworkId, ownerModId, reason, amountWh, 0, code, message);
            return result;
        }

        var remaining = amountWh;
        var touched = new List<Guid>();
        foreach (var cell in cells)
        {
            var capacity = Math.Max(0, cell.CapacityWh);
            cell.StoredWh = Math.Clamp(cell.StoredWh, 0, capacity);
            var accepted = Math.Min(remaining, capacity - cell.StoredWh);
            if (accepted <= 0)
                continue;

            cell.StoredWh += accepted;
            touched.Add(cell.MachineGuid);
            acceptedWh += accepted;
            remaining -= accepted;
            if (remaining <= 0)
                break;
        }

        if (acceptedWh <= 0)
        {
            var result = Fail(SvsapmeEnergyErrorCode.StorageFull, "Network energy storage is full.", out code, out message);
            this.telemetry.RecordDeposit(svsapNetworkId, ownerModId, reason, amountWh, 0, code, message);
            return result;
        }

        foreach (var machineGuid in touched)
            this.registry.SyncPlacedMachineState(machineGuid);

        code = SvsapmeEnergyErrorCode.None;
        message = $"Accepted {acceptedWh} Wh from {ownerModId}:{reason}.";
        this.telemetry.RecordDeposit(svsapNetworkId, ownerModId, reason, amountWh, acceptedWh, code, message);
        return true;
    }

    public bool TryConsumeWh(
        Guid svsapNetworkId,
        long amountWh,
        string ownerModId,
        string reason,
        bool allowPartial,
        out long consumedWh,
        out SvsapmeEnergyErrorCode code,
        out string message)
    {
        consumedWh = 0;
        if (!this.IsHostAuthority)
        {
            var result = Fail(SvsapmeEnergyErrorCode.NotHost, "Energy writes are host-authoritative.", out code, out message);
            this.telemetry.RecordConsume(svsapNetworkId, ownerModId, reason, amountWh, 0, code, message);
            return result;
        }

        if (amountWh <= 0)
        {
            var result = Fail(SvsapmeEnergyErrorCode.InternalError, "Consume amount must be positive.", out code, out message);
            this.telemetry.RecordConsume(svsapNetworkId, ownerModId, reason, amountWh, 0, code, message);
            return result;
        }

        var cells = this.GetLinkedEnergyCells(svsapNetworkId)
            .OrderByDescending(cell => cell.CapacityWh)
            .ThenBy(cell => cell.MachineGuid)
            .ToList();
        if (cells.Count == 0)
        {
            var result = Fail(SvsapmeEnergyErrorCode.NoEnergyCell, "No active SVSAPME energy cell is linked to this network.", out code, out message);
            this.telemetry.RecordConsume(svsapNetworkId, ownerModId, reason, amountWh, 0, code, message);
            return result;
        }

        var availableWh = cells.Sum(cell => Math.Clamp(cell.StoredWh, 0, Math.Max(0, cell.CapacityWh)));
        if (availableWh < amountWh && !allowPartial)
        {
            var result = Fail(SvsapmeEnergyErrorCode.InsufficientEnergy, $"Need {amountWh} Wh but only {availableWh} Wh is stored.", out code, out message);
            this.telemetry.RecordConsume(svsapNetworkId, ownerModId, reason, amountWh, 0, code, message);
            return result;
        }

        var remaining = allowPartial ? Math.Min(amountWh, availableWh) : amountWh;
        var touched = new List<Guid>();
        foreach (var cell in cells)
        {
            cell.StoredWh = Math.Clamp(cell.StoredWh, 0, Math.Max(0, cell.CapacityWh));
            var consumed = Math.Min(remaining, cell.StoredWh);
            if (consumed <= 0)
                continue;

            cell.StoredWh -= consumed;
            touched.Add(cell.MachineGuid);
            consumedWh += consumed;
            remaining -= consumed;
            if (remaining <= 0)
                break;
        }

        if (consumedWh <= 0)
        {
            var result = Fail(SvsapmeEnergyErrorCode.InsufficientEnergy, "No stored energy is available.", out code, out message);
            this.telemetry.RecordConsume(svsapNetworkId, ownerModId, reason, amountWh, 0, code, message);
            return result;
        }

        foreach (var machineGuid in touched)
            this.registry.SyncPlacedMachineState(machineGuid);

        code = SvsapmeEnergyErrorCode.None;
        message = $"Consumed {consumedWh} Wh for {ownerModId}:{reason}.";
        this.telemetry.RecordConsume(svsapNetworkId, ownerModId, reason, amountWh, consumedWh, code, message);
        return true;
    }

    private IEnumerable<MachineState> GetLinkedEnergyCells(Guid svsapNetworkId)
    {
        if (this.linkedEnergyCellCacheScopeDepth > 0 && this.linkedEnergyCellCache is not null)
        {
            return this.linkedEnergyCellCache.TryGetValue(svsapNetworkId, out var cells)
                ? cells
                : Enumerable.Empty<MachineState>();
        }

        return this.EnumerateLinkedEnergyCells(svsapNetworkId);
    }

    private Dictionary<Guid, List<MachineState>> BuildLinkedEnergyCellIndex()
    {
        var index = new Dictionary<Guid, List<MachineState>>();
        var api = this.getSvsapApi();
        if (api is null)
            return index;

        foreach (var (state, networkId) in this.EnumerateLinkedEnergyCellEndpoints(api))
        {
            if (!index.TryGetValue(networkId, out var cells))
            {
                cells = new List<MachineState>();
                index[networkId] = cells;
            }

            cells.Add(state);
        }

        return index;
    }

    private IEnumerable<MachineState> EnumerateLinkedEnergyCells(Guid svsapNetworkId)
    {
        var api = this.getSvsapApi();
        if (api is null)
            yield break;

        foreach (var (state, networkId) in this.EnumerateLinkedEnergyCellEndpoints(api))
        {
            if (networkId == svsapNetworkId)
                yield return state;
        }
    }

    private IEnumerable<(MachineState State, Guid NetworkId)> EnumerateLinkedEnergyCellEndpoints(ISvsapApi api)
    {
        foreach (var state in this.repository.Data.Machines.Values)
        {
            if (!EnergyCellRules.IsEnergyCell(state.QualifiedItemId))
                continue;

            var location = Game1.getLocationFromName(state.LocationName);
            var tile = new Microsoft.Xna.Framework.Vector2(state.TileX, state.TileY);
            if (location is null
                || !location.Objects.TryGetValue(tile, out var placedObject)
                || placedObject.QualifiedItemId != state.QualifiedItemId
                || !Guid.TryParse(placedObject.modData.GetValueOrDefault(MachineRegistryService.MachineGuidKey), out var placedGuid)
                || placedGuid != state.MachineGuid)
            {
                continue;
            }

            if (!api.TryGetLinkedEndpoint(location, tile, out var endpoint, out var code, out var message))
            {
                this.monitor.Log($"Skipped unlinked SVSAPME energy cell {state.MachineGuid:N}: {code} {message}", LogLevel.Trace);
                continue;
            }

            if (endpoint is not null && endpoint.Active)
                yield return (state, endpoint.NetworkId);
        }
    }

    private void EndLinkedEnergyCellCacheScope()
    {
        if (this.linkedEnergyCellCacheScopeDepth <= 0)
        {
            this.linkedEnergyCellCache = null;
            this.linkedEnergyCellCacheScopeDepth = 0;
            return;
        }

        this.linkedEnergyCellCacheScopeDepth--;
        if (this.linkedEnergyCellCacheScopeDepth == 0)
            this.linkedEnergyCellCache = null;
    }

    private static bool Fail(SvsapmeEnergyErrorCode errorCode, string failureMessage, out SvsapmeEnergyErrorCode code, out string message)
    {
        code = errorCode;
        message = failureMessage;
        return false;
    }

    private sealed class LinkedEnergyCellCacheScope : IDisposable
    {
        private EnergyNetworkManager? owner;

        public LinkedEnergyCellCacheScope(EnergyNetworkManager owner)
        {
            this.owner = owner;
        }

        public void Dispose()
        {
            if (this.owner is null)
                return;

            this.owner.EndLinkedEnergyCellCacheScope();
            this.owner = null;
        }
    }
}
