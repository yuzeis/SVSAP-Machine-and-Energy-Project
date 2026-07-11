using Koizumi.SVSAP.Api;
using Koizumi.SVSAPME.Api;
using Microsoft.Xna.Framework;
using SVSAPME.Content;
using SVSAPME.Models;
using SVSAPME.UI;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Objects;
using System.Reflection;
using System.Text.Json;
using SObject = StardewValley.Object;

namespace SVSAPME.Services;

internal sealed class MachineRuntimeService
{
    private const int PrototypeTransferThroughput = 64;
    internal const int PoweredTransferUpgradeSlotCount = 4;
    private const string PoweredTransferHalfWhCreditKey = ModItemCatalog.UniqueId + "/PoweredTransferHalfWhCredit";
    private const string PoweredTransferUpgradeSlotsKey = ModItemCatalog.UniqueId + "/PoweredTransferUpgradeSlots";
    private const string BatteryDischargerEnabledKey = ModItemCatalog.UniqueId + "/BatteryDischargerEnabled";
    private const string SvsapTransferFilterKey = ModItemCatalog.SvsapUniqueId + "/TransferFilter";
    private const string SvsapTransferFilterListKey = ModItemCatalog.SvsapUniqueId + "/TransferFilters";
    private const string SvsapTransferFilterBlacklistKey = ModItemCatalog.SvsapUniqueId + "/TransferFilterBlacklist";
    private const string SvsapTransferOreDictionaryKey = ModItemCatalog.SvsapUniqueId + "/TransferOreDictionary";
    private const string SvsapTransferFacingDirectionKey = ModItemCatalog.SvsapUniqueId + "/TransferFacingDirection";
    private const string SvsapTransferQualityStrategyKey = ModItemCatalog.SvsapUniqueId + "/TransferQualityStrategy";
    private const string SvsapTransferMinSourceKeepKey = ModItemCatalog.SvsapUniqueId + "/TransferMinSourceKeep";
    private const string SvsapTransferTargetKeepKey = ModItemCatalog.SvsapUniqueId + "/TransferTargetKeep";
    private const string SvsapTransferItemsPerOperationKey = ModItemCatalog.SvsapUniqueId + "/TransferItemsPerOperation";
    private static readonly Vector2[] AdjacentOffsets =
    {
        new(0, -1),
        new(1, 0),
        new(0, 1),
        new(-1, 0)
    };
    private static readonly string[] PoweredTransferModDataKeys =
    {
        SvsapTransferFilterKey,
        SvsapTransferFilterListKey,
        SvsapTransferFilterBlacklistKey,
        SvsapTransferOreDictionaryKey,
        SvsapTransferFacingDirectionKey,
        SvsapTransferQualityStrategyKey,
        SvsapTransferMinSourceKeepKey,
        SvsapTransferTargetKeepKey,
        SvsapTransferItemsPerOperationKey,
        PoweredTransferUpgradeSlotsKey
    };

    private readonly MachineStateRepository repository;
    private readonly MachineRegistryService registry;
    private readonly EnergyNetworkManager energy;
    private readonly EnergyTelemetryService telemetry;
    private readonly Func<ISvsapApi?> getSvsapApi;
    private readonly Func<ModConfig> getConfig;
    private readonly IInputHelper inputHelper;
    private readonly IMonitor monitor;
    private Func<SvsapmeMachineActionRequest, bool>? sendClientAction;
    private Func<Guid, bool>? sendSnapshotRequest;
    private uint lastRouteTick;

    public MachineRuntimeService(
        MachineStateRepository repository,
        MachineRegistryService registry,
        EnergyNetworkManager energy,
        EnergyTelemetryService telemetry,
        Func<ISvsapApi?> getSvsapApi,
        Func<ModConfig> getConfig,
        IInputHelper inputHelper,
        IMonitor monitor)
    {
        this.repository = repository;
        this.registry = registry;
        this.energy = energy;
        this.telemetry = telemetry;
        this.getSvsapApi = getSvsapApi;
        this.getConfig = getConfig;
        this.inputHelper = inputHelper;
        this.monitor = monitor;
    }

    public void SetClientActionSender(Func<SvsapmeMachineActionRequest, bool> sender)
    {
        this.sendClientAction = sender;
    }

    public void SetSnapshotRequestSender(Func<Guid, bool> sender)
    {
        this.sendSnapshotRequest = sender;
    }

#if DEBUG
    internal void RunRouteTickForE2E()
    {
        this.lastRouteTick = (uint)Math.Max(0, Game1.ticks);
        this.RunRouteTick();
    }

    internal bool SuppressAutomaticRouteTicksForE2E { get; set; }
#endif

    public void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
#if DEBUG
        if (this.SuppressAutomaticRouteTicksForE2E)
            return;
#endif

        if (!Context.IsMainPlayer || !Context.IsWorldReady)
            return;

        var interval = Math.Max(1, this.getConfig().EnergyTickInterval);
        if (e.Ticks - this.lastRouteTick < interval)
            return;

        this.lastRouteTick = e.Ticks;
        this.RunRouteTick();
    }

    public void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.activeClickableMenu is not null || !e.Button.IsActionButton())
            return;

        var tile = e.Cursor.GrabTile;
        var location = Game1.currentLocation;
        if (location is null || !location.Objects.TryGetValue(tile, out var placedObject))
            return;

        if (Game1.player.CurrentItem?.QualifiedItemId == "(O)" + ModItemCatalog.SvsapLinkTool)
            return;

        if (Game1.player.CurrentItem is null && IsPoweredConfigurableMachine(placedObject.QualifiedItemId) && Context.IsMainPlayer)
        {
            Game1.activeClickableMenu = new PoweredTransferMenu(placedObject, location, tile, this);
            this.HelperSuppress(e);
            return;
        }

        if (Game1.player.CurrentItem is null && this.TryOpenMachineStatusMenu(placedObject, location, tile))
        {
            this.HelperSuppress(e);
            return;
        }

        if (IsPoweredConfigurableMachine(placedObject.QualifiedItemId))
        {
            if (Game1.player.CurrentItem is null)
            {
                var filter = FormatPoweredFilterSummary(placedObject, null);
                Game1.addHUDMessage(new HUDMessage(
                    string.IsNullOrWhiteSpace(filter)
                        ? ModText.Get("hud.poweredFilter.empty", "SVSAPME powered filter is empty.")
                        : ModText.Get("hud.poweredFilter.value", "SVSAPME powered filter: {{filter}}", new { filter }),
                    HUDMessage.newQuest_type));
                this.HelperSuppress(e);
                return;
            }

            if (!Context.IsMainPlayer)
            {
                this.TrySendClientAction(
                    placedObject,
                    SvsapmeMachineActionKind.ConfigurePoweredFilter,
                    Game1.player.CurrentItem.QualifiedItemId,
                    Math.Clamp(Game1.player.CurrentItem.Stack, 1, 999),
                    Game1.player.FarmingLevel);
                this.HelperSuppress(e);
                return;
            }

            var result = this.TryConfigurePoweredFilter(
                placedObject,
                location,
                tile,
                Game1.player.CurrentItem.QualifiedItemId,
                Math.Clamp(Game1.player.CurrentItem.Stack, 1, 999));
            Game1.addHUDMessage(new HUDMessage(result.Message, result.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));
            this.HelperSuppress(e);
            return;
        }

        if (placedObject.QualifiedItemId == "(BC)" + ModItemCatalog.EnergyMonitorTerminal)
        {
            this.ShowEnergyMonitor(location, tile);
            this.HelperSuppress(e);
            return;
        }

        if (placedObject.QualifiedItemId is ("(BC)" + ModItemCatalog.ElectricFurnace) or ("(BC)" + ModItemCatalog.ElectricGeodeCrusher))
        {
            if (!Context.IsMainPlayer)
            {
                if (this.TrySendElectricMachineClientAction(placedObject))
                    this.HelperSuppress(e);

                return;
            }

            if (this.TryHandleElectricMachineManualUse(placedObject, location, tile))
                this.HelperSuppress(e);

            return;
        }

        if (placedObject.QualifiedItemId != "(BC)" + ModItemCatalog.CarbonGenerator)
            return;

        var fuel = Game1.player.CurrentItem;
        if (fuel is null
            || fuel.Stack <= 0
            || !CarbonGeneratorFuelRules.TryGetFuelWh(fuel.QualifiedItemId, this.getConfig().EnableExtendedGeneratorFuels, out _))
        {
            Game1.addHUDMessage(new HUDMessage(this.getConfig().EnableExtendedGeneratorFuels
                ? ModText.Get("hud.carbonGenerator.holdExtendedFuel", "Hold Coal, Wood, Hardwood, Fiber, or Sap to fuel the Carbon Generator.")
                : ModText.Get("hud.carbonGenerator.holdCoal", "Hold Coal to fuel the Carbon Generator."), HUDMessage.error_type));
            this.HelperSuppress(e);
            return;
        }

        if (!Context.IsMainPlayer)
        {
            this.TrySendClientAction(
                placedObject,
                SvsapmeMachineActionKind.FuelCarbonGenerator,
                fuel.QualifiedItemId,
                1,
                Game1.player.FarmingLevel);
            this.HelperSuppress(e);
            return;
        }

        var fuelResult = this.TryFuelCarbonGenerator(placedObject, location, tile, fuel.QualifiedItemId);
        if (fuelResult.Success && fuelResult.ConsumeEscrowedItem)
            Game1.player.reduceActiveItemByOne();

        Game1.addHUDMessage(new HUDMessage(fuelResult.Message, fuelResult.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));
        this.HelperSuppress(e);
    }

    private bool TrySendClientAction(
        SObject placedObject,
        SvsapmeMachineActionKind actionKind,
        string qualifiedItemId,
        int count,
        int farmingLevel)
    {
        if (this.sendClientAction is null)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.multiplayerActionSenderNotReady", "SVSAPME multiplayer action sender is not ready."), HUDMessage.error_type));
            return false;
        }

        if (!TryReadMachineGuid(placedObject, out var machineGuid))
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.machineIdentityNotSynced", "SVSAPME machine identity has not synced from the host yet."), HUDMessage.error_type));
            return false;
        }

        return this.sendClientAction(new SvsapmeMachineActionRequest
        {
            TransactionId = Guid.NewGuid(),
            MachineGuid = machineGuid,
            ActionKind = actionKind,
            QualifiedItemId = qualifiedItemId,
            Count = count,
            FarmingLevel = farmingLevel
        });
    }

    private bool TrySendElectricMachineClientAction(SObject placedObject)
    {
        var held = Game1.player.CurrentItem;
        if (held is null || held.Stack <= 0 || !IsIdleMachine(placedObject))
            return false;

        if (placedObject.QualifiedItemId == "(BC)" + ModItemCatalog.ElectricFurnace)
        {
            if (!ElectricMachineRules.TryGetFurnaceRecipe(held.QualifiedItemId, out var recipe)
                || held.Stack < recipe.InputCount)
            {
                return false;
            }

            return this.TrySendClientAction(
                placedObject,
                SvsapmeMachineActionKind.StartElectricFurnace,
                recipe.InputQualifiedItemId,
                recipe.InputCount,
                Game1.player.FarmingLevel);
        }

        if (placedObject.QualifiedItemId == "(BC)" + ModItemCatalog.ElectricGeodeCrusher
            && Utility.IsGeode(held, disallow_special_geodes: true))
        {
            return this.TrySendClientAction(
                placedObject,
                SvsapmeMachineActionKind.StartElectricGeodeCrusher,
                held.QualifiedItemId,
                1,
                Game1.player.FarmingLevel);
        }

        return false;
    }

    private bool TryOpenMachineStatusMenu(SObject placedObject, GameLocation location, Vector2 tile)
    {
        if (!ModItemCatalog.IsSvsapmeBigCraftable(placedObject.QualifiedItemId)
            || IsFarmMachine(placedObject.QualifiedItemId)
            || SingleBlockProcessorRules.IsProcessorMachine(placedObject.QualifiedItemId)
            || placedObject.QualifiedItemId == "(BC)" + ModItemCatalog.ReclaimCrate)
        {
            return false;
        }

        if (!Context.IsMainPlayer)
        {
            if (TryReadMachineGuid(placedObject, out var remoteMachineGuid))
                return this.TrySendSnapshotRequest(remoteMachineGuid);

            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.multiplayer.unknownMachineGuid", "MachineGuid is unknown on the host."), HUDMessage.error_type));
            return true;
        }

        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return false;
        }

        var actions = new List<SvsapmeMenuAction>();
        if (placedObject.QualifiedItemId == "(BC)" + ModItemCatalog.BatteryDischarger)
        {
            actions.Add(new SvsapmeMenuAction(
                GetBatteryDischargerEnabled(state)
                    ? ModText.Get("ui.status.action.disable", "Disable")
                    : ModText.Get("ui.status.action.enable", "Enable"),
                () =>
                {
                    var enabled = !GetBatteryDischargerEnabled(state);
                    state.ModData[BatteryDischargerEnabledKey] = enabled.ToString();
                    this.repository.Save();
                    return enabled
                        ? ModText.Get("ui.machine.batteryDischarger.enabled", "Battery Discharger enabled.")
                        : ModText.Get("ui.machine.batteryDischarger.disabled", "Battery Discharger disabled.");
                }));
        }

        Game1.activeClickableMenu = new SvsapmeStatusMenu(
            placedObject.DisplayName,
            () => this.BuildMachineStatusLines(placedObject, location, tile, state),
            actions);
        return true;
    }

    private IReadOnlyList<string> BuildMachineStatusLines(SObject placedObject, GameLocation location, Vector2 tile, MachineState state)
    {
        var lines = new List<string>
        {
            ModText.Get("ui.machine.name", "Machine: {{name}}", new { name = placedObject.DisplayName }),
            ModText.Get("ui.machine.location", "Location: {{location}} ({{x}}, {{y}})", new { location = location.NameOrUniqueName, x = tile.X.ToString("0"), y = tile.Y.ToString("0") }),
            ModText.Get("ui.machine.guid", "GUID: {{guid}}", new { guid = state.MachineGuid.ToString("N") })
        };

        var api = this.getSvsapApi();
        ISvsapEndpointInfo? endpoint = null;
        var endpointCode = SvsapApiErrorCode.InternalError;
        var endpointMessage = string.Empty;
        if (api is not null
            && api.TryGetLinkedEndpoint(location, tile, out endpoint, out endpointCode, out endpointMessage)
            && endpoint is not null)
        {
            lines.Add(endpoint.Active
                ? ModText.Get("ui.machine.network.active", "Network: {{network}} active", new { network = FormatShortGuid(endpoint.NetworkId) })
                : ModText.Get("ui.machine.network.inactive", "Network: {{network}} inactive", new { network = FormatShortGuid(endpoint.NetworkId) }));
            var energyCode = SvsapmeEnergyErrorCode.InternalError;
            if (endpoint.Active && this.energy.TryGetNetworkEnergy(endpoint.NetworkId, out var networkStoredWh, out var networkCapacityWh, out energyCode))
                lines.Add(ModText.Get("ui.machine.networkEnergy", "Network energy: {{stored}} / {{capacity}}", new { stored = FormatWh(networkStoredWh), capacity = FormatWh(networkCapacityWh) }));
            else if (endpoint.Active)
                lines.Add(ModText.Get("ui.machine.networkEnergyUnavailable", "Network energy: unavailable ({{code}})", new { code = energyCode.ToString() }));
            if (endpoint.Active)
            {
                var flow = this.telemetry.GetSnapshot(endpoint.NetworkId);
                lines.Add(ModText.Get("ui.energyMeter.lastTick", "Last flow: +{{generated}} / -{{consumed}} / net {{net}}", new
                {
                    generated = FormatWh(flow.LastGeneratedWh),
                    consumed = FormatWh(flow.LastConsumedWh),
                    net = FormatSignedWh(flow.LastNetWh)
                }));
                lines.Add(ModText.Get("ui.energyMeter.today", "Today: +{{generated}} / -{{consumed}} / net {{net}}", new
                {
                    generated = FormatWh(flow.TodayGeneratedWh),
                    consumed = FormatWh(flow.TodayConsumedWh),
                    net = FormatSignedWh(flow.TodayNetWh)
                }));
            }
        }
        else
        {
            lines.Add(api is null
                ? ModText.Get("ui.machine.network.apiUnavailable", "Network: SVSAP API unavailable")
                : ModText.Get("ui.machine.network.notLinked", "Network: not linked ({{code}}: {{message}})", new { code = endpointCode.ToString(), message = endpointMessage }));
        }

        if (state.CapacityWh > 0)
            lines.Add(ModText.Get("ui.machine.internalEnergy", "Internal energy: {{stored}} / {{capacity}}", new { stored = FormatWh(Math.Clamp(state.StoredWh, 0, state.CapacityWh)), capacity = FormatWh(state.CapacityWh) }));
        else if (state.StoredWh > 0)
            lines.Add(ModText.Get("ui.machine.bufferedEnergy", "Buffered energy: {{stored}}", new { stored = FormatWh(state.StoredWh) }));

        if (state.OutputBuffer.Count > 0)
            lines.Add(ModText.Get("ui.machine.outputBuffer", "Output buffer: {{items}}", new { items = FormatBufferedItems(state.OutputBuffer) }));

        this.AddPortStatusLines(lines, placedObject.QualifiedItemId);

        switch (placedObject.QualifiedItemId)
        {
            case "(BC)" + ModItemCatalog.CarbonGenerator:
                lines.Add(ModText.Get("ui.machine.carbon.role", "Role: stores fuel energy briefly, then deposits it into linked network storage."));
                lines.Add(ModText.Get(
                    "ui.machine.carbon.fuels",
                    "Accepted fuels: {{fuels}}",
                    new
                    {
                        fuels = this.getConfig().EnableExtendedGeneratorFuels
                            ? ModText.Get("ui.machine.carbon.fuels.extended", "Coal, Wood, Hardwood, Fiber, Sap")
                            : ModText.Get("ui.machine.carbon.fuels.coal", "Coal")
                    }));
                break;

            case "(BC)" + ModItemCatalog.SolarNetworkPanel:
                lines.Add(ModText.Get("ui.machine.forecastOutput", "Today forecast output: {{wh}}", new { wh = FormatWh(GetSolarPanelWhForStatus(location)) }));
                lines.Add(ModText.Get("ui.machine.weather.solar", "Weather: outdoors={{outdoors}}, rain={{rain}}, lightning={{lightning}}, season={{season}}", new { outdoors = FormatBool(location.IsOutdoors), rain = FormatBool(location.IsRainingHere()), lightning = FormatBool(location.IsLightningHere()), season = location.GetSeason().ToString() }));
                break;

            case "(BC)" + ModItemCatalog.LightningCapacitor:
                lines.Add(ModText.Get("ui.machine.forecastOutput", "Today forecast output: {{wh}}", new { wh = FormatWh(GetLightningCapacitorWhForStatus(location)) }));
                lines.Add(ModText.Get("ui.machine.weather.lightning", "Weather: outdoors={{outdoors}}, lightning={{lightning}}", new { outdoors = FormatBool(location.IsOutdoors), lightning = FormatBool(location.IsLightningHere()) }));
                break;

            case "(BC)" + ModItemCatalog.BatterySynthesizer:
                lines.Add(ModText.Get("ui.machine.synth.progress", "Charge progress: {{progress}} / {{required}}", new { progress = FormatWh(Math.Clamp(state.ProgressWh, 0, BatterySynthesizerRules.RequiredWh)), required = FormatWh(BatterySynthesizerRules.RequiredWh) }));
                if (api is not null && endpoint is not null && endpoint.Active)
                {
                    foreach (var material in BatterySynthesizerRules.Materials)
                        lines.Add(ModText.Get(
                            "ui.machine.synth.material",
                            "Material {{item}}: {{available}}/{{required}}",
                            new
                            {
                                item = FormatItem(material.QualifiedItemId),
                                available = api.GetAvailableCount(endpoint.NetworkId, material.QualifiedItemId, quality: null).ToString("N0"),
                                required = material.Count.ToString("N0")
                            }));
                }
                break;

            case "(BC)" + ModItemCatalog.BatteryDischarger:
                lines.Add(ModText.Get("ui.machine.enabled", "Machine enabled: {{enabled}}", new { enabled = FormatBool(GetBatteryDischargerEnabled(state)) }));
                lines.Add(ModText.Get("ui.machine.batteryDischarger.output", "Output per Battery Pack: {{wh}}", new { wh = FormatWh(GetBatteryDischargerOutputWh()) }));
                if (api is not null && endpoint is not null && endpoint.Active)
                    lines.Add(ModText.Get("ui.machine.batteryDischarger.networkBatteries", "Battery Packs in network: {{count}}", new { count = api.GetAvailableCount(endpoint.NetworkId, BatteryDischargerRules.BatteryPackQualifiedItemId, quality: null).ToString("N0") }));
                break;
        }

        if (IsPoweredConfigurableMachine(placedObject.QualifiedItemId))
            this.AddPoweredStatusLines(lines, placedObject);

        if (placedObject.QualifiedItemId is ("(BC)" + ModItemCatalog.ElectricFurnace) or ("(BC)" + ModItemCatalog.ElectricGeodeCrusher))
            this.AddElectricMachineStatusLines(lines, placedObject);

        return lines;
    }

    private void AddPortStatusLines(List<string> lines, string qualifiedItemId)
    {
        foreach (var port in MachinePortCatalog.GetPorts(qualifiedItemId))
        {
            lines.Add(ModText.Get(
                "ui.machine.port.line",
                "Port {{role}}: {{side}} - {{description}}",
                new
                {
                    role = ModText.Get(port.RoleKey, port.RoleKey),
                    side = ModText.Get(port.SideKey, port.SideKey),
                    description = ModText.Get(port.DescriptionKey, port.DescriptionKey)
                }));
        }
    }

    private void AddPoweredStatusLines(List<string> lines, SObject placedObject)
    {
        var tier = GetPoweredTier(placedObject.QualifiedItemId);
        var upgrades = GetPoweredUpgradeSlots(placedObject, null);
        var capacityMultiplier = HasPoweredUpgrade(upgrades, ModItemCatalog.SvsapCapacityCard) ? 2 : 1;
        var speedMultiplier = HasPoweredUpgrade(upgrades, ModItemCatalog.SvsapSpeedCard) ? 2 : 1;
        var blacklist = GetBoolModData(placedObject, null, SvsapTransferFilterBlacklistKey, false);
        var quality = Enum.TryParse(placedObject.modData.GetValueOrDefault(SvsapTransferQualityStrategyKey), out PoweredTransferQualityStrategy parsedQuality)
            ? parsedQuality.ToString()
            : PoweredTransferQualityStrategy.LowQualityFirst.ToString();
        lines.Add(ModText.Get("ui.machine.powered.tier", "Powered tier: {{tier}}", new { tier = tier.ToString() }));
        lines.Add(ModText.Get(
            "ui.machine.powered.throughput",
            "Throughput: {{powered}}/route tick; prototype fallback {{prototype}}/route tick",
            new
            {
                powered = (PoweredTransferRules.GetEffectivePoweredThroughput(tier, PrototypeTransferThroughput) * capacityMultiplier).ToString("N0"),
                prototype = PrototypeTransferThroughput.ToString("N0")
            }));
        lines.Add(ModText.Get("ui.machine.powered.interval", "Effective interval: {{ticks}} ticks", new
        {
            ticks = Math.Max(1, (int)Math.Ceiling(this.getConfig().EnergyTickInterval / (double)speedMultiplier)).ToString("N0")
        }));
        lines.Add(ModText.Get("ui.machine.powered.energyCost", "Energy cost: 0.5 Wh/item"));
        var filterSummary = FormatPoweredFilterSummary(placedObject, null);
        lines.Add(string.IsNullOrWhiteSpace(filterSummary)
            ? ModText.Get("ui.machine.powered.filter.empty", "Filter: empty")
            : ModText.Get("ui.machine.powered.filter.value", "Filter: {{filter}} ({{mode}})", new { filter = filterSummary, mode = blacklist ? ModText.Get("ui.machine.powered.filter.blacklist", "blacklist") : ModText.Get("ui.machine.powered.filter.whitelist", "whitelist") }));
        lines.Add(ModText.Get("ui.machine.powered.quality", "Quality strategy: {{quality}}", new { quality }));
        if (IsPoweredImporterMachine(placedObject.QualifiedItemId))
            lines.Add(ModText.Get("ui.machine.powered.importerKeep", "Importer source keep: {{count}}", new { count = GetNonNegativeIntModData(placedObject, null, SvsapTransferMinSourceKeepKey, 0).ToString("N0") }));
        if (IsPoweredExporterMachine(placedObject.QualifiedItemId))
            lines.Add(ModText.Get("ui.machine.powered.exporterKeep", "Exporter target keep: {{count}}", new { count = GetNonNegativeIntModData(placedObject, null, SvsapTransferTargetKeepKey, 0).ToString("N0") }));
        if (IsPoweredMachineInterfaceMachine(placedObject.QualifiedItemId))
            lines.Add(ModText.Get("ui.machine.powered.range", "Powered range: tier-scaled square; unpowered fallback is orthogonal adjacent 4 tiles."));
    }

    private void AddElectricMachineStatusLines(List<string> lines, SObject placedObject)
    {
        lines.Add(placedObject.QualifiedItemId == "(BC)" + ModItemCatalog.ElectricFurnace
            ? ModText.Get("ui.machine.electricFurnace.status", "Electric Furnace: {{wh}} per powered run, powered time is 2x faster.", new { wh = FormatWh(ElectricMachineRules.FurnaceWhPerRun) })
            : ModText.Get("ui.machine.electricGeodeCrusher.status", "Electric Geode Crusher: {{wh}} per powered geode.", new { wh = FormatWh(ElectricMachineRules.GeodeCrusherWhPerRun) }));
        lines.Add(IsIdleMachine(placedObject)
            ? ModText.Get("ui.machine.state.idle", "Machine state: idle")
            : ModText.Get("ui.machine.state.processing", "Machine state: processing/output pending"));
    }

    private bool GetBatteryDischargerEnabled(MachineState state)
    {
        return bool.TryParse(state.ModData.GetValueOrDefault(BatteryDischargerEnabledKey), out var enabled)
            ? enabled
            : this.getConfig().AllowBatteryDischarge;
    }

    private long GetBatteryDischargerOutputWh()
    {
        var config = this.getConfig();
        return (long)Math.Round(BatteryDischargerRules.DefaultOutputWh * Math.Clamp(config.BatteryDischargeEfficiency / 0.8, 0.0, 10.0));
    }

    private static long GetSolarPanelWhForStatus(GameLocation location)
    {
        _ = location.GetWeather();
        return EnergyProductionRules.GetSolarPanelWh(location.IsOutdoors, location.IsRainingHere(), location.IsLightningHere(), location.GetSeason());
    }

    private static long GetLightningCapacitorWhForStatus(GameLocation location)
    {
        _ = location.GetWeather();
        return EnergyProductionRules.GetLightningCapacitorWh(location.IsOutdoors, location.IsLightningHere());
    }

    private static string FormatWh(long wh)
    {
        return $"{Math.Max(0, wh) / 1000m:0.00} kWh";
    }

    private static string FormatSignedWh(long wh)
    {
        var sign = wh >= 0 ? "+" : "-";
        return sign + FormatWh(Math.Abs(wh));
    }

    private static string FormatEnergyCode(SvsapmeEnergyErrorCode code)
    {
        return code switch
        {
            SvsapmeEnergyErrorCode.NoEnergyCell => ModText.Get("ui.energy.reason.noEnergyCell", "no energy cell"),
            SvsapmeEnergyErrorCode.StorageFull => ModText.Get("ui.energy.reason.storageFull", "storage full"),
            SvsapmeEnergyErrorCode.InsufficientEnergy => ModText.Get("ui.energy.reason.insufficient", "insufficient energy"),
            SvsapmeEnergyErrorCode.NotHost => ModText.Get("ui.energy.reason.notHost", "not host"),
            SvsapmeEnergyErrorCode.NetworkUnknown => ModText.Get("ui.energy.reason.notLinked", "network unknown"),
            SvsapmeEnergyErrorCode.SubsystemDisabled => ModText.Get("ui.energy.reason.configDisabled", "disabled"),
            _ => code.ToString()
        };
    }

    private static string FormatTelemetryTotals(IReadOnlyList<EnergyTelemetryReasonTotal> totals)
    {
        if (totals.Count == 0)
            return ModText.Get("ui.common.none", "none");

        return string.Join(", ", totals.Select(total => $"{total.Reason} {FormatWh(total.Wh)}"));
    }

    private static string FormatTelemetryDevice(EnergyTelemetryReasonTotal total)
    {
        if (string.IsNullOrWhiteSpace(total.DeviceId))
            return total.Reason;
        var separator = total.DeviceId.IndexOf(':');
        var identity = separator >= 0 && separator + 1 < total.DeviceId.Length
            ? total.DeviceId[(separator + 1)..]
            : total.DeviceId;
        var parts = identity.Split('|');
        if (parts.Length >= 3 && string.Equals(parts[0], "machine", StringComparison.Ordinal))
        {
            var name = FormatItem(parts[1]);
            var shortGuid = parts[2].Length >= 8 ? parts[2][..8] : parts[2];
            return $"{name} [{shortGuid}]";
        }

        return identity;
    }

    private static string BuildMachineEnergyReason(MachineState state, string operation)
    {
        return $"machine|{state.QualifiedItemId}|{state.MachineGuid:N}|{operation}";
    }

    private static string BuildMachineEnergyReason(SObject? machine, string operation)
    {
        if (machine is null)
            return operation;

        var rawGuid = machine.modData.GetValueOrDefault(MachineRegistryService.MachineGuidKey);
        var guid = Guid.TryParse(rawGuid, out var parsed) ? parsed : Guid.Empty;
        return $"machine|{machine.QualifiedItemId}|{guid:N}|{operation}";
    }

    private static string FormatBool(bool value)
    {
        return value
            ? ModText.Get("ui.common.yes", "yes")
            : ModText.Get("ui.common.no", "no");
    }

    private static string FormatShortGuid(Guid guid)
    {
        return guid == Guid.Empty ? ModText.Get("ui.common.none", "none") : guid.ToString("N")[..8];
    }

    private static string FormatItem(string qualifiedItemId)
    {
        try
        {
            return ItemRegistry.Create(qualifiedItemId).DisplayName;
        }
        catch
        {
            return qualifiedItemId;
        }
    }

    private static string FormatPoweredFilterSummary(SObject placedObject, MachineState? state)
    {
        var ids = GetPoweredFilterIds(placedObject, state);
        if (ids.Count == 0)
            return string.Empty;

        var names = ids.Take(3).Select(FormatItem).ToList();
        var suffix = ids.Count > names.Count ? $" +{ids.Count - names.Count:N0}" : string.Empty;
        return string.Join(", ", names) + suffix;
    }

    private static string FormatBufferedItems(IEnumerable<BufferedItemStack> stacks)
    {
        var parts = stacks
            .Where(stack => stack.Stack > 0)
            .Select(stack => $"{FormatItem(stack.QualifiedItemId)} x{stack.Stack:N0}")
            .ToList();
        return parts.Count == 0 ? ModText.Get("ui.common.empty", "empty") : string.Join(", ", parts);
    }

    internal SvsapmeMachineActionApplyResult TryConfigurePoweredFilter(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        string filterQualifiedItemId,
        int count)
    {
        if (!IsPoweredConfigurableMachine(placedObject.QualifiedItemId))
            return new(false, false, ModText.Get("hud.poweredFilter.notAccepted", "Target machine does not accept a powered filter."));

        if (string.IsNullOrWhiteSpace(filterQualifiedItemId))
            return new(false, false, ModText.Get("hud.poweredFilter.missingItem", "Powered filter item is missing."));

        var clampedCount = Math.Clamp(count, 1, 999);
        if (filterQualifiedItemId == "(O)" + ModItemCatalog.SvsapFilterCard)
        {
            var hasFilter = GetPoweredFilterIds(placedObject, null).Count > 0;
            if (!hasFilter)
            {
                placedObject.modData.Remove(SvsapTransferFilterBlacklistKey);
                this.SyncPoweredTransferModData(placedObject, location, tile);
                return new(true, false, ModText.Get("hud.poweredFilter.alreadyEmpty", "Powered transfer filter is already empty."));
            }

            if (!GetBoolModData(placedObject, null, SvsapTransferFilterBlacklistKey, false))
            {
                placedObject.modData[SvsapTransferFilterBlacklistKey] = true.ToString();
                this.SyncPoweredTransferModData(placedObject, location, tile);
                return new(true, false, ModText.Get("hud.poweredFilter.modeBlacklist", "Powered transfer filter mode: blacklist."));
            }

            placedObject.modData.Remove(SvsapTransferFilterKey);
            placedObject.modData.Remove(SvsapTransferFilterListKey);
            placedObject.modData.Remove(SvsapTransferFilterBlacklistKey);
            placedObject.modData.Remove(SvsapTransferMinSourceKeepKey);
            placedObject.modData.Remove(SvsapTransferTargetKeepKey);
            this.SyncPoweredTransferModData(placedObject, location, tile);
            return new(true, false, ModText.Get("hud.poweredFilter.cleared", "Powered transfer filter cleared."));
        }

        if (IsPoweredUpgradeCard(filterQualifiedItemId))
            return new(false, false, ModText.Get("hud.poweredUpgrade.useMenu", "Open the powered transfer console and install this card in an upgrade slot."));

        Item probe;
        try
        {
            probe = ItemRegistry.Create(filterQualifiedItemId);
        }
        catch (Exception ex)
        {
            return new(false, false, ModText.Get("hud.poweredFilter.invalidItem", "Powered filter item is invalid: {{message}}", new { message = ex.Message }));
        }

        if (GetPoweredFilterIds(placedObject, null).Contains(filterQualifiedItemId, StringComparer.Ordinal))
        {
            if (IsPoweredImporterMachine(placedObject.QualifiedItemId))
            {
                placedObject.modData[SvsapTransferMinSourceKeepKey] = clampedCount.ToString();
                this.SyncPoweredTransferModData(placedObject, location, tile);
                return new(true, false, ModText.Get("hud.poweredFilter.importerKeep", "Powered Importer will keep {{count}} in the source.", new { count = clampedCount.ToString("N0") }));
            }

            if (IsPoweredExporterMachine(placedObject.QualifiedItemId))
            {
                placedObject.modData[SvsapTransferTargetKeepKey] = clampedCount.ToString();
                this.SyncPoweredTransferModData(placedObject, location, tile);
                return new(true, false, ModText.Get("hud.poweredFilter.exporterKeep", "Powered Exporter will maintain {{count}} in the target.", new { count = clampedCount.ToString("N0") }));
            }

            placedObject.modData[SvsapTransferItemsPerOperationKey] = clampedCount.ToString();
            this.SyncPoweredTransferModData(placedObject, location, tile);
            return new(true, false, ModText.Get("hud.poweredFilter.interfaceFeedCount", "Powered Machine Interface feed count set to {{count}}.", new { count = clampedCount.ToString("N0") }));
        }

        WritePoweredFilterIds(placedObject, new[] { filterQualifiedItemId });
        placedObject.modData.Remove(SvsapTransferFilterBlacklistKey);
        if (IsPoweredMachineInterfaceMachine(placedObject.QualifiedItemId))
            placedObject.modData[SvsapTransferItemsPerOperationKey] = clampedCount.ToString();
        else
            placedObject.modData.Remove(SvsapTransferItemsPerOperationKey);

        this.SyncPoweredTransferModData(placedObject, location, tile);
        return new(true, false, ModText.Get("hud.poweredFilter.set", "Powered transfer filter set to {{item}}.", new { item = probe.DisplayName }));
    }

    internal IReadOnlyList<PoweredTransferFilterSlotView> GetPoweredFilterSlotViews(SObject placedObject)
    {
        var ids = GetPoweredFilterSlots(placedObject, null);
        var result = new List<PoweredTransferFilterSlotView>();
        for (var index = 0; index < 9; index++)
        {
            if (string.IsNullOrWhiteSpace(ids[index]) || !TryCreateItem(ids[index], out var item))
            {
                result.Add(PoweredTransferFilterSlotView.Empty(index));
                continue;
            }

            result.Add(new PoweredTransferFilterSlotView
            {
                SlotIndex = index,
                QualifiedItemId = ids[index],
                DisplayName = item.DisplayName,
                Item = item,
                OreGroups = GetPoweredOreGroups(item).OrderBy(group => group, StringComparer.Ordinal).ToList()
            });
        }

        return result;
    }

    internal IReadOnlyList<string> GetPoweredUpgradeSlotIds(SObject placedObject)
    {
        return GetPoweredUpgradeSlots(placedObject, null);
    }

    internal bool HasPoweredUpgrade(SObject placedObject, string qualifiedItemId)
    {
        return GetPoweredUpgradeSlots(placedObject, null).Contains(qualifiedItemId, StringComparer.Ordinal);
    }

    internal SvsapmeMachineActionApplyResult TryInstallPoweredUpgrade(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        int slotIndex,
        string qualifiedItemId)
    {
        if (!IsPoweredConfigurableMachine(placedObject.QualifiedItemId))
            return new(false, false, ModText.Get("hud.poweredFilter.notAccepted", "Target machine does not accept a powered filter."));

        if (slotIndex is < 0 or >= PoweredTransferUpgradeSlotCount)
            return new(false, false, ModText.Get("hud.poweredUpgrade.invalidSlot", "Powered upgrade slot is invalid."));

        if (!IsPoweredUpgradeCard(qualifiedItemId))
            return new(false, false, ModText.Get("hud.poweredUpgrade.invalidCard", "This item is not a supported powered transfer upgrade card."));

        var slots = GetPoweredUpgradeSlots(placedObject, null);
        if (!string.IsNullOrWhiteSpace(slots[slotIndex]))
            return new(false, false, ModText.Get("hud.poweredUpgrade.slotOccupied", "Powered upgrade slot is occupied."));

        if (slots.Contains(qualifiedItemId, StringComparer.Ordinal))
            return new(false, false, ModText.Get("hud.poweredUpgrade.duplicate", "Only one card of each powered upgrade type can be installed."));

        slots[slotIndex] = qualifiedItemId;
        WritePoweredUpgradeSlots(placedObject, slots);
        this.SyncPoweredTransferModData(placedObject, location, tile);
        return new(true, true, ModText.Get("hud.poweredUpgrade.installed", "Powered upgrade installed: {{item}}.", new { item = FormatItem(qualifiedItemId) }));
    }

    internal SvsapmeMachineActionApplyResult TryRemovePoweredUpgrade(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        int slotIndex)
    {
        var slots = GetPoweredUpgradeSlots(placedObject, null);
        if (slotIndex is < 0 or >= PoweredTransferUpgradeSlotCount || string.IsNullOrWhiteSpace(slots[slotIndex]))
            return new(false, false, ModText.Get("hud.poweredUpgrade.slotEmpty", "Powered upgrade slot is empty."));

        var qualifiedItemId = slots[slotIndex];
        slots[slotIndex] = string.Empty;
        WritePoweredUpgradeSlots(placedObject, slots);
        if (qualifiedItemId == "(O)" + ModItemCatalog.SvsapOreDictionaryCard)
            placedObject.modData.Remove(SvsapTransferOreDictionaryKey);
        if (qualifiedItemId == "(O)" + ModItemCatalog.SvsapQualityCard)
            placedObject.modData.Remove(SvsapTransferQualityStrategyKey);

        this.SyncPoweredTransferModData(placedObject, location, tile);
        var returned = ItemRegistry.Create(qualifiedItemId, 1);
        return new(true, false, ModText.Get("hud.poweredUpgrade.removed", "Powered upgrade removed: {{item}}.", new { item = returned.DisplayName }))
        {
            ReturnedItems = new List<BufferedItemStack> { BufferedItemCodec.FromItem(returned) }
        };
    }

    internal bool TrySetPoweredFilterSlot(SObject placedObject, GameLocation location, Vector2 tile, int slotIndex, string qualifiedItemId, out string message)
    {
        if (!IsPoweredConfigurableMachine(placedObject.QualifiedItemId))
        {
            message = ModText.Get("hud.poweredFilter.notAccepted", "Target machine does not accept a powered filter.");
            return false;
        }

        if (slotIndex is < 0 or >= 9 || string.IsNullOrWhiteSpace(qualifiedItemId))
        {
            message = ModText.Get("hud.poweredFilter.invalidItem", "Powered filter item is invalid: {{message}}", new { message = qualifiedItemId });
            return false;
        }

        if (!TryCreateItem(qualifiedItemId, out var item))
        {
            message = ModText.Get("hud.poweredFilter.invalidItem", "Powered filter item is invalid: {{message}}", new { message = qualifiedItemId });
            return false;
        }

        var ids = GetPoweredFilterSlots(placedObject, null);
        ids[slotIndex] = qualifiedItemId;
        WritePoweredFilterSlots(placedObject, ids);
        if (GetPoweredFilterIds(placedObject, null).Count == 0)
            placedObject.modData.Remove(SvsapTransferFilterBlacklistKey);

        this.SyncPoweredTransferModData(placedObject, location, tile);
        message = ModText.Get("hud.poweredFilter.slotSet", "Powered transfer filter slot {{slot}} set to {{item}}.", new { slot = (slotIndex + 1).ToString("N0"), item = item.DisplayName });
        return true;
    }

    internal bool TryClearPoweredFilterSlot(SObject placedObject, GameLocation location, Vector2 tile, int slotIndex, out string message)
    {
        var ids = GetPoweredFilterSlots(placedObject, null);
        if (slotIndex < 0 || slotIndex >= ids.Count || string.IsNullOrWhiteSpace(ids[slotIndex]))
        {
            message = ModText.Get("hud.poweredFilter.slotEmpty", "Powered transfer filter slot is already empty.");
            return false;
        }

        ids[slotIndex] = string.Empty;
        WritePoweredFilterSlots(placedObject, ids);
        if (GetPoweredFilterIds(placedObject, null).Count == 0)
            placedObject.modData.Remove(SvsapTransferFilterBlacklistKey);

        this.SyncPoweredTransferModData(placedObject, location, tile);
        message = ModText.Get("hud.poweredFilter.slotCleared", "Powered transfer filter slot cleared.");
        return true;
    }

    internal bool TryClearPoweredFilter(SObject placedObject, GameLocation location, Vector2 tile, out string message)
    {
        WritePoweredFilterIds(placedObject, Array.Empty<string>());
        placedObject.modData.Remove(SvsapTransferFilterBlacklistKey);
        placedObject.modData.Remove(SvsapTransferMinSourceKeepKey);
        placedObject.modData.Remove(SvsapTransferTargetKeepKey);
        this.SyncPoweredTransferModData(placedObject, location, tile);
        message = ModText.Get("hud.poweredFilter.cleared", "Powered transfer filter cleared.");
        return true;
    }

    internal bool TryTogglePoweredFilterMode(SObject placedObject, GameLocation location, Vector2 tile, out string message)
    {
        if (GetPoweredFilterIds(placedObject, null).Count == 0)
        {
            message = ModText.Get("hud.poweredFilter.empty", "SVSAPME powered filter is empty.");
            return false;
        }

        var next = !GetBoolModData(placedObject, null, SvsapTransferFilterBlacklistKey, false);
        placedObject.modData[SvsapTransferFilterBlacklistKey] = next.ToString();
        this.SyncPoweredTransferModData(placedObject, location, tile);
        message = next
            ? ModText.Get("hud.poweredFilter.modeBlacklist", "Powered transfer filter mode: blacklist.")
            : ModText.Get("hud.poweredFilter.modeWhitelist", "Powered transfer filter mode: whitelist.");
        return true;
    }

    internal bool TryTogglePoweredOreDictionaryMode(SObject placedObject, GameLocation location, Vector2 tile, out string message)
    {
        if (!this.HasPoweredUpgrade(placedObject, "(O)" + ModItemCatalog.SvsapOreDictionaryCard))
        {
            message = ModText.Get("hud.poweredUpgrade.oreRequired", "Install an ore dictionary card before enabling ore dictionary matching.");
            return false;
        }

        var next = !GetBoolModData(placedObject, null, SvsapTransferOreDictionaryKey, false);
        placedObject.modData[SvsapTransferOreDictionaryKey] = next.ToString();
        this.SyncPoweredTransferModData(placedObject, location, tile);
        message = next
            ? ModText.Get("hud.poweredFilter.oreDictionaryOn", "Powered transfer ore dictionary matching enabled.")
            : ModText.Get("hud.poweredFilter.oreDictionaryOff", "Powered transfer ore dictionary matching disabled.");
        return true;
    }

    internal bool TryTogglePoweredQualityStrategy(SObject placedObject, GameLocation location, Vector2 tile, out string message)
    {
        if (!this.HasPoweredUpgrade(placedObject, "(O)" + ModItemCatalog.SvsapQualityCard))
        {
            message = ModText.Get("hud.poweredUpgrade.qualityRequired", "Install a quality card before changing the quality strategy.");
            return false;
        }

        var strategy = GetPoweredQualityStrategy(placedObject, null) switch
        {
            PoweredTransferQualityStrategy.LowQualityFirst => PoweredTransferQualityStrategy.HighQualityFirst,
            PoweredTransferQualityStrategy.HighQualityFirst => PoweredTransferQualityStrategy.PreserveGoldIridium,
            _ => PoweredTransferQualityStrategy.LowQualityFirst
        };
        placedObject.modData[SvsapTransferQualityStrategyKey] = strategy.ToString();
        this.SyncPoweredTransferModData(placedObject, location, tile);
        message = ModText.Get("hud.poweredFilter.quality", "Powered transfer quality: {{quality}}.", new { quality = FormatPoweredQualityStrategy(strategy) });
        return true;
    }

    internal bool TrySetPoweredFacingDirection(SObject placedObject, GameLocation location, Vector2 tile, int facingDirection, out string message)
    {
        var normalized = NormalizeFacingDirection(facingDirection);
        placedObject.modData[SvsapTransferFacingDirectionKey] = normalized.ToString();
        this.SyncPoweredTransferModData(placedObject, location, tile);
        message = ModText.Get("hud.poweredFilter.direction", "Powered transfer direction: {{direction}}.", new { direction = FormatPoweredFacingDirection(normalized) });
        return true;
    }

    internal int GetPoweredFacingDirection(SObject placedObject)
    {
        var raw = placedObject.modData.GetValueOrDefault(SvsapTransferFacingDirectionKey);
        return int.TryParse(raw, out var parsed) ? NormalizeFacingDirection(parsed) : -1;
    }

    internal bool IsPoweredOreDictionaryModeEnabled(SObject placedObject)
    {
        return GetBoolModData(placedObject, null, SvsapTransferOreDictionaryKey, false);
    }

    internal PoweredTransferMenuView GetPoweredTransferMenuView(SObject placedObject)
    {
        var tier = GetPoweredTier(placedObject.QualifiedItemId);
        var upgrades = GetPoweredUpgradeSlots(placedObject, null);
        var capacityMultiplier = HasPoweredUpgrade(upgrades, ModItemCatalog.SvsapCapacityCard) ? 2 : 1;
        var speedMultiplier = HasPoweredUpgrade(upgrades, ModItemCatalog.SvsapSpeedCard) ? 2 : 1;
        return new PoweredTransferMenuView(
            GetBoolModData(placedObject, null, SvsapTransferFilterBlacklistKey, false),
            IsPoweredOreDictionaryModeEnabled(placedObject),
            GetPoweredQualityStrategy(placedObject, null).ToString(),
            GetPoweredFacingDirection(placedObject),
            GetPoweredFilterSlotViews(placedObject),
            PoweredTransferRules.GetEffectivePoweredThroughput(tier, PrototypeTransferThroughput) * capacityMultiplier,
            Math.Max(1, (int)Math.Ceiling(this.getConfig().EnergyTickInterval / (double)speedMultiplier)),
            0.5m,
            upgrades);
    }

    internal PoweredNetworkStatusView GetPoweredNetworkStatus(GameLocation location, Vector2 tile)
    {
        var api = this.getSvsapApi();
        if (api is null
            || !api.TryGetLinkedEndpoint(location, tile, out var endpoint, out _, out _)
            || endpoint is null
            || !endpoint.Active
            || !this.energy.TryGetNetworkEnergy(endpoint.NetworkId, out var storedWh, out var capacityWh, out _))
        {
            return new PoweredNetworkStatusView(false, 0, 0);
        }

        return new PoweredNetworkStatusView(true, storedWh, capacityWh);
    }

    internal IReadOnlyList<string> DescribePoweredConfigurationLines(SObject placedObject)
    {
        var lines = new List<string>();
        var filter = FormatPoweredFilterSummary(placedObject, null);
        lines.Add(string.IsNullOrWhiteSpace(filter)
            ? ModText.Get("ui.machine.powered.filter.empty", "Filter: empty")
            : ModText.Get("ui.machine.powered.filter.value", "Filter: {{filter}} ({{mode}})", new
            {
                filter,
                mode = GetBoolModData(placedObject, null, SvsapTransferFilterBlacklistKey, false)
                    ? ModText.Get("ui.machine.powered.filter.blacklist", "blacklist")
                    : ModText.Get("ui.machine.powered.filter.whitelist", "whitelist")
            }));
        lines.Add(ModText.Get("ui.machine.powered.oreDictionary", "Ore dictionary: {{enabled}}", new { enabled = FormatBool(IsPoweredOreDictionaryModeEnabled(placedObject)) }));
        lines.Add(ModText.Get("ui.machine.powered.direction", "Direction: {{direction}}", new { direction = FormatPoweredFacingDirection(GetPoweredFacingDirection(placedObject)) }));
        lines.Add(ModText.Get("ui.machine.powered.quality", "Quality strategy: {{quality}}", new { quality = FormatPoweredQualityStrategy(GetPoweredQualityStrategy(placedObject, null)) }));
        if (IsPoweredImporterMachine(placedObject.QualifiedItemId))
            lines.Add(ModText.Get("ui.machine.powered.importerKeep", "Importer source keep: {{count}}", new { count = GetNonNegativeIntModData(placedObject, null, SvsapTransferMinSourceKeepKey, 0).ToString("N0") }));
        if (IsPoweredExporterMachine(placedObject.QualifiedItemId))
            lines.Add(ModText.Get("ui.machine.powered.exporterKeep", "Exporter target keep: {{count}}", new { count = GetNonNegativeIntModData(placedObject, null, SvsapTransferTargetKeepKey, 0).ToString("N0") }));
        return lines;
    }

    internal SvsapmeMachineActionApplyResult TryFuelCarbonGenerator(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        string fuelQualifiedItemId)
    {
        if (placedObject.QualifiedItemId != "(BC)" + ModItemCatalog.CarbonGenerator)
            return new(false, false, ModText.Get("hud.carbonGenerator.notGenerator", "Target machine is not a Carbon Generator."));

        if (!CarbonGeneratorFuelRules.TryGetFuelWh(fuelQualifiedItemId, this.getConfig().EnableExtendedGeneratorFuels, out var fuelWh))
            return new(false, false, ModText.Get("hud.carbonGenerator.invalidFuel", "Carbon Generator does not accept this fuel with the current config."));

        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return new(false, false, ModText.Get("hud.machineRegisterFailed", "SVSAPME could not register this machine."));
        }

        var capacity = Math.Max(0, state.CapacityWh);
        state.StoredWh = Math.Clamp(state.StoredWh, 0, capacity);
        if (capacity - state.StoredWh < fuelWh)
            return new(false, false, ModText.Get("hud.carbonGenerator.bufferFull", "Carbon Generator buffer is full."));

        state.StoredWh += fuelWh;
        placedObject.modData[MachineRegistryService.StoredWhKey] = state.StoredWh.ToString();
        this.repository.Save();
        return new(true, true, ModText.Get("hud.carbonGenerator.buffered", "Carbon Generator buffered {{stored}}/{{capacity}} kWh.", new { stored = (state.StoredWh / 1000m).ToString("0.00"), capacity = (capacity / 1000m).ToString("0.00") }));
    }

    internal SvsapmeMachineActionApplyResult TryStartElectricFurnaceManualUse(
        SObject machine,
        GameLocation location,
        Vector2 tile,
        string inputQualifiedItemId,
        int inputCount)
    {
        if (machine.QualifiedItemId != "(BC)" + ModItemCatalog.ElectricFurnace)
            return new(false, false, ModText.Get("hud.electricFurnace.notFurnace", "Target machine is not an Electric Furnace."));

        if (!IsIdleMachine(machine))
            return new(false, false, ModText.Get("hud.electricMachine.busy", "This electric machine is already running."));

        if (!ElectricMachineRules.TryGetFurnaceRecipe(inputQualifiedItemId, out var recipe)
            || inputCount < recipe.InputCount)
        {
            return new(false, false, ModText.Get("hud.electricFurnace.invalidInput", "Electric Furnace cannot process this input."));
        }

        if (!this.TryConsumeManualElectricMachineEnergy(location, tile, ElectricMachineRules.FurnaceWhPerRun))
            return new(false, false, ModText.Get("hud.electricMachine.noNetworkPower", "Network power is unavailable for this farmhand machine action."));

        StartElectricMachine(machine, ItemRegistry.Create(recipe.OutputQualifiedItemId, recipe.OutputCount), ElectricMachineRules.GetPoweredMinutes(recipe.PrototypeMinutes));
        return new(true, true, ModText.Get("hud.electricFurnace.networkStarted", "Electric Furnace started with network power."));
    }

    internal SvsapmeMachineActionApplyResult TryStartElectricGeodeCrusherManualUse(
        SObject machine,
        GameLocation location,
        Vector2 tile,
        string geodeQualifiedItemId)
    {
        if (machine.QualifiedItemId != "(BC)" + ModItemCatalog.ElectricGeodeCrusher)
            return new(false, false, ModText.Get("hud.electricGeodeCrusher.notCrusher", "Target machine is not an Electric Geode Crusher."));

        if (!IsIdleMachine(machine))
            return new(false, false, ModText.Get("hud.electricMachine.busy", "This electric machine is already running."));

        var geode = ItemRegistry.Create(geodeQualifiedItemId);
        if (!Utility.IsGeode(geode, disallow_special_geodes: true))
            return new(false, false, ModText.Get("hud.electricGeodeCrusher.invalidInput", "Electric Geode Crusher cannot process this input."));

        if (!this.TryConsumeManualElectricMachineEnergy(location, tile, ElectricMachineRules.GeodeCrusherWhPerRun))
            return new(false, false, ModText.Get("hud.electricMachine.noNetworkPower", "Network power is unavailable for this farmhand machine action."));

        var output = Utility.getTreasureFromGeode(geode);
        if (output is null)
            return new(false, false, ModText.Get("hud.electricGeodeCrusher.invalidInput", "Electric Geode Crusher cannot process this input."));

        StartElectricMachine(machine, output, ElectricMachineRules.GetPoweredMinutes(60));
        return new(true, true, ModText.Get("hud.electricGeodeCrusher.networkStarted", "Electric Geode Crusher started with network power."));
    }

    private void RunRouteTick()
    {
        var api = this.getSvsapApi();
        if (api is null)
            return;

        using var energyCellCache = this.energy.BeginLinkedEnergyCellCacheScope();
        var changed = false;
        foreach (var machine in this.registry.MachinesByGuid.Values.OrderBy(machine => machine.MachineGuid).ToList())
        {
            if (!this.repository.TryGet(machine.MachineGuid, out var state))
                continue;

            if (!TryGetActiveEndpoint(api, machine, out var location, out var endpoint))
                continue;

            var machineChanged = false;
            switch (machine.QualifiedItemId)
            {
                case "(BC)" + ModItemCatalog.CarbonGenerator:
                    machineChanged |= this.RouteCarbonGenerator(state, endpoint.NetworkId);
                    break;

                case "(BC)" + ModItemCatalog.BatterySynthesizer:
                    machineChanged |= this.RouteBatterySynthesizer(api, state, endpoint.NetworkId);
                    break;

                case "(BC)" + ModItemCatalog.BatteryDischarger:
                    machineChanged |= this.RouteBatteryDischarger(api, state, endpoint.NetworkId);
                    break;

                case "(BC)" + ModItemCatalog.PoweredImporterCopper:
                case "(BC)" + ModItemCatalog.PoweredImporterSteel:
                case "(BC)" + ModItemCatalog.PoweredImporterGold:
                case "(BC)" + ModItemCatalog.PoweredImporterIridium:
                    machineChanged |= RunPoweredOperations(
                        () => this.RoutePoweredImporter(api, state, endpoint.NetworkId, location, machine),
                        GetPoweredOperationCount(location, machine.Tile, state));
                    break;

                case "(BC)" + ModItemCatalog.PoweredExporterCopper:
                case "(BC)" + ModItemCatalog.PoweredExporterSteel:
                case "(BC)" + ModItemCatalog.PoweredExporterGold:
                case "(BC)" + ModItemCatalog.PoweredExporterIridium:
                    machineChanged |= RunPoweredOperations(
                        () => this.RoutePoweredExporter(api, state, endpoint.NetworkId, location, machine),
                        GetPoweredOperationCount(location, machine.Tile, state));
                    break;

                case "(BC)" + ModItemCatalog.PoweredMachineInterfaceCopper:
                case "(BC)" + ModItemCatalog.PoweredMachineInterfaceSteel:
                case "(BC)" + ModItemCatalog.PoweredMachineInterfaceGold:
                case "(BC)" + ModItemCatalog.PoweredMachineInterfaceIridium:
                    machineChanged |= RunPoweredOperations(
                        () => this.RoutePoweredMachineInterface(api, state, endpoint.NetworkId, location, machine),
                        GetPoweredOperationCount(location, machine.Tile, state));
                    break;

                case "(BC)" + ModItemCatalog.ElectricFurnace:
                    machineChanged |= this.RouteElectricFurnace(api, endpoint.NetworkId, location, machine);
                    break;

                case "(BC)" + ModItemCatalog.ElectricGeodeCrusher:
                    machineChanged |= this.RouteElectricGeodeCrusher(api, endpoint.NetworkId, location, machine);
                    break;
            }

            if (machineChanged)
            {
                changed = true;
                SyncPlacedObjectStateModData(location, machine, state);
            }
        }

        if (changed)
            this.repository.Save();
    }

    private static bool RunPoweredOperations(Func<bool> operation, int operationCount)
    {
        var changed = false;
        for (var i = 0; i < Math.Clamp(operationCount, 1, 2); i++)
        {
            var operationChanged = operation();
            changed |= operationChanged;
            if (!operationChanged)
                break;
        }

        return changed;
    }

    private static int GetPoweredOperationCount(GameLocation location, Vector2 tile, MachineState state)
    {
        if (!location.Objects.TryGetValue(tile, out var placedObject))
            return 1;

        return HasPoweredUpgrade(GetPoweredUpgradeSlots(placedObject, state), ModItemCatalog.SvsapSpeedCard) ? 2 : 1;
    }

    private void ShowEnergyMonitor(GameLocation location, Vector2 tile)
    {
        if (!Context.IsMainPlayer)
        {
            if (location.Objects.TryGetValue(tile, out var placedObject)
                && TryReadMachineGuid(placedObject, out var machineGuid)
                && this.TrySendSnapshotRequest(machineGuid))
            {
                return;
            }

            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.multiplayer.snapshotFailed", "SVSAPME machine status failed; please retry."), HUDMessage.error_type));
            return;
        }

        var api = this.getSvsapApi();
        if (api is null)
        {
            Game1.activeClickableMenu = new SvsapmeStatusMenu(
                ModText.Get("ui.energyMeter.title", "Energy Meter"),
                () => new[] { ModText.Get("ui.machine.network.apiUnavailable", "Network: SVSAP API unavailable") });
            return;
        }

        if (!api.TryGetLinkedEndpoint(location, tile, out var endpoint, out var endpointCode, out var endpointMessage)
            || endpoint is null)
        {
            Game1.activeClickableMenu = new SvsapmeStatusMenu(
                ModText.Get("ui.energyMeter.title", "Energy Meter"),
                () => new[] { ModText.Get("ui.machine.network.notLinked", "Network: not linked ({{code}}: {{message}})", new { code = endpointCode.ToString(), message = endpointMessage }) });
            return;
        }

        if (!endpoint.Active)
        {
            Game1.activeClickableMenu = new SvsapmeStatusMenu(
                ModText.Get("ui.energyMeter.title", "Energy Meter"),
                () => new[] { ModText.Get("ui.machine.network.inactive", "Network: {{network}} inactive", new { network = FormatShortGuid(endpoint.NetworkId) }) });
            return;
        }

        Game1.activeClickableMenu = new EnergyMonitorMenu(() => this.GetEnergyMonitorView(endpoint.NetworkId));
    }

    internal EnergyMonitorView GetEnergyMonitorView(Guid networkId)
    {
        var online = this.energy.TryGetNetworkEnergy(networkId, out var storedWh, out var capacityWh, out var code);
        var telemetry = this.telemetry.GetSnapshot(networkId);
        var status = online
            ? string.IsNullOrWhiteSpace(telemetry.LastWarning)
                ? ModText.Get("ui.machine.network.online", "Network online")
                : ModText.Get("ui.energyMeter.warning", "Warning: {{message}}", new { message = telemetry.LastWarning })
            : ModText.Get("hud.energyMonitor.failed", "Energy report failed: {{code}}.", new { code = FormatEnergyCode(code) });
        return new EnergyMonitorView(
            online,
            status,
            storedWh,
            capacityWh,
            telemetry.LastGeneratedWh,
            telemetry.LastConsumedWh,
            telemetry.TodayGeneratedWh,
            telemetry.TodayConsumedWh,
            telemetry.LastWarning,
            telemetry.TopProducers.Select(total => new EnergyMonitorDeviceView(total.DeviceId, FormatTelemetryDevice(total), total.Wh, BuildTelemetryDeviceDetails(total))).ToList(),
            telemetry.TopConsumers.Select(total => new EnergyMonitorDeviceView(total.DeviceId, FormatTelemetryDevice(total), total.Wh, BuildTelemetryDeviceDetails(total))).ToList());
    }

    private static IReadOnlyList<string> BuildTelemetryDeviceDetails(EnergyTelemetryReasonTotal total)
    {
        var lines = new List<string>
        {
            ModText.Get("ui.energyMeter.deviceTotal", "Today total: {{value}}", new { value = FormatWh(total.Wh) })
        };
        if (!TryGetTelemetryDeviceQualifiedItemId(total.DeviceId, out var qualifiedItemId))
            return lines;

        foreach (var port in MachinePortCatalog.GetPorts(qualifiedItemId))
        {
            lines.Add(ModText.Get(
                "ui.machine.port.line",
                "Port {{role}}: {{side}} - {{description}}",
                new
                {
                    role = ModText.Get(port.RoleKey, port.RoleKey),
                    side = ModText.Get(port.SideKey, port.SideKey),
                    description = ModText.Get(port.DescriptionKey, port.DescriptionKey)
                }));
        }

        return lines;
    }

    private static bool TryGetTelemetryDeviceQualifiedItemId(string deviceId, out string qualifiedItemId)
    {
        qualifiedItemId = string.Empty;
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        var separator = deviceId.IndexOf(':');
        var identity = separator >= 0 && separator + 1 < deviceId.Length ? deviceId[(separator + 1)..] : deviceId;
        var parts = identity.Split('|');
        if (parts.Length < 3 || !string.Equals(parts[0], "machine", StringComparison.Ordinal))
            return false;

        qualifiedItemId = parts[1];
        return !string.IsNullOrWhiteSpace(qualifiedItemId);
    }

    private IReadOnlyList<string> BuildEnergyMonitorLines(Guid networkId)
    {
        var lines = new List<string>
        {
            ModText.Get("ui.energyMeter.network", "Network: {{network}}", new { network = FormatShortGuid(networkId) })
        };

        if (this.energy.TryGetNetworkEnergy(networkId, out var storedWh, out var capacityWh, out var code))
        {
            var percent = capacityWh > 0 ? storedWh / (decimal)capacityWh : 0m;
            lines.Add(ModText.Get(
                "ui.energyMeter.stored",
                "Stored: {{stored}} / {{capacity}} ({{percent}})",
                new { stored = FormatWh(storedWh), capacity = FormatWh(capacityWh), percent = percent.ToString("P0") }));
        }
        else
        {
            lines.Add(ModText.Get("hud.energyMonitor.failed", "Energy report failed: {{code}}.", new { code = FormatEnergyCode(code) }));
        }

        var snapshot = this.telemetry.GetSnapshot(networkId);
        lines.Add(ModText.Get(
            "ui.energyMeter.lastTick",
            "Last flow: +{{generated}} / -{{consumed}} / net {{net}}",
            new { generated = FormatWh(snapshot.LastGeneratedWh), consumed = FormatWh(snapshot.LastConsumedWh), net = FormatSignedWh(snapshot.LastNetWh) }));
        lines.Add(ModText.Get(
            "ui.energyMeter.today",
            "Today: +{{generated}} / -{{consumed}} / net {{net}}",
            new { generated = FormatWh(snapshot.TodayGeneratedWh), consumed = FormatWh(snapshot.TodayConsumedWh), net = FormatSignedWh(snapshot.TodayNetWh) }));
        lines.Add(ModText.Get("ui.energyMeter.topProducers", "Top producers: {{items}}", new { items = FormatTelemetryTotals(snapshot.TopProducers) }));
        lines.Add(ModText.Get("ui.energyMeter.topConsumers", "Top consumers: {{items}}", new { items = FormatTelemetryTotals(snapshot.TopConsumers) }));
        if (!string.IsNullOrWhiteSpace(snapshot.LastWarning))
            lines.Add(ModText.Get("ui.energyMeter.warning", "Warning: {{message}}", new { message = snapshot.LastWarning }));

        return lines;
    }

    private bool TrySendSnapshotRequest(Guid machineGuid)
    {
        if (this.sendSnapshotRequest is null)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.multiplayerActionSenderNotReady", "SVSAPME multiplayer action sender is not ready."), HUDMessage.error_type));
            return true;
        }

        this.sendSnapshotRequest(machineGuid);
        return true;
    }

    private bool RouteCarbonGenerator(MachineState state, Guid networkId)
    {
        if (!this.getConfig().EnableCarbonGenerator || state.StoredWh <= 0)
            return false;

        if (this.energy.TryDepositWh(
                networkId,
                state.StoredWh,
                ModItemCatalog.UniqueId,
                BuildMachineEnergyReason(state, "carbon-generator-route"),
                out var acceptedWh,
                out _,
                out _)
            && acceptedWh > 0)
        {
            state.StoredWh = Math.Max(0, state.StoredWh - acceptedWh);
            return true;
        }

        return false;
    }

    private bool RouteBatteryDischarger(ISvsapApi api, MachineState state, Guid networkId)
    {
        var outputWh = this.GetBatteryDischargerOutputWh();
        if (!this.energy.TryGetNetworkEnergy(networkId, out var storedWh, out var capacityWh, out _))
            return false;

        var available = api.GetAvailableCount(networkId, BatteryDischargerRules.BatteryPackQualifiedItemId, quality: null);
        if (!BatteryDischargerRules.CanDischarge(this.GetBatteryDischargerEnabled(state), available, storedWh, capacityWh, outputWh))
            return false;

        if (!api.TryExtractItem(networkId, BatteryDischargerRules.BatteryPackQualifiedItemId, quality: null, count: 1, out var extracted, out _, out _)
            || extracted is null
            || extracted.Stack <= 0)
        {
            return false;
        }

        if (this.energy.TryDepositWh(
                networkId,
                outputWh,
                ModItemCatalog.UniqueId,
                BuildMachineEnergyReason(state, "battery-discharger"),
                out var acceptedWh,
                out _,
                out _)
            && acceptedWh == outputWh)
        {
            return true;
        }

        if (!api.TryInsertItem(networkId, extracted, out _, out var code, out var message))
            this.monitor.Log($"Failed to roll back Battery Discharger input: {code} {message}", LogLevel.Error);

        return false;
    }

    private bool RouteBatterySynthesizer(ISvsapApi api, MachineState state, Guid networkId)
    {
        if (!this.getConfig().EnableBatterySynthesizer)
            return false;

        var changed = this.FlushOutputBuffer(api, state, networkId);
        if (state.OutputBuffer.Count > 0)
            return changed;

        if (state.ProgressWh < BatterySynthesizerRules.RequiredWh)
        {
            var needed = BatterySynthesizerRules.RequiredWh - state.ProgressWh;
            if (this.energy.TryConsumeWh(
                    networkId,
                    needed,
                    ModItemCatalog.UniqueId,
                    BuildMachineEnergyReason(state, "battery-synthesizer-charge"),
                    allowPartial: true,
                    out var consumedWh,
                    out _,
                    out _))
            {
                state.ProgressWh = Math.Min(BatterySynthesizerRules.RequiredWh, state.ProgressWh + consumedWh);
                changed = consumedWh > 0 || changed;
            }
        }

        if (!BatterySynthesizerRules.CanAssemble(
                state.ProgressWh,
                state.OutputBuffer.Count,
                qualifiedItemId => api.GetAvailableCount(networkId, qualifiedItemId, quality: null)))
        {
            return changed;
        }

        if (!this.TryConsumeSynthMaterials(api, networkId, out var rollbackItems))
            return changed;

        var battery = ItemRegistry.Create(BatterySynthesizerRules.BatteryPackQualifiedItemId, 1);
        state.OutputBuffer.Add(BufferedItemCodec.FromItem(battery));
        state.ProgressWh = 0;
        changed = true;
        changed |= this.FlushOutputBuffer(api, state, networkId);

        if (state.OutputBuffer.Count > 0)
            this.monitor.Log("Battery Synthesizer produced a Battery Pack but network insert failed; output remains buffered.", LogLevel.Trace);

        rollbackItems.Clear();
        return changed;
    }

    private bool RouteElectricFurnace(ISvsapApi api, Guid networkId, GameLocation location, MachineLocation machineLocation)
    {
        if (!this.getConfig().EnableElectricMachines)
            return false;

        if (!location.Objects.TryGetValue(machineLocation.Tile, out var machine))
            return false;

        if (this.TryCollectMachineOutput(api, networkId, machine, powered: false))
            return true;

        if (!IsIdleMachine(machine))
            return false;

        foreach (var recipe in ElectricMachineRules.FurnaceRecipes)
        {
            if (api.GetAvailableCount(networkId, recipe.InputQualifiedItemId, quality: null) < recipe.InputCount)
                continue;

            if (!this.energy.TryConsumeWh(
                    networkId,
                    ElectricMachineRules.FurnaceWhPerRun,
                    ModItemCatalog.UniqueId,
                    BuildMachineEnergyReason(machine, "electric-furnace"),
                    allowPartial: false,
                    out _,
                    out _,
                    out _))
            {
                return false;
            }

            if (!api.TryExtractItem(networkId, recipe.InputQualifiedItemId, quality: null, recipe.InputCount, out var extracted, out _, out _)
                || extracted is null
                || extracted.Stack < recipe.InputCount)
            {
                this.TryDepositEnergyRefund(networkId, ElectricMachineRules.FurnaceWhPerRun, BuildMachineEnergyReason(machine, "electric-furnace-refund"));
                if (extracted is not null && extracted.Stack > 0)
                    api.TryInsertItem(networkId, extracted, out _, out _, out _);
                return false;
            }

            var output = ItemRegistry.Create(recipe.OutputQualifiedItemId, recipe.OutputCount);
            StartElectricMachine(machine, output, ElectricMachineRules.GetPoweredMinutes(recipe.PrototypeMinutes));
            return true;
        }

        return false;
    }

    private bool RouteElectricGeodeCrusher(ISvsapApi api, Guid networkId, GameLocation location, MachineLocation machineLocation)
    {
        if (!this.getConfig().EnableElectricMachines)
            return false;

        if (!location.Objects.TryGetValue(machineLocation.Tile, out var machine))
            return false;

        if (this.TryCollectMachineOutput(api, networkId, machine, powered: false))
            return true;

        if (!IsIdleMachine(machine))
            return false;

        foreach (var geodeQualifiedItemId in ElectricMachineRules.KnownGeodeQualifiedItemIds)
        {
            if (api.GetAvailableCount(networkId, geodeQualifiedItemId, quality: null) <= 0)
                continue;

            Item geodeProbe;
            try
            {
                geodeProbe = ItemRegistry.Create(geodeQualifiedItemId);
            }
            catch
            {
                continue;
            }

            if (!Utility.IsGeode(geodeProbe, disallow_special_geodes: true))
                continue;

            if (!this.energy.TryConsumeWh(
                    networkId,
                    ElectricMachineRules.GeodeCrusherWhPerRun,
                    ModItemCatalog.UniqueId,
                    BuildMachineEnergyReason(machine, "electric-geode-crusher"),
                    allowPartial: false,
                    out _,
                    out _,
                    out _))
            {
                return false;
            }

            if (!api.TryExtractItem(networkId, geodeQualifiedItemId, quality: null, count: 1, out var extracted, out _, out _)
                || extracted is null
                || extracted.Stack <= 0)
            {
                this.TryDepositEnergyRefund(networkId, ElectricMachineRules.GeodeCrusherWhPerRun, BuildMachineEnergyReason(machine, "electric-geode-crusher-refund"));
                return false;
            }

            var output = Utility.getTreasureFromGeode(extracted);
            if (output is null)
            {
                this.TryDepositEnergyRefund(networkId, ElectricMachineRules.GeodeCrusherWhPerRun, BuildMachineEnergyReason(machine, "electric-geode-crusher-refund"));
                api.TryInsertItem(networkId, extracted, out _, out _, out _);
                return false;
            }

            StartElectricMachine(machine, output, ElectricMachineRules.GetPoweredMinutes(60));
            return true;
        }

        return false;
    }

    private bool TryHandleElectricMachineManualUse(SObject machine, GameLocation location, Vector2 tile)
    {
        var held = Game1.player.CurrentItem;
        if (held is null || held.Stack <= 0 || !IsIdleMachine(machine))
            return false;

        if (machine.QualifiedItemId == "(BC)" + ModItemCatalog.ElectricFurnace)
            return this.TryHandleElectricFurnaceManualUse(machine, location, tile, held);

        if (machine.QualifiedItemId == "(BC)" + ModItemCatalog.ElectricGeodeCrusher)
            return this.TryHandleElectricGeodeCrusherManualUse(machine, location, tile, held);

        return false;
    }

    private bool TryHandleElectricFurnaceManualUse(SObject machine, GameLocation location, Vector2 tile, Item held)
    {
        if (!ElectricMachineRules.TryGetFurnaceRecipe(held.QualifiedItemId, out var recipe))
            return false;

        var canUsePower = this.TryConsumeManualElectricMachineEnergy(location, tile, ElectricMachineRules.FurnaceWhPerRun);
        if (canUsePower)
        {
            if (!TryConsumePlayerItems(recipe.InputQualifiedItemId, recipe.InputCount))
            {
                this.TryRefundManualElectricMachineEnergy(location, tile, ElectricMachineRules.FurnaceWhPerRun);
                return false;
            }

            StartElectricMachine(machine, ItemRegistry.Create(recipe.OutputQualifiedItemId, recipe.OutputCount), ElectricMachineRules.GetPoweredMinutes(recipe.PrototypeMinutes));
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.electricFurnace.networkStarted", "Electric Furnace started with network power."), HUDMessage.newQuest_type));
            return true;
        }

        if (CountPlayerItems(recipe.InputQualifiedItemId) < recipe.InputCount || CountPlayerItems(ElectricMachineRules.CoalQualifiedItemId) < 1)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.electricFurnace.needsCoal", "Electric Furnace needs ore/quartz plus Coal without network power."), HUDMessage.error_type));
            return true;
        }

        TryConsumePlayerItems(recipe.InputQualifiedItemId, recipe.InputCount);
        TryConsumePlayerItems(ElectricMachineRules.CoalQualifiedItemId, 1);
        StartElectricMachine(machine, ItemRegistry.Create(recipe.OutputQualifiedItemId, recipe.OutputCount), recipe.PrototypeMinutes);
        Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.electricFurnace.prototypeStarted", "Electric Furnace started on prototype coal path."), HUDMessage.newQuest_type));
        return true;
    }

    private bool TryHandleElectricGeodeCrusherManualUse(SObject machine, GameLocation location, Vector2 tile, Item held)
    {
        if (!Utility.IsGeode(held, disallow_special_geodes: true))
            return false;

        var canUsePower = this.TryConsumeManualElectricMachineEnergy(location, tile, ElectricMachineRules.GeodeCrusherWhPerRun);
        if (!canUsePower && CountPlayerItems(ElectricMachineRules.CoalQualifiedItemId) < 1)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.electricGeodeCrusher.needsCoal", "Electric Geode Crusher needs Coal without network power."), HUDMessage.error_type));
            return true;
        }

        var geode = held.getOne();
        var output = Utility.getTreasureFromGeode(geode);
        if (output is null)
        {
            if (canUsePower)
                this.TryRefundManualElectricMachineEnergy(location, tile, ElectricMachineRules.GeodeCrusherWhPerRun);
            return false;
        }

        if (!TryConsumePlayerItems(held.QualifiedItemId, 1))
        {
            if (canUsePower)
                this.TryRefundManualElectricMachineEnergy(location, tile, ElectricMachineRules.GeodeCrusherWhPerRun);
            return false;
        }

        if (!canUsePower)
            TryConsumePlayerItems(ElectricMachineRules.CoalQualifiedItemId, 1);

        StartElectricMachine(machine, output, canUsePower ? ElectricMachineRules.GetPoweredMinutes(60) : 60);
        Game1.addHUDMessage(new HUDMessage(
            canUsePower
                ? ModText.Get("hud.electricGeodeCrusher.networkStarted", "Electric Geode Crusher started with network power.")
                : ModText.Get("hud.electricGeodeCrusher.prototypeStarted", "Electric Geode Crusher started on prototype coal path."),
            HUDMessage.newQuest_type));
        return true;
    }

    private bool RoutePoweredImporter(ISvsapApi api, MachineState state, Guid networkId, GameLocation location, MachineLocation machine)
    {
        if (!this.getConfig().EnablePoweredTransfer)
            return false;

        if (!location.Objects.TryGetValue(machine.Tile, out var placedObject))
            return false;

        var tier = GetPoweredTier(machine.QualifiedItemId);
        var settings = GetPoweredTransferSettings(placedObject, state);
        foreach (var (_, obj) in GetAdjacentObjects(location, machine.Tile, settings))
        {
            if (obj is Chest chest)
            {
                if (ChestAccessHelper.TryRunWithLock(
                    chest,
                    () =>
                    {
                        if (!this.IsCurrentMachineInstance(location, machine, placedObject, state))
                            return;

                        if (this.TryRoutePoweredImportFromChest(api, state, networkId, chest, tier, settings))
                        {
                            SyncPlacedObjectStateModData(location, machine, state);
                            this.repository.Save();
                        }
                    }))
                {
                    return false;
                }

                continue;
            }

            if (this.TryRoutePoweredImportMachineOutput(api, state, networkId, obj, tier, settings))
                return true;
        }

        return false;
    }

    private bool TryRoutePoweredImportFromChest(
        ISvsapApi api,
        MachineState state,
        Guid networkId,
        Chest chest,
        PoweredMachineTier tier,
        PoweredTransferSettings settings)
    {
        foreach (var seedSlot in GetPoweredChestSlotOrder(chest, settings.QualityStrategy))
        {
            var source = chest.Items[seedSlot];
            if (!CanImportPoweredSource(source, settings))
                continue;

            var sourceSlots = GetMatchingImportSlots(chest, source!, settings).ToList();
            var sourceTotal = sourceSlots.Sum(slot => Math.Max(0, chest.Items[slot]?.Stack ?? 0));
            var sourceAvailable = Math.Max(0, sourceTotal - settings.MinSourceKeep);
            if (sourceAvailable <= 0)
                continue;

            var maxCandidate = Math.Min(sourceAvailable, GetPoweredOperationLimit(tier, settings));
            var probe = source!.getOne();
            probe.Stack = maxCandidate;
            var targetCapacity = api.GetInsertCapacity(networkId, probe, maxCandidate);
            var plan = this.PlanPoweredTransfer(networkId, sourceAvailable, targetCapacity, tier, state, settings.CapacityMultiplier);
            if (plan.Mode == PoweredTransferRunMode.None)
                continue;

            if (!this.TryPayPoweredTransfer(networkId, state, plan))
                continue;

            var moved = this.InsertFromChestSlotsIntoNetwork(api, networkId, chest, sourceSlots, probe, plan.PlannedItems);

            var changed = moved > 0;
            changed |= this.SettlePoweredTransfer(networkId, state, plan, moved);
            return changed;
        }

        return false;
    }

    private bool TryRoutePoweredImportMachineOutput(
        ISvsapApi api,
        MachineState state,
        Guid networkId,
        SObject sourceMachine,
        PoweredMachineTier tier,
        PoweredTransferSettings settings)
    {
        var held = sourceMachine.heldObject.Value;
        if (held is null
            || held.Stack <= 0
            || !sourceMachine.readyForHarvest.Value
            || !MatchesPoweredFilter(held, settings)
            || !AllowsPoweredQuality(held, settings.QualityStrategy))
        {
            return false;
        }

        var sourceAvailable = held.Stack;
        var maxCandidate = Math.Min(sourceAvailable, GetPoweredOperationLimit(tier, settings));
        var probe = held.getOne();
        probe.Stack = maxCandidate;
        var targetCapacity = api.GetInsertCapacity(networkId, probe, maxCandidate);
        var plan = this.PlanPoweredTransfer(networkId, sourceAvailable, targetCapacity, tier, state, settings.CapacityMultiplier);
        if (plan.Mode == PoweredTransferRunMode.None)
            return false;

        if (!this.TryPayPoweredTransfer(networkId, state, plan))
            return false;

        var moved = this.InsertItemIntoNetworkInChunks(api, networkId, held, plan.PlannedItems);

        if (moved > 0)
        {
            held.Stack -= moved;
            if (held.Stack <= 0)
                ResetAfterAutomatedCollect(sourceMachine);
        }

        var changed = moved > 0;
        changed |= this.SettlePoweredTransfer(networkId, state, plan, moved);
        return changed;
    }

    private bool RoutePoweredExporter(ISvsapApi api, MachineState state, Guid networkId, GameLocation location, MachineLocation machine)
    {
        if (!this.getConfig().EnablePoweredTransfer)
            return false;

        if (!location.Objects.TryGetValue(machine.Tile, out var placedObject))
            return false;

        var settings = GetPoweredTransferSettings(placedObject, state);
        if (settings.FilterQualifiedItemIds.Count == 0)
            return false;

        var tier = GetPoweredTier(machine.QualifiedItemId);
        foreach (var (tile, obj) in GetAdjacentObjects(location, machine.Tile, settings))
        {
            if (obj is not Chest chest)
            {
                if (this.TryRoutePoweredExportToMachine(api, state, networkId, location, tile, obj, tier, settings))
                    return true;

                continue;
            }

            if (!ChestAccessHelper.TryRunWithLock(
                chest,
                () =>
                {
                    if (!this.IsCurrentMachineInstance(location, machine, placedObject, state))
                        return;

                    var changed = settings.FilterBlacklist
                        ? this.TryRoutePoweredExportBlacklist(api, state, networkId, location, tile, chest, tier, settings)
                        : this.TryRoutePoweredExportWhitelist(api, state, networkId, location, tile, chest, tier, settings);
                    if (changed)
                    {
                        SyncPlacedObjectStateModData(location, machine, state);
                        this.repository.Save();
                    }
                }))
            {
                continue;
            }

            return false;
        }

        return false;
    }

    private bool IsCurrentMachineInstance(
        GameLocation location,
        MachineLocation machine,
        SObject expectedObject,
        MachineState expectedState)
    {
        return location.Objects.TryGetValue(machine.Tile, out var currentObject)
            && ReferenceEquals(currentObject, expectedObject)
            && this.repository.TryGet(machine.MachineGuid, out var currentState)
            && ReferenceEquals(currentState, expectedState);
    }

    private bool TryRoutePoweredExportToMachine(
        ISvsapApi api,
        MachineState state,
        Guid networkId,
        GameLocation location,
        Vector2 targetTile,
        SObject targetMachine,
        PoweredMachineTier tier,
        PoweredTransferSettings settings)
    {
        if (targetMachine.heldObject.Value is not null || ModItemCatalog.IsSvsapmeBigCraftable(targetMachine.QualifiedItemId))
            return false;

        var operationLimit = GetPoweredOperationLimit(tier, settings);
        bool CandidateCanMove(Item item)
        {
            return MatchesPoweredFilter(item, settings)
                && TryProbeMachineInput(targetMachine, item.QualifiedItemId, operationLimit);
        }

        if (!api.TryPeekFirstMatchingItem(
                networkId,
                CandidateCanMove,
                IsHighQualityFirst(settings.QualityStrategy),
                IsPreserveGoldIridium(settings.QualityStrategy),
                out var prototype,
                out var sourceAvailable,
                out _,
                out _)
            || prototype is null
            || sourceAvailable <= 0)
        {
            return false;
        }

        var maxCandidate = Math.Min(sourceAvailable, operationLimit);
        var plan = this.PlanPoweredTransfer(networkId, maxCandidate, maxCandidate, tier, state, settings.CapacityMultiplier);
        if (plan.Mode == PoweredTransferRunMode.None)
            return false;

        if (!this.TryPayPoweredTransfer(networkId, state, plan))
            return false;

        var accepted = this.ExtractFirstMatchingItemsToMachine(
            api,
            networkId,
            location,
            targetTile,
            targetMachine,
            CandidateCanMove,
            item => Math.Min(plan.PlannedItems, operationLimit),
            IsHighQualityFirst(settings.QualityStrategy),
            IsPreserveGoldIridium(settings.QualityStrategy),
            plan.PlannedItems);

        var changed = accepted > 0;
        changed |= this.SettlePoweredTransfer(networkId, state, plan, accepted);
        return changed;
    }

    private bool TryRoutePoweredExportWhitelist(
        ISvsapApi api,
        MachineState state,
        Guid networkId,
        GameLocation location,
        Vector2 targetTile,
        Chest chest,
        PoweredMachineTier tier,
        PoweredTransferSettings settings)
    {
        var operationLimit = GetPoweredOperationLimit(tier, settings);
        bool CandidateCanMove(Item item)
        {
            if (!MatchesPoweredFilter(item, settings))
                return false;

            if (settings.TargetKeep > 0 && CountMatching(chest, item, settings) >= settings.TargetKeep)
                return false;

            var probe = item.getOne();
            probe.Stack = operationLimit;
            return GetChestAcceptCount(chest, probe, operationLimit) > 0;
        }

        if (!api.TryPeekFirstMatchingItem(
                networkId,
                CandidateCanMove,
                IsHighQualityFirst(settings.QualityStrategy),
                IsPreserveGoldIridium(settings.QualityStrategy),
                out var prototype,
                out var sourceAvailable,
                out _,
                out _)
            || prototype is null
            || sourceAvailable <= 0)
        {
            return false;
        }

        var targetRemaining = settings.TargetKeep > 0
            ? Math.Max(0, settings.TargetKeep - CountMatching(chest, prototype, settings))
            : int.MaxValue;
        var maxCandidate = Math.Min(Math.Min(sourceAvailable, targetRemaining), operationLimit);
        if (maxCandidate <= 0)
            return false;

        var probe = prototype.getOne();
        probe.Stack = maxCandidate;
        var targetCapacity = GetChestAcceptCount(chest, probe, maxCandidate);
        var plan = this.PlanPoweredTransfer(networkId, maxCandidate, targetCapacity, tier, state, settings.CapacityMultiplier);
        if (plan.Mode == PoweredTransferRunMode.None)
            return false;

        if (!this.TryPayPoweredTransfer(networkId, state, plan))
            return false;

        var accepted = this.ExtractFirstMatchingItemsToChest(
            api,
            networkId,
            location,
            targetTile,
            chest,
            CandidateCanMove,
            item =>
            {
                var remaining = settings.TargetKeep > 0
                    ? Math.Max(0, settings.TargetKeep - CountMatching(chest, item, settings))
                    : plan.PlannedItems;
                return Math.Min(plan.PlannedItems, remaining);
            },
            IsHighQualityFirst(settings.QualityStrategy),
            IsPreserveGoldIridium(settings.QualityStrategy),
            plan.PlannedItems);

        var changed = accepted > 0;
        changed |= this.SettlePoweredTransfer(networkId, state, plan, accepted);
        return changed;
    }

    private bool TryRoutePoweredExportBlacklist(
        ISvsapApi api,
        MachineState state,
        Guid networkId,
        GameLocation location,
        Vector2 targetTile,
        Chest chest,
        PoweredMachineTier tier,
        PoweredTransferSettings settings)
    {
        var operationLimit = GetPoweredOperationLimit(tier, settings);
        bool CandidateCanMove(Item item)
        {
            if (!MatchesPoweredFilter(item, settings))
                return false;

            if (settings.TargetKeep > 0 && CountMatching(chest, item, settings) >= settings.TargetKeep)
                return false;

            var targetRemaining = settings.TargetKeep > 0
                ? Math.Max(0, settings.TargetKeep - CountMatching(chest, item, settings))
                : operationLimit;
            var probe = item.getOne();
            probe.Stack = Math.Min(targetRemaining, operationLimit);
            return GetChestAcceptCount(chest, probe, probe.Stack) > 0;
        }

        if (!api.TryPeekFirstMatchingItem(
                networkId,
                CandidateCanMove,
                IsHighQualityFirst(settings.QualityStrategy),
                IsPreserveGoldIridium(settings.QualityStrategy),
                out var prototype,
                out var sourceAvailable,
                out _,
                out _)
            || prototype is null
            || sourceAvailable <= 0)
        {
            return false;
        }

        var targetRemainingForPrototype = settings.TargetKeep > 0
            ? Math.Max(0, settings.TargetKeep - CountMatching(chest, prototype, settings))
            : int.MaxValue;
        var maxCandidate = Math.Min(Math.Min(sourceAvailable, targetRemainingForPrototype), operationLimit);
        if (maxCandidate <= 0)
            return false;

        var probe = prototype.getOne();
        probe.Stack = maxCandidate;
        var targetCapacity = GetChestAcceptCount(chest, probe, maxCandidate);
        var plan = this.PlanPoweredTransfer(networkId, maxCandidate, targetCapacity, tier, state, settings.CapacityMultiplier);
        if (plan.Mode == PoweredTransferRunMode.None)
            return false;

        if (!this.TryPayPoweredTransfer(networkId, state, plan))
            return false;

        var accepted = this.ExtractFirstMatchingItemsToChest(
            api,
            networkId,
            location,
            targetTile,
            chest,
            CandidateCanMove,
            item =>
            {
                var remaining = settings.TargetKeep > 0
                    ? Math.Max(0, settings.TargetKeep - CountMatching(chest, item, settings))
                    : plan.PlannedItems;
                return Math.Min(plan.PlannedItems, remaining);
            },
            IsHighQualityFirst(settings.QualityStrategy),
            IsPreserveGoldIridium(settings.QualityStrategy),
            plan.PlannedItems);

        var changed = accepted > 0;
        changed |= this.SettlePoweredTransfer(networkId, state, plan, accepted);
        return changed;
    }

    private int InsertExportedItemOrReturnLeftover(
        ISvsapApi api,
        Guid networkId,
        GameLocation location,
        Vector2 targetTile,
        Chest chest,
        Item extracted)
    {
        var requested = Math.Max(0, extracted.Stack);
        var accepted = InsertIntoChestSlots(chest, extracted, requested);
        if (accepted < requested)
            this.ReturnOrDropLeftoverCount(api, networkId, extracted, requested - accepted, location, targetTile);

        return accepted;
    }

    private static int InsertIntoChestSlots(Chest chest, Item prototype, int count)
    {
        var remaining = Math.Max(0, count);
        if (remaining <= 0)
            return 0;

        foreach (var stack in chest.Items)
        {
            if (remaining <= 0)
                break;

            if (stack is null || !stack.canStackWith(prototype))
                continue;

            var free = Math.Max(0, stack.maximumStackSize() - stack.Stack);
            if (free <= 0)
                continue;

            var moved = Math.Min(remaining, free);
            stack.Stack += moved;
            remaining -= moved;
        }

        for (var i = 0; i < chest.Items.Count && remaining > 0; i++)
        {
            if (chest.Items[i] is not null)
                continue;

            var moved = Math.Min(remaining, Math.Max(1, prototype.maximumStackSize()));
            var stack = prototype.getOne();
            stack.Stack = moved;
            chest.Items[i] = stack;
            remaining -= moved;
        }

        var capacity = GetChestSlotCapacity(chest);
        while (remaining > 0 && chest.Items.Count < capacity)
        {
            var moved = Math.Min(remaining, Math.Max(1, prototype.maximumStackSize()));
            var stack = prototype.getOne();
            stack.Stack = moved;
            chest.Items.Add(stack);
            remaining -= moved;
        }

        return count - remaining;
    }

    private int InsertFromChestSlotsIntoNetwork(
        ISvsapApi api,
        Guid networkId,
        Chest chest,
        IReadOnlyList<int> sourceSlots,
        Item prototype,
        int plannedItems)
    {
        var remaining = Math.Max(0, plannedItems);
        var movedTotal = 0;
        var chunkLimit = GetMoveChunkLimit(prototype);
        foreach (var slot in sourceSlots)
        {
            if (remaining <= 0)
                break;

            while (remaining > 0)
            {
                if (slot < 0 || slot >= chest.Items.Count)
                    break;

                var source = chest.Items[slot];
                if (source is null || source.Stack <= 0 || !source.canStackWith(prototype))
                    break;

                var chunkCount = Math.Min(Math.Min(source.Stack, remaining), chunkLimit);
                var moving = source.getOne();
                moving.Stack = chunkCount;
                var moved = 0;
                if (api.TryInsertItem(networkId, moving, out var remainder, out _, out _))
                    moved = chunkCount - Math.Max(0, remainder?.Stack ?? 0);

                if (moved <= 0)
                    return movedTotal;

                source.Stack -= moved;
                if (source.Stack <= 0)
                    chest.Items[slot] = null;

                remaining -= moved;
                movedTotal += moved;
                if (moved < chunkCount)
                    return movedTotal;
            }
        }

        return movedTotal;
    }

    private int InsertItemIntoNetworkInChunks(ISvsapApi api, Guid networkId, Item prototype, int plannedItems)
    {
        var remaining = Math.Min(Math.Max(0, plannedItems), Math.Max(0, prototype.Stack));
        var movedTotal = 0;
        var chunkLimit = GetMoveChunkLimit(prototype);
        while (remaining > 0)
        {
            var chunkCount = Math.Min(remaining, chunkLimit);
            var moving = prototype.getOne();
            moving.Stack = chunkCount;
            var moved = 0;
            if (api.TryInsertItem(networkId, moving, out var remainder, out _, out _))
                moved = chunkCount - Math.Max(0, remainder?.Stack ?? 0);

            if (moved <= 0)
                break;

            remaining -= moved;
            movedTotal += moved;
            if (moved < chunkCount)
                break;
        }

        return movedTotal;
    }

    private int ExtractFilteredItemsToChest(
        ISvsapApi api,
        Guid networkId,
        GameLocation location,
        Vector2 targetTile,
        Chest chest,
        string qualifiedItemId,
        int? quality,
        Item prototype,
        int plannedItems)
    {
        var remaining = Math.Max(0, plannedItems);
        var accepted = 0;
        var chunkLimit = GetMoveChunkLimit(prototype);
        while (remaining > 0)
        {
            var requestCount = Math.Min(remaining, chunkLimit);
            if (!api.TryExtractItem(networkId, qualifiedItemId, quality, requestCount, out var extracted, out _, out _)
                || extracted is null
                || extracted.Stack <= 0)
            {
                break;
            }

            var extractedCount = extracted.Stack;
            var moved = this.InsertExportedItemOrReturnLeftover(api, networkId, location, targetTile, chest, extracted);
            accepted += moved;
            remaining -= moved;
            if (moved <= 0 || moved < extractedCount)
                break;
        }

        return accepted;
    }

    private int ExtractFirstMatchingItemsToChest(
        ISvsapApi api,
        Guid networkId,
        GameLocation location,
        Vector2 targetTile,
        Chest chest,
        Func<Item, bool> predicate,
        Func<Item, int> requestedCountSelector,
        bool highQualityFirst,
        bool preserveGoldIridium,
        int plannedItems)
    {
        var remaining = Math.Max(0, plannedItems);
        var accepted = 0;
        while (remaining > 0)
        {
            if (!api.TryExtractFirstMatchingItem(
                    networkId,
                    predicate,
                    item => Math.Min(remaining, Math.Max(0, requestedCountSelector(item))),
                    highQualityFirst,
                    preserveGoldIridium,
                    out var extracted,
                    out _,
                    out _)
                || extracted is null
                || extracted.Stack <= 0)
            {
                break;
            }

            var extractedCount = extracted.Stack;
            var moved = this.InsertExportedItemOrReturnLeftover(api, networkId, location, targetTile, chest, extracted);
            accepted += moved;
            remaining -= moved;
            if (moved <= 0 || moved < extractedCount)
                break;
        }

        return accepted;
    }

    private int ExtractFirstMatchingItemsToMachine(
        ISvsapApi api,
        Guid networkId,
        GameLocation location,
        Vector2 targetTile,
        SObject targetMachine,
        Func<Item, bool> predicate,
        Func<Item, int> requestedCountSelector,
        bool highQualityFirst,
        bool preserveGoldIridium,
        int plannedItems)
    {
        if (!api.TryExtractFirstMatchingItem(
                networkId,
                predicate,
                item => Math.Min(Math.Min(Math.Max(1, plannedItems), GetMoveChunkLimit(item)), Math.Max(0, requestedCountSelector(item))),
                highQualityFirst,
                preserveGoldIridium,
                out var extracted,
                out _,
                out _)
            || extracted is null
            || extracted.Stack <= 0)
        {
            return 0;
        }

        var scratchInventory = new Inventory();
        scratchInventory.Add(extracted);
        try
        {
            var beforeAutoLoad = scratchInventory.Sum(item => item?.Stack ?? 0);
            targetMachine.AttemptAutoLoad(scratchInventory, Game1.player);
            var afterAutoLoad = scratchInventory.Sum(item => item?.Stack ?? 0);
            return Math.Max(0, beforeAutoLoad - afterAutoLoad);
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Powered Exporter failed to auto-load {extracted.QualifiedItemId} into {targetMachine.QualifiedItemId}: {ex.Message}", LogLevel.Trace);
            return 0;
        }
        finally
        {
            this.ReturnScratchInventory(api, networkId, scratchInventory, location, targetTile);
        }
    }

    private bool TrySelectPoweredExportQuality(
        ISvsapApi api,
        Guid networkId,
        PoweredTransferSettings settings,
        out int? quality,
        out int sourceAvailable)
    {
        quality = null;
        sourceAvailable = 0;
        var filterQualifiedItemId = settings.FilterQualifiedItemIds.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(filterQualifiedItemId))
            return false;

        foreach (var candidateQuality in GetPoweredQualityOrder(settings.QualityStrategy))
        {
            var available = api.GetAvailableCount(networkId, filterQualifiedItemId, candidateQuality);
            if (available <= 0)
                continue;

            quality = candidateQuality;
            sourceAvailable = available;
            return true;
        }

        return false;
    }

    private bool RoutePoweredMachineInterface(ISvsapApi api, MachineState state, Guid networkId, GameLocation location, MachineLocation machine)
    {
        if (!this.getConfig().EnablePoweredTransfer)
            return false;

        if (!location.Objects.TryGetValue(machine.Tile, out var placedObject))
            return false;

        var tier = GetPoweredTier(machine.QualifiedItemId);
        var powered = this.HasAtLeastEnergy(networkId, PoweredMachineInterfaceRules.WhPerAction);
        var targets = GetMachineInterfaceTargets(location, machine.Tile, tier, powered).ToList();
        foreach (var target in targets)
        {
            if (this.TryCollectMachineOutput(api, networkId, target.Object, powered))
                return true;
        }

        var settings = GetPoweredTransferSettings(placedObject, state);
        var filterQualifiedItemId = settings.FilterQualifiedItemIds.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(filterQualifiedItemId))
            return false;

        var count = Math.Clamp(settings.ItemsPerOperation * settings.CapacityMultiplier, 1, 999);
        foreach (var target in targets)
        {
            if (this.TryFeedMachineFromNetwork(api, networkId, location, target.Tile, target.Object, filterQualifiedItemId, count, powered))
                return true;
        }

        return false;
    }

    private bool TryCollectMachineOutput(ISvsapApi api, Guid networkId, SObject targetMachine, bool powered)
    {
        var held = targetMachine.heldObject.Value;
        if (held is null || held.Stack <= 0 || !targetMachine.readyForHarvest.Value)
            return false;

        var expectedCount = held.Stack;
        var probe = held.getOne();
        probe.Stack = expectedCount;
        if (api.GetInsertCapacity(networkId, probe, expectedCount) < expectedCount)
            return false;

        if (powered && !this.TryPayMachineInterfaceAction(networkId, targetMachine))
            return false;

        var moving = held.getOne();
        moving.Stack = expectedCount;
        if (!api.TryInsertItem(networkId, moving, out var remainder, out _, out _))
        {
            if (powered)
                this.TryRefundMachineInterfaceAction(networkId, targetMachine);
            return false;
        }

        var moved = expectedCount - Math.Max(0, remainder?.Stack ?? 0);
        if (moved < expectedCount)
        {
            if (moved > 0)
                api.TryExtractItem(networkId, held.QualifiedItemId, held.Quality, moved, out _, out _, out _);

            if (powered)
                this.TryRefundMachineInterfaceAction(networkId, targetMachine);
            return false;
        }

        ResetAfterAutomatedCollect(targetMachine);
        return true;
    }

    private bool TryFeedMachineFromNetwork(ISvsapApi api, Guid networkId, GameLocation location, Vector2 tile, SObject targetMachine, string qualifiedItemId, int count, bool powered)
    {
        if (targetMachine.heldObject.Value is not null || ModItemCatalog.IsSvsapmeBigCraftable(targetMachine.QualifiedItemId))
            return false;

        count = Math.Max(1, count);
        if (!TryProbeMachineInput(targetMachine, qualifiedItemId, count))
            return false;

        if (api.GetAvailableCount(networkId, qualifiedItemId, quality: null) < count)
            return false;

        if (powered && !this.TryPayMachineInterfaceAction(networkId, targetMachine))
            return false;

        if (!api.TryExtractItem(networkId, qualifiedItemId, quality: null, count, out var extracted, out _, out _)
            || extracted is null
            || extracted.Stack <= 0)
        {
            if (powered)
                this.TryRefundMachineInterfaceAction(networkId, targetMachine);
            return false;
        }

        var scratchInventory = new Inventory();
        var beforeAutoLoad = extracted.Stack;
        scratchInventory.Add(extracted);
        var accepted = false;
        try
        {
            targetMachine.AttemptAutoLoad(scratchInventory, Game1.player);
            accepted = targetMachine.heldObject.Value is not null || scratchInventory.Sum(item => item?.Stack ?? 0) < beforeAutoLoad;
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Powered Machine Interface failed to auto-load {extracted.QualifiedItemId} into {targetMachine.QualifiedItemId}: {ex.Message}", LogLevel.Trace);
        }

        this.ReturnScratchInventory(api, networkId, scratchInventory, location, tile);
        if (!accepted && powered)
            this.TryRefundMachineInterfaceAction(networkId, targetMachine);

        return accepted;
    }

    private bool HasAtLeastEnergy(Guid networkId, long requiredWh)
    {
        return this.energy.TryGetNetworkEnergy(networkId, out var storedWh, out _, out _)
            && storedWh >= requiredWh;
    }

    private bool TryPayMachineInterfaceAction(Guid networkId, SObject targetMachine)
    {
        return this.energy.TryConsumeWh(
            networkId,
            PoweredMachineInterfaceRules.WhPerAction,
            ModItemCatalog.UniqueId,
            BuildMachineEnergyReason(targetMachine, "powered-machine-interface"),
            allowPartial: false,
            out _,
            out _,
            out _);
    }

    private bool TryRefundMachineInterfaceAction(Guid networkId, SObject targetMachine)
    {
        return this.energy.TryDepositWh(
            networkId,
            PoweredMachineInterfaceRules.WhPerAction,
            ModItemCatalog.UniqueId,
            BuildMachineEnergyReason(targetMachine, "powered-machine-interface-refund"),
            out _,
            out _,
            out _);
    }

    private bool TryDepositEnergyRefund(Guid networkId, long amountWh, string reason)
    {
        return this.energy.TryDepositWh(
            networkId,
            amountWh,
            ModItemCatalog.UniqueId,
            reason,
            out var acceptedWh,
            out _,
            out _)
            && acceptedWh == amountWh;
    }

    private bool TryConsumeManualElectricMachineEnergy(GameLocation location, Vector2 tile, long amountWh)
    {
        var api = this.getSvsapApi();
        if (api is null
            || !api.TryGetLinkedEndpoint(location, tile, out var endpoint, out _, out _)
            || endpoint is null
            || !endpoint.Active)
        {
            return false;
        }

        return this.energy.TryConsumeWh(
            endpoint.NetworkId,
            amountWh,
            ModItemCatalog.UniqueId,
            BuildMachineEnergyReason(location.Objects.GetValueOrDefault(tile), "manual-electric-machine"),
            allowPartial: false,
            out _,
            out _,
            out _);
    }

    private bool TryRefundManualElectricMachineEnergy(GameLocation location, Vector2 tile, long amountWh)
    {
        var api = this.getSvsapApi();
        if (api is null
            || !api.TryGetLinkedEndpoint(location, tile, out var endpoint, out _, out _)
            || endpoint is null
            || !endpoint.Active)
        {
            return false;
        }

        return this.TryDepositEnergyRefund(endpoint.NetworkId, amountWh, BuildMachineEnergyReason(location.Objects.GetValueOrDefault(tile), "manual-electric-machine-refund"));
    }

    private PoweredTransferPlan PlanPoweredTransfer(
        Guid networkId,
        int sourceAvailable,
        int targetCapacity,
        PoweredMachineTier tier,
        MachineState state,
        int capacityMultiplier)
    {
        var storedWh = 0L;
        if (this.energy.TryGetNetworkEnergy(networkId, out var availableWh, out _, out _))
            storedWh = availableWh;

        return PoweredTransferRules.PlanImporterExporter(
            sourceAvailable,
            targetCapacity,
            tier,
            storedWh,
            ReadHalfWhCredit(state),
            PrototypeTransferThroughput,
            capacityMultiplier);
    }

    private static int GetPoweredOperationLimit(PoweredMachineTier tier, PoweredTransferSettings settings)
    {
        return PoweredTransferRules.GetEffectivePoweredThroughput(tier, PrototypeTransferThroughput)
            * Math.Clamp(settings.CapacityMultiplier, 1, 2);
    }

    private bool TryPayPoweredTransfer(Guid networkId, MachineState state, PoweredTransferPlan plan)
    {
        if (plan.Mode != PoweredTransferRunMode.Powered || plan.WhToConsume <= 0)
            return true;

        return this.energy.TryConsumeWh(
            networkId,
            plan.WhToConsume,
            ModItemCatalog.UniqueId,
            BuildMachineEnergyReason(state, "powered-transfer"),
            allowPartial: false,
            out _,
            out _,
            out _);
    }

    private bool SettlePoweredTransfer(Guid networkId, MachineState state, PoweredTransferPlan plan, int actualMoved)
    {
        if (plan.Mode != PoweredTransferRunMode.Powered)
            return false;

        var refund = PoweredTransferRules.CalculateRefund(plan.PlannedItems, actualMoved, plan.CreditAfterPrepay);
        var changed = WriteHalfWhCredit(state, refund.FinalHalfWhCredit);
        if (refund.RefundWh > 0)
        {
            if (this.energy.TryDepositWh(
                    networkId,
                    refund.RefundWh,
                    ModItemCatalog.UniqueId,
                    BuildMachineEnergyReason(state, "powered-transfer-refund"),
                    out var acceptedWh,
                    out var code,
                    out var message)
                && acceptedWh == refund.RefundWh)
            {
                changed = true;
            }
            else
            {
                this.monitor.Log($"Powered transfer refund failed for {refund.RefundWh} Wh: {code} {message}", LogLevel.Error);
            }
        }

        return changed;
    }

    private void ReturnOrDropLeftover(ISvsapApi api, Guid networkId, Item leftover, GameLocation location, Vector2 tile)
    {
        if (api.TryInsertItem(networkId, leftover, out var remainder, out _, out _)
            && (remainder is null || remainder.Stack <= 0))
        {
            return;
        }

        if (leftover.Stack > 0)
            Game1.createItemDebris(leftover, (tile + new Vector2(0.5f, 0.5f)) * Game1.tileSize, -1, location);
    }

    private void ReturnOrDropLeftoverCount(ISvsapApi api, Guid networkId, Item prototype, int count, GameLocation location, Vector2 tile)
    {
        var remaining = Math.Max(0, count);
        var chunkLimit = GetMoveChunkLimit(prototype);
        while (remaining > 0)
        {
            var chunk = prototype.getOne();
            chunk.Stack = Math.Min(remaining, chunkLimit);
            remaining -= chunk.Stack;
            this.ReturnOrDropLeftover(api, networkId, chunk, location, tile);
        }
    }

    private void ReturnScratchInventory(ISvsapApi api, Guid networkId, Inventory scratchInventory, GameLocation location, Vector2 tile)
    {
        foreach (var item in scratchInventory.Where(item => item is not null && item.Stack > 0).ToList())
            this.ReturnOrDropLeftover(api, networkId, item, location, tile);

        scratchInventory.Clear();
    }

    private bool FlushOutputBuffer(ISvsapApi api, MachineState state, Guid networkId)
    {
        var changed = false;
        for (var i = 0; i < state.OutputBuffer.Count;)
        {
            var item = BufferedItemCodec.CreateItem(state.OutputBuffer[i]);
            if (!api.TryInsertItem(networkId, item, out var remainder, out _, out _))
            {
                state.OutputBuffer[i] = BufferedItemCodec.FromItem(remainder ?? item);
                i++;
                continue;
            }

            if (remainder is null || remainder.Stack <= 0)
            {
                state.OutputBuffer.RemoveAt(i);
                changed = true;
            }
            else
            {
                state.OutputBuffer[i] = BufferedItemCodec.FromItem(remainder);
                changed = true;
                i++;
            }
        }

        return changed;
    }

    private bool TryConsumeSynthMaterials(ISvsapApi api, Guid networkId, out List<Item> rollbackItems)
    {
        rollbackItems = new List<Item>();
        foreach (var material in BatterySynthesizerRules.Materials)
        {
            if (!api.TryExtractItem(
                    networkId,
                    material.QualifiedItemId,
                    quality: null,
                    material.Count,
                    out var extracted,
                    out _,
                    out _)
                || extracted is null
                || extracted.Stack < material.Count)
            {
                this.RollBackExtracted(api, networkId, rollbackItems);
                return false;
            }

            rollbackItems.Add(extracted);
        }

        return true;
    }

    private void RollBackExtracted(ISvsapApi api, Guid networkId, IEnumerable<Item> rollbackItems)
    {
        foreach (var item in rollbackItems)
        {
            if (!api.TryInsertItem(networkId, item, out _, out var code, out var message))
                this.monitor.Log($"Failed to roll back Battery Synthesizer material {item.QualifiedItemId}: {code} {message}", LogLevel.Error);
        }
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

    private static bool TryReadMachineGuid(SObject placedObject, out Guid machineGuid)
    {
        return Guid.TryParse(placedObject.modData.GetValueOrDefault(MachineRegistryService.MachineGuidKey), out machineGuid);
    }

    private static IEnumerable<(Vector2 Tile, SObject Object)> GetAdjacentObjects(GameLocation location, Vector2 tile, PoweredTransferSettings settings)
    {
        var offsets = settings.FacingDirection >= 0 && settings.FacingDirection < AdjacentOffsets.Length
            ? new[] { AdjacentOffsets[settings.FacingDirection] }
            : AdjacentOffsets;
        foreach (var offset in offsets)
        {
            var adjacent = tile + offset;
            if (location.Objects.TryGetValue(adjacent, out var obj))
                yield return (adjacent, obj);
        }
    }

    private static IEnumerable<(Vector2 Tile, SObject Object)> GetMachineInterfaceTargets(GameLocation location, Vector2 tile, PoweredMachineTier tier, bool powered)
    {
        foreach (var offset in PoweredMachineInterfaceRules.GetOffsets(tier, powered))
        {
            var targetTile = tile + offset;
            if (location.Objects.TryGetValue(targetTile, out var obj) && obj is not Chest)
                yield return (targetTile, obj);
        }
    }

    private static PoweredMachineTier GetPoweredTier(string qualifiedItemId)
    {
        return qualifiedItemId switch
        {
            "(BC)" + ModItemCatalog.PoweredImporterSteel
                or "(BC)" + ModItemCatalog.PoweredExporterSteel
                or "(BC)" + ModItemCatalog.PoweredMachineInterfaceSteel => PoweredMachineTier.Steel,
            "(BC)" + ModItemCatalog.PoweredImporterGold
                or "(BC)" + ModItemCatalog.PoweredExporterGold
                or "(BC)" + ModItemCatalog.PoweredMachineInterfaceGold => PoweredMachineTier.Gold,
            "(BC)" + ModItemCatalog.PoweredImporterIridium
                or "(BC)" + ModItemCatalog.PoweredExporterIridium
                or "(BC)" + ModItemCatalog.PoweredMachineInterfaceIridium => PoweredMachineTier.Iridium,
            _ => PoweredMachineTier.Copper
        };
    }

    private static bool IsPoweredConfigurableMachine(string qualifiedItemId)
    {
        return qualifiedItemId is
            "(BC)" + ModItemCatalog.PoweredImporterCopper
            or "(BC)" + ModItemCatalog.PoweredImporterSteel
            or "(BC)" + ModItemCatalog.PoweredImporterGold
            or "(BC)" + ModItemCatalog.PoweredImporterIridium
            or "(BC)" + ModItemCatalog.PoweredExporterCopper
            or "(BC)" + ModItemCatalog.PoweredExporterSteel
            or "(BC)" + ModItemCatalog.PoweredExporterGold
            or "(BC)" + ModItemCatalog.PoweredExporterIridium
            or "(BC)" + ModItemCatalog.PoweredMachineInterfaceCopper
            or "(BC)" + ModItemCatalog.PoweredMachineInterfaceSteel
            or "(BC)" + ModItemCatalog.PoweredMachineInterfaceGold
            or "(BC)" + ModItemCatalog.PoweredMachineInterfaceIridium;
    }

    private static bool IsFarmMachine(string qualifiedItemId)
    {
        return qualifiedItemId is
            "(BC)" + ModItemCatalog.CopperFarm
            or "(BC)" + ModItemCatalog.SteelFarm
            or "(BC)" + ModItemCatalog.GoldFarm
            or "(BC)" + ModItemCatalog.IridiumFarm;
    }

    private static bool IsPoweredImporterMachine(string qualifiedItemId)
    {
        return qualifiedItemId is
            "(BC)" + ModItemCatalog.PoweredImporterCopper
            or "(BC)" + ModItemCatalog.PoweredImporterSteel
            or "(BC)" + ModItemCatalog.PoweredImporterGold
            or "(BC)" + ModItemCatalog.PoweredImporterIridium;
    }

    private static bool IsPoweredExporterMachine(string qualifiedItemId)
    {
        return qualifiedItemId is
            "(BC)" + ModItemCatalog.PoweredExporterCopper
            or "(BC)" + ModItemCatalog.PoweredExporterSteel
            or "(BC)" + ModItemCatalog.PoweredExporterGold
            or "(BC)" + ModItemCatalog.PoweredExporterIridium;
    }

    private static bool IsPoweredMachineInterfaceMachine(string qualifiedItemId)
    {
        return qualifiedItemId is
            "(BC)" + ModItemCatalog.PoweredMachineInterfaceCopper
            or "(BC)" + ModItemCatalog.PoweredMachineInterfaceSteel
            or "(BC)" + ModItemCatalog.PoweredMachineInterfaceGold
            or "(BC)" + ModItemCatalog.PoweredMachineInterfaceIridium;
    }

    private static void SyncPlacedObjectStateModData(GameLocation location, MachineLocation machine, MachineState state)
    {
        if (!location.Objects.TryGetValue(machine.Tile, out var placedObject)
            || placedObject.QualifiedItemId != state.QualifiedItemId
            || !TryReadMachineGuid(placedObject, out var placedGuid)
            || placedGuid != state.MachineGuid)
        {
            return;
        }

        if (state.StoredWh > 0)
            placedObject.modData[MachineRegistryService.StoredWhKey] = state.StoredWh.ToString();
        else
            placedObject.modData.Remove(MachineRegistryService.StoredWhKey);
    }

    private void SyncPoweredTransferModData(SObject placedObject, GameLocation location, Vector2 tile)
    {
        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var configuredGuid)
            || !this.repository.TryGet(configuredGuid, out var configuredState))
        {
            return;
        }

        foreach (var key in PoweredTransferModDataKeys)
        {
            if (placedObject.modData.TryGetValue(key, out var value))
                configuredState.ModData[key] = value;
            else
                configuredState.ModData.Remove(key);
        }

        this.repository.Save();
    }

    private static PoweredTransferSettings GetPoweredTransferSettings(SObject placedObject, MachineState state)
    {
        var upgrades = GetPoweredUpgradeSlots(placedObject, state);
        return new PoweredTransferSettings(
            GetPoweredFilterIds(placedObject, state),
            GetBoolModData(placedObject, state, SvsapTransferFilterBlacklistKey, false),
            GetBoolModData(placedObject, state, SvsapTransferOreDictionaryKey, false),
            NormalizeFacingDirection(GetSignedIntModData(placedObject, state, SvsapTransferFacingDirectionKey, -1)),
            GetPoweredQualityStrategy(placedObject, state),
            GetNonNegativeIntModData(placedObject, state, SvsapTransferMinSourceKeepKey, 0),
            GetNonNegativeIntModData(placedObject, state, SvsapTransferTargetKeepKey, 0),
            GetIntModData(placedObject, state, SvsapTransferItemsPerOperationKey, 1),
            HasPoweredUpgrade(upgrades, ModItemCatalog.SvsapCapacityCard) ? 2 : 1);
    }

    private static bool IsPoweredUpgradeCard(string qualifiedItemId)
    {
        return qualifiedItemId is
            ("(O)" + ModItemCatalog.SvsapSpeedCard)
            or ("(O)" + ModItemCatalog.SvsapCapacityCard)
            or ("(O)" + ModItemCatalog.SvsapQualityCard)
            or ("(O)" + ModItemCatalog.SvsapOreDictionaryCard);
    }

    private static bool HasPoweredUpgrade(IReadOnlyList<string> slots, string itemId)
    {
        return slots.Contains("(O)" + itemId, StringComparer.Ordinal);
    }

    private static List<string> GetPoweredUpgradeSlots(SObject placedObject, MachineState? state)
    {
        var raw = GetRawModData(placedObject, state, PoweredTransferUpgradeSlotsKey);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var ids = JsonSerializer.Deserialize<List<string>>(raw);
                if (ids is not null)
                    return NormalizePoweredUpgradeSlots(ids);
            }
            catch
            {
                // Invalid card data is treated as an empty upgrade bank.
            }
        }

        return NormalizePoweredUpgradeSlots(Array.Empty<string>());
    }

    private static void WritePoweredUpgradeSlots(SObject placedObject, IReadOnlyList<string> ids)
    {
        var slots = NormalizePoweredUpgradeSlots(ids);
        if (slots.All(string.IsNullOrWhiteSpace))
        {
            placedObject.modData.Remove(PoweredTransferUpgradeSlotsKey);
            return;
        }

        placedObject.modData[PoweredTransferUpgradeSlotsKey] = JsonSerializer.Serialize(slots);
    }

    private static List<string> NormalizePoweredUpgradeSlots(IEnumerable<string> ids)
    {
        var result = ids
            .Take(PoweredTransferUpgradeSlotCount)
            .Select(id => !string.IsNullOrWhiteSpace(id) && IsPoweredUpgradeCard(id.Trim()) ? id.Trim() : string.Empty)
            .ToList();
        while (result.Count < PoweredTransferUpgradeSlotCount)
            result.Add(string.Empty);

        return result;
    }

    private static List<string> GetPoweredFilterIds(SObject placedObject, MachineState? state)
    {
        return GetPoweredFilterSlots(placedObject, state)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Take(9)
            .ToList();
    }

    private static List<string> GetPoweredFilterSlots(SObject placedObject, MachineState? state)
    {
        var rawList = GetRawModData(placedObject, state, SvsapTransferFilterListKey);
        if (!string.IsNullOrWhiteSpace(rawList))
        {
            try
            {
                var ids = JsonSerializer.Deserialize<List<string>>(rawList);
                if (ids is not null)
                    return NormalizePoweredFilterSlots(ids);
            }
            catch
            {
                // fall through to the legacy single-filter key
            }
        }

        var legacy = GetRawModData(placedObject, state, SvsapTransferFilterKey);
        return string.IsNullOrWhiteSpace(legacy)
            ? NormalizePoweredFilterSlots(Array.Empty<string>())
            : NormalizePoweredFilterSlots(new[] { legacy });
    }

    private static void WritePoweredFilterIds(SObject placedObject, IEnumerable<string> ids)
    {
        var normalized = NormalizePoweredFilterIds(ids);
        if (normalized.Count == 0)
        {
            placedObject.modData.Remove(SvsapTransferFilterKey);
            placedObject.modData.Remove(SvsapTransferFilterListKey);
            return;
        }

        placedObject.modData[SvsapTransferFilterKey] = normalized[0];
        placedObject.modData[SvsapTransferFilterListKey] = JsonSerializer.Serialize(normalized);
    }

    private static void WritePoweredFilterSlots(SObject placedObject, IReadOnlyList<string> ids)
    {
        var slots = NormalizePoweredFilterSlots(ids);
        var first = slots.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        if (string.IsNullOrWhiteSpace(first))
        {
            placedObject.modData.Remove(SvsapTransferFilterKey);
            placedObject.modData.Remove(SvsapTransferFilterListKey);
            return;
        }

        placedObject.modData[SvsapTransferFilterKey] = first;
        placedObject.modData[SvsapTransferFilterListKey] = JsonSerializer.Serialize(slots);
    }

    private static List<string> NormalizePoweredFilterIds(IEnumerable<string> ids)
    {
        return ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(9)
            .ToList();
    }

    private static List<string> NormalizePoweredFilterSlots(IEnumerable<string> ids)
    {
        var result = ids
            .Take(9)
            .Select(id => string.IsNullOrWhiteSpace(id) ? string.Empty : id.Trim())
            .ToList();
        while (result.Count < 9)
            result.Add(string.Empty);

        return result;
    }

    private static string? GetRawModData(SObject placedObject, MachineState? state, string key)
    {
        var objectValue = placedObject.modData.GetValueOrDefault(key);
        if (!string.IsNullOrWhiteSpace(objectValue))
            return objectValue;

        return state?.ModData.GetValueOrDefault(key);
    }

    private static bool GetBoolModData(SObject placedObject, MachineState? state, string key, bool defaultValue)
    {
        var raw = GetRawModData(placedObject, state, key);
        return bool.TryParse(raw, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static int GetNonNegativeIntModData(SObject placedObject, MachineState? state, string key, int defaultValue)
    {
        var raw = GetRawModData(placedObject, state, key);
        return int.TryParse(raw, out var parsed)
            ? Math.Max(0, parsed)
            : defaultValue;
    }

    private static PoweredTransferQualityStrategy GetPoweredQualityStrategy(SObject placedObject, MachineState? state)
    {
        var raw = GetRawModData(placedObject, state, SvsapTransferQualityStrategyKey);
        return Enum.TryParse(raw, out PoweredTransferQualityStrategy parsed)
            ? parsed
            : PoweredTransferQualityStrategy.LowQualityFirst;
    }

    private static IEnumerable<int> GetPoweredChestSlotOrder(Chest chest, PoweredTransferQualityStrategy strategy)
    {
        var slots = Enumerable.Range(0, chest.Items.Count);
        return strategy == PoweredTransferQualityStrategy.HighQualityFirst
            ? slots.OrderByDescending(slot => chest.Items[slot]?.Quality ?? -1).ThenBy(slot => slot)
            : slots.OrderBy(slot => chest.Items[slot]?.Quality ?? int.MaxValue).ThenBy(slot => slot);
    }

    private static IEnumerable<int> GetMatchingImportSlots(Chest chest, Item prototype, PoweredTransferSettings settings)
    {
        foreach (var slot in GetPoweredChestSlotOrder(chest, settings.QualityStrategy))
        {
            var item = chest.Items[slot];
            if (CanImportPoweredSource(item, settings) && item!.canStackWith(prototype))
                yield return slot;
        }
    }

    private static IEnumerable<int> GetPoweredQualityOrder(PoweredTransferQualityStrategy strategy)
    {
        return strategy switch
        {
            PoweredTransferQualityStrategy.HighQualityFirst => new[] { 4, 2, 1, 0 },
            PoweredTransferQualityStrategy.PreserveGoldIridium => new[] { 0, 1 },
            _ => new[] { 0, 1, 2, 4 }
        };
    }

    private static bool IsHighQualityFirst(PoweredTransferQualityStrategy strategy)
    {
        return strategy == PoweredTransferQualityStrategy.HighQualityFirst;
    }

    private static bool IsPreserveGoldIridium(PoweredTransferQualityStrategy strategy)
    {
        return strategy == PoweredTransferQualityStrategy.PreserveGoldIridium;
    }

    private static bool AllowsPoweredQuality(Item item, PoweredTransferQualityStrategy strategy)
    {
        return strategy != PoweredTransferQualityStrategy.PreserveGoldIridium || item.Quality < 2;
    }

    private static bool CanImportPoweredSource(Item? item, PoweredTransferSettings settings)
    {
        return item is not null
            && item.Stack > 0
            && MatchesPoweredFilter(item, settings)
            && AllowsPoweredQuality(item, settings.QualityStrategy);
    }

    private static bool MatchesPoweredFilter(Item item, PoweredTransferSettings settings)
    {
        if (settings.FilterQualifiedItemIds.Count == 0)
            return true;

        var same = settings.OreDictionaryMode
            ? PoweredOreDictionaryMatches(item, settings.FilterQualifiedItemIds)
            : settings.FilterQualifiedItemIds.Contains(item.QualifiedItemId, StringComparer.Ordinal);
        return settings.FilterBlacklist ? !same : same;
    }

    private static int CountMatching(Chest chest, string qualifiedItemId)
    {
        return chest.Items
            .Where(item => item is not null && item.QualifiedItemId == qualifiedItemId)
            .Sum(item => item!.Stack);
    }

    private static int CountMatching(Chest chest, Item prototype, PoweredTransferSettings settings)
    {
        return chest.Items
            .Where(item => item is not null && MatchesPoweredTargetItem(item, prototype, settings))
            .Sum(item => item!.Stack);
    }

    private static bool MatchesPoweredTargetItem(Item item, Item prototype, PoweredTransferSettings settings)
    {
        if (!settings.OreDictionaryMode)
            return item.QualifiedItemId == prototype.QualifiedItemId;

        return PoweredOreDictionaryMatches(item, new[] { prototype.QualifiedItemId });
    }

    private static bool PoweredOreDictionaryMatches(Item item, IReadOnlyList<string> filterQualifiedItemIds)
    {
        if (filterQualifiedItemIds.Count == 0)
            return true;

        var groups = GetPoweredOreGroups(item);
        foreach (var filterId in filterQualifiedItemIds)
        {
            if (item.QualifiedItemId == filterId)
                return true;

            if (groups.Count == 0 || !TryCreateItem(filterId, out var filterItem))
                continue;

            if (groups.Overlaps(GetPoweredOreGroups(filterItem)))
                return true;
        }

        return false;
    }

    private static HashSet<string> GetPoweredOreGroups(Item item)
    {
        var groups = new HashSet<string>(StringComparer.Ordinal);
        if (item is SObject obj)
        {
            if (obj.Category == -4)
                groups.Add("ore:fish");
            if (obj.Category is -2 or -12)
                groups.Add("ore:mineral");
            if (obj.Category is -15 or -16)
                groups.Add("ore:material");
            if (obj.Category is -5 or -6 or -26 or -27)
                groups.Add("ore:processed");
        }

        foreach (var group in GetExplicitPoweredOreGroups(item.QualifiedItemId))
            groups.Add(group);

        try
        {
            foreach (var tag in item.GetContextTags())
            {
                if (!string.IsNullOrWhiteSpace(tag)
                    && !tag.StartsWith("color_", StringComparison.OrdinalIgnoreCase)
                    && !tag.StartsWith("quality_", StringComparison.OrdinalIgnoreCase)
                    && !tag.StartsWith("season_", StringComparison.OrdinalIgnoreCase))
                {
                    groups.Add("tag:" + tag);
                }
            }
        }
        catch
        {
            // Context tags are optional for this compatibility matcher.
        }

        return groups;
    }

    private static IEnumerable<string> GetExplicitPoweredOreGroups(string qualifiedItemId)
    {
        return qualifiedItemId switch
        {
            "(O)378" => new[] { "ore:metal", "ore:copper" },
            "(O)380" => new[] { "ore:metal", "ore:iron" },
            "(O)384" => new[] { "ore:metal", "ore:gold" },
            "(O)386" => new[] { "ore:metal", "ore:iridium" },
            "(O)909" => new[] { "ore:metal", "ore:radioactive" },
            "(O)334" => new[] { "ingot:metal", "ingot:copper" },
            "(O)335" => new[] { "ingot:metal", "ingot:iron" },
            "(O)336" => new[] { "ingot:metal", "ingot:gold" },
            "(O)337" => new[] { "ingot:metal", "ingot:iridium" },
            "(O)910" => new[] { "ingot:metal", "ingot:radioactive" },
            "(O)382" => new[] { "resource:coal", "fuel:coal" },
            "(O)787" => new[] { "resource:battery", "component:battery" },
            _ => Array.Empty<string>()
        };
    }

    private static bool TryCreateItem(string qualifiedItemId, out Item item)
    {
        try
        {
            item = ItemRegistry.Create(qualifiedItemId);
            return true;
        }
        catch
        {
            item = null!;
            return false;
        }
    }

    private static string FormatPoweredQualityStrategy(PoweredTransferQualityStrategy strategy)
    {
        return strategy switch
        {
            PoweredTransferQualityStrategy.HighQualityFirst => ModText.Get("ui.machine.powered.quality.highFirst", "high quality first"),
            PoweredTransferQualityStrategy.PreserveGoldIridium => ModText.Get("ui.machine.powered.quality.preserveGoldIridium", "preserve gold/iridium"),
            _ => ModText.Get("ui.machine.powered.quality.lowFirst", "low quality first")
        };
    }

    private static string FormatPoweredFacingDirection(int facingDirection)
    {
        return NormalizeFacingDirection(facingDirection) switch
        {
            0 => ModText.Get("ui.machine.powered.direction.up", "up"),
            1 => ModText.Get("ui.machine.powered.direction.right", "right"),
            2 => ModText.Get("ui.machine.powered.direction.down", "down"),
            3 => ModText.Get("ui.machine.powered.direction.left", "left"),
            _ => ModText.Get("ui.machine.powered.direction.all", "all")
        };
    }

    private static int GetChestAcceptCount(Chest chest, Item item, int maxCount)
    {
        var remaining = Math.Max(0, maxCount);
        var capacity = GetChestSlotCapacity(chest);
        foreach (var stack in chest.Items)
        {
            if (remaining <= 0)
                break;

            if (stack is null)
            {
                remaining -= item.maximumStackSize();
                continue;
            }

            if (stack.canStackWith(item))
                remaining -= Math.Max(0, stack.maximumStackSize() - stack.Stack);
        }

        var missingSlots = Math.Max(0, capacity - chest.Items.Count);
        if (remaining > 0 && missingSlots > 0)
            remaining -= missingSlots * Math.Max(1, item.maximumStackSize());

        return Math.Max(0, maxCount - Math.Max(0, remaining));
    }

    private static int GetChestSlotCapacity(Chest chest)
    {
        var actualCapacity = TryGetChestActualCapacity(chest);
        if (actualCapacity > 0)
            return Math.Max(actualCapacity, chest.Items.Count);

        var baseline = chest.SpecialChestType.ToString().Contains("Big", StringComparison.OrdinalIgnoreCase)
            ? 70
            : 36;
        return Math.Max(baseline, chest.Items.Count);
    }

    private static int TryGetChestActualCapacity(Chest chest)
    {
        try
        {
            var method = chest.GetType().GetMethod(
                "GetActualCapacity",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            if (method?.ReturnType != typeof(int))
                return 0;

            return Math.Max(0, (int)(method.Invoke(chest, null) ?? 0));
        }
        catch
        {
            return 0;
        }
    }

    private static int GetMoveChunkLimit(Item item)
    {
        return Math.Max(1, item.maximumStackSize());
    }

    private static bool TryProbeMachineInput(SObject machine, string qualifiedItemId, int count)
    {
        try
        {
            var probeItem = ItemRegistry.Create(qualifiedItemId);
            probeItem.Stack = Math.Max(1, count);
            return machine.performObjectDropInAction(probeItem, true, Game1.player, false);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsIdleMachine(SObject machine)
    {
        return machine.heldObject.Value is null
            && !machine.readyForHarvest.Value
            && machine.MinutesUntilReady <= 0;
    }

    private static void StartElectricMachine(SObject machine, Item output, int minutesUntilReady)
    {
        machine.heldObject.Value = (SObject)output;
        machine.readyForHarvest.Value = false;
        machine.showNextIndex.Value = false;
        machine.MinutesUntilReady = Math.Max(1, minutesUntilReady);
    }

    private static void ResetAfterAutomatedCollect(SObject machine)
    {
        machine.heldObject.Value = null;
        machine.readyForHarvest.Value = false;
        machine.MinutesUntilReady = 0;
        machine.showNextIndex.Value = false;
    }

    private static int GetIntModData(SObject placedObject, MachineState state, string key, int defaultValue)
    {
        if (int.TryParse(placedObject.modData.GetValueOrDefault(key), out var objectValue))
            return Math.Max(1, objectValue);

        return int.TryParse(state.ModData.GetValueOrDefault(key), out var stateValue)
            ? Math.Max(1, stateValue)
            : defaultValue;
    }

    private static int GetSignedIntModData(SObject placedObject, MachineState state, string key, int defaultValue)
    {
        if (int.TryParse(placedObject.modData.GetValueOrDefault(key), out var objectValue))
            return objectValue;

        return int.TryParse(state.ModData.GetValueOrDefault(key), out var stateValue)
            ? stateValue
            : defaultValue;
    }

    private static int NormalizeFacingDirection(int facingDirection)
    {
        return facingDirection is >= 0 and < 4 ? facingDirection : -1;
    }

    private static int CountPlayerItems(string qualifiedItemId)
    {
        if (Game1.player is null)
            return 0;

        return Game1.player.Items
            .Where(item => item is not null && item.QualifiedItemId == qualifiedItemId)
            .Sum(item => item!.Stack);
    }

    private static bool TryConsumePlayerItems(string qualifiedItemId, int count)
    {
        if (Game1.player is null || count <= 0 || CountPlayerItems(qualifiedItemId) < count)
            return false;

        var remaining = count;
        for (var i = 0; i < Game1.player.Items.Count && remaining > 0; i++)
        {
            var item = Game1.player.Items[i];
            if (item is null || item.QualifiedItemId != qualifiedItemId)
                continue;

            var taken = Math.Min(remaining, item.Stack);
            item.Stack -= taken;
            remaining -= taken;
            if (item.Stack <= 0)
                Game1.player.Items[i] = null;
        }

        return remaining == 0;
    }

    private static int ReadHalfWhCredit(MachineState state)
    {
        return int.TryParse(state.ModData.GetValueOrDefault(PoweredTransferHalfWhCreditKey), out var credit)
            ? Math.Clamp(credit, 0, 1)
            : 0;
    }

    private static bool WriteHalfWhCredit(MachineState state, int credit)
    {
        credit = Math.Clamp(credit, 0, 1);
        var previous = ReadHalfWhCredit(state);
        if (previous == credit)
            return false;

        if (credit == 0)
            state.ModData.Remove(PoweredTransferHalfWhCreditKey);
        else
            state.ModData[PoweredTransferHalfWhCreditKey] = credit.ToString();

        return true;
    }

    private void HelperSuppress(ButtonPressedEventArgs e)
    {
        try
        {
            this.inputHelper.Suppress(e.Button);
        }
        catch (InvalidOperationException ex)
        {
            this.monitor.Log($"Could not suppress input after SVSAPME interaction: {ex.Message}", LogLevel.Trace);
        }
    }

    private enum PoweredTransferQualityStrategy
    {
        LowQualityFirst,
        HighQualityFirst,
        PreserveGoldIridium
    }

    private readonly record struct PoweredTransferSettings(
        IReadOnlyList<string> FilterQualifiedItemIds,
        bool FilterBlacklist,
        bool OreDictionaryMode,
        int FacingDirection,
        PoweredTransferQualityStrategy QualityStrategy,
        int MinSourceKeep,
        int TargetKeep,
        int ItemsPerOperation,
        int CapacityMultiplier);
}

internal sealed class PoweredTransferFilterSlotView
{
    public int SlotIndex { get; init; }
    public bool Occupied => this.Item is not null;
    public string QualifiedItemId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public Item? Item { get; init; }
    public IReadOnlyList<string> OreGroups { get; init; } = Array.Empty<string>();

    public static PoweredTransferFilterSlotView Empty(int slotIndex)
    {
        return new PoweredTransferFilterSlotView { SlotIndex = slotIndex };
    }
}

internal readonly record struct PoweredTransferMenuView(
    bool IsBlacklist,
    bool OreDictionaryEnabled,
    string QualityStrategy,
    int FacingDirection,
    IReadOnlyList<PoweredTransferFilterSlotView> FilterSlots,
    int Throughput,
    int TransferIntervalTicks,
    decimal EnergyPerActionWh,
    IReadOnlyList<string> UpgradeSlotQualifiedItemIds);

internal readonly record struct PoweredNetworkStatusView(bool Online, long StoredWh, long CapacityWh);
