using Koizumi.SVSAP.Api;
using Koizumi.SVSAPME.Api;
using Microsoft.Xna.Framework;
using SVSAPME.Content;
using SVSAPME.Models;
using SVSAPME.UI;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using System.Text.Json;
using SObject = StardewValley.Object;

namespace SVSAPME.Services;

internal sealed class SvsapmeMultiplayerService
{
    private const int ReconciledTransactionLimit = 256;
    private const int PendingDeliveryRetentionDays = 7;
    private const int ClientActionResponseTimeoutTicks = 300;
    private const int ClientActionRetryLimit = 3;
    private const string ReconciledTransactionModDataKey = ModItemCatalog.UniqueId + "/ReconciledDeliveries";
    private const string DurableRemoteDeliveryModDataKey = ModItemCatalog.UniqueId + "/DurableRemoteDeliveries";
    private const string PersistentActionEscrowModDataKey = ModItemCatalog.UniqueId + "/PendingActionEscrows";

    private readonly IModHelper helper;
    private readonly IManifest manifest;
    private readonly MachineStateRepository repository;
    private readonly MachineRegistryService registry;
    private readonly EnergyNetworkManager energy;
    private readonly EnergyTelemetryService telemetry;
    private readonly MachineRuntimeService machineRuntime;
    private readonly SingleBlockFarmService singleBlockFarm;
    private readonly SingleBlockProcessorService singleBlockProcessor;
    private readonly Func<ISvsapApi?> getSvsapApi;
    private readonly IMonitor monitor;
    private readonly HashSet<long> warnedMissingPeers = new();
    private readonly HashSet<Guid> reconciledTransactions = new();
    private readonly Queue<Guid> reconciledTransactionOrder = new();
    private readonly MultiplayerTransactionCache<SvsapmeMachineActionResponse> actionResponses = new();
    private readonly MultiplayerEscrowStore<Item> pendingActionEscrows = new();
    private readonly Dictionary<Guid, PendingClientAction> pendingClientActionsByMachine = new();
    private readonly Dictionary<long, string> blockedPeerActionMessages = new();
    private string clientGameplayDisabledMessage = string.Empty;

    public SvsapmeMultiplayerService(
        IModHelper helper,
        IManifest manifest,
        MachineStateRepository repository,
        MachineRegistryService registry,
        EnergyNetworkManager energy,
        EnergyTelemetryService telemetry,
        MachineRuntimeService machineRuntime,
        SingleBlockFarmService singleBlockFarm,
        SingleBlockProcessorService singleBlockProcessor,
        Func<ISvsapApi?> getSvsapApi,
        IMonitor monitor)
    {
        this.helper = helper;
        this.manifest = manifest;
        this.repository = repository;
        this.registry = registry;
        this.energy = energy;
        this.telemetry = telemetry;
        this.machineRuntime = machineRuntime;
        this.singleBlockFarm = singleBlockFarm;
        this.singleBlockProcessor = singleBlockProcessor;
        this.getSvsapApi = getSvsapApi;
        this.monitor = monitor;
    }

    public bool ClientGameplayEnabled { get; private set; } = true;

    public int CachedActionResponseCount => this.actionResponses.Count;

    public int PendingActionEscrowCount => this.pendingActionEscrows.Count;

    public int PendingClientMachineActionCount => this.pendingClientActionsByMachine.Count;

    internal bool IsMachineActionPending(Guid machineGuid)
    {
        return machineGuid != Guid.Empty && this.pendingClientActionsByMachine.ContainsKey(machineGuid);
    }

    public void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (!Context.IsMainPlayer || !Context.IsWorldReady)
            return;

        if (this.PruneConfirmedExpiredPendingDeliveries(Game1.Date.TotalDays) > 0)
            this.repository.Save();
    }

    public bool TrySendMachineSnapshotRequest(Guid machineGuid)
    {
        return this.TrySendMachineSnapshotRequest(machineGuid, offset: 0, limit: 64, notify: true);
    }

    internal bool TrySendMachineSnapshotRequest(Guid machineGuid, int offset, int limit, bool notify)
    {
        if (Context.IsMainPlayer || machineGuid == Guid.Empty)
            return false;

        var host = this.helper.Multiplayer.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        if (host is null || !host.HasSmapi || !PeerHasThisMod(host))
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.multiplayer.hostMustInstall", "Host must install SVSAPME for multiplayer machine actions."), HUDMessage.error_type));
            return false;
        }

        try
        {
            this.helper.Multiplayer.SendMessage(
                new SvsapmeMachineSnapshotRequest
                {
                    MachineGuid = machineGuid,
                    Offset = Math.Max(0, offset),
                    Limit = Math.Clamp(limit, 1, 64)
                },
                SvsapmeMultiplayerMessageTypes.MachineSnapshotRequest,
                modIDs: new[] { this.manifest.UniqueID },
                playerIDs: new[] { host.PlayerID });
            if (notify)
                Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.multiplayer.snapshotRequested", "Requested SVSAPME machine status from host."), HUDMessage.newQuest_type));
            return true;
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to send SVSAPME machine snapshot request to host: {ex.Message}", LogLevel.Warn);
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.multiplayer.actionSendFailed", "SVSAPME action could not be sent; please retry."), HUDMessage.error_type));
            return false;
        }
    }

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
            var message = string.IsNullOrWhiteSpace(this.clientGameplayDisabledMessage)
                ? ModText.Get("hud.multiplayer.hostMissingSvsapme", "SVSAPME gameplay is disabled because the host is missing SVSAPME.")
                : this.clientGameplayDisabledMessage;
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
            return false;
        }

        var host = this.helper.Multiplayer.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        if (host is null || !host.HasSmapi || !PeerHasThisMod(host))
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.multiplayer.hostMustInstall", "Host must install SVSAPME for multiplayer machine actions."), HUDMessage.error_type));
            return false;
        }

        if (!this.TryReservePendingClientAction(request, out var pendingMessage))
        {
            Game1.addHUDMessage(new HUDMessage(pendingMessage, HUDMessage.error_type));
            return false;
        }

        if (!this.TryCaptureEscrowedHeldItem(request, out var captureMessage))
        {
            this.ClearPendingClientAction(request);
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
            this.MarkPendingClientActionSent(request);
        }
        catch (Exception ex)
        {
            this.ClearPendingClientAction(request);
            this.ResolvePendingActionEscrow(request.TransactionId, restore: true);
            this.monitor.Log($"Failed to send SVSAPME machine action request to host: {ex.Message}", LogLevel.Warn);
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.multiplayer.actionSendFailed", "SVSAPME action could not be sent; please retry."), HUDMessage.error_type));
            return false;
        }

        return true;
    }

    public void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.reconciledTransactions.Clear();
        this.reconciledTransactionOrder.Clear();
        this.actionResponses.Clear();
        this.pendingActionEscrows.ClearWithoutRestore();
        this.pendingClientActionsByMachine.Clear();
        if (!Context.IsMainPlayer)
            this.RehydrateDurableActionEscrows();
        this.RestoreDurableRemoteDeliveries();
        this.blockedPeerActionMessages.Clear();
        this.ClientGameplayEnabled = true;
        this.clientGameplayDisabledMessage = string.Empty;
    }

    public void OnPeerContextReceived(object? sender, PeerContextReceivedEventArgs e)
    {
        if (Context.IsMainPlayer)
        {
            this.blockedPeerActionMessages.Remove(e.Peer.PlayerID);
            if (e.Peer.IsHost)
                return;

            var peerMod = e.Peer.HasSmapi ? e.Peer.GetMod(this.manifest.UniqueID) : null;
            if (peerMod is null)
            {
                this.WarnMissingRequiredMod(e.Peer, "SVSAPME requires every player to install this mod. A connected player is missing it, so SVSAPME custom machines are disabled for that peer.");
                return;
            }

            var localVersion = this.manifest.Version.ToString();
            var peerVersion = peerMod.Version.ToString();
            if (!string.Equals(localVersion, peerVersion, StringComparison.OrdinalIgnoreCase))
            {
                var message = this.CreateVersionMismatchMessage(hostVersion: localVersion, peerVersion: peerVersion);
                this.blockedPeerActionMessages[e.Peer.PlayerID] = message;
                this.WarnPeerVersionMismatch(e.Peer, message, localVersion, peerVersion);
                return;
            }

            this.ResendPendingDeliveries(e.Peer.PlayerID);
            return;
        }

        if (!e.Peer.IsHost)
            return;

        var hostMod = e.Peer.HasSmapi ? e.Peer.GetMod(this.manifest.UniqueID) : null;
        if (hostMod is null)
        {
            this.DisableClientGameplay(ModText.Get("hud.multiplayer.hostMissingSvsapme", "SVSAPME gameplay is disabled because the host is missing SVSAPME."));
            this.WarnMissingRequiredMod(e.Peer, this.clientGameplayDisabledMessage);
            return;
        }

        var hostVersion = hostMod.Version.ToString();
        var thisVersion = this.manifest.Version.ToString();
        if (!string.Equals(hostVersion, thisVersion, StringComparison.OrdinalIgnoreCase))
        {
            var message = this.CreateVersionMismatchMessage(hostVersion, thisVersion);
            this.DisableClientGameplay(message);
            this.WarnPeerVersionMismatch(e.Peer, message, hostVersion, thisVersion);
            return;
        }

        this.ClientGameplayEnabled = true;
        this.clientGameplayDisabledMessage = string.Empty;
        this.RetryPendingClientActions(force: true);
    }

    public void OnPeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
    {
        this.warnedMissingPeers.Remove(e.Peer.PlayerID);
        this.blockedPeerActionMessages.Remove(e.Peer.PlayerID);
        if (!Context.IsMainPlayer && e.Peer.IsHost)
        {
            this.ClientGameplayEnabled = false;
            this.clientGameplayDisabledMessage = ModText.Get("hud.multiplayer.hostMissingSvsapme", "SVSAPME gameplay is disabled because the host is missing SVSAPME.");
            this.reconciledTransactions.Clear();
            this.reconciledTransactionOrder.Clear();
            this.actionResponses.Clear();
            return;
        }
    }

    public void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (Context.IsMainPlayer || !Context.IsWorldReady || !this.ClientGameplayEnabled || !e.IsMultipleOf(30))
            return;

        this.RetryPendingClientActions(force: false);
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

                case SvsapmeMultiplayerMessageTypes.MachineDeliveryAck:
                    if (Context.IsMainPlayer)
                        this.HandleMachineDeliveryAck(e.ReadAs<SvsapmeMachineDeliveryAck>(), e.FromPlayerID);
                    break;

                case SvsapmeMultiplayerMessageTypes.MachineSnapshotResponse:
                    if (!Context.IsMainPlayer)
                        this.HandleMachineSnapshotResponse(e.ReadAs<SvsapmeMachineSnapshotResponse>());
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
        var response = this.CreateMachineSnapshotResponse(request.MachineGuid, request.Offset, request.Limit);
        this.helper.Multiplayer.SendMessage(
            response,
            SvsapmeMultiplayerMessageTypes.MachineSnapshotResponse,
            modIDs: new[] { this.manifest.UniqueID },
            playerIDs: new[] { e.FromPlayerID });
    }

    private void HandleMachineSnapshotResponse(SvsapmeMachineSnapshotResponse response)
    {
        if (!response.Success)
        {
            Game1.addHUDMessage(new HUDMessage(
                string.IsNullOrWhiteSpace(response.Message)
                    ? ModText.Get("hud.multiplayer.snapshotFailed", "SVSAPME machine status failed; please retry.")
                    : response.Message,
                HUDMessage.error_type));
            return;
        }

        if (Game1.activeClickableMenu is IRemoteMachineMenu remoteMenu
            && remoteMenu.MachineGuid == response.MachineGuid)
        {
            remoteMenu.ApplySnapshot(response);
            return;
        }

        Game1.activeClickableMenu = new RemoteMachineControlMenu(
            response,
            this.TrySendMachineActionRequest,
            (machineGuid, offset, limit) => this.TrySendMachineSnapshotRequest(machineGuid, offset, limit, notify: false),
            this.IsMachineActionPending);
    }

    private void HandleMachineActionRequest(ModMessageReceivedEventArgs e)
    {
        SvsapmeMachineActionRequest? request = null;
        try
        {
            request = e.ReadAs<SvsapmeMachineActionRequest>();
            if (this.blockedPeerActionMessages.TryGetValue(e.FromPlayerID, out var blockMessage))
            {
                var blocked = CreateFailureActionResponse(request, blockMessage);
                this.actionResponses.Remember(e.FromPlayerID, request.TransactionId, blocked);
                this.SendMachineActionResponse(blocked, e.FromPlayerID);
                return;
            }

            if (this.actionResponses.TryGet(e.FromPlayerID, request.TransactionId, out var cached))
            {
                this.SendMachineActionResponse(cached, e.FromPlayerID);
                return;
            }

            if (this.TryGetPendingDeliveryResponse(e.FromPlayerID, request.TransactionId, out var pendingDeliveryResponse))
            {
                this.actionResponses.Remember(e.FromPlayerID, request.TransactionId, pendingDeliveryResponse);
                this.SendMachineActionResponse(pendingDeliveryResponse, e.FromPlayerID);
                return;
            }

            var response = this.ExecuteMachineActionRequest(request, e.FromPlayerID);
            if (response.Success && response.ReturnedItems.Count > 0)
                this.RememberPendingDelivery(e.FromPlayerID, request, response);

            this.actionResponses.Remember(e.FromPlayerID, request.TransactionId, response);
            this.SendMachineActionResponse(response, e.FromPlayerID);
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to execute SVSAPME machine action from {e.FromPlayerID}: {ex.Message}", LogLevel.Warn);
            if (request is null || request.TransactionId == Guid.Empty)
                return;

            var failure = CreateFailureActionResponse(
                request,
                ModText.Get("hud.multiplayer.actionFailed", "SVSAPME action failed; please retry."));
            this.actionResponses.Remember(e.FromPlayerID, request.TransactionId, failure);
            try
            {
                this.SendMachineActionResponse(failure, e.FromPlayerID);
            }
            catch (Exception sendEx)
            {
                this.monitor.Log($"Failed to send SVSAPME machine action failure response to {e.FromPlayerID}: {sendEx.Message}", LogLevel.Warn);
            }
        }
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
        if (!this.MarkTransactionReconciled(response.TransactionId, persist: response.Success && response.ReturnedItems.Count > 0))
        {
            if (response.Success && response.ReturnedItems.Count > 0)
                this.TrySendMachineDeliveryAck(response);
            this.ResolvePendingActionEscrow(
                response.TransactionId,
                SvsapmeActionEscrowRules.ShouldRestoreOnResponse(response.Success, response.ConsumeEscrowedItem));
            this.ClearPendingClientAction(response);
            return;
        }

        if (response.Success && response.ReturnedItems.Count > 0)
        {
            DeliverReturnedItems(response.ReturnedItems);
            this.TrySendMachineDeliveryAck(response);
        }

        this.ResolvePendingActionEscrow(
            response.TransactionId,
            SvsapmeActionEscrowRules.ShouldRestoreOnResponse(response.Success, response.ConsumeEscrowedItem));
        this.ClearPendingClientAction(response);

        if (response.Snapshot is not null
            && Game1.activeClickableMenu is IRemoteMachineMenu remoteMenu
            && remoteMenu.MachineGuid == response.MachineGuid)
        {
            remoteMenu.ApplySnapshot(response.Snapshot);
        }

        Game1.addHUDMessage(new HUDMessage(
            string.IsNullOrWhiteSpace(response.Message)
                ? LocalizeMachineActionResponse(response.Success)
                : response.Message,
            response.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));
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
        {
            message = ModText.Get("hud.multiplayer.heldChanged", "Held item changed; please retry.");
            return false;
        }

        var escrowCount = SvsapmeActionEscrowRules.GetPrimaryEscrowCount(request.ActionKind, request.Count);
        if (player.Items.IndexOf(held) < 0
            || held.Stack < escrowCount
            || !string.Equals(held.QualifiedItemId, request.QualifiedItemId, StringComparison.Ordinal))
        {
            message = ModText.Get("hud.multiplayer.heldChanged", "Held item changed; please retry.");
            return false;
        }

        var escrowedItem = held.getOne();
        escrowedItem.Stack = escrowCount;
        var slot = player.Items.IndexOf(held);
        held.Stack -= escrowCount;
        if (held.Stack <= 0)
            player.Items[slot] = null;

        if (this.pendingActionEscrows.Track(request.TransactionId, escrowedItem))
        {
            if (this.TrackDurableActionEscrow(request, escrowedItem))
                return true;

            this.ResolvePendingActionEscrow(request.TransactionId, restore: true);
            message = ModText.Get("hud.multiplayer.actionSendFailed", "SVSAPME action could not be sent; please retry.");
            return false;
        }

        RestoreEscrowedItem(escrowedItem);
        message = ModText.Get("hud.multiplayer.actionSendFailed", "SVSAPME action could not be sent; please retry.");
        return false;
    }

    private SvsapmeMachineActionResponse ExecuteMachineActionRequest(SvsapmeMachineActionRequest request, long fromPlayerId)
    {
        if (!this.TryResolveHostActionContext(request, out var state, out var location, out var tile, out var placedObject, out var failure))
            return CreateFailureActionResponse(request, failure);

        var player = Game1.GetPlayer(fromPlayerId, onlyOnline: true);
        if (player is null)
            return CreateFailureActionResponse(request, ModText.Get("hud.multiplayer.requestPlayerOffline", "The requesting player is no longer online."));

        var result = request.ActionKind switch
        {
            SvsapmeMachineActionKind.ConfigurePoweredFilter => this.machineRuntime.TryConfigurePoweredFilter(
                placedObject,
                location,
                tile,
                request.QualifiedItemId,
                Math.Clamp(request.Count, 1, 999)),
            SvsapmeMachineActionKind.SetPoweredFilterSlot => WrapPoweredAction(
                this.machineRuntime.TrySetPoweredFilterSlot(placedObject, location, tile, request.SlotIndex, request.QualifiedItemId, out var setFilterMessage),
                setFilterMessage),
            SvsapmeMachineActionKind.ClearPoweredFilterSlot => WrapPoweredAction(
                this.machineRuntime.TryClearPoweredFilterSlot(placedObject, location, tile, request.SlotIndex, out var clearFilterSlotMessage),
                clearFilterSlotMessage),
            SvsapmeMachineActionKind.TogglePoweredFilterMode => WrapPoweredAction(
                this.machineRuntime.TryTogglePoweredFilterMode(placedObject, location, tile, out var toggleFilterMessage),
                toggleFilterMessage),
            SvsapmeMachineActionKind.TogglePoweredOreDictionaryMode => WrapPoweredAction(
                this.machineRuntime.TryTogglePoweredOreDictionaryMode(placedObject, location, tile, out var toggleOreMessage),
                toggleOreMessage),
            SvsapmeMachineActionKind.CyclePoweredQualityStrategy => WrapPoweredAction(
                this.machineRuntime.TryTogglePoweredQualityStrategy(placedObject, location, tile, out var qualityMessage),
                qualityMessage),
            SvsapmeMachineActionKind.ClearPoweredFilters => WrapPoweredAction(
                this.machineRuntime.TryClearPoweredFilter(placedObject, location, tile, out var clearFiltersMessage),
                clearFiltersMessage),
            SvsapmeMachineActionKind.SetPoweredFacingDirection => WrapPoweredAction(
                this.machineRuntime.TrySetPoweredFacingDirection(placedObject, location, tile, request.Direction, out var directionMessage),
                directionMessage),
            SvsapmeMachineActionKind.InstallPoweredUpgrade => this.machineRuntime.TryInstallPoweredUpgrade(
                placedObject,
                location,
                tile,
                request.SlotIndex,
                request.QualifiedItemId),
            SvsapmeMachineActionKind.RemovePoweredUpgrade => this.machineRuntime.TryRemovePoweredUpgrade(
                placedObject,
                location,
                tile,
                request.SlotIndex),
            SvsapmeMachineActionKind.LoadFarmSeed => this.singleBlockFarm.TryLoadSeed(
                placedObject,
                location,
                tile,
                request.QualifiedItemId,
                placedByFarmingLevel: Math.Clamp(request.FarmingLevel, 0, 10),
                count: Math.Clamp(request.Count, 1, 999)),
            SvsapmeMachineActionKind.LoadFarmFertilizer => this.singleBlockFarm.TryLoadFertilizer(
                placedObject,
                location,
                tile,
                request.QualifiedItemId,
                Math.Clamp(request.Count, 1, 999)),
            SvsapmeMachineActionKind.ExtractFarmSeed => this.singleBlockFarm.TryExtractFarmInput(placedObject, location, tile, fertilizer: false),
            SvsapmeMachineActionKind.ExtractFarmFertilizer => this.singleBlockFarm.TryExtractFarmInput(placedObject, location, tile, fertilizer: true),
            SvsapmeMachineActionKind.InstallFarmModule => this.singleBlockFarm.TryInstallModule(
                placedObject,
                location,
                tile,
                request.QualifiedItemId),
            SvsapmeMachineActionKind.RemoveFarmModule => this.singleBlockFarm.TryRemoveModule(placedObject, location, tile, request.SlotIndex),
            SvsapmeMachineActionKind.ToggleFarmAutoPull => this.singleBlockFarm.ToggleFarmAutoPull(placedObject, location, tile),
            SvsapmeMachineActionKind.ToggleFarmAutoPush => this.singleBlockFarm.ToggleFarmAutoPush(placedObject, location, tile),
            SvsapmeMachineActionKind.ToggleFarmInputMode => this.singleBlockFarm.ToggleFarmInputMode(placedObject, location, tile),
            SvsapmeMachineActionKind.ToggleFarmFilterMode => this.singleBlockFarm.ToggleFarmFilterMode(placedObject, location, tile),
            SvsapmeMachineActionKind.AddFarmFilter => this.singleBlockFarm.AddHeldFarmFilter(
                placedObject,
                location,
                tile,
                TryCreateRequestItem(request)),
            SvsapmeMachineActionKind.ClearFarmFilter => this.singleBlockFarm.ClearFarmFilter(placedObject, location, tile),
            SvsapmeMachineActionKind.PlantFarmPlot => this.singleBlockFarm.TryManualPlant(
                placedObject,
                location,
                tile,
                request.SlotIndex,
                CreateRequestItem(request),
                Math.Clamp(request.FarmingLevel, 0, 10)),
            SvsapmeMachineActionKind.HarvestFarmPlot => this.singleBlockFarm.TryHarvestPlot(placedObject, location, tile, request.SlotIndex),
            SvsapmeMachineActionKind.ToggleFarmPlotLock => this.singleBlockFarm.ToggleFarmPlotLock(
                placedObject,
                location,
                tile,
                request.SlotIndex,
                request.QualifiedItemId),
            SvsapmeMachineActionKind.CollectFarmOutput => this.singleBlockFarm.TryCollectFarmOutput(placedObject, location, tile),
            SvsapmeMachineActionKind.FuelCarbonGenerator => this.machineRuntime.TryFuelCarbonGenerator(
                placedObject,
                location,
                tile,
                request.QualifiedItemId),
            SvsapmeMachineActionKind.StartElectricFurnace => this.machineRuntime.TryStartElectricFurnaceManualUse(
                placedObject,
                location,
                tile,
                request.QualifiedItemId,
                request.Count),
            SvsapmeMachineActionKind.StartElectricGeodeCrusher => this.machineRuntime.TryStartElectricGeodeCrusherManualUse(
                placedObject,
                location,
                tile,
                request.QualifiedItemId),
            SvsapmeMachineActionKind.LoadProcessorInput => this.singleBlockProcessor.TryLoadInput(
                placedObject,
                location,
                tile,
                CreateRequestStack(request)),
            SvsapmeMachineActionKind.ExtractProcessorInput => this.singleBlockProcessor.TryExtractProcessorInput(placedObject, location, tile),
            SvsapmeMachineActionKind.ToggleProcessorAutoPull => this.singleBlockProcessor.ToggleProcessorAutoPull(placedObject, location, tile),
            SvsapmeMachineActionKind.ToggleProcessorAutoPush => this.singleBlockProcessor.ToggleProcessorAutoPush(placedObject, location, tile),
            SvsapmeMachineActionKind.ToggleProcessorInputMode => this.singleBlockProcessor.ToggleProcessorInputMode(placedObject, location, tile),
            SvsapmeMachineActionKind.ToggleProcessorFilterMode => this.singleBlockProcessor.ToggleProcessorFilterMode(placedObject, location, tile),
            SvsapmeMachineActionKind.AddProcessorFilter => this.singleBlockProcessor.AddHeldProcessorFilter(
                placedObject,
                location,
                tile,
                TryCreateRequestItem(request)),
            SvsapmeMachineActionKind.ClearProcessorFilter => this.singleBlockProcessor.ClearProcessorFilter(placedObject, location, tile),
            SvsapmeMachineActionKind.CollectProcessorOutput => this.singleBlockProcessor.TryCollectOutputForRemotePlayer(
                placedObject,
                location,
                tile,
                request.SlotIndex >= 0 ? request.SlotIndex + 1 : Math.Max(0, request.Count)),
            _ => new SvsapmeMachineActionApplyResult(false, false, ModText.Get("hud.multiplayer.unsupportedAction", "Unsupported SVSAPME machine action."))
        };

        var response = new SvsapmeMachineActionResponse
        {
            TransactionId = request.TransactionId,
            MachineGuid = request.MachineGuid,
            Success = result.Success,
            ConsumeEscrowedItem = result.Success && result.ConsumeEscrowedItem,
            Message = result.Message,
            ReturnedItems = result.ReturnedItems
        };

        if (result.Success)
            response.Snapshot = this.CreateMachineSnapshotResponse(request.MachineGuid, request.Offset, request.Limit);

        return response;
    }

    private static SvsapmeMachineActionApplyResult WrapPoweredAction(bool success, string message)
    {
        return new SvsapmeMachineActionApplyResult(success, false, message);
    }

    private static BufferedItemStack CreateRequestStack(SvsapmeMachineActionRequest request)
    {
        return new BufferedItemStack
        {
            QualifiedItemId = request.QualifiedItemId,
            Stack = Math.Clamp(request.Count, 1, 999),
            Quality = request.Quality,
            PreservedParentSheetIndex = request.PreservedParentSheetIndex,
            PreserveType = request.PreserveType,
            Price = request.Price,
            Edibility = request.Edibility,
            Category = request.Category,
            Type = request.Type,
            Name = request.Name,
            DisplayName = request.DisplayName,
            Color = request.Color,
            ModData = request.ModData ?? new()
        };
    }

    private static Item CreateRequestItem(SvsapmeMachineActionRequest request)
    {
        return BufferedItemCodec.CreateItem(CreateRequestStack(request));
    }

    private static Item? TryCreateRequestItem(SvsapmeMachineActionRequest request)
    {
        try
        {
            return string.IsNullOrWhiteSpace(request.QualifiedItemId) ? null : CreateRequestItem(request);
        }
        catch
        {
            return null;
        }
    }

    private bool TryReservePendingClientAction(SvsapmeMachineActionRequest request, out string message)
    {
        message = string.Empty;
        if (request.MachineGuid == Guid.Empty || request.TransactionId == Guid.Empty)
            return true;

        if (this.pendingClientActionsByMachine.TryGetValue(request.MachineGuid, out var pending)
            && pending.TransactionId != request.TransactionId)
        {
            message = ModText.Get("hud.multiplayer.actionPending", "SVSAPME is still waiting for the previous action on this machine.");
            return false;
        }

        this.pendingClientActionsByMachine[request.MachineGuid] = new PendingClientAction(request);
        return true;
    }

    private void MarkPendingClientActionSent(SvsapmeMachineActionRequest request)
    {
        if (request.MachineGuid == Guid.Empty)
            return;

        if (this.pendingClientActionsByMachine.TryGetValue(request.MachineGuid, out var pending)
            && pending.TransactionId == request.TransactionId)
        {
            pending.LastSentTick = Game1.ticks;
        }
    }

    private void RetryPendingClientActions(bool force)
    {
        if (Context.IsMainPlayer || this.pendingClientActionsByMachine.Count == 0)
            return;

        var host = this.helper.Multiplayer.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        var hostMod = host?.HasSmapi == true ? host.GetMod(this.manifest.UniqueID) : null;
        if (host is null
            || hostMod is null
            || !string.Equals(hostMod.Version.ToString(), this.manifest.Version.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var tick = Game1.ticks;
        foreach (var pending in this.pendingClientActionsByMachine.Values.ToList())
        {
            if (force)
            {
                pending.RetryCount = 0;
                pending.RetryLimitNotified = false;
                pending.LastSentTick = tick - ClientActionResponseTimeoutTicks;
            }

            var elapsed = tick >= pending.LastSentTick
                ? tick - pending.LastSentTick
                : ClientActionResponseTimeoutTicks;
            if (elapsed < ClientActionResponseTimeoutTicks)
                continue;

            if (pending.RetryCount >= ClientActionRetryLimit)
            {
                if (!pending.RetryLimitNotified)
                {
                    pending.RetryLimitNotified = true;
                    Game1.addHUDMessage(new HUDMessage(
                        ModText.Get("hud.multiplayer.actionReconcilePending", "SVSAPME is keeping the item safe while waiting to reconcile this action with the host."),
                        HUDMessage.error_type));
                }
                continue;
            }

            try
            {
                this.helper.Multiplayer.SendMessage(
                    pending.Request,
                    SvsapmeMultiplayerMessageTypes.MachineActionRequest,
                    modIDs: new[] { this.manifest.UniqueID },
                    playerIDs: new[] { host.PlayerID });
                pending.LastSentTick = tick;
                pending.RetryCount++;
            }
            catch (Exception ex)
            {
                pending.LastSentTick = tick;
                pending.RetryCount++;
                this.monitor.Log($"Failed to retry SVSAPME action {pending.TransactionId:N}: {ex.Message}", LogLevel.Trace);
            }
        }
    }

    private void ClearPendingClientAction(SvsapmeMachineActionRequest request)
    {
        if (request.MachineGuid == Guid.Empty)
            return;

        if (this.pendingClientActionsByMachine.TryGetValue(request.MachineGuid, out var pending)
            && pending.TransactionId == request.TransactionId)
        {
            this.pendingClientActionsByMachine.Remove(request.MachineGuid);
        }
    }

    private void ClearPendingClientAction(SvsapmeMachineActionResponse response)
    {
        if (response.MachineGuid == Guid.Empty)
            return;

        if (this.pendingClientActionsByMachine.TryGetValue(response.MachineGuid, out var pending)
            && pending.TransactionId == response.TransactionId)
        {
            this.pendingClientActionsByMachine.Remove(response.MachineGuid);
        }
    }

    private static void RestoreEscrowedItemToPlayer(Farmer player, Item item)
    {
        if (player.addItemToInventoryBool(item))
            return;

        var location = player.currentLocation ?? Game1.currentLocation;
        if (Context.IsWorldReady && location is not null)
            Game1.createItemDebris(item, player.getStandingPosition(), -1, location);
    }

    private static void DeliverReturnedItems(IEnumerable<BufferedItemStack> returnedItems)
    {
        var player = Game1.player;
        if (player is null)
            return;

        foreach (var stack in returnedItems)
        {
            var item = BufferedItemCodec.CreateItem(stack);
            RestoreEscrowedItemToPlayer(player, item);
        }
    }

    private void RememberPendingDelivery(long playerId, SvsapmeMachineActionRequest request, SvsapmeMachineActionResponse response)
    {
        if (request.TransactionId == Guid.Empty || response.ReturnedItems.Count == 0)
            return;

        this.repository.Data.PendingRemoteDeliveries.RemoveAll(delivery =>
            delivery.PlayerId == playerId && delivery.TransactionId == request.TransactionId);
        this.repository.Data.PendingRemoteDeliveries.Add(new PendingRemoteDelivery
        {
            TransactionId = request.TransactionId,
            MachineGuid = request.MachineGuid,
            PlayerId = playerId,
            PlayerName = Game1.GetPlayer(playerId, onlyOnline: false)?.Name ?? string.Empty,
            Message = response.Message,
            ConsumeEscrowedItem = response.ConsumeEscrowedItem,
            CreatedDay = Context.IsWorldReady ? Game1.Date.TotalDays : 0,
            CreatedTick = Game1.ticks,
            ReturnedItems = response.ReturnedItems
        });
        this.repository.Save();
    }

    private bool TryGetPendingDeliveryResponse(long playerId, Guid transactionId, out SvsapmeMachineActionResponse response)
    {
        var delivery = this.repository.Data.PendingRemoteDeliveries.FirstOrDefault(candidate =>
            candidate.PlayerId == playerId && candidate.TransactionId == transactionId);
        if (delivery is null)
        {
            response = null!;
            return false;
        }

        response = CreateDeliveryResponse(delivery);
        return true;
    }

    private void ResendPendingDeliveries(long playerId)
    {
        foreach (var delivery in this.repository.Data.PendingRemoteDeliveries
            .Where(delivery => delivery.PlayerId == playerId && delivery.ReturnedItems.Count > 0)
            .ToList())
        {
            var response = CreateDeliveryResponse(delivery);
            this.actionResponses.Remember(playerId, delivery.TransactionId, response);
            try
            {
                this.SendMachineActionResponse(response, playerId);
            }
            catch (Exception ex)
            {
                this.monitor.Log($"Failed to resend pending SVSAPME delivery {delivery.TransactionId:N} to player {playerId}: {ex.Message}", LogLevel.Trace);
            }
        }
    }

    private void TrySendMachineDeliveryAck(SvsapmeMachineActionResponse response)
    {
        var host = this.helper.Multiplayer.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        if (host is null || !host.HasSmapi || !PeerHasThisMod(host))
            return;

        try
        {
            this.helper.Multiplayer.SendMessage(
                new SvsapmeMachineDeliveryAck
                {
                    TransactionId = response.TransactionId,
                    MachineGuid = response.MachineGuid
                },
                SvsapmeMultiplayerMessageTypes.MachineDeliveryAck,
                modIDs: new[] { this.manifest.UniqueID },
                playerIDs: new[] { host.PlayerID });
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to send SVSAPME delivery ack {response.TransactionId:N}: {ex.Message}", LogLevel.Trace);
        }
    }

    private void HandleMachineDeliveryAck(SvsapmeMachineDeliveryAck ack, long playerId)
    {
        var removed = this.repository.Data.PendingRemoteDeliveries.RemoveAll(delivery =>
            delivery.PlayerId == playerId
            && delivery.TransactionId == ack.TransactionId
            && (ack.MachineGuid == Guid.Empty || delivery.MachineGuid == ack.MachineGuid));
        if (removed <= 0)
            return;

        this.repository.Save();
    }

    private int PruneConfirmedExpiredPendingDeliveries(int currentDay)
    {
        var changed = false;
        var removed = 0;
        var queued = 0;
        foreach (var delivery in this.repository.Data.PendingRemoteDeliveries.ToList())
        {
            if (delivery.CreatedDay <= 0)
            {
                delivery.CreatedDay = currentDay;
                delivery.CreatedTick = Game1.ticks;
                changed = true;
                continue;
            }

            if (!IsExpiredPendingDelivery(delivery, currentDay))
                continue;

            if (!PlayerHasPersistedReconciledTransaction(delivery.PlayerId, delivery.TransactionId)
                && !this.QueueDurableRemoteDeliveryForPlayer(delivery))
            {
                continue;
            }

            if (!PlayerHasPersistedReconciledTransaction(delivery.PlayerId, delivery.TransactionId))
                queued++;

            this.repository.Data.PendingRemoteDeliveries.Remove(delivery);
            removed++;
            changed = true;
        }

        if (removed > 0)
            this.monitor.Log($"Pruned {removed - queued:N0} confirmed and queued {queued:N0} expired SVSAPME pending remote delivery record(s).", LogLevel.Info);

        return changed ? Math.Max(removed, 1) : 0;
    }

    private static bool IsExpiredPendingDelivery(PendingRemoteDelivery delivery, int currentDay)
    {
        return currentDay - delivery.CreatedDay >= PendingDeliveryRetentionDays
            && delivery.ReturnedItems.Count > 0;
    }

    private static SvsapmeMachineActionResponse CreateDeliveryResponse(PendingRemoteDelivery delivery)
    {
        return new SvsapmeMachineActionResponse
        {
            TransactionId = delivery.TransactionId,
            MachineGuid = delivery.MachineGuid,
            Success = true,
            ConsumeEscrowedItem = delivery.ConsumeEscrowedItem,
            Message = delivery.Message,
            ReturnedItems = delivery.ReturnedItems
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
            return Fail(ModText.Get("hud.multiplayer.missingTransactionId", "SVSAPME action request is missing a TransactionId."), out message);

        if (!this.repository.TryGet(request.MachineGuid, out state!))
            return Fail(ModText.Get("hud.multiplayer.unknownMachineGuid", "MachineGuid is unknown on the host."), out message);

        location = Game1.getLocationFromName(state.LocationName);
        if (location is null)
            return Fail(ModText.Get("hud.multiplayer.machineLocationMissing", "Machine location is not loaded on the host."), out message);

        tile = new Vector2(state.TileX, state.TileY);
        if (!location.Objects.TryGetValue(tile, out placedObject!))
            return Fail(ModText.Get("hud.multiplayer.machineTileMissing", "Machine is missing from its recorded tile."), out message);

        if (placedObject.QualifiedItemId != state.QualifiedItemId)
            return Fail(ModText.Get("hud.multiplayer.machineTileChanged", "Machine tile now contains a different object."), out message);

        if (!Guid.TryParse(placedObject.modData.GetValueOrDefault(MachineRegistryService.MachineGuidKey), out var placedGuid)
            || placedGuid != request.MachineGuid)
        {
            return Fail(ModText.Get("hud.multiplayer.machineGuidMismatch", "MachineGuid does not match the object at its recorded tile."), out message);
        }

        if (!RequiresActiveEndpoint(request.ActionKind))
        {
            message = string.Empty;
            return true;
        }

        var api = this.getSvsapApi();
        if (api is null)
            return Fail(ModText.Get("hud.multiplayer.svsapApiUnavailable", "SVSAP API is unavailable on the host."), out message);

        if (!api.TryGetLinkedEndpoint(location, tile, out var endpoint, out var code, out var apiMessage))
            return Fail(ModText.Get("hud.multiplayer.endpointValidationFailed", "SVSAP endpoint validation failed: {{code}} {{message}}", new { code, message = apiMessage }), out message);

        if (endpoint is null || !endpoint.Active)
            return Fail(ModText.Get("hud.multiplayer.endpointInactive", "Machine is not on an active SVSAP network."), out message);

        message = string.Empty;
        return true;
    }

    private static bool RequiresActiveEndpoint(SvsapmeMachineActionKind actionKind)
    {
        return actionKind is SvsapmeMachineActionKind.StartElectricFurnace
            or SvsapmeMachineActionKind.StartElectricGeodeCrusher;
    }

    private static bool Fail(string failureMessage, out string message)
    {
        message = failureMessage;
        return false;
    }

    private bool MarkTransactionReconciled(Guid transactionId, bool persist)
    {
        if (transactionId == Guid.Empty)
            return true;

        if (persist && HasPersistedReconciledTransaction(transactionId))
            return false;

        if (!this.reconciledTransactions.Add(transactionId))
            return false;

        this.reconciledTransactionOrder.Enqueue(transactionId);
        while (this.reconciledTransactionOrder.Count > ReconciledTransactionLimit)
            this.reconciledTransactions.Remove(this.reconciledTransactionOrder.Dequeue());

        if (persist)
            PersistReconciledTransaction(transactionId);

        return true;
    }

    private static bool HasPersistedReconciledTransaction(Guid transactionId)
    {
        return ReadPersistedReconciledTransactions(Game1.player).Contains(transactionId);
    }

    private static bool PlayerHasPersistedReconciledTransaction(long playerId, Guid transactionId)
    {
        return ReadPersistedReconciledTransactions(Game1.GetPlayer(playerId, onlyOnline: false)).Contains(transactionId);
    }

    private static void PersistReconciledTransaction(Guid transactionId)
    {
        var player = Game1.player;
        if (player is null)
            return;

        var transactions = ReadPersistedReconciledTransactions(player);
        if (transactions.Contains(transactionId))
            return;

        transactions.Add(transactionId);
        while (transactions.Count > ReconciledTransactionLimit)
            transactions.RemoveAt(0);

        player.modData[ReconciledTransactionModDataKey] = string.Join("|", transactions.Select(id => id.ToString("N")));
    }

    private static List<Guid> ReadPersistedReconciledTransactions(Farmer? player)
    {
        if (player is null
            || !player.modData.TryGetValue(ReconciledTransactionModDataKey, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return new List<Guid>();
        }

        var transactions = new List<Guid>();
        foreach (var piece in raw.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Guid.TryParse(piece.Trim(), out var parsed))
                transactions.Add(parsed);
        }

        return transactions;
    }

    private bool ResolvePendingActionEscrow(Guid transactionId, bool restore)
    {
        var resolved = this.pendingActionEscrows.Resolve(transactionId, restore, RestoreEscrowedItem);
        if (transactionId != Guid.Empty)
            this.RemoveDurableActionEscrow(transactionId);

        return resolved;
    }

    private bool TrackDurableActionEscrow(SvsapmeMachineActionRequest request, Item item)
    {
        var player = Game1.player;
        if (player is null || request.TransactionId == Guid.Empty || request.MachineGuid == Guid.Empty)
            return false;

        try
        {
            if (!this.TryReadDurableActionEscrows(player, out var existing))
                return false;

            var records = existing
                .Where(record => record.TransactionId != request.TransactionId)
                .ToList();
            records.Add(new PersistentActionEscrowRecord
            {
                TransactionId = request.TransactionId,
                Request = request,
                Item = BufferedItemCodec.FromItem(item)
            });
            this.WriteDurableActionEscrows(player, records);
            return true;
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to persist SVSAPME action escrow {request.TransactionId:N}: {ex.Message}", LogLevel.Warn);
            return false;
        }
    }

    private void RemoveDurableActionEscrow(Guid transactionId)
    {
        var player = Game1.player;
        if (player is null || transactionId == Guid.Empty)
            return;

        try
        {
            if (!this.TryReadDurableActionEscrows(player, out var existing))
                return;

            var records = existing
                .Where(record => record.TransactionId != transactionId)
                .ToList();
            this.WriteDurableActionEscrows(player, records);
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to clear SVSAPME action escrow {transactionId:N}: {ex.Message}", LogLevel.Trace);
        }
    }

    private int RehydrateDurableActionEscrows()
    {
        var player = Game1.player;
        if (player is null)
            return 0;

        if (!this.TryReadDurableActionEscrows(player, out var records))
            return 0;

        if (records.Count == 0)
            return 0;

        var loaded = 0;
        foreach (var record in records)
        {
            var request = record.Request;
            if (record.TransactionId == Guid.Empty
                || request is null
                || request.TransactionId != record.TransactionId
                || request.MachineGuid == Guid.Empty)
            {
                this.monitor.Log($"Durable SVSAPME action escrow {record.TransactionId:N} has no replayable request and remains quarantined.", LogLevel.Warn);
                continue;
            }

            if (this.pendingClientActionsByMachine.TryGetValue(request.MachineGuid, out var existing)
                && existing.TransactionId != request.TransactionId)
            {
                this.monitor.Log($"Durable SVSAPME action escrow {record.TransactionId:N} conflicts with another pending action for machine {request.MachineGuid:N} and remains quarantined.", LogLevel.Warn);
                continue;
            }

            try
            {
                var item = BufferedItemCodec.CreateItem(record.Item);
                if (!this.pendingActionEscrows.Track(record.TransactionId, item))
                    continue;

                var pending = new PendingClientAction(request)
                {
                    LastSentTick = Game1.ticks - ClientActionResponseTimeoutTicks
                };
                this.pendingClientActionsByMachine[request.MachineGuid] = pending;
                loaded++;
            }
            catch (Exception ex)
            {
                this.monitor.Log($"Failed to rehydrate durable SVSAPME action escrow {record.TransactionId:N}: {ex.Message}", LogLevel.Warn);
            }
        }

        if (loaded > 0)
            this.monitor.Log($"Rehydrated {loaded:N0} durable SVSAPME action escrow item(s) for host reconciliation.", LogLevel.Warn);

        return loaded;
    }

    private bool TryReadDurableActionEscrows(Farmer player, out List<PersistentActionEscrowRecord> records)
    {
        records = new List<PersistentActionEscrowRecord>();
        if (!player.modData.TryGetValue(PersistentActionEscrowModDataKey, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        try
        {
            records = JsonSerializer.Deserialize<List<PersistentActionEscrowRecord>>(raw) ?? new List<PersistentActionEscrowRecord>();
            return true;
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Could not parse durable SVSAPME action escrows; payload remains quarantined: {ex.Message}", LogLevel.Warn);
            return false;
        }
    }

    private void WriteDurableActionEscrows(Farmer player, IReadOnlyCollection<PersistentActionEscrowRecord> records)
    {
        if (records.Count == 0)
        {
            player.modData.Remove(PersistentActionEscrowModDataKey);
            return;
        }

        player.modData[PersistentActionEscrowModDataKey] = JsonSerializer.Serialize(records);
    }

    private bool QueueDurableRemoteDeliveryForPlayer(PendingRemoteDelivery delivery)
    {
        if (delivery.TransactionId == Guid.Empty
            || delivery.PlayerId <= 0
            || delivery.ReturnedItems.Count == 0)
        {
            return false;
        }

        var player = Game1.GetPlayer(delivery.PlayerId, onlyOnline: false);
        if (player is null)
            return false;

        try
        {
            var records = this.ReadDurableRemoteDeliveries(player)
                .Where(record => record.TransactionId != delivery.TransactionId)
                .ToList();
            records.Add(new DurableRemoteDeliveryRecord
            {
                TransactionId = delivery.TransactionId,
                MachineGuid = delivery.MachineGuid,
                Message = delivery.Message,
                ConsumeEscrowedItem = delivery.ConsumeEscrowedItem,
                CreatedDay = delivery.CreatedDay,
                ReturnedItems = delivery.ReturnedItems
            });

            while (records.Count > ReconciledTransactionLimit)
                records.RemoveAt(0);

            this.WriteDurableRemoteDeliveries(player, records);
            return true;
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to queue expired SVSAPME delivery {delivery.TransactionId:N} for player {delivery.PlayerId}: {ex.Message}", LogLevel.Warn);
            return false;
        }
    }

    private int RestoreDurableRemoteDeliveries()
    {
        var player = Game1.player;
        if (player is null)
            return 0;

        var records = this.ReadDurableRemoteDeliveries(player);
        if (records.Count == 0)
            return 0;

        var remaining = new List<DurableRemoteDeliveryRecord>();
        var restored = 0;
        var changed = false;
        foreach (var record in records)
        {
            if (record.TransactionId == Guid.Empty || record.ReturnedItems.Count == 0)
            {
                changed = true;
                continue;
            }

            try
            {
                DeliverReturnedItems(record.ReturnedItems);
                PersistReconciledTransaction(record.TransactionId);
                restored++;
                changed = true;
            }
            catch (Exception ex)
            {
                remaining.Add(record);
                this.monitor.Log($"Failed to restore durable SVSAPME delivery {record.TransactionId:N}: {ex.Message}", LogLevel.Warn);
            }
        }

        if (!changed)
            return 0;

        this.WriteDurableRemoteDeliveries(player, remaining);
        if (restored > 0)
            this.monitor.Log($"Restored {restored:N0} durable SVSAPME remote delivery record(s).", LogLevel.Warn);

        return restored;
    }

    private List<DurableRemoteDeliveryRecord> ReadDurableRemoteDeliveries(Farmer player)
    {
        if (!player.modData.TryGetValue(DurableRemoteDeliveryModDataKey, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return new List<DurableRemoteDeliveryRecord>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<DurableRemoteDeliveryRecord>>(raw) ?? new List<DurableRemoteDeliveryRecord>();
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Could not parse durable SVSAPME remote deliveries; clearing malformed payload: {ex.Message}", LogLevel.Warn);
            player.modData.Remove(DurableRemoteDeliveryModDataKey);
            return new List<DurableRemoteDeliveryRecord>();
        }
    }

    private void WriteDurableRemoteDeliveries(Farmer player, IReadOnlyCollection<DurableRemoteDeliveryRecord> records)
    {
        if (records.Count == 0)
        {
            player.modData.Remove(DurableRemoteDeliveryModDataKey);
            return;
        }

        player.modData[DurableRemoteDeliveryModDataKey] = JsonSerializer.Serialize(records);
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
            var snapshot = this.telemetry.GetSnapshot(request.NetworkId);
            response.LastTickGeneratedWh = snapshot.LastGeneratedWh;
            response.LastTickConsumedWh = snapshot.LastConsumedWh;
            response.TodayGeneratedWh = snapshot.TodayGeneratedWh;
            response.TodayConsumedWh = snapshot.TodayConsumedWh;
            response.LastWarning = snapshot.LastWarning;
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

    private SvsapmeMachineSnapshotResponse CreateMachineSnapshotResponse(Guid machineGuid, int offset, int limit)
    {
        if (!this.repository.TryGet(machineGuid, out var state))
        {
            return new SvsapmeMachineSnapshotResponse
            {
                MachineGuid = machineGuid,
                Success = false,
                Message = ModText.Get("hud.multiplayer.unknownMachineGuid", "MachineGuid is unknown on the host.")
            };
        }

        var processorReady = 0;
        var canCollectProcessor = SingleBlockProcessorRules.IsProcessorMachine(state.QualifiedItemId);
        if (canCollectProcessor)
        {
            var tier = SingleBlockProcessorRules.GetTier(state.QualifiedItemId);
            SingleBlockProcessorRules.NormalizeSlots(state.Processor, tier);
            processorReady = SingleBlockProcessorRules.CountReady(state.Processor) + state.Processor.OutputBuffer.Count;
        }

        var response = new SvsapmeMachineSnapshotResponse
        {
            MachineGuid = machineGuid,
            Success = true,
            Message = ModText.Get("hud.multiplayer.machineSnapshotResolved", "Machine snapshot resolved."),
            DisplayName = TryCreateDisplayName(state.QualifiedItemId),
            QualifiedItemId = state.QualifiedItemId,
            LocationName = state.LocationName,
            TileX = state.TileX,
            TileY = state.TileY,
            StoredWh = state.StoredWh,
            CapacityWh = state.CapacityWh,
            ProgressWh = state.ProgressWh,
            OutputBufferStacks = state.OutputBuffer.Count,
            CanCollectProcessorOutput = canCollectProcessor,
            ProcessorReadyStacks = processorReady,
            Revision = Game1.ticks,
            MenuKind = ResolveMenuKind(state.QualifiedItemId),
            Lines = this.CreateMachineSnapshotLines(state)
        };

        var location = Game1.getLocationFromName(state.LocationName);
        var tile = new Vector2(state.TileX, state.TileY);
        if (location is null
            || !location.Objects.TryGetValue(tile, out var placedObject)
            || placedObject.QualifiedItemId != state.QualifiedItemId)
        {
            response.Success = false;
            response.Message = ModText.Get("hud.multiplayer.machineTileMissing", "Machine is missing from its recorded tile.");
            return response;
        }

        var normalizedOffset = Math.Max(0, offset);
        var normalizedLimit = Math.Clamp(limit, 1, 64);
        switch (response.MenuKind)
        {
            case SvsapmeMachineMenuKind.Farm:
                response.Farm = this.CreateFarmMenuSnapshot(state, placedObject, location, tile, normalizedOffset, normalizedLimit);
                break;
            case SvsapmeMachineMenuKind.Processor:
                response.Processor = this.CreateProcessorMenuSnapshot(state, placedObject, location, tile, normalizedOffset, normalizedLimit);
                break;
            case SvsapmeMachineMenuKind.PoweredTransfer:
                response.PoweredTransfer = this.CreatePoweredTransferMenuSnapshot(placedObject, location, tile);
                break;
            case SvsapmeMachineMenuKind.EnergyMonitor:
                response.EnergyMonitor = this.CreateEnergyMonitorSnapshot(location, tile);
                break;
        }

        return response;
    }

    private SvsapmeFarmMenuSnapshot CreateFarmMenuSnapshot(
        MachineState state,
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        int offset,
        int limit)
    {
        var tier = SingleBlockFarmRules.GetFarmTier(placedObject.QualifiedItemId);
        var dashboard = this.singleBlockFarm.GetDashboard(placedObject, location, tile);
        var plots = this.singleBlockFarm.GetPlotViews(placedObject, location, tile);
        var safeOffset = Math.Clamp(offset, 0, Math.Max(0, tier.Plots - 1));
        return new SvsapmeFarmMenuSnapshot
        {
            PlotCapacity = tier.Plots,
            Offset = safeOffset,
            AutoPullFromNetwork = dashboard.AutoPullFromNetwork,
            AutoPushOutputToNetwork = dashboard.AutoPushOutputToNetwork,
            AutoHarvest = true,
            InputMode = dashboard.InputMode,
            FilterMode = dashboard.FilterMode,
            SeedFilterQualifiedItemIds = state.Farm.SeedFilterQualifiedItemIds.ToList(),
            InputBuffer = state.Farm.InputBuffer.Select(CloneBufferedStack).ToList(),
            FertilizerQualifiedItemId = state.Farm.BoundFertilizerQualifiedItemId,
            FertilizerCount = state.Farm.InternalFertilizerCount,
            InstalledModuleQualifiedItemIds = state.Farm.InstalledModuleQualifiedItemIds.ToList(),
            ModuleSlotCapacity = dashboard.ModuleSlotsCapacity,
            Plots = plots
                .Skip(safeOffset)
                .Take(limit)
                .Select(plot => new SvsapmeFarmPlotSnapshot
                {
                    PlotIndex = plot.PlotIndex,
                    SeedQualifiedItemId = plot.SeedQualifiedItemId,
                    HarvestQualifiedItemId = plot.HarvestQualifiedItemId,
                    FertilizerQualifiedItemId = plot.FertilizerQualifiedItemId,
                    LockedSeedQualifiedItemId = plot.LockedSeedQualifiedItemId,
                    ProgressUnits = plot.ProgressUnits,
                    RequiredUnits = plot.RequiredUnits,
                    Ready = plot.Ready,
                    IsLocked = plot.IsLocked
                })
                .ToList(),
            OutputBuffer = state.OutputBuffer.Select(CloneBufferedStack).ToList(),
            EstimatedDailyValue = dashboard.EstimatedDailyValue,
            EstimatedDailyEnergyWh = dashboard.EstimatedDailyEnergyWh
        };
    }

    private SvsapmeProcessorMenuSnapshot CreateProcessorMenuSnapshot(
        MachineState state,
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        int offset,
        int limit)
    {
        var tier = SingleBlockProcessorRules.GetTier(placedObject.QualifiedItemId);
        var dashboard = this.singleBlockProcessor.GetDashboard(placedObject, location, tile);
        var slots = this.singleBlockProcessor.GetSlotViews(placedObject, location, tile);
        var safeOffset = Math.Clamp(offset, 0, Math.Max(0, tier.Slots - 1));
        return new SvsapmeProcessorMenuSnapshot
        {
            SlotCapacity = tier.Slots,
            Offset = safeOffset,
            AutoPullFromNetwork = dashboard.AutoPullFromNetwork,
            AutoPushOutputToNetwork = dashboard.AutoPushOutputToNetwork,
            InputMode = dashboard.InputMode,
            FilterMode = dashboard.FilterMode,
            FilterQualifiedItemIds = state.Processor.FilterQualifiedItemIds.ToList(),
            InputBuffer = state.Processor.InputBuffer.Select(CloneBufferedStack).ToList(),
            OutputBuffer = state.Processor.OutputBuffer.Select(CloneBufferedStack).ToList(),
            Slots = slots
                .Skip(safeOffset)
                .Take(limit)
                .Select(slot => new SvsapmeProcessorSlotSnapshot
                {
                    SlotIndex = slot.SlotIndex,
                    Input = slot.Input is null ? null : CloneBufferedStack(slot.Input),
                    Output = slot.Output is null ? null : CloneBufferedStack(slot.Output),
                    Ready = slot.Ready,
                    CanEject = slot.CanEject,
                    CanCollect = slot.CanCollect,
                    Remaining = slot.Remaining,
                    Total = slot.Total
                })
                .ToList(),
            EstimatedDailyValue = dashboard.EstimatedDailyValue
        };
    }

    private SvsapmePoweredTransferMenuSnapshot CreatePoweredTransferMenuSnapshot(
        SObject placedObject,
        GameLocation location,
        Vector2 tile)
    {
        var view = this.machineRuntime.GetPoweredTransferMenuView(placedObject);
        var networkStatus = this.machineRuntime.GetPoweredNetworkStatus(location, tile);

        return new SvsapmePoweredTransferMenuSnapshot
        {
            IsBlacklist = view.IsBlacklist,
            OreDictionaryEnabled = view.OreDictionaryEnabled,
            QualityStrategy = view.QualityStrategy,
            FacingDirection = view.FacingDirection,
            FilterSlots = view.FilterSlots.Select(slot => new SvsapmeFilterSlotSnapshot
            {
                SlotIndex = slot.SlotIndex,
                QualifiedItemId = slot.QualifiedItemId,
                DisplayName = slot.DisplayName,
                OreGroups = slot.OreGroups.ToList()
            }).ToList(),
            InstalledUpgradeQualifiedItemIds = view.UpgradeSlotQualifiedItemIds.ToList(),
            UpgradeSlotCapacity = MachineRuntimeService.PoweredTransferUpgradeSlotCount,
            Throughput = view.Throughput,
            TransferIntervalTicks = view.TransferIntervalTicks,
            EnergyPerActionWh = view.EnergyPerActionWh,
            NetworkOnline = networkStatus.Online,
            StoredWh = networkStatus.StoredWh,
            CapacityWh = networkStatus.CapacityWh
        };
    }

    private SvsapmeEnergyMonitorSnapshot? CreateEnergyMonitorSnapshot(GameLocation location, Vector2 tile)
    {
        var api = this.getSvsapApi();
        if (api is null
            || !api.TryGetLinkedEndpoint(location, tile, out var endpoint, out _, out _)
            || endpoint is null)
        {
            return null;
        }

        var view = this.machineRuntime.GetEnergyMonitorView(endpoint.NetworkId);
        return new SvsapmeEnergyMonitorSnapshot
        {
            NetworkId = endpoint.NetworkId,
            Online = view.Online,
            StatusText = view.StatusText,
            StoredWh = view.StoredWh,
            CapacityWh = view.CapacityWh,
            LastTickGeneratedWh = view.LastGeneratedWh,
            LastTickConsumedWh = view.LastConsumedWh,
            TodayGeneratedWh = view.TodayGeneratedWh,
            TodayConsumedWh = view.TodayConsumedWh,
            LastWarning = view.Warning,
            Producers = view.Producers.Select(device => new SvsapmeEnergyDeviceSnapshot
            {
                DeviceId = device.DeviceId,
                DisplayName = device.DisplayName,
                TotalWh = device.Wh,
                Details = device.Details.ToList()
            }).ToList(),
            Consumers = view.Consumers.Select(device => new SvsapmeEnergyDeviceSnapshot
            {
                DeviceId = device.DeviceId,
                DisplayName = device.DisplayName,
                TotalWh = device.Wh,
                Details = device.Details.ToList()
            }).ToList()
        };
    }

    private static BufferedItemStack CloneBufferedStack(BufferedItemStack source)
    {
        return new BufferedItemStack
        {
            QualifiedItemId = source.QualifiedItemId,
            Stack = source.Stack,
            Quality = source.Quality,
            PreservedParentSheetIndex = source.PreservedParentSheetIndex,
            PreserveType = source.PreserveType,
            Price = source.Price,
            Edibility = source.Edibility,
            Category = source.Category,
            Type = source.Type,
            Name = source.Name,
            DisplayName = source.DisplayName,
            Color = source.Color,
            ModData = new Dictionary<string, string>(source.ModData)
        };
    }

    private static SvsapmeMachineMenuKind ResolveMenuKind(string qualifiedItemId)
    {
        if (SingleBlockProcessorRules.IsProcessorMachine(qualifiedItemId))
            return SvsapmeMachineMenuKind.Processor;

        if (qualifiedItemId is
            "(BC)" + ModItemCatalog.CopperFarm
            or "(BC)" + ModItemCatalog.SteelFarm
            or "(BC)" + ModItemCatalog.GoldFarm
            or "(BC)" + ModItemCatalog.IridiumFarm)
        {
            return SvsapmeMachineMenuKind.Farm;
        }

        if (qualifiedItemId is
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
            or "(BC)" + ModItemCatalog.PoweredMachineInterfaceIridium)
        {
            return SvsapmeMachineMenuKind.PoweredTransfer;
        }

        return qualifiedItemId == "(BC)" + ModItemCatalog.EnergyMonitorTerminal
            ? SvsapmeMachineMenuKind.EnergyMonitor
            : SvsapmeMachineMenuKind.Generic;
    }

    private List<string> CreateMachineSnapshotLines(MachineState state)
    {
        var lines = new List<string>
        {
            ModText.Get("ui.machine.name", "Machine: {{name}}", new { name = TryCreateDisplayName(state.QualifiedItemId) }),
            ModText.Get("ui.machine.location", "Location: {{location}} ({{x}}, {{y}})", new { location = state.LocationName, x = state.TileX.ToString("0"), y = state.TileY.ToString("0") }),
            ModText.Get("ui.machine.guid", "GUID: {{guid}}", new { guid = state.MachineGuid.ToString("N") }),
            ModText.Get("ui.machine.storedWh", "Internal energy: {{stored}}/{{capacity}} kWh", new { stored = (state.StoredWh / 1000m).ToString("0.00"), capacity = (state.CapacityWh / 1000m).ToString("0.00") }),
            ModText.Get("ui.machine.progressWh", "Progress energy: {{progress}} kWh", new { progress = (state.ProgressWh / 1000m).ToString("0.00") }),
            ModText.Get("ui.machine.outputStacks", "Output buffer stacks: {{count}}", new { count = state.OutputBuffer.Count.ToString("N0") })
        };

        foreach (var port in MachinePortCatalog.GetPorts(state.QualifiedItemId))
        {
            lines.Add(ModText.Get(
                "ui.machine.port.line",
                "Port {{role}}: {{side}} - {{description}}",
                new
                {
                    role = ModText.Get(port.RoleKey, port.RoleKey),
                    side = ModText.Get(port.SideKey, port.SideKey),
                    description = ModText.Get(port.DescriptionKey, port.DescriptionKey)
                }));
        }

        var location = Game1.getLocationFromName(state.LocationName);
        var tile = new Vector2(state.TileX, state.TileY);
        var api = this.getSvsapApi();
        if (api is not null
            && location is not null
            && api.TryGetLinkedEndpoint(location, tile, out var endpoint, out _, out _)
            && endpoint is not null
            && this.energy.TryGetNetworkEnergy(endpoint.NetworkId, out var networkStoredWh, out var networkCapacityWh, out _))
        {
            lines.Add(ModText.Get("hud.energyMonitor.report", "SVSAPME energy: {{storedKwh}}/{{capacityKwh}} kWh.", new { storedKwh = $"{networkStoredWh / 1000m:0.00}", capacityKwh = $"{networkCapacityWh / 1000m:0.00}" }));
            var telemetry = this.telemetry.GetSnapshot(endpoint.NetworkId);
            lines.Add(ModText.Get("ui.energyMeter.lastTick", "Last flow: +{{generated}} / -{{consumed}} / net {{net}}", new { generated = FormatWh(telemetry.LastGeneratedWh), consumed = FormatWh(telemetry.LastConsumedWh), net = FormatSignedWh(telemetry.LastNetWh) }));
            lines.Add(ModText.Get("ui.energyMeter.today", "Today: +{{generated}} / -{{consumed}} / net {{net}}", new { generated = FormatWh(telemetry.TodayGeneratedWh), consumed = FormatWh(telemetry.TodayConsumedWh), net = FormatSignedWh(telemetry.TodayNetWh) }));
        }

        if (!string.IsNullOrWhiteSpace(state.Farm.BoundSeedQualifiedItemId)
            || state.Farm.InternalSeedCount > 0
            || state.Farm.InternalFertilizerCount > 0)
        {
            lines.Add(ModText.Get("ui.farm.seedsStored", "Seeds stored: {{count}}", new { count = state.Farm.InternalSeedCount.ToString("N0") }));
            lines.Add(ModText.Get("ui.farm.fertilizerStored", "Fertilizer stored: {{count}}", new { count = state.Farm.InternalFertilizerCount.ToString("N0") }));
            lines.Add(string.IsNullOrWhiteSpace(state.Farm.BoundSeedQualifiedItemId)
                ? ModText.Get("ui.farm.cropNone", "Crop: none bound")
                : ModText.Get("ui.farm.seed", "Seed: {{item}}", new { item = state.Farm.BoundSeedQualifiedItemId }));
        }

        if (SingleBlockProcessorRules.IsProcessorMachine(state.QualifiedItemId))
        {
            var tier = SingleBlockProcessorRules.GetTier(state.QualifiedItemId);
            SingleBlockProcessorRules.NormalizeSlots(state.Processor, tier);
            lines.Add(ModText.Get(
                "ui.processor.slots",
                "Slots: {{active}} active, {{ready}} ready, {{empty}} empty / {{capacity}}",
                new
                {
                    active = SingleBlockProcessorRules.CountActive(state.Processor).ToString("N0"),
                    ready = SingleBlockProcessorRules.CountReady(state.Processor).ToString("N0"),
                    empty = SingleBlockProcessorRules.CountEmpty(state.Processor).ToString("N0"),
                    capacity = tier.Slots.ToString("N0")
                }));

            foreach (var pair in state.Processor.Slots
                .Select((slot, index) => (slot, index))
                .Where(pair => SingleBlockProcessorRules.IsWorking(pair.slot))
                .Take(8))
            {
                lines.Add(ModText.Get(
                    "ui.processor.slot.line",
                    "#{{slot}} {{input}} -> {{output}} ETA {{eta}}",
                    new
                    {
                        slot = (pair.index + 1).ToString("N0"),
                        input = TryCreateDisplayName(pair.slot.Input?.QualifiedItemId ?? string.Empty),
                        output = TryCreateDisplayName(pair.slot.Output?.QualifiedItemId ?? string.Empty),
                        eta = SingleBlockProcessorRules.FormatEta(pair.slot)
                    }));
            }
        }

        return lines;
    }

    private static string FormatWh(long wh)
    {
        return $"{Math.Max(0, wh) / 1000m:0.00} kWh";
    }

    private static string FormatSignedWh(long wh)
    {
        var sign = wh >= 0 ? "+" : "-";
        return sign + FormatWh(Math.Abs(wh));
    }

    private static string TryCreateDisplayName(string qualifiedItemId)
    {
        try
        {
            return ItemRegistry.Create(qualifiedItemId).DisplayName;
        }
        catch
        {
            return qualifiedItemId;
        }
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

    private static string LocalizeMachineActionResponse(bool success)
    {
        return success
            ? ModText.Get("hud.multiplayer.actionSucceeded", "SVSAPME action completed.")
            : ModText.Get("hud.multiplayer.actionFailed", "SVSAPME action failed; please retry.");
    }

    private static void RestoreEscrowedItem(Item item)
    {
        var player = Game1.player;
        if (player is not null && player.addItemToInventoryBool(item))
            return;

        var location = player?.currentLocation ?? Game1.currentLocation;
        if (Context.IsWorldReady && player is not null && location is not null)
            Game1.createItemDebris(item, player.getStandingPosition(), -1, location);
    }

    private bool PeerHasThisMod(IMultiplayerPeer peer)
    {
        return peer.HasSmapi && peer.GetMod(this.manifest.UniqueID) is not null;
    }

    private void DisableClientGameplay(string message)
    {
        this.ClientGameplayEnabled = false;
        this.clientGameplayDisabledMessage = message;
    }

    private string CreateVersionMismatchMessage(string hostVersion, string peerVersion)
    {
        return ModText.Get(
            "hud.multiplayer.versionMismatch",
            "SVSAPME multiplayer version mismatch: host {{host}}, this player {{peer}}. Update every player to the same version before using SVSAPME machine actions.",
            new { host = hostVersion, peer = peerVersion });
    }

    private void WarnPeerVersionMismatch(IMultiplayerPeer peer, string message, string hostVersion, string peerVersion)
    {
        if (!this.warnedMissingPeers.Add(peer.PlayerID))
            return;

        this.monitor.Log($"{message} (peer {peer.PlayerID}; host={hostVersion}; peer={peerVersion})", LogLevel.Warn);
        if (Context.IsWorldReady)
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
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

    private sealed class PendingClientAction
    {
        public PendingClientAction(SvsapmeMachineActionRequest request)
        {
            this.Request = request;
            this.LastSentTick = Game1.ticks;
        }

        public Guid TransactionId => this.Request.TransactionId;
        public SvsapmeMachineActionRequest Request { get; }
        public int LastSentTick { get; set; }
        public int RetryCount { get; set; }
        public bool RetryLimitNotified { get; set; }
    }

    private sealed class PersistentActionEscrowRecord
    {
        public Guid TransactionId { get; set; }
        public SvsapmeMachineActionRequest? Request { get; set; }
        public BufferedItemStack Item { get; set; } = new();
    }

    private sealed class DurableRemoteDeliveryRecord
    {
        public Guid TransactionId { get; set; }
        public Guid MachineGuid { get; set; }
        public string Message { get; set; } = string.Empty;
        public bool ConsumeEscrowedItem { get; set; }
        public int CreatedDay { get; set; }
        public List<BufferedItemStack> ReturnedItems { get; set; } = new();
    }
}
