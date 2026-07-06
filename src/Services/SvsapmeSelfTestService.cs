using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using Koizumi.SVSAPME.Api;
using SVSAPME.Content;
using SVSAPME.Models;

namespace SVSAPME.Services;

internal sealed class SvsapmeSelfTestService
{
    private readonly IMonitor monitor;

    public SvsapmeSelfTestService(IMonitor monitor)
    {
        this.monitor = monitor;
    }

    public void RunCommand(string command, string[] args)
    {
        IReadOnlyList<string> requested = args.Length == 0
            ? ImplementedCases
            : args.Select(arg => arg.Trim()).Where(arg => arg.Length > 0).ToArray();

        var failures = new List<string>();
        foreach (var testCase in requested)
        {
            var caseFailures = RunCase(testCase);
            if (caseFailures.Count == 0)
            {
                this.monitor.Log($"PASS {testCase}", LogLevel.Info);
                continue;
            }

            foreach (var failure in caseFailures)
            {
                this.monitor.Log($"FAIL {testCase}: {failure}", LogLevel.Error);
                failures.Add($"{testCase}: {failure}");
            }
        }

        if (failures.Count > 0)
            this.monitor.Log($"SVSAPME selftest completed with {failures.Count} failure(s).", LogLevel.Error);
        else
            this.monitor.Log($"SVSAPME selftest completed: {requested.Count} implemented case(s) passed.", LogLevel.Info);
    }

    private static IReadOnlyList<string> RunCase(string testCase)
    {
        return testCase switch
        {
            "wh-roundtrip" => TestWhRoundtrip(),
            "tier-table" => EnergyTierTable.Validate(),
            "content-table" => TestContentTable(),
            "api-shape" => TestApiShape(),
            "config-surface" => TestConfigSurface(),
            "cell-stack-guard" => TestCellStackGuard(),
            "machine-guid-reconcile" => TestMachineGuidReconcile(),
            "orphan-reclaim" => TestOrphanReclaim(),
            "claim-force-gate" => TestClaimForceGate(),
            "consumed-charged-retire" => TestConsumedChargedRetire(),
            "disassembly-energy-policy" => TestDisassemblyEnergyPolicy(),
            "missing-machine-reclaim" => TestMissingMachineReclaim(),
            "multiplayer-protocol" => TestMultiplayerProtocol(),
            "action-idempotent" => TestActionIdempotent(),
            "escrow-restore" => TestEscrowRestore(),
            "host-action-dispatch" => TestHostActionDispatch(),
            "energy-production-rules" => TestEnergyProductionRules(),
            "synth-atomic" => TestSynthAtomicRules(),
            "farm-crop-set" => TestFarmCropSet(),
            "farm-power-freeze" => TestFarmPowerFreeze(),
            "farm-daily-progress" => TestFarmDailyProgress(),
            "farm-single-crop-budget" => TestFarmEnergyBudgetRules(),
            "farm-module-economy" => TestFarmModuleEconomy(),
            "farm-fertilizer-quality" => TestFarmFertilizerQuality(),
            "farm-locked-output" => TestFarmLockedOutput(),
            "daily-order-storage-gate" => TestDailyOrderStorageGate(),
            "location-cache-full-enum" => TestLocationCacheFullEnum(),
            "building-demolish-reclaim" => TestBuildingDemolishReclaim(),
            "powered-prescan-refund" => TestPoweredPrescanRefund(),
            "powered-degrade-parity" => TestPoweredDegradeParity(),
            "powered-interface-range" => TestPoweredInterfaceRange(),
            "battery-discharge-gate" => TestBatteryDischargeGate(),
            "electric-machine-rules" => TestElectricMachineRules(),
            "b10-parity" => SvsapmeBalanceTable.ValidateParity(),
            "no-arbitrage-audit" => SvsapmeBalanceTable.ValidateNoArbitrage(),
            _ => new[] { $"case is not implemented in the SVSAPME selftest harness: {testCase}" }
        };
    }

    private static IReadOnlyList<string> TestWhRoundtrip()
    {
        var failures = new List<string>();

        var tenKwh = EnergyWh.FromKWh(10.00m);
        if (tenKwh.Value != 10_000)
            failures.Add("10.00 kWh must equal 10000 Wh");

        var fractional = EnergyWh.FromKWh(0.35m);
        if (fractional.Value != 350)
            failures.Add("0.35 kWh must equal 350 Wh");

        if (new EnergyWh(12_345).KWh != 12.345m)
            failures.Add("Wh to kWh projection must preserve decimal precision");

        return failures;
    }

    private static IReadOnlyList<string> TestApiShape()
    {
        var failures = new List<string>();
        var apiType = typeof(ISvsapmeEnergyApi);
        if (!typeof(SvsapmeEnergyApi).IsPublic)
            failures.Add("SvsapmeEnergyApi adapter type must be public so SMAPI can expose the mod API");

        if (apiType.Namespace != "Koizumi.SVSAPME.Api")
            failures.Add("ISvsapmeEnergyApi must live in Koizumi.SVSAPME.Api");

        if (apiType.GetProperty(nameof(ISvsapmeEnergyApi.ApiVersion))?.PropertyType != typeof(int))
            failures.Add("ISvsapmeEnergyApi.ApiVersion must be an int property");

        if (apiType.GetProperty(nameof(ISvsapmeEnergyApi.IsHostAuthority))?.PropertyType != typeof(bool))
            failures.Add("ISvsapmeEnergyApi.IsHostAuthority must be a bool property");

        foreach (var (name, expected) in new[]
                 {
                     (nameof(SvsapmeEnergyErrorCode.None), 0),
                     (nameof(SvsapmeEnergyErrorCode.NotHost), 1),
                     (nameof(SvsapmeEnergyErrorCode.NetworkUnknown), 2),
                     (nameof(SvsapmeEnergyErrorCode.InsufficientEnergy), 3),
                     (nameof(SvsapmeEnergyErrorCode.StorageFull), 4),
                     (nameof(SvsapmeEnergyErrorCode.SubsystemDisabled), 5),
                     (nameof(SvsapmeEnergyErrorCode.InternalError), 6)
                 })
        {
            if (!Enum.TryParse<SvsapmeEnergyErrorCode>(name, out var parsed) || (int)parsed != expected)
                failures.Add($"SvsapmeEnergyErrorCode.{name} must equal {expected}");
        }

        var methodNames = apiType.GetMethods().Select(method => method.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[] { "get_ApiVersion", "get_IsHostAuthority", "TryGetNetworkEnergy", "TryDepositWh", "TryConsumeWh" })
        {
            if (!methodNames.Contains(required))
                failures.Add($"ISvsapmeEnergyApi is missing {required}");
        }

        return failures;
    }

    private static IReadOnlyList<string> TestConfigSurface()
    {
        var failures = new List<string>();
        var actual = typeof(ModConfig).GetProperties().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var expected = new[]
        {
            nameof(ModConfig.AllowBatteryDischarge),
            nameof(ModConfig.BatteryDischargeEfficiency),
            nameof(ModConfig.BatterySynthesisEnergyMultiplier),
            nameof(ModConfig.DebugEnergyRouting),
            nameof(ModConfig.DetailedEnergyLogs),
            nameof(ModConfig.EnableAutomaticFarmOutputToNetwork),
            nameof(ModConfig.EnableBatterySynthesizer),
            nameof(ModConfig.EnableCarbonGenerator),
            nameof(ModConfig.EnableElectricMachines),
            nameof(ModConfig.EnableExtendedGeneratorFuels),
            nameof(ModConfig.EnableLightningCapacitor),
            nameof(ModConfig.EnablePoweredTransfer),
            nameof(ModConfig.EnableSingleBlockFarm),
            nameof(ModConfig.EnableSolarNetworkPanel),
            nameof(ModConfig.EnergyTickInterval),
            nameof(ModConfig.FarmEnergyCostMultiplier),
            nameof(ModConfig.GeneratorMultiplier),
            nameof(ModConfig.MachineEnergyCostMultiplier),
            nameof(ModConfig.RecipeCostMode)
        }.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            failures.Add($"ModConfig property surface drifted. actual=[{string.Join(",", actual)}]");

        if (actual.Contains("Language", StringComparer.Ordinal))
            failures.Add("ModConfig must not include Language; localization belongs to SMAPI i18n");

        return failures;
    }

    private static IReadOnlyList<string> TestMultiplayerProtocol()
    {
        var failures = new List<string>();
        var messageTypes = new[]
        {
            SvsapmeMultiplayerMessageTypes.MachineSnapshotRequest,
            SvsapmeMultiplayerMessageTypes.MachineSnapshotResponse,
            SvsapmeMultiplayerMessageTypes.MachineActionRequest,
            SvsapmeMultiplayerMessageTypes.MachineActionResponse,
            SvsapmeMultiplayerMessageTypes.MachineItemMovementReport,
            SvsapmeMultiplayerMessageTypes.EnergyDebugRequest,
            SvsapmeMultiplayerMessageTypes.EnergyDebugResponse
        };

        if (messageTypes.Any(string.IsNullOrWhiteSpace) || messageTypes.Distinct(StringComparer.Ordinal).Count() != 7)
            failures.Add("SVSAPME multiplayer message set must contain seven distinct message names");

        if (messageTypes.Any(type => !type.StartsWith("Svsapme", StringComparison.Ordinal)))
            failures.Add("SVSAPME multiplayer messages must use SVSAPME names and never forge Koizumi.SVSAP messages");

        var cache = new MultiplayerTransactionCache<SvsapmeMachineActionResponse>(limit: 2);
        var tx1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var tx2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var tx3 = Guid.Parse("33333333-3333-3333-3333-333333333333");
        cache.Remember(7, tx1, new SvsapmeMachineActionResponse { TransactionId = tx1, Message = "first" });
        cache.Remember(7, tx2, new SvsapmeMachineActionResponse { TransactionId = tx2, Message = "second" });
        if (!cache.TryGet(7, tx1, out var replay) || replay.Message != "first")
            failures.Add("multiplayer action cache must replay duplicate transaction responses");

        cache.Remember(7, tx3, new SvsapmeMachineActionResponse { TransactionId = tx3, Message = "third" });
        if (cache.Count != 2)
            failures.Add("multiplayer action cache must enforce its response limit");

        if (cache.TryGet(7, tx1, out _))
            failures.Add("multiplayer action cache must evict the oldest transaction after exceeding the limit");

        cache.Clear();
        if (cache.Count != 0)
            failures.Add("multiplayer action cache clear must drop all cached responses");

        return failures;
    }

    private static IReadOnlyList<string> TestActionIdempotent()
    {
        var failures = new List<string>();
        var cache = new MultiplayerTransactionCache<SvsapmeMachineActionResponse>(limit: 4);
        var tx1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var tx2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var tx3 = Guid.Parse("33333333-3333-3333-3333-333333333333");
        cache.Remember(7, tx1, new SvsapmeMachineActionResponse { TransactionId = tx1, Message = "first" });
        cache.Remember(7, tx2, new SvsapmeMachineActionResponse { TransactionId = tx2, Message = "second" });
        if (!cache.TryGet(7, tx1, out var replay) || replay.Message != "first")
            failures.Add("action cache must replay duplicate transaction responses");

        cache.Remember(8, tx1, new SvsapmeMachineActionResponse { TransactionId = tx1, Message = "other-player" });
        if (!cache.TryGet(7, tx1, out replay) || replay.Message != "first")
            failures.Add("action cache must key responses by player id and transaction id");

        var limited = new MultiplayerTransactionCache<SvsapmeMachineActionResponse>(limit: 2);
        limited.Remember(7, tx1, new SvsapmeMachineActionResponse { TransactionId = tx1, Message = "first" });
        limited.Remember(7, tx2, new SvsapmeMachineActionResponse { TransactionId = tx2, Message = "second" });
        limited.Remember(7, tx3, new SvsapmeMachineActionResponse { TransactionId = tx3, Message = "third" });
        if (limited.Count != 2)
            failures.Add("action cache must enforce its response limit");

        if (limited.TryGet(7, tx1, out _))
            failures.Add("action cache must evict the oldest transaction after exceeding the limit");

        limited.Clear();
        if (limited.Count != 0)
            failures.Add("action cache clear must drop all cached responses");

        return failures;
    }

    private static IReadOnlyList<string> TestEscrowRestore()
    {
        var failures = new List<string>();
        if (!SvsapmeActionEscrowRules.ActionMayEscrowHeldItem(SvsapmeMachineActionKind.ConfigurePoweredFilter)
            || !SvsapmeActionEscrowRules.ActionMayEscrowHeldItem(SvsapmeMachineActionKind.LoadFarmSeed)
            || !SvsapmeActionEscrowRules.ActionMayEscrowHeldItem(SvsapmeMachineActionKind.LoadFarmFertilizer)
            || !SvsapmeActionEscrowRules.ActionMayEscrowHeldItem(SvsapmeMachineActionKind.InstallFarmModule)
            || !SvsapmeActionEscrowRules.ActionMayEscrowHeldItem(SvsapmeMachineActionKind.FuelCarbonGenerator))
        {
            failures.Add("item-bearing SVSAPME machine actions must be escrow candidates");
        }

        if (SvsapmeActionEscrowRules.ActionMayEscrowHeldItem(SvsapmeMachineActionKind.None))
            failures.Add("no-op machine action must not escrow held items");

        if (!SvsapmeActionEscrowRules.ShouldRestoreOnResponse(success: false, consumeEscrowedItem: false))
            failures.Add("failed action responses must restore escrowed items");

        if (!SvsapmeActionEscrowRules.ShouldRestoreOnResponse(success: true, consumeEscrowedItem: false))
            failures.Add("successful non-consuming responses must restore escrowed items");

        if (SvsapmeActionEscrowRules.ShouldRestoreOnResponse(success: true, consumeEscrowedItem: true))
            failures.Add("successful consuming responses must not restore escrowed items");

        var restored = new List<string>();
        var store = new MultiplayerEscrowStore<string>();
        var failTx = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var nonConsumeTx = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var consumeTx = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var disconnectTx1 = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var disconnectTx2 = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

        store.Track(failTx, "seed");
        store.Resolve(failTx, restore: true, restored.Add);
        if (!restored.SequenceEqual(new[] { "seed" }, StringComparer.Ordinal))
            failures.Add("failed escrow resolution must restore the captured item");

        store.Track(nonConsumeTx, "filter");
        store.Resolve(nonConsumeTx, restore: true, restored.Add);
        if (!restored.SequenceEqual(new[] { "seed", "filter" }, StringComparer.Ordinal))
            failures.Add("non-consuming success must restore the captured item");

        store.Track(consumeTx, "coal");
        store.Resolve(consumeTx, restore: false, restored.Add);
        if (!restored.SequenceEqual(new[] { "seed", "filter" }, StringComparer.Ordinal))
            failures.Add("consuming success must not restore the captured item");

        store.Track(disconnectTx1, "blueberry");
        store.Track(disconnectTx2, "coffee");
        var restoredCount = store.RestoreAll(restored.Add);
        if (restoredCount != 2 || store.Count != 0)
            failures.Add("host disconnect restore must return every pending escrow and clear the store");

        if (!restored.TakeLast(2).SequenceEqual(new[] { "blueberry", "coffee" }, StringComparer.Ordinal))
            failures.Add("host disconnect restore must preserve all pending escrow payloads");

        if (typeof(SvsapmeMachineActionResponse).GetProperty(nameof(SvsapmeMachineActionResponse.ConsumeEscrowedItem))?.PropertyType != typeof(bool))
            failures.Add("machine action responses must carry the authoritative consume/restore escrow decision");

        return failures;
    }

    private static IReadOnlyList<string> TestHostActionDispatch()
    {
        var failures = new List<string>();
        var nonPublicInstance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var configure = typeof(MachineRuntimeService).GetMethod("TryConfigurePoweredFilter", nonPublicInstance);
        var fuel = typeof(MachineRuntimeService).GetMethod("TryFuelCarbonGenerator", nonPublicInstance);
        var loadSeed = typeof(SingleBlockFarmService).GetMethod("TryLoadSeed", nonPublicInstance);
        var loadFertilizer = typeof(SingleBlockFarmService).GetMethod("TryLoadFertilizer", nonPublicInstance);
        var installModule = typeof(SingleBlockFarmService).GetMethod("TryInstallModule", nonPublicInstance);
        if (configure?.ReturnType != typeof(SvsapmeMachineActionApplyResult))
            failures.Add("MachineRuntimeService must expose host-dispatchable powered filter configuration");

        if (fuel?.ReturnType != typeof(SvsapmeMachineActionApplyResult))
            failures.Add("MachineRuntimeService must expose host-dispatchable Carbon Generator fueling");

        if (loadSeed?.ReturnType != typeof(SvsapmeMachineActionApplyResult))
            failures.Add("SingleBlockFarmService must expose host-dispatchable seed loading");

        if (loadFertilizer?.ReturnType != typeof(SvsapmeMachineActionApplyResult))
            failures.Add("SingleBlockFarmService must expose host-dispatchable fertilizer loading");

        if (installModule?.ReturnType != typeof(SvsapmeMachineActionApplyResult))
            failures.Add("SingleBlockFarmService must expose host-dispatchable module installation");

        if (typeof(SvsapmeMultiplayerService).GetMethod("TryResolveHostActionContext", nonPublicInstance) is null)
            failures.Add("SvsapmeMultiplayerService must validate MachineGuid, tile, and active SVSAP endpoint before host actions");

        if (typeof(SvsapmeMultiplayerService).GetMethod("ExecuteMachineActionRequest", nonPublicInstance)?.ReturnType != typeof(SvsapmeMachineActionResponse))
            failures.Add("SvsapmeMultiplayerService must dispatch host machine actions into response messages");

        if (typeof(SvsapmeMultiplayerService).GetMethod("TrySendMachineActionRequest", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) is null)
            failures.Add("SvsapmeMultiplayerService must expose a client action sender for farmhand clicks");

        if (typeof(SvsapmeMultiplayerService).GetField("pendingClientActionsByMachine", nonPublicInstance) is null
            || typeof(SvsapmeMultiplayerService).GetProperty("PendingClientMachineActionCount")?.PropertyType != typeof(int))
        {
            failures.Add("farmhand machine actions must keep an in-flight guard so repeated clicks cannot send duplicate consumptions");
        }

        if (typeof(SvsapmeMultiplayerService).GetMethod("TryReservePendingClientAction", nonPublicInstance) is null
            || typeof(SvsapmeMultiplayerService).GetMethod("TryTakePlayerActionItem", nonPublicInstance) is not null)
        {
            failures.Add("SVSAPME multiplayer escrow must be client-side and must not mutate farmhand inventory from the host copy");
        }

        if (typeof(SvsapmeMultiplayerService).GetMethod("TrySendMachineItemMovementReport", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) is null)
            failures.Add("SvsapmeMultiplayerService must expose a client item-movement reporter for farmhand machine items");

        if (typeof(MachineRuntimeService).GetMethod("SetClientActionSender") is null
            || typeof(SingleBlockFarmService).GetMethod("SetClientActionSender") is null)
        {
            failures.Add("interactive SVSAPME services must bind farmhand clicks to multiplayer action requests");
        }

        if (typeof(SvsapmeMachineActionRequest).GetProperty(nameof(SvsapmeMachineActionRequest.FarmingLevel))?.PropertyType != typeof(int))
            failures.Add("farm seed action requests must carry the actor farming level");

        if (typeof(SvsapmeMultiplayerService).GetField("machineRuntime", nonPublicInstance)?.FieldType != typeof(MachineRuntimeService)
            || typeof(SvsapmeMultiplayerService).GetField("singleBlockFarm", nonPublicInstance)?.FieldType != typeof(SingleBlockFarmService))
        {
            failures.Add("SvsapmeMultiplayerService must retain runtime action services for host dispatch");
        }

        if (typeof(SvsapmeMultiplayerService).GetField("registry", nonPublicInstance)?.FieldType != typeof(MachineRegistryService))
            failures.Add("SvsapmeMultiplayerService must retain MachineRegistryService for farmhand item movement reports");

        return failures;
    }

    private static IReadOnlyList<string> TestMissingMachineReclaim()
    {
        var failures = new List<string>();
        if (MachineReclaimRules.ShouldQueueMissingMachineReclaim(0))
            failures.Add("missing machine reclaim must not queue on day 0");

        if (MachineReclaimRules.ShouldQueueMissingMachineReclaim(1))
            failures.Add("missing machine reclaim must allow one missing day of grace");

        if (!MachineReclaimRules.ShouldQueueMissingMachineReclaim(2))
            failures.Add("missing machine reclaim must queue after the grace day");

        return failures;
    }

    private static IReadOnlyList<string> TestLocationCacheFullEnum()
    {
        var failures = new List<string>();
        if (!PersistentLocationEnumerationRules.RebuildUsesUtilityForEachLocationDefaultOverload)
            failures.Add("cache rebuild must use Utility.ForEachLocation default overload");

        if (!PersistentLocationEnumerationRules.DefaultForEachLocationIncludesInteriors)
            failures.Add("Utility.ForEachLocation default coverage must include instantiated building interiors");

        if (PersistentLocationEnumerationRules.DefaultForEachLocationIncludesGeneratedLocations)
            failures.Add("Utility.ForEachLocation default coverage must exclude generated locations");

        if (!PersistentLocationEnumerationRules.RouteTickUsesMachinePositionCacheOnly)
            failures.Add("route tick must use the machine position cache instead of full location enumeration");

        if (!PersistentLocationEnumerationRules.ShouldRegisterLocationName("Farm")
            || !PersistentLocationEnumerationRules.ShouldRegisterLocationName("FarmHouse"))
        {
            failures.Add("persistent outdoor and building interior locations must register");
        }

        if (PersistentLocationEnumerationRules.ShouldRegisterLocationName("UndergroundMine120")
            || PersistentLocationEnumerationRules.ShouldRegisterLocationName("VolcanoDungeon5"))
        {
            failures.Add("generated mine and volcano locations must not register");
        }

        return failures;
    }

    private static IReadOnlyList<string> TestFarmCropSet()
    {
        var failures = new List<string>();
        foreach (var crop in FarmCropCatalog.AcceptanceCrops)
        {
            var farm = new FarmMachineState { InternalSeedCount = 1 };
            SingleBlockFarmRules.BindSeed(farm, crop, placedByFarmingLevel: 10);
            var outputBuffer = new List<BufferedItemStack>();
            var tier = EnergyTierTable.Farms.Single(entry => entry.Tier == EnergyTier.Copper);
            var modules = new FarmModuleSnapshot(0, 1.0m, false, 1.0m, 0, 1);
            var firstSeason = crop.Seasons[0];
            var harvested = 0;
            for (var day = 1; day <= crop.BaseGrowthDays; day++)
            {
                var plan = SingleBlockFarmRules.PlanDay(farm, tier, crop, modules, availableNetworkSeeds: 0, availableNetworkFertilizer: 0, firstSeason, energyMultiplier: 1.0);
                var result = SingleBlockFarmRules.ApplyPaidDay(farm, outputBuffer, tier, crop, modules, plan);
                harvested += result.HarvestedPlots;
                if (day < crop.BaseGrowthDays && harvested > 0)
                    failures.Add($"{crop.DisplayName} harvested before its base growth duration");
            }

            if (harvested != 1)
                failures.Add($"{crop.DisplayName} should harvest exactly once after its base growth duration");

            var minimumStack = Math.Max(1, crop.HarvestMinStack);
            var output = outputBuffer.LastOrDefault();
            if (output is null || output.QualifiedItemId != crop.HarvestQualifiedItemId || output.Stack < minimumStack)
                failures.Add($"{crop.DisplayName} output should match whitelist harvest id and at least its minimum stack");

            if (crop.RegrowDays > 0)
            {
                var beforeRegrowOutputs = outputBuffer.Count;
                for (var day = 1; day <= crop.RegrowDays; day++)
                {
                    var plan = SingleBlockFarmRules.PlanDay(farm, tier, crop, modules, availableNetworkSeeds: 0, availableNetworkFertilizer: 0, firstSeason, energyMultiplier: 1.0);
                    SingleBlockFarmRules.ApplyPaidDay(farm, outputBuffer, tier, crop, modules, plan);
                    if (day < crop.RegrowDays && outputBuffer.Count > beforeRegrowOutputs)
                        failures.Add($"{crop.DisplayName} regrow harvested before its regrow duration");
                }

                if (outputBuffer.Count != beforeRegrowOutputs + 1)
                    failures.Add($"{crop.DisplayName} should harvest once after regrow duration");
            }
        }

        return failures;
    }

    private static IReadOnlyList<string> TestEnergyProductionRules()
    {
        var failures = new List<string>();
        if (EnergyProductionRules.GetSolarPanelWh(isOutdoors: false, isRaining: false, isLightning: false, Season.Spring) != 0)
            failures.Add("indoor solar panels must generate 0 Wh");

        if (EnergyProductionRules.GetSolarPanelWh(isOutdoors: true, isRaining: false, isLightning: false, Season.Spring) != 1500)
            failures.Add("spring sunny solar panel output must be 1.50 kWh");

        if (EnergyProductionRules.GetSolarPanelWh(isOutdoors: true, isRaining: false, isLightning: false, Season.Winter) != 1000)
            failures.Add("winter sunny solar panel output must be 1.00 kWh");

        if (EnergyProductionRules.GetSolarPanelWh(isOutdoors: true, isRaining: true, isLightning: false, Season.Summer) != 200)
            failures.Add("rainy solar panel output must be 0.20 kWh");

        if (EnergyProductionRules.GetLightningCapacitorWh(isOutdoors: true, isLightning: true) != 6000)
            failures.Add("outdoor lightning capacitor output must be 6.00 kWh on storm days");

        if (EnergyProductionRules.GetLightningCapacitorWh(isOutdoors: false, isLightning: true) != 0)
            failures.Add("indoor lightning capacitor output must be 0 Wh");

        if (!CarbonGeneratorFuelRules.TryGetFuelWh("(O)382", allowExtended: false, out var coalWh) || coalWh != 350)
            failures.Add("Carbon Generator coal fuel must always be 0.35 kWh");

        if (CarbonGeneratorFuelRules.TryGetFuelWh("(O)388", allowExtended: false, out _))
            failures.Add("Carbon Generator wood fuel must be disabled unless extended fuels are enabled");

        if (!CarbonGeneratorFuelRules.TryGetFuelWh("(O)388", allowExtended: true, out var woodWh) || woodWh != 40)
            failures.Add("Carbon Generator wood fuel must be 0.04 kWh when enabled");

        if (!CarbonGeneratorFuelRules.TryGetFuelWh("(O)709", allowExtended: true, out var hardwoodWh) || hardwoodWh != 250)
            failures.Add("Carbon Generator hardwood fuel must be 0.25 kWh when enabled");

        if (!CarbonGeneratorFuelRules.TryGetFuelWh("(O)771", allowExtended: true, out var fiberWh) || fiberWh != 10)
            failures.Add("Carbon Generator fiber fuel must be 0.01 kWh when enabled");

        if (!CarbonGeneratorFuelRules.TryGetFuelWh("(O)92", allowExtended: true, out var sapWh) || sapWh != 5)
            failures.Add("Carbon Generator sap fuel must be 0.005 kWh when enabled");

        return failures;
    }

    private static IReadOnlyList<string> TestFarmPowerFreeze()
    {
        var failures = new List<string>();
        var crop = FarmCropCatalog.AcceptanceCrops.First(candidate => candidate.SeedQualifiedItemId == "(O)472");
        var farm = new FarmMachineState { InternalSeedCount = 3 };
        SingleBlockFarmRules.BindSeed(farm, crop, placedByFarmingLevel: 10);
        farm.Plots.Add(new FarmPlotState { ProgressUnits = 123 });
        var beforeSeeds = farm.InternalSeedCount;
        var beforePlots = farm.Plots.Count;
        var beforeProgress = farm.Plots[0].ProgressUnits;
        var outputBuffer = new List<BufferedItemStack>();
        var beforeOutputs = outputBuffer.Count;

        var frozen = SingleBlockFarmRules.ApplyFrozenDay();
        if (frozen.Outcome != FarmDailyOutcome.Frozen)
            failures.Add("farm freeze result must be marked Frozen");

        if (farm.InternalSeedCount != beforeSeeds || farm.Plots.Count != beforePlots || farm.Plots[0].ProgressUnits != beforeProgress || outputBuffer.Count != beforeOutputs)
            failures.Add("farm freeze must not consume seeds, add plots, advance progress, or create output");

        return failures;
    }

    private static IReadOnlyList<string> TestFarmDailyProgress()
    {
        var failures = new List<string>();

        if (FarmGrowthRules.GetConstantModuleMaturityDays(28, 0.80m, 0.90m) != 21)
            failures.Add("ancient fruit with iridium light + iridium thermostat should mature in 21 days");

        if (FarmGrowthRules.GetConstantModuleMaturityDays(28, 0.64m, 0.90m) != 17)
            failures.Add("ancient fruit with double iridium lights + iridium thermostat should mature in 17 days");

        var daily = FarmGrowthRules.GetDailyProgressUnits(0.80m, 0.90m);
        if (!FarmGrowthRules.IsMature(daily * 21, 28))
            failures.Add("daily progress model should mature on the same day as the constant-module ceil formula");

        if (FarmGrowthRules.IsMature(daily * 20, 28))
            failures.Add("daily progress model should not mature before the ceil-formula day");

        return failures;
    }

    private static IReadOnlyList<string> TestFarmEnergyBudgetRules()
    {
        var failures = new List<string>();

        if (FarmGrowthRules.GetDailyBaseEnergyWh(256) != 7_680)
            failures.Add("iridium farm base energy should be 7.68 kWh for 256 occupied plots");

        if (FarmGrowthRules.GetDailyWaterEnergyWh(256, 0) != 1_280)
            failures.Add("fully covered iridium farm water floor should be 1.28 kWh");

        if (FarmGrowthRules.GetDailyWaterEnergyWh(0, 256) != 5_120)
            failures.Add("uncovered iridium farm water cost should be 5.12 kWh");

        var crop = FarmCropCatalog.AcceptanceCrops.First(candidate => candidate.SeedQualifiedItemId == "(O)472");
        var farm = new FarmMachineState { InternalSeedCount = 2 };
        SingleBlockFarmRules.BindSeed(farm, crop, placedByFarmingLevel: 10);
        farm.Plots.Add(new FarmPlotState());
        farm.Plots.Add(new FarmPlotState());
        farm.Plots.Add(new FarmPlotState());
        var tier = EnergyTierTable.Farms.Single(entry => entry.Tier == EnergyTier.Copper);
        var modules = new FarmModuleSnapshot(SprinklerCoveredPlots: 2, LightFactorProduct: 1.0m, HasThermostat: false, ThermostatFactor: 1.0m, ChamberModuleWhPerPlot: 10, FertilizerCoveragePerFertilizer: 1);
        var plan = SingleBlockFarmRules.PlanDay(farm, tier, crop, modules, availableNetworkSeeds: 5, availableNetworkFertilizer: 0, season: "spring", energyMultiplier: 1.0);
        if (plan.PlannedSeedCount != 7 || plan.ChargedOccupiedPlots != 10)
            failures.Add("farm budget must count existing occupied plots plus planned internal/network reseeds");

        if (plan.RequiredWh != 570)
            failures.Add("farm budget should include base, sprinkler water, and chamber module energy for charged plots");

        if (SingleBlockFarmRules.CanBindSeed(farm, "(O)481"))
            failures.Add("occupied farm must reject changing to a different seed type");

        return failures;
    }

    private static IReadOnlyList<string> TestFarmModuleEconomy()
    {
        var failures = new List<string>();
        var tier = EnergyTierTable.Farms.Single(entry => entry.Tier == EnergyTier.Iridium);
        var farm = new FarmMachineState();

        for (var i = 0; i < 11; i++)
        {
            var sprinkler = FarmModuleRules.TryInstallModule(farm, tier, FarmModuleRules.IridiumSprinklerQualifiedItemId);
            if (!sprinkler.Success)
                failures.Add("iridium farm should accept 11 iridium sprinklers as one shared sprinkler slot");
        }

        var light1 = FarmModuleRules.TryInstallModule(farm, tier, "(O)" + ModItemCatalog.IridiumGrowthLightModule);
        var light2 = FarmModuleRules.TryInstallModule(farm, tier, "(O)" + ModItemCatalog.IridiumGrowthLightModule);
        var thermostat = FarmModuleRules.TryInstallModule(farm, tier, "(O)" + ModItemCatalog.IridiumThermostatModule);
        var slow = FarmModuleRules.TryInstallModule(farm, tier, "(O)" + ModItemCatalog.IridiumSlowReleaseModule);
        if (!light1.Success || !light2.Success || !thermostat.Success || !slow.Success)
            failures.Add("iridium farm should accept sprinkler slot, two lights, one thermostat, and one slow-release module");

        var thirdLight = FarmModuleRules.TryInstallModule(farm, tier, "(O)" + ModItemCatalog.IridiumGrowthLightModule);
        if (thirdLight.Success)
            failures.Add("farm must reject a third growth light");

        var secondThermostat = FarmModuleRules.TryInstallModule(farm, tier, "(O)" + ModItemCatalog.AdvancedThermostatModule);
        if (secondThermostat.Success)
            failures.Add("farm must reject a second thermostat");

        if (FarmModuleRules.GetUsedSlots(farm) != 5)
            failures.Add("full iridium farm module set must use exactly five slots");

        var modules = SingleBlockFarmRules.GetModuleSnapshot(farm);
        if (modules.SprinklerCoveredPlots < 256 || modules.LightFactorProduct != 0.64m || !modules.HasThermostat || modules.ThermostatFactor != 0.90m)
            failures.Add("module snapshot must combine sprinkler coverage, double iridium lights, and iridium thermostat factors");

        if (modules.ChamberModuleWhPerPlot != 310)
            failures.Add("two iridium lights plus iridium thermostat must cost 0.31 kWh per occupied plot");

        var crop = FarmCropCatalog.AcceptanceCrops.First(candidate => candidate.SeedQualifiedItemId == "(O)499");
        SingleBlockFarmRules.BindSeed(farm, crop, placedByFarmingLevel: 10);
        for (var i = 0; i < 256; i++)
            farm.Plots.Add(new FarmPlotState());

        var plan = SingleBlockFarmRules.PlanDay(farm, tier, crop, modules, availableNetworkSeeds: 0, availableNetworkFertilizer: 0, season: "winter", energyMultiplier: 1.0);
        if (!plan.CanGrowToday || plan.RequiredWh != 88_320)
            failures.Add("full iridium farm module budget must be 88.32 kWh/day and thermostat must allow off-season growth");

        return failures;
    }

    private static IReadOnlyList<string> TestFarmFertilizerQuality()
    {
        var failures = new List<string>();
        var crop = FarmCropCatalog.AcceptanceCrops.First(candidate => candidate.SeedQualifiedItemId == "(O)472");
        var farm = new FarmMachineState
        {
            InternalSeedCount = 1,
            BoundFertilizerQualifiedItemId = FarmModuleRules.DeluxeFertilizerQualifiedItemId,
            InternalFertilizerCount = 1
        };
        SingleBlockFarmRules.BindSeed(farm, crop, placedByFarmingLevel: 10);
        var tier = EnergyTierTable.Farms.Single(entry => entry.Tier == EnergyTier.Copper);
        var slow = FarmModuleRules.TryInstallModule(farm, tier, "(O)" + ModItemCatalog.IridiumSlowReleaseModule);
        if (!slow.Success)
            failures.Add("copper farm should accept one slow-release module");

        var modules = SingleBlockFarmRules.GetModuleSnapshot(farm);
        var outputBuffer = new List<BufferedItemStack>();
        var plan = SingleBlockFarmRules.PlanDay(farm, tier, crop, modules, availableNetworkSeeds: 0, availableNetworkFertilizer: 0, season: "spring", energyMultiplier: 1.0);
        if (plan.PlannedSeedCount != 1 || plan.PlannedFertilizerCount != 1)
            failures.Add("farm should plan one fertilizer unit for a fertilized planted plot");

        SingleBlockFarmRules.ApplyPaidDay(farm, outputBuffer, tier, crop, modules, plan);
        if (farm.InternalFertilizerCount != 0 || farm.Plots.Count != 1 || farm.Plots[0].FertilizerQualifiedItemId != FarmModuleRules.DeluxeFertilizerQualifiedItemId)
            failures.Add("planted plot must consume and remember its fertilizer");

        farm.Plots[0].ProgressUnits = FarmGrowthRules.GetRequiredProgressUnits(crop.BaseGrowthDays) - FarmGrowthRules.GetDailyProgressUnits(1.0m, 1.0m);
        var harvestPlan = SingleBlockFarmRules.PlanDay(farm, tier, crop, modules, availableNetworkSeeds: 0, availableNetworkFertilizer: 0, season: "spring", energyMultiplier: 1.0);
        SingleBlockFarmRules.ApplyPaidDay(farm, outputBuffer, tier, crop, modules, harvestPlan);
        var chances = FarmModuleRules.GetHarvestQualityChances(10, FarmModuleRules.DeluxeFertilizerQualifiedItemId);
        if (chances.IridiumBasisPoints <= 0 || chances.GoldBasisPoints <= 0 || chances.SilverBasisPoints <= 0)
            failures.Add("deluxe fertilizer with farming level 10 must preserve vanilla-style iridium/gold/silver quality probabilities");

        if (outputBuffer.Count != 1 || outputBuffer[0].Quality is not (1 or 2 or 4))
            failures.Add("deluxe fertilizer with farming level 10 must produce a fertilized-quality farm output without guaranteeing iridium");

        return failures;
    }

    private static IReadOnlyList<string> TestFarmLockedOutput()
    {
        var failures = new List<string>();
        var crop = FarmCropCatalog.AcceptanceCrops.First(candidate => candidate.SeedQualifiedItemId == "(O)472");
        var farm = new FarmMachineState();
        var outputBuffer = new List<BufferedItemStack>();
        SingleBlockFarmRules.BindSeed(farm, crop, placedByFarmingLevel: 10);
        farm.Plots.Add(new FarmPlotState { ProgressUnits = FarmGrowthRules.GetRequiredProgressUnits(crop.BaseGrowthDays) - FarmGrowthRules.GetDailyProgressUnits(1.0m, 1.0m) });

        var tier = EnergyTierTable.Farms.Single(entry => entry.Tier == EnergyTier.Copper);
        var modules = new FarmModuleSnapshot(0, 1.0m, false, 1.0m, 0, 1);
        var plan = SingleBlockFarmRules.PlanDay(farm, tier, crop, modules, availableNetworkSeeds: 0, availableNetworkFertilizer: 0, season: "spring", energyMultiplier: 1.0);
        var result = SingleBlockFarmRules.ApplyPaidDay(farm, outputBuffer, tier, crop, modules, plan);
        if (result.HarvestedPlots != 1)
            failures.Add("mature farm plot should harvest into output buffer");

        if (outputBuffer.Count != 1 || outputBuffer[0].QualifiedItemId != crop.HarvestQualifiedItemId)
            failures.Add("farm output must remain buffered until a later network insert succeeds");

        return failures;
    }

    private static IReadOnlyList<string> TestDailyOrderStorageGate()
    {
        var failures = new List<string>();

        if (!DailySettlementRules.FarmConsumesBeforeEnergyProduction())
            failures.Add("DayStarted settlement must run farm consumption/growth before daily energy production");

        if (DailySettlementRules.CanFarmRunFromDayStartStorage(startingStoredWh: 0, requiredFarmWh: 1000))
            failures.Add("farm must not be able to spend solar energy generated later in the same DayStarted");

        if (!DailySettlementRules.CanFarmRunFromDayStartStorage(startingStoredWh: 1000, requiredFarmWh: 1000))
            failures.Add("farm should run when yesterday's stored energy covers the required daily budget");

        return failures;
    }

    private static IReadOnlyList<string> TestCellStackGuard()
    {
        var failures = new List<string>();

        if (!EnergyCellRules.ShouldSplitChargedStack("(BC)" + ModItemCatalog.CopperEnergyCell, 2, 1))
            failures.Add("charged stacked energy cell must be split");

        if (EnergyCellRules.ShouldSplitChargedStack("(BC)" + ModItemCatalog.CopperEnergyCell, 2, 0))
            failures.Add("uncharged energy cell stack should remain stackable");

        if (EnergyCellRules.ShouldSplitChargedStack("(BC)" + ModItemCatalog.BatterySynthesizer, 2, 1))
            failures.Add("non-cell machines must not be treated as charged cells");

        if (EnergyCellRules.ShouldSplitChargedStack("(BC)" + ModItemCatalog.CopperEnergyCell, 1, 500))
            failures.Add("single charged cell already satisfies one-cell-one-state semantics");

        if (typeof(CellStackGuardService).GetMethod("SetClientMovementReporter") is null)
            failures.Add("cell stack guard must bind farmhand machine item movement reports to multiplayer");

        var charged = CreateSelfTestCell();
        charged.modData[MachineRegistryService.StoredWhKey] = "500";
        var empty = CreateSelfTestCell();
        if (EnergyCellRules.CanStackPreservingCellState(charged, empty)
            || EnergyCellRules.CanStackPreservingCellState(empty, charged))
        {
            failures.Add("charged or GUID-bearing energy cells must be rejected before vanilla stack merging can drop modData");
        }

        var guidOnly = CreateSelfTestCell();
        guidOnly.modData[MachineRegistryService.MachineGuidKey] = Guid.NewGuid().ToString("N");
        if (EnergyCellRules.CanStackPreservingCellState(guidOnly, empty))
            failures.Add("energy cells with MachineGuid must be one-state-per-item even before they store energy");

        if (!EnergyCellRules.CanStackPreservingCellState(empty, CreateSelfTestCell()))
            failures.Add("empty energy cells without persistent state should remain stackable");

        var guidFarm = CreateSelfTestMachine(ModItemCatalog.CopperFarm);
        guidFarm.modData[MachineRegistryService.MachineGuidKey] = Guid.NewGuid().ToString("N");
        var emptyFarm = CreateSelfTestMachine(ModItemCatalog.CopperFarm);
        if (EnergyCellRules.CanStackPreservingCellState(guidFarm, emptyFarm)
            || EnergyCellRules.CanStackPreservingCellState(emptyFarm, guidFarm))
        {
            failures.Add("GUID-bearing SVSAPME machine items, including farms, must be rejected before vanilla stack merging can drop modData");
        }

        return failures;
    }

    private static Item CreateSelfTestCell()
    {
        return new SelfTestItem(ModItemCatalog.CopperEnergyCell, stack: 1);
    }

    private static Item CreateSelfTestMachine(string itemId)
    {
        return new SelfTestItem(itemId, stack: 1);
    }

    private sealed class SelfTestItem : Item
    {
        public SelfTestItem(string itemId, int stack)
        {
            this.ItemId = itemId;
            this.Stack = stack;
        }

        public override string TypeDefinitionId => "(BC)";

        public override string DisplayName => this.ItemId;

        public override void drawInMenu(
            SpriteBatch spriteBatch,
            Vector2 location,
            float scaleSize,
            float transparency,
            float layerDepth,
            StackDrawType drawStackNumber,
            Color color,
            bool drawShadow)
        {
        }

        public override int maximumStackSize()
        {
            return 999;
        }

        public override string getDescription()
        {
            return string.Empty;
        }

        public override bool isPlaceable()
        {
            return false;
        }

        protected override Item GetOneNew()
        {
            return new SelfTestItem(this.ItemId, stack: 1);
        }
    }

    private static IReadOnlyList<string> TestMachineGuidReconcile()
    {
        var failures = new List<string>();
        var machineGuid = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var sameTickAdded = new HashSet<Guid> { machineGuid };
        if (!MachineLifecycleRules.IsSameTickReplayRemoval(machineGuid, sameTickAdded))
            failures.Add("same tick remove+add with identical MachineGuid must be treated as replay/reposition, not orphan removal");

        if (MachineLifecycleRules.IsSameTickReplayRemoval(machineGuid, new HashSet<Guid>()))
            failures.Add("removed machine without same-tick added MachineGuid must not be treated as replay");

        if (MachineLifecycleRules.IsSameTickReplayRemoval(Guid.Empty, sameTickAdded))
            failures.Add("empty MachineGuid must never be treated as a valid replay");

        return failures;
    }

    private static IReadOnlyList<string> TestOrphanReclaim()
    {
        var failures = new List<string>();
        var machineGuid = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var state = new MachineState
        {
            MachineGuid = machineGuid,
            QualifiedItemId = "(BC)" + ModItemCatalog.BatterySynthesizer,
            LocationName = "Farm",
            TileX = 11,
            TileY = 22,
            StoredWh = 5000,
            OutputBuffer =
            {
                new BufferedItemStack { QualifiedItemId = "(O)787", Stack = 1 }
            }
        };

        var reclaim = MachineReclaimRules.CreateOrphanReclaim(state, Array.Empty<PendingReclaimCrate>());
        if (reclaim is null)
        {
            failures.Add("orphan removal should queue a reclaim record for the removed machine state");
            return failures;
        }

        if (reclaim.Reason != MachineReclaimRules.OrphanReason || reclaim.OriginalLocationName != "Farm" || reclaim.TileX != 11 || reclaim.TileY != 22)
            failures.Add("orphan reclaim must preserve reason, location, and last known tile");

        if (!reclaim.MachineGuids.SequenceEqual(new[] { machineGuid }))
            failures.Add("orphan reclaim must contain exactly the removed MachineGuid");

        var duplicate = MachineReclaimRules.CreateOrphanReclaim(state, new[] { reclaim });
        if (duplicate is not null)
            failures.Add("orphan reclaim must not queue the same machine twice");

        var summary = MachineReclaimRules.SummarizeRecoverableItems(new[] { reclaim }, new Dictionary<Guid, MachineState> { [machineGuid] = state });
        if (summary.Machines != 1 || summary.BufferedItemStacks != 1)
            failures.Add("orphan reclaim summary must include the machine and buffered item stacks");

        return failures;
    }

    private static IReadOnlyList<string> TestClaimForceGate()
    {
        var failures = new List<string>();
        var confirmed = new PendingReclaimCrate
        {
            ReclaimId = Guid.Parse("10101010-1010-1010-1010-101010101010"),
            Reason = MachineReclaimRules.OrphanReason
        };
        var demolished = new PendingReclaimCrate
        {
            ReclaimId = Guid.Parse("20202020-2020-2020-2020-202020202020"),
            Reason = MachineReclaimRules.BuildingDemolishReason
        };
        var unconfirmed = new PendingReclaimCrate
        {
            ReclaimId = Guid.Parse("30303030-3030-3030-3030-303030303030"),
            Reason = "manual-unconfirmed"
        };

        if (!MachineReclaimRules.IsConfirmedClaimReason(confirmed.Reason))
            failures.Add("orphan reclaim must be claimable without force");

        if (!MachineReclaimRules.IsConfirmedClaimReason(demolished.Reason))
            failures.Add("building demolish reclaim must be claimable without force");

        if (MachineReclaimRules.IsConfirmedClaimReason(unconfirmed.Reason))
            failures.Add("unconfirmed reclaim reason must not be claimable without force");

        if (!MachineReclaimRules.ShouldClaimReclaim(confirmed, includeUnconfirmed: false))
            failures.Add("confirmed reclaim must pass the default claim gate");

        if (MachineReclaimRules.ShouldClaimReclaim(unconfirmed, includeUnconfirmed: false))
            failures.Add("unconfirmed reclaim must be blocked by the default claim gate");

        if (!MachineReclaimRules.ShouldClaimReclaim(unconfirmed, includeUnconfirmed: true))
            failures.Add("force claim must allow unconfirmed reclaim records");

        if (MachineRegistryService.ShouldRecoverMachineForReclaim(verifyNotLive: true, isMachineGuidLive: true))
            failures.Add("force claim must still exclude held or placed live machines");

        return failures;
    }

    private static IReadOnlyList<string> TestConsumedChargedRetire()
    {
        var failures = new List<string>();
        var machineGuid = Guid.Parse("abababab-abab-abab-abab-abababababab");
        var data = new MachineSaveData();
        data.Machines[machineGuid] = new MachineState
        {
            MachineGuid = machineGuid,
            QualifiedItemId = "(BC)" + ModItemCatalog.CopperEnergyCell,
            StoredWh = 4000
        };
        data.PendingReclaims.Add(new PendingReclaimCrate
        {
            ReclaimId = Guid.Parse("cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd"),
            MachineGuids = { machineGuid }
        });

        if (!MachineRegistryService.CanRetireConfirmedConsumedMachine(data.Machines[machineGuid]))
            failures.Add("energy-only consumed machines may retire; stored Wh is discarded by policy");

        var loadedState = new MachineState
        {
            MachineGuid = Guid.Parse("edededed-eded-eded-eded-edededededed"),
            QualifiedItemId = "(BC)" + ModItemCatalog.BatterySynthesizer,
            OutputBuffer = { new BufferedItemStack { QualifiedItemId = "(O)787", Stack = 1 } }
        };
        if (MachineRegistryService.CanRetireConfirmedConsumedMachine(loadedState))
            failures.Add("consumed-candidate machines with output buffers must not be retired as confirmed consumed");

        var farmPayloadState = new MachineState
        {
            MachineGuid = Guid.Parse("efefefef-efef-efef-efef-efefefefefef"),
            QualifiedItemId = "(BC)" + ModItemCatalog.CopperFarm,
            Farm =
            {
                BoundSeedQualifiedItemId = "(O)472",
                InternalSeedCount = 1,
                InstalledModuleQualifiedItemIds = { "(O)" + ModItemCatalog.BasicGrowthLightModule }
            }
        };
        if (MachineRegistryService.CanRetireConfirmedConsumedMachine(farmPayloadState))
            failures.Add("consumed-candidate farms with internal seeds or modules must stay recoverable instead of retiring");

        var result = MachineRegistryService.RetireConfirmedConsumedMachine(data, machineGuid);
        if (!result.Retired)
            failures.Add("confirmed consumed machine must report retirement");

        if (result.DiscardedStoredWh != 4000)
            failures.Add("confirmed consumed charged machine must report discarded stored Wh");

        if (data.Machines.ContainsKey(machineGuid))
            failures.Add("confirmed consumed machine must be removed from MachineSaveData.Machines");

        if (data.PendingReclaims.Any(reclaim => reclaim.MachineGuids.Contains(machineGuid)))
            failures.Add("confirmed consumed machine must not remain in pending reclaim records");

        return failures;
    }

    private static IReadOnlyList<string> TestDisassemblyEnergyPolicy()
    {
        var failures = new List<string>();
        var nonCell = new MachineState
        {
            MachineGuid = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            QualifiedItemId = "(BC)" + ModItemCatalog.BatterySynthesizer,
            StoredWh = 4000,
            ProgressWh = 2500
        };
        if (!MachineLifecycleRules.ClearDisassembledMachineEnergy(nonCell))
            failures.Add("non-cell machine disassembly must report an energy-state change");

        if (nonCell.StoredWh != 0 || nonCell.ProgressWh != 0)
            failures.Add("non-cell machine disassembly must zero StoredWh and ProgressWh");

        var cell = new MachineState
        {
            MachineGuid = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            QualifiedItemId = "(BC)" + ModItemCatalog.CopperEnergyCell,
            StoredWh = 4000,
            ProgressWh = 2500
        };
        MachineLifecycleRules.ClearDisassembledMachineEnergy(cell);
        if (cell.StoredWh != 4000)
            failures.Add("energy cell disassembly must preserve StoredWh");

        if (cell.ProgressWh != 0)
            failures.Add("energy cell disassembly must still clear non-cell progress fields");

        return failures;
    }

    private static IReadOnlyList<string> TestSynthAtomicRules()
    {
        var failures = new List<string>();
        var fullInventory = BatterySynthesizerRules.Materials.ToDictionary(
            material => material.QualifiedItemId,
            material => material.Count,
            StringComparer.Ordinal);

        if (!BatterySynthesizerRules.CanAssemble(BatterySynthesizerRules.RequiredWh, 0, id => fullInventory[id]))
            failures.Add("synth should assemble when charged, output buffer has room, and all materials are available");

        if (BatterySynthesizerRules.CanAssemble(BatterySynthesizerRules.RequiredWh - 1, 0, id => fullInventory[id]))
            failures.Add("synth must not assemble before 10 kWh is charged");

        if (BatterySynthesizerRules.CanAssemble(BatterySynthesizerRules.RequiredWh, BatterySynthesizerRules.OutputBufferLimit, id => fullInventory[id]))
            failures.Add("synth must not consume materials when output buffer is full");

        var missingCoal = new Dictionary<string, int>(fullInventory, StringComparer.Ordinal)
        {
            ["(O)382"] = 9
        };
        if (BatterySynthesizerRules.CanAssemble(BatterySynthesizerRules.RequiredWh, 0, id => missingCoal[id]))
            failures.Add("synth must not consume partial materials when any material is missing");

        return failures;
    }

    private static IReadOnlyList<string> TestContentTable()
    {
        var failures = new List<string>();
        var objectIds = ModItemCatalog.ObjectItems.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var bigCraftableIds = ModItemCatalog.BigCraftables.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var recipeName in ModItemCatalog.CraftingRecipes.Keys)
        {
            if (!objectIds.Contains(recipeName) && !bigCraftableIds.Contains(recipeName))
                failures.Add($"recipe has no matching content definition: {recipeName}");
        }

        var nonCraftableBigCraftables = new HashSet<string>(StringComparer.Ordinal)
        {
            ModItemCatalog.ReclaimCrate,
            ModItemCatalog.LightningCapacitor
        };
        foreach (var bigCraftable in ModItemCatalog.BigCraftables)
        {
            if (!nonCraftableBigCraftables.Contains(bigCraftable.Id)
                && !ModItemCatalog.CraftingRecipes.ContainsKey(bigCraftable.Id))
            {
                failures.Add($"craftable big craftable has no recipe: {bigCraftable.Id}");
            }
        }

        foreach (var recipeName in ModItemCatalog.CraftingRecipes.Keys)
        {
            if (!ModItemCatalog.CraftingRecipeSkillRequirements.ContainsKey(recipeName))
                failures.Add($"crafting recipe has no skill requirement: {recipeName}");
        }

        foreach (var required in new[]
        {
            ModItemCatalog.CarbonGenerator,
            ModItemCatalog.SolarNetworkPanel,
            ModItemCatalog.CopperEnergyCell,
            ModItemCatalog.SteelEnergyCell,
            ModItemCatalog.GoldEnergyCell,
            ModItemCatalog.IridiumEnergyCell,
            ModItemCatalog.BatterySynthesizer,
            ModItemCatalog.CopperFarm,
            ModItemCatalog.SteelFarm,
            ModItemCatalog.GoldFarm,
            ModItemCatalog.IridiumFarm,
            ModItemCatalog.PoweredImporterCopper,
            ModItemCatalog.PoweredImporterSteel,
            ModItemCatalog.PoweredImporterGold,
            ModItemCatalog.PoweredImporterIridium,
            ModItemCatalog.PoweredExporterCopper,
            ModItemCatalog.PoweredExporterSteel,
            ModItemCatalog.PoweredExporterGold,
            ModItemCatalog.PoweredExporterIridium,
            ModItemCatalog.PoweredMachineInterfaceCopper,
            ModItemCatalog.PoweredMachineInterfaceSteel,
            ModItemCatalog.PoweredMachineInterfaceGold,
            ModItemCatalog.PoweredMachineInterfaceIridium,
            ModItemCatalog.BatteryDischarger,
            ModItemCatalog.EnergyMonitorTerminal,
            ModItemCatalog.ElectricFurnace,
            ModItemCatalog.ElectricGeodeCrusher,
            ModItemCatalog.LightningCapacitor,
            ModItemCatalog.ReclaimCrate
        })
        {
            if (!bigCraftableIds.Contains(required))
                failures.Add($"missing required big craftable definition: {required}");
        }

        return failures;
    }

    private static IReadOnlyList<string> TestBuildingDemolishReclaim()
    {
        var failures = new List<string>();
        var machineA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var machineB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var outsideMachine = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var indoorName = "Farm_Barn_1";
        var states = new[]
        {
            new MachineState
            {
                MachineGuid = machineB,
                QualifiedItemId = "(BC)" + ModItemCatalog.CopperFarm,
                LocationName = indoorName,
                OutputBuffer =
                {
                    new BufferedItemStack { QualifiedItemId = "(O)787", Stack = 1 }
                },
                Farm =
                {
                    BoundSeedQualifiedItemId = "(O)472",
                    InternalSeedCount = 5,
                    BoundFertilizerQualifiedItemId = FarmModuleRules.QualityFertilizerQualifiedItemId,
                    InternalFertilizerCount = 3,
                    InstalledModuleQualifiedItemIds =
                    {
                        FarmModuleRules.IridiumSprinklerQualifiedItemId,
                        "(O)" + ModItemCatalog.BasicGrowthLightModule
                    }
                }
            },
            new MachineState
            {
                MachineGuid = machineA,
                QualifiedItemId = "(BC)" + ModItemCatalog.CopperEnergyCell,
                LocationName = indoorName,
                StoredWh = 7500
            },
            new MachineState
            {
                MachineGuid = outsideMachine,
                QualifiedItemId = "(BC)" + ModItemCatalog.CarbonGenerator,
                LocationName = "Farm"
            }
        };

        var reclaim = MachineReclaimRules.CreateBuildingDemolishReclaim(
            indoorName,
            buildingTileX: 10,
            buildingTileY: 20,
            tilesWide: 7,
            tilesHigh: 4,
            states,
            Array.Empty<PendingReclaimCrate>());
        if (reclaim is null)
        {
            failures.Add("building demolish should create a pending reclaim for machines in the removed interior");
            return failures;
        }

        if (reclaim.Reason != MachineReclaimRules.BuildingDemolishReason)
            failures.Add("pending reclaim reason must be building-demolish-reclaim");

        if (reclaim.OriginalLocationName != indoorName)
            failures.Add("pending reclaim must preserve the removed interior location name");

        if (reclaim.TileX != 13 || reclaim.TileY != 22)
            failures.Add("pending reclaim should target the old building center tile");

        var firstRing = MachineReclaimRules.EnumerateSpiralTiles(reclaim.TileX, reclaim.TileY, maxRadius: 1).ToList();
        var expectedFirstRing = new[]
        {
            (13, 22),
            (12, 21),
            (13, 21),
            (14, 21),
            (14, 22),
            (14, 23),
            (13, 23),
            (12, 23),
            (12, 22)
        };
        if (!firstRing.SequenceEqual(expectedFirstRing))
            failures.Add("building reclaim placement must search the old center tile first, then expand in deterministic spiral order");

        if (!reclaim.MachineGuids.SequenceEqual(new[] { machineA, machineB }))
            failures.Add("pending reclaim must include only interior machine GUIDs in deterministic order");

        if (MachineRegistryService.ShouldRecoverMachineForReclaim(verifyNotLive: true, isMachineGuidLive: true))
            failures.Add("building demolish reclaim with live verification must exclude held or placed machines");

        if (!MachineRegistryService.ShouldRecoverMachineForReclaim(verifyNotLive: true, isMachineGuidLive: false))
            failures.Add("building demolish reclaim with live verification must still recover non-live machines");

        if (!MachineRegistryService.ShouldRecoverMachineForReclaim(verifyNotLive: false, isMachineGuidLive: true))
            failures.Add("legacy reclaim without live verification must preserve its explicit behavior");

        var duplicate = MachineReclaimRules.CreateBuildingDemolishReclaim(
            indoorName,
            10,
            20,
            7,
            4,
            states,
            new[] { reclaim });
        if (duplicate is not null)
            failures.Add("same interior machine set must not be queued twice");

        var byGuid = states.ToDictionary(state => state.MachineGuid);
        var summary = MachineReclaimRules.SummarizeRecoverableItems(
            new[]
            {
                reclaim,
                new PendingReclaimCrate
                {
                    MachineGuids = new List<Guid> { machineA, Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd") }
                }
            },
            byGuid);
        if (summary.Machines != 2)
            failures.Add("reclaim summary must count each recoverable machine once and skip missing states");

        if (summary.BufferedItemStacks != 1)
            failures.Add("reclaim summary must include buffered output stacks");

        if (summary.InternalSeedStacks != 1)
            failures.Add("reclaim summary must include farm internal seed stacks");

        if (summary.InternalFertilizerStacks != 1)
            failures.Add("reclaim summary must include farm internal fertilizer stacks");

        if (summary.InstalledModuleStacks != 2)
            failures.Add("reclaim summary must include installed farm modules");

        return failures;
    }

    private static IReadOnlyList<string> TestPoweredPrescanRefund()
    {
        var failures = new List<string>();

        var plan = PoweredTransferRules.PlanImporterExporter(
            sourceAvailable: 20,
            targetCapacity: 17,
            PoweredMachineTier.Copper,
            storedWh: 8,
            halfWhCredit: 0,
            prototypeThroughput: 4);
        if (plan.Mode != PoweredTransferRunMode.Powered || plan.PlannedItems != 16)
            failures.Add("powered prescan must plan min(source, target, tier throughput)");

        if (plan.RequiredHalfWh != 16 || plan.WhToConsume != 8 || plan.CreditAfterPrepay != 0)
            failures.Add("even powered transfer batches must debit exact half-Wh cost as whole Wh");

        var oddPayment = PoweredTransferRules.CreatePayment(plannedItems: 5, halfWhCredit: 0);
        if (oddPayment.WhToConsume != 3 || oddPayment.CreditAfterPrepay != 1)
            failures.Add("odd powered transfer batches must prepay one whole Wh and carry one half-Wh credit");

        var refund = PoweredTransferRules.CalculateRefund(plannedItems: 5, actualMoved: 3, oddPayment.CreditAfterPrepay);
        if (refund.RefundWh != 1 || refund.FinalHalfWhCredit != 1)
            failures.Add("short powered transfer execution must refund whole unused Wh and preserve half-Wh credit");

        var fullMissRefund = PoweredTransferRules.CalculateRefund(plannedItems: 1, actualMoved: 0, creditAfterPrepay: 1);
        if (fullMissRefund.RefundWh != 1 || fullMissRefund.FinalHalfWhCredit != 0)
            failures.Add("a one-item planned move that moves nothing must refund the prepaid whole Wh");

        return failures;
    }

    private static IReadOnlyList<string> TestPoweredDegradeParity()
    {
        var failures = new List<string>();

        var insufficient = PoweredTransferRules.PlanImporterExporter(
            sourceAvailable: 20,
            targetCapacity: 20,
            PoweredMachineTier.Copper,
            storedWh: 7,
            halfWhCredit: 0,
            prototypeThroughput: 4);
        if (insufficient.Mode != PoweredTransferRunMode.Prototype)
            failures.Add("insufficient energy must choose prototype path instead of partial powered throughput");

        if (insufficient.PlannedItems != 4 || insufficient.WhToConsume != 0)
            failures.Add("prototype degradation must preserve prototype throughput and spend zero energy");

        var copperWithPrototypeBaseline = PoweredTransferRules.PlanImporterExporter(
            sourceAvailable: 1_000,
            targetCapacity: 1_000,
            PoweredMachineTier.Copper,
            storedWh: 32,
            halfWhCredit: 0,
            prototypeThroughput: 64);
        if (copperWithPrototypeBaseline.Mode != PoweredTransferRunMode.Powered
            || copperWithPrototypeBaseline.PlannedItems < 64
            || copperWithPrototypeBaseline.WhToConsume != 32)
        {
            failures.Add("powered transfer throughput must never be lower than its prototype degradation throughput when fully paid");
        }

        var none = PoweredTransferRules.PlanImporterExporter(
            sourceAvailable: 20,
            targetCapacity: 20,
            PoweredMachineTier.Copper,
            storedWh: 0,
            halfWhCredit: 0,
            prototypeThroughput: 0);
        if (none.Mode != PoweredTransferRunMode.None || none.PlannedItems != 0)
            failures.Add("if both powered and prototype paths cannot move items, the plan must be none");

        var creditPowered = PoweredTransferRules.PlanImporterExporter(
            sourceAvailable: 1,
            targetCapacity: 1,
            PoweredMachineTier.Copper,
            storedWh: 0,
            halfWhCredit: 1,
            prototypeThroughput: 0);
        if (creditPowered.Mode != PoweredTransferRunMode.Powered || creditPowered.WhToConsume != 0)
            failures.Add("prepaid half-Wh credit must be usable for the next one-item powered move");

        return failures;
    }

    private static IReadOnlyList<string> TestPoweredInterfaceRange()
    {
        var failures = new List<string>();
        var prototypeOffsets = PoweredMachineInterfaceRules.GetOffsets(PoweredMachineTier.Iridium, powered: false);
        if (prototypeOffsets.Count != 4 || prototypeOffsets.Any(offset => Math.Abs(offset.X) + Math.Abs(offset.Y) != 1))
            failures.Add("unpowered Powered Machine Interface must degrade to the SVSAP prototype orthogonal 4-neighbor scan");

        foreach (var (tier, expectedCount) in new[]
                 {
                     (PoweredMachineTier.Copper, 8),
                     (PoweredMachineTier.Steel, 24),
                     (PoweredMachineTier.Gold, 48),
                     (PoweredMachineTier.Iridium, 80)
                 })
        {
            var offsets = PoweredMachineInterfaceRules.GetOffsets(tier, powered: true);
            if (offsets.Count != expectedCount)
                failures.Add($"{tier} powered interface offset count should be {expectedCount} for its square range");
        }

        if (PoweredMachineInterfaceRules.CanRunPoweredAction(0))
            failures.Add("powered interface should not run a powered action with zero stored Wh");

        if (!PoweredMachineInterfaceRules.CanRunPoweredAction(1))
            failures.Add("powered interface should run one powered action with one stored Wh");

        return failures;
    }

    private static IReadOnlyList<string> TestBatteryDischargeGate()
    {
        var failures = new List<string>();
        if (BatteryDischargerRules.CanDischarge(enabled: false, availableBatteryPacks: 1, storedWh: 0, capacityWh: 8_000, outputWh: 8_000))
            failures.Add("Battery Discharger must stay off while AllowBatteryDischarge is false");

        if (BatteryDischargerRules.CanDischarge(enabled: true, availableBatteryPacks: 0, storedWh: 0, capacityWh: 8_000, outputWh: 8_000))
            failures.Add("Battery Discharger must not run without a Battery Pack");

        if (BatteryDischargerRules.CanDischarge(enabled: true, availableBatteryPacks: 1, storedWh: 1, capacityWh: 8_000, outputWh: 8_000))
            failures.Add("Battery Discharger must require full free capacity for its output");

        if (!BatteryDischargerRules.CanDischarge(enabled: true, availableBatteryPacks: 1, storedWh: 0, capacityWh: 8_000, outputWh: 8_000))
            failures.Add("Battery Discharger should run with one Battery Pack and exactly 8 kWh free capacity");

        return failures;
    }

    private static IReadOnlyList<string> TestElectricMachineRules()
    {
        var failures = new List<string>();
        if (ElectricMachineRules.FurnaceWhPerRun != 500)
            failures.Add("Electric Furnace must cost 0.50 kWh per powered run");

        if (ElectricMachineRules.GeodeCrusherWhPerRun != 600)
            failures.Add("Electric Geode Crusher must cost 0.60 kWh per powered geode");

        if (!ElectricMachineRules.TryGetFurnaceRecipe("(O)378", out var copper)
            || copper.InputCount != 5
            || copper.OutputQualifiedItemId != "(O)334"
            || copper.OutputCount != 1)
        {
            failures.Add("Electric Furnace must include the copper ore to copper bar recipe");
        }

        if (!ElectricMachineRules.TryGetFurnaceRecipe("(O)82", out var fireQuartz)
            || fireQuartz.OutputQualifiedItemId != "(O)338"
            || fireQuartz.OutputCount != 3)
        {
            failures.Add("Electric Furnace must preserve Fire Quartz -> 3 Refined Quartz output");
        }

        if (ElectricMachineRules.GetPoweredMinutes(30) != 15 || ElectricMachineRules.GetPoweredMinutes(1) != 1)
            failures.Add("Electric Furnace powered duration must be half rounded up with a one-minute floor");

        return failures;
    }

    private static readonly IReadOnlyList<string> ImplementedCases =
    new[]
    {
        "wh-roundtrip",
        "tier-table",
        "content-table",
        "api-shape",
        "config-surface",
        "cell-stack-guard",
        "machine-guid-reconcile",
        "orphan-reclaim",
        "claim-force-gate",
        "consumed-charged-retire",
        "disassembly-energy-policy",
        "missing-machine-reclaim",
        "multiplayer-protocol",
        "action-idempotent",
        "escrow-restore",
        "host-action-dispatch",
        "energy-production-rules",
        "synth-atomic",
        "farm-crop-set",
        "farm-power-freeze",
        "farm-daily-progress",
        "farm-single-crop-budget",
        "farm-module-economy",
        "farm-fertilizer-quality",
        "farm-locked-output",
        "daily-order-storage-gate",
        "location-cache-full-enum",
        "building-demolish-reclaim",
        "powered-prescan-refund",
        "powered-degrade-parity",
        "powered-interface-range",
        "battery-discharge-gate",
        "electric-machine-rules",
        "b10-parity",
        "no-arbitrage-audit"
    };
}
