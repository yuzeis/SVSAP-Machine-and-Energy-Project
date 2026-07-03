using Koizumi.SVSAP.Api;
using SVSAPME.Content;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace SVSAPME.Services;

internal sealed class EnergyProductionService
{
    private readonly MachineRegistryService registry;
    private readonly EnergyNetworkManager energy;
    private readonly Func<ISvsapApi?> getSvsapApi;
    private readonly Func<ModConfig> getConfig;
    private readonly IMonitor monitor;

    public EnergyProductionService(
        MachineRegistryService registry,
        EnergyNetworkManager energy,
        Func<ISvsapApi?> getSvsapApi,
        Func<ModConfig> getConfig,
        IMonitor monitor)
    {
        this.registry = registry;
        this.energy = energy;
        this.getSvsapApi = getSvsapApi;
        this.getConfig = getConfig;
        this.monitor = monitor;
    }

    public void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (!Context.IsMainPlayer)
            return;

        var api = this.getSvsapApi();
        if (api is null)
            return;

        var config = this.getConfig();
        foreach (var machine in this.registry.MachinesByGuid.Values
            .Where(machine => machine.QualifiedItemId is ("(BC)" + ModItemCatalog.SolarNetworkPanel) or ("(BC)" + ModItemCatalog.LightningCapacitor))
            .OrderBy(machine => machine.MachineGuid))
        {
            if (!TryGetActiveEndpoint(api, machine, out var location, out var endpoint))
                continue;

            var generatedWh = machine.QualifiedItemId == "(BC)" + ModItemCatalog.SolarNetworkPanel
                ? (config.EnableSolarNetworkPanel ? GetSolarPanelWh(location) : 0)
                : (config.EnableLightningCapacitor ? GetLightningCapacitorWh(location) : 0);
            generatedWh = ApplyGeneratorMultiplier(generatedWh, config.GeneratorMultiplier);
            if (generatedWh <= 0)
                continue;

            if (!this.energy.TryDepositWh(
                    endpoint.NetworkId,
                    generatedWh,
                    ModItemCatalog.UniqueId,
                    machine.QualifiedItemId == "(BC)" + ModItemCatalog.SolarNetworkPanel ? "solar-panel-daystarted" : "lightning-capacitor-daystarted",
                    out var acceptedWh,
                    out var code,
                    out var message))
            {
                this.monitor.Log($"Energy producer {machine.MachineGuid:N} could not deposit {generatedWh} Wh: {code} {message}", LogLevel.Trace);
                continue;
            }

            if (acceptedWh < generatedWh && config.DetailedEnergyLogs)
                this.monitor.Log($"Energy producer {machine.MachineGuid:N} generated {generatedWh} Wh; accepted {acceptedWh} Wh and discarded {generatedWh - acceptedWh} Wh.", LogLevel.Info);
        }
    }

    private static long GetSolarPanelWh(GameLocation location)
    {
        _ = location.GetWeather();
        return EnergyProductionRules.GetSolarPanelWh(location.IsOutdoors, location.IsRainingHere(), location.IsLightningHere(), location.GetSeason());
    }

    private static long GetLightningCapacitorWh(GameLocation location)
    {
        _ = location.GetWeather();
        return EnergyProductionRules.GetLightningCapacitorWh(location.IsOutdoors, location.IsLightningHere());
    }

    private static long ApplyGeneratorMultiplier(long baseWh, double multiplier)
    {
        if (baseWh <= 0 || multiplier <= 0)
            return 0;

        return checked((long)Math.Round(baseWh * multiplier, MidpointRounding.AwayFromZero));
    }

    private static bool TryGetActiveEndpoint(
        ISvsapApi api,
        MachineLocation machine,
        out GameLocation location,
        out ISvsapEndpointInfo endpoint)
    {
        endpoint = null!;
        location = Game1.getLocationFromName(machine.LocationName);
        if (location is null || !location.Objects.TryGetValue(machine.Tile, out _))
            return false;

        if (!api.TryGetLinkedEndpoint(location, machine.Tile, out var linkedEndpoint, out _, out _)
            || linkedEndpoint is null
            || !linkedEndpoint.Active)
        {
            return false;
        }

        endpoint = linkedEndpoint;
        return true;
    }
}
