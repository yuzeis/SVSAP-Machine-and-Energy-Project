using System.Text.Json;
using Koizumi.SVSAP.Api;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Objects;
using SVSAPME.Content;
using SVSAPME.Models;
using SObject = StardewValley.Object;

namespace SVSAPME.Services;

internal sealed class SvsapmeP0P1E2EService
{
    private const string RoleEnv = "STARDEW_SVSAPME_P0P1_E2E_ROLE";
    private const string OutputDirEnv = "STARDEW_SVSAPME_P0P1_E2E_OUTPUT";
    private const string VersionEnv = "STARDEW_SVSAPME_P0P1_E2E_VERSION";
    private const string DefaultVersionLabel = "ver1.2-alpha.2";
    private const string SvsapNetworkIdKey = ModItemCatalog.SvsapUniqueId + "/NetworkId";
    private const string SvsapEndpointIdKey = ModItemCatalog.SvsapUniqueId + "/EndpointId";
    private const string PoweredTransferHalfWhCreditKey = ModItemCatalog.UniqueId + "/PoweredTransferHalfWhCredit";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly MachineStateRepository repository;
    private readonly MachineRegistryService registry;
    private readonly EnergyNetworkManager energy;
    private readonly MachineRuntimeService runtime;
    private readonly Func<ISvsapApi?> getSvsapApi;
    private readonly Func<ModConfig> getConfig;
    private readonly string role;
    private readonly string outputDir;
    private readonly string versionLabel;
    private readonly List<E2EResult> results = new();

    private bool started;
    private bool stopped;
    private int stage;
    private int stageTicks;
    private SingleFixture? single;
    private MultiFixture? multi;
    private int r6PrototypeMoved;
    private long r6PrototypeEnergyBefore;
    private long r6PrototypeEnergyAfter;
    private int r6TargetCapacityBeforeRoute;
    private PoweredTransferRunMode r6PlannedModeBeforeRoute;
    private int r6PlannedItemsBeforeRoute;

    public SvsapmeP0P1E2EService(
        IModHelper helper,
        IMonitor monitor,
        MachineStateRepository repository,
        MachineRegistryService registry,
        EnergyNetworkManager energy,
        MachineRuntimeService runtime,
        Func<ISvsapApi?> getSvsapApi,
        Func<ModConfig> getConfig)
    {
        this.helper = helper;
        this.monitor = monitor;
        this.repository = repository;
        this.registry = registry;
        this.energy = energy;
        this.runtime = runtime;
        this.getSvsapApi = getSvsapApi;
        this.getConfig = getConfig;
        this.role = (Environment.GetEnvironmentVariable(RoleEnv) ?? string.Empty).Trim().ToLowerInvariant();
        this.outputDir = Environment.GetEnvironmentVariable(OutputDirEnv) ?? string.Empty;
        this.versionLabel = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VersionEnv))
            ? DefaultVersionLabel
            : Environment.GetEnvironmentVariable(VersionEnv)!;
    }

    private bool IsEnabled => this.role is "single" or "host" or "client";

    public void Start()
    {
        if (!this.IsEnabled || this.started)
            return;

        this.started = true;
        this.runtime.SuppressAutomaticRouteTicksForE2E = true;
        if (!string.IsNullOrWhiteSpace(this.outputDir))
            Directory.CreateDirectory(this.outputDir);

        this.helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        this.monitor.Log($"SVSAPME_P0P1_E2E started role={this.role} version={this.versionLabel} output=\"{this.outputDir}\"", LogLevel.Info);
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
        if (this.stopped || !Context.IsWorldReady)
            return;

        try
        {
            switch (this.role)
            {
                case "single" when Context.IsMainPlayer:
                    this.TickSingle();
                    break;
                case "host" when Context.IsMainPlayer:
                    this.TickHost();
                    break;
                case "client" when !Context.IsMainPlayer:
                    this.TickClient();
                    break;
            }
        }
        catch (Exception ex)
        {
            this.Record("exception", false, $"{ex.GetType().Name}: {ex.Message}");
            this.WriteResults($"{this.role}-fail.json");
            this.monitor.Log($"SVSAPME_P0P1_E2E_FAIL role={this.role} stage={this.stage} {ex}", LogLevel.Error);
            this.Stop();
        }
    }

    private void TickSingle()
    {
        this.stageTicks++;
        if (this.stage == 0)
        {
            this.getConfig().EnergyTickInterval = 1;
            this.single = this.CreateSingleFixture();
            this.stage = 10;
            this.stageTicks = 0;
            return;
        }

        if (this.stage == 10)
        {
            if (this.stageTicks < 30)
                return;

            this.RunR1ToR5(this.single ?? throw new InvalidOperationException("single fixture missing"));
            this.PrepareR6(this.single!, storedWh: 0);
            this.stage = 20;
            this.stageTicks = 0;
            return;
        }

        if (this.stage == 20)
        {
            if (this.stageTicks < 5)
                return;

            this.CheckR6Prototype(this.single!);
            this.PrepareR6(this.single!, storedWh: 1_000);
            this.stage = 30;
            this.stageTicks = 0;
            return;
        }

        if (this.stage == 30)
        {
            if (this.stageTicks < 5)
                return;

            this.CheckR6Powered(this.single!);
            this.WriteResults("single-complete.json");
            this.Stop();
        }
    }

    private void TickHost()
    {
        this.stageTicks++;
        if (this.stage == 0)
        {
            this.multi = this.CreateMultiFixture();
            this.WritePayload("host-ready.json", MultiFixturePayload.FromFixture(this.multi));
            this.stage = 10;
            this.stageTicks = 0;
            return;
        }

        if (this.stage == 10)
        {
            if (!this.Exists("client-replayed.json"))
                return;

            this.registry.ReconcileMissingMachinesOnDayStarted();
            var cellState = this.GetState(this.multi!.CellGuid);
            var location = this.GetFarm();
            var cellPlaced = location.Objects.TryGetValue(this.multi.CellTile, out var placed)
                && placed.QualifiedItemId == "(BC)" + ModItemCatalog.CopperEnergyCell
                && TryReadMachineGuid(placed, out var placedGuid)
                && placedGuid == this.multi.CellGuid;
            this.Record(
                "M1",
                cellPlaced && cellState.StoredWh == this.multi.CellStoredWh,
                $"farmhand replay cellPlaced={cellPlaced} storedWh={cellState.StoredWh} expected={this.multi.CellStoredWh}");
            this.WritePayload("host-m1-verified.json", new { ok = this.results.Last().Pass });
            this.stage = 20;
            this.stageTicks = 0;
            return;
        }

        if (this.stage == 20)
        {
            if (!this.Exists("client-held.json"))
                return;

            this.registry.ReconcileMissingMachinesOnDayStarted();
            this.registry.ReconcileMissingMachinesOnDayStarted();
            var noNaturalPending = !this.repository.Data.PendingReclaims.SelectMany(reclaim => reclaim.MachineGuids).Contains(this.multi!.FarmGuid);
            this.repository.Data.PendingReclaims.Add(new PendingReclaimCrate
            {
                ReclaimId = Guid.NewGuid(),
                Reason = "e2e-force",
                OriginalLocationName = "Farm",
                TileX = (int)this.multi.FarmTile.X,
                TileY = (int)this.multi.FarmTile.Y,
                MachineGuids = { this.multi.FarmGuid }
            });
            var forceClaimed = this.registry.TryClaimPendingReclaims(Game1.player, includeUnconfirmed: true, out var machines, out _, out var message);
            var pendingAfterForce = this.repository.Data.PendingReclaims.SelectMany(reclaim => reclaim.MachineGuids).Contains(this.multi.FarmGuid);
            this.Record(
                "M2",
                noNaturalPending && !forceClaimed && machines == 0 && !pendingAfterForce,
                $"farmhand-held noNaturalPending={noNaturalPending} forceClaimed={forceClaimed} machines={machines} pendingAfterForce={pendingAfterForce} message=\"{message}\"");
            this.WriteResults("host-complete.json");
            this.Stop();
        }
    }

    private void TickClient()
    {
        this.stageTicks++;
        if (this.stage == 0)
        {
            if (!this.Exists("host-ready.json"))
                return;

            this.multi = this.ReadPayload<MultiFixturePayload>("host-ready.json").ToFixture();
            this.stage = 10;
            this.stageTicks = 0;
            return;
        }

        if (this.stage == 10)
        {
            var location = this.GetFarm();
            var cell = this.PickUpMachineToPlayer(location, this.multi!.CellTile);
            this.PlaceMachineFromPlayer(location, this.multi.CellTile, this.multi.CellGuid, "(BC)" + ModItemCatalog.CopperEnergyCell);
            this.WritePayload("client-replayed.json", new
            {
                cellQualifiedItemId = cell.QualifiedItemId,
                machineGuid = this.multi.CellGuid.ToString("N"),
                storedWh = cell.modData.GetValueOrDefault(MachineRegistryService.StoredWhKey)
            });
            this.stage = 20;
            this.stageTicks = 0;
            return;
        }

        if (this.stage == 20)
        {
            if (!this.Exists("host-m1-verified.json"))
                return;

            var location = this.GetFarm();
            var farm = this.PickUpMachineToPlayer(location, this.multi!.FarmTile);
            this.WritePayload("client-held.json", new
            {
                farmQualifiedItemId = farm.QualifiedItemId,
                machineGuid = this.multi.FarmGuid.ToString("N")
            });
            this.stage = 30;
            this.stageTicks = 0;
            return;
        }

        if (this.stage == 30)
        {
            if (!this.Exists("host-complete.json"))
                return;

            this.WritePayload("client-complete.json", new { ok = true, version = this.versionLabel });
            this.Stop();
        }
    }

    private void RunR1ToR5(SingleFixture fixture)
    {
        this.RunR1(fixture);
        this.RunR2(fixture);
        this.RunR3();
        this.RunR4(fixture);
        this.RunR5();
    }

    private void RunR1(SingleFixture fixture)
    {
        var depositOk = this.energy.TryDepositWh(fixture.NetworkId, 5_000, ModItemCatalog.UniqueId, "e2e-r1-deposit", out var accepted, out _, out _);
        var consumeOk = this.energy.TryConsumeWh(fixture.NetworkId, 1_234, ModItemCatalog.UniqueId, "e2e-r1-consume", allowPartial: false, out var consumed, out _, out _);
        var beforeReload = this.GetState(fixture.CellGuid).StoredWh;
        this.repository.Save();
        this.repository.Load();
        this.registry.RebuildCache();
        this.energy.TryGetNetworkEnergy(fixture.NetworkId, out var storedAfterReload, out _, out _);

        var picked = this.PickUpMachineToPlayer(fixture.Location, fixture.CellTile);
        this.registry.ReconcileMissingMachinesOnDayStarted();
        var itemWh = long.TryParse(picked.modData.GetValueOrDefault(MachineRegistryService.StoredWhKey), out var parsed) ? parsed : -1;
        this.PlaceSpecificMachineItem(fixture.Location, fixture.CellTile, picked);

        this.Record(
            "R1",
            depositOk && accepted == 5_000 && consumeOk && consumed == 1_234 && beforeReload == 3_766 && storedAfterReload == 3_766 && itemWh == 3_766,
            $"accepted={accepted} consumed={consumed} beforeReload={beforeReload} afterReload={storedAfterReload} itemStoredWh={itemWh}");
    }

    private void RunR2(SingleFixture fixture)
    {
        var before = this.GetState(fixture.CellGuid).StoredWh;
        var picked = this.PickUpMachineToPlayer(fixture.Location, fixture.CellTile);
        this.registry.ReconcileMissingMachinesOnDayStarted();
        var pickedWh = long.TryParse(picked.modData.GetValueOrDefault(MachineRegistryService.StoredWhKey), out var parsed) ? parsed : -1;
        this.PlaceSpecificMachineItem(fixture.Location, fixture.CellTile, picked);
        this.registry.TryRegisterPlacedMachine((SObject)picked, fixture.Location, fixture.CellTile);
        var replayWh = this.GetState(fixture.CellGuid).StoredWh;

        var held = this.PickUpMachineToPlayer(fixture.Location, fixture.CellTile);
        this.registry.ReconcileMissingMachinesOnDayStarted();
        this.registry.ReconcileMissingMachinesOnDayStarted();
        var noPending = !this.repository.Data.PendingReclaims.SelectMany(reclaim => reclaim.MachineGuids).Contains(fixture.CellGuid);
        var claim = this.registry.TryClaimPendingReclaims(Game1.player, out var machines, out _, out var message);
        this.PlaceSpecificMachineItem(fixture.Location, fixture.CellTile, held);

        this.Record(
            "R2",
            pickedWh == before && replayWh == before && noPending && !claim && machines == 0,
            $"before={before} pickedWh={pickedWh} replayWh={replayWh} noPending={noPending} claim={claim} machines={machines} message=\"{message}\"");
    }

    private void RunR3()
    {
        var farmHouse = Game1.getLocationFromName("FarmHouse") as StardewValley.Locations.FarmHouse
            ?? throw new InvalidOperationException("FarmHouse not available for fridge test.");
        var fridge = farmHouse.fridge.Value ?? throw new InvalidOperationException("FarmHouse fridge not available.");
        var item = this.CreateStatefulMachineItem("(BC)" + ModItemCatalog.CopperFarm, out var guid);
        fridge.Items.Add(item);
        this.registry.ReconcileMissingMachinesOnDayStarted();
        this.registry.ReconcileMissingMachinesOnDayStarted();
        var noNaturalPending = !this.repository.Data.PendingReclaims.SelectMany(reclaim => reclaim.MachineGuids).Contains(guid);

        this.repository.Data.PendingReclaims.Add(new PendingReclaimCrate
        {
            ReclaimId = Guid.NewGuid(),
            Reason = "e2e-force",
            OriginalLocationName = "FarmHouse",
            TileX = 0,
            TileY = 0,
            MachineGuids = { guid }
        });
        var forceClaim = this.registry.TryClaimPendingReclaims(Game1.player, includeUnconfirmed: true, out var machines, out _, out var message);
        var pendingAfterForce = this.repository.Data.PendingReclaims.SelectMany(reclaim => reclaim.MachineGuids).Contains(guid);
        fridge.Items.Remove(item);
        this.repository.Data.Machines.Remove(guid);
        this.repository.Data.PendingReclaims.RemoveAll(reclaim => reclaim.MachineGuids.Contains(guid));

        this.Record(
            "R3",
            noNaturalPending && !forceClaim && machines == 0 && !pendingAfterForce,
            $"fridgeHeld=true noNaturalPending={noNaturalPending} forceClaim={forceClaim} machines={machines} pendingAfterForce={pendingAfterForce} message=\"{message}\"");
    }

    private void RunR4(SingleFixture fixture)
    {
        var statefulFarm = this.CreateStatefulMachineItem("(BC)" + ModItemCatalog.CopperFarm, out var guid);
        var plainFarm = ItemRegistry.Create("(BC)" + ModItemCatalog.CopperFarm, 1);
        var playerStackRejected = !statefulFarm.canStackWith(plainFarm) && !plainFarm.canStackWith(statefulFarm);

        ResetChestSlots(fixture.TargetChest);
        var insertItem = statefulFarm.getOne();
        insertItem.modData[MachineRegistryService.MachineGuidKey] = guid.ToString("N");
        var inserted = this.getSvsapApi()!.TryInsertItem(fixture.NetworkId, insertItem, out var remainder, out _, out _);
        var inNetworkChest = fixture.TargetChest.Items.Any(item =>
            item is not null
            && item.QualifiedItemId == "(BC)" + ModItemCatalog.CopperFarm
            && item.modData.GetValueOrDefault(MachineRegistryService.MachineGuidKey) == guid.ToString("N"));

        for (var i = 0; i < fixture.TargetChest.Items.Count; i++)
        {
            var item = fixture.TargetChest.Items[i];
            if (item is not null
                && item.modData.GetValueOrDefault(MachineRegistryService.MachineGuidKey) == guid.ToString("N"))
            {
                fixture.TargetChest.Items[i] = null;
            }
        }
        this.repository.Data.Machines.Remove(guid);
        this.repository.Save();

        this.Record(
            "R4",
            playerStackRejected && inserted && remainder is null && inNetworkChest,
            $"stackRejected={playerStackRejected} inserted={inserted} remainder={(remainder?.Stack ?? 0)} networkChestHasGuid={inNetworkChest}");
    }

    private void RunR5()
    {
        this.repository.Data.PendingReclaims.Clear();
        var consumed = this.CreateStatefulMachineItem("(BC)" + ModItemCatalog.CopperEnergyCell, out var guid, storedWh: 4_000);
        Game1.player.addItemToInventory(consumed);
        this.registry.MarkPotentiallyConsumedMachineItem(consumed);
        this.RemoveFromPlayerInventory(guid);
        this.registry.ReconcileMissingMachinesOnDayStarted();
        this.registry.ReconcileMissingMachinesOnDayStarted();
        var retired = !this.repository.Data.Machines.ContainsKey(guid);

        this.repository.Data.PendingReclaims.Add(new PendingReclaimCrate
        {
            ReclaimId = Guid.NewGuid(),
            Reason = "e2e-force",
            OriginalLocationName = "Farm",
            TileX = 0,
            TileY = 0,
            MachineGuids = { guid }
        });
        var claim = this.registry.TryClaimPendingReclaims(Game1.player, includeUnconfirmed: true, out var machines, out _, out var message);

        this.Record(
            "R5",
            retired && !claim && machines == 0,
            $"consumedCandidateRetired={retired} claim={claim} machines={machines} message=\"{message}\"");
    }

    private void PrepareR6(SingleFixture fixture, long storedWh)
    {
        ResetChestSlots(fixture.TargetChest);
        fixture.SourceChest.Items.Clear();
        fixture.SourceChest.Items.Add(ItemRegistry.Create("(O)390", 100));
        var importerState = this.GetState(fixture.ImporterGuid);
        importerState.ModData.Remove(PoweredTransferHalfWhCreditKey);
        var cellState = this.GetState(fixture.CellGuid);
        cellState.StoredWh = storedWh;
        fixture.CellObject.modData[MachineRegistryService.StoredWhKey] = storedWh.ToString();
        this.repository.Save();
        this.energy.TryGetNetworkEnergy(fixture.NetworkId, out this.r6PrototypeEnergyBefore, out _, out _);
        var probe = ItemRegistry.Create("(O)390", 64);
        this.r6TargetCapacityBeforeRoute = this.getSvsapApi()!.GetInsertCapacity(fixture.NetworkId, probe, 64);
        var plan = PoweredTransferRules.PlanImporterExporter(
            sourceAvailable: 100,
            targetCapacity: this.r6TargetCapacityBeforeRoute,
            PoweredMachineTier.Copper,
            storedWh,
            halfWhCredit: 0,
            prototypeThroughput: 64);
        this.r6PlannedModeBeforeRoute = plan.Mode;
        this.r6PlannedItemsBeforeRoute = plan.PlannedItems;
    }

    private void CheckR6Prototype(SingleFixture fixture)
    {
        this.runtime.RunRouteTickForE2E();
        this.energy.TryGetNetworkEnergy(fixture.NetworkId, out this.r6PrototypeEnergyAfter, out _, out _);
        this.r6PrototypeMoved = CountItem(fixture.TargetChest, "(O)390");
        var importerActive = this.TryProbeEndpoint(fixture.Location, fixture.ImporterTile, out var importerProbe);
        this.Record(
            "R6-prototype",
            this.r6PrototypeMoved >= 64 && this.r6PrototypeEnergyBefore == this.r6PrototypeEnergyAfter,
            $"moved={this.r6PrototypeMoved} sourceRemaining={CountItem(fixture.SourceChest, "(O)390")} targetCapacityBefore={this.r6TargetCapacityBeforeRoute} plannedMode={this.r6PlannedModeBeforeRoute} plannedItems={this.r6PlannedItemsBeforeRoute} energyBefore={this.r6PrototypeEnergyBefore} energyAfter={this.r6PrototypeEnergyAfter} importerActive={importerActive} importerProbe=\"{importerProbe}\" cacheHasImporter={this.registry.MachinesByGuid.ContainsKey(fixture.ImporterGuid)}");
    }

    private void CheckR6Powered(SingleFixture fixture)
    {
        this.runtime.RunRouteTickForE2E();
        var energyReadOk = this.energy.TryGetNetworkEnergy(fixture.NetworkId, out var after, out var capacity, out var energyCode);
        var cellState = this.GetState(fixture.CellGuid);
        var cellStoredModData = fixture.CellObject.modData.GetValueOrDefault(MachineRegistryService.StoredWhKey);
        var cellActive = this.TryProbeEndpoint(fixture.Location, fixture.CellTile, out var cellProbe);
        var moved = CountItem(fixture.TargetChest, "(O)390");
        var spent = 1_000 - after;
        var importerActive = this.TryProbeEndpoint(fixture.Location, fixture.ImporterTile, out var importerProbe);
        this.Record(
            "R6",
            moved >= this.r6PrototypeMoved && moved >= 64 && spent * 2 == moved,
            $"poweredMoved={moved} sourceRemaining={CountItem(fixture.SourceChest, "(O)390")} targetCapacityBefore={this.r6TargetCapacityBeforeRoute} plannedMode={this.r6PlannedModeBeforeRoute} plannedItems={this.r6PlannedItemsBeforeRoute} prototypeMoved={this.r6PrototypeMoved} spentWh={spent} expectedHalfWh={moved} energyReadOk={energyReadOk} energyCode={energyCode} capacity={capacity} cellStateWh={cellState.StoredWh} cellModDataWh={cellStoredModData} cellActive={cellActive} cellProbe=\"{cellProbe}\" importerActive={importerActive} importerProbe=\"{importerProbe}\" cacheHasImporter={this.registry.MachinesByGuid.ContainsKey(fixture.ImporterGuid)}");
    }

    private SingleFixture CreateSingleFixture()
    {
        var location = this.GetFarm();
        var origin = FindClearBlock(location, 8, 4);
        ClearBlock(location, origin, 8, 4);
        var networkId = Guid.NewGuid();

        var coreTile = origin;
        var targetChestTile = origin + new Vector2(1, 0);
        var cellTile = origin + new Vector2(0, 1);
        var importerTile = origin + new Vector2(2, 0);
        var sourceChestTile = origin + new Vector2(3, 0);

        var core = this.PlaceLinkedMachine(location, coreTile, "(BC)" + ModItemCatalog.SvsapUniqueId + ".NetworkCore", networkId);
        var targetChest = this.PlaceLinkedChest(location, targetChestTile, networkId);
        var cell = this.PlaceLinkedMachine(location, cellTile, "(BC)" + ModItemCatalog.CopperEnergyCell, networkId);
        var importer = this.PlaceLinkedMachine(location, importerTile, "(BC)" + ModItemCatalog.PoweredImporterCopper, networkId);
        var sourceChest = this.PlaceChest(location, sourceChestTile);

        var cellGuid = this.RegisterMachine(cell, location, cellTile, storedWh: 0);
        var importerGuid = this.RegisterMachine(importer, location, importerTile, storedWh: 0);
        this.WritePayload("single-fixture.json", new
        {
            networkId = networkId.ToString("N"),
            coreTile = FormatTile(coreTile),
            targetChestTile = FormatTile(targetChestTile),
            cellTile = FormatTile(cellTile),
            importerTile = FormatTile(importerTile),
            sourceChestTile = FormatTile(sourceChestTile),
            core = core.QualifiedItemId
        });

        return new SingleFixture(location, networkId, targetChest, sourceChest, cell, cellTile, cellGuid, importerTile, importerGuid);
    }

    private MultiFixture CreateMultiFixture()
    {
        var location = this.GetFarm();
        var origin = FindClearBlock(location, 4, 3);
        ClearBlock(location, origin, 4, 3);
        var cellTile = origin;
        var farmTile = origin + new Vector2(1, 0);
        var cell = this.PlaceMachine(location, cellTile, "(BC)" + ModItemCatalog.CopperEnergyCell);
        var farm = this.PlaceMachine(location, farmTile, "(BC)" + ModItemCatalog.CopperFarm);
        var cellGuid = this.RegisterMachine(cell, location, cellTile, storedWh: 3_210);
        var farmGuid = this.RegisterMachine(farm, location, farmTile, storedWh: 0);
        cell.modData[MachineRegistryService.StoredWhKey] = "3210";
        return new MultiFixture(cellTile, farmTile, cellGuid, farmGuid, 3_210);
    }

    private SObject PlaceLinkedMachine(GameLocation location, Vector2 tile, string qualifiedItemId, Guid networkId)
    {
        if (ItemRegistry.Create(qualifiedItemId, 1) is not SObject obj)
            throw new InvalidOperationException($"Could not create placeable object {qualifiedItemId}.");

        StampEndpoint(obj, networkId);
        location.Objects[tile] = obj;
        return obj;
    }

    private SObject PlaceMachine(GameLocation location, Vector2 tile, string qualifiedItemId)
    {
        if (ItemRegistry.Create(qualifiedItemId, 1) is not SObject obj)
            throw new InvalidOperationException($"Could not create placeable object {qualifiedItemId}.");

        location.Objects[tile] = obj;
        return obj;
    }

    private Chest PlaceLinkedChest(GameLocation location, Vector2 tile, Guid networkId)
    {
        var chest = new Chest(CreateEmptyChestSlots(), tile, false, 0, false);
        StampEndpoint(chest, networkId);
        location.Objects[tile] = chest;
        return chest;
    }

    private Chest PlaceChest(GameLocation location, Vector2 tile)
    {
        var chest = new Chest(CreateEmptyChestSlots(), tile, false, 0, false);
        location.Objects[tile] = chest;
        return chest;
    }

    private static List<Item> CreateEmptyChestSlots()
    {
        var items = new List<Item>(36);
        for (var i = 0; i < 36; i++)
            items.Add(null!);

        return items;
    }

    private static void ResetChestSlots(Chest chest)
    {
        chest.Items.Clear();
        foreach (var item in CreateEmptyChestSlots())
            chest.Items.Add(item);
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
        state.CapacityWh = qualifiedItemId == "(BC)" + ModItemCatalog.CopperEnergyCell ? 10_000 : 0;
        state.MachineType = ModItemCatalog.GetLocalKey(item.ItemId);
        this.repository.Save();
        return item;
    }

    private Item PickUpMachineToPlayer(GameLocation location, Vector2 tile)
    {
        if (!location.Objects.TryGetValue(tile, out var obj))
            throw new InvalidOperationException($"No machine at {FormatTile(tile)} to pick up.");

        location.Objects.Remove(tile);
        var rejected = Game1.player.addItemToInventory(obj);
        if (rejected is not null)
            throw new InvalidOperationException($"Could not add picked machine to inventory: {rejected.QualifiedItemId}");

        return obj;
    }

    private void PlaceMachineFromPlayer(GameLocation location, Vector2 tile, Guid guid, string qualifiedItemId)
    {
        for (var i = 0; i < Game1.player.Items.Count; i++)
        {
            var item = Game1.player.Items[i];
            if (item is null
                || item.QualifiedItemId != qualifiedItemId
                || item.modData.GetValueOrDefault(MachineRegistryService.MachineGuidKey) != guid.ToString("N"))
            {
                continue;
            }

            Game1.player.Items[i] = null;
            this.PlaceSpecificMachineItem(location, tile, item);
            return;
        }

        throw new InvalidOperationException($"Could not find held machine {qualifiedItemId} guid={guid:N}.");
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

    private MachineState GetState(Guid guid)
    {
        if (!this.repository.Data.Machines.TryGetValue(guid, out var state))
            throw new InvalidOperationException($"Missing machine state {guid:N}.");

        return state;
    }

    private bool TryProbeEndpoint(GameLocation location, Vector2 tile, out string probe)
    {
        var api = this.getSvsapApi();
        if (api is null)
        {
            probe = "api-null";
            return false;
        }

        if (!api.TryGetLinkedEndpoint(location, tile, out var endpoint, out var code, out var message) || endpoint is null)
        {
            probe = $"{code}:{message}";
            return false;
        }

        probe = $"network={endpoint.NetworkId:N} endpoint={endpoint.EndpointId:N} active={endpoint.Active} type={endpoint.EndpointType}";
        return endpoint.Active;
    }

    private GameLocation GetFarm()
    {
        return Game1.getLocationFromName("Farm") ?? Game1.currentLocation ?? throw new InvalidOperationException("Farm location is not available.");
    }

    private static void StampEndpoint(Item item, Guid networkId)
    {
        item.modData[SvsapNetworkIdKey] = networkId.ToString("N");
        item.modData[SvsapEndpointIdKey] = Guid.NewGuid().ToString("N");
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
        for (var y = 8; y < 60; y++)
        {
            for (var x = 8; x < 90; x++)
            {
                var origin = new Vector2(x, y);
                if (BlockIsClear(location, origin, width, height))
                    return origin;
            }
        }

        throw new InvalidOperationException("Could not find clear farm block for SVSAPME E2E fixture.");
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
        this.monitor.Log($"SVSAPME_P0P1_E2E {id} {(pass ? "PASS" : "FAIL")} {evidence}", pass ? LogLevel.Info : LogLevel.Error);
    }

    private void WriteResults(string fileName)
    {
        this.WritePayload(fileName, new
        {
            version = this.versionLabel,
            role = this.role,
            pass = this.results.All(result => result.Pass),
            results = this.results
        });
    }

    private void WritePayload(string fileName, object payload)
    {
        if (string.IsNullOrWhiteSpace(this.outputDir))
            return;

        File.WriteAllText(Path.Combine(this.outputDir, fileName), JsonSerializer.Serialize(payload, JsonOptions));
    }

    private T ReadPayload<T>(string fileName)
    {
        return JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(this.outputDir, fileName)), JsonOptions)
            ?? throw new InvalidOperationException($"Could not read payload {fileName}.");
    }

    private bool Exists(string fileName)
    {
        return !string.IsNullOrWhiteSpace(this.outputDir) && File.Exists(Path.Combine(this.outputDir, fileName));
    }

    private static string FormatTile(Vector2 tile)
    {
        return $"{tile.X:0},{tile.Y:0}";
    }

    private static Vector2 ParseTile(string raw)
    {
        var parts = raw.Split(',', 2);
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var x)
            || !int.TryParse(parts[1], out var y))
        {
            throw new FormatException($"Invalid tile payload: {raw}");
        }

        return new Vector2(x, y);
    }

    private sealed record E2EResult(string Id, bool Pass, string Evidence);

    private sealed record SingleFixture(
        GameLocation Location,
        Guid NetworkId,
        Chest TargetChest,
        Chest SourceChest,
        SObject CellObject,
        Vector2 CellTile,
        Guid CellGuid,
        Vector2 ImporterTile,
        Guid ImporterGuid);

    private sealed record MultiFixture(
        Vector2 CellTile,
        Vector2 FarmTile,
        Guid CellGuid,
        Guid FarmGuid,
        long CellStoredWh);

    private sealed record MultiFixturePayload(
        string CellTile,
        string FarmTile,
        Guid CellGuid,
        Guid FarmGuid,
        long CellStoredWh)
    {
        public static MultiFixturePayload FromFixture(MultiFixture fixture)
        {
            return new MultiFixturePayload(
                FormatTile(fixture.CellTile),
                FormatTile(fixture.FarmTile),
                fixture.CellGuid,
                fixture.FarmGuid,
                fixture.CellStoredWh);
        }

        public MultiFixture ToFixture()
        {
            return new MultiFixture(
                ParseTile(this.CellTile),
                ParseTile(this.FarmTile),
                this.CellGuid,
                this.FarmGuid,
                this.CellStoredWh);
        }
    }
}
