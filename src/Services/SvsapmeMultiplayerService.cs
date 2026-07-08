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
    private const string ReconciledTransactionModDataKey = ModItemCatalog.UniqueId + "/ReconciledDeliveries";
    private const string PersistentActionEscrowModDataKey = ModItemCatalog.UniqueId + "/PendingActionEscrows";

    private readonly IModHelper helper;
    private readonly IManifest manifest;
    private readonly MachineStateRepository repository;
    private readonly MachineRegistryService registry;
    private readonly EnergyNetworkManager energy;
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

    public void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (!Context.IsMainPlayer || !Context.IsWorldReady)
            return;

        if (this.PruneConfirmedExpiredPendingDeliveries(Game1.Date.TotalDays) > 0)
            this.repository.Save();
    }

    public bool TrySendMachineSnapshotRequest(Guid machineGuid)
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
                    MachineGuid = machineGuid
                },
                SvsapmeMultiplayerMessageTypes.MachineSnapshotRequest,
                modIDs: new[] { this.manifest.UniqueID },
                playerIDs: new[] { host.PlayerID });
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
        this.RestoreDurableActionEscrows();
        this.pendingClientActionsByMachine.Clear();
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
        }
    }

    public void OnPeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
    {
        this.warnedMissingPeers.Remove(e.Peer.PlayerID);
        this.blockedPeerActionMessages.Remove(e.Peer.PlayerID);
        if (!Context.IsMainPlayer && e.Peer.IsHost)
        {
            this.ClientGameplayEnabled = false;
            this.clientGameplayDisabledMessage = ModText.Get("hud.multiplayer.hostMissingSvsapme", "SVSAPME gameplay is disabled because the host is missing SVSAPME.");
            this.RestorePendingActionEscrows();
            this.pendingClientActionsByMachine.Clear();
            this.reconciledTransactions.Clear();
            this.reconciledTransactionOrder.Clear();
            this.actionResponses.Clear();
            return;
        }
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
        var response = this.CreateMachineSnapshotResponse(request.MachineGuid);
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

        var title = string.IsNullOrWhiteSpace(response.DisplayName)
            ? ModText.Get("ui.machine.remoteTitle", "SVSAPME Machine")
            : response.DisplayName;
        var actions = new List<SvsapmeMenuAction>();
        if (response.CanCollectProcessorOutput)
        {
            actions.Add(new SvsapmeMenuAction(
                ModText.Get("ui.processor.collectAll", "Collect All"),
                () =>
                {
                    var sent = this.TrySendMachineActionRequest(new SvsapmeMachineActionRequest
                    {
                        TransactionId = Guid.NewGuid(),
                        MachineGuid = response.MachineGuid,
                        ActionKind = SvsapmeMachineActionKind.CollectProcessorOutput,
                        Count = 0
                    });
                    if (!sent)
                        return null;

                    Game1.activeClickableMenu?.exitThisMenu();
                    return ModText.Get("hud.multiplayer.actionSent", "SVSAPME action sent to host.");
                },
                () => response.ProcessorReadyStacks > 0));
        }

        Game1.activeClickableMenu = new SvsapmeStatusMenu(title, () => response.Lines, actions);
    }

    private void HandleMachineActionRequest(ModMessageReceivedEventArgs e)
    {
        var request = e.ReadAs<SvsapmeMachineActionRequest>();
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

        Game1.addHUDMessage(new HUDMessage(
            LocalizeMachineActionResponse(response.Success),
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
            if (this.TrackDurableActionEscrow(request.TransactionId, escrowedItem))
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
                new BufferedItemStack
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
                }),
            SvsapmeMachineActionKind.CollectProcessorOutput => this.singleBlockProcessor.TryCollectOutputForRemotePlayer(
                placedObject,
                location,
                tile,
                Math.Max(0, request.Count)),
            _ => new SvsapmeMachineActionApplyResult(false, false, ModText.Get("hud.multiplayer.unsupportedAction", "Unsupported SVSAPME machine action."))
        };

        return new SvsapmeMachineActionResponse
        {
            TransactionId = request.TransactionId,
            MachineGuid = request.MachineGuid,
            Success = result.Success,
            ConsumeEscrowedItem = result.Success && result.ConsumeEscrowedItem,
            Message = result.Message,
            ReturnedItems = result.ReturnedItems
        };
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

        this.pendingClientActionsByMachine[request.MachineGuid] = new PendingClientAction(request.TransactionId);
        return true;
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
        var expired = this.repository.Data.PendingRemoteDeliveries
            .Where(delivery => IsExpiredPendingDelivery(delivery, currentDay))
            .ToList();
        var removed = 0;
        foreach (var delivery in expired)
        {
            if (!PlayerHasPersistedReconciledTransaction(delivery.PlayerId, delivery.TransactionId))
                continue;

            this.repository.Data.PendingRemoteDeliveries.Remove(delivery);
            removed++;
        }

        if (removed > 0)
            this.monitor.Log($"Pruned {removed:N0} confirmed expired SVSAPME pending remote delivery record(s).", LogLevel.Info);

        return removed;
    }

    private static bool IsExpiredPendingDelivery(PendingRemoteDelivery delivery, int currentDay)
    {
        return delivery.CreatedDay > 0
            && currentDay - delivery.CreatedDay >= PendingDeliveryRetentionDays
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
        return actionKind != SvsapmeMachineActionKind.CollectProcessorOutput;
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

    private bool TrackDurableActionEscrow(Guid transactionId, Item item)
    {
        var player = Game1.player;
        if (player is null || transactionId == Guid.Empty)
            return false;

        try
        {
            var records = this.ReadDurableActionEscrows(player)
                .Where(record => record.TransactionId != transactionId)
                .ToList();
            records.Add(new PersistentActionEscrowRecord
            {
                TransactionId = transactionId,
                Item = BufferedItemCodec.FromItem(item)
            });
            this.WriteDurableActionEscrows(player, records);
            return true;
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to persist SVSAPME action escrow {transactionId:N}: {ex.Message}", LogLevel.Warn);
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
            var records = this.ReadDurableActionEscrows(player)
                .Where(record => record.TransactionId != transactionId)
                .ToList();
            this.WriteDurableActionEscrows(player, records);
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to clear SVSAPME action escrow {transactionId:N}: {ex.Message}", LogLevel.Trace);
        }
    }

    private void ClearDurableActionEscrows()
    {
        Game1.player?.modData.Remove(PersistentActionEscrowModDataKey);
    }

    private int RestoreDurableActionEscrows()
    {
        var player = Game1.player;
        if (player is null)
            return 0;

        var records = this.ReadDurableActionEscrows(player);
        if (records.Count == 0)
            return 0;

        this.ClearDurableActionEscrows();
        var restored = 0;
        foreach (var record in records)
        {
            try
            {
                RestoreEscrowedItem(BufferedItemCodec.CreateItem(record.Item));
                restored++;
            }
            catch (Exception ex)
            {
                this.monitor.Log($"Failed to restore durable SVSAPME action escrow {record.TransactionId:N}: {ex.Message}", LogLevel.Warn);
            }
        }

        if (restored > 0)
            this.monitor.Log($"Restored {restored:N0} durable SVSAPME action escrow item(s).", LogLevel.Warn);

        return restored;
    }

    private List<PersistentActionEscrowRecord> ReadDurableActionEscrows(Farmer player)
    {
        if (!player.modData.TryGetValue(PersistentActionEscrowModDataKey, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return new List<PersistentActionEscrowRecord>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<PersistentActionEscrowRecord>>(raw) ?? new List<PersistentActionEscrowRecord>();
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Could not parse durable SVSAPME action escrows; clearing malformed payload: {ex.Message}", LogLevel.Warn);
            player.modData.Remove(PersistentActionEscrowModDataKey);
            return new List<PersistentActionEscrowRecord>();
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

        return new SvsapmeMachineSnapshotResponse
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
            Lines = this.CreateMachineSnapshotLines(state)
        };
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

    private void RestorePendingActionEscrows()
    {
        var restored = this.pendingActionEscrows.RestoreAll(RestoreEscrowedItem);
        this.ClearDurableActionEscrows();
        this.pendingClientActionsByMachine.Clear();
        if (restored > 0)
            this.monitor.Log($"Restored {restored:N0} pending SVSAPME action escrow item(s).", LogLevel.Warn);
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

    private sealed record PendingClientAction(Guid TransactionId);

    private sealed class PersistentActionEscrowRecord
    {
        public Guid TransactionId { get; set; }
        public BufferedItemStack Item { get; set; } = new();
    }
}
