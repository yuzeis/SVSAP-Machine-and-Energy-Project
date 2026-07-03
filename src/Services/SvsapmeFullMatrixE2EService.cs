#if DEBUG
using System.Text.Json;
using Koizumi.SVSAP.Api;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Objects;
using SVSAPME.Content;
using SVSAPME.Models;
using SObject = StardewValley.Object;

namespace SVSAPME.Services;

internal sealed class SvsapmeFullMatrixE2EService
{
    private const string EnabledEnv = "STARDEW_SVSAPME_FULL_E2E";
    private const string OutputDirEnv = "STARDEW_SVSAPME_FULL_E2E_OUTPUT";
    private const string VersionEnv = "STARDEW_SVSAPME_FULL_E2E_VERSION";
    private const string DefaultVersionLabel = "ver1.2-alpha.2";
    private const string SvsapNetworkIdKey = ModItemCatalog.SvsapUniqueId + "/NetworkId";
    private const string SvsapEndpointIdKey = ModItemCatalog.SvsapUniqueId + "/EndpointId";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly MachineStateRepository repository;
    private readonly MachineRegistryService registry;
    private readonly EnergyNetworkManager energy;
    private readonly MachineRuntimeService runtime;
    private readonly EnergyProductionService production;
    private readonly Func<ISvsapApi?> getSvsapApi;
    private readonly Func<ModConfig> getConfig;
    private readonly string outputDir;
    private readonly string versionLabel;
    private readonly List<E2EResult> results = new();
    private object? svsapNetworkRepository;

    private bool started;
    private bool stopped;
    private int ticks;

    public SvsapmeFullMatrixE2EService(
        IModHelper helper,
        IMonitor monitor,
        MachineStateRepository repository,
        MachineRegistryService registry,
        EnergyNetworkManager energy,
        MachineRuntimeService runtime,
        EnergyProductionService production,
        Func<ISvsapApi?> getSvsapApi,
        Func<ModConfig> getConfig)
    {
        this.helper = helper;
        this.monitor = monitor;
        this.repository = repository;
        this.registry = registry;
        this.energy = energy;
        this.runtime = runtime;
        this.production = production;
        this.getSvsapApi = getSvsapApi;
        this.getConfig = getConfig;
        this.outputDir = Environment.GetEnvironmentVariable(OutputDirEnv) ?? string.Empty;
        this.versionLabel = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VersionEnv))
            ? DefaultVersionLabel
            : Environment.GetEnvironmentVariable(VersionEnv)!;
    }

    private bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnabledEnv), "1", StringComparison.Ordinal)
        || !string.IsNullOrWhiteSpace(this.outputDir);

    public void Start()
    {
        if (!this.IsEnabled || this.started)
            return;

        this.started = true;
        this.runtime.SuppressAutomaticRouteTicksForE2E = true;
        if (!string.IsNullOrWhiteSpace(this.outputDir))
            Directory.CreateDirectory(this.outputDir);

        this.helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        this.monitor.Log($"SVSAPME_FULL_E2E started version={this.versionLabel} output=\"{this.outputDir}\"", LogLevel.Info);
    }

    private void Stop()
    {
        if (this.stopped)
            return;

        this.stopped = true;
        this.helper.Events.GameLoop.UpdateTicked -= this.OnUpdateTicked;
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (this.stopped || !Context.IsWorldReady || !Context.IsMainPlayer)
            return;

        this.ticks++;
        if (this.ticks < 60)
            return;

        try
        {
            this.RunAll();
            this.WriteResults("full-matrix-complete.json");
        }
        catch (Exception ex)
        {
            this.Record("exception", false, $"{ex.GetType().Name}: {ex.Message}");
            this.WriteResults("full-matrix-fail.json");
            this.monitor.Log($"SVSAPME_FULL_E2E_FAIL {ex}", LogLevel.Error);
        }
        finally
        {
            this.Stop();
        }
    }

    private void RunAll()
    {
        var config = this.getConfig();
        config.EnergyTickInterval = 1;
        config.MachineEnergyCostMultiplier = 1.0;
        config.FarmEnergyCostMultiplier = 1.0;
        config.BatterySynthesisEnergyMultiplier = 1.0;
        config.GeneratorMultiplier = 1.0;
        config.BatteryDischargeEfficiency = 0.8;

        this.registry.RebuildCache();

        this.Check("36", this.TestCarbonGenerator);
        this.Check("37", this.TestSolarPanel);
        this.Check("38", this.TestLightningCapacitor);
        this.Check("39", this.TestEnergyCells);
        this.Check("40", this.TestBatterySynthesizer);
        this.Check("41", this.TestBatteryDischarger);
        this.Check("42", this.TestEnergyMonitorBackend);
        this.Check("43", this.TestFarmTiers);
        this.Check("44", this.TestFarmChamberRecipes);
        this.Check("45", this.TestGrowthLights);
        this.Check("46", this.TestThermostats);
        this.Check("47", this.TestSlowRelease);
        this.Check("48", this.TestModuleHotSwapProgress);
        this.Check("49", this.TestPoweredImporterTiers);
        this.Check("50", this.TestPoweredExporterTiers);
        this.Check("51", this.TestPoweredInterfaceRangeAndAction);
        this.Check("52", this.TestElectricFurnace);
        this.Check("53", this.TestElectricGeodeCrusher);
        this.Check("54", this.TestL7NoRegression);
        this.Check("55", this.TestAtomicEnergyPayments);
        this.Check("56", this.TestNetworkConnection);
        this.Check("57", this.TestCraftingTerminalSurface);
        this.Check("58", this.TestPatternSurface);
        this.Check("59", this.TestQualityStrategySurface);
        this.Check("60", this.TestCrossLocationNetwork);
        this.Check("61", this.TestMachineGuidPickupReplay);
        this.Check("62", this.TestChargedCellPickupReplay);
        this.Check("63", this.TestPersistentContainers);
        this.Check("64", this.TestJunimoGlobalInventoryCoverage);
        this.Check("65", this.TestConsumedChargedMachine);
        this.Check("66", this.TestDigitizeGuard);
        this.Check("67", this.TestStackGuard);
        this.Check("68", this.TestPendingReclaimLifecycle);
        this.Check("69", this.TestClaimGate);
        this.Check("70", this.TestDemolishHeldExclusion);
        this.Check("71", this.TestRepositoryRoundTrip);
        this.Check("88", this.TestFullNetworkStress);
        this.Check("89", this.TestZeroEnergyDegrade);
        this.Check("90", this.TestEnergyBoundaries);
        this.Check("91", this.TestRapidPickupReplay);
        this.Check("92", this.TestInterruptedSettlementRoundTrip);
    }

    private void Check(string id, Func<(bool Pass, string Evidence)> test)
    {
        try
        {
            var result = test();
            this.Record(id, result.Pass, result.Evidence);
        }
        catch (Exception ex)
        {
            this.Record(id, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private (bool, string) TestCarbonGenerator()
    {
        var fixture = this.CreateFixture(width: 4, height: 3);
        var generatorTile = fixture.Origin + new Vector2(2, 0);
        var generator = this.PlaceLinkedMachine(fixture.Location, generatorTile, "(BC)" + ModItemCatalog.CarbonGenerator, fixture.NetworkId);
        var generatorGuid = this.RegisterMachine(generator, fixture.Location, generatorTile, 0);
        var fuel = this.runtime.TryFuelCarbonGenerator(generator, fixture.Location, generatorTile, "(O)382");
        var before = this.ReadEnergy(fixture.NetworkId).StoredWh;
        this.runtime.RunRouteTickForE2E();
        var after = this.ReadEnergy(fixture.NetworkId).StoredWh;
        var state = this.GetState(generatorGuid);
        return (fuel.Success && after - before == 350 && state.StoredWh == 0, $"fuel={fuel.Success} before={before} after={after} generatorBuffer={state.StoredWh}");
    }

    private (bool, string) TestSolarPanel()
    {
        var sunny = EnergyProductionRules.GetSolarPanelWh(true, false, false, Season.Spring);
        var rainy = EnergyProductionRules.GetSolarPanelWh(true, true, false, Season.Spring);
        var winter = EnergyProductionRules.GetSolarPanelWh(true, false, false, Season.Winter);
        var indoor = EnergyProductionRules.GetSolarPanelWh(false, false, false, Season.Spring);
        return (sunny == 1500 && rainy == 200 && winter == 1000 && indoor == 0, $"sunny={sunny} rainy={rainy} winter={winter} indoor={indoor}");
    }

    private (bool, string) TestLightningCapacitor()
    {
        var storm = EnergyProductionRules.GetLightningCapacitorWh(true, true);
        var rainless = EnergyProductionRules.GetLightningCapacitorWh(true, false);
        var indoor = EnergyProductionRules.GetLightningCapacitorWh(false, true);
        return (storm == 6000 && rainless == 0 && indoor == 0, $"storm={storm} rainless={rainless} indoor={indoor}");
    }

    private (bool, string) TestEnergyCells()
    {
        var fixture = this.CreateFixture(width: 8, height: 3);
        var cells = new[]
        {
            ("(BC)" + ModItemCatalog.CopperEnergyCell, 10_000L),
            ("(BC)" + ModItemCatalog.SteelEnergyCell, 40_000L),
            ("(BC)" + ModItemCatalog.GoldEnergyCell, 160_000L),
            ("(BC)" + ModItemCatalog.IridiumEnergyCell, 640_000L)
        };

        var details = new List<string>();
        var ok = true;
        for (var i = 0; i < cells.Length; i++)
        {
            var tile = fixture.Origin + new Vector2(i + 2, 0);
            var obj = this.PlaceLinkedMachine(fixture.Location, tile, cells[i].Item1, fixture.NetworkId);
            var guid = this.RegisterMachine(obj, fixture.Location, tile, 0);
            var state = this.GetState(guid);
            ok &= state.CapacityWh == cells[i].Item2;
            details.Add($"{obj.QualifiedItemId}:{state.CapacityWh}");
        }

        var deposit = this.energy.TryDepositWh(fixture.NetworkId, 123_456, ModItemCatalog.UniqueId, "full-e2e-cell-deposit", out var accepted, out _, out _);
        var consume = this.energy.TryConsumeWh(fixture.NetworkId, 23_456, ModItemCatalog.UniqueId, "full-e2e-cell-consume", false, out var consumed, out _, out _);
        var read = this.ReadEnergy(fixture.NetworkId);
        ok &= deposit && accepted == 123_456 && consume && consumed == 23_456 && read.StoredWh == 100_000 && read.CapacityWh >= 850_000;
        return (ok, $"tiers=[{string.Join(",", details)}] accepted={accepted} consumed={consumed} stored={read.StoredWh} capacity={read.CapacityWh}");
    }

    private (bool, string) TestBatterySynthesizer()
    {
        var fixture = this.CreateFixture(width: 5, height: 3);
        var synthTile = fixture.Origin + new Vector2(2, 0);
        var synth = this.PlaceLinkedMachine(fixture.Location, synthTile, "(BC)" + ModItemCatalog.BatterySynthesizer, fixture.NetworkId);
        var synthGuid = this.RegisterMachine(synth, fixture.Location, synthTile, 0);
        this.SetNetworkEnergy(fixture, 20_000);
        foreach (var material in BatterySynthesizerRules.Materials)
            this.InsertNetwork(fixture.NetworkId, material.QualifiedItemId, material.Count);

        var before = this.ReadEnergy(fixture.NetworkId).StoredWh;
        this.runtime.RunRouteTickForE2E();
        var after = this.ReadEnergy(fixture.NetworkId).StoredWh;
        var batteryCount = CountItem(fixture.TargetChest, BatterySynthesizerRules.BatteryPackQualifiedItemId);
        var synthState = this.GetState(synthGuid);
        return (before - after == 10_000 && batteryCount >= 1 && synthState.ProgressWh == 0, $"before={before} after={after} batteryCount={batteryCount} progress={synthState.ProgressWh}");
    }

    private (bool, string) TestBatteryDischarger()
    {
        var fixture = this.CreateFixture(width: 5, height: 3);
        var dischargerTile = fixture.Origin + new Vector2(2, 0);
        var discharger = this.PlaceLinkedMachine(fixture.Location, dischargerTile, "(BC)" + ModItemCatalog.BatteryDischarger, fixture.NetworkId);
        this.RegisterMachine(discharger, fixture.Location, dischargerTile, 0);
        this.SetNetworkEnergy(fixture, 0);
        this.InsertNetwork(fixture.NetworkId, BatteryDischargerRules.BatteryPackQualifiedItemId, 2);

        this.getConfig().AllowBatteryDischarge = false;
        this.runtime.RunRouteTickForE2E();
        var disabledWh = this.ReadEnergy(fixture.NetworkId).StoredWh;
        var disabledBatteryCount = CountItem(fixture.TargetChest, BatteryDischargerRules.BatteryPackQualifiedItemId);

        this.getConfig().AllowBatteryDischarge = true;
        this.runtime.RunRouteTickForE2E();
        var enabledWh = this.ReadEnergy(fixture.NetworkId).StoredWh;
        var enabledBatteryCount = CountItem(fixture.TargetChest, BatteryDischargerRules.BatteryPackQualifiedItemId);
        this.getConfig().AllowBatteryDischarge = false;

        return (disabledWh == 0 && disabledBatteryCount == 2 && enabledWh == 8_000 && enabledBatteryCount == 1, $"disabledWh={disabledWh} disabledBatteries={disabledBatteryCount} enabledWh={enabledWh} enabledBatteries={enabledBatteryCount}");
    }

    private (bool, string) TestEnergyMonitorBackend()
    {
        var fixture = this.CreateFixture(width: 4, height: 3);
        var monitorTile = fixture.Origin + new Vector2(2, 0);
        var monitor = this.PlaceLinkedMachine(fixture.Location, monitorTile, "(BC)" + ModItemCatalog.EnergyMonitorTerminal, fixture.NetworkId);
        this.RegisterMachine(monitor, fixture.Location, monitorTile, 0);
        this.SetNetworkEnergy(fixture, 4_321);
        var endpointOk = this.getSvsapApi()!.TryGetLinkedEndpoint(fixture.Location, monitorTile, out var endpoint, out _, out _);
        var energyOk = this.energy.TryGetNetworkEnergy(fixture.NetworkId, out var stored, out var capacity, out _);
        return (endpointOk && endpoint is not null && endpoint.Active && energyOk && stored == 4_321 && capacity >= 10_000, $"endpoint={endpointOk}/{endpoint?.Active} stored={stored} capacity={capacity}");
    }

    private (bool, string) TestFarmTiers()
    {
        var expected = new[]
        {
            ("(BC)" + ModItemCatalog.CopperFarm, 16, 2),
            ("(BC)" + ModItemCatalog.SteelFarm, 64, 3),
            ("(BC)" + ModItemCatalog.GoldFarm, 144, 4),
            ("(BC)" + ModItemCatalog.IridiumFarm, 256, 5)
        };
        var ok = true;
        var details = new List<string>();
        foreach (var entry in expected)
        {
            var tier = SingleBlockFarmRules.GetFarmTier(entry.Item1);
            ok &= tier.Plots == entry.Item2 && tier.ModuleSlots == entry.Item3;
            details.Add($"{entry.Item1}:{tier.Plots}/{tier.ModuleSlots}");
        }

        return (ok, string.Join(" ", details));
    }

    private (bool, string) TestFarmChamberRecipes()
    {
        var recipes = ModItemCatalog.CraftingRecipes;
        var steel = recipes[ModItemCatalog.SteelFarm].Contains("(BC)" + ModItemCatalog.CopperFarm, StringComparison.Ordinal);
        var gold = recipes[ModItemCatalog.GoldFarm].Contains("(BC)" + ModItemCatalog.SteelFarm, StringComparison.Ordinal);
        var iridium = recipes[ModItemCatalog.IridiumFarm].Contains("(BC)" + ModItemCatalog.GoldFarm, StringComparison.Ordinal);
        return (steel && gold && iridium, $"steelConsumesCopper={steel} goldConsumesSteel={gold} iridiumConsumesGold={iridium}");
    }

    private (bool, string) TestGrowthLights()
    {
        var farm = new FarmMachineState();
        var tier = SingleBlockFarmRules.GetFarmTier("(BC)" + ModItemCatalog.IridiumFarm);
        var a = FarmModuleRules.TryInstallModule(farm, tier, "(O)" + ModItemCatalog.IridiumGrowthLightModule);
        var b = FarmModuleRules.TryInstallModule(farm, tier, "(O)" + ModItemCatalog.IridiumGrowthLightModule);
        var c = FarmModuleRules.TryInstallModule(farm, tier, "(O)" + ModItemCatalog.IridiumGrowthLightModule);
        var snapshot = SingleBlockFarmRules.GetModuleSnapshot(farm);
        return (a.Success && b.Success && !c.Success && snapshot.LightFactorProduct == 0.64m && snapshot.ChamberModuleWhPerPlot == 160, $"a={a.Success} b={b.Success} c={c.Success} light={snapshot.LightFactorProduct} wh={snapshot.ChamberModuleWhPerPlot}");
    }

    private (bool, string) TestThermostats()
    {
        var farm = new FarmMachineState();
        var tier = SingleBlockFarmRules.GetFarmTier("(BC)" + ModItemCatalog.IridiumFarm);
        var a = FarmModuleRules.TryInstallModule(farm, tier, "(O)" + ModItemCatalog.IridiumThermostatModule);
        var b = FarmModuleRules.TryInstallModule(farm, tier, "(O)" + ModItemCatalog.BasicThermostatModule);
        var snapshot = SingleBlockFarmRules.GetModuleSnapshot(farm);
        return (a.Success && !b.Success && snapshot.HasThermostat && snapshot.ThermostatFactor == 0.9m && snapshot.ChamberModuleWhPerPlot == 150, $"a={a.Success} b={b.Success} thermostat={snapshot.HasThermostat} factor={snapshot.ThermostatFactor} wh={snapshot.ChamberModuleWhPerPlot}");
    }

    private (bool, string) TestSlowRelease()
    {
        var farm = new FarmMachineState();
        var tier = SingleBlockFarmRules.GetFarmTier("(BC)" + ModItemCatalog.GoldFarm);
        var a = FarmModuleRules.TryInstallModule(farm, tier, "(O)" + ModItemCatalog.IridiumSlowReleaseModule);
        var b = FarmModuleRules.TryInstallModule(farm, tier, "(O)" + ModItemCatalog.BasicSlowReleaseModule);
        var snapshot = SingleBlockFarmRules.GetModuleSnapshot(farm);
        var units = FarmModuleRules.CalculateFertilizerUnitsForPlots(17, snapshot.FertilizerCoveragePerFertilizer);
        return (a.Success && !b.Success && snapshot.FertilizerCoveragePerFertilizer == 8 && units == 3, $"a={a.Success} b={b.Success} coverage={snapshot.FertilizerCoveragePerFertilizer} units17={units}");
    }

    private (bool, string) TestModuleHotSwapProgress()
    {
        FarmCropCatalog.TryGetBySeed("(O)499", out var ancient);
        var tier = SingleBlockFarmRules.GetFarmTier("(BC)" + ModItemCatalog.CopperFarm);
        var farm = new FarmMachineState { InternalSeedCount = 1 };
        SingleBlockFarmRules.BindSeed(farm, ancient, 10);
        var noModules = SingleBlockFarmRules.GetModuleSnapshot(farm);
        var plan1 = SingleBlockFarmRules.PlanDay(farm, tier, ancient, noModules, 0, 0, "spring", 1.0);
        SingleBlockFarmRules.ApplyPaidDay(farm, new List<BufferedItemStack>(), tier, ancient, noModules, plan1);
        var afterDay1 = farm.Plots.Single().ProgressUnits;
        FarmModuleRules.TryInstallModule(farm, tier, "(O)" + ModItemCatalog.IridiumGrowthLightModule);
        var lit = SingleBlockFarmRules.GetModuleSnapshot(farm);
        var plan2 = SingleBlockFarmRules.PlanDay(farm, tier, ancient, lit, 0, 0, "spring", 1.0);
        SingleBlockFarmRules.ApplyPaidDay(farm, new List<BufferedItemStack>(), tier, ancient, lit, plan2);
        var afterDay2 = farm.Plots.Single().ProgressUnits;
        return (afterDay1 == FarmGrowthRules.GetDailyProgressUnits(1m, 1m) && afterDay2 > afterDay1 && afterDay2 < FarmGrowthRules.GetRequiredProgressUnits(ancient.BaseGrowthDays), $"afterDay1={afterDay1} afterDay2={afterDay2} required={FarmGrowthRules.GetRequiredProgressUnits(ancient.BaseGrowthDays)}");
    }

    private (bool, string) TestPoweredImporterTiers()
    {
        var tiers = new[]
        {
            (PoweredMachineTier.Copper, "(BC)" + ModItemCatalog.PoweredImporterCopper, 64),
            (PoweredMachineTier.Steel, "(BC)" + ModItemCatalog.PoweredImporterSteel, 64),
            (PoweredMachineTier.Gold, "(BC)" + ModItemCatalog.PoweredImporterGold, 256),
            (PoweredMachineTier.Iridium, "(BC)" + ModItemCatalog.PoweredImporterIridium, 1024)
        };

        var ok = true;
        var details = new List<string>();
        foreach (var tier in tiers)
        {
            var moved = this.RunImporterScenario(tier.Item2, tier.Item3, powered: true);
            ok &= moved >= tier.Item3;
            details.Add($"{tier.Item1}:{moved}");
        }

        return (ok, string.Join(" ", details));
    }

    private (bool, string) TestPoweredExporterTiers()
    {
        var tiers = new[]
        {
            (PoweredMachineTier.Copper, "(BC)" + ModItemCatalog.PoweredExporterCopper, 64),
            (PoweredMachineTier.Steel, "(BC)" + ModItemCatalog.PoweredExporterSteel, 64),
            (PoweredMachineTier.Gold, "(BC)" + ModItemCatalog.PoweredExporterGold, 256),
            (PoweredMachineTier.Iridium, "(BC)" + ModItemCatalog.PoweredExporterIridium, 1024)
        };

        var ok = true;
        var details = new List<string>();
        foreach (var tier in tiers)
        {
            var moved = this.RunExporterScenario(tier.Item2, tier.Item3);
            ok &= moved >= tier.Item3;
            details.Add($"{tier.Item1}:{moved}");
        }

        return (ok, string.Join(" ", details));
    }

    private (bool, string) TestPoweredInterfaceRangeAndAction()
    {
        var prototype = PoweredMachineInterfaceRules.GetOffsets(PoweredMachineTier.Iridium, powered: false).Count;
        var copper = PoweredMachineInterfaceRules.GetOffsets(PoweredMachineTier.Copper, powered: true).Count;
        var steel = PoweredMachineInterfaceRules.GetOffsets(PoweredMachineTier.Steel, powered: true).Count;
        var gold = PoweredMachineInterfaceRules.GetOffsets(PoweredMachineTier.Gold, powered: true).Count;
        var iridium = PoweredMachineInterfaceRules.GetOffsets(PoweredMachineTier.Iridium, powered: true).Count;
        return (prototype == 4 && copper == 8 && steel == 24 && gold == 48 && iridium == 80 && PoweredMachineInterfaceRules.CanRunPoweredAction(1) && !PoweredMachineInterfaceRules.CanRunPoweredAction(0), $"prototype={prototype} powered={copper}/{steel}/{gold}/{iridium}");
    }

    private (bool, string) TestElectricFurnace()
    {
        var fixture = this.CreateFixture(width: 5, height: 3);
        var furnaceTile = fixture.Origin + new Vector2(2, 0);
        var furnace = this.PlaceLinkedMachine(fixture.Location, furnaceTile, "(BC)" + ModItemCatalog.ElectricFurnace, fixture.NetworkId);
        this.RegisterMachine(furnace, fixture.Location, furnaceTile, 0);
        this.SetNetworkEnergy(fixture, 2_000);
        this.InsertNetwork(fixture.NetworkId, "(O)378", 5);
        var before = this.ReadEnergy(fixture.NetworkId).StoredWh;
        this.runtime.RunRouteTickForE2E();
        var after = this.ReadEnergy(fixture.NetworkId).StoredWh;
        var started = furnace.heldObject.Value is not null && furnace.MinutesUntilReady > 0;
        return (started && before - after == ElectricMachineRules.FurnaceWhPerRun && furnace.MinutesUntilReady == ElectricMachineRules.GetPoweredMinutes(30), $"started={started} before={before} after={after} minutes={furnace.MinutesUntilReady}");
    }

    private (bool, string) TestElectricGeodeCrusher()
    {
        var fixture = this.CreateFixture(width: 5, height: 3);
        var crusherTile = fixture.Origin + new Vector2(2, 0);
        var crusher = this.PlaceLinkedMachine(fixture.Location, crusherTile, "(BC)" + ModItemCatalog.ElectricGeodeCrusher, fixture.NetworkId);
        this.RegisterMachine(crusher, fixture.Location, crusherTile, 0);
        this.SetNetworkEnergy(fixture, 2_000);
        this.InsertNetwork(fixture.NetworkId, "(O)535", 1);
        var before = this.ReadEnergy(fixture.NetworkId).StoredWh;
        this.runtime.RunRouteTickForE2E();
        var after = this.ReadEnergy(fixture.NetworkId).StoredWh;
        var started = crusher.heldObject.Value is not null && crusher.MinutesUntilReady > 0;
        return (started && before - after == ElectricMachineRules.GeodeCrusherWhPerRun && crusher.MinutesUntilReady == ElectricMachineRules.GetPoweredMinutes(60), $"started={started} before={before} after={after} minutes={crusher.MinutesUntilReady} output={(crusher.heldObject.Value?.QualifiedItemId ?? "null")}");
    }

    private (bool, string) TestL7NoRegression()
    {
        var powered = PoweredTransferRules.PlanImporterExporter(1_000, 1_000, PoweredMachineTier.Copper, 100_000, 0, 64);
        var prototype = PoweredTransferRules.PlanImporterExporter(1_000, 1_000, PoweredMachineTier.Copper, 0, 0, 64);
        var furnace = ElectricMachineRules.GetPoweredMinutes(30) <= 30;
        var iface = PoweredMachineInterfaceRules.GetOffsets(PoweredMachineTier.Iridium, powered: false).Count == 4;
        return (powered.Mode == PoweredTransferRunMode.Powered && prototype.Mode == PoweredTransferRunMode.Prototype && powered.PlannedItems >= prototype.PlannedItems && furnace && iface, $"powered={powered.Mode}/{powered.PlannedItems} prototype={prototype.Mode}/{prototype.PlannedItems} furnaceNoSlower={furnace} ifacePrototype4={iface}");
    }

    private (bool, string) TestAtomicEnergyPayments()
    {
        var insufficient = PoweredTransferRules.PlanImporterExporter(1_000, 1_000, PoweredMachineTier.Iridium, 1, 0, 64);
        var payment = PoweredTransferRules.CreatePayment(65, 0);
        var refund = PoweredTransferRules.CalculateRefund(65, 64, payment.CreditAfterPrepay);
        return (insufficient.Mode == PoweredTransferRunMode.Prototype && insufficient.PlannedItems == 64 && payment.WhToConsume == 33 && refund.RefundWh == 1, $"insufficient={insufficient.Mode}/{insufficient.PlannedItems} paymentWh={payment.WhToConsume} refundWh={refund.RefundWh} credit={refund.FinalHalfWhCredit}");
    }

    private (bool, string) TestNetworkConnection()
    {
        var fixture = this.CreateFixture(width: 4, height: 3);
        var endpointOk = this.getSvsapApi()!.TryGetLinkedEndpoint(fixture.Location, fixture.CellTile, out var endpoint, out _, out _);
        var energyOk = this.energy.TryGetNetworkEnergy(fixture.NetworkId, out _, out var capacity, out _);
        return (endpointOk && endpoint is not null && endpoint.Active && energyOk && capacity >= 10_000, $"endpoint={endpointOk}/{endpoint?.Active} capacity={capacity}");
    }

    private (bool, string) TestCraftingTerminalSurface()
    {
        var fixture = this.CreateFixture(width: 4, height: 3);
        var inserted = this.InsertNetwork(fixture.NetworkId, "(O)390", 7);
        var extracted = this.getSvsapApi()!.TryExtractItem(fixture.NetworkId, "(O)390", null, 3, out var item, out _, out _);
        return (inserted && extracted && item is not null && item.Stack == 3 && CountItem(fixture.TargetChest, "(O)390") == 4, $"inserted={inserted} extracted={extracted} extractedStack={item?.Stack ?? 0} remaining={CountItem(fixture.TargetChest, "(O)390")}");
    }

    private (bool, string) TestPatternSurface()
    {
        var craftingPatternId = ModItemCatalog.SvsapPrefix + "CraftingPattern";
        var patternItem = ItemRegistry.Create("(O)" + craftingPatternId, 1);
        return (patternItem.QualifiedItemId == "(O)" + craftingPatternId && patternItem.Stack == 1, $"pattern={patternItem.QualifiedItemId} stack={patternItem.Stack}");
    }

    private (bool, string) TestQualityStrategySurface()
    {
        var fixture = this.CreateFixture(width: 5, height: 3);
        var exporterTile = fixture.Origin + new Vector2(2, 0);
        var targetChest = this.PlaceChest(fixture.Location, exporterTile + new Vector2(1, 0));
        var exporter = this.PlaceLinkedMachine(fixture.Location, exporterTile, "(BC)" + ModItemCatalog.PoweredExporterCopper, fixture.NetworkId);
        this.RegisterMachine(exporter, fixture.Location, exporterTile, 0);
        this.runtime.TryConfigurePoweredFilter(exporter, fixture.Location, exporterTile, "(O)390", 1);
        var toggle = this.runtime.TryConfigurePoweredFilter(exporter, fixture.Location, exporterTile, "(O)" + ModItemCatalog.SvsapQualityCard, 1);
        this.SetNetworkEnergy(fixture, 1_000);
        var silver = ItemRegistry.Create("(O)390", 5);
        silver.Quality = 1;
        var gold = ItemRegistry.Create("(O)390", 5);
        gold.Quality = 2;
        this.getSvsapApi()!.TryInsertItem(fixture.NetworkId, silver, out _, out _, out _);
        this.getSvsapApi()!.TryInsertItem(fixture.NetworkId, gold, out _, out _, out _);
        this.runtime.RunRouteTickForE2E();
        var movedQuality = targetChest.Items.FirstOrDefault(item => item is not null)?.Quality ?? -1;
        return (toggle.Success && movedQuality == 2, $"toggle={toggle.Success} movedQuality={movedQuality}");
    }

    private (bool, string) TestCrossLocationNetwork()
    {
        var fixture = this.CreateFixture(width: 4, height: 3);
        var farmhouse = Game1.getLocationFromName("FarmHouse") ?? throw new InvalidOperationException("FarmHouse missing.");
        var chestTile = FindClearBlock(farmhouse, 2, 2);
        var remoteChest = this.PlaceLinkedChest(farmhouse, chestTile, fixture.NetworkId);
        var inserted = this.InsertNetwork(fixture.NetworkId, "(O)388", 11);
        var foundRemote = CountItem(remoteChest, "(O)388") + CountItem(fixture.TargetChest, "(O)388");
        return (inserted && foundRemote == 11, $"farmhouse={farmhouse.NameOrUniqueName} inserted={inserted} totalNetworkChestCount={foundRemote}");
    }

    private (bool, string) TestMachineGuidPickupReplay()
    {
        var fixture = this.CreateFixture(width: 4, height: 3);
        var tile = fixture.Origin + new Vector2(2, 0);
        var farm = this.PlaceMachine(fixture.Location, tile, "(BC)" + ModItemCatalog.CopperFarm);
        var guid = this.RegisterMachine(farm, fixture.Location, tile, 0);
        var item = this.PickUpMachineToPlayer(fixture.Location, tile);
        this.PlaceSpecificMachineItem(fixture.Location, tile, item);
        var replayGuid = TryReadMachineGuid(item, out var parsed) ? parsed : Guid.Empty;
        return (guid == replayGuid && this.repository.Data.Machines.ContainsKey(guid), $"guid={guid:N} replay={replayGuid:N} machines={this.repository.Data.Machines.ContainsKey(guid)}");
    }

    private (bool, string) TestChargedCellPickupReplay()
    {
        var fixture = this.CreateFixture(width: 4, height: 3);
        this.SetNetworkEnergy(fixture, 6_789);
        var before = this.GetState(fixture.CellGuid).StoredWh;
        var item = this.PickUpMachineToPlayer(fixture.Location, fixture.CellTile);
        var heldWh = item.modData.GetValueOrDefault(MachineRegistryService.StoredWhKey);
        this.PlaceSpecificMachineItem(fixture.Location, fixture.CellTile, item);
        var after = this.GetState(fixture.CellGuid).StoredWh;
        return (before == 6_789 && heldWh == "6789" && after == before, $"before={before} heldWh={heldWh} after={after}");
    }

    private (bool, string) TestPersistentContainers()
    {
        var fixture = this.CreateFixture(width: 5, height: 3);
        var chestItem = this.CreateStatefulMachineItem("(BC)" + ModItemCatalog.CopperFarm, out var chestGuid);
        fixture.SourceChest.Items.Add(chestItem);

        var farmhouse = Game1.getLocationFromName("FarmHouse") as FarmHouse;
        var fridge = farmhouse?.fridge.Value;
        var fridgeItem = this.CreateStatefulMachineItem("(BC)" + ModItemCatalog.CopperEnergyCell, out var fridgeGuid, storedWh: 123);
        fridge?.Items.Add(fridgeItem);

        this.registry.ReconcileMissingMachinesOnDayStarted();
        this.registry.ReconcileMissingMachinesOnDayStarted();
        var pending = this.repository.Data.PendingReclaims.SelectMany(reclaim => reclaim.MachineGuids).ToHashSet();
        var ok = !pending.Contains(chestGuid) && fridge is not null && !pending.Contains(fridgeGuid);
        fixture.SourceChest.Items.Remove(chestItem);
        fridge?.Items.Remove(fridgeItem);
        this.repository.Data.Machines.Remove(chestGuid);
        this.repository.Data.Machines.Remove(fridgeGuid);
        return (ok, $"chestGuidPending={pending.Contains(chestGuid)} fridgeAvailable={fridge is not null} fridgeGuidPending={pending.Contains(fridgeGuid)}");
    }

    private (bool, string) TestJunimoGlobalInventoryCoverage()
    {
        var item = this.CreateStatefulMachineItem("(BC)" + ModItemCatalog.CopperEnergyCell, out var guid, storedWh: 456);
        var junimoInventory = Game1.player.team.GetOrCreateGlobalInventory(FarmerTeam.GlobalInventoryId_JunimoChest);
        junimoInventory.Add(item);

        this.registry.ReconcileMissingMachinesOnDayStarted();
        this.registry.ReconcileMissingMachinesOnDayStarted();
        var pending = this.repository.Data.PendingReclaims.SelectMany(reclaim => reclaim.MachineGuids).Contains(guid);

        junimoInventory.Remove(item);
        this.repository.Data.Machines.Remove(guid);
        return (!pending, $"junimoGlobalHeld=True pending={pending}");
    }

    private (bool, string) TestConsumedChargedMachine()
    {
        var item = this.CreateStatefulMachineItem("(BC)" + ModItemCatalog.CopperEnergyCell, out var guid, storedWh: 4_000);
        Game1.player.addItemToInventory(item);
        this.registry.MarkPotentiallyConsumedMachineItem(item);
        this.RemoveFromPlayerInventory(guid);
        var beforeHud = Game1.hudMessages.Count;
        this.registry.ReconcileMissingMachinesOnDayStarted();
        this.registry.ReconcileMissingMachinesOnDayStarted();
        var retired = !this.repository.Data.Machines.ContainsKey(guid);
        var pending = this.repository.Data.PendingReclaims.SelectMany(reclaim => reclaim.MachineGuids).Contains(guid);
        var hud = Game1.hudMessages.Count > beforeHud;
        return (retired && !pending && hud, $"retired={retired} pending={pending} hudAdded={hud}");
    }

    private (bool, string) TestDigitizeGuard()
    {
        var fixture = this.CreateFixture(width: 4, height: 3);
        var item = this.CreateStatefulMachineItem("(BC)" + ModItemCatalog.CopperFarm, out var guid);
        var inserted = this.getSvsapApi()!.TryInsertItem(fixture.NetworkId, item, out var remainder, out _, out _);
        var inChest = fixture.TargetChest.Items.Any(entry => entry is not null
            && entry.modData.GetValueOrDefault(MachineRegistryService.MachineGuidKey) == guid.ToString("N"));
        this.repository.Data.Machines.Remove(guid);
        return (inserted && remainder is null && inChest, $"inserted={inserted} remainder={(remainder?.Stack ?? 0)} inNetworkChest={inChest}");
    }

    private (bool, string) TestStackGuard()
    {
        var stateful = this.CreateStatefulMachineItem("(BC)" + ModItemCatalog.CopperEnergyCell, out var guid, storedWh: 100);
        var plain = ItemRegistry.Create("(BC)" + ModItemCatalog.CopperEnergyCell, 1);
        var rejected = !stateful.canStackWith(plain) && !plain.canStackWith(stateful);
        this.repository.Data.Machines.Remove(guid);
        return (rejected, $"canStatefulWithPlain={stateful.canStackWith(plain)} canPlainWithStateful={plain.canStackWith(stateful)}");
    }

    private (bool, string) TestPendingReclaimLifecycle()
    {
        var fixture = this.CreateFixture(width: 4, height: 3);
        var tile = fixture.Origin + new Vector2(2, 0);
        var farm = this.PlaceMachine(fixture.Location, tile, "(BC)" + ModItemCatalog.CopperFarm);
        var guid = this.RegisterMachine(farm, fixture.Location, tile, 0);
        this.repository.Data.PendingReclaims.Add(new PendingReclaimCrate
        {
            ReclaimId = Guid.NewGuid(),
            Reason = "e2e-force",
            OriginalLocationName = fixture.Location.NameOrUniqueName,
            TileX = (int)tile.X,
            TileY = (int)tile.Y,
            MachineGuids = { guid }
        });
        this.registry.TryRegisterPlacedMachine(farm, fixture.Location, tile);
        var pending = this.repository.Data.PendingReclaims.SelectMany(reclaim => reclaim.MachineGuids).Contains(guid);
        return (!pending, $"guid={guid:N} pendingAfterRegister={pending}");
    }

    private (bool, string) TestClaimGate()
    {
        var item = this.CreateStatefulMachineItem("(BC)" + ModItemCatalog.CopperFarm, out var guid);
        this.repository.Data.PendingReclaims.Add(new PendingReclaimCrate
        {
            ReclaimId = Guid.NewGuid(),
            Reason = "unconfirmed-e2e",
            OriginalLocationName = "Farm",
            TileX = 0,
            TileY = 0,
            MachineGuids = { guid }
        });
        var normal = this.registry.TryClaimPendingReclaims(Game1.player, out var normalMachines, out _, out _);
        var force = this.registry.TryClaimPendingReclaims(Game1.player, includeUnconfirmed: true, out var forceMachines, out _, out _);
        this.repository.Data.Machines.Remove(guid);
        _ = item;
        return (!normal && normalMachines == 0 && force && forceMachines == 1, $"normal={normal}/{normalMachines} force={force}/{forceMachines}");
    }

    private (bool, string) TestDemolishHeldExclusion()
    {
        var item = this.CreateStatefulMachineItem("(BC)" + ModItemCatalog.CopperFarm, out var guid);
        Game1.player.addItemToInventory(item);
        this.repository.Data.PendingReclaims.Add(new PendingReclaimCrate
        {
            ReclaimId = Guid.NewGuid(),
            Reason = "building-demolish",
            OriginalLocationName = "FarmHouse",
            TileX = (int)Game1.player.Tile.X,
            TileY = (int)Game1.player.Tile.Y,
            MachineGuids = { guid }
        });
        var claim = this.registry.TryClaimPendingReclaims(Game1.player, includeUnconfirmed: true, out var machines, out _, out _);
        this.RemoveFromPlayerInventory(guid);
        this.repository.Data.Machines.Remove(guid);
        return (!claim && machines == 0, $"claim={claim} machines={machines}");
    }

    private (bool, string) TestRepositoryRoundTrip()
    {
        var item = this.CreateStatefulMachineItem("(BC)" + ModItemCatalog.IridiumEnergyCell, out var guid, storedWh: 123_456);
        this.repository.Save();
        this.repository.Load();
        var exists = this.repository.Data.Machines.TryGetValue(guid, out var state);
        this.repository.Data.Machines.Remove(guid);
        _ = item;
        return (exists && state!.StoredWh == 123_456 && state.CapacityWh == 640_000, $"exists={exists} stored={state?.StoredWh ?? -1} capacity={state?.CapacityWh ?? -1}");
    }

    private (bool, string) TestFullNetworkStress()
    {
        var fixture = this.CreateFixture(width: 12, height: 6);
        for (var i = 0; i < 12; i++)
        {
            var tile = fixture.Origin + new Vector2(i % 6, 2 + i / 6);
            var cell = this.PlaceLinkedMachine(fixture.Location, tile, "(BC)" + ModItemCatalog.CopperEnergyCell, fixture.NetworkId);
            this.RegisterMachine(cell, fixture.Location, tile, i * 100);
        }

        var deposit = this.energy.TryDepositWh(fixture.NetworkId, 50_000, ModItemCatalog.UniqueId, "stress", out var accepted, out _, out _);
        var read = this.ReadEnergy(fixture.NetworkId);
        return (deposit && accepted == 50_000 && read.StoredWh >= 50_000 && read.CapacityWh >= 130_000, $"accepted={accepted} stored={read.StoredWh} capacity={read.CapacityWh} machines={this.registry.MachinesByGuid.Count}");
    }

    private (bool, string) TestZeroEnergyDegrade()
    {
        var moved = this.RunImporterScenario("(BC)" + ModItemCatalog.PoweredImporterCopper, 64, powered: false);
        var plan = PoweredTransferRules.PlanImporterExporter(100, 100, PoweredMachineTier.Copper, 0, 0, 64);
        return (moved >= 64 && plan.Mode == PoweredTransferRunMode.Prototype, $"moved={moved} plan={plan.Mode}/{plan.PlannedItems}");
    }

    private (bool, string) TestEnergyBoundaries()
    {
        var fixture = this.CreateFixture(width: 4, height: 3);
        var capacity = this.ReadEnergy(fixture.NetworkId).CapacityWh;
        var deposit = this.energy.TryDepositWh(fixture.NetworkId, capacity + 1, ModItemCatalog.UniqueId, "boundary-fill", out var accepted, out _, out _);
        var readFull = this.ReadEnergy(fixture.NetworkId);
        var consume = this.energy.TryConsumeWh(fixture.NetworkId, capacity + 1, ModItemCatalog.UniqueId, "boundary-consume", false, out var consumed, out _, out _);
        var consumeExact = this.energy.TryConsumeWh(fixture.NetworkId, capacity, ModItemCatalog.UniqueId, "boundary-consume-exact", false, out var consumedExact, out _, out _);
        return (deposit && accepted == capacity && readFull.StoredWh == capacity && !consume && consumed == 0 && consumeExact && consumedExact == capacity, $"capacity={capacity} accepted={accepted} full={readFull.StoredWh} overConsume={consume}/{consumed} exact={consumeExact}/{consumedExact}");
    }

    private (bool, string) TestRapidPickupReplay()
    {
        var fixture = this.CreateFixture(width: 4, height: 3);
        var guid = fixture.CellGuid;
        for (var i = 0; i < 10; i++)
        {
            var item = this.PickUpMachineToPlayer(fixture.Location, fixture.CellTile);
            this.PlaceSpecificMachineItem(fixture.Location, fixture.CellTile, item);
        }

        var live = this.registry.MachinesByGuid.ContainsKey(guid) && this.repository.Data.Machines.ContainsKey(guid);
        var duplicateStates = this.repository.Data.Machines.Keys.Count(id => id == guid);
        return (live && duplicateStates == 1, $"live={live} duplicateStates={duplicateStates} guid={guid:N}");
    }

    private (bool, string) TestInterruptedSettlementRoundTrip()
    {
        var fixture = this.CreateFixture(width: 5, height: 3);
        var generatorTile = fixture.Origin + new Vector2(2, 0);
        var generator = this.PlaceLinkedMachine(fixture.Location, generatorTile, "(BC)" + ModItemCatalog.CarbonGenerator, fixture.NetworkId);
        var guid = this.RegisterMachine(generator, fixture.Location, generatorTile, 0);
        this.runtime.TryFuelCarbonGenerator(generator, fixture.Location, generatorTile, "(O)382");
        this.repository.Save();
        this.repository.Load();
        this.registry.RebuildCache();
        var exists = this.repository.Data.Machines.TryGetValue(guid, out var state);
        return (exists && state!.StoredWh == 350, $"exists={exists} buffer={state?.StoredWh ?? -1}");
    }

    private int RunImporterScenario(string importerQualifiedItemId, int expected, bool powered)
    {
        var fixture = this.CreateFixture(width: 6, height: 3);
        var importerTile = fixture.Origin + new Vector2(2, 0);
        var sourceTile = importerTile + new Vector2(1, 0);
        var importer = this.PlaceLinkedMachine(fixture.Location, importerTile, importerQualifiedItemId, fixture.NetworkId);
        this.RegisterMachine(importer, fixture.Location, importerTile, 0);
        var sourceChest = this.PlaceChest(fixture.Location, sourceTile);
        AddItemsToChest(sourceChest, "(O)390", Math.Max(expected + 10, 1500));
        this.SetNetworkEnergy(fixture, powered ? 100_000 : 0);
        this.runtime.RunRouteTickForE2E();
        return CountItem(fixture.TargetChest, "(O)390");
    }

    private int RunExporterScenario(string exporterQualifiedItemId, int expected)
    {
        var fixture = this.CreateFixture(width: 6, height: 3);
        var exporterTile = fixture.Origin + new Vector2(2, 0);
        var targetChest = this.PlaceChest(fixture.Location, exporterTile + new Vector2(1, 0));
        var exporter = this.PlaceLinkedMachine(fixture.Location, exporterTile, exporterQualifiedItemId, fixture.NetworkId);
        this.RegisterMachine(exporter, fixture.Location, exporterTile, 0);
        this.runtime.TryConfigurePoweredFilter(exporter, fixture.Location, exporterTile, "(O)390", 1);
        this.SetNetworkEnergy(fixture, 100_000);
        this.InsertNetwork(fixture.NetworkId, "(O)390", Math.Max(expected + 10, 1500));
        this.runtime.RunRouteTickForE2E();
        return CountItem(targetChest, "(O)390");
    }

    private Fixture CreateFixture(int width, int height)
    {
        var location = this.GetFarm();
        var origin = FindClearBlock(location, width, height);
        ClearBlock(location, origin, width, height);
        var networkId = Guid.NewGuid();
        var coreTile = origin;
        var targetChestTile = origin + new Vector2(1, 0);
        var cellTile = origin + new Vector2(0, 1);
        this.PlaceLinkedMachine(location, coreTile, "(BC)" + ModItemCatalog.SvsapUniqueId + ".NetworkCore", networkId);
        var targetChest = this.PlaceLinkedChest(location, targetChestTile, networkId);
        var sourceChest = this.PlaceChest(location, origin + new Vector2(1, 1));
        var cell = this.PlaceLinkedMachine(location, cellTile, "(BC)" + ModItemCatalog.IridiumEnergyCell, networkId);
        var cellGuid = this.RegisterMachine(cell, location, cellTile, 0);
        return new Fixture(location, origin, networkId, targetChest, sourceChest, cell, cellTile, cellGuid);
    }

    private SObject PlaceLinkedMachine(GameLocation location, Vector2 tile, string qualifiedItemId, Guid networkId)
    {
        var obj = this.PlaceMachine(location, tile, qualifiedItemId);
        this.RegisterSvsapEndpoint(obj, location, tile, networkId, GetSvsapEndpointType(obj));
        return obj;
    }

    private SObject PlaceMachine(GameLocation location, Vector2 tile, string qualifiedItemId)
    {
        if (ItemRegistry.Create(qualifiedItemId, 1) is not SObject obj)
            throw new InvalidOperationException($"Could not create placeable object {qualifiedItemId}.");

        obj.TileLocation = tile;
        location.Objects[tile] = obj;
        return obj;
    }

    private Chest PlaceLinkedChest(GameLocation location, Vector2 tile, Guid networkId)
    {
        var chest = new Chest(CreateEmptyChestSlots(), tile, false, 0, false);
        this.RegisterSvsapEndpoint(chest, location, tile, networkId, "Chest");
        location.Objects[tile] = chest;
        return chest;
    }

    private Chest PlaceChest(GameLocation location, Vector2 tile)
    {
        var chest = new Chest(CreateEmptyChestSlots(), tile, false, 0, false);
        location.Objects[tile] = chest;
        return chest;
    }

    private Guid RegisterMachine(SObject obj, GameLocation location, Vector2 tile, long storedWh)
    {
        this.registry.TryRegisterPlacedMachine(obj, location, tile);
        if (!TryReadMachineGuid(obj, out var guid))
            throw new InvalidOperationException($"Placed machine {obj.QualifiedItemId} did not receive a MachineGuid.");

        var state = this.GetState(guid);
        state.StoredWh = storedWh;
        obj.modData[MachineRegistryService.StoredWhKey] = storedWh.ToString();
        this.repository.Save();
        return guid;
    }

    private void SetNetworkEnergy(Fixture fixture, long storedWh)
    {
        var state = this.GetState(fixture.CellGuid);
        state.StoredWh = Math.Clamp(storedWh, 0, state.CapacityWh);
        fixture.CellObject.modData[MachineRegistryService.StoredWhKey] = state.StoredWh.ToString();
        this.repository.Save();
    }

    private (long StoredWh, long CapacityWh) ReadEnergy(Guid networkId)
    {
        this.energy.TryGetNetworkEnergy(networkId, out var storedWh, out var capacityWh, out _);
        return (storedWh, capacityWh);
    }

    private bool InsertNetwork(Guid networkId, string qualifiedItemId, int count)
    {
        var remaining = Math.Max(0, count);
        while (remaining > 0)
        {
            var item = ItemRegistry.Create(qualifiedItemId);
            var chunkCount = Math.Min(remaining, Math.Max(1, item.maximumStackSize()));
            item.Stack = chunkCount;
            if (!this.getSvsapApi()!.TryInsertItem(networkId, item, out var remainder, out _, out _)
                || (remainder is not null && remainder.Stack > 0))
            {
                return false;
            }

            remaining -= chunkCount;
        }

        return true;
    }

    private static void AddItemsToChest(Chest chest, string qualifiedItemId, int count)
    {
        var remaining = Math.Max(0, count);
        while (remaining > 0)
        {
            var item = ItemRegistry.Create(qualifiedItemId);
            item.Stack = Math.Min(remaining, Math.Max(1, item.maximumStackSize()));
            chest.Items.Add(item);
            remaining -= item.Stack;
        }
    }

    private Item CreateStatefulMachineItem(string qualifiedItemId, out Guid guid, long storedWh = 0)
    {
        var item = ItemRegistry.Create(qualifiedItemId, 1);
        guid = Guid.NewGuid();
        item.modData[MachineRegistryService.MachineGuidKey] = guid.ToString("N");
        if (storedWh > 0)
            item.modData[MachineRegistryService.StoredWhKey] = storedWh.ToString();

        var state = this.repository.GetOrCreate(guid);
        state.QualifiedItemId = qualifiedItemId;
        state.MachineGuid = guid;
        state.StoredWh = storedWh;
        state.CapacityWh = qualifiedItemId switch
        {
            "(BC)" + ModItemCatalog.CopperEnergyCell => 10_000,
            "(BC)" + ModItemCatalog.SteelEnergyCell => 40_000,
            "(BC)" + ModItemCatalog.GoldEnergyCell => 160_000,
            "(BC)" + ModItemCatalog.IridiumEnergyCell => 640_000,
            _ => 0
        };
        state.MachineType = ModItemCatalog.GetLocalKey(item.ItemId);
        this.repository.Save();
        return item;
    }

    private Item PickUpMachineToPlayer(GameLocation location, Vector2 tile)
    {
        if (!location.Objects.TryGetValue(tile, out var obj))
            throw new InvalidOperationException($"No machine at {FormatTile(tile)} to pick up.");

        location.Objects.Remove(tile);
        EnsurePlayerInventorySpace();
        var rejected = Game1.player.addItemToInventory(obj);
        if (rejected is not null)
            throw new InvalidOperationException($"Could not add picked machine to inventory: {rejected.QualifiedItemId}");

        return obj;
    }

    private void PlaceSpecificMachineItem(GameLocation location, Vector2 tile, Item item)
    {
        if (item is not SObject obj)
            throw new InvalidOperationException($"Held item is not placeable SObject: {item.QualifiedItemId}");

        location.Objects[tile] = obj;
        this.registry.TryRegisterPlacedMachine(obj, location, tile);
    }

    private void RemoveFromPlayerInventory(Guid guid)
    {
        for (var i = 0; i < Game1.player.Items.Count; i++)
        {
            var item = Game1.player.Items[i];
            if (item is not null && item.modData.GetValueOrDefault(MachineRegistryService.MachineGuidKey) == guid.ToString("N"))
            {
                Game1.player.Items[i] = null;
                return;
            }
        }
    }

    private static void EnsurePlayerInventorySpace()
    {
        if (Game1.player.Items.Any(item => item is null))
            return;

        Game1.player.Items[0] = null;
    }

    private MachineState GetState(Guid guid)
    {
        if (!this.repository.Data.Machines.TryGetValue(guid, out var state))
            throw new InvalidOperationException($"Missing machine state {guid:N}.");

        return state;
    }

    private GameLocation GetFarm()
    {
        return Game1.getLocationFromName("Farm") ?? Game1.currentLocation ?? throw new InvalidOperationException("Farm location is not available.");
    }

    private static List<Item> CreateEmptyChestSlots()
    {
        var items = new List<Item>(36);
        for (var i = 0; i < 36; i++)
            items.Add(null!);

        return items;
    }

    private void RegisterSvsapEndpoint(Item item, GameLocation location, Vector2 tile, Guid networkId, string endpointTypeName)
    {
        var endpointId = Guid.NewGuid();
        item.modData[SvsapNetworkIdKey] = networkId.ToString("N");
        item.modData[SvsapEndpointIdKey] = endpointId.ToString("N");

        var repository = this.GetSvsapNetworkRepository();
        var repositoryType = repository.GetType();
        var network = repositoryType.GetMethod("GetOrCreateNetwork")?.Invoke(repository, new object?[] { networkId })
            ?? throw new InvalidOperationException("Could not create SVSAP test network.");
        var assembly = network.GetType().Assembly;
        var endpointType = assembly.GetType("SVSAP.Models.NetworkEndpoint")
            ?? throw new InvalidOperationException("SVSAP.Models.NetworkEndpoint type was not found.");
        var endpointKindType = assembly.GetType("SVSAP.Models.EndpointType")
            ?? throw new InvalidOperationException("SVSAP.Models.EndpointType type was not found.");
        var endpoint = Activator.CreateInstance(endpointType)
            ?? throw new InvalidOperationException("Could not instantiate SVSAP network endpoint.");

        SetProperty(endpoint, "EndpointId", endpointId);
        SetProperty(endpoint, "LocationName", location.NameOrUniqueName);
        SetProperty(endpoint, "TileX", tile.X);
        SetProperty(endpoint, "TileY", tile.Y);
        SetProperty(endpoint, "Type", Enum.Parse(endpointKindType, endpointTypeName));
        SetProperty(endpoint, "Active", true);
        SetProperty(endpoint, "Priority", 0);

        repositoryType.GetMethod("UpsertEndpoint")?.Invoke(repository, new[] { networkId, endpoint });
    }

    private object GetSvsapNetworkRepository()
    {
        if (this.svsapNetworkRepository is not null)
            return this.svsapNetworkRepository;

        var modInfo = this.helper.ModRegistry.Get(ModItemCatalog.SvsapUniqueId)
            ?? throw new InvalidOperationException("Koizumi.SVSAP mod metadata was not found.");
        var mod = modInfo.GetType().GetProperty("Mod")?.GetValue(modInfo)
            ?? throw new InvalidOperationException("Koizumi.SVSAP mod instance was not available.");
        var field = mod.GetType().GetField("networkRepository", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Koizumi.SVSAP networkRepository field was not found.");
        this.svsapNetworkRepository = field.GetValue(mod)
            ?? throw new InvalidOperationException("Koizumi.SVSAP networkRepository was null.");
        return this.svsapNetworkRepository;
    }

    private static string GetSvsapEndpointType(Item item)
    {
        if (item.QualifiedItemId == "(BC)" + ModItemCatalog.SvsapUniqueId + ".NetworkCore")
            return "NetworkCore";

        return "Machine";
    }

    private static void SetProperty(object target, string name, object value)
    {
        var property = target.GetType().GetProperty(name)
            ?? throw new InvalidOperationException($"Property {name} was not found on {target.GetType().FullName}.");
        property.SetValue(target, value);
    }

    private static bool TryReadMachineGuid(Item item, out Guid guid)
    {
        return Guid.TryParse(item.modData.GetValueOrDefault(MachineRegistryService.MachineGuidKey), out guid);
    }

    private static int CountItem(Chest chest, string qualifiedItemId)
    {
        return chest.Items.Sum(item => item is not null && item.QualifiedItemId == qualifiedItemId ? item.Stack : 0);
    }

    private static Vector2 FindClearBlock(GameLocation location, int width, int height)
    {
        for (var y = 8; y < 80; y++)
        {
            for (var x = 8; x < 100; x++)
            {
                var origin = new Vector2(x, y);
                if (BlockIsClear(location, origin, width, height))
                    return origin;
            }
        }

        throw new InvalidOperationException($"Could not find clear {width}x{height} block in {location.NameOrUniqueName}.");
    }

    private static bool BlockIsClear(GameLocation location, Vector2 origin, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (location.Objects.ContainsKey(origin + new Vector2(x, y)))
                    return false;
            }
        }

        return true;
    }

    private static void ClearBlock(GameLocation location, Vector2 origin, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                location.Objects.Remove(origin + new Vector2(x, y));
        }
    }

    private void Record(string id, bool pass, string evidence)
    {
        this.results.RemoveAll(result => result.Id == id);
        this.results.Add(new E2EResult(id, pass, evidence));
        this.monitor.Log($"SVSAPME_FULL_E2E {id} {(pass ? "PASS" : "FAIL")} {evidence}", pass ? LogLevel.Info : LogLevel.Error);
    }

    private void WriteResults(string fileName)
    {
        this.WritePayload(fileName, new
        {
            version = this.versionLabel,
            pass = this.results.All(result => result.Pass),
            passed = this.results.Count(result => result.Pass),
            total = this.results.Count,
            results = this.results
        });
    }

    private void WritePayload(string fileName, object payload)
    {
        if (string.IsNullOrWhiteSpace(this.outputDir))
            return;

        File.WriteAllText(Path.Combine(this.outputDir, fileName), JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static string FormatTile(Vector2 tile)
    {
        return $"{tile.X:0},{tile.Y:0}";
    }

    private sealed record E2EResult(string Id, bool Pass, string Evidence);

    private sealed record Fixture(
        GameLocation Location,
        Vector2 Origin,
        Guid NetworkId,
        Chest TargetChest,
        Chest SourceChest,
        SObject CellObject,
        Vector2 CellTile,
        Guid CellGuid);
}
#endif
