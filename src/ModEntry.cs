using Koizumi.SVSAP.Api;
using Koizumi.SVSAPME.Api;
using SVSAPME.Content;
using SVSAPME.Integrations;
using SVSAPME.Services;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace SVSAPME;

public sealed class ModEntry : Mod
{
    private ModConfig config = new();
    private ISvsapApi? svsapApi;
    private ISvsapmeEnergyApi? energyApi;
#if DEBUG
    private SvsapmeSelfTestService selfTestService = null!;
#endif
    private MachineStateRepository machineStateRepository = null!;
    private MachineRegistryService machineRegistryService = null!;
    private EnergyNetworkManager energyNetworkManager = null!;
    private EnergyProductionService energyProductionService = null!;
    private MachineRuntimeService machineRuntimeService = null!;
    private SingleBlockFarmService singleBlockFarmService = null!;
    private CellStackGuardService cellStackGuardService = null!;
    private SvsapmeMultiplayerService multiplayerService = null!;
#if DEBUG
    private SvsapmeP0P1E2EService p0p1E2EService = null!;
    private SvsapmeFullMatrixE2EService fullMatrixE2EService = null!;
#endif

    public override void Entry(IModHelper helper)
    {
        this.config = helper.ReadConfig<ModConfig>();
        this.NormalizeConfig();
        ModText.Load(helper.Translation);
#if DEBUG
        this.selfTestService = new SvsapmeSelfTestService(this.Monitor);
#endif
        this.machineStateRepository = new MachineStateRepository(helper, this.Monitor);
        this.machineRegistryService = new MachineRegistryService(this.machineStateRepository, this.Monitor);
        this.energyNetworkManager = new EnergyNetworkManager(
            this.machineStateRepository,
            this.machineRegistryService,
            () => this.svsapApi,
            this.Monitor);
        this.energyApi = new SvsapmeEnergyApi(this.energyNetworkManager);
        this.energyProductionService = new EnergyProductionService(
            this.machineRegistryService,
            this.energyNetworkManager,
            () => this.svsapApi,
            () => this.config,
            this.Monitor);
        this.machineRuntimeService = new MachineRuntimeService(
            this.machineStateRepository,
            this.machineRegistryService,
            this.energyNetworkManager,
            () => this.svsapApi,
            () => this.config,
            helper.Input,
            this.Monitor);
        this.singleBlockFarmService = new SingleBlockFarmService(
            this.machineStateRepository,
            this.machineRegistryService,
            this.energyNetworkManager,
            () => this.svsapApi,
            () => this.config,
            helper.Input,
            this.Monitor);
        this.cellStackGuardService = new CellStackGuardService(this.Monitor, this.machineRegistryService);
        EnergyCellStackingPatch.Apply(this.ModManifest.UniqueID, this.Monitor);
        this.multiplayerService = new SvsapmeMultiplayerService(
            helper,
            this.ModManifest,
            this.machineStateRepository,
            this.machineRegistryService,
            this.energyNetworkManager,
            this.machineRuntimeService,
            this.singleBlockFarmService,
            () => this.svsapApi,
            this.Monitor);
#if DEBUG
        this.p0p1E2EService = new SvsapmeP0P1E2EService(
            helper,
            this.Monitor,
            this.machineStateRepository,
            this.machineRegistryService,
            this.energyNetworkManager,
            this.machineRuntimeService,
            this.multiplayerService,
            () => this.svsapApi,
            () => this.config);
        this.fullMatrixE2EService = new SvsapmeFullMatrixE2EService(
            helper,
            this.Monitor,
            this.machineStateRepository,
            this.machineRegistryService,
            this.energyNetworkManager,
            this.machineRuntimeService,
            this.energyProductionService,
            () => this.svsapApi,
            () => this.config);
#endif
        this.machineRuntimeService.SetClientActionSender(this.multiplayerService.TrySendMachineActionRequest);
        this.singleBlockFarmService.SetClientActionSender(this.multiplayerService.TrySendMachineActionRequest);
        this.cellStackGuardService.SetClientMovementReporter(this.multiplayerService.TrySendMachineItemMovementReport);
        var contentInjector = new ContentInjector(() => this.config);

        helper.Events.Content.AssetRequested += contentInjector.OnAssetRequested;
        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.Events.GameLoop.UpdateTicked += this.machineRuntimeService.OnUpdateTicked;
        helper.Events.GameLoop.Saving += this.OnSaving;
        helper.Events.Input.ButtonPressed += this.machineRuntimeService.OnButtonPressed;
        helper.Events.Input.ButtonPressed += this.singleBlockFarmService.OnButtonPressed;
        helper.Events.Multiplayer.PeerContextReceived += this.multiplayerService.OnPeerContextReceived;
        helper.Events.Multiplayer.PeerDisconnected += this.multiplayerService.OnPeerDisconnected;
        helper.Events.Multiplayer.ModMessageReceived += this.multiplayerService.OnModMessageReceived;
        helper.Events.Player.InventoryChanged += this.cellStackGuardService.OnInventoryChanged;
        helper.Events.World.ChestInventoryChanged += this.cellStackGuardService.OnChestInventoryChanged;
        helper.Events.World.ObjectListChanged += this.machineRegistryService.OnObjectListChanged;
        helper.Events.World.BuildingListChanged += this.machineRegistryService.OnBuildingListChanged;

        helper.ConsoleCommands.Add(
            "svsapme_ids",
            "List SVSAPME item and machine ids.",
            this.CommandListIds);
        helper.ConsoleCommands.Add(
            "svsapme_balance",
            "Generate the SVSAPME B10 balance table from recipe constants.",
            this.CommandBalance);
#if DEBUG
        helper.ConsoleCommands.Add(
            "svsapme_selftest",
            "Run implemented SVSAPME foundation selftests. Optional args include wh-roundtrip tier-table content-table api-shape config-surface cell-stack-guard machine-guid-reconcile orphan-reclaim claim-force-gate consumed-charged-retire missing-machine-reclaim multiplayer-protocol action-idempotent escrow-restore host-action-dispatch energy-production-rules synth-atomic farm-crop-set farm-power-freeze farm-daily-progress farm-single-crop-budget farm-module-economy farm-fertilizer-quality farm-locked-output daily-order-storage-gate location-cache-full-enum building-demolish-reclaim powered-prescan-refund powered-degrade-parity powered-interface-range battery-discharge-gate electric-machine-rules b10-parity no-arbitrage-audit.",
            this.selfTestService.RunCommand);
#endif
        helper.ConsoleCommands.Add(
            "svsapme_debug_network",
            "Print the SVSAP API bridge state used by SVSAPME.",
            this.CommandDebugNetwork);
        helper.ConsoleCommands.Add(
            "svsapme_claim",
            "Create a reclaim chest at the player's feet for pending SVSAPME reclaim records. Usage: svsapme_claim [force]",
            this.CommandClaim);
        helper.ConsoleCommands.Add(
            "svsapme_energy_report",
            "Report stored/capacity Wh for a linked SVSAP network. Usage: svsapme_energy_report <networkGuid>",
            this.CommandEnergyReport);

#if DEBUG
        this.p0p1E2EService.Start();
        this.fullMatrixE2EService.Start();
#endif
        this.Monitor.Log("SVSAPME foundation loaded. Energy constants, balance generator, and API bridge commands are active.", LogLevel.Info);
    }

    public override object? GetApi()
    {
        return this.energyApi;
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        this.RegisterGmcm();
        this.svsapApi = this.Helper.ModRegistry.GetApi<ISvsapApi>("Koizumi.SVSAP");
        if (this.svsapApi is null)
        {
            this.Monitor.Log("Koizumi.SVSAP API was not found. SVSAPME gameplay systems will stay disabled until SVSAP 1.2.0+ is loaded.", LogLevel.Error);
            return;
        }

        if (this.svsapApi.ApiVersion < 1)
        {
            this.Monitor.Log($"Koizumi.SVSAP API version {this.svsapApi.ApiVersion} is too old. SVSAPME requires API version 1 or newer.", LogLevel.Error);
            return;
        }

        var snapshot = this.svsapApi.GetConfigSnapshot();
        this.Monitor.Log(
            $"Connected to Koizumi.SVSAP API v{this.svsapApi.ApiVersion}. RequireCables={snapshot.RequireCables}, MaxEndpoints={snapshot.MaxEndpointsPerNetwork}, MaxOpsPerTick={snapshot.MaxOperationsPerTick}.",
            LogLevel.Info);

#if DEBUG
        if (string.Equals(Environment.GetEnvironmentVariable("STARDEW_SVSAPME_RUN_SELFTEST"), "1", StringComparison.Ordinal))
            this.selfTestService.RunCommand("svsapme_selftest", Array.Empty<string>());
#endif
    }

    private void RegisterGmcm()
    {
        var gmcm = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (gmcm is null)
            return;

        gmcm.Register(
            mod: this.ModManifest,
            reset: () => this.config = new ModConfig(),
            save: () =>
            {
                this.NormalizeConfig();
                this.Helper.WriteConfig(this.config);
                this.Helper.GameContent.InvalidateCache("Data/CraftingRecipes");
                if (Context.IsWorldReady && Game1.player is not null)
                    SyncSvsapmeCraftingRecipeUnlocks(Game1.player, this.config);
            });

        gmcm.AddNumberOption(this.ModManifest, () => this.config.EnergyTickInterval, value => this.config.EnergyTickInterval = Math.Clamp(value, 1, 3600), () => ModText.Get("gmcm.energyTickInterval.name", "Energy tick interval"), min: 1, max: 3600);
        gmcm.AddBoolOption(this.ModManifest, () => this.config.EnableCarbonGenerator, value => this.config.EnableCarbonGenerator = value, () => ModText.Get("gmcm.enableCarbonGenerator.name", "Enable Carbon Generator"));
        gmcm.AddBoolOption(this.ModManifest, () => this.config.EnableExtendedGeneratorFuels, value => this.config.EnableExtendedGeneratorFuels = value, () => ModText.Get("gmcm.enableExtendedGeneratorFuels.name", "Enable Extended Generator Fuels"));
        gmcm.AddBoolOption(this.ModManifest, () => this.config.EnableSolarNetworkPanel, value => this.config.EnableSolarNetworkPanel = value, () => ModText.Get("gmcm.enableSolarNetworkPanel.name", "Enable Solar Network Panel"));
        gmcm.AddBoolOption(this.ModManifest, () => this.config.EnableLightningCapacitor, value => this.config.EnableLightningCapacitor = value, () => ModText.Get("gmcm.enableLightningCapacitor.name", "Enable Lightning Capacitor"));
        gmcm.AddBoolOption(this.ModManifest, () => this.config.EnableSingleBlockFarm, value => this.config.EnableSingleBlockFarm = value, () => ModText.Get("gmcm.enableSingleBlockFarm.name", "Enable Single-Block Farm"));
        gmcm.AddBoolOption(this.ModManifest, () => this.config.EnableBatterySynthesizer, value => this.config.EnableBatterySynthesizer = value, () => ModText.Get("gmcm.enableBatterySynthesizer.name", "Enable Battery Synthesizer"));
        gmcm.AddBoolOption(this.ModManifest, () => this.config.EnablePoweredTransfer, value => this.config.EnablePoweredTransfer = value, () => ModText.Get("gmcm.enablePoweredTransfer.name", "Enable Powered Transfer"));
        gmcm.AddBoolOption(this.ModManifest, () => this.config.EnableElectricMachines, value => this.config.EnableElectricMachines = value, () => ModText.Get("gmcm.enableElectricMachines.name", "Enable Electric Machines"));
        gmcm.AddBoolOption(this.ModManifest, () => this.config.EnableAutomaticFarmOutputToNetwork, value => this.config.EnableAutomaticFarmOutputToNetwork = value, () => ModText.Get("gmcm.enableAutomaticFarmOutputToNetwork.name", "Enable Farm Output To Network"));
        gmcm.AddBoolOption(this.ModManifest, () => this.config.AllowBatteryDischarge, value => this.config.AllowBatteryDischarge = value, () => ModText.Get("gmcm.allowBatteryDischarge.name", "Allow Battery Discharge"));
        gmcm.AddNumberOption(this.ModManifest, () => (float)this.config.BatteryDischargeEfficiency, value => this.config.BatteryDischargeEfficiency = Math.Clamp(value, 0f, 1f), () => ModText.Get("gmcm.batteryDischargeEfficiency.name", "Battery Discharge Efficiency"), min: 0f, max: 1f, interval: 0.05f, formatValue: value => value.ToString("0.00"));
        gmcm.AddNumberOption(this.ModManifest, () => (float)this.config.GeneratorMultiplier, value => this.config.GeneratorMultiplier = Math.Clamp(value, 0f, 10f), () => ModText.Get("gmcm.generatorMultiplier.name", "Generator Multiplier"), min: 0f, max: 10f, interval: 0.1f, formatValue: value => value.ToString("0.0"));
        gmcm.AddNumberOption(this.ModManifest, () => (float)this.config.MachineEnergyCostMultiplier, value => this.config.MachineEnergyCostMultiplier = Math.Clamp(value, 0.1f, 10f), () => ModText.Get("gmcm.machineEnergyCostMultiplier.name", "Machine Energy Cost Multiplier"), min: 0.1f, max: 10f, interval: 0.1f, formatValue: value => value.ToString("0.0"));
        gmcm.AddNumberOption(this.ModManifest, () => (float)this.config.FarmEnergyCostMultiplier, value => this.config.FarmEnergyCostMultiplier = Math.Clamp(value, 0.1f, 10f), () => ModText.Get("gmcm.farmEnergyCostMultiplier.name", "Farm Energy Cost Multiplier"), min: 0.1f, max: 10f, interval: 0.1f, formatValue: value => value.ToString("0.0"));
        gmcm.AddNumberOption(this.ModManifest, () => (float)this.config.BatterySynthesisEnergyMultiplier, value => this.config.BatterySynthesisEnergyMultiplier = Math.Clamp(value, 0.1f, 10f), () => ModText.Get("gmcm.batterySynthesisEnergyMultiplier.name", "Battery Synthesis Energy Multiplier"), min: 0.1f, max: 10f, interval: 0.1f, formatValue: value => value.ToString("0.0"));
        gmcm.AddTextOption(
            this.ModManifest,
            () => this.config.GetRecipeCostMode(),
            value => this.config.RecipeCostMode = RecipeCostModes.Normalize(value),
            () => ModText.Get("gmcm.recipeCostMode.name", "Material Cost"),
            () => ModText.Get("gmcm.recipeCostMode.tooltip", "Normal uses designed recipes; Casual roughly halves ingredients; Debug removes skill gates and makes SVSAPME recipes cost 0."),
            allowedValues: RecipeCostModes.All,
            formatAllowedValue: FormatRecipeCostMode);
        gmcm.AddBoolOption(this.ModManifest, () => this.config.DetailedEnergyLogs, value => this.config.DetailedEnergyLogs = value, () => ModText.Get("gmcm.detailedEnergyLogs.name", "Detailed Energy Logs"));
        gmcm.AddBoolOption(this.ModManifest, () => this.config.DebugEnergyRouting, value => this.config.DebugEnergyRouting = value, () => ModText.Get("gmcm.debugEnergyRouting.name", "Debug Energy Routing"));
    }

    private void NormalizeConfig()
    {
        this.config.EnergyTickInterval = Math.Clamp(this.config.EnergyTickInterval, 1, 3600);
        this.config.BatteryDischargeEfficiency = Math.Clamp(this.config.BatteryDischargeEfficiency, 0.0, 1.0);
        this.config.GeneratorMultiplier = Math.Clamp(this.config.GeneratorMultiplier, 0.0, 10.0);
        this.config.MachineEnergyCostMultiplier = Math.Clamp(this.config.MachineEnergyCostMultiplier, 0.1, 10.0);
        this.config.FarmEnergyCostMultiplier = Math.Clamp(this.config.FarmEnergyCostMultiplier, 0.1, 10.0);
        this.config.BatterySynthesisEnergyMultiplier = Math.Clamp(this.config.BatterySynthesisEnergyMultiplier, 0.1, 10.0);
        this.config.NormalizeRecipeCostMode();
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        if (Game1.player is not null)
            SyncSvsapmeCraftingRecipeUnlocks(Game1.player, this.config);

        if (!Context.IsMainPlayer)
            return;

        this.machineStateRepository.Load();
        this.multiplayerService.OnSaveLoaded(sender, e);
        this.machineRegistryService.RebuildCache();
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (Game1.player is not null)
            SyncSvsapmeCraftingRecipeUnlocks(Game1.player, this.config);

        this.machineRegistryService.ReconcileMissingMachinesOnDayStarted();
        foreach (var step in DailySettlementRules.DayStartedOrder)
        {
            switch (step)
            {
                case DailySettlementStep.FarmConsumptionAndGrowth:
                    this.singleBlockFarmService.OnDayStarted(sender, e);
                    break;

                case DailySettlementStep.EnergyProduction:
                    this.energyProductionService.OnDayStarted(sender, e);
                    break;
            }
        }
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        if (!Context.IsMainPlayer)
            return;

        this.machineStateRepository.Save();
    }

    private void CommandListIds(string command, string[] args)
    {
        this.Monitor.Log("SVSAPME object items:", LogLevel.Info);
        foreach (var id in ModItemCatalog.ObjectItemIds)
            this.Monitor.Log($"  (O){id}", LogLevel.Info);

        this.Monitor.Log("SVSAPME big craftables:", LogLevel.Info);
        foreach (var id in ModItemCatalog.BigCraftableIds)
            this.Monitor.Log($"  (BC){id}", LogLevel.Info);

        this.Monitor.Log("Energy cell tiers:", LogLevel.Info);
        foreach (var tier in EnergyTierTable.EnergyCells)
        {
            this.Monitor.Log(
                $"  {tier.Tier}: capacity={tier.CapacityWh} Wh, routeTickIoLimit={tier.RouteTickIoLimitWh} Wh, mining={tier.RequiredMiningLevel}",
                LogLevel.Info);
        }

        this.Monitor.Log("Single-block farm tiers:", LogLevel.Info);
        foreach (var tier in EnergyTierTable.Farms)
        {
            this.Monitor.Log(
                $"  {tier.Tier}: plots={tier.Plots}, moduleSlots={tier.ModuleSlots}, base={tier.BaseWhPerPlotPerDay} Wh/plot/day, farming={tier.RequiredFarmingLevel}",
                LogLevel.Info);
        }
    }

    private void CommandBalance(string command, string[] args)
    {
        foreach (var line in SvsapmeBalanceTable.ToReportLines())
            this.Monitor.Log(line, LogLevel.Info);
    }

    private void CommandDebugNetwork(string command, string[] args)
    {
        if (this.svsapApi is null)
        {
            this.Monitor.Log("SVSAP API bridge is not connected.", LogLevel.Warn);
            return;
        }

        this.Monitor.Log($"SVSAP API version: {this.svsapApi.ApiVersion}", LogLevel.Info);
        this.Monitor.Log($"Host authority: {this.svsapApi.IsHostAuthority}", LogLevel.Info);
        this.Monitor.Log($"NetworkId key: {this.svsapApi.NetworkIdModDataKey}", LogLevel.Info);
        this.Monitor.Log($"EndpointId key: {this.svsapApi.EndpointIdModDataKey}", LogLevel.Info);
        this.Monitor.Log($"Placed SVSAPME machines in cache: {this.machineRegistryService.MachinesByGuid.Count}", LogLevel.Info);
    }

    private void CommandEnergyReport(string command, string[] args)
    {
        if (args.Length < 1 || !Guid.TryParse(args[0], out var networkId))
        {
            this.Monitor.Log("Usage: svsapme_energy_report <networkGuid>", LogLevel.Warn);
            return;
        }

        if (!this.energyNetworkManager.TryGetNetworkEnergy(networkId, out var storedWh, out var capacityWh, out var code))
        {
            this.Monitor.Log($"Energy report failed: {code}", LogLevel.Warn);
            return;
        }

        this.Monitor.Log($"Network {networkId:N}: {storedWh} / {capacityWh} Wh ({storedWh / 1000m:0.00} / {capacityWh / 1000m:0.00} kWh)", LogLevel.Info);
    }

    private void CommandClaim(string command, string[] args)
    {
        var includeUnconfirmed = false;
        foreach (var arg in args)
        {
            if (string.Equals(arg, "force", StringComparison.OrdinalIgnoreCase))
            {
                includeUnconfirmed = true;
                continue;
            }

            this.Monitor.Log("Usage: svsapme_claim [force]", LogLevel.Warn);
            return;
        }

        var pending = this.machineStateRepository.Data.PendingReclaims;
        if (pending.Count == 0)
        {
            this.Monitor.Log("No pending SVSAPME reclaim records.", LogLevel.Info);
            return;
        }

        if (!Context.IsMainPlayer)
        {
            this.Monitor.Log("SVSAPME reclaim can only be claimed by the host.", LogLevel.Warn);
            return;
        }

        if (Game1.player is null)
        {
            this.Monitor.Log("No player is available for SVSAPME reclaim placement.", LogLevel.Warn);
            return;
        }

        if (this.machineRegistryService.TryClaimPendingReclaims(Game1.player, includeUnconfirmed, out var machines, out var bufferedItems, out var message))
            this.Monitor.Log($"{message} machines={machines}, bufferedItems={bufferedItems}", LogLevel.Info);
        else
            this.Monitor.Log(message, LogLevel.Warn);
    }

    private static string FormatRecipeCostMode(string value)
    {
        return RecipeCostModes.Normalize(value) switch
        {
            RecipeCostModes.Casual => ModText.Get("gmcm.recipeCostMode.Casual", "Casual"),
            RecipeCostModes.Debug => ModText.Get("gmcm.recipeCostMode.Debug", "Debug"),
            _ => ModText.Get("gmcm.recipeCostMode.Normal", "Normal")
        };
    }

    internal static void SyncSvsapmeCraftingRecipeUnlocks(Farmer player, ModConfig config)
    {
        if (config.IsDebugRecipeCostMode())
        {
            foreach (var recipeName in ModItemCatalog.CraftingRecipes.Keys)
            {
                if (!player.craftingRecipes.ContainsKey(recipeName))
                    player.craftingRecipes.Add(recipeName, 0);
            }

            return;
        }

        foreach (var pair in ModItemCatalog.CraftingRecipeSkillRequirements)
        {
            if (MeetsSkillRequirement(player, pair.Value))
            {
                if (!player.craftingRecipes.ContainsKey(pair.Key))
                    player.craftingRecipes.Add(pair.Key, 0);
            }
            else
            {
                player.craftingRecipes.Remove(pair.Key);
            }
        }
    }

    private static bool MeetsSkillRequirement(Farmer player, string requirement)
    {
        var parts = requirement.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var requiredLevel))
            return true;

        return parts[0] switch
        {
            "Mining" => player.MiningLevel >= requiredLevel,
            "Farming" => player.FarmingLevel >= requiredLevel,
            _ => true
        };
    }
}
