using Microsoft.Xna.Framework;
using SVSAPME.Content;
using SVSAPME.Models;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Objects;

namespace SVSAPME.Services;

internal sealed class CellStackGuardService
{
    private readonly IMonitor monitor;
    private readonly MachineRegistryService registry;
    private Func<SvsapmeMachineItemMovementReport, bool>? clientMovementReporter;
    private bool normalizing;

    public CellStackGuardService(IMonitor monitor, MachineRegistryService registry)
    {
        this.monitor = monitor;
        this.registry = registry;
    }

    public void SetClientMovementReporter(Func<SvsapmeMachineItemMovementReport, bool> reporter)
    {
        this.clientMovementReporter = reporter;
    }

    public void OnInventoryChanged(object? sender, InventoryChangedEventArgs e)
    {
        if (this.normalizing || e.Player is null)
            return;

        var changed = this.TrackMachineItemMovement(e.Added, e.Removed, e.QuantityChanged.Select(change => change.Item));
        this.normalizing = true;
        try
        {
            this.NormalizeInventory(e.Player);
        }
        finally
        {
            this.normalizing = false;
        }

        if (changed)
            this.registry.Save();
    }

    public void OnChestInventoryChanged(object? sender, ChestInventoryChangedEventArgs e)
    {
        if (this.normalizing || e.Chest is null)
            return;

        var changed = this.TrackMachineItemMovement(e.Added, e.Removed, e.QuantityChanged.Select(change => change.Item));
        if (!Context.IsMainPlayer)
            return;

        this.normalizing = true;
        try
        {
            this.NormalizeChest(e.Chest, e.Location);
        }
        finally
        {
            this.normalizing = false;
        }

        if (changed)
            this.registry.Save();
    }

    private bool TrackMachineItemMovement(IEnumerable<Item?> added, IEnumerable<Item?> removed, IEnumerable<Item?> quantityChanged)
    {
        if (!Context.IsMainPlayer)
        {
            this.ReportClientMachineItemMovement(added, removed, quantityChanged);
            return false;
        }

        var changed = false;
        foreach (var item in removed)
            changed |= this.registry.MarkPotentiallyConsumedMachineItem(item);

        foreach (var item in added.Concat(quantityChanged))
            changed |= this.registry.ObserveMachineItem(item);

        return changed;
    }

    private void ReportClientMachineItemMovement(IEnumerable<Item?> added, IEnumerable<Item?> removed, IEnumerable<Item?> quantityChanged)
    {
        if (this.clientMovementReporter is null)
            return;

        var report = new SvsapmeMachineItemMovementReport
        {
            RemovedMachineGuids = ExtractMachineGuids(removed).ToList(),
            ObservedMachineGuids = ExtractMachineGuids(added.Concat(quantityChanged)).ToList()
        };
        if (report.RemovedMachineGuids.Count == 0 && report.ObservedMachineGuids.Count == 0)
            return;

        this.clientMovementReporter(report);
    }

    private static IEnumerable<Guid> ExtractMachineGuids(IEnumerable<Item?> items)
    {
        return items
            .Where(item => item is not null && ModItemCatalog.IsSvsapmeBigCraftable(item.QualifiedItemId))
            .Select(item => Guid.TryParse(item!.modData.GetValueOrDefault(MachineRegistryService.MachineGuidKey), out var machineGuid) ? machineGuid : Guid.Empty)
            .Where(machineGuid => machineGuid != Guid.Empty)
            .Distinct();
    }

    private void NormalizeInventory(Farmer player)
    {
        for (var i = 0; i < player.Items.Count; i++)
        {
            var item = player.Items[i];
            if (item is null || !long.TryParse(item.modData.GetValueOrDefault(MachineRegistryService.StoredWhKey), out var storedWh))
                continue;

            if (!EnergyCellRules.ShouldSplitChargedStack(item.QualifiedItemId, item.Stack, storedWh))
                continue;

            var remaining = item.Stack - 1;
            item.Stack = 1;

            var uncharged = item.getOne();
            uncharged.Stack = remaining;
            uncharged.modData.Remove(MachineRegistryService.StoredWhKey);
            uncharged.modData.Remove(MachineRegistryService.MachineGuidKey);

            var rejected = player.addItemToInventory(uncharged);
            if (rejected is not null)
            {
                var location = player.currentLocation ?? Game1.currentLocation;
                if (location is not null)
                    Game1.createItemDebris(rejected, player.Position, player.FacingDirection, location);
                else
                    this.monitor.Log($"Could not return split uncharged SVSAPME cell stack {rejected.QualifiedItemId} x{rejected.Stack}; no current location was available.", LogLevel.Error);
            }

            this.monitor.Log($"Split charged SVSAPME energy cell stack in {player.Name}'s inventory to preserve one-cell-one-state semantics.", LogLevel.Trace);
        }
    }

    private void NormalizeChest(Chest chest, GameLocation location)
    {
        for (var i = 0; i < chest.Items.Count; i++)
        {
            var item = chest.Items[i];
            if (item is null || !long.TryParse(item.modData.GetValueOrDefault(MachineRegistryService.StoredWhKey), out var storedWh))
                continue;

            if (!EnergyCellRules.ShouldSplitChargedStack(item.QualifiedItemId, item.Stack, storedWh))
                continue;

            var remaining = item.Stack - 1;
            item.Stack = 1;

            var uncharged = item.getOne();
            uncharged.Stack = remaining;
            uncharged.modData.Remove(MachineRegistryService.StoredWhKey);
            uncharged.modData.Remove(MachineRegistryService.MachineGuidKey);

            var rejected = chest.addItem(uncharged);
            if (rejected is not null)
                Game1.createItemDebris(rejected, (chest.TileLocation + new Vector2(0.5f, 0.5f)) * Game1.tileSize, -1, location);

            this.monitor.Log("Split charged SVSAPME energy cell stack in a chest to preserve one-cell-one-state semantics.", LogLevel.Trace);
        }
    }
}
