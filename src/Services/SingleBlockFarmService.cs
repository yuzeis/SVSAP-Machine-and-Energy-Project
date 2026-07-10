using Koizumi.SVSAP.Api;
using Microsoft.Xna.Framework;
using SVSAPME.Content;
using SVSAPME.Models;
using SVSAPME.UI;
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
    private Func<Guid, bool>? sendSnapshotRequest;

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

    public void SetSnapshotRequestSender(Func<Guid, bool> sender)
    {
        this.sendSnapshotRequest = sender;
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
            if (this.TryOpenFarmStatusMenu(placedObject, location, tile))
                this.Suppress(e);
            else
                Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.farm.holdSupportedItem", "Hold a supported seed, fertilizer, sprinkler, or farm module."), HUDMessage.error_type));

            return;
        }

        if (!TryClassifyFarmAction(held.QualifiedItemId, out var actionKind, out var error))
        {
            Game1.addHUDMessage(new HUDMessage(error, HUDMessage.error_type));
            this.Suppress(e);
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
            _ => new SvsapmeMachineActionApplyResult(false, false, ModText.Get("hud.farm.unsupportedAction", "Unsupported farm action."))
        };
        if (result.Success && result.ConsumeEscrowedItem)
            Game1.player.reduceActiveItemByOne();

        Game1.addHUDMessage(new HUDMessage(result.Message, result.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));
        this.Suppress(e);
    }

    internal bool TrySendClientAction(SObject placedObject, SvsapmeMachineActionKind actionKind, string qualifiedItemId, int farmingLevel)
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

    private bool TryOpenFarmStatusMenu(SObject placedObject, GameLocation location, Vector2 tile)
    {
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

        Game1.activeClickableMenu = new SingleBlockFarmMenu(placedObject, location, tile, this);
        return true;
    }

    public IReadOnlyList<FarmPlotView> GetPlotViews(SObject placedObject, GameLocation location, Vector2 tile)
    {
        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return Array.Empty<FarmPlotView>();
        }

        NormalizeLegacyFarmState(state.Farm);
        var tier = SingleBlockFarmRules.GetFarmTier(placedObject.QualifiedItemId);
        SingleBlockFarmRules.NormalizePlotIndices(state.Farm, tier.Plots);
        var views = new List<FarmPlotView>();
        for (var i = 0; i < tier.Plots; i++)
        {
            var plot = state.Farm.Plots.FirstOrDefault(candidate => candidate.PlotIndex == i);
            var lockedSeed = state.Farm.PlotLocks.GetValueOrDefault(i) ?? plot?.LockedSeedQualifiedItemId ?? string.Empty;
            if (plot is null)
            {
                views.Add(new FarmPlotView(i, string.Empty, string.Empty, string.Empty, 0, 0, false, string.Empty, !string.IsNullOrWhiteSpace(lockedSeed), lockedSeed));
                continue;
            }

            var seed = string.IsNullOrWhiteSpace(plot.SeedQualifiedItemId) ? state.Farm.BoundSeedQualifiedItemId : plot.SeedQualifiedItemId;
            if (!FarmCropCatalog.TryGetBySeed(seed, out var crop))
            {
                views.Add(new FarmPlotView(i, seed, string.Empty, ModText.Get("ui.farm.plot.unknown", "Unknown"), 0, 0, false, plot.FertilizerQualifiedItemId, !string.IsNullOrWhiteSpace(lockedSeed), lockedSeed));
                continue;
            }

            var baseDays = plot.InRegrow ? crop.RegrowDays : crop.BaseGrowthDays;
            var required = FarmGrowthRules.GetRequiredProgressUnits(baseDays);
            var ready = FarmGrowthRules.IsMature(plot.ProgressUnits, baseDays);
            views.Add(new FarmPlotView(
                i,
                crop.SeedQualifiedItemId,
                crop.HarvestQualifiedItemId,
                ready ? ModText.Get("ui.processor.slot.ready", "Ready") : FormatPlotEta(plot.ProgressUnits, required),
                Math.Min(plot.ProgressUnits, required),
                required,
                ready,
                plot.FertilizerQualifiedItemId,
                !string.IsNullOrWhiteSpace(lockedSeed),
                lockedSeed));
        }

        return views;
    }

    public FarmDashboardView GetDashboard(SObject placedObject, GameLocation location, Vector2 tile)
    {
        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return new FarmDashboardView(false, false, false, MachineInputModes.AllEligible, MachineFilterModes.Whitelist, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<string>(), 0m, 0L);
        }

        var tier = SingleBlockFarmRules.GetFarmTier(placedObject.QualifiedItemId);
        var occupied = SingleBlockFarmRules.CountOccupied(state.Farm);
        var moduleItems = FarmModuleRules.GetInstalledModuleItems(state.Farm).ToList();
        return new FarmDashboardView(
            true,
            state.Farm.AutoPullFromNetwork,
            state.Farm.AutoPushOutputToNetwork,
            state.Farm.InputMode,
            state.Farm.FilterMode,
            state.Farm.SeedFilterQualifiedItemIds.Count,
            state.Farm.InputBuffer.Count,
            state.Farm.InternalFertilizerCount,
            occupied,
            Math.Max(0, tier.Plots - occupied),
            state.OutputBuffer.Count,
            FarmModuleRules.GetUsedSlots(state.Farm),
            tier.ModuleSlots,
            moduleItems,
            EstimateFarmDailyValue(state.Farm),
            CalculateFarmRequiredWh(occupied, SingleBlockFarmRules.GetModuleSnapshot(state.Farm), this.getConfig().FarmEnergyCostMultiplier));
    }

    internal SvsapmeMachineActionApplyResult ToggleFarmAutoPull(SObject placedObject, GameLocation location, Vector2 tile)
    {
        return this.MutateFarmState(placedObject, location, tile, state =>
        {
            state.Farm.AutoPullFromNetwork = !state.Farm.AutoPullFromNetwork;
            return state.Farm.AutoPullFromNetwork
                ? ModText.Get("hud.farm.autoPullOn", "Farm network input enabled.")
                : ModText.Get("hud.farm.autoPullOff", "Farm network input disabled.");
        });
    }

    internal SvsapmeMachineActionApplyResult ToggleFarmAutoPush(SObject placedObject, GameLocation location, Vector2 tile)
    {
        return this.MutateFarmState(placedObject, location, tile, state =>
        {
            state.Farm.AutoPushOutputToNetwork = !state.Farm.AutoPushOutputToNetwork;
            return state.Farm.AutoPushOutputToNetwork
                ? ModText.Get("hud.farm.autoPushOn", "Farm network output enabled.")
                : ModText.Get("hud.farm.autoPushOff", "Farm network output disabled.");
        });
    }

    internal SvsapmeMachineActionApplyResult ToggleFarmInputMode(SObject placedObject, GameLocation location, Vector2 tile)
    {
        return this.MutateFarmState(placedObject, location, tile, state =>
        {
            state.Farm.InputMode = string.Equals(state.Farm.InputMode, MachineInputModes.Filter, StringComparison.Ordinal)
                ? MachineInputModes.AllEligible
                : MachineInputModes.Filter;
            return string.Equals(state.Farm.InputMode, MachineInputModes.Filter, StringComparison.Ordinal)
                ? ModText.Get("hud.farm.inputModeFilter", "Farm input mode: filter.")
                : ModText.Get("hud.farm.inputModeAll", "Farm input mode: all eligible.");
        });
    }

    internal SvsapmeMachineActionApplyResult ToggleFarmFilterMode(SObject placedObject, GameLocation location, Vector2 tile)
    {
        return this.MutateFarmState(placedObject, location, tile, state =>
        {
            state.Farm.FilterMode = string.Equals(state.Farm.FilterMode, MachineFilterModes.Blacklist, StringComparison.Ordinal)
                ? MachineFilterModes.Whitelist
                : MachineFilterModes.Blacklist;
            return string.Equals(state.Farm.FilterMode, MachineFilterModes.Blacklist, StringComparison.Ordinal)
                ? ModText.Get("hud.farm.filterModeBlacklist", "Farm filter mode: blacklist.")
                : ModText.Get("hud.farm.filterModeWhitelist", "Farm filter mode: whitelist.");
        });
    }

    internal SvsapmeMachineActionApplyResult AddHeldFarmFilter(SObject placedObject, GameLocation location, Vector2 tile, Item? held)
    {
        if (held is null || !FarmCropCatalog.TryGetBySeed(held.QualifiedItemId, out _))
            return new(false, false, ModText.Get("hud.farm.filterHoldSeed", "Hold a supported seed to add it to the farm filter."));

        return this.MutateFarmState(placedObject, location, tile, state =>
        {
            if (!state.Farm.SeedFilterQualifiedItemIds.Contains(held.QualifiedItemId, StringComparer.Ordinal))
                state.Farm.SeedFilterQualifiedItemIds.Add(held.QualifiedItemId);
            state.Farm.InputMode = MachineInputModes.Filter;
            return ModText.Get("hud.farm.filterAdded", "Farm filter added: {{item}}.", new { item = held.DisplayName });
        });
    }

    internal SvsapmeMachineActionApplyResult ClearFarmFilter(SObject placedObject, GameLocation location, Vector2 tile)
    {
        return this.MutateFarmState(placedObject, location, tile, state =>
        {
            state.Farm.SeedFilterQualifiedItemIds.Clear();
            return ModText.Get("hud.farm.filterCleared", "Farm filter cleared.");
        });
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

    private IReadOnlyList<string> BuildFarmStatusLines(SObject placedObject, GameLocation location, Vector2 tile, MachineState state)
    {
        var tier = SingleBlockFarmRules.GetFarmTier(placedObject.QualifiedItemId);
        var modules = SingleBlockFarmRules.GetModuleSnapshot(state.Farm);
        var occupied = SingleBlockFarmRules.CountOccupied(state.Farm);
        var lines = new List<string>
        {
            ModText.Get("ui.farm.name", "Farm: {{name}}", new { name = placedObject.DisplayName }),
            ModText.Get("ui.farm.location", "Location: {{location}} ({{x}}, {{y}})", new { location = location.NameOrUniqueName, x = tile.X.ToString("0"), y = tile.Y.ToString("0") }),
            ModText.Get("ui.farm.plots", "Plots: {{occupied}}/{{capacity}}", new { occupied = occupied.ToString("N0"), capacity = tier.Plots.ToString("N0") }),
            ModText.Get("ui.farm.moduleSlots", "Module slots: {{used}}/{{capacity}}", new { used = FarmModuleRules.GetUsedSlots(state.Farm).ToString("N0"), capacity = tier.ModuleSlots.ToString("N0") }),
            ModText.Get("ui.farm.seedsStored", "Seeds stored: {{count}}", new { count = CountSeedInputs(state.Farm).ToString("N0") }),
            ModText.Get("ui.farm.fertilizerStored", "Fertilizer stored: {{count}}", new { count = state.Farm.InternalFertilizerCount.ToString("N0") }),
            ModText.Get("ui.farm.modules", "Modules: {{modules}}", new { modules = FormatModules(state.Farm) }),
            ModText.Get("ui.farm.sprinklerCoverage", "Sprinkler coverage: {{covered}}/{{capacity}}", new { covered = Math.Min(state.Farm.SprinklerCoveredPlots, tier.Plots).ToString("N0"), capacity = tier.Plots.ToString("N0") }),
            ModText.Get("ui.farm.moduleFactors", "Light factor: {{light}}; thermostat: {{thermostat}}; slow release: {{slowRelease}} plot-cycles/fertilizer", new { light = (state.Farm.LightFactorPermille / 1000m).ToString("0.###"), thermostat = (state.Farm.HasThermostat ? state.Farm.ThermostatFactorPermille / 1000m : 0).ToString("0.###"), slowRelease = state.Farm.SlowReleaseCoveragePerFertilizer.ToString("N0") })
        };

        if (FarmCropCatalog.TryGetBySeed(state.Farm.BoundSeedQualifiedItemId, out var crop))
        {
            lines.Add(ModText.Get("ui.farm.crop", "Crop: {{crop}} ({{seed}} -> {{harvest}})", new { crop = crop.DisplayName, seed = FormatItem(crop.SeedQualifiedItemId), harvest = FormatItem(crop.HarvestQualifiedItemId) }));
            lines.Add(ModText.Get("ui.farm.seasons", "Seasons: {{seasons}}; growth {{growth}} day(s); regrow {{regrow}}", new { seasons = string.Join(", ", crop.Seasons), growth = crop.BaseGrowthDays.ToString("N0"), regrow = crop.RegrowDays.ToString("N0") }));
            lines.Add(ModText.Get("ui.farm.progress", "Progress: {{progress}}", new { progress = FormatFarmProgress(state.Farm, crop) }));
        }
        else
        {
            lines.Add(ModText.Get("ui.farm.cropNone", "Crop: none bound"));
        }

        lines.Add(string.IsNullOrWhiteSpace(state.Farm.BoundFertilizerQualifiedItemId)
            ? ModText.Get("ui.farm.fertilizerNone", "Fertilizer: none bound")
            : ModText.Get("ui.farm.fertilizer", "Fertilizer: {{item}}", new { item = FormatItem(state.Farm.BoundFertilizerQualifiedItemId) }));

        if (state.OutputBuffer.Count > 0)
            lines.Add(ModText.Get("ui.farm.outputBuffer", "Output buffer: {{items}}", new { items = FormatBufferedItems(state.OutputBuffer) }));

        var api = this.getSvsapApi();
        if (api is not null
            && TryReadMachineGuid(placedObject, out var machineGuid)
            && TryGetActiveEndpoint(api, new MachineLocation(machineGuid, location.NameOrUniqueName, tile, placedObject.QualifiedItemId), out _, out var endpoint))
        {
            lines.Add(ModText.Get("ui.farm.network", "Network: {{network}}", new { network = endpoint.NetworkId.ToString("N") }));
            if (FarmCropCatalog.TryGetBySeed(state.Farm.BoundSeedQualifiedItemId, out var plannedCrop))
            {
                var availableNetworkSeeds = api.GetAvailableCount(endpoint.NetworkId, plannedCrop.SeedQualifiedItemId, quality: null);
                var availableNetworkFertilizer = string.IsNullOrWhiteSpace(state.Farm.BoundFertilizerQualifiedItemId)
                    ? 0
                    : api.GetAvailableCount(endpoint.NetworkId, state.Farm.BoundFertilizerQualifiedItemId, quality: null);
                var plan = SingleBlockFarmRules.PlanDay(
                    state.Farm,
                    tier,
                    plannedCrop,
                    modules,
                    availableNetworkSeeds,
                    availableNetworkFertilizer,
                    location.GetSeason().ToString(),
                    this.getConfig().FarmEnergyCostMultiplier);
                lines.Add(ModText.Get(
                    "ui.farm.nextPlan",
                    "Next daily plan: plant {{plant}}, fertilizer {{fertilizer}}, charged plots {{charged}}, cost {{cost}}, can grow today={{canGrow}}",
                    new
                    {
                        plant = plan.PlannedSeedCount.ToString("N0"),
                        fertilizer = plan.PlannedFertilizerCount.ToString("N0"),
                        charged = plan.ChargedOccupiedPlots.ToString("N0"),
                        cost = FormatWh(plan.RequiredWh),
                        canGrow = FormatBool(plan.CanGrowToday)
                    }));
            }
        }
        else
        {
            lines.Add(api is null
                ? ModText.Get("ui.farm.network.apiUnavailable", "Network: SVSAP API unavailable")
                : ModText.Get("ui.farm.network.notLinked", "Network: not linked or inactive"));
        }

        return lines;
    }

    private SvsapmeMachineActionApplyResult MutateFarmState(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        Func<MachineState, string> mutate)
    {
        if (!IsFarmMachine(placedObject.QualifiedItemId))
            return new(false, false, ModText.Get("hud.farm.notFarm", "Target machine is not a Single-Block Farm."));

        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return new(false, false, ModText.Get("hud.farm.registerFailed", "SVSAPME could not register this farm."));
        }

        var message = mutate(state);
        this.repository.Save();
        return new(true, false, message);
    }

    private static string FormatPlotEta(long progressUnits, long requiredUnits)
    {
        if (requiredUnits <= 0)
            return "0%";

        var percent = Math.Clamp(progressUnits / (decimal)requiredUnits, 0, 1);
        return percent.ToString("P0");
    }

    private static decimal EstimateFarmDailyValue(FarmMachineState farm)
    {
        decimal value = 0;
        foreach (var plot in farm.Plots)
        {
            var seed = string.IsNullOrWhiteSpace(plot.SeedQualifiedItemId) ? farm.BoundSeedQualifiedItemId : plot.SeedQualifiedItemId;
            if (!FarmCropCatalog.TryGetBySeed(seed, out var crop))
                continue;

            try
            {
                var item = ItemRegistry.Create(crop.HarvestQualifiedItemId);
                var price = Math.Max(0, item.salePrice(false));
                var cycle = plot.InRegrow && crop.RegrowDays > 0 ? crop.RegrowDays : crop.BaseGrowthDays;
                value += price * Math.Max(1, crop.HarvestMinStack) / (decimal)Math.Max(1, cycle);
            }
            catch
            {
                // Ignore custom harvests that cannot be materialized outside gameplay content.
            }
        }

        return value;
    }

    private static string FormatFarmProgress(FarmMachineState farm, FarmCropSpec crop)
    {
        if (farm.Plots.Count == 0)
            return ModText.Get("ui.farm.progress.empty", "no occupied plots");

        var mature = 0;
        decimal total = 0;
        foreach (var plot in farm.Plots)
        {
            var baseDays = plot.InRegrow ? crop.RegrowDays : crop.BaseGrowthDays;
            var required = FarmGrowthRules.GetRequiredProgressUnits(baseDays);
            if (FarmGrowthRules.IsMature(plot.ProgressUnits, baseDays))
                mature++;
            total += required <= 0 ? 0 : Math.Min(1m, plot.ProgressUnits / (decimal)required);
        }

        return ModText.Get(
            "ui.farm.progress.value",
            "{{mature}} mature, average {{average}}",
            new { mature = mature.ToString("N0"), average = (total / farm.Plots.Count).ToString("P0") });
    }

    private static string FormatModules(FarmMachineState farm)
    {
        var modules = FarmModuleRules.GetInstalledModuleItems(farm)
            .Select(FormatItem)
            .ToList();
        return modules.Count == 0 ? ModText.Get("ui.common.none", "none") : string.Join(", ", modules);
    }

    private static string FormatBufferedItems(IEnumerable<BufferedItemStack> stacks)
    {
        var parts = stacks
            .Where(stack => stack.Stack > 0)
            .Select(stack => $"{FormatItem(stack.QualifiedItemId)} x{stack.Stack:N0}")
            .ToList();
        return parts.Count == 0 ? ModText.Get("ui.common.empty", "empty") : string.Join(", ", parts);
    }

    private static string FormatItem(string qualifiedItemId)
    {
        if (string.IsNullOrWhiteSpace(qualifiedItemId))
            return ModText.Get("ui.common.none", "none");

        try
        {
            return ItemRegistry.Create(qualifiedItemId).DisplayName;
        }
        catch
        {
            return qualifiedItemId;
        }
    }

    private static string FormatWh(long wh)
    {
        return $"{Math.Max(0, wh) / 1000m:0.00} kWh";
    }

    private static string FormatBool(bool value)
    {
        return value
            ? ModText.Get("ui.common.yes", "yes")
            : ModText.Get("ui.common.no", "no");
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
        message = ModText.Get("hud.farm.holdSupportedItem", "Hold a supported seed, fertilizer, sprinkler, or farm module.");
        return false;
    }

    internal SvsapmeMachineActionApplyResult TryLoadSeed(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        string seedQualifiedItemId,
        int placedByFarmingLevel,
        int count = 1)
    {
        if (!IsFarmMachine(placedObject.QualifiedItemId))
            return new(false, false, ModText.Get("hud.farm.notFarm", "Target machine is not a Single-Block Farm."));

        if (!FarmCropCatalog.TryGetBySeed(seedQualifiedItemId, out var crop))
            return new(false, false, ModText.Get("hud.farm.holdSeed", "Hold a supported Data/Crops seed."));

        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return new(false, false, ModText.Get("hud.farm.registerFailed", "SVSAPME could not register this farm."));
        }

        SingleBlockFarmRules.BindSeed(state.Farm, crop, placedByFarmingLevel);
        var seed = ItemRegistry.Create(seedQualifiedItemId);
        seed.Stack = Math.Clamp(count, 1, 999);
        AddBufferedInput(state.Farm.InputBuffer, seed);
        this.repository.Save();
        return new(true, true, ModText.Get("hud.farm.seedLoaded", "{{crop}} seed loaded. Internal seeds: {{count}}.", new { crop = crop.DisplayName, count = CountSeedInputs(state.Farm).ToString("N0") }));
    }

    internal SvsapmeMachineActionApplyResult TryLoadFertilizer(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        string fertilizerQualifiedItemId,
        int count = 1)
    {
        if (!IsFarmMachine(placedObject.QualifiedItemId))
            return new(false, false, ModText.Get("hud.farm.notFarm", "Target machine is not a Single-Block Farm."));

        if (!FarmModuleRules.IsFertilizer(fertilizerQualifiedItemId))
            return new(false, false, ModText.Get("hud.farm.holdFertilizer", "Hold Basic, Quality, or Deluxe Fertilizer."));

        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return new(false, false, ModText.Get("hud.farm.registerFailed", "SVSAPME could not register this farm."));
        }

        if (!FarmModuleRules.CanBindFertilizer(state.Farm, fertilizerQualifiedItemId))
            return new(false, false, ModText.Get("hud.farm.clearBeforeFertilizerChange", "Clear the farm before changing its fertilizer type."));

        state.Farm.BoundFertilizerQualifiedItemId = fertilizerQualifiedItemId;
        state.Farm.InternalFertilizerCount += Math.Clamp(count, 1, 999);
        this.repository.Save();
        return new(true, true, ModText.Get("hud.farm.fertilizerLoaded", "Fertilizer loaded. Internal fertilizer: {{count}}.", new { count = state.Farm.InternalFertilizerCount.ToString("N0") }));
    }

    internal SvsapmeMachineActionApplyResult TryInstallModule(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        string moduleQualifiedItemId)
    {
        if (!IsFarmMachine(placedObject.QualifiedItemId))
            return new(false, false, ModText.Get("hud.farm.notFarm", "Target machine is not a Single-Block Farm."));

        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return new(false, false, ModText.Get("hud.farm.registerFailed", "SVSAPME could not register this farm."));
        }

        var tier = SingleBlockFarmRules.GetFarmTier(placedObject.QualifiedItemId);
        var result = FarmModuleRules.TryInstallModule(state.Farm, tier, moduleQualifiedItemId);
        if (result.Success)
            this.repository.Save();

        return new(result.Success, result.ConsumeHeldItem, result.Message);
    }

    internal SvsapmeMachineActionApplyResult TryManualPlant(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        Item seedItem,
        int farmingLevel)
    {
        var tier = SingleBlockFarmRules.GetFarmTier(placedObject.QualifiedItemId);
        var plotIndex = 0;
        if (this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            && TryReadMachineGuid(placedObject, out var machineGuid)
            && this.repository.TryGet(machineGuid, out var state))
        {
            SingleBlockFarmRules.NormalizePlotIndices(state.Farm, tier.Plots);
            var occupied = state.Farm.Plots.Select(plot => plot.PlotIndex).ToHashSet();
            plotIndex = Enumerable.Range(0, tier.Plots).FirstOrDefault(index => !occupied.Contains(index));
        }

        return this.TryManualPlant(placedObject, location, tile, plotIndex, seedItem, farmingLevel);
    }

    internal SvsapmeMachineActionApplyResult TryManualPlant(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        int plotIndex,
        Item seedItem,
        int farmingLevel)
    {
        if (!IsFarmMachine(placedObject.QualifiedItemId))
            return new(false, false, ModText.Get("hud.farm.notFarm", "Target machine is not a Single-Block Farm."));

        if (!FarmCropCatalog.TryGetBySeed(seedItem.QualifiedItemId, out var crop))
            return new(false, false, ModText.Get("hud.farm.holdSeed", "Hold a supported Data/Crops seed."));

        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return new(false, false, ModText.Get("hud.farm.registerFailed", "SVSAPME could not register this farm."));
        }

        var tier = SingleBlockFarmRules.GetFarmTier(placedObject.QualifiedItemId);
        SingleBlockFarmRules.NormalizePlotIndices(state.Farm, tier.Plots);
        if (plotIndex < 0 || plotIndex >= tier.Plots)
            return new(false, false, ModText.Get("hud.farm.invalidPlot", "That farm plot does not exist."));
        if (state.Farm.Plots.Any(plot => plot.PlotIndex == plotIndex))
            return new(false, false, ModText.Get("hud.farm.plotOccupied", "That farm plot is already occupied."));

        var lockedSeed = state.Farm.PlotLocks.GetValueOrDefault(plotIndex) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(lockedSeed)
            && !string.Equals(lockedSeed, crop.SeedQualifiedItemId, StringComparison.Ordinal))
        {
            return new(false, false, ModText.Get("hud.farm.plotLockedToSeed", "That plot is locked to a different seed."));
        }

        state.Farm.Plots.Add(new FarmPlotState
        {
            PlotIndex = plotIndex,
            SeedQualifiedItemId = crop.SeedQualifiedItemId,
            HarvestQualifiedItemId = crop.HarvestQualifiedItemId,
            PlacedByFarmingLevel = farmingLevel,
            FertilizerQualifiedItemId = state.Farm.BoundFertilizerQualifiedItemId,
            ProgressUnits = 0,
            InRegrow = false,
            IsLocked = !string.IsNullOrWhiteSpace(lockedSeed),
            LockedSeedQualifiedItemId = lockedSeed
        });

        this.repository.Save();
        return new(true, true, ModText.Get("hud.farm.seedLoaded", "{{crop}} seed loaded. Internal seeds: {{count}}.", new { crop = crop.DisplayName, count = state.Farm.Plots.Count.ToString("N0") }));
    }

    internal SvsapmeMachineActionApplyResult TryRemoveModule(SObject placedObject, GameLocation location, Vector2 tile, int index)
    {
        if (!IsFarmMachine(placedObject.QualifiedItemId))
            return new(false, false, ModText.Get("hud.farm.notFarm", "Target machine is not a Single-Block Farm."));

        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return new(false, false, ModText.Get("hud.farm.registerFailed", "SVSAPME could not register this farm."));
        }

        if (index < 0 || index >= state.Farm.InstalledModuleQualifiedItemIds.Count)
            return new(false, false, ModText.Get("hud.farm.invalidModuleSlot", "That farm module slot does not exist."));

        var removedId = state.Farm.InstalledModuleQualifiedItemIds[index];
        state.Farm.InstalledModuleQualifiedItemIds.RemoveAt(index);
        FarmModuleRules.RecalculateModuleSnapshot(state.Farm);
        this.repository.Save();

        var item = ItemRegistry.Create(removedId);
        return new SvsapmeMachineActionApplyResult(
            true,
            false,
            ModText.Get("hud.farm.moduleRemoved", "Removed module: {{item}}.", new { item = item.DisplayName }))
        {
            ReturnedItems = new List<BufferedItemStack> { BufferedItemCodec.FromItem(item) }
        };
    }

    internal SvsapmeMachineActionApplyResult TryHarvestPlot(SObject placedObject, GameLocation location, Vector2 tile, int plotIndex)
    {
        if (!this.TryGetFarmState(placedObject, location, tile, out var state, out var failure))
            return new(false, false, failure);

        var tier = SingleBlockFarmRules.GetFarmTier(placedObject.QualifiedItemId);
        SingleBlockFarmRules.NormalizePlotIndices(state.Farm, tier.Plots);
        var plot = state.Farm.Plots.FirstOrDefault(candidate => candidate.PlotIndex == plotIndex);
        if (plot is null || !FarmCropCatalog.TryGetBySeed(plot.SeedQualifiedItemId, out var crop))
            return new(false, false, ModText.Get("hud.farm.plotEmpty", "That farm plot is empty."));

        var required = FarmGrowthRules.GetRequiredProgressUnits(plot.InRegrow ? crop.RegrowDays : crop.BaseGrowthDays);
        if (plot.ProgressUnits < required)
            return new(false, false, ModText.Get("hud.farm.plotNotReady", "That crop is not ready to harvest."));

        var returned = new List<BufferedItemStack>();
        SingleBlockFarmRules.AddHarvestOutput(returned, state.Farm, crop, plot);
        if (crop.RegrowDays > 0)
        {
            plot.ProgressUnits = 0;
            plot.InRegrow = true;
        }
        else
        {
            if (plot.IsLocked && !string.IsNullOrWhiteSpace(plot.LockedSeedQualifiedItemId))
                state.Farm.PlotLocks[plot.PlotIndex] = plot.LockedSeedQualifiedItemId;
            state.Farm.Plots.Remove(plot);
        }

        this.repository.Save();
        return new SvsapmeMachineActionApplyResult(true, false, ModText.Get("hud.farm.plotHarvested", "Crop harvested."))
        {
            ReturnedItems = returned
        };
    }

    internal SvsapmeMachineActionApplyResult ToggleFarmPlotLock(SObject placedObject, GameLocation location, Vector2 tile, int plotIndex, string seedQualifiedItemId = "")
    {
        if (!this.TryGetFarmState(placedObject, location, tile, out var state, out var failure))
            return new(false, false, failure);

        var tier = SingleBlockFarmRules.GetFarmTier(placedObject.QualifiedItemId);
        if (plotIndex < 0 || plotIndex >= tier.Plots)
            return new(false, false, ModText.Get("hud.farm.invalidPlot", "That farm plot does not exist."));

        SingleBlockFarmRules.NormalizePlotIndices(state.Farm, tier.Plots);
        var plot = state.Farm.Plots.FirstOrDefault(candidate => candidate.PlotIndex == plotIndex);
        var existing = state.Farm.PlotLocks.GetValueOrDefault(plotIndex) ?? plot?.LockedSeedQualifiedItemId ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(existing))
        {
            state.Farm.PlotLocks.Remove(plotIndex);
            if (plot is not null)
            {
                plot.IsLocked = false;
                plot.LockedSeedQualifiedItemId = string.Empty;
            }
            this.repository.Save();
            return new(true, false, ModText.Get("hud.farm.plotUnlocked", "Farm plot unlocked."));
        }

        var targetSeed = !string.IsNullOrWhiteSpace(seedQualifiedItemId) ? seedQualifiedItemId : plot?.SeedQualifiedItemId ?? string.Empty;
        if (!FarmCropCatalog.TryGetBySeed(targetSeed, out _))
            return new(false, false, ModText.Get("hud.farm.lockNeedsSeed", "Plant the plot or hold a supported seed before locking it."));

        state.Farm.PlotLocks[plotIndex] = targetSeed;
        if (plot is not null)
        {
            plot.IsLocked = true;
            plot.LockedSeedQualifiedItemId = targetSeed;
        }
        this.repository.Save();
        return new(true, false, ModText.Get("hud.farm.plotLocked", "Farm plot locked to its current crop."));
    }

    internal SvsapmeMachineActionApplyResult TryCollectFarmOutput(SObject placedObject, GameLocation location, Vector2 tile)
    {
        if (!this.TryGetFarmState(placedObject, location, tile, out var state, out var failure))
            return new(false, false, failure);
        if (state.OutputBuffer.Count == 0)
            return new(false, false, ModText.Get("hud.farm.outputEmpty", "The farm output buffer is empty."));

        var returned = state.OutputBuffer.ToList();
        state.OutputBuffer.Clear();
        this.repository.Save();
        return new SvsapmeMachineActionApplyResult(true, false, ModText.Get("hud.farm.outputCollected", "Collected {{count}} farm output stack(s).", new { count = returned.Count.ToString("N0") }))
        {
            ReturnedItems = returned
        };
    }

    internal SvsapmeMachineActionApplyResult TryExtractFarmInput(SObject placedObject, GameLocation location, Vector2 tile, bool fertilizer)
    {
        if (!this.TryGetFarmState(placedObject, location, tile, out var state, out var failure))
            return new(false, false, failure);

        BufferedItemStack returned;
        if (fertilizer)
        {
            if (state.Farm.InternalFertilizerCount <= 0 || string.IsNullOrWhiteSpace(state.Farm.BoundFertilizerQualifiedItemId))
                return new(false, false, ModText.Get("hud.farm.inputEmpty", "That farm input is empty."));
            returned = new BufferedItemStack { QualifiedItemId = state.Farm.BoundFertilizerQualifiedItemId, Stack = state.Farm.InternalFertilizerCount };
            state.Farm.InternalFertilizerCount = 0;
            state.Farm.BoundFertilizerQualifiedItemId = string.Empty;
        }
        else
        {
            if (state.Farm.InputBuffer.Count == 0)
                return new(false, false, ModText.Get("hud.farm.inputEmpty", "That farm input is empty."));
            returned = state.Farm.InputBuffer[0];
            state.Farm.InputBuffer.RemoveAt(0);
        }

        this.repository.Save();
        return new SvsapmeMachineActionApplyResult(true, false, ModText.Get("hud.farm.inputExtracted", "Farm input removed."))
        {
            ReturnedItems = new List<BufferedItemStack> { returned }
        };
    }

    private bool TryGetFarmState(SObject placedObject, GameLocation location, Vector2 tile, out MachineState state, out string failure)
    {
        state = null!;
        if (!IsFarmMachine(placedObject.QualifiedItemId))
        {
            failure = ModText.Get("hud.farm.notFarm", "Target machine is not a Single-Block Farm.");
            return false;
        }
        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out state))
        {
            failure = ModText.Get("hud.farm.registerFailed", "SVSAPME could not register this farm.");
            return false;
        }
        failure = string.Empty;
        return true;
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
                || !TryGetActiveEndpoint(api, machine, out var location, out var endpoint))
            {
                continue;
            }

            changed |= this.ProcessFarmDay(api, endpoint.NetworkId, location, machine, state);
        }

        if (changed)
            this.repository.Save();
    }

    private bool ProcessFarmDay(ISvsapApi api, Guid networkId, GameLocation location, MachineLocation machine, MachineState state)
    {
        var tier = SingleBlockFarmRules.GetFarmTier(machine.QualifiedItemId);
        var modules = SingleBlockFarmRules.GetModuleSnapshot(state.Farm);
        var season = location.GetSeason().ToString();
        NormalizeLegacyFarmState(state.Farm);
        SingleBlockFarmRules.NormalizePlotIndices(state.Farm, tier.Plots);

        var planned = this.PlanMixedFarmPlanting(api, networkId, state.Farm, tier, modules, season);
        var chargedOccupied = Math.Min(tier.Plots, state.Farm.Plots.Count + planned.Count);
        var requiredWh = CalculateFarmRequiredWh(chargedOccupied, modules, this.getConfig().FarmEnergyCostMultiplier);
        if (requiredWh > 0
            && !this.energy.TryConsumeWh(
                networkId,
                requiredWh,
                ModItemCatalog.UniqueId,
                BuildMachineEnergyReason(state, "single-block-farm-day"),
                allowPartial: false,
                out _,
                out _,
                out _))
        {
            this.RollbackMixedFarmPlanting(api, networkId, state.Farm, planned);
            SingleBlockFarmRules.ApplyFrozenDay();
            return false;
        }

        AssignPlannedFertilizer(api, networkId, state.Farm, modules, planned);
        var changed = false;
        foreach (var seed in planned)
        {
            if (!TryApplyPlannedFarmSeed(state.Farm, tier, seed))
            {
                this.RollbackPlannedSeed(api, networkId, state.Farm, seed);
                continue;
            }
            changed = true;
        }

        changed |= ApplyMixedFarmGrowth(state.Farm, state.OutputBuffer, modules, season);
        if (this.getConfig().EnableAutomaticFarmOutputToNetwork && state.Farm.AutoPushOutputToNetwork)
        {
            changed |= this.FlushOutputBuffer(api, state, networkId);
        }

        return changed;
    }

    private List<PlannedFarmSeed> PlanMixedFarmPlanting(
        ISvsapApi api,
        Guid networkId,
        FarmMachineState farm,
        FarmTierInfo tier,
        FarmModuleSnapshot modules,
        string season)
    {
        var planned = new List<PlannedFarmSeed>();
        foreach (var plotIndex in GetMixedFarmPlantingOrder(farm, tier.Plots))
        {
            var requiredSeed = farm.PlotLocks.GetValueOrDefault(plotIndex) ?? string.Empty;
            if (TryTakeBufferedSeed(farm, modules, season, requiredSeed, out var seedItem, out var crop))
            {
                planned.Add(new PlannedFarmSeed(seedItem, crop, FarmSeedSource.InputBuffer, farm.PlacedByFarmingLevel, plotIndex));
                continue;
            }

            if (farm.AutoPullFromNetwork
                && TryExtractNetworkSeed(api, networkId, farm, modules, season, requiredSeed, out seedItem, out crop))
            {
                planned.Add(new PlannedFarmSeed(seedItem, crop, FarmSeedSource.Network, farm.PlacedByFarmingLevel, plotIndex));
                continue;
            }

            if (string.IsNullOrWhiteSpace(requiredSeed))
                break;
        }

        return planned;
    }

    internal static IReadOnlyList<int> GetMixedFarmPlantingOrder(FarmMachineState farm, int capacity)
    {
        var occupied = farm.Plots.Select(plot => plot.PlotIndex).ToHashSet();
        return Enumerable.Range(0, Math.Max(0, capacity))
            .Where(index => !occupied.Contains(index))
            .OrderByDescending(index => farm.PlotLocks.ContainsKey(index))
            .ThenBy(index => index)
            .ToList();
    }

    internal static bool TryApplyPlannedFarmSeed(FarmMachineState farm, FarmTierInfo tier, PlannedFarmSeed seed)
    {
        if (farm.Plots.Count >= tier.Plots
            || seed.TargetPlotIndex < 0
            || seed.TargetPlotIndex >= tier.Plots
            || farm.Plots.Any(plot => plot.PlotIndex == seed.TargetPlotIndex))
        {
            return false;
        }

        farm.Plots.Add(new FarmPlotState
        {
            PlotIndex = seed.TargetPlotIndex,
            SeedQualifiedItemId = seed.Crop.SeedQualifiedItemId,
            HarvestQualifiedItemId = seed.Crop.HarvestQualifiedItemId,
            PlacedByFarmingLevel = seed.FarmingLevel,
            FertilizerQualifiedItemId = seed.FertilizerQualifiedItemId,
            IsLocked = farm.PlotLocks.ContainsKey(seed.TargetPlotIndex),
            LockedSeedQualifiedItemId = farm.PlotLocks.GetValueOrDefault(seed.TargetPlotIndex) ?? string.Empty
        });
        return true;
    }

    private static void AssignPlannedFertilizer(
        ISvsapApi api,
        Guid networkId,
        FarmMachineState farm,
        FarmModuleSnapshot modules,
        IReadOnlyList<PlannedFarmSeed> planned)
    {
        if (planned.Count == 0 || string.IsNullOrWhiteSpace(farm.BoundFertilizerQualifiedItemId))
            return;

        var unitsNeeded = FarmModuleRules.CalculateFertilizerUnitsForPlots(planned.Count, modules.FertilizerCoveragePerFertilizer);
        var internalUnits = Math.Min(Math.Max(0, farm.InternalFertilizerCount), unitsNeeded);
        farm.InternalFertilizerCount -= internalUnits;
        var totalUnits = internalUnits;
        var networkNeeded = unitsNeeded - internalUnits;
        if (networkNeeded > 0
            && farm.AutoPullFromNetwork
            && api.TryExtractItem(networkId, farm.BoundFertilizerQualifiedItemId, quality: null, networkNeeded, out var extracted, out _, out _)
            && extracted is not null)
        {
            totalUnits += Math.Min(networkNeeded, extracted.Stack);
            if (extracted.Stack > networkNeeded)
            {
                extracted.Stack -= networkNeeded;
                api.TryInsertItem(networkId, extracted, out _, out _, out _);
            }
        }

        var coverage = Math.Min(planned.Count, totalUnits * Math.Max(1, modules.FertilizerCoveragePerFertilizer));
        for (var i = 0; i < coverage; i++)
            planned[i].FertilizerQualifiedItemId = farm.BoundFertilizerQualifiedItemId;
    }

    private void RollbackMixedFarmPlanting(ISvsapApi api, Guid networkId, FarmMachineState farm, IEnumerable<PlannedFarmSeed> planned)
    {
        foreach (var seed in planned)
            this.RollbackPlannedSeed(api, networkId, farm, seed);
    }

    private void RollbackPlannedSeed(ISvsapApi api, Guid networkId, FarmMachineState farm, PlannedFarmSeed seed)
    {
        if (seed.Source == FarmSeedSource.Network)
        {
            api.TryInsertItem(networkId, seed.SeedItem, out _, out _, out _);
            return;
        }

        AddBufferedInput(farm.InputBuffer, seed.SeedItem);
    }

    internal static bool TryTakeBufferedSeed(
        FarmMachineState farm,
        FarmModuleSnapshot modules,
        string season,
        string requiredSeedQualifiedItemId,
        out Item seedItem,
        out FarmCropSpec crop)
    {
        seedItem = null!;
        crop = null!;
        for (var i = 0; i < farm.InputBuffer.Count; i++)
        {
            var item = BufferedItemCodec.CreateItem(farm.InputBuffer[i]);
            if (item.Stack <= 0
                || !FarmCropCatalog.TryGetBySeed(item.QualifiedItemId, out crop!)
                || (!string.IsNullOrWhiteSpace(requiredSeedQualifiedItemId)
                    && !string.Equals(requiredSeedQualifiedItemId, item.QualifiedItemId, StringComparison.Ordinal))
                || !CanFarmCropGrowToday(crop, modules, season)
                || !MatchesFarmSeedFilter(farm, item))
            {
                continue;
            }

            seedItem = item.getOne();
            seedItem.Stack = 1;
            farm.InputBuffer[i].Stack--;
            if (farm.InputBuffer[i].Stack <= 0)
                farm.InputBuffer.RemoveAt(i);
            return true;
        }

        return false;
    }

    private static bool TryExtractNetworkSeed(
        ISvsapApi api,
        Guid networkId,
        FarmMachineState farm,
        FarmModuleSnapshot modules,
        string season,
        string requiredSeedQualifiedItemId,
        out Item seedItem,
        out FarmCropSpec crop)
    {
        seedItem = null!;
        crop = null!;
        if (!api.TryExtractFirstMatchingItem(
                networkId,
                item => FarmCropCatalog.TryGetBySeed(item.QualifiedItemId, out var candidate)
                    && (string.IsNullOrWhiteSpace(requiredSeedQualifiedItemId)
                        || string.Equals(requiredSeedQualifiedItemId, item.QualifiedItemId, StringComparison.Ordinal))
                    && CanFarmCropGrowToday(candidate, modules, season)
                    && MatchesFarmSeedFilter(farm, item),
                _ => 1,
                highQualityFirst: false,
                preserveGoldIridium: false,
                out var extracted,
                out _,
                out _)
            || extracted is null
            || extracted.Stack <= 0
            || !FarmCropCatalog.TryGetBySeed(extracted.QualifiedItemId, out crop!))
        {
            return false;
        }

        seedItem = extracted;
        seedItem.Stack = 1;
        return true;
    }

    internal static bool ApplyMixedFarmGrowth(
        FarmMachineState farm,
        IList<BufferedItemStack> outputBuffer,
        FarmModuleSnapshot modules,
        string season)
    {
        var changed = false;
        var dailyProgress = FarmGrowthRules.GetDailyProgressUnits(modules.LightFactorProduct, modules.HasThermostat ? modules.ThermostatFactor : 1.0m);
        foreach (var plot in farm.Plots.ToList())
        {
            var seedQualifiedItemId = string.IsNullOrWhiteSpace(plot.SeedQualifiedItemId)
                ? farm.BoundSeedQualifiedItemId
                : plot.SeedQualifiedItemId;
            if (!FarmCropCatalog.TryGetBySeed(seedQualifiedItemId, out var crop)
                || !CanFarmCropGrowToday(crop, modules, season))
            {
                continue;
            }

            plot.SeedQualifiedItemId = crop.SeedQualifiedItemId;
            plot.HarvestQualifiedItemId = crop.HarvestQualifiedItemId;
            plot.ProgressUnits += dailyProgress;
            changed = true;
            var baseDays = plot.InRegrow ? crop.RegrowDays : crop.BaseGrowthDays;
            if (!FarmGrowthRules.IsMature(plot.ProgressUnits, baseDays))
                continue;

            SingleBlockFarmRules.AddHarvestOutput(outputBuffer, farm, crop, plot);
            if (crop.RegrowDays > 0)
            {
                plot.ProgressUnits = 0;
                plot.InRegrow = true;
            }
            else
            {
                if (plot.IsLocked && !string.IsNullOrWhiteSpace(plot.LockedSeedQualifiedItemId))
                    farm.PlotLocks[plot.PlotIndex] = plot.LockedSeedQualifiedItemId;
                farm.Plots.Remove(plot);
            }
        }

        return changed;
    }

    private static long CalculateFarmRequiredWh(int chargedOccupiedPlots, FarmModuleSnapshot modules, double energyMultiplier)
    {
        var covered = Math.Min(Math.Max(0, modules.SprinklerCoveredPlots), Math.Max(0, chargedOccupiedPlots));
        var uncovered = Math.Max(0, chargedOccupiedPlots - covered);
        var baseWh = FarmGrowthRules.GetDailyBaseEnergyWh(chargedOccupiedPlots);
        var waterWh = FarmGrowthRules.GetDailyWaterEnergyWh(covered, uncovered);
        var moduleWh = Math.Max(0, modules.ChamberModuleWhPerPlot) * Math.Max(0, chargedOccupiedPlots);
        var multiplier = energyMultiplier <= 0 ? 0 : energyMultiplier;
        return checked((long)Math.Round((baseWh + waterWh + moduleWh) * multiplier, MidpointRounding.AwayFromZero));
    }

    private static bool CanFarmCropGrowToday(FarmCropSpec crop, FarmModuleSnapshot modules, string season)
    {
        return modules.HasThermostat
            || crop.Seasons.Contains(season.Trim().ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);
    }

    private static bool MatchesFarmSeedFilter(FarmMachineState farm, Item item)
    {
        if (!string.Equals(farm.InputMode, MachineInputModes.Filter, StringComparison.Ordinal))
            return true;

        var matched = farm.SeedFilterQualifiedItemIds.Contains(item.QualifiedItemId, StringComparer.Ordinal);
        return string.Equals(farm.FilterMode, MachineFilterModes.Blacklist, StringComparison.Ordinal)
            ? !matched
            : matched;
    }

    private static void NormalizeLegacyFarmState(FarmMachineState farm)
    {
        if (farm.InternalSeedCount > 0 && !string.IsNullOrWhiteSpace(farm.BoundSeedQualifiedItemId))
        {
            var seed = ItemRegistry.Create(farm.BoundSeedQualifiedItemId);
            seed.Stack = farm.InternalSeedCount;
            AddBufferedInput(farm.InputBuffer, seed);
            farm.InternalSeedCount = 0;
        }

        foreach (var plot in farm.Plots)
        {
            if (string.IsNullOrWhiteSpace(plot.SeedQualifiedItemId))
                plot.SeedQualifiedItemId = farm.BoundSeedQualifiedItemId;
            if (string.IsNullOrWhiteSpace(plot.HarvestQualifiedItemId))
                plot.HarvestQualifiedItemId = farm.HarvestQualifiedItemId;
            if (plot.PlacedByFarmingLevel <= 0)
                plot.PlacedByFarmingLevel = farm.PlacedByFarmingLevel;
        }
    }

    private static void AddBufferedInput(IList<BufferedItemStack> buffer, Item item)
    {
        var toAdd = item.getOne();
        toAdd.Stack = Math.Max(1, item.Stack);
        foreach (var stack in buffer)
        {
            var existing = BufferedItemCodec.CreateItem(stack);
            if (!existing.canStackWith(toAdd))
                continue;

            stack.Stack += toAdd.Stack;
            return;
        }

        buffer.Add(BufferedItemCodec.FromItem(toAdd));
    }

    private static int CountSeedInputs(FarmMachineState farm)
    {
        return Math.Max(0, farm.InternalSeedCount)
            + farm.InputBuffer
                .Where(stack => FarmCropCatalog.TryGetBySeed(stack.QualifiedItemId, out _))
                .Sum(stack => Math.Max(0, stack.Stack));
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

    private static string BuildMachineEnergyReason(MachineState state, string operation)
    {
        return $"machine|{state.QualifiedItemId}|{state.MachineGuid:N}|{operation}";
    }
}

internal enum FarmSeedSource
{
    InputBuffer,
    Network
}

internal sealed class PlannedFarmSeed
{
    public PlannedFarmSeed(Item seedItem, FarmCropSpec crop, FarmSeedSource source, int farmingLevel, int targetPlotIndex)
    {
        this.SeedItem = seedItem;
        this.Crop = crop;
        this.Source = source;
        this.FarmingLevel = farmingLevel;
        this.TargetPlotIndex = targetPlotIndex;
    }

    public Item SeedItem { get; }
    public FarmCropSpec Crop { get; }
    public FarmSeedSource Source { get; }
    public int FarmingLevel { get; }
    public int TargetPlotIndex { get; }
    public string FertilizerQualifiedItemId { get; set; } = string.Empty;
}

internal readonly record struct FarmPlotView(
    int PlotIndex,
    string SeedQualifiedItemId,
    string HarvestQualifiedItemId,
    string Eta,
    long ProgressUnits,
    long RequiredUnits,
    bool Ready,
    string FertilizerQualifiedItemId,
    bool IsLocked,
    string LockedSeedQualifiedItemId);

internal readonly record struct FarmDashboardView(
    bool Available,
    bool AutoPullFromNetwork,
    bool AutoPushOutputToNetwork,
    string InputMode,
    string FilterMode,
    int FilterCount,
    int InputBufferStacks,
    int FertilizerCount,
    int OccupiedPlots,
    int EmptyPlots,
    int OutputBufferStacks,
    int ModuleSlotsUsed,
    int ModuleSlotsCapacity,
    IReadOnlyList<string> ModuleQualifiedItemIds,
    decimal EstimatedDailyValue,
    long EstimatedDailyEnergyWh);
