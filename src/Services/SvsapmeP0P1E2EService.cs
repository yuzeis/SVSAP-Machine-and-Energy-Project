using System.Text.Json;
using Koizumi.SVSAP.Api;
using Koizumi.SVSAPME.Api;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Network;
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
    private const string FarmNameEnv = "STARDEW_SVSAPME_P0P1_E2E_FARM";
    private const string JoinAddressEnv = "STARDEW_SVSAPME_P0P1_E2E_JOIN";
    private const string DefaultVersionLabel = "ver1.3.0-alpha.1";
    private const int StartupTimeoutTicks = 12000;
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
    private readonly SvsapmeMultiplayerService multiplayer;
    private readonly Func<ISvsapApi?> getSvsapApi;
    private readonly Func<ModConfig> getConfig;
    private readonly string role;
    private readonly string outputDir;
    private readonly string versionLabel;
    private readonly string farmName;
    private readonly string joinAddress;
    private readonly List<E2EResult> results = new();
    private object? svsapNetworkRepository;

    private bool started;
    private bool stopped;
    private int startupStage;
    private int startupTicks;
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
    private SvsapmeEnergyDebugResponse? energyDebugResponse;
    private bool hostOfflineRecorded;

    public SvsapmeP0P1E2EService(
        IModHelper helper,
        IMonitor monitor,
        MachineStateRepository repository,
        MachineRegistryService registry,
        EnergyNetworkManager energy,
        MachineRuntimeService runtime,
        SvsapmeMultiplayerService multiplayer,
        Func<ISvsapApi?> getSvsapApi,
        Func<ModConfig> getConfig)
    {
        this.helper = helper;
        this.monitor = monitor;
        this.repository = repository;
        this.registry = registry;
        this.energy = energy;
        this.runtime = runtime;
        this.multiplayer = multiplayer;
        this.getSvsapApi = getSvsapApi;
        this.getConfig = getConfig;
        this.role = (Environment.GetEnvironmentVariable(RoleEnv) ?? string.Empty).Trim().ToLowerInvariant();
        this.outputDir = Environment.GetEnvironmentVariable(OutputDirEnv) ?? string.Empty;
        this.versionLabel = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VersionEnv))
            ? DefaultVersionLabel
            : Environment.GetEnvironmentVariable(VersionEnv)!;
        this.farmName = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FarmNameEnv))
            ? "SVSAPMEP0P1"
            : Environment.GetEnvironmentVariable(FarmNameEnv)!;
        this.joinAddress = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(JoinAddressEnv))
            ? "127.0.0.1"
            : Environment.GetEnvironmentVariable(JoinAddressEnv)!;
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
        this.helper.Events.Multiplayer.ModMessageReceived += this.OnP0P1ModMessageReceived;
        this.helper.Events.Multiplayer.PeerDisconnected += this.OnP0P1PeerDisconnected;
        this.monitor.Log($"SVSAPME_P0P1_E2E started role={this.role} version={this.versionLabel} output=\"{this.outputDir}\"", LogLevel.Info);
    }

    private void Stop()
    {
        if (this.stopped)
            return;

        this.stopped = true;
        this.helper.Events.GameLoop.UpdateTicked -= this.OnUpdateTicked;
        this.helper.Events.Multiplayer.ModMessageReceived -= this.OnP0P1ModMessageReceived;
        this.helper.Events.Multiplayer.PeerDisconnected -= this.OnP0P1PeerDisconnected;
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (this.stopped)
            return;

        try
        {
            if (!Context.IsWorldReady)
            {
                this.TickStartup();
                return;
            }

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

    private void OnP0P1ModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        if (Context.IsMainPlayer || e.FromModID != ModItemCatalog.UniqueId)
            return;

        if (e.Type == SvsapmeMultiplayerMessageTypes.EnergyDebugResponse)
            this.energyDebugResponse = e.ReadAs<SvsapmeEnergyDebugResponse>();
    }

    private void OnP0P1PeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
    {
        if (this.role != "client" || !e.Peer.IsHost || this.hostOfflineRecorded)
            return;

        this.hostOfflineRecorded = true;
        var observedGuid = this.multi?.CellGuid ?? Guid.Empty;
        var reportSent = this.multiplayer.TrySendMachineItemMovementReport(new SvsapmeMachineItemMovementReport
        {
            ObservedMachineGuids = { observedGuid }
        });
        this.WritePayload("client-host-offline.json", new
        {
            hostConnected = false,
            clientGameplayEnabled = this.multiplayer.ClientGameplayEnabled,
            reportSent,
            observedGuid = observedGuid.ToString("N"),
            hostCompleteSeen = this.Exists("host-complete.json")
        });

        if (this.Exists("host-complete.json"))
        {
            this.WritePayload("client-complete.json", new { ok = true, version = this.versionLabel });
            this.Stop();
        }
    }

    private void TickStartup()
    {
        this.startupTicks++;
        if (this.role == "client"
            && !this.hostOfflineRecorded
            && this.Exists("host-complete.json")
            && this.Exists("client-energy-debug.json"))
        {
            this.hostOfflineRecorded = true;
            var observedGuid = this.multi?.CellGuid ?? Guid.Empty;
            var reportSent = this.multiplayer.TrySendMachineItemMovementReport(new SvsapmeMachineItemMovementReport
            {
                ObservedMachineGuids = { observedGuid }
            });
            this.WritePayload("client-host-offline.json", new
            {
                hostConnected = false,
                clientGameplayEnabled = this.multiplayer.ClientGameplayEnabled,
                reportSent,
                observedGuid = observedGuid.ToString("N"),
                hostCompleteSeen = true,
                worldReady = Context.IsWorldReady,
                mainPlayer = Context.IsMainPlayer
            });
            this.WritePayload("client-complete.json", new { ok = true, version = this.versionLabel });
            this.Stop();
            return;
        }

        if (this.startupTicks > StartupTimeoutTicks)
        {
            this.WriteNotReadyResults();
            this.Stop();
            return;
        }

        if (this.role is "single" or "host")
        {
            this.TickHostFarmStartup(multiplayerServer: this.role == "host");
            return;
        }

        if (this.role == "client")
            this.TickClientJoinStartup();
    }

    private void TickHostFarmStartup(bool multiplayerServer)
    {
        if (this.startupStage == 0)
        {
            if (Game1.activeClickableMenu is not TitleMenu || TitleMenu.subMenu is not null)
                return;

            this.StartHostFarmCreation(multiplayerServer);
            this.startupStage = 1;
            return;
        }

        if (this.startupStage == 1 && TitleMenu.subMenu is CharacterCustomization menu)
        {
            this.CompleteHostFarmCreation(menu, multiplayerServer);
            this.startupStage = 2;
        }
    }

    private void TickClientJoinStartup()
    {
        var hostReady = string.IsNullOrWhiteSpace(this.outputDir) || File.Exists(Path.Combine(this.outputDir, "host-ready.json"));
        if (!hostReady)
            return;

        if (this.startupStage == 0)
        {
            if (Game1.activeClickableMenu is not TitleMenu)
                return;

            var multiplayerCore = this.helper.Reflection.GetField<Multiplayer>(typeof(Game1), "multiplayer").GetValue();
            var client = multiplayerCore.InitClient(new LidgrenClient(this.joinAddress));
            TitleMenu.subMenu = new FarmhandMenu(client);
            this.WritePayload("client-join-menu.json", new { ok = true, joinAddress = this.joinAddress, version = this.versionLabel });
            this.monitor.Log($"SVSAPME_P0P1_E2E client-join-menu address=\"{this.joinAddress}\"", LogLevel.Info);
            this.startupStage = 1;
            return;
        }

        if (this.startupStage == 1)
        {
            var menu = this.GetFarmhandMenu();
            var slot = menu?.MenuSlots.OfType<FarmhandMenu.FarmhandSlot>().FirstOrDefault(slot => !slot.BelongsToAnotherPlayer());
            if (slot is null)
                return;

            slot.Activate();
            this.WritePayload("client-slot-activated.json", new { ok = true, version = this.versionLabel });
            this.monitor.Log("SVSAPME_P0P1_E2E client-slot-activated", LogLevel.Info);
            this.startupStage = 2;
        }
    }

    private FarmhandMenu? GetFarmhandMenu()
    {
        if (Game1.activeClickableMenu is FarmhandMenu active)
            return active;

        if (Game1.activeClickableMenu is TitleMenu && TitleMenu.subMenu is FarmhandMenu sub)
            return sub;

        return null;
    }

    private void StartHostFarmCreation(bool multiplayerServer)
    {
        Game1.resetPlayer();
        this.ApplyHostFarmIdentity();
        Game1.startingCabins = multiplayerServer ? 1 : 0;
        Game1.cabinsSeparate = false;
        Game1.whichFarm = 0;
        Game1.options.enableServer = multiplayerServer;
        Game1.player.team.useSeparateWallets.Value = false;
        TitleMenu.subMenu = new CharacterCustomization(CharacterCustomization.Source.HostNewFarm, multiplayerServer: multiplayerServer);
        this.WritePayload($"{this.role}-farm-create-started.json", new { ok = true, farmName = this.farmName, multiplayerServer, version = this.versionLabel });
        this.monitor.Log($"SVSAPME_P0P1_E2E farm-create-started role={this.role} farm=\"{this.farmName}\" multiplayer={multiplayerServer}", LogLevel.Info);
    }

    private void CompleteHostFarmCreation(CharacterCustomization menu, bool multiplayerServer)
    {
        this.ApplyHostFarmIdentity();
        Game1.startingCabins = multiplayerServer ? 1 : 0;
        Game1.cabinsSeparate = false;
        Game1.whichFarm = 0;
        Game1.options.enableServer = multiplayerServer;
        Game1.player.team.useSeparateWallets.Value = false;

        this.helper.Reflection.GetField<TextBox>(menu, "nameBox").GetValue().Text = multiplayerServer ? "SVSAPMEHost" : "SVSAPMESolo";
        this.helper.Reflection.GetField<TextBox>(menu, "farmnameBox").GetValue().Text = this.farmName;
        this.helper.Reflection.GetField<TextBox>(menu, "favThingBox").GetValue().Text = "SVSAPME";
        this.helper.Reflection.GetField<bool>(menu, "skipIntro").SetValue(true);

        var ok = menu.okButton.bounds.Center;
        menu.receiveLeftClick(ok.X, ok.Y);
        this.WritePayload($"{this.role}-farm-create-submitted.json", new { ok = true, farmName = this.farmName, multiplayerServer, version = this.versionLabel });
        this.monitor.Log($"SVSAPME_P0P1_E2E farm-create-submitted role={this.role} farm=\"{this.farmName}\"", LogLevel.Info);
    }

    private void ApplyHostFarmIdentity()
    {
        Game1.player.Name = this.role == "host" ? "SVSAPMEHost" : "SVSAPMESolo";
        Game1.player.displayName = Game1.player.Name;
        Game1.player.farmName.Value = this.farmName;
        Game1.player.favoriteThing.Value = "SVSAPME";
        Game1.player.isCustomized.Value = true;
    }

    private void WriteNotReadyResults()
    {
        this.WritePayload($"{this.role}-not-ready.json", new
        {
            version = this.versionLabel,
            role = this.role,
            pass = false,
            e2eReady = false,
            worldReady = Context.IsWorldReady,
            mainPlayer = Context.IsMainPlayer,
            startupStage = this.startupStage,
            startupTicks = this.startupTicks
        });
        this.monitor.Log($"SVSAPME_P0P1_E2E_NOT_READY role={this.role} worldReady={Context.IsWorldReady} mainPlayer={Context.IsMainPlayer} stage={this.startupStage} ticks={this.startupTicks}", LogLevel.Warn);
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
            var noPending = !this.repository.Data.PendingReclaims.SelectMany(reclaim => reclaim.MachineGuids).Contains(this.multi.CellGuid);
            this.Record(
                "M1",
                cellPlaced && cellState.StoredWh == this.multi.CellStoredWh && noPending,
                $"farmhand replay cellPlaced={cellPlaced} storedWh={cellState.StoredWh} expected={this.multi.CellStoredWh} noPending={noPending}");
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
            this.WritePayload("host-m2-verified.json", new { ok = this.results.Last().Pass });
            this.stage = 30;
            this.stageTicks = 0;
            return;
        }

        if (this.stage == 30)
        {
            if (!this.Exists("client-consumed.json") || this.stageTicks < 30)
                return;

            this.registry.ReconcileMissingMachinesOnDayStarted();
            this.registry.ReconcileMissingMachinesOnDayStarted();
            var retired = !this.repository.Data.Machines.ContainsKey(this.multi!.FarmGuid);
            var noNaturalPending = !this.repository.Data.PendingReclaims.SelectMany(reclaim => reclaim.MachineGuids).Contains(this.multi.FarmGuid);
            this.repository.Data.PendingReclaims.Add(new PendingReclaimCrate
            {
                ReclaimId = Guid.NewGuid(),
                Reason = "e2e-consumed-force",
                OriginalLocationName = "Farm",
                TileX = (int)this.multi.FarmTile.X,
                TileY = (int)this.multi.FarmTile.Y,
                MachineGuids = { this.multi.FarmGuid }
            });
            var forceClaimed = this.registry.TryClaimPendingReclaims(Game1.player, includeUnconfirmed: true, out var machines, out _, out var message);
            this.Record(
                "M3",
                retired && noNaturalPending && !forceClaimed && machines == 0,
                $"farmhand-consumed retired={retired} noNaturalPending={noNaturalPending} forceClaimed={forceClaimed} machines={machines} message=\"{message}\"");
            var depositOk = this.energy.TryDepositWh(this.multi.NetworkId, 1_000, ModItemCatalog.UniqueId, "e2e-host-authority", out var accepted, out var code, out var energyMessage);
            var readOk = this.energy.TryGetNetworkEnergy(this.multi.NetworkId, out var storedWh, out var capacityWh, out var readCode);
            this.WritePayload("host-energy-ready.json", new HostEnergyReadyPayload(this.multi.NetworkId, depositOk, accepted, readOk, storedWh, capacityWh, code.ToString(), readCode.ToString(), energyMessage));
            this.stage = 40;
            this.stageTicks = 0;
            return;
        }

        if (this.stage == 40)
        {
            if (!this.Exists("client-energy-denied.json") || !this.Exists("client-energy-debug.json") || this.stageTicks < 10)
                return;

            var ready = this.ReadPayload<HostEnergyReadyPayload>("host-energy-ready.json");
            var denied = this.ReadPayload<ClientEnergyDeniedPayload>("client-energy-denied.json");
            var debug = this.ReadPayload<ClientEnergyDebugPayload>("client-energy-debug.json");
            var readOk = this.energy.TryGetNetworkEnergy(this.multi!.NetworkId, out var storedWh, out var capacityWh, out var code);
            this.Record(
                "M4",
                ready.DepositOk && ready.AcceptedWh == 1_000 && !denied.DepositOk && denied.AcceptedWh == 0 && denied.Code == SvsapmeEnergyErrorCode.NotHost.ToString() && denied.RequestSent,
                $"hostDeposit={ready.DepositOk}/{ready.AcceptedWh} clientDeposit={denied.DepositOk}/{denied.AcceptedWh} clientCode={denied.Code} requestSent={denied.RequestSent}");
            this.Record(
                "M5",
                readOk && debug.Success && debug.NetworkId == this.multi.NetworkId && debug.StoredWh == storedWh && debug.CapacityWh == capacityWh,
                $"hostRead={readOk}/{storedWh}/{capacityWh}/{code} clientDebug={debug.Success}/{debug.StoredWh}/{debug.CapacityWh}/{debug.Code}");
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
            if (!this.Exists("host-m2-verified.json"))
                return;

            var hadBefore = this.PlayerHasMachineGuid(this.multi!.FarmGuid);
            this.RemoveFromPlayerInventory(this.multi.FarmGuid);
            var hasAfter = this.PlayerHasMachineGuid(this.multi.FarmGuid);
            var reportSent = this.multiplayer.TrySendMachineItemMovementReport(new SvsapmeMachineItemMovementReport
            {
                RemovedMachineGuids = { this.multi.FarmGuid }
            });
            this.WritePayload("client-consumed.json", new
            {
                machineGuid = this.multi.FarmGuid.ToString("N"),
                inventoryHadBefore = hadBefore,
                inventoryHasAfter = hasAfter,
                reportSent
            });
            this.stage = 40;
            this.stageTicks = 0;
            return;
        }

        if (this.stage == 40)
        {
            if (!this.Exists("host-energy-ready.json"))
                return;

            var ready = this.ReadPayload<HostEnergyReadyPayload>("host-energy-ready.json");
            var depositOk = this.energy.TryDepositWh(ready.NetworkId, 250, ModItemCatalog.UniqueId, "e2e-client-denied", out var accepted, out var code, out var message);
            var host = this.helper.Multiplayer.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
            var requestSent = false;
            if (host is not null)
            {
                this.helper.Multiplayer.SendMessage(
                    new SvsapmeEnergyDebugRequest { NetworkId = ready.NetworkId },
                    SvsapmeMultiplayerMessageTypes.EnergyDebugRequest,
                    modIDs: new[] { ModItemCatalog.UniqueId },
                    playerIDs: new[] { host.PlayerID });
                requestSent = true;
            }

            this.WritePayload("client-energy-denied.json", new ClientEnergyDeniedPayload(depositOk, accepted, code.ToString(), message, requestSent));
            this.stage = 50;
            this.stageTicks = 0;
            return;
        }

        if (this.stage == 50)
        {
            if (this.energyDebugResponse is null)
                return;

            this.WritePayload("client-energy-debug.json", new ClientEnergyDebugPayload(
                this.energyDebugResponse.NetworkId,
                this.energyDebugResponse.Success,
                this.energyDebugResponse.Code,
                this.energyDebugResponse.Message,
                this.energyDebugResponse.StoredWh,
                this.energyDebugResponse.CapacityWh));
            this.stage = 60;
            this.stageTicks = 0;
            return;
        }

        if (this.stage == 60)
        {
            if (!this.Exists("host-complete.json"))
                return;

            var hostConnected = this.helper.Multiplayer.GetConnectedPlayers().Any(peer => peer.IsHost);
            if (hostConnected)
                return;

            var offlineReportSent = this.multiplayer.TrySendMachineItemMovementReport(new SvsapmeMachineItemMovementReport
            {
                ObservedMachineGuids = { this.multi!.CellGuid }
            });
            this.WritePayload("client-host-offline.json", new
            {
                hostConnected,
                clientGameplayEnabled = this.multiplayer.ClientGameplayEnabled,
                reportSent = offlineReportSent,
                observedGuid = this.multi.CellGuid.ToString("N")
            });
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
        var networkId = Guid.NewGuid();
        var coreTile = origin;
        var cellTile = origin + new Vector2(1, 0);
        var farmTile = origin + new Vector2(2, 0);
        this.PlaceLinkedMachine(location, coreTile, "(BC)" + ModItemCatalog.SvsapUniqueId + ".NetworkCore", networkId);
        var cell = this.PlaceLinkedMachine(location, cellTile, "(BC)" + ModItemCatalog.CopperEnergyCell, networkId);
        var farm = this.PlaceLinkedMachine(location, farmTile, "(BC)" + ModItemCatalog.CopperFarm, networkId);
        var cellGuid = this.RegisterMachine(cell, location, cellTile, storedWh: 3_210);
        var farmGuid = this.RegisterMachine(farm, location, farmTile, storedWh: 0);
        cell.modData[MachineRegistryService.StoredWhKey] = "3210";
        return new MultiFixture(networkId, cellTile, farmTile, cellGuid, farmGuid, 3_210);
    }

    private SObject PlaceLinkedMachine(GameLocation location, Vector2 tile, string qualifiedItemId, Guid networkId)
    {
        if (ItemRegistry.Create(qualifiedItemId, 1) is not SObject obj)
            throw new InvalidOperationException($"Could not create placeable object {qualifiedItemId}.");

        this.RegisterSvsapEndpoint(obj, location, tile, networkId, GetSvsapEndpointType(obj));
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

    private bool PlayerHasMachineGuid(Guid guid)
    {
        return Game1.player.Items.Any(item =>
            item is not null
            && item.modData.GetValueOrDefault(MachineRegistryService.MachineGuidKey) == guid.ToString("N"));
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

    private void RegisterSvsapEndpoint(Item item, GameLocation location, Vector2 tile, Guid networkId, string endpointTypeName)
    {
        var endpointId = Guid.NewGuid();
        item.modData[SvsapNetworkIdKey] = networkId.ToString("N");
        item.modData[SvsapEndpointIdKey] = endpointId.ToString("N");

        var repository = this.GetSvsapNetworkRepository();
        var repositoryType = repository.GetType();
        var network = repositoryType.GetMethod("GetOrCreateNetwork")?.Invoke(repository, new object?[] { networkId })
            ?? throw new InvalidOperationException("Could not create SVSAP P0/P1 test network.");
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
        Guid NetworkId,
        Vector2 CellTile,
        Vector2 FarmTile,
        Guid CellGuid,
        Guid FarmGuid,
        long CellStoredWh);

    private sealed record MultiFixturePayload(
        Guid NetworkId,
        string CellTile,
        string FarmTile,
        Guid CellGuid,
        Guid FarmGuid,
        long CellStoredWh)
    {
        public static MultiFixturePayload FromFixture(MultiFixture fixture)
        {
            return new MultiFixturePayload(
                fixture.NetworkId,
                FormatTile(fixture.CellTile),
                FormatTile(fixture.FarmTile),
                fixture.CellGuid,
                fixture.FarmGuid,
                fixture.CellStoredWh);
        }

        public MultiFixture ToFixture()
        {
            return new MultiFixture(
                this.NetworkId,
                ParseTile(this.CellTile),
                ParseTile(this.FarmTile),
                this.CellGuid,
                this.FarmGuid,
                this.CellStoredWh);
        }
    }

    private sealed record HostEnergyReadyPayload(
        Guid NetworkId,
        bool DepositOk,
        long AcceptedWh,
        bool ReadOk,
        long StoredWh,
        long CapacityWh,
        string DepositCode,
        string ReadCode,
        string Message);

    private sealed record ClientEnergyDeniedPayload(
        bool DepositOk,
        long AcceptedWh,
        string Code,
        string Message,
        bool RequestSent);

    private sealed record ClientEnergyDebugPayload(
        Guid NetworkId,
        bool Success,
        string Code,
        string Message,
        long StoredWh,
        long CapacityWh);
}
