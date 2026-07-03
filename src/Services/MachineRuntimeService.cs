using Koizumi.SVSAP.Api;
using Microsoft.Xna.Framework;
using SVSAPME.Content;
using SVSAPME.Models;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace SVSAPME.Services;

internal sealed class MachineRuntimeService
{
    private const int PrototypeTransferThroughput = 64;
    private const string PoweredTransferHalfWhCreditKey = ModItemCatalog.UniqueId + "/PoweredTransferHalfWhCredit";
    private const string SvsapTransferFilterKey = ModItemCatalog.SvsapUniqueId + "/TransferFilter";
    private const string SvsapTransferFilterBlacklistKey = ModItemCatalog.SvsapUniqueId + "/TransferFilterBlacklist";
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
        SvsapTransferFilterBlacklistKey,
        SvsapTransferQualityStrategyKey,
        SvsapTransferMinSourceKeepKey,
        SvsapTransferTargetKeepKey,
        SvsapTransferItemsPerOperationKey
    };

    private readonly MachineStateRepository repository;
    private readonly MachineRegistryService registry;
    private readonly EnergyNetworkManager energy;
    private readonly Func<ISvsapApi?> getSvsapApi;
    private readonly Func<ModConfig> getConfig;
    private readonly IInputHelper inputHelper;
    private readonly IMonitor monitor;
    private Func<SvsapmeMachineActionRequest, bool>? sendClientAction;
    private uint lastRouteTick;

    public MachineRuntimeService(
        MachineStateRepository repository,
        MachineRegistryService registry,
        EnergyNetworkManager energy,
        Func<ISvsapApi?> getSvsapApi,
        Func<ModConfig> getConfig,
        IInputHelper inputHelper,
        IMonitor monitor)
    {
        this.repository = repository;
        this.registry = registry;
        this.energy = energy;
        this.getSvsapApi = getSvsapApi;
        this.getConfig = getConfig;
        this.inputHelper = inputHelper;
        this.monitor = monitor;
    }

    public void SetClientActionSender(Func<SvsapmeMachineActionRequest, bool> sender)
    {
        this.sendClientAction = sender;
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

        if (IsPoweredConfigurableMachine(placedObject.QualifiedItemId))
        {
            if (Game1.player.CurrentItem is null)
            {
                var filter = placedObject.modData.GetValueOrDefault(SvsapTransferFilterKey);
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

    internal SvsapmeMachineActionApplyResult TryConfigurePoweredFilter(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        string filterQualifiedItemId,
        int count)
    {
        if (!IsPoweredConfigurableMachine(placedObject.QualifiedItemId))
            return new(false, false, "Target machine does not accept a powered filter.");

        if (string.IsNullOrWhiteSpace(filterQualifiedItemId))
            return new(false, false, "Powered filter item is missing.");

        var clampedCount = Math.Clamp(count, 1, 999);
        if (filterQualifiedItemId == "(O)" + ModItemCatalog.SvsapFilterCard)
        {
            var hasFilter = !string.IsNullOrWhiteSpace(placedObject.modData.GetValueOrDefault(SvsapTransferFilterKey));
            if (!hasFilter)
            {
                placedObject.modData.Remove(SvsapTransferFilterBlacklistKey);
                this.SyncPoweredTransferModData(placedObject, location, tile);
                return new(true, false, "Powered transfer filter is already empty.");
            }

            if (!GetBoolModData(placedObject, null, SvsapTransferFilterBlacklistKey, false))
            {
                placedObject.modData[SvsapTransferFilterBlacklistKey] = true.ToString();
                this.SyncPoweredTransferModData(placedObject, location, tile);
                return new(true, true, "Powered transfer filter mode: blacklist.");
            }

            placedObject.modData.Remove(SvsapTransferFilterKey);
            placedObject.modData.Remove(SvsapTransferFilterBlacklistKey);
            placedObject.modData.Remove(SvsapTransferMinSourceKeepKey);
            placedObject.modData.Remove(SvsapTransferTargetKeepKey);
            this.SyncPoweredTransferModData(placedObject, location, tile);
            return new(true, true, "Powered transfer filter cleared.");
        }

        if (filterQualifiedItemId == "(O)" + ModItemCatalog.SvsapQualityCard)
        {
            var strategy = GetPoweredQualityStrategy(placedObject, null) == PoweredTransferQualityStrategy.LowQualityFirst
                ? PoweredTransferQualityStrategy.HighQualityFirst
                : PoweredTransferQualityStrategy.LowQualityFirst;
            placedObject.modData[SvsapTransferQualityStrategyKey] = strategy.ToString();
            this.SyncPoweredTransferModData(placedObject, location, tile);
            return new(true, true, $"Powered transfer quality: {FormatPoweredQualityStrategy(strategy)}.");
        }

        if (filterQualifiedItemId is ("(O)" + ModItemCatalog.SvsapSpeedCard) or ("(O)" + ModItemCatalog.SvsapCapacityCard))
            return new(false, false, "Powered transfer speed and capacity are fixed by machine tier.");

        Item probe;
        try
        {
            probe = ItemRegistry.Create(filterQualifiedItemId);
        }
        catch (Exception ex)
        {
            return new(false, false, $"Powered filter item is invalid: {ex.Message}");
        }

        if (placedObject.modData.GetValueOrDefault(SvsapTransferFilterKey) == filterQualifiedItemId)
        {
            if (IsPoweredImporterMachine(placedObject.QualifiedItemId))
            {
                placedObject.modData[SvsapTransferMinSourceKeepKey] = clampedCount.ToString();
                this.SyncPoweredTransferModData(placedObject, location, tile);
                return new(true, false, $"Powered Importer will keep {clampedCount:N0} in the source.");
            }

            if (IsPoweredExporterMachine(placedObject.QualifiedItemId))
            {
                placedObject.modData[SvsapTransferTargetKeepKey] = clampedCount.ToString();
                this.SyncPoweredTransferModData(placedObject, location, tile);
                return new(true, false, $"Powered Exporter will maintain {clampedCount:N0} in the target.");
            }

            placedObject.modData[SvsapTransferItemsPerOperationKey] = clampedCount.ToString();
            this.SyncPoweredTransferModData(placedObject, location, tile);
            return new(true, false, $"Powered Machine Interface feed count set to {clampedCount:N0}.");
        }

        placedObject.modData[SvsapTransferFilterKey] = filterQualifiedItemId;
        placedObject.modData.Remove(SvsapTransferFilterBlacklistKey);
        if (IsPoweredMachineInterfaceMachine(placedObject.QualifiedItemId))
            placedObject.modData[SvsapTransferItemsPerOperationKey] = clampedCount.ToString();
        else
            placedObject.modData.Remove(SvsapTransferItemsPerOperationKey);

        this.SyncPoweredTransferModData(placedObject, location, tile);
        return new(true, false, $"Powered transfer filter set to {probe.DisplayName}.");
    }

    internal SvsapmeMachineActionApplyResult TryFuelCarbonGenerator(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        string fuelQualifiedItemId)
    {
        if (placedObject.QualifiedItemId != "(BC)" + ModItemCatalog.CarbonGenerator)
            return new(false, false, "Target machine is not a Carbon Generator.");

        if (!CarbonGeneratorFuelRules.TryGetFuelWh(fuelQualifiedItemId, this.getConfig().EnableExtendedGeneratorFuels, out var fuelWh))
            return new(false, false, "Carbon Generator does not accept this fuel with the current config.");

        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return new(false, false, "SVSAPME could not register this machine.");
        }

        var capacity = Math.Max(0, state.CapacityWh);
        state.StoredWh = Math.Clamp(state.StoredWh, 0, capacity);
        if (capacity - state.StoredWh < fuelWh)
            return new(false, false, "Carbon Generator buffer is full.");

        state.StoredWh += fuelWh;
        placedObject.modData[MachineRegistryService.StoredWhKey] = state.StoredWh.ToString();
        this.repository.Save();
        return new(true, true, $"Carbon Generator buffered {state.StoredWh / 1000m:0.00}/{capacity / 1000m:0.00} kWh.");
    }

    private void RunRouteTick()
    {
        var api = this.getSvsapApi();
        if (api is null)
            return;

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
                    machineChanged |= this.RouteBatteryDischarger(api, endpoint.NetworkId);
                    break;

                case "(BC)" + ModItemCatalog.PoweredImporterCopper:
                case "(BC)" + ModItemCatalog.PoweredImporterSteel:
                case "(BC)" + ModItemCatalog.PoweredImporterGold:
                case "(BC)" + ModItemCatalog.PoweredImporterIridium:
                    machineChanged |= this.RoutePoweredImporter(api, state, endpoint.NetworkId, location, machine);
                    break;

                case "(BC)" + ModItemCatalog.PoweredExporterCopper:
                case "(BC)" + ModItemCatalog.PoweredExporterSteel:
                case "(BC)" + ModItemCatalog.PoweredExporterGold:
                case "(BC)" + ModItemCatalog.PoweredExporterIridium:
                    machineChanged |= this.RoutePoweredExporter(api, state, endpoint.NetworkId, location, machine);
                    break;

                case "(BC)" + ModItemCatalog.PoweredMachineInterfaceCopper:
                case "(BC)" + ModItemCatalog.PoweredMachineInterfaceSteel:
                case "(BC)" + ModItemCatalog.PoweredMachineInterfaceGold:
                case "(BC)" + ModItemCatalog.PoweredMachineInterfaceIridium:
                    machineChanged |= this.RoutePoweredMachineInterface(api, state, endpoint.NetworkId, location, machine);
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

    private void ShowEnergyMonitor(GameLocation location, Vector2 tile)
    {
        var api = this.getSvsapApi();
        if (api is null
            || !api.TryGetLinkedEndpoint(location, tile, out var endpoint, out _, out _)
            || endpoint is null
            || !endpoint.Active)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.energyMonitor.notLinked", "Energy Monitor is not linked to an active SVSAP network."), HUDMessage.error_type));
            return;
        }

        if (!this.energy.TryGetNetworkEnergy(endpoint.NetworkId, out var storedWh, out var capacityWh, out var code))
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.energyMonitor.failed", "Energy report failed: {{code}}.", new { code }), HUDMessage.error_type));
            return;
        }

        Game1.addHUDMessage(new HUDMessage(
            ModText.Get(
                "hud.energyMonitor.report",
                "SVSAPME energy: {{storedKwh}}/{{capacityKwh}} kWh.",
                new { storedKwh = $"{storedWh / 1000m:0.00}", capacityKwh = $"{capacityWh / 1000m:0.00}" }),
            HUDMessage.newQuest_type));
    }

    private bool RouteCarbonGenerator(MachineState state, Guid networkId)
    {
        if (!this.getConfig().EnableCarbonGenerator || state.StoredWh <= 0)
            return false;

        if (this.energy.TryDepositWh(
                networkId,
                state.StoredWh,
                ModItemCatalog.UniqueId,
                "carbon-generator-route",
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

    private bool RouteBatteryDischarger(ISvsapApi api, Guid networkId)
    {
        var config = this.getConfig();
        var outputWh = (long)Math.Round(BatteryDischargerRules.DefaultOutputWh * Math.Clamp(config.BatteryDischargeEfficiency / 0.8, 0.0, 10.0));
        if (!this.energy.TryGetNetworkEnergy(networkId, out var storedWh, out var capacityWh, out _))
            return false;

        var available = api.GetAvailableCount(networkId, BatteryDischargerRules.BatteryPackQualifiedItemId, quality: null);
        if (!BatteryDischargerRules.CanDischarge(config.AllowBatteryDischarge, available, storedWh, capacityWh, outputWh))
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
                "battery-discharger",
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
                    "battery-synthesizer-charge",
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
                    "electric-furnace",
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
                this.TryDepositEnergyRefund(networkId, ElectricMachineRules.FurnaceWhPerRun, "electric-furnace-refund");
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
                    "electric-geode-crusher",
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
                this.TryDepositEnergyRefund(networkId, ElectricMachineRules.GeodeCrusherWhPerRun, "electric-geode-crusher-refund");
                return false;
            }

            var output = Utility.getTreasureFromGeode(extracted);
            if (output is null)
            {
                this.TryDepositEnergyRefund(networkId, ElectricMachineRules.GeodeCrusherWhPerRun, "electric-geode-crusher-refund");
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
        Game1.addHUDMessage(new HUDMessage(canUsePower ? "Electric Geode Crusher started with network power." : "Electric Geode Crusher started on prototype coal path.", HUDMessage.newQuest_type));
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
        foreach (var (_, obj) in GetAdjacentObjects(location, machine.Tile))
        {
            if (obj is Chest chest)
            {
                if (this.TryRoutePoweredImportFromChest(api, state, networkId, chest, tier, settings))
                    return true;

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

            var maxCandidate = Math.Min(sourceAvailable, Math.Max(PoweredTransferRules.GetPoweredThroughput(tier), PrototypeTransferThroughput));
            var probe = source!.getOne();
            probe.Stack = maxCandidate;
            var targetCapacity = api.GetInsertCapacity(networkId, probe, maxCandidate);
            var plan = this.PlanPoweredTransfer(networkId, sourceAvailable, targetCapacity, tier, state);
            if (plan.Mode == PoweredTransferRunMode.None)
                continue;

            if (!this.TryPayPoweredTransfer(networkId, plan))
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
        var maxCandidate = Math.Min(sourceAvailable, Math.Max(PoweredTransferRules.GetPoweredThroughput(tier), PrototypeTransferThroughput));
        var probe = held.getOne();
        probe.Stack = maxCandidate;
        var targetCapacity = api.GetInsertCapacity(networkId, probe, maxCandidate);
        var plan = this.PlanPoweredTransfer(networkId, sourceAvailable, targetCapacity, tier, state);
        if (plan.Mode == PoweredTransferRunMode.None)
            return false;

        if (!this.TryPayPoweredTransfer(networkId, plan))
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
        if (string.IsNullOrWhiteSpace(settings.FilterQualifiedItemId))
            return false;

        var tier = GetPoweredTier(machine.QualifiedItemId);
        foreach (var (tile, obj) in GetAdjacentObjects(location, machine.Tile))
        {
            if (obj is not Chest chest)
                continue;

            if (settings.FilterBlacklist)
            {
                if (this.TryRoutePoweredExportBlacklist(api, state, networkId, location, tile, chest, tier, settings))
                    return true;

                continue;
            }

            if (this.TryRoutePoweredExportWhitelist(api, state, networkId, location, tile, chest, tier, settings))
                return true;
        }

        return false;
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
        if (string.IsNullOrWhiteSpace(settings.FilterQualifiedItemId))
            return false;

        if (settings.TargetKeep > 0 && CountMatching(chest, settings.FilterQualifiedItemId) >= settings.TargetKeep)
            return false;

        if (!TryCreateItem(settings.FilterQualifiedItemId, out var probe))
        {
            this.monitor.Log($"Powered Exporter skipped invalid filter {settings.FilterQualifiedItemId}.", LogLevel.Trace);
            return false;
        }

        if (!this.TrySelectPoweredExportQuality(api, networkId, settings, out var quality, out var sourceAvailable))
            return false;

        if (quality.HasValue)
            probe.Quality = quality.Value;

        var targetRemaining = settings.TargetKeep > 0
            ? Math.Max(0, settings.TargetKeep - CountMatching(chest, settings.FilterQualifiedItemId))
            : int.MaxValue;
        var operationLimit = Math.Max(PoweredTransferRules.GetPoweredThroughput(tier), PrototypeTransferThroughput);
        var maxCandidate = Math.Min(Math.Min(sourceAvailable, targetRemaining), operationLimit);
        if (maxCandidate <= 0)
            return false;

        probe.Stack = maxCandidate;
        var targetCapacity = GetChestAcceptCount(chest, probe, maxCandidate);
        var plan = this.PlanPoweredTransfer(networkId, maxCandidate, targetCapacity, tier, state);
        if (plan.Mode == PoweredTransferRunMode.None)
            return false;

        if (!this.TryPayPoweredTransfer(networkId, plan))
            return false;

        var accepted = this.ExtractFilteredItemsToChest(api, networkId, location, targetTile, chest, settings.FilterQualifiedItemId, quality, probe, plan.PlannedItems);

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
        var operationLimit = Math.Max(PoweredTransferRules.GetPoweredThroughput(tier), PrototypeTransferThroughput);
        bool CandidateCanMove(Item item)
        {
            if (!MatchesPoweredFilter(item, settings))
                return false;

            if (settings.TargetKeep > 0 && CountMatching(chest, item.QualifiedItemId) >= settings.TargetKeep)
                return false;

            var targetRemaining = settings.TargetKeep > 0
                ? Math.Max(0, settings.TargetKeep - CountMatching(chest, item.QualifiedItemId))
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
            ? Math.Max(0, settings.TargetKeep - CountMatching(chest, prototype.QualifiedItemId))
            : int.MaxValue;
        var maxCandidate = Math.Min(Math.Min(sourceAvailable, targetRemainingForPrototype), operationLimit);
        if (maxCandidate <= 0)
            return false;

        var probe = prototype.getOne();
        probe.Stack = maxCandidate;
        var targetCapacity = GetChestAcceptCount(chest, probe, maxCandidate);
        var plan = this.PlanPoweredTransfer(networkId, maxCandidate, targetCapacity, tier, state);
        if (plan.Mode == PoweredTransferRunMode.None)
            return false;

        if (!this.TryPayPoweredTransfer(networkId, plan))
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
                    ? Math.Max(0, settings.TargetKeep - CountMatching(chest, item.QualifiedItemId))
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
        var accepted = 0;
        var remaining = Math.Max(0, extracted.Stack);
        var chunkLimit = GetMoveChunkLimit(extracted);
        while (remaining > 0)
        {
            var chunkCount = Math.Min(remaining, chunkLimit);
            var chunk = extracted.getOne();
            chunk.Stack = chunkCount;
            var leftover = chest.addItem(chunk);
            var moved = chunkCount - Math.Max(0, leftover?.Stack ?? 0);
            accepted += moved;
            remaining -= chunkCount;
            if (leftover is null || leftover.Stack <= 0)
                continue;

            this.ReturnOrDropLeftover(api, networkId, leftover, location, targetTile);
            if (remaining > 0)
                this.ReturnOrDropLeftoverCount(api, networkId, extracted, remaining, location, targetTile);
            break;
        }

        return accepted;
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
                    item => Math.Min(Math.Min(remaining, GetMoveChunkLimit(item)), Math.Max(0, requestedCountSelector(item))),
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

    private bool TrySelectPoweredExportQuality(
        ISvsapApi api,
        Guid networkId,
        PoweredTransferSettings settings,
        out int? quality,
        out int sourceAvailable)
    {
        quality = null;
        sourceAvailable = 0;
        if (string.IsNullOrWhiteSpace(settings.FilterQualifiedItemId))
            return false;

        foreach (var candidateQuality in GetPoweredQualityOrder(settings.QualityStrategy))
        {
            var available = api.GetAvailableCount(networkId, settings.FilterQualifiedItemId, candidateQuality);
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
        var filterQualifiedItemId = settings.FilterQualifiedItemId;

        if (string.IsNullOrWhiteSpace(filterQualifiedItemId))
            return false;

        var count = Math.Max(1, settings.ItemsPerOperation);
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

        if (powered && !this.TryPayMachineInterfaceAction(networkId))
            return false;

        var moving = held.getOne();
        moving.Stack = expectedCount;
        if (!api.TryInsertItem(networkId, moving, out var remainder, out _, out _))
        {
            if (powered)
                this.TryRefundMachineInterfaceAction(networkId);
            return false;
        }

        var moved = expectedCount - Math.Max(0, remainder?.Stack ?? 0);
        if (moved < expectedCount)
        {
            if (moved > 0)
                api.TryExtractItem(networkId, held.QualifiedItemId, held.Quality, moved, out _, out _, out _);

            if (powered)
                this.TryRefundMachineInterfaceAction(networkId);
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

        if (powered && !this.TryPayMachineInterfaceAction(networkId))
            return false;

        if (!api.TryExtractItem(networkId, qualifiedItemId, quality: null, count, out var extracted, out _, out _)
            || extracted is null
            || extracted.Stack <= 0)
        {
            if (powered)
                this.TryRefundMachineInterfaceAction(networkId);
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
            this.TryRefundMachineInterfaceAction(networkId);

        return accepted;
    }

    private bool HasAtLeastEnergy(Guid networkId, long requiredWh)
    {
        return this.energy.TryGetNetworkEnergy(networkId, out var storedWh, out _, out _)
            && storedWh >= requiredWh;
    }

    private bool TryPayMachineInterfaceAction(Guid networkId)
    {
        return this.energy.TryConsumeWh(
            networkId,
            PoweredMachineInterfaceRules.WhPerAction,
            ModItemCatalog.UniqueId,
            "powered-machine-interface",
            allowPartial: false,
            out _,
            out _,
            out _);
    }

    private bool TryRefundMachineInterfaceAction(Guid networkId)
    {
        return this.energy.TryDepositWh(
            networkId,
            PoweredMachineInterfaceRules.WhPerAction,
            ModItemCatalog.UniqueId,
            "powered-machine-interface-refund",
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
            "manual-electric-machine",
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

        return this.TryDepositEnergyRefund(endpoint.NetworkId, amountWh, "manual-electric-machine-refund");
    }

    private PoweredTransferPlan PlanPoweredTransfer(Guid networkId, int sourceAvailable, int targetCapacity, PoweredMachineTier tier, MachineState state)
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
            PrototypeTransferThroughput);
    }

    private bool TryPayPoweredTransfer(Guid networkId, PoweredTransferPlan plan)
    {
        if (plan.Mode != PoweredTransferRunMode.Powered || plan.WhToConsume <= 0)
            return true;

        return this.energy.TryConsumeWh(
            networkId,
            plan.WhToConsume,
            ModItemCatalog.UniqueId,
            "powered-transfer",
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
                    "powered-transfer-refund",
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

    private static IEnumerable<(Vector2 Tile, SObject Object)> GetAdjacentObjects(GameLocation location, Vector2 tile)
    {
        foreach (var offset in AdjacentOffsets)
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
        return new PoweredTransferSettings(
            GetRawModData(placedObject, state, SvsapTransferFilterKey),
            GetBoolModData(placedObject, state, SvsapTransferFilterBlacklistKey, false),
            GetPoweredQualityStrategy(placedObject, state),
            GetNonNegativeIntModData(placedObject, state, SvsapTransferMinSourceKeepKey, 0),
            GetNonNegativeIntModData(placedObject, state, SvsapTransferTargetKeepKey, 0),
            GetIntModData(placedObject, state, SvsapTransferItemsPerOperationKey, 1));
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
        if (string.IsNullOrWhiteSpace(settings.FilterQualifiedItemId))
            return true;

        var same = string.Equals(item.QualifiedItemId, settings.FilterQualifiedItemId, StringComparison.Ordinal);
        return settings.FilterBlacklist ? !same : same;
    }

    private static int CountMatching(Chest chest, string qualifiedItemId)
    {
        return chest.Items
            .Where(item => item is not null && item.QualifiedItemId == qualifiedItemId)
            .Sum(item => item!.Stack);
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
            PoweredTransferQualityStrategy.HighQualityFirst => "high quality first",
            PoweredTransferQualityStrategy.PreserveGoldIridium => "preserve gold/iridium",
            _ => "low quality first"
        };
    }

    private static int GetChestAcceptCount(Chest chest, Item item, int maxCount)
    {
        var remaining = Math.Max(0, maxCount);
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

        return Math.Max(0, maxCount - Math.Max(0, remaining));
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
        string? FilterQualifiedItemId,
        bool FilterBlacklist,
        PoweredTransferQualityStrategy QualityStrategy,
        int MinSourceKeep,
        int TargetKeep,
        int ItemsPerOperation);
}
