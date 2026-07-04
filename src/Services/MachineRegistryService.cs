using Microsoft.Xna.Framework;
using SVSAPME.Content;
using SVSAPME.Models;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley.Buildings;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace SVSAPME.Services;

internal sealed class MachineRegistryService
{
    internal const string MachineGuidKey = ModItemCatalog.UniqueId + "/MachineGuid";
    internal const string StoredWhKey = ModItemCatalog.UniqueId + "/StoredWh";
    private const string ReclaimInTransitKey = ModItemCatalog.UniqueId + "/ReclaimInTransit";
    private const string ConsumedCandidateKey = ModItemCatalog.UniqueId + "/ConsumedCandidate";

    private readonly MachineStateRepository repository;
    private readonly IMonitor monitor;
    private readonly Dictionary<Guid, MachineLocation> machinesByGuid = new();

    public MachineRegistryService(MachineStateRepository repository, IMonitor monitor)
    {
        this.repository = repository;
        this.monitor = monitor;
    }

    public IReadOnlyDictionary<Guid, MachineLocation> MachinesByGuid => this.machinesByGuid;

    public void Save()
    {
        this.repository.Save();
    }

    public bool ReconcileMissingMachinesOnDayStarted()
    {
        if (!Context.IsMainPlayer || !Context.IsWorldReady)
            return false;

        var changed = this.PrunePendingReclaimsForLiveMachines();
        var pendingMachineGuids = this.repository.Data.PendingReclaims
            .SelectMany(reclaim => reclaim.MachineGuids)
            .ToHashSet();
        foreach (var state in this.repository.Data.Machines.Values.ToList())
        {
            if (!ModItemCatalog.IsSvsapmeBigCraftable(state.QualifiedItemId)
                || state.ModData.ContainsKey(ReclaimInTransitKey))
            {
                continue;
            }

            if (pendingMachineGuids.Contains(state.MachineGuid))
                continue;

            if (this.TryFindPlacedMachine(state, out var location, out var tile))
            {
                state.LocationName = location.NameOrUniqueName;
                state.TileX = tile.X;
                state.TileY = tile.Y;
                state.MissingDays = 0;
                state.ModData.Remove(ConsumedCandidateKey);
                this.machinesByGuid[state.MachineGuid] = new MachineLocation(state.MachineGuid, state.LocationName, tile, state.QualifiedItemId);
                continue;
            }

            if (this.TryFindHeldMachineItem(state.MachineGuid, out var heldItem))
            {
                changed |= SyncMachineItemState(heldItem, state);
                changed |= state.ModData.Remove(ConsumedCandidateKey);
                if (state.MissingDays != 0)
                {
                    state.MissingDays = 0;
                    changed = true;
                }

                this.machinesByGuid.Remove(state.MachineGuid);
                continue;
            }

            if (state.ModData.Remove(ConsumedCandidateKey))
            {
                changed = true;
                var consumption = RetireConfirmedConsumedMachine(this.repository.Data, state.MachineGuid);

                // Upgrade recipes consume the prior-tier machine item as literal material
                // (SVSAPMEFinalDesign.md: "配方以原机器为原料（升级路径，字面替代）"). A consumed
                // machine is gone; it is never reissued as an item, or the upgrade cost accounted for
                // in the B10 table would be refundable and the whole upgrade economy would collapse.
                // Any residual stored energy is annihilated with the machine, but never silently:
                // we surface a host-side HUD warning plus a log line so the loss is observable.
                if (consumption.DiscardedStoredWh > 0)
                {
                    this.monitor.Log($"Discarded {consumption.DiscardedStoredWh:N0} Wh of residual energy from consumed SVSAPME machine item {state.MachineGuid:N}; charged machines used as crafting material lose their stored energy.", LogLevel.Warn);
                    if (Context.IsWorldReady)
                    {
                        var discardedWh = consumption.DiscardedStoredWh.ToString("N0");
                        Game1.addHUDMessage(new HUDMessage(
                            ModText.Get(
                                "hud.machineConsumedEnergyLost",
                                "A charged SVSAPME machine was used as crafting material; {{wh}} Wh of stored energy was lost.",
                                new { wh = discardedWh }),
                            HUDMessage.error_type));
                    }
                }

                this.machinesByGuid.Remove(state.MachineGuid);
                this.monitor.Log($"Retired SVSAPME machine state {state.MachineGuid:N} after its item form was consumed and no live holder remained.", LogLevel.Trace);
                continue;
            }

            state.MissingDays++;
            changed = true;
            if (!MachineReclaimRules.ShouldQueueMissingMachineReclaim(state.MissingDays))
                continue;

            var reclaim = MachineReclaimRules.CreateOrphanReclaim(state, this.repository.Data.PendingReclaims);
            if (reclaim is null)
                continue;

            this.repository.Data.PendingReclaims.Add(reclaim);
            pendingMachineGuids.Add(state.MachineGuid);
            this.machinesByGuid.Remove(state.MachineGuid);
            this.monitor.Log($"Queued SVSAPME missing-machine reclaim for {state.MachineGuid:N} after {state.MissingDays:N0} missing day(s).", LogLevel.Warn);
        }

        if (changed)
            this.repository.Save();

        return changed;
    }

    public void RebuildCache()
    {
        this.machinesByGuid.Clear();

        if (!Context.IsWorldReady)
            return;

        Utility.ForEachLocation(location =>
        {
            foreach (var pair in location.Objects.Pairs)
            {
                if (this.TryRegisterPlacedMachine(pair.Value, location, pair.Key))
                    continue;
            }

            return true;
        });

        this.monitor.Log($"Rebuilt SVSAPME machine cache with {this.machinesByGuid.Count} placed machine(s).", LogLevel.Trace);
    }

    public void OnObjectListChanged(object? sender, ObjectListChangedEventArgs e)
    {
        if (!Context.IsMainPlayer)
            return;

        var changed = false;
        var addedMachineGuids = e.Added
            .Select(pair => TryReadMachineGuid(pair.Value, out var machineGuid) ? machineGuid : (Guid?)null)
            .Where(machineGuid => machineGuid.HasValue)
            .Select(machineGuid => machineGuid!.Value)
            .ToHashSet();

        foreach (var pair in e.Added)
            changed |= this.TryRegisterPlacedMachine(pair.Value, e.Location, pair.Key);

        foreach (var pair in e.Removed)
            changed |= this.HandleRemovedMachine(pair.Value, e.Location, pair.Key, addedMachineGuids);

        if (changed)
            this.repository.Save();
    }

    public void OnBuildingListChanged(object? sender, BuildingListChangedEventArgs e)
    {
        if (!Context.IsMainPlayer)
            return;

        var changed = false;
        foreach (var building in e.Removed)
            changed |= this.ReclaimRemovedBuilding(building, e.Location);

        if (changed)
            this.repository.Save();
    }

    public bool TryRegisterPlacedMachine(SObject placedObject, GameLocation location, Vector2 tile)
    {
        if (!ModItemCatalog.IsSvsapmeBigCraftable(placedObject.QualifiedItemId))
            return false;

        if (!PersistentLocationEnumerationRules.ShouldRegisterLocation(location))
            return false;

        var machineGuid = EnsureMachineGuid(placedObject);
        var state = this.repository.GetOrCreate(machineGuid);
        state.QualifiedItemId = placedObject.QualifiedItemId;
        state.LocationName = location.NameOrUniqueName;
        state.TileX = tile.X;
        state.TileY = tile.Y;
        state.MachineType = ModItemCatalog.GetLocalKey(placedObject.ItemId);
        state.MissingDays = 0;
        state.ModData.Remove(ReclaimInTransitKey);
        state.ModData.Remove(ConsumedCandidateKey);

        if (long.TryParse(placedObject.modData.GetValueOrDefault(StoredWhKey), out var storedWh))
            state.StoredWh = Math.Max(0, storedWh);

        state.CapacityWh = GetMachineCapacityWh(placedObject.QualifiedItemId);
        this.machinesByGuid[machineGuid] = new MachineLocation(machineGuid, location.NameOrUniqueName, tile, placedObject.QualifiedItemId);
        this.RemovePendingReclaimMachineGuid(machineGuid);
        return true;
    }

    public bool MarkPotentiallyConsumedMachineItem(Item? item)
    {
        if (!TryReadMachineGuid(item, out var machineGuid)
            || item is null
            || !ModItemCatalog.IsSvsapmeBigCraftable(item.QualifiedItemId))
        {
            return false;
        }

        return this.MarkPotentiallyConsumedMachineGuid(machineGuid);
    }

    public bool MarkPotentiallyConsumedMachineGuid(Guid machineGuid)
    {
        if (machineGuid == Guid.Empty
            || !this.repository.Data.Machines.TryGetValue(machineGuid, out var state)
            || !ModItemCatalog.IsSvsapmeBigCraftable(state.QualifiedItemId))
        {
            return false;
        }

        var changed = false;
        if (!state.ModData.TryGetValue(ConsumedCandidateKey, out var oldValue)
            || oldValue != "1")
        {
            state.ModData[ConsumedCandidateKey] = "1";
            changed = true;
        }

        if (state.MissingDays != 0)
        {
            state.MissingDays = 0;
            changed = true;
        }

        changed |= this.machinesByGuid.Remove(machineGuid);
        return changed;
    }

    public bool ObserveMachineItem(Item? item)
    {
        if (!TryReadMachineGuid(item, out var machineGuid))
            return false;

        return this.ObserveMachineGuid(machineGuid);
    }

    public bool ObserveMachineGuid(Guid machineGuid)
    {
        if (machineGuid == Guid.Empty
            || !this.repository.Data.Machines.TryGetValue(machineGuid, out var state))
        {
            return false;
        }

        var changed = state.ModData.Remove(ConsumedCandidateKey);
        if (state.MissingDays != 0)
        {
            state.MissingDays = 0;
            changed = true;
        }

        changed |= this.RemovePendingReclaimMachineGuid(machineGuid);
        changed |= this.machinesByGuid.Remove(machineGuid);
        return changed;
    }

    public bool SyncPlacedMachineState(Guid machineGuid)
    {
        if (!this.machinesByGuid.TryGetValue(machineGuid, out var machine)
            || !this.repository.Data.Machines.TryGetValue(machineGuid, out var state))
        {
            return false;
        }

        var location = Game1.getLocationFromName(machine.LocationName);
        if (location is null
            || !location.Objects.TryGetValue(machine.Tile, out var placedObject)
            || !TryReadMachineGuid(placedObject, out var placedGuid)
            || placedGuid != machineGuid)
        {
            return false;
        }

        return SyncMachineItemState(placedObject, state);
    }

    public bool TryClaimPendingReclaims(Farmer player, out int reclaimedMachines, out int reclaimedItems, out string message)
    {
        return this.TryClaimPendingReclaims(player, includeUnconfirmed: false, out reclaimedMachines, out reclaimedItems, out message);
    }

    public bool TryClaimPendingReclaims(Farmer player, bool includeUnconfirmed, out int reclaimedMachines, out int reclaimedItems, out string message)
    {
        reclaimedMachines = 0;
        reclaimedItems = 0;

        if (this.repository.Data.PendingReclaims.Count == 0)
        {
            message = "No pending SVSAPME reclaim records.";
            return false;
        }

        var location = player.currentLocation ?? Game1.currentLocation;
        if (location is null)
        {
            message = "No current location is available for reclaim crate placement.";
            return false;
        }

        if (!PersistentLocationEnumerationRules.ShouldRegisterLocation(location))
        {
            message = ModText.Get(
                "hud.reclaimClaimPersistentLocation",
                "SVSAPME reclaim crates can only be claimed in persistent locations; move to the farm, farmhouse, town, or another saved location first.");
            return false;
        }

        var items = new List<Item>();
        var reclaimedMachineGuids = new HashSet<Guid>();
        var issuedMachineGuids = new HashSet<Guid>();
        var pruned = this.PrunePendingReclaimsForLiveMachines();
        var skippedUnconfirmed = false;
        foreach (var reclaim in this.repository.Data.PendingReclaims)
        {
            if (!MachineReclaimRules.ShouldClaimReclaim(reclaim, includeUnconfirmed))
            {
                skippedUnconfirmed = true;
                continue;
            }

            var built = this.BuildRecoverableItems(reclaim, reclaimedMachineGuids, verifyNotLive: true);
            var recoverableMachineGuids = built.MachineGuids.ToHashSet();
            if (reclaim.MachineGuids.RemoveAll(machineGuid => !recoverableMachineGuids.Contains(machineGuid)) > 0)
                pruned = true;

            items.AddRange(built.Items);
            issuedMachineGuids.UnionWith(built.MachineGuids);
            reclaimedMachines += built.Machines;
            reclaimedItems += built.BufferedItems;
        }

        if (items.Count == 0)
        {
            if (pruned || this.repository.Data.PendingReclaims.RemoveAll(reclaim => reclaim.MachineGuids.Count == 0) > 0)
                this.repository.Save();

            message = skippedUnconfirmed && !includeUnconfirmed
                ? "Pending reclaim records include unconfirmed entries; use svsapme_claim force to recover non-live unconfirmed records."
                : "Pending reclaim records contained no recoverable machine states.";
            return false;
        }

        Utility.createOverflowChest(location, player.Tile, items);

        this.MarkReclaimIssued(issuedMachineGuids);
        this.RemovePendingReclaimMachineGuids(issuedMachineGuids);
        this.repository.Save();
        message = $"Created SVSAPME reclaim chest with {reclaimedMachines} machine(s) and {reclaimedItems} buffered item stack(s).";
        return true;
    }

    private bool HandleRemovedMachine(SObject placedObject, GameLocation location, Vector2 tile, IReadOnlySet<Guid> addedMachineGuids)
    {
        if (!ModItemCatalog.IsSvsapmeBigCraftable(placedObject.QualifiedItemId))
            return false;

        if (!TryReadMachineGuid(placedObject, out var machineGuid))
            return false;

        if (MachineLifecycleRules.IsSameTickReplayRemoval(machineGuid, addedMachineGuids))
            return true;

        this.machinesByGuid.Remove(machineGuid);
        if (this.repository.TryGet(machineGuid, out var state))
        {
            state.LocationName = location.NameOrUniqueName;
            state.TileX = tile.X;
            state.TileY = tile.Y;
            state.MissingDays = 0;
            placedObject.modData[StoredWhKey] = state.StoredWh.ToString();
        }

        return true;
    }

    private bool ReclaimRemovedBuilding(Building building, GameLocation exteriorLocation)
    {
        var indoorName = building.GetIndoorsName();
        if (string.IsNullOrWhiteSpace(indoorName))
            return false;

        var reclaim = MachineReclaimRules.CreateBuildingDemolishReclaim(
            indoorName,
            building.tileX.Value,
            building.tileY.Value,
            building.tilesWide.Value,
            building.tilesHigh.Value,
            this.repository.Data.Machines.Values,
            this.repository.Data.PendingReclaims);
        if (reclaim is null)
            return false;

        var built = this.BuildRecoverableItems(reclaim, new HashSet<Guid>(), verifyNotLive: true);
        reclaim.MachineGuids = built.MachineGuids;
        if (reclaim.MachineGuids.Count == 0)
        {
            this.monitor.Log($"Skipped SVSAPME reclaim for removed building interior {indoorName}; every matching machine state is still live or already unrecoverable.", LogLevel.Trace);
            return false;
        }

        foreach (var machineGuid in reclaim.MachineGuids)
            this.machinesByGuid.Remove(machineGuid);

        if (built.Items.Count > 0 && this.TryPlaceReclaimCrate(exteriorLocation, reclaim, built.Items, out var placedTile))
        {
            this.MarkReclaimIssued(reclaim);
            Game1.addHUDMessage(new HUDMessage(
                ModText.Get(
                    "hud.reclaimCratePlaced",
                    "SVSAPME reclaimed {{count}} machine(s) from a demolished building.",
                    new { count = built.Machines }),
                HUDMessage.newQuest_type));
            this.monitor.Log($"Placed SVSAPME reclaim crate for removed building interior {indoorName} at {exteriorLocation.NameOrUniqueName} ({placedTile.X:0},{placedTile.Y:0}); machines={built.Machines}, bufferedItems={built.BufferedItems}.", LogLevel.Warn);
            return true;
        }

        this.repository.Data.PendingReclaims.Add(reclaim);
        Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.reclaimCratePlacementFailed", "SVSAPME reclaim crate placement failed; use svsapme_claim to recover pending machines."), HUDMessage.error_type));

        this.monitor.Log($"Queued SVSAPME reclaim for removed building interior {indoorName}: {reclaim.MachineGuids.Count} machine state(s).", LogLevel.Warn);
        return true;
    }

    private bool TryFindPlacedMachine(MachineState state, out GameLocation location, out Vector2 tile)
    {
        location = null!;
        tile = new Vector2(state.TileX, state.TileY);

        var directLocation = Game1.getLocationFromName(state.LocationName);
        if (directLocation is not null
            && directLocation.Objects.TryGetValue(tile, out var directObject)
            && directObject.QualifiedItemId == state.QualifiedItemId
            && TryReadMachineGuid(directObject, out var directGuid)
            && directGuid == state.MachineGuid)
        {
            location = directLocation;
            return true;
        }

        var found = false;
        GameLocation? foundLocation = null;
        var foundTile = tile;
        Utility.ForEachLocation(candidate =>
        {
            foreach (var pair in candidate.Objects.Pairs)
            {
                if (pair.Value.QualifiedItemId != state.QualifiedItemId
                    || !TryReadMachineGuid(pair.Value, out var machineGuid)
                    || machineGuid != state.MachineGuid)
                {
                    continue;
                }

                foundLocation = candidate;
                foundTile = pair.Key;
                found = true;
                return false;
            }

            return true;
        });

        if (foundLocation is not null)
        {
            location = foundLocation;
            tile = foundTile;
        }

        return found;
    }

    private static Guid EnsureMachineGuid(SObject placedObject)
    {
        if (!TryReadMachineGuid(placedObject, out var machineGuid))
        {
            machineGuid = Guid.NewGuid();
            placedObject.modData[MachineGuidKey] = machineGuid.ToString("N");
        }

        return machineGuid;
    }

    private static bool TryReadMachineGuid(SObject placedObject, out Guid machineGuid)
    {
        return Guid.TryParse(placedObject.modData.GetValueOrDefault(MachineGuidKey), out machineGuid);
    }

    private static bool TryReadMachineGuid(Item? item, out Guid machineGuid)
    {
        if (item is not null
            && Guid.TryParse(item.modData.GetValueOrDefault(MachineGuidKey), out machineGuid))
        {
            return true;
        }

        machineGuid = Guid.Empty;
        return false;
    }

    private static long GetMachineCapacityWh(string qualifiedItemId)
    {
        return qualifiedItemId switch
        {
            "(BC)" + ModItemCatalog.CopperEnergyCell => 10_000,
            "(BC)" + ModItemCatalog.SteelEnergyCell => 40_000,
            "(BC)" + ModItemCatalog.GoldEnergyCell => 160_000,
            "(BC)" + ModItemCatalog.IridiumEnergyCell => 640_000,
            "(BC)" + ModItemCatalog.CarbonGenerator => 2_000,
            _ => 0
        };
    }

    private static Item CreateMachineItem(MachineState state)
    {
        var item = ItemRegistry.Create(state.QualifiedItemId, 1);
        SyncMachineItemState(item, state);
        return item;
    }

    private ReclaimBuildResult BuildRecoverableItems(PendingReclaimCrate reclaim, HashSet<Guid> reclaimedMachineGuids, bool verifyNotLive)
    {
        var items = new List<Item>();
        var machineGuids = new List<Guid>();
        var reclaimedMachines = 0;
        var reclaimedItems = 0;
        foreach (var machineGuid in reclaim.MachineGuids)
        {
            if (!reclaimedMachineGuids.Add(machineGuid)
                || !this.repository.Data.Machines.TryGetValue(machineGuid, out var state))
            {
                continue;
            }

            if (!ShouldRecoverMachineForReclaim(verifyNotLive, this.IsMachineGuidLive(state)))
                continue;

            items.Add(CreateMachineItem(state));
            machineGuids.Add(machineGuid);
            reclaimedMachines++;

            foreach (var buffered in state.OutputBuffer)
            {
                items.Add(BufferedItemCodec.CreateItem(buffered));
                reclaimedItems++;
            }

            if (state.Farm.InternalSeedCount > 0
                && !string.IsNullOrWhiteSpace(state.Farm.BoundSeedQualifiedItemId))
            {
                items.Add(ItemRegistry.Create(state.Farm.BoundSeedQualifiedItemId, state.Farm.InternalSeedCount));
                reclaimedItems++;
            }

            if (state.Farm.InternalFertilizerCount > 0
                && !string.IsNullOrWhiteSpace(state.Farm.BoundFertilizerQualifiedItemId))
            {
                items.Add(ItemRegistry.Create(state.Farm.BoundFertilizerQualifiedItemId, state.Farm.InternalFertilizerCount));
                reclaimedItems++;
            }

            foreach (var moduleQualifiedItemId in FarmModuleRules.GetInstalledModuleItems(state.Farm))
            {
                items.Add(ItemRegistry.Create(moduleQualifiedItemId, 1));
                reclaimedItems++;
            }
        }

        return new ReclaimBuildResult(items, machineGuids, reclaimedMachines, reclaimedItems);
    }

    internal static bool ShouldRecoverMachineForReclaim(bool verifyNotLive, bool isMachineGuidLive)
    {
        return !verifyNotLive || !isMachineGuidLive;
    }

    internal static ConfirmedMachineConsumptionResult RetireConfirmedConsumedMachine(MachineSaveData data, Guid machineGuid)
    {
        if (!data.Machines.TryGetValue(machineGuid, out var state))
            return new ConfirmedMachineConsumptionResult(false, 0);

        var discardedStoredWh = Math.Max(0, state.StoredWh);
        data.Machines.Remove(machineGuid);
        for (var i = data.PendingReclaims.Count - 1; i >= 0; i--)
        {
            var reclaim = data.PendingReclaims[i];
            reclaim.MachineGuids.RemoveAll(id => id == machineGuid);
            if (reclaim.MachineGuids.Count == 0)
                data.PendingReclaims.RemoveAt(i);
        }

        return new ConfirmedMachineConsumptionResult(true, discardedStoredWh);
    }

    private void MarkReclaimIssued(PendingReclaimCrate reclaim)
    {
        this.MarkReclaimIssued(reclaim.MachineGuids);
    }

    private void MarkReclaimIssued(IEnumerable<Guid> machineGuids)
    {
        foreach (var machineGuid in machineGuids)
        {
            if (!this.repository.Data.Machines.TryGetValue(machineGuid, out var state))
                continue;

            state.OutputBuffer.Clear();
            state.Farm.InternalSeedCount = 0;
            state.Farm.InternalFertilizerCount = 0;
            FarmModuleRules.ClearInstalledModules(state.Farm);
            state.ModData[ReclaimInTransitKey] = "1";
            state.MissingDays = 0;
        }
    }

    private bool PrunePendingReclaimsForLiveMachines()
    {
        var liveMachineGuids = new HashSet<Guid>();
        foreach (var reclaim in this.repository.Data.PendingReclaims)
        {
            foreach (var machineGuid in reclaim.MachineGuids)
            {
                if (!this.repository.Data.Machines.TryGetValue(machineGuid, out var state))
                    continue;

                if (this.IsMachineGuidLive(state))
                    liveMachineGuids.Add(machineGuid);
            }
        }

        return this.RemovePendingReclaimMachineGuids(liveMachineGuids);
    }

    private bool IsMachineGuidLive(MachineState state)
    {
        return this.TryFindPlacedMachine(state, out _, out _)
            || this.TryFindHeldMachineItem(state.MachineGuid, out _);
    }

    private bool RemovePendingReclaimMachineGuid(Guid machineGuid)
    {
        return this.RemovePendingReclaimMachineGuids(new[] { machineGuid });
    }

    private bool RemovePendingReclaimMachineGuids(IEnumerable<Guid> machineGuids)
    {
        var remove = machineGuids.ToHashSet();
        if (remove.Count == 0)
            return false;

        var changed = false;
        for (var i = this.repository.Data.PendingReclaims.Count - 1; i >= 0; i--)
        {
            var reclaim = this.repository.Data.PendingReclaims[i];
            var oldCount = reclaim.MachineGuids.Count;
            reclaim.MachineGuids.RemoveAll(remove.Contains);
            if (reclaim.MachineGuids.Count != oldCount)
                changed = true;

            if (reclaim.MachineGuids.Count == 0)
                this.repository.Data.PendingReclaims.RemoveAt(i);
        }

        return changed;
    }

    private bool TryFindHeldMachineItem(Guid machineGuid, out Item item)
    {
        foreach (var candidate in this.EnumeratePersistentInventoryItems())
        {
            if (candidate is not null
                && Guid.TryParse(candidate.modData.GetValueOrDefault(MachineGuidKey), out var heldGuid)
                && heldGuid == machineGuid)
            {
                item = candidate;
                return true;
            }
        }

        item = null!;
        return false;
    }

    private IEnumerable<Item?> EnumeratePersistentInventoryItems()
    {
        var items = new List<Item?>();
        foreach (var farmer in Game1.getAllFarmers())
        {
            foreach (var item in farmer.Items)
                AddPersistentInventoryItem(items, item);
        }

        foreach (var item in EnumerateTeamGlobalInventoryItems())
            AddPersistentInventoryItem(items, item);

        Utility.ForEachLocation(location =>
        {
            if (!PersistentLocationEnumerationRules.ShouldRegisterLocation(location))
                return true;

            foreach (var pair in location.Objects.Pairs)
            {
                if (pair.Value is not Chest chest)
                    continue;

                foreach (var item in chest.Items)
                    AddPersistentInventoryItem(items, item);
            }

            foreach (var fridge in EnumerateFridges(location))
            {
                foreach (var item in fridge.Items)
                    AddPersistentInventoryItem(items, item);
            }

            return true;
        });

        return items;
    }

    private static void AddPersistentInventoryItem(ICollection<Item?> items, Item? item, int depth = 0)
    {
        items.Add(item);
        if (item is not Chest chest || depth >= 8)
            return;

        foreach (var nested in chest.Items)
            AddPersistentInventoryItem(items, nested, depth + 1);
    }

    private static IEnumerable<Item?> EnumerateTeamGlobalInventoryItems()
    {
        var team = Game1.player?.team;
        if (team is null)
            yield break;

        foreach (var item in team.GetOrCreateGlobalInventory(FarmerTeam.GlobalInventoryId_JunimoChest))
            yield return item;
    }

    private static IEnumerable<Chest> EnumerateFridges(GameLocation location)
    {
        switch (location)
        {
            case FarmHouse farmHouse when farmHouse.fridge.Value is not null:
                yield return farmHouse.fridge.Value;
                break;
            case IslandFarmHouse islandFarmHouse when islandFarmHouse.fridge.Value is not null:
                yield return islandFarmHouse.fridge.Value;
                break;
        }
    }

    private static bool SyncMachineItemState(Item item, MachineState state)
    {
        var changed = false;
        changed |= SetModData(item, MachineGuidKey, state.MachineGuid.ToString("N"));
        if (state.StoredWh > 0)
            changed |= SetModData(item, StoredWhKey, state.StoredWh.ToString());
        else
            changed |= item.modData.Remove(StoredWhKey);

        foreach (var pair in state.ModData)
            changed |= SetModData(item, pair.Key, pair.Value);

        return changed;
    }

    private static bool SetModData(Item item, string key, string value)
    {
        if (item.modData.TryGetValue(key, out var oldValue)
            && string.Equals(oldValue, value, StringComparison.Ordinal))
        {
            return false;
        }

        item.modData[key] = value;
        return true;
    }

    private bool TryPlaceReclaimCrate(GameLocation location, PendingReclaimCrate reclaim, List<Item> items, out Vector2 placedTile)
    {
        foreach (var (x, y) in MachineReclaimRules.EnumerateSpiralTiles(
                     reclaim.TileX,
                     reclaim.TileY,
                     MachineReclaimRules.MaxReclaimPlacementSearchRadius))
        {
            var tile = new Vector2(x, y);
            if (!IsSafeReclaimCrateTile(location, tile))
                continue;

            Utility.createOverflowChest(location, tile, items);
            placedTile = tile;
            return true;
        }

        placedTile = Vector2.Zero;
        return false;
    }

    private static bool IsSafeReclaimCrateTile(GameLocation location, Vector2 tile)
    {
        return !location.Objects.ContainsKey(tile)
            && !location.terrainFeatures.ContainsKey(tile)
            && !location.IsTileOccupiedBy(tile);
    }
}

internal sealed record MachineLocation(
    Guid MachineGuid,
    string LocationName,
    Vector2 Tile,
    string QualifiedItemId);

internal sealed record ReclaimBuildResult(List<Item> Items, List<Guid> MachineGuids, int Machines, int BufferedItems);

internal readonly record struct ConfirmedMachineConsumptionResult(bool Retired, long DiscardedStoredWh);
