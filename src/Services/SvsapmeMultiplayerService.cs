using Koizumi.SVSAP.Api;
using Koizumi.SVSAPME.Api;
using Microsoft.Xna.Framework;
using SVSAPME.Models;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using SObject = StardewValley.Object;

namespace SVSAPME.Services;

internal sealed class SvsapmeMultiplayerService
{
    private readonly IModHelper helper;
    private readonly IManifest manifest;
    private readonly MachineStateRepository repository;
    private readonly MachineRegistryService registry;
    private readonly EnergyNetworkManager energy;
    private readonly MachineRuntimeService machineRuntime;
    private readonly SingleBlockFarmService singleBlockFarm;
    private readonly Func<ISvsapApi?> getSvsapApi;
    private readonly IMonitor monitor;
    private readonly HashSet<long> warnedMissingPeers = new();
    private readonly HashSet<Guid> reconciledTransactions = new();
    private readonly MultiplayerTransactionCache<SvsapmeMachineActionResponse> actionResponses = new();
    private readonly MultiplayerEscrowStore<Item> pendingActionEscrows = new();

    public SvsapmeMultiplayerService(
        IModHelper helper,
        IManifest manifest,
        MachineStateRepository repository,
        MachineRegistryService registry,
        EnergyNetworkManager energy,
        MachineRuntimeService machineRuntime,
        SingleBlockFarmService singleBlockFarm,
        Func<ISvsapApi?> getSvsapApi,
        IMonitor monitor)
    {
        this.helper = helper;
        this.manifest = manifest;
        this.repository = repository;
        this.registry = registry;
        this.energy = energy;
        this.machineRuntime = machineRuntime;
        this.singleBlockFarm = singleBlockFarm;
        this.getSvsapApi = getSvsapApi;
        this.monitor = monitor;
    }

    public bool ClientGameplayEnabled { get; private set; } = true;

    public int CachedActionResponseCount => this.actionResponses.Count;

    public int PendingActionEscrowCount => this.pendingActionEscrows.Count;

    public bool TrySendMachineItemMovementReport(SvsapmeMachineItemMovementReport report)
    {
        if (Context.IsMainPlayer)
            return false;

        if (!this.ClientGameplayEnabled
            || (report.RemovedMachineGuids.Count == 0 && report.ObservedMachineGuids.Count == 0))
        {
            return false;
        }

        var host = this.helper.Multiplayer.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        if (host is null || !host.HasSmapi || !PeerHasThisMod(host))
            return false;

        try
        {
            this.helper.Multiplayer.SendMessage(
                report,
                SvsapmeMultiplayerMessageTypes.MachineItemMovementReport,
                modIDs: new[] { this.manifest.UniqueID },
                playerIDs: new[] { host.PlayerID });
            return true;
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to send SVSAPME machine item movement report to host: {ex.Message}", LogLevel.Trace);
            return false;
        }
    }

    public bool TrySendMachineActionRequest(SvsapmeMachineActionRequest request)
    {
        if (Context.IsMainPlayer)
            return false;

        if (!this.ClientGameplayEnabled)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.multiplayer.hostMissingSvsapme", "SVSAPME gameplay is disabled because the host is missing SVSAPME."), HUDMessage.error_type));
            return false;
        }

        var host = this.helper.Multiplayer.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        if (host is null || !host.HasSmapi || !PeerHasThisMod(host))
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.multiplayer.hostMustInstall", "Host must install SVSAPME for multiplayer machine actions."), HUDMessage.error_type));
            return false;
        }

        if (!this.TryCaptureEscrowedHeldItem(request, out var captureMessage))
        {
            Game1.addHUDMessage(new HUDMessage(captureMessage, HUDMessage.error_type));
            return false;
        }

        try
        {
            this.helper.Multiplayer.SendMessage(
                request,
                SvsapmeMultiplayerMessageTypes.MachineActionRequest,
                modIDs: new[] { this.manifest.UniqueID },
                playerIDs: new[] { host.PlayerID });
        }
        catch (Exception ex)
        {
            this.pendingActionEscrows.Resolve(request.TransactionId, restore: true, RestoreEscrowedItem);
            this.monitor.Log($"Failed to send SVSAPME machine action request to host: {ex.Message}", LogLevel.Warn);
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.multiplayer.actionSendFailed", "SVSAPME action could not be sent; escrowed item was restored."), HUDMessage.error_type));
            return false;
        }

        return true;
    }

    public void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.reconciledTransactions.Clear();
        this.actionResponses.Clear();
        this.pendingActionEscrows.ClearWithoutRestore();
        this.ClientGameplayEnabled = true;
    }

    public void OnPeerContextReceived(object? sender, PeerContextReceivedEventArgs e)
    {
        if (Context.IsMainPlayer)
        {
            if (!e.Peer.IsHost && !PeerHasThisMod(e.Peer))
                this.WarnMissingRequiredMod(e.Peer, "SVSAPME requires every player to install this mod. A connected player is missing it, so SVSAPME custom machines are disabled for that peer.");

            return;
        }

        if (e.Peer.IsHost && !PeerHasThisMod(e.Peer))
        {
            this.ClientGameplayEnabled = false;
            this.WarnMissingRequiredMod(e.Peer, "SVSAPME is installed locally, but the host is missing it. SVSAPME gameplay is disabled for this multiplayer save.");
        }
    }

    public void OnPeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
    {
        this.warnedMissingPeers.Remove(e.Peer.PlayerID);
        if (!Context.IsMainPlayer && e.Peer.IsHost)
        {
            this.ClientGameplayEnabled = false;
            this.RestorePendingActionEscrows();
            this.reconciledTransactions.Clear();
            this.actionResponses.Clear();
            return;
        }

        if (Context.IsMainPlayer)
            this.actionResponses.Clear();
    }

    public void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        if (e.FromModID != this.manifest.UniqueID)
            return;

        try
        {
            switch (e.Type)
            {
                case SvsapmeMultiplayerMessageTypes.MachineSnapshotRequest:
                    if (Context.IsMainPlayer)
                        this.HandleMachineSnapshotRequest(e);
                    break;

                case SvsapmeMultiplayerMessageTypes.MachineActionRequest:
                    if (Context.IsMainPlayer)
                        this.HandleMachineActionRequest(e);
                    break;

                case SvsapmeMultiplayerMessageTypes.MachineActionResponse:
                    if (!Context.IsMainPlayer)
                        this.HandleMachineActionResponse(e.ReadAs<SvsapmeMachineActionResponse>());
                    break;

                case SvsapmeMultiplayerMessageTypes.MachineItemMovementReport:
                    if (Context.IsMainPlayer)
                        this.HandleMachineItemMovementReport(e.ReadAs<SvsapmeMachineItemMovementReport>());
                    break;

                case SvsapmeMultiplayerMessageTypes.EnergyDebugRequest:
                    if (Context.IsMainPlayer)
                        this.HandleEnergyDebugRequest(e);
                    break;
            }
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to handle SVSAPME multiplayer message '{e.Type}' from {e.FromPlayerID}: {ex.Message}", LogLevel.Warn);
        }
    }

    private void HandleMachineItemMovementReport(SvsapmeMachineItemMovementReport report)
    {
        var changed = false;
        foreach (var machineGuid in report.RemovedMachineGuids.Where(machineGuid => machineGuid != Guid.Empty).Distinct())
            changed |= this.registry.MarkPotentiallyConsumedMachineGuid(machineGuid);

        foreach (var machineGuid in report.ObservedMachineGuids.Where(machineGuid => machineGuid != Guid.Empty).Distinct())
            changed |= this.registry.ObserveMachineGuid(machineGuid);

        if (!changed)
            return;

        this.repository.Save();
    }

    private void HandleMachineSnapshotRequest(ModMessageReceivedEventArgs e)
    {
        var request = e.ReadAs<SvsapmeMachineSnapshotRequest>();
        var response = this.CreateMachineSnapshotResponse(request.MachineGuid);
        this.helper.Multiplayer.SendMessage(
            response,
            SvsapmeMultiplayerMessageTypes.MachineSnapshotResponse,
            modIDs: new[] { this.manifest.UniqueID },
            playerIDs: new[] { e.FromPlayerID });
    }

    private void HandleMachineActionRequest(ModMessageReceivedEventArgs e)
    {
        var request = e.ReadAs<SvsapmeMachineActionRequest>();
        if (this.actionResponses.TryGet(e.FromPlayerID, request.TransactionId, out var cached))
        {
            this.SendMachineActionResponse(cached, e.FromPlayerID);
            return;
        }

        var response = this.ExecuteMachineActionRequest(request);
        this.actionResponses.Remember(e.FromPlayerID, request.TransactionId, response);
        this.SendMachineActionResponse(response, e.FromPlayerID);
    }

    private void SendMachineActionResponse(SvsapmeMachineActionResponse response, long playerId)
    {
        this.helper.Multiplayer.SendMessage(
            response,
            SvsapmeMultiplayerMessageTypes.MachineActionResponse,
            modIDs: new[] { this.manifest.UniqueID },
            playerIDs: new[] { playerId });
    }

    private void HandleMachineActionResponse(SvsapmeMachineActionResponse response)
    {
        if (!this.reconciledTransactions.Add(response.TransactionId))
            return;

        this.pendingActionEscrows.Resolve(
            response.TransactionId,
            SvsapmeActionEscrowRules.ShouldRestoreOnResponse(response.Success, response.ConsumeEscrowedItem),
            RestoreEscrowedItem);

        Game1.addHUDMessage(new HUDMessage(response.Message, response.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));
    }

    internal bool TryCaptureEscrowedHeldItem(SvsapmeMachineActionRequest request, out string message)
    {
        message = string.Empty;
        if (Context.IsMainPlayer
            || request.TransactionId == Guid.Empty
            || !SvsapmeActionEscrowRules.ActionMayEscrowHeldItem(request.ActionKind))
        {
            return true;
        }

        var player = Game1.player;
        var held = player?.CurrentItem;
        if (player is null || held is null)
            return true;

        if (player.Items.IndexOf(held) < 0 || held.Stack <= 0)
        {
            message = "Held item changed; please retry.";
            return false;
        }

        var escrowed = held.getOne();
        escrowed.Stack = 1;
        held.Stack -= 1;
        if (held.Stack <= 0)
            player.removeItemFromInventory(held);

        this.pendingActionEscrows.Track(request.TransactionId, escrowed);
        return true;
    }

    private SvsapmeMachineActionResponse ExecuteMachineActionRequest(SvsapmeMachineActionRequest request)
    {
        if (!this.TryResolveHostActionContext(request, out var state, out var location, out var tile, out var placedObject, out var failure))
            return CreateFailureActionResponse(request, failure);

        var result = request.ActionKind switch
        {
            SvsapmeMachineActionKind.ConfigurePoweredFilter => this.machineRuntime.TryConfigurePoweredFilter(
                placedObject,
                location,
                tile,
                request.QualifiedItemId,
                Math.Clamp(request.Count, 1, 999)),
            SvsapmeMachineActionKind.LoadFarmSeed => this.singleBlockFarm.TryLoadSeed(
                placedObject,
                location,
                tile,
                request.QualifiedItemId,
                placedByFarmingLevel: Math.Clamp(request.FarmingLevel, 0, 10)),
            SvsapmeMachineActionKind.LoadFarmFertilizer => this.singleBlockFarm.TryLoadFertilizer(
                placedObject,
                location,
                tile,
                request.QualifiedItemId),
            SvsapmeMachineActionKind.InstallFarmModule => this.singleBlockFarm.TryInstallModule(
                placedObject,
                location,
                tile,
                request.QualifiedItemId),
            SvsapmeMachineActionKind.FuelCarbonGenerator => this.machineRuntime.TryFuelCarbonGenerator(
                placedObject,
                location,
                tile,
                request.QualifiedItemId),
            _ => new SvsapmeMachineActionApplyResult(false, false, "Unsupported SVSAPME machine action.")
        };

        return new SvsapmeMachineActionResponse
        {
            TransactionId = request.TransactionId,
            MachineGuid = request.MachineGuid,
            Success = result.Success,
            ConsumeEscrowedItem = result.ConsumeEscrowedItem,
            Message = result.Message
        };
    }

    private bool TryResolveHostActionContext(
        SvsapmeMachineActionRequest request,
        out MachineState state,
        out GameLocation location,
        out Vector2 tile,
        out SObject placedObject,
        out string message)
    {
        state = null!;
        location = null!;
        tile = Vector2.Zero;
        placedObject = null!;

        if (request.TransactionId == Guid.Empty)
            return Fail("SVSAPME action request is missing a TransactionId.", out message);

        if (!this.repository.TryGet(request.MachineGuid, out state!))
            return Fail("MachineGuid is unknown on the host.", out message);

        location = Game1.getLocationFromName(state.LocationName);
        if (location is null)
            return Fail("Machine location is not loaded on the host.", out message);

        tile = new Vector2(state.TileX, state.TileY);
        if (!location.Objects.TryGetValue(tile, out placedObject!))
            return Fail("Machine is missing from its recorded tile.", out message);

        if (placedObject.QualifiedItemId != state.QualifiedItemId)
            return Fail("Machine tile now contains a different object.", out message);

        if (!Guid.TryParse(placedObject.modData.GetValueOrDefault(MachineRegistryService.MachineGuidKey), out var placedGuid)
            || placedGuid != request.MachineGuid)
        {
            return Fail("MachineGuid does not match the object at its recorded tile.", out message);
        }

        var api = this.getSvsapApi();
        if (api is null)
            return Fail("SVSAP API is unavailable on the host.", out message);

        if (!api.TryGetLinkedEndpoint(location, tile, out var endpoint, out var code, out var apiMessage))
            return Fail($"SVSAP endpoint validation failed: {code} {apiMessage}", out message);

        if (endpoint is null || !endpoint.Active)
            return Fail("Machine is not on an active SVSAP network.", out message);

        message = string.Empty;
        return true;
    }

    private static bool Fail(string failureMessage, out string message)
    {
        message = failureMessage;
        return false;
    }

    private void HandleEnergyDebugRequest(ModMessageReceivedEventArgs e)
    {
        var request = e.ReadAs<SvsapmeEnergyDebugRequest>();
        var response = new SvsapmeEnergyDebugResponse
        {
            NetworkId = request.NetworkId
        };
        if (this.energy.TryGetNetworkEnergy(request.NetworkId, out var storedWh, out var capacityWh, out var code))
        {
            response.Success = true;
            response.StoredWh = storedWh;
            response.CapacityWh = capacityWh;
            response.Code = SvsapmeEnergyErrorCode.None.ToString();
            response.Message = $"{storedWh}/{capacityWh} Wh";
        }
        else
        {
            response.Success = false;
            response.Code = code.ToString();
            response.Message = $"Energy debug failed: {code}";
        }

        this.helper.Multiplayer.SendMessage(
            response,
            SvsapmeMultiplayerMessageTypes.EnergyDebugResponse,
            modIDs: new[] { this.manifest.UniqueID },
            playerIDs: new[] { e.FromPlayerID });
    }

    private SvsapmeMachineSnapshotResponse CreateMachineSnapshotResponse(Guid machineGuid)
    {
        if (!this.repository.TryGet(machineGuid, out var state))
        {
            return new SvsapmeMachineSnapshotResponse
            {
                MachineGuid = machineGuid,
                Success = false,
                Message = "MachineGuid is unknown on the host."
            };
        }

        return new SvsapmeMachineSnapshotResponse
        {
            MachineGuid = machineGuid,
            Success = true,
            Message = "Machine snapshot resolved.",
            QualifiedItemId = state.QualifiedItemId,
            LocationName = state.LocationName,
            TileX = state.TileX,
            TileY = state.TileY,
            StoredWh = state.StoredWh,
            CapacityWh = state.CapacityWh,
            ProgressWh = state.ProgressWh,
            OutputBufferStacks = state.OutputBuffer.Count
        };
    }

    private static SvsapmeMachineActionResponse CreateFailureActionResponse(SvsapmeMachineActionRequest request, string message)
    {
        return new SvsapmeMachineActionResponse
        {
            TransactionId = request.TransactionId,
            MachineGuid = request.MachineGuid,
            Success = false,
            ConsumeEscrowedItem = false,
            Message = message
        };
    }

    private void RestorePendingActionEscrows()
    {
        var restored = this.pendingActionEscrows.RestoreAll(RestoreEscrowedItem);
        if (restored > 0)
            this.monitor.Log($"Restored {restored:N0} pending SVSAPME action escrow item(s).", LogLevel.Warn);
    }

    private static void RestoreEscrowedItem(Item item)
    {
        if (Game1.player is not null && Game1.player.addItemToInventoryBool(item))
            return;

        if (Context.IsWorldReady && Game1.player is not null)
            Game1.createItemDebris(item, Game1.player.getStandingPosition(), -1, Game1.currentLocation);
    }

    private static bool PeerHasThisMod(IMultiplayerPeer peer)
    {
        return peer.HasSmapi && peer.GetMod("Koizumi.SVSAPME") is not null;
    }

    private void WarnMissingRequiredMod(IMultiplayerPeer peer, string message)
    {
        if (!this.warnedMissingPeers.Add(peer.PlayerID))
            return;

        var detail = peer.HasSmapi
            ? $"peer {peer.PlayerID} does not have {this.manifest.UniqueID} installed"
            : $"peer {peer.PlayerID} is not running SMAPI";
        this.monitor.Log($"{message} ({detail})", LogLevel.Warn);
        if (Context.IsWorldReady)
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.multiplayer.allPlayersNeedMod", "SVSAPME: all players must install this mod for multiplayer SVSAPME gameplay."), HUDMessage.error_type));
    }
}
