using Koizumi.SVSAP.Api;
using Microsoft.Xna.Framework;
using SVSAPME.Content;
using SVSAPME.Models;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using SObject = StardewValley.Object;

namespace SVSAPME.Services;

internal sealed class SingleBlockFarmService
{
    private readonly MachineStateRepository repository;
    private readonly MachineRegistryService registry;
    private readonly EnergyNetworkManager energy;
    private readonly Func<ISvsapApi?> getSvsapApi;
    private readonly Func<ModConfig> getConfig;
    private readonly IInputHelper inputHelper;
    private readonly IMonitor monitor;
    private Func<SvsapmeMachineActionRequest, bool>? sendClientAction;

    public SingleBlockFarmService(
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

    public void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.activeClickableMenu is not null || !e.Button.IsActionButton())
            return;

        var location = Game1.currentLocation;
        var tile = e.Cursor.GrabTile;
        if (location is null || !location.Objects.TryGetValue(tile, out var placedObject) || !IsFarmMachine(placedObject.QualifiedItemId))
            return;

        var held = Game1.player.CurrentItem;
        if (held is null || held.Stack <= 0)
        {
            var tier = SingleBlockFarmRules.GetFarmTier(placedObject.QualifiedItemId);
            if (this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
                && TryReadMachineGuid(placedObject, out var statusGuid)
                && this.repository.TryGet(statusGuid, out var statusState))
            {
                Game1.addHUDMessage(new HUDMessage(FarmModuleRules.DescribeModules(statusState.Farm, tier), HUDMessage.newQuest_type));
            }
            else
            {
                Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.farm.holdSupportedItem", "Hold a supported seed, fertilizer, sprinkler, or farm module."), HUDMessage.error_type));
            }

            return;
        }

        if (!TryClassifyFarmAction(held.QualifiedItemId, out var actionKind, out var error))
        {
            Game1.addHUDMessage(new HUDMessage(error, HUDMessage.error_type));
            return;
        }

        if (!Context.IsMainPlayer)
        {
            this.TrySendClientAction(placedObject, actionKind, held.QualifiedItemId, Game1.player.FarmingLevel);
            this.Suppress(e);
            return;
        }

        var result = actionKind switch
        {
            SvsapmeMachineActionKind.LoadFarmSeed => this.TryLoadSeed(placedObject, location, tile, held.QualifiedItemId, Game1.player.FarmingLevel),
            SvsapmeMachineActionKind.LoadFarmFertilizer => this.TryLoadFertilizer(placedObject, location, tile, held.QualifiedItemId),
            SvsapmeMachineActionKind.InstallFarmModule => this.TryInstallModule(placedObject, location, tile, held.QualifiedItemId),
            _ => new SvsapmeMachineActionApplyResult(false, false, "Unsupported farm action.")
        };
        if (result.Success && result.ConsumeEscrowedItem)
            Game1.player.reduceActiveItemByOne();

        Game1.addHUDMessage(new HUDMessage(result.Message, result.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));
        this.Suppress(e);
    }

    private bool TrySendClientAction(SObject placedObject, SvsapmeMachineActionKind actionKind, string qualifiedItemId, int farmingLevel)
    {
        if (this.sendClientAction is null)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.multiplayerActionSenderNotReady", "SVSAPME multiplayer action sender is not ready."), HUDMessage.error_type));
            return false;
        }

        if (!TryReadMachineGuid(placedObject, out var machineGuid))
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.farmIdentityNotSynced", "SVSAPME farm identity has not synced from the host yet."), HUDMessage.error_type));
            return false;
        }

        return this.sendClientAction(new SvsapmeMachineActionRequest
        {
            TransactionId = Guid.NewGuid(),
            MachineGuid = machineGuid,
            ActionKind = actionKind,
            QualifiedItemId = qualifiedItemId,
            Count = 1,
            FarmingLevel = farmingLevel
        });
    }

    private static bool TryClassifyFarmAction(string qualifiedItemId, out SvsapmeMachineActionKind actionKind, out string message)
    {
        if (FarmCropCatalog.TryGetBySeed(qualifiedItemId, out _))
        {
            actionKind = SvsapmeMachineActionKind.LoadFarmSeed;
            message = string.Empty;
            return true;
        }

        if (FarmModuleRules.IsFertilizer(qualifiedItemId))
        {
            actionKind = SvsapmeMachineActionKind.LoadFarmFertilizer;
            message = string.Empty;
            return true;
        }

        if (FarmModuleRules.TryGetModule(qualifiedItemId, out _))
        {
            actionKind = SvsapmeMachineActionKind.InstallFarmModule;
            message = string.Empty;
            return true;
        }

        actionKind = SvsapmeMachineActionKind.None;
        message = "Hold a supported seed, fertilizer, sprinkler, or farm module.";
        return false;
    }

    internal SvsapmeMachineActionApplyResult TryLoadSeed(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        string seedQualifiedItemId,
        int placedByFarmingLevel)
    {
        if (!IsFarmMachine(placedObject.QualifiedItemId))
            return new(false, false, "Target machine is not a Single-Block Farm.");

        if (!FarmCropCatalog.TryGetBySeed(seedQualifiedItemId, out var crop))
            return new(false, false, "Hold a supported Data/Crops seed.");

        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return new(false, false, "SVSAPME could not register this farm.");
        }

        if (!SingleBlockFarmRules.CanBindSeed(state.Farm, seedQualifiedItemId))
            return new(false, false, "Clear the farm before changing its crop type.");

        SingleBlockFarmRules.BindSeed(state.Farm, crop, placedByFarmingLevel);
        state.Farm.InternalSeedCount++;
        this.repository.Save();
        return new(true, true, $"{crop.DisplayName} seed loaded. Internal seeds: {state.Farm.InternalSeedCount:N0}.");
    }

    internal SvsapmeMachineActionApplyResult TryLoadFertilizer(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        string fertilizerQualifiedItemId)
    {
        if (!IsFarmMachine(placedObject.QualifiedItemId))
            return new(false, false, "Target machine is not a Single-Block Farm.");

        if (!FarmModuleRules.IsFertilizer(fertilizerQualifiedItemId))
            return new(false, false, "Hold Basic, Quality, or Deluxe Fertilizer.");

        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return new(false, false, "SVSAPME could not register this farm.");
        }

        if (!FarmModuleRules.CanBindFertilizer(state.Farm, fertilizerQualifiedItemId))
            return new(false, false, "Clear the farm before changing its fertilizer type.");

        state.Farm.BoundFertilizerQualifiedItemId = fertilizerQualifiedItemId;
        state.Farm.InternalFertilizerCount++;
        this.repository.Save();
        return new(true, true, $"Fertilizer loaded. Internal fertilizer: {state.Farm.InternalFertilizerCount:N0}.");
    }

    internal SvsapmeMachineActionApplyResult TryInstallModule(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        string moduleQualifiedItemId)
    {
        if (!IsFarmMachine(placedObject.QualifiedItemId))
            return new(false, false, "Target machine is not a Single-Block Farm.");

        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return new(false, false, "SVSAPME could not register this farm.");
        }

        var tier = SingleBlockFarmRules.GetFarmTier(placedObject.QualifiedItemId);
        var result = FarmModuleRules.TryInstallModule(state.Farm, tier, moduleQualifiedItemId);
        if (result.Success)
            this.repository.Save();

        return new(result.Success, result.ConsumeHeldItem, result.Message);
    }

    public void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (!Context.IsMainPlayer || !Context.IsWorldReady || !this.getConfig().EnableSingleBlockFarm)
            return;

        var api = this.getSvsapApi();
        if (api is null)
            return;

        var changed = false;
        foreach (var machine in this.registry.MachinesByGuid.Values
            .Where(machine => IsFarmMachine(machine.QualifiedItemId))
            .OrderBy(machine => machine.MachineGuid)
            .ToList())
        {
            if (!this.repository.TryGet(machine.MachineGuid, out var state)
                || !FarmCropCatalog.TryGetBySeed(state.Farm.BoundSeedQualifiedItemId, out var crop)
                || !TryGetActiveEndpoint(api, machine, out var location, out var endpoint))
            {
                continue;
            }

            changed |= this.ProcessFarmDay(api, endpoint.NetworkId, location, machine, state, crop);
        }

        if (changed)
            this.repository.Save();
    }

    private bool ProcessFarmDay(ISvsapApi api, Guid networkId, GameLocation location, MachineLocation machine, MachineState state, FarmCropSpec crop)
    {
        var tier = SingleBlockFarmRules.GetFarmTier(machine.QualifiedItemId);
        var modules = SingleBlockFarmRules.GetModuleSnapshot(state.Farm);
        var availableNetworkSeeds = api.GetAvailableCount(networkId, crop.SeedQualifiedItemId, quality: null);
        var availableNetworkFertilizer = string.IsNullOrWhiteSpace(state.Farm.BoundFertilizerQualifiedItemId)
            ? 0
            : api.GetAvailableCount(networkId, state.Farm.BoundFertilizerQualifiedItemId, quality: null);
        var plan = SingleBlockFarmRules.PlanDay(
            state.Farm,
            tier,
            crop,
            modules,
            availableNetworkSeeds,
            availableNetworkFertilizer,
            location.GetSeason().ToString(),
            this.getConfig().FarmEnergyCostMultiplier);

        if (plan.RequiredWh > 0
            && !this.energy.TryConsumeWh(
                networkId,
                plan.RequiredWh,
                ModItemCatalog.UniqueId,
                "single-block-farm-day",
                allowPartial: false,
                out _,
                out _,
                out _))
        {
            SingleBlockFarmRules.ApplyFrozenDay();
            return false;
        }

        var extractedForRollback = new List<Item>();
        var networkSeedsNeeded = Math.Max(0, plan.PlannedSeedCount - Math.Max(0, state.Farm.InternalSeedCount));
        Item? extractedSeeds = null;
        if (networkSeedsNeeded > 0
            && (!api.TryExtractItem(networkId, crop.SeedQualifiedItemId, quality: null, networkSeedsNeeded, out extractedSeeds, out _, out _)
                || extractedSeeds is null
                || extractedSeeds.Stack < networkSeedsNeeded))
        {
            if (extractedSeeds is not null && extractedSeeds.Stack > 0)
                api.TryInsertItem(networkId, extractedSeeds, out _, out _, out _);
            this.RefundFarmEnergy(networkId, plan.RequiredWh);
            return false;
        }

        if (networkSeedsNeeded > 0)
            extractedForRollback.Add(extractedSeeds!);

        var networkFertilizerNeeded = Math.Max(0, plan.PlannedFertilizerCount - Math.Max(0, state.Farm.InternalFertilizerCount));
        Item? extractedFertilizer = null;
        if (networkFertilizerNeeded > 0
            && (!api.TryExtractItem(networkId, state.Farm.BoundFertilizerQualifiedItemId, quality: null, networkFertilizerNeeded, out extractedFertilizer, out _, out _)
                || extractedFertilizer is null
                || extractedFertilizer.Stack < networkFertilizerNeeded))
        {
            if (extractedFertilizer is not null && extractedFertilizer.Stack > 0)
                api.TryInsertItem(networkId, extractedFertilizer, out _, out _, out _);

            foreach (var rollbackItem in extractedForRollback)
                api.TryInsertItem(networkId, rollbackItem, out _, out _, out _);

            this.RefundFarmEnergy(networkId, plan.RequiredWh);
            return false;
        }

        var result = SingleBlockFarmRules.ApplyPaidDay(state.Farm, state.OutputBuffer, tier, crop, modules, plan);
        var changed = result.Outcome == FarmDailyOutcome.Applied
            && (plan.ChargedOccupiedPlots > 0 || result.PlantedFromInternal > 0 || result.PlantedFromNetwork > 0 || result.HarvestedPlots > 0);
        if (this.getConfig().EnableAutomaticFarmOutputToNetwork)
            changed |= this.FlushOutputBuffer(api, state, networkId);

        return changed;
    }

    private void RefundFarmEnergy(Guid networkId, long wh)
    {
        if (wh <= 0)
            return;

        if (!this.energy.TryDepositWh(
            networkId,
            wh,
            ModItemCatalog.UniqueId,
            "single-block-farm-seed-extract-refund",
            out var acceptedWh,
            out var code,
            out var message)
            || acceptedWh < wh)
        {
            this.monitor.Log($"Farm energy refund failed for {wh} Wh after seed extraction failure: {code} {message}", LogLevel.Error);
        }
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

    private static bool IsFarmMachine(string qualifiedItemId)
    {
        return qualifiedItemId is "(BC)" + ModItemCatalog.CopperFarm
            or "(BC)" + ModItemCatalog.SteelFarm
            or "(BC)" + ModItemCatalog.GoldFarm
            or "(BC)" + ModItemCatalog.IridiumFarm;
    }

    private static bool TryReadMachineGuid(SObject placedObject, out Guid machineGuid)
    {
        return Guid.TryParse(placedObject.modData.GetValueOrDefault(MachineRegistryService.MachineGuidKey), out machineGuid);
    }

    private void Suppress(ButtonPressedEventArgs e)
    {
        try
        {
            this.inputHelper.Suppress(e.Button);
        }
        catch (InvalidOperationException ex)
        {
            this.monitor.Log($"Could not suppress input after SVSAPME farm interaction: {ex.Message}", LogLevel.Trace);
        }
    }
}
