using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using Koizumi.SVSAPME.Api;
using SVSAPME.Content;
using SVSAPME.Models;
using SVSAPME.UI;

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
            "durable-action-ledger" => TestDurableActionLedger(),
            "escrow-restore" => TestEscrowRestore(),
            "host-action-dispatch" => TestHostActionDispatch(),
            "energy-production-rules" => TestEnergyProductionRules(),
            "synth-atomic" => TestSynthAtomicRules(),
            "farm-crop-set" => TestFarmCropSet(),
            "farm-power-freeze" => TestFarmPowerFreeze(),
            "farm-daily-progress" => TestFarmDailyProgress(),
            "farm-mixed-lock-production" => TestFarmMixedLockProduction(),
            "farm-single-crop-budget" => TestFarmEnergyBudgetRules(),
            "farm-module-economy" => TestFarmModuleEconomy(),
            "farm-fertilizer-quality" => TestFarmFertilizerQuality(),
            "farm-locked-output" => TestFarmLockedOutput(),
            "single-block-processor-rules" => TestSingleBlockProcessorRules(),
            "malformed-buffer-normalization" => TestMalformedBufferNormalization(),
            "daily-order-storage-gate" => TestDailyOrderStorageGate(),
            "location-cache-full-enum" => TestLocationCacheFullEnum(),
            "building-demolish-reclaim" => TestBuildingDemolishReclaim(),
            "powered-prescan-refund" => TestPoweredPrescanRefund(),
            "powered-degrade-parity" => TestPoweredDegradeParity(),
            "powered-interface-range" => TestPoweredInterfaceRange(),
            "battery-discharge-gate" => TestBatteryDischargeGate(),
            "electric-machine-rules" => TestElectricMachineRules(),
            "gui-layout-bounds" => TestGuiLayoutBounds(),
            "single-block-real-state-text-fit" => TestSingleBlockRealStateTextFit(),
            "machine-port-definition-contract" => TestMachinePortDefinitionContract(),
            "energy-telemetry-contract" => TestEnergyTelemetryContract(),
            "b10-parity" => SvsapmeBalanceTable.ValidateParity(),
            "no-arbitrage-audit" => SvsapmeBalanceTable.ValidateNoArbitrage(),
            _ => new[] { $"case is not implemented in the SVSAPME selftest harness: {testCase}" }
        };
    }

    private static IReadOnlyList<string> TestGuiLayoutBounds()
    {
        var failures = new List<string>();

        var frame = new Rectangle(0, 0, 720, 640);
        var safeContent = SvsapmeUiText.GetFrameContentBounds(frame);
        if (safeContent != new Rectangle(36, 76, 648, 528))
            failures.Add("SVSAPME GUI content must use the shared frame-safe rectangle");
        if (SvsapmeUiText.VisualContentGutter < 8)
            failures.Add("SVSAPME GUI controls must keep at least 8 visible pixels clear of the metal bevel");

        if (!SingleBlockProcessorMenu.LayoutFits(menuWidth: 980, menuHeight: 700)
            || !SingleBlockProcessorMenu.LayoutFits(menuWidth: 752, menuHeight: 672)
            || !SingleBlockProcessorMenu.LayoutFits(menuWidth: 752, menuHeight: 672, inventorySlotCount: 48)
            || !SingleBlockProcessorMenu.LayoutFits(menuWidth: 752, menuHeight: 672, inventorySlotCount: 60))
        {
            failures.Add("single-block processor menu must keep input panel, work grid, output panel, and page buttons inside 1280x720 and 800x720 UI bounds");
        }

        if (!SingleBlockProcessorMenu.PortStripFits(menuWidth: 980)
            || !SingleBlockProcessorMenu.PortStripFits(menuWidth: 752)
            || !SingleBlockProcessorMenu.PortStripFits(menuWidth: 592)
            || !RemoteMachineControlMenu.ProcessorPortStripFits(menuWidth: 980)
            || !RemoteMachineControlMenu.ProcessorPortStripFits(menuWidth: 608)
            || !RemoteMachineControlMenu.NestedWorkHeaderKeepsSafeGutter())
        {
            failures.Add("local and remote single-block processor ports and modules must fit their header strip with a visible nested-panel gutter");
        }

        var processorCompact = SingleBlockProcessorMenu.CalculateLayoutShape(menuWidth: 752, menuHeight: 672);
        if (processorCompact.Columns < 5 || processorCompact.Rows < 4 || processorCompact.PageSize != processorCompact.Columns * processorCompact.Rows)
            failures.Add("single-block processor compact layout must preserve a useful paged work-grid above the 12x3 backpack");

        var processorExtended = SingleBlockProcessorMenu.CalculateLayoutShape(menuWidth: 752, menuHeight: 672, inventorySlotCount: 60);
        if (processorExtended.Columns < 5 || processorExtended.Rows < 2 || processorExtended.PageSize != processorExtended.Columns * processorExtended.Rows)
            failures.Add("single-block processor compact layout must remain usable with a 60-slot extended backpack");

        if (!SingleBlockFarmMenu.LayoutFits(menuWidth: 930, menuHeight: 660)
            || !SingleBlockFarmMenu.LayoutFits(menuWidth: 752, menuHeight: 660)
            || !SingleBlockFarmMenu.LayoutFits(menuWidth: 752, menuHeight: 660, inventorySlotCount: 48)
            || !SingleBlockFarmMenu.LayoutFits(menuWidth: 752, menuHeight: 660, inventorySlotCount: 60))
        {
            failures.Add("single-block farm menu must keep input panel, plot grid, output panel, and page buttons inside 1280x720 and 800x720 UI bounds");
        }

        var farmCompact = SingleBlockFarmMenu.CalculateLayoutShape(menuWidth: 752, menuHeight: 660);
        if (farmCompact.Columns < 6 || farmCompact.Rows < 5 || farmCompact.PageSize != farmCompact.Columns * farmCompact.Rows)
            failures.Add("single-block farm compact layout must preserve a useful paged plot-grid above the 12x3 backpack");

        var farmExtended = SingleBlockFarmMenu.CalculateLayoutShape(menuWidth: 752, menuHeight: 660, inventorySlotCount: 60);
        if (farmExtended.Columns < 6 || farmExtended.Rows < 3 || farmExtended.PageSize != farmExtended.Columns * farmExtended.Rows)
            failures.Add("single-block farm compact layout must remain usable with a 60-slot extended backpack");

        if (!PoweredTransferMenu.LayoutFits(menuWidth: 1040)
            || !PoweredTransferMenu.LayoutFits(menuWidth: 720)
            || !PoweredTransferMenu.LayoutFits(menuWidth: 720, menuHeight: 640, inventorySlotCount: 60)
            || !PoweredTransferMenu.LayoutFits(menuWidth: 592, menuHeight: 672, inventorySlotCount: 60))
            failures.Add("powered importer/exporter menu controls must wrap instead of overflowing compact UI widths");

        if (!RemoteMachineControlMenu.PoweredLayoutFits(menuWidth: 720, contentHeight: 374)
            || !RemoteMachineControlMenu.PoweredLayoutFits(menuWidth: 608, contentHeight: 250))
            failures.Add("remote powered-transfer upgrades and controls must stay separated at 800x720 and 640x720 with a 60-slot backpack");

        var compactControls = SvsapmeUiText.CalculateControlButtonBounds(new Rectangle(0, 0, 152, 180), topOffset: 92, count: 5);
        if (compactControls.Any(control => control.X < 0 || control.Y < 0 || control.Right > 152 || control.Bottom > 180)
            || compactControls.SelectMany((left, index) => compactControls.Skip(index + 1).Select(right => left.Intersects(right))).Any(intersects => intersects))
        {
            failures.Add("single-block side-panel controls must reflow without overlap when a 60-slot backpack shortens the work area");
        }

        if (RemoteMachineControlMenu.GetNextFacingDirection(-1) != 0
            || RemoteMachineControlMenu.GetNextFacingDirection(0) != 1
            || RemoteMachineControlMenu.GetNextFacingDirection(1) != 2
            || RemoteMachineControlMenu.GetNextFacingDirection(2) != 3
            || RemoteMachineControlMenu.GetNextFacingDirection(3) != -1)
        {
            failures.Add("remote powered-transfer direction cycling must include the all-sides state");
        }

        if (RemoteMachineControlMenu.ResolveEnergyStatus(false, string.Empty, 0, 0) != PixelStatus.Offline
            || RemoteMachineControlMenu.ResolveEnergyStatus(true, "warning", 1000, 1000) != PixelStatus.Warning
            || RemoteMachineControlMenu.ResolveEnergyStatus(true, string.Empty, 50, 1000) != PixelStatus.Warning
            || RemoteMachineControlMenu.ResolveEnergyStatus(true, string.Empty, 500, 1000) != PixelStatus.Ready)
        {
            failures.Add("energy status lights must prioritize offline and telemetry warnings over the generic online state");
        }

        return failures;
    }

    private static IReadOnlyList<string> TestSingleBlockRealStateTextFit()
    {
        var failures = new List<string>();

        if (SvsapmeUiText.FormatInputMode(MachineInputModes.AllEligible).Equals(MachineInputModes.AllEligible, StringComparison.Ordinal)
            || SvsapmeUiText.FormatFilterMode(MachineFilterModes.Whitelist).Equals(MachineFilterModes.Whitelist, StringComparison.Ordinal))
        {
            failures.Add("single-block menus must render localized short labels instead of raw enum names");
        }

        if (typeof(SvsapmeUiText).GetMethod("DrawFittedLine") is null
            || typeof(SvsapmeUiText).GetMethod("DrawFittedLines") is null)
        {
            failures.Add("single-block menus must use fitted small-font drawing helpers for localized status text");
        }

        if (SvsapmeUiText.FormatDayValue(123456m).Contains("Day ", StringComparison.Ordinal))
            failures.Add("single-block daily value label must use compact localized text instead of the old Day prefix");

        if (SvsapmeUiText.FormatItemCount(999) != "999"
            || SvsapmeUiText.FormatItemCount(1000) != "1K"
            || SvsapmeUiText.FormatItemCount(999_999) != "1M"
            || SvsapmeUiText.FormatItemCount(1_000_000) != "1M")
        {
            failures.Add("machine item badges must keep vanilla counts below 1000 and use only K/M abbreviations for large values");
        }

        return failures;
    }

    private static IReadOnlyList<string> TestMachinePortDefinitionContract()
    {
        var failures = new List<string>();
        var machineIds = new[]
        {
            ModItemCatalog.CarbonGenerator,
            ModItemCatalog.SolarNetworkPanel,
            ModItemCatalog.LightningCapacitor,
            ModItemCatalog.CopperEnergyCell,
            ModItemCatalog.IridiumEnergyCell,
            ModItemCatalog.BatterySynthesizer,
            ModItemCatalog.BatteryDischarger,
            ModItemCatalog.EnergyMonitorTerminal,
            ModItemCatalog.PoweredImporterCopper,
            ModItemCatalog.PoweredExporterCopper,
            ModItemCatalog.PoweredMachineInterfaceCopper,
            ModItemCatalog.ElectricFurnace,
            ModItemCatalog.ElectricGeodeCrusher,
            ModItemCatalog.CopperFarm,
            ModItemCatalog.IridiumFarm,
            ModItemCatalog.CopperKeg,
            ModItemCatalog.IridiumKeg,
            ModItemCatalog.CopperCask,
            ModItemCatalog.IridiumCask
        };

        foreach (var machineId in machineIds)
        {
            if (!MachinePortCatalog.HasRequiredPorts("(BC)" + machineId))
                failures.Add($"{machineId} must have visible machine port definitions");
        }

        var carbonPorts = MachinePortCatalog.GetPorts("(BC)" + ModItemCatalog.CarbonGenerator);
        if (!carbonPorts.Any(port => port.RoleKey == "ui.machine.port.fuelIn")
            || !carbonPorts.Any(port => port.RoleKey == "ui.machine.port.energyOut"))
        {
            failures.Add("Carbon Generator must expose fuel input and energy output ports");
        }

        var processorPorts = MachinePortCatalog.GetPorts("(BC)" + ModItemCatalog.IridiumKeg);
        if (processorPorts.Count != 3
            || !processorPorts.Any(port => port.RoleKey == "ui.machine.port.itemIn")
            || !processorPorts.Any(port => port.RoleKey == "ui.machine.port.itemOut")
            || !processorPorts.Any(port => port.RoleKey == "ui.machine.port.energyIn"))
        {
            failures.Add("single-block processors must expose visible item input, item output, and energy input slots");
        }

        return failures;
    }

    private static IReadOnlyList<string> TestEnergyTelemetryContract()
    {
        var failures = new List<string>();
        var telemetry = new EnergyTelemetryService();
        var networkId = Guid.NewGuid();
        telemetry.RecordDeposit(networkId, "test-producer", "selftest", 1000, 750, SvsapmeEnergyErrorCode.None, "ok");
        telemetry.RecordConsume(networkId, "test-consumer", "selftest", 250, 250, SvsapmeEnergyErrorCode.None, "ok");
        telemetry.RecordDeposit(networkId, "test-producer", "full", 100, 0, SvsapmeEnergyErrorCode.StorageFull, "storage full");
        var snapshot = telemetry.GetSnapshot(networkId);

        if (snapshot.TodayGeneratedWh != 750 || snapshot.TodayConsumedWh != 250 || snapshot.TodayNetWh != 500)
            failures.Add("energy telemetry must aggregate generated, consumed, and net Wh for the current day");
        if (snapshot.TopProducers.Count == 0
            || snapshot.TopProducers[0].Reason != "selftest"
            || snapshot.TopProducers[0].DeviceId != "test-producer:selftest")
        {
            failures.Add("energy telemetry must keep producer device and reason totals");
        }
        if (snapshot.TopConsumers.Count == 0
            || snapshot.TopConsumers[0].Reason != "selftest"
            || snapshot.TopConsumers[0].DeviceId != "test-consumer:selftest")
        {
            failures.Add("energy telemetry must keep consumer device and reason totals");
        }
        if (string.IsNullOrWhiteSpace(snapshot.LastWarning))
            failures.Add("energy telemetry must preserve the latest user-visible warning");

        return failures;
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
                     (nameof(SvsapmeEnergyErrorCode.InternalError), 6),
                     (nameof(SvsapmeEnergyErrorCode.NoEnergyCell), 7)
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
            nameof(ModConfig.EnableAutomaticProcessorInputFromNetwork),
            nameof(ModConfig.EnableAutomaticProcessorOutputToNetwork),
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
            SvsapmeMultiplayerMessageTypes.MachineDeliveryAck,
            SvsapmeMultiplayerMessageTypes.MachineItemMovementReport,
            SvsapmeMultiplayerMessageTypes.EnergyDebugRequest,
            SvsapmeMultiplayerMessageTypes.EnergyDebugResponse
        };

        if (messageTypes.Any(string.IsNullOrWhiteSpace) || messageTypes.Distinct(StringComparer.Ordinal).Count() != 8)
            failures.Add("SVSAPME multiplayer message set must contain eight distinct message names");

        if (messageTypes.Any(type => !type.StartsWith("Svsapme", StringComparison.Ordinal)))
            failures.Add("SVSAPME multiplayer messages must use SVSAPME names and never forge Koizumi.SVSAP messages");

        if (typeof(SvsapmeMachineSnapshotResponse).GetProperty(nameof(SvsapmeMachineSnapshotResponse.DisplayName))?.PropertyType != typeof(string)
            || typeof(SvsapmeMachineSnapshotResponse).GetProperty(nameof(SvsapmeMachineSnapshotResponse.Lines))?.PropertyType != typeof(List<string>))
        {
            failures.Add("machine snapshot responses must carry host-authored display text for farmhand status menus");
        }

        var sessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        if (!RemoteSnapshotSessionRules.Matches(sessionId, sessionId)
            || RemoteSnapshotSessionRules.Matches(Guid.Empty, Guid.Empty)
            || RemoteSnapshotSessionRules.Matches(sessionId, Guid.NewGuid())
            || !RemoteSnapshotSessionRules.IsNewer(7, 8)
            || RemoteSnapshotSessionRules.IsNewer(8, 8)
            || RemoteSnapshotSessionRules.IsNewer(9, 8)
            || RemoteSnapshotSessionRules.HasTimedOut(10, 19, 10)
            || !RemoteSnapshotSessionRules.HasTimedOut(10, 20, 10)
            || !RemoteSnapshotSessionRules.HasTimedOut(20, 10, 10)
            || !RemoteSnapshotSessionRules.ShouldOpenMenu(consumedPendingSession: true, hasActiveMenu: false)
            || RemoteSnapshotSessionRules.ShouldOpenMenu(consumedPendingSession: true, hasActiveMenu: true)
            || RemoteSnapshotSessionRules.ShouldOpenMenu(consumedPendingSession: false, hasActiveMenu: false))
        {
            failures.Add("machine snapshots must reject empty, duplicate, stale, mismatched, and already-consumed menu sessions");
        }

        if (typeof(SvsapmeMachineSnapshotRequest).GetProperty(nameof(SvsapmeMachineSnapshotRequest.MenuSessionId))?.PropertyType != typeof(Guid)
            || typeof(SvsapmeMachineSnapshotRequest).GetProperty(nameof(SvsapmeMachineSnapshotRequest.RequestSequence))?.PropertyType != typeof(long)
            || typeof(SvsapmeMachineSnapshotResponse).GetProperty(nameof(SvsapmeMachineSnapshotResponse.MenuSessionId))?.PropertyType != typeof(Guid)
            || typeof(SvsapmeMachineSnapshotResponse).GetProperty(nameof(SvsapmeMachineSnapshotResponse.RequestSequence))?.PropertyType != typeof(long))
        {
            failures.Add("machine snapshot requests and responses must carry menu-session and request-order fields");
        }

        if (typeof(SvsapmeMachineActionRequest).GetProperty(nameof(SvsapmeMachineActionRequest.MenuSessionId))?.PropertyType != typeof(Guid)
            || typeof(SvsapmeMachineActionRequest).GetProperty(nameof(SvsapmeMachineActionRequest.RequestSequence))?.PropertyType != typeof(long)
            || typeof(SvsapmeMachineActionResponse).GetProperty(nameof(SvsapmeMachineActionResponse.MenuSessionId))?.PropertyType != typeof(Guid)
            || typeof(SvsapmeMachineActionResponse).GetProperty(nameof(SvsapmeMachineActionResponse.RequestSequence))?.PropertyType != typeof(long))
        {
            failures.Add("machine action responses must be scoped to the menu session and request order that created them");
        }

        if (typeof(SvsapmeMachineSnapshotResponse).GetProperty(nameof(SvsapmeMachineSnapshotResponse.MenuKind))?.PropertyType != typeof(SvsapmeMachineMenuKind)
            || typeof(SvsapmeMachineSnapshotResponse).GetProperty(nameof(SvsapmeMachineSnapshotResponse.Farm))?.PropertyType != typeof(SvsapmeFarmMenuSnapshot)
            || typeof(SvsapmeMachineSnapshotResponse).GetProperty(nameof(SvsapmeMachineSnapshotResponse.Processor))?.PropertyType != typeof(SvsapmeProcessorMenuSnapshot)
            || typeof(SvsapmeMachineSnapshotResponse).GetProperty(nameof(SvsapmeMachineSnapshotResponse.PoweredTransfer))?.PropertyType != typeof(SvsapmePoweredTransferMenuSnapshot)
            || typeof(SvsapmeMachineSnapshotResponse).GetProperty(nameof(SvsapmeMachineSnapshotResponse.EnergyMonitor))?.PropertyType != typeof(SvsapmeEnergyMonitorSnapshot))
        {
            failures.Add("machine snapshots must carry structured farm, processor, powered-transfer, and energy-monitor menu state");
        }

        if (typeof(SvsapmeProcessorSlotSnapshot).GetProperty(nameof(SvsapmeProcessorSlotSnapshot.CanEject))?.PropertyType != typeof(bool)
            || typeof(SvsapmeProcessorSlotSnapshot).GetProperty(nameof(SvsapmeProcessorSlotSnapshot.CanCollect))?.PropertyType != typeof(bool))
        {
            failures.Add("processor snapshots must expose intermediate cask eject and collect-all state");
        }

        if (typeof(SvsapmeProcessorMenuSnapshot).GetProperty(nameof(SvsapmeProcessorMenuSnapshot.NetworkOnline))?.PropertyType != typeof(bool)
            || typeof(SvsapmeProcessorMenuSnapshot).GetProperty(nameof(SvsapmeProcessorMenuSnapshot.EnergyOnline))?.PropertyType != typeof(bool)
            || typeof(SvsapmeProcessorMenuSnapshot).GetProperty(nameof(SvsapmeProcessorMenuSnapshot.StoredWh))?.PropertyType != typeof(long)
            || typeof(SvsapmeProcessorMenuSnapshot).GetProperty(nameof(SvsapmeProcessorMenuSnapshot.CapacityWh))?.PropertyType != typeof(long)
            || typeof(SvsapmeProcessorMenuSnapshot).GetProperty(nameof(SvsapmeProcessorMenuSnapshot.RequiredWhForNextStep))?.PropertyType != typeof(long)
            || typeof(SvsapmeProcessorMenuSnapshot).GetProperty(nameof(SvsapmeProcessorMenuSnapshot.InstalledUpgradeQualifiedItemIds))?.PropertyType != typeof(List<string>)
            || typeof(SvsapmeProcessorMenuSnapshot).GetProperty(nameof(SvsapmeProcessorMenuSnapshot.UpgradeSlotCapacity))?.PropertyType != typeof(int)
            || typeof(SvsapmeProcessorMenuSnapshot).GetProperty(nameof(SvsapmeProcessorMenuSnapshot.SpeedPermille))?.PropertyType != typeof(int)
            || typeof(SvsapmeProcessorMenuSnapshot).GetProperty(nameof(SvsapmeProcessorMenuSnapshot.OutputBufferCapacityItems))?.PropertyType != typeof(int))
        {
            failures.Add("processor snapshots must expose live energy plus physical upgrade-slot state and effects");
        }

        if (typeof(SvsapmePoweredTransferMenuSnapshot).GetProperty(nameof(SvsapmePoweredTransferMenuSnapshot.InstalledUpgradeQualifiedItemIds))?.PropertyType != typeof(List<string>)
            || typeof(SvsapmePoweredTransferMenuSnapshot).GetProperty(nameof(SvsapmePoweredTransferMenuSnapshot.UpgradeSlotCapacity))?.PropertyType != typeof(int)
            || typeof(SvsapmePoweredTransferMenuSnapshot).GetProperty(nameof(SvsapmePoweredTransferMenuSnapshot.NetworkOnline))?.PropertyType != typeof(bool)
            || typeof(SvsapmePoweredTransferMenuSnapshot).GetProperty(nameof(SvsapmePoweredTransferMenuSnapshot.StoredWh))?.PropertyType != typeof(long)
            || typeof(SvsapmePoweredTransferMenuSnapshot).GetProperty(nameof(SvsapmePoweredTransferMenuSnapshot.CapacityWh))?.PropertyType != typeof(long))
        {
            failures.Add("powered-transfer snapshots must expose physical upgrade slots plus live network energy state");
        }

        if (typeof(SvsapmeEnergyDeviceSnapshot).GetProperty(nameof(SvsapmeEnergyDeviceSnapshot.Details))?.PropertyType != typeof(List<string>)
            || typeof(SvsapmeEnergyMonitorSnapshot).GetProperty(nameof(SvsapmeEnergyMonitorSnapshot.Online))?.PropertyType != typeof(bool)
            || typeof(SvsapmeEnergyMonitorSnapshot).GetProperty(nameof(SvsapmeEnergyMonitorSnapshot.StatusText))?.PropertyType != typeof(string))
            failures.Add("energy-device snapshots must carry localized port and usage tooltip details");

        if (typeof(SvsapmeMachineActionResponse).GetProperty(nameof(SvsapmeMachineActionResponse.ReturnedItems))?.PropertyType != typeof(List<BufferedItemStack>))
            failures.Add("machine action responses must carry returned item payloads for farmhand processor collection");

        if (typeof(SvsapmeMachineDeliveryAck).GetProperty(nameof(SvsapmeMachineDeliveryAck.TransactionId))?.PropertyType != typeof(Guid)
            || typeof(MachineSaveData).GetProperty(nameof(MachineSaveData.PendingRemoteDeliveries))?.PropertyType != typeof(List<PendingRemoteDelivery>))
        {
            failures.Add("farmhand returned-item deliveries must be backed by a persistent host ack protocol");
        }

        var nonPublicInstance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var nonPublicStatic = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        if (typeof(SvsapmeMultiplayerService).GetMethod("PruneConfirmedExpiredPendingDeliveries", nonPublicInstance)?.ReturnType != typeof(int)
            || typeof(SvsapmeMultiplayerService).GetMethod(nameof(SvsapmeMultiplayerService.OnDayStarted)) is null)
        {
            failures.Add("host pending remote deliveries must have a day-start expiry path that prunes confirmed stale payloads without automatic duplication");
        }

        if (typeof(SvsapmeMultiplayerService).GetMethod("QueueDurableRemoteDeliveryForPlayer", nonPublicInstance) is null
            || typeof(SvsapmeMultiplayerService).GetMethod("RestoreDurableRemoteDeliveries", nonPublicInstance)?.ReturnType != typeof(int))
        {
            failures.Add("expired unconfirmed SVSAPME deliveries must move to a player mailbox and restore on save load instead of staying in host pending forever");
        }

        if (typeof(SvsapmeMultiplayerService).GetField("PendingDeliveryRetentionDays", nonPublicStatic)?.GetRawConstantValue() is not int retentionDays
            || retentionDays <= 0)
        {
            failures.Add("pending remote delivery expiry must use a positive retention window");
        }

        if (typeof(SvsapmeMultiplayerService).GetField("DurableRemoteDeliveryModDataKey", nonPublicStatic)?.GetRawConstantValue() is not string deliveryMailboxKey
            || string.IsNullOrWhiteSpace(deliveryMailboxKey))
        {
            failures.Add("durable remote delivery mailbox must use a stable player modData key");
        }

        if (typeof(SvsapmeMultiplayerService).GetField("PersistentActionEscrowModDataKey", nonPublicStatic)?.GetRawConstantValue() is not string escrowKey
            || string.IsNullOrWhiteSpace(escrowKey)
            || typeof(SvsapmeMultiplayerService).GetMethod("RehydrateDurableActionEscrows", nonPublicInstance)?.ReturnType != typeof(int)
            || typeof(SvsapmeMultiplayerService).GetMethod("TrackDurableActionEscrow", nonPublicInstance)?.ReturnType != typeof(bool))
        {
            failures.Add("farmhand item escrow must rehydrate and replay the same transaction across save/load or crash windows");
        }

        if (typeof(SvsapmeMultiplayerService).GetMethod(nameof(SvsapmeMultiplayerService.OnUpdateTicked)) is null
            || typeof(SvsapmeMultiplayerService).GetField("ClientActionResponseTimeoutTicks", nonPublicStatic)?.GetRawConstantValue() is not int timeoutTicks
            || timeoutTicks <= 0
            || typeof(SvsapmeMultiplayerService).GetField("ClientActionRetryLimit", nonPublicStatic)?.GetRawConstantValue() is not int retryLimit
            || retryLimit <= 0)
        {
            failures.Add("farmhand action reconciliation must use a positive timeout and bounded retry limit");
        }

        if (typeof(MachineSaveData).GetProperty(nameof(MachineSaveData.SchemaVersion))?.PropertyType != typeof(int))
            failures.Add("SVSAPME machine save data must carry a schema version");

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
        cache.Remember(7, tx1, new SvsapmeMachineActionResponse { TransactionId = tx1, Message = "later duplicate" });
        if (!cache.TryGet(7, tx1, out var replay) || replay.Message != "first")
            failures.Add("action cache must retain and replay the first transaction response");

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

    private static IReadOnlyList<string> TestDurableActionLedger()
    {
        var failures = new List<string>();
        const long playerId = 77112233L;
        var transactionId = Guid.NewGuid();
        var machineGuid = Guid.NewGuid();
        var entries = new List<ExecutedMachineAction>();
        var first = new ExecutedMachineAction
        {
            PlayerId = playerId,
            TransactionId = transactionId,
            MachineGuid = machineGuid,
            ActionKind = SvsapmeMachineActionKind.LoadProcessorInput.ToString(),
            Message = "first",
            ConsumeEscrowedItem = true
        };
        if (!ExecutedMachineActionLedger.Remember(entries, first))
            failures.Add("first consumed remote machine action must enter the durable ledger");

        var duplicate = new ExecutedMachineAction
        {
            PlayerId = playerId,
            TransactionId = transactionId,
            MachineGuid = machineGuid,
            ActionKind = SvsapmeMachineActionKind.LoadProcessorInput.ToString(),
            Message = "later duplicate",
            ConsumeEscrowedItem = true
        };
        if (ExecutedMachineActionLedger.Remember(entries, duplicate))
            failures.Add("duplicate consumed transaction ids must not overwrite the first durable result");
        if (!ExecutedMachineActionLedger.TryGet(entries, playerId, transactionId, out var replay)
            || !ReferenceEquals(replay, first)
            || replay.Message != "first")
        {
            failures.Add("durable machine action replay must retain the first committed response");
        }

        entries.Add(duplicate);
        entries.Add(new ExecutedMachineAction());
        if (!ExecutedMachineActionLedger.Normalize(entries))
            failures.Add("durable machine action normalization must remove invalid and duplicate records");
        if (entries.Count != 1 || entries[0].Message != "first")
            failures.Add("durable machine action normalization must keep the first committed response");

        return failures;
    }

    private static IReadOnlyList<string> TestMalformedBufferNormalization()
    {
        var failures = new List<string>();
        var valid = new BufferedItemStack
        {
            QualifiedItemId = "(O)388",
            Stack = 5,
            PreservedParentSheetIndex = null!,
            Type = null!,
            Name = null!,
            DisplayName = null!,
            ModData = null!
        };
        var stacks = new List<BufferedItemStack>
        {
            new() { QualifiedItemId = string.Empty, Stack = 1 },
            new() { QualifiedItemId = "(O)390", Stack = 0 },
            valid
        };
        var discarded = 0;
        if (!MachineStateRepository.NormalizeBufferedList(stacks, ref discarded)
            || discarded != 2
            || stacks.Count != 1
            || !ReferenceEquals(stacks[0], valid))
        {
            failures.Add("load normalization must discard blank-id and non-positive buffered stacks while preserving valid stacks");
        }
        if (valid.PreservedParentSheetIndex is null
            || valid.Type is null
            || valid.Name is null
            || valid.DisplayName is null
            || valid.ModData is null)
        {
            failures.Add("load normalization must initialize optional buffered item metadata");
        }

        var processor = new SingleBlockProcessorMachineState
        {
            Slots = new List<SingleBlockProcessorSlotState>
            {
                new()
                {
                    Input = new BufferedItemStack { QualifiedItemId = "(O)433", Stack = 5 },
                    Output = new BufferedItemStack { QualifiedItemId = string.Empty, Stack = 1 },
                    InputCount = 5,
                    RemainingMinutes = 120,
                    TotalMinutes = 120
                },
                new()
                {
                    Input = new BufferedItemStack { QualifiedItemId = string.Empty, Stack = 1 },
                    Output = new BufferedItemStack { QualifiedItemId = "(O)395", Stack = 1 },
                    InputCount = 5,
                    RemainingMinutes = 0,
                    TotalMinutes = 120
                }
            }
        };
        discarded = 0;
        if (!MachineStateRepository.NormalizeProcessorSlots(processor, ref discarded)
            || discarded != 2
            || processor.InputBuffer.Count != 1
            || processor.InputBuffer[0].QualifiedItemId != "(O)433"
            || processor.OutputBuffer.Count != 1
            || processor.OutputBuffer[0].QualifiedItemId != "(O)395"
            || processor.Slots.Any(SingleBlockProcessorRules.IsWorking))
        {
            failures.Add("malformed processor slots must preserve each valid counterpart in the matching recovery buffer");
        }

        return failures;
    }

    private static IReadOnlyList<string> TestEscrowRestore()
    {
        var failures = new List<string>();
        if (!SvsapmeActionEscrowRules.ActionMayEscrowHeldItem(SvsapmeMachineActionKind.LoadFarmSeed)
            || !SvsapmeActionEscrowRules.ActionMayEscrowHeldItem(SvsapmeMachineActionKind.LoadFarmFertilizer)
            || !SvsapmeActionEscrowRules.ActionMayEscrowHeldItem(SvsapmeMachineActionKind.InstallFarmModule)
            || !SvsapmeActionEscrowRules.ActionMayEscrowHeldItem(SvsapmeMachineActionKind.PlantFarmPlot)
            || !SvsapmeActionEscrowRules.ActionMayEscrowHeldItem(SvsapmeMachineActionKind.FuelCarbonGenerator)
            || !SvsapmeActionEscrowRules.ActionMayEscrowHeldItem(SvsapmeMachineActionKind.StartElectricFurnace)
            || !SvsapmeActionEscrowRules.ActionMayEscrowHeldItem(SvsapmeMachineActionKind.StartElectricGeodeCrusher)
            || !SvsapmeActionEscrowRules.ActionMayEscrowHeldItem(SvsapmeMachineActionKind.LoadProcessorInput)
            || !SvsapmeActionEscrowRules.ActionMayEscrowHeldItem(SvsapmeMachineActionKind.InstallProcessorUpgrade)
            || !SvsapmeActionEscrowRules.ActionMayEscrowHeldItem(SvsapmeMachineActionKind.InstallPoweredUpgrade))
        {
            failures.Add("item-bearing SVSAPME machine actions must be escrow candidates");
        }

        if (SvsapmeActionEscrowRules.ActionMayEscrowHeldItem(SvsapmeMachineActionKind.ConfigurePoweredFilter))
            failures.Add("powered transfer ghost filters must not consume or escrow the selected inventory item");

        if (SvsapmeActionEscrowRules.GetPrimaryEscrowCount(SvsapmeMachineActionKind.StartElectricFurnace, 5) != 5
            || SvsapmeActionEscrowRules.GetPrimaryEscrowCount(SvsapmeMachineActionKind.StartElectricGeodeCrusher, 1) != 1)
        {
            failures.Add("electric machine farmhand actions must escrow the authoritative input count");
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
        var startFurnace = typeof(MachineRuntimeService).GetMethod("TryStartElectricFurnaceManualUse", nonPublicInstance);
        var startGeodeCrusher = typeof(MachineRuntimeService).GetMethod("TryStartElectricGeodeCrusherManualUse", nonPublicInstance);
        var installPoweredUpgrade = typeof(MachineRuntimeService).GetMethod("TryInstallPoweredUpgrade", nonPublicInstance);
        var removePoweredUpgrade = typeof(MachineRuntimeService).GetMethod("TryRemovePoweredUpgrade", nonPublicInstance);
        var installProcessorUpgrade = typeof(SingleBlockProcessorService).GetMethod("TryInstallProcessorUpgrade", nonPublicInstance);
        var removeProcessorUpgrade = typeof(SingleBlockProcessorService).GetMethod("TryRemoveProcessorUpgrade", nonPublicInstance);
        var loadSeed = typeof(SingleBlockFarmService).GetMethod("TryLoadSeed", nonPublicInstance);
        var loadFertilizer = typeof(SingleBlockFarmService).GetMethod("TryLoadFertilizer", nonPublicInstance);
        var installModule = typeof(SingleBlockFarmService).GetMethod("TryInstallModule", nonPublicInstance);
        if (configure?.ReturnType != typeof(SvsapmeMachineActionApplyResult))
            failures.Add("MachineRuntimeService must expose host-dispatchable powered filter configuration");

        if (fuel?.ReturnType != typeof(SvsapmeMachineActionApplyResult))
            failures.Add("MachineRuntimeService must expose host-dispatchable Carbon Generator fueling");

        if (startFurnace?.ReturnType != typeof(SvsapmeMachineActionApplyResult))
            failures.Add("MachineRuntimeService must expose host-dispatchable Electric Furnace starts");

        if (startGeodeCrusher?.ReturnType != typeof(SvsapmeMachineActionApplyResult))
            failures.Add("MachineRuntimeService must expose host-dispatchable Electric Geode Crusher starts");

        if (installPoweredUpgrade?.ReturnType != typeof(SvsapmeMachineActionApplyResult)
            || removePoweredUpgrade?.ReturnType != typeof(SvsapmeMachineActionApplyResult))
        {
            failures.Add("MachineRuntimeService must expose host-dispatchable powered upgrade insert/eject actions");
        }

        if (installProcessorUpgrade?.ReturnType != typeof(SvsapmeMachineActionApplyResult)
            || removeProcessorUpgrade?.ReturnType != typeof(SvsapmeMachineActionApplyResult))
        {
            failures.Add("SingleBlockProcessorService must expose host-dispatchable processor upgrade insert/eject actions");
        }

        if (loadSeed?.ReturnType != typeof(SvsapmeMachineActionApplyResult))
            failures.Add("SingleBlockFarmService must expose host-dispatchable seed loading");

        if (loadFertilizer?.ReturnType != typeof(SvsapmeMachineActionApplyResult))
            failures.Add("SingleBlockFarmService must expose host-dispatchable fertilizer loading");

        if (installModule?.ReturnType != typeof(SvsapmeMachineActionApplyResult))
            failures.Add("SingleBlockFarmService must expose host-dispatchable module installation");

        if (typeof(SvsapmeMultiplayerService).GetMethod("TryResolveHostActionContext", nonPublicInstance) is null)
            failures.Add("SvsapmeMultiplayerService must validate MachineGuid, tile, and active SVSAP endpoint before host actions");

        var endpointPolicy = typeof(SvsapmeMultiplayerService).GetMethod("RequiresActiveEndpoint", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        if (endpointPolicy?.ReturnType != typeof(bool))
        {
            failures.Add("SvsapmeMultiplayerService must expose an endpoint policy for farmhand host actions");
        }
        else
        {
            var collectRequiresEndpoint = (bool)(endpointPolicy.Invoke(null, new object[] { SvsapmeMachineActionKind.CollectProcessorOutput }) ?? true);
            var furnaceRequiresEndpoint = (bool)(endpointPolicy.Invoke(null, new object[] { SvsapmeMachineActionKind.StartElectricFurnace }) ?? false);
            if (collectRequiresEndpoint || !furnaceRequiresEndpoint)
                failures.Add("farmhand processor collection must not require an active endpoint, while energy-consuming host actions still must");
        }

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

        var snapshotRequestMethods = typeof(SvsapmeMultiplayerService).GetMethods(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        if (!snapshotRequestMethods.Any(method => method.Name == "TrySendMachineSnapshotRequest" && method.ReturnType == typeof(bool))
            || typeof(SvsapmeMultiplayerService).GetMethod("CreateMachineSnapshotResponse", nonPublicInstance)?.ReturnType != typeof(SvsapmeMachineSnapshotResponse))
        {
            failures.Add("SvsapmeMultiplayerService must expose host-authored machine snapshots for farmhand status menus");
        }

        if (typeof(MachineRuntimeService).GetMethod("SetClientActionSender") is null
            || typeof(SingleBlockFarmService).GetMethod("SetClientActionSender") is null)
        {
            failures.Add("interactive SVSAPME services must bind farmhand clicks to multiplayer action requests");
        }

        if (typeof(MachineRuntimeService).GetMethod("SetSnapshotRequestSender") is null
            || typeof(SingleBlockFarmService).GetMethod("SetSnapshotRequestSender") is null)
        {
            failures.Add("interactive SVSAPME services must bind farmhand empty-hand status clicks to host snapshots");
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

    private static IReadOnlyList<string> TestFarmMixedLockProduction()
    {
        var failures = new List<string>();
        var parsnip = FarmCropCatalog.AcceptanceCrops.First(candidate => candidate.SeedQualifiedItemId == "(O)472");
        var ancient = FarmCropCatalog.AcceptanceCrops.First(candidate => candidate.SeedQualifiedItemId == "(O)499");
        var tier = EnergyTierTable.Farms.Single(entry => entry.Tier == EnergyTier.Copper);
        var modules = new FarmModuleSnapshot(0, 1.0m, false, 1.0m, 0, 1);
        var farm = new FarmMachineState { PlacedByFarmingLevel = 10 };
        farm.PlotLocks[3] = ancient.SeedQualifiedItemId;
        farm.InputBuffer.Add(BufferedItemCodec.FromItem(ItemRegistry.Create(ancient.SeedQualifiedItemId)));
        farm.InputBuffer.Add(BufferedItemCodec.FromItem(ItemRegistry.Create(parsnip.SeedQualifiedItemId)));

        var order = SingleBlockFarmService.GetMixedFarmPlantingOrder(farm, tier.Plots);
        if (order.Count == 0 || order[0] != 3)
            failures.Add("mixed farm production planner must service locked empty plots before unlocked plots");

        if (!SingleBlockFarmService.TryTakeBufferedSeed(farm, modules, "spring", farm.PlotLocks[3], out var lockedSeed, out var lockedCrop)
            || lockedCrop.SeedQualifiedItemId != ancient.SeedQualifiedItemId)
        {
            failures.Add("locked mixed farm plot must consume only its matching buffered seed");
        }
        else
        {
            var plannedLocked = new PlannedFarmSeed(lockedSeed, lockedCrop, FarmSeedSource.InputBuffer, 10, 3);
            if (!SingleBlockFarmService.TryApplyPlannedFarmSeed(farm, tier, plannedLocked)
                || farm.Plots.Single(plot => plot.PlotIndex == 3) is not { IsLocked: true } lockedPlot
                || lockedPlot.LockedSeedQualifiedItemId != ancient.SeedQualifiedItemId)
            {
                failures.Add("production planting helper must preserve the locked crop identity on the created plot");
            }
        }

        if (!SingleBlockFarmService.TryTakeBufferedSeed(farm, modules, "spring", string.Empty, out var openSeed, out var openCrop)
            || openCrop.SeedQualifiedItemId != parsnip.SeedQualifiedItemId
            || !SingleBlockFarmService.TryApplyPlannedFarmSeed(farm, tier, new PlannedFarmSeed(openSeed, openCrop, FarmSeedSource.InputBuffer, 10, 0)))
        {
            failures.Add("mixed farm production helper must plant a different eligible crop into an unlocked plot");
        }

        foreach (var plot in farm.Plots)
        {
            var crop = plot.PlotIndex == 3 ? ancient : parsnip;
            plot.ProgressUnits = FarmGrowthRules.GetRequiredProgressUnits(crop.BaseGrowthDays) - FarmGrowthRules.GetDailyProgressUnits(1.0m, 1.0m);
        }

        var output = new List<BufferedItemStack>();
        if (!SingleBlockFarmService.ApplyMixedFarmGrowth(farm, output, modules, "spring"))
            failures.Add("mixed farm production growth helper must advance and harvest active plots");

        if (!farm.PlotLocks.TryGetValue(3, out var lockedSeedId)
            || lockedSeedId != ancient.SeedQualifiedItemId
            || farm.Plots.All(plot => plot.PlotIndex != 3))
        {
            failures.Add("locked regrow plot must remain assigned to its crop after production-path harvest");
        }

        if (farm.Plots.Any(plot => plot.PlotIndex == 0)
            || output.All(stack => stack.QualifiedItemId != parsnip.HarvestQualifiedItemId)
            || output.All(stack => stack.QualifiedItemId != ancient.HarvestQualifiedItemId))
        {
            failures.Add("mixed production harvest must remove non-regrow plots and buffer each crop's own output");
        }

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

    private static IReadOnlyList<string> TestSingleBlockProcessorRules()
    {
        var failures = new List<string>();

        var copperKegId = "(BC)" + ModItemCatalog.CopperKeg;
        var iridiumKegId = "(BC)" + ModItemCatalog.IridiumKeg;
        if (SingleBlockProcessorRules.GetProcessorKind(copperKegId) != SingleBlockProcessorKind.Keg)
            failures.Add("copper single-block keg must be identified as a processor keg");

        if (SingleBlockProcessorRules.GetTier(copperKegId).Slots != 16
            || SingleBlockProcessorRules.GetTier(iridiumKegId).Slots != 256)
        {
            failures.Add("single-block processor tiers must expose 16/64/144/256 internal slots");
        }

        var copperTier = SingleBlockProcessorRules.GetTier(copperKegId);
        var steelTier = SingleBlockProcessorRules.GetTier("(BC)" + ModItemCatalog.SteelKeg);
        var goldTier = SingleBlockProcessorRules.GetTier("(BC)" + ModItemCatalog.GoldKeg);
        var iridiumTier = SingleBlockProcessorRules.GetTier(iridiumKegId);
        if (ProcessorUpgradeRules.GetSlotCapacity(copperTier) != 2
            || ProcessorUpgradeRules.GetSlotCapacity(steelTier) != 3
            || ProcessorUpgradeRules.GetSlotCapacity(goldTier) != 4
            || ProcessorUpgradeRules.GetSlotCapacity(iridiumTier) != 5)
        {
            failures.Add("processor upgrade slots must scale 2/3/4/5 from copper through iridium");
        }

        var copperUpgradeProcessor = new SingleBlockProcessorMachineState();
        ProcessorUpgradeRules.TryInstall(
            copperUpgradeProcessor,
            copperTier,
            SingleBlockProcessorKind.Keg,
            "(O)" + ModItemCatalog.SvsapSpeedCard);
        ProcessorUpgradeRules.TryInstall(
            copperUpgradeProcessor,
            copperTier,
            SingleBlockProcessorKind.Keg,
            "(O)" + ModItemCatalog.SvsapCapacityCard);
        if (ProcessorUpgradeRules.TryInstall(
                copperUpgradeProcessor,
                copperTier,
                SingleBlockProcessorKind.Keg,
                "(O)" + ModItemCatalog.SvsapSpeedCard).Success)
        {
            failures.Add("a copper processor must reject a third upgrade after its two physical slots are occupied");
        }

        var upgradeProcessor = new SingleBlockProcessorMachineState();
        var speed1 = ProcessorUpgradeRules.TryInstall(
            upgradeProcessor,
            iridiumTier,
            SingleBlockProcessorKind.Keg,
            "(O)" + ModItemCatalog.SvsapSpeedCard);
        var speed2 = ProcessorUpgradeRules.TryInstall(
            upgradeProcessor,
            iridiumTier,
            SingleBlockProcessorKind.Keg,
            "(O)" + ModItemCatalog.SvsapSpeedCard);
        var capacity = ProcessorUpgradeRules.TryInstall(
            upgradeProcessor,
            iridiumTier,
            SingleBlockProcessorKind.Keg,
            "(O)" + ModItemCatalog.SvsapCapacityCard);
        if (!speed1.Success || !speed1.ConsumesItem || !speed2.Success || !capacity.Success)
            failures.Add("processor speed/capacity cards must install into physical upgrade slots and consume one card");
        if (ProcessorUpgradeRules.GetSpeedPermille(upgradeProcessor) != 1200
            || ProcessorUpgradeRules.CalculateScaledWork(10, 1200, 0, out var speedRemainder) != 12
            || speedRemainder != 0)
        {
            failures.Add("two processor speed cards must advance 20% more deterministic work without fractional loss");
        }
        if (ProcessorUpgradeRules.GetOutputBufferCapacityItems(upgradeProcessor, iridiumTier) != 256)
            failures.Add("one capacity card must provide one full iridium processor batch of completed-output buffering");

        var qualityProcessor = new SingleBlockProcessorMachineState();
        var qualityInstall = ProcessorUpgradeRules.TryInstall(
            qualityProcessor,
            iridiumTier,
            SingleBlockProcessorKind.Keg,
            "(O)" + ModItemCatalog.SvsapQualityCard);
        var duplicateQuality = ProcessorUpgradeRules.TryInstall(
            qualityProcessor,
            iridiumTier,
            SingleBlockProcessorKind.Keg,
            "(O)" + ModItemCatalog.SvsapQualityCard);
        var caskQuality = ProcessorUpgradeRules.TryInstall(
            new SingleBlockProcessorMachineState(),
            iridiumTier,
            SingleBlockProcessorKind.Cask,
            "(O)" + ModItemCatalog.SvsapQualityCard);
        if (!qualityInstall.Success || duplicateQuality.Success || caskQuality.Success)
            failures.Add("kegs may install one quality card, while casks must reject it as meaningless");

        var goldCoffeeProbe = ItemRegistry.Create("(O)433", 1);
        goldCoffeeProbe.Quality = 2;
        if (!SingleBlockProcessorService.TryCreateAutomatedInputJob(SingleBlockProcessorKind.Keg, goldCoffeeProbe, out var automatedCoffee)
            || SingleBlockProcessorService.GetProcessorInputCount(SingleBlockProcessorKind.Keg, goldCoffeeProbe) != 5
            || automatedCoffee.InputCount != 5)
        {
            failures.Add("network auto-input probing must recognize the five-coffee-bean recipe from a stack-one inventory prototype");
        }
        else
        {
            ProcessorUpgradeRules.ApplyJobModifiers(qualityProcessor, SingleBlockProcessorKind.Keg, automatedCoffee);
            if (!ProcessorUpgradeRules.PreservesKegInputQuality(qualityProcessor)
                || automatedCoffee.Output?.Quality != 2)
            {
                failures.Add("a keg quality card must preserve gold input quality for automatically probed jobs");
            }
        }

        var processor = new SingleBlockProcessorMachineState();
        if (processor.AutoPullFromNetwork)
            failures.Add("single-block processors must default network auto-input to off until the player opts in");

        SingleBlockProcessorRules.NormalizeSlots(processor, SingleBlockProcessorRules.GetTier(iridiumKegId));
        if (processor.Slots.Count != 256)
            failures.Add("iridium processor state must normalize to 256 slots");

        var apple = ItemRegistry.Create("(O)613", 1);
        if (!SingleBlockProcessorRules.TryCreateJob(SingleBlockProcessorKind.Keg, apple, out var wineSlot, out _))
        {
            failures.Add("single-block keg must accept fruit as wine input");
        }
        else
        {
            if (wineSlot.Output?.QualifiedItemId != "(O)348" || wineSlot.Output.PreservedParentSheetIndex != "613")
                failures.Add("single-block keg wine output must preserve the fruit parent sheet index");

            if (wineSlot.Output?.PreserveType != (int)StardewValley.Object.PreserveType.Wine)
                failures.Add("single-block keg wine output must preserve vanilla preserve type identity");

            var vanillaWine = ItemRegistry.GetObjectTypeDefinition().CreateFlavoredWine((StardewValley.Object)apple.getOne());
            var restoredWine = BufferedItemCodec.CreateItem(wineSlot.Output!);
            if (restoredWine.DisplayName != vanillaWine.DisplayName || restoredWine.salePrice(false) != vanillaWine.salePrice(false))
                failures.Add("single-block keg wine output must use vanilla flavored output naming and pricing");

            if (wineSlot.RemainingMinutes != 10_000 || wineSlot.TotalMinutes != 10_000)
                failures.Add("single-block keg fruit processing time must match vanilla wine time");

            var goldApple = ItemRegistry.Create("(O)613", 1);
            goldApple.Quality = 2;
            if (!SingleBlockProcessorRules.TryCreateJob(SingleBlockProcessorKind.Keg, goldApple, out var qualityWineSlot, out _))
            {
                failures.Add("quality-card test fruit must create a keg job");
            }
            else
            {
                ProcessorUpgradeRules.ApplyJobModifiers(qualityProcessor, SingleBlockProcessorKind.Keg, qualityWineSlot);
                if (qualityWineSlot.Output?.Quality != 2)
                    failures.Add("a keg quality card must preserve the input ingredient quality on output");
            }

            if (SingleBlockProcessorRules.GetRecoverableStack(wineSlot)?.QualifiedItemId != "(O)613")
                failures.Add("unfinished single-block processor slots must recover the input, not the future output");

            SingleBlockProcessorRules.AdvanceKegSlot(wineSlot, 10_000);
            if (!SingleBlockProcessorRules.IsReady(wineSlot))
                failures.Add("single-block keg slot must become ready after its full minute budget is paid");

            if (SingleBlockProcessorRules.GetRecoverableStack(wineSlot)?.QualifiedItemId != "(O)348")
                failures.Add("finished single-block processor slots must recover the completed output");

            var overflowProcessor = new SingleBlockProcessorMachineState();
            var overflowInput = ItemRegistry.Create("(O)613", 1);
            if (SingleBlockProcessorRules.TryCreateJob(SingleBlockProcessorKind.Keg, overflowInput, out var overflowSlot, out _))
            {
                SingleBlockProcessorRules.MoveSlotPayloadToBuffer(overflowProcessor, overflowSlot);
                if (overflowProcessor.OutputBuffer.Count != 1 || overflowProcessor.OutputBuffer[0].QualifiedItemId != "(O)613")
                    failures.Add("processor overflow recovery must preserve unfinished input instead of creating output early");
            }
        }

        var shortCoffee = ItemRegistry.Create("(O)433", 4);
        if (SingleBlockProcessorRules.TryCreateJob(SingleBlockProcessorKind.Keg, shortCoffee, out _, out _))
            failures.Add("coffee recipe must require five coffee beans");

        var coffee = ItemRegistry.Create("(O)433", 5);
        if (!SingleBlockProcessorRules.TryCreateJob(SingleBlockProcessorKind.Keg, coffee, out var coffeeSlot, out _)
            || coffeeSlot.InputCount != 5
            || coffeeSlot.Output?.QualifiedItemId != "(O)395"
            || coffeeSlot.RemainingMinutes != 120)
        {
            failures.Add("coffee recipe must consume five beans and produce coffee after 120 minutes");
        }

        var wine = ItemRegistry.Create("(O)348", 1);
        wine.Quality = 0;
        if (!SingleBlockProcessorRules.TryCreateJob(SingleBlockProcessorKind.Cask, wine, out var caskSlot, out _)
            || caskSlot.Output?.Quality != 0
            || caskSlot.RemainingDays != 56)
        {
            failures.Add("single-block cask must begin normal wine at its real current quality with 56 days remaining");
        }
        else
        {
            SingleBlockProcessorRules.AdvanceCaskSlot(caskSlot, 14);
            if (caskSlot.Output?.Quality != 1 || !SingleBlockProcessorRules.CanEjectCaskOutput(caskSlot))
                failures.Add("single-block cask must expose a manual eject point when wine reaches silver quality");
            if (SingleBlockProcessorRules.GetRecoverableStack(caskSlot)?.Quality != 1)
                failures.Add("interrupting a cask after a milestone must recover the achieved quality");

            SingleBlockProcessorRules.AdvanceCaskSlot(caskSlot, 14);
            if (caskSlot.Output?.Quality != 2 || !SingleBlockProcessorRules.CanEjectCaskOutput(caskSlot))
                failures.Add("single-block cask must expose a manual eject point when wine reaches gold quality");

            SingleBlockProcessorRules.AdvanceCaskSlot(caskSlot, 28);
            if (caskSlot.Output?.Quality != 4 || !SingleBlockProcessorRules.IsReady(caskSlot))
                failures.Add("single-block cask must finish wine at iridium quality after 56 days");
        }

        var iridiumWine = ItemRegistry.Create("(O)348", 1);
        iridiumWine.Quality = 4;
        if (SingleBlockProcessorRules.TryCreateJob(SingleBlockProcessorKind.Cask, iridiumWine, out _, out _))
            failures.Add("single-block cask must reject already-iridium input");

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

        var farmInputBufferState = new MachineState
        {
            MachineGuid = Guid.Parse("fafafafa-fafa-fafa-fafa-fafafafafafa"),
            QualifiedItemId = "(BC)" + ModItemCatalog.CopperFarm,
            Farm =
            {
                InputBuffer = { new BufferedItemStack { QualifiedItemId = "(O)472", Stack = 4 } }
            }
        };
        if (MachineRegistryService.CanRetireConfirmedConsumedMachine(farmInputBufferState))
            failures.Add("consumed-candidate farms with buffered input stacks must stay recoverable instead of retiring");

        var processorInputBufferState = new MachineState
        {
            MachineGuid = Guid.Parse("fbfbfbfb-fbfb-fbfb-fbfb-fbfbfbfbfbfb"),
            QualifiedItemId = "(BC)" + ModItemCatalog.CopperKeg,
            Processor =
            {
                InputBuffer = { new BufferedItemStack { QualifiedItemId = "(O)433", Stack = 5 } }
            }
        };
        if (MachineRegistryService.CanRetireConfirmedConsumedMachine(processorInputBufferState))
            failures.Add("consumed-candidate processors with buffered input stacks must stay recoverable instead of retiring");

        var processorUpgradeState = new MachineState
        {
            MachineGuid = Guid.Parse("fcfcfcfc-fcfc-fcfc-fcfc-fcfcfcfcfcfc"),
            QualifiedItemId = "(BC)" + ModItemCatalog.CopperKeg,
            Processor =
            {
                InstalledUpgradeQualifiedItemIds = { "(O)" + ModItemCatalog.SvsapSpeedCard }
            }
        };
        if (MachineRegistryService.CanRetireConfirmedConsumedMachine(processorUpgradeState))
            failures.Add("consumed-candidate processors with installed upgrade cards must stay recoverable instead of retiring");

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
            ModItemCatalog.CopperKeg,
            ModItemCatalog.SteelKeg,
            ModItemCatalog.GoldKeg,
            ModItemCatalog.IridiumKeg,
            ModItemCatalog.CopperCask,
            ModItemCatalog.SteelCask,
            ModItemCatalog.GoldCask,
            ModItemCatalog.IridiumCask,
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

        var capacityBoosted = PoweredTransferRules.PlanImporterExporter(
            sourceAvailable: 64,
            targetCapacity: 64,
            PoweredMachineTier.Copper,
            storedWh: 16,
            halfWhCredit: 0,
            prototypeThroughput: 4,
            poweredThroughputMultiplier: 2);
        if (capacityBoosted.Mode != PoweredTransferRunMode.Powered
            || capacityBoosted.PlannedItems != 32
            || capacityBoosted.WhToConsume != 16)
        {
            failures.Add("capacity-card multiplier must double paid powered throughput without changing per-item energy cost");
        }

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

        var nonPublicStatic = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        if (typeof(MachineRuntimeService).GetMethod("GetChestSlotCapacity", nonPublicStatic)?.ReturnType != typeof(int)
            || typeof(MachineRuntimeService).GetMethod("TryGetChestActualCapacity", nonPublicStatic)?.ReturnType != typeof(int))
        {
            failures.Add("powered importer/exporter chest capacity must use the game's actual chest capacity path before falling back to vanilla baselines");
        }

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
        "durable-action-ledger",
        "escrow-restore",
        "host-action-dispatch",
        "energy-production-rules",
        "synth-atomic",
        "farm-crop-set",
        "farm-power-freeze",
        "farm-daily-progress",
        "farm-mixed-lock-production",
        "farm-single-crop-budget",
        "farm-module-economy",
        "farm-fertilizer-quality",
        "farm-locked-output",
        "single-block-processor-rules",
        "malformed-buffer-normalization",
        "daily-order-storage-gate",
        "location-cache-full-enum",
        "building-demolish-reclaim",
        "powered-prescan-refund",
        "powered-degrade-parity",
        "powered-interface-range",
        "battery-discharge-gate",
        "electric-machine-rules",
        "gui-layout-bounds",
        "single-block-real-state-text-fit",
        "machine-port-definition-contract",
        "energy-telemetry-contract",
        "b10-parity",
        "no-arbitrage-audit"
    };
}
