using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SVSAPME.Content;
using SVSAPME.Models;
using SVSAPME.Services;
using StardewValley;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace SVSAPME.UI;

internal sealed class PoweredTransferMenu : IClickableMenu
{
    private const int Pad = SvsapmeUiText.ContentPad;
    private const int Cell = 64;
    private const int InventoryCell = 48;
    private const int FilterColumns = 3;
    private const int FilterRows = 3;
    private const int UpgradeCell = 52;
    private const int ControlButtonWidth = 150;
    private const int ControlButtonMinWidth = 72;
    private const int ControlButtonHeight = 42;
    private const int ControlButtonGap = 10;
    private const int ControlRowGap = 12;
    private const int DirectionButtonMaxWidth = 80;
    private const int DirectionButtonMinWidth = 48;
    private const int DirectionButtonGap = 6;
    private const int ViewRefreshTicks = 30;
    private static readonly Rectangle PanelSource = new(0, 256, 60, 60);

    private readonly SObject machine;
    private readonly GameLocation location;
    private readonly Vector2 tile;
    private readonly MachineRuntimeService runtime;
    private readonly List<ClickableComponent> directionButtons = new();
    private ClickableComponent modeButton = null!;
    private ClickableComponent oreButton = null!;
    private ClickableComponent qualityButton = null!;
    private ClickableComponent clearButton = null!;
    private Rectangle filterArea;
    private Rectangle upgradeArea;
    private Rectangle inventoryArea;
    private int backpackColumns;
    private int selectedSlot;
    private int selectedUpgradeSlot = -1;
    private readonly SvsapmeItemIconCache itemIconCache = new();
    private PoweredTransferMenuView cachedView;
    private PoweredNetworkStatusView cachedNetwork;
    private int cachedAtTick = -1;

    public PoweredTransferMenu(SObject machine, GameLocation location, Vector2 tile, MachineRuntimeService runtime)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - GetMenuWidth()) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - GetMenuHeight()) / 2),
            width: GetMenuWidth(),
            height: GetMenuHeight(),
            showUpperRightCloseButton: true)
    {
        this.machine = machine;
        this.location = location;
        this.tile = tile;
        this.runtime = runtime;
        this.BuildLayout();
        this.PositionCloseButton();
    }

    private static int GetMenuWidth() => Math.Max(1, Math.Min(1040, Game1.uiViewport.Width - 48));

    private static int GetMenuHeight() => Math.Max(1, Math.Min(760, Game1.uiViewport.Height - 48));

    private void BuildLayout()
    {
        var innerX = this.xPositionOnScreen + Pad;
        var innerW = this.width - Pad * 2;
        var contentTop = this.yPositionOnScreen + SvsapmeUiText.ContentTopOffset;

        this.filterArea = new Rectangle(innerX, contentTop + 58, FilterColumns * Cell, FilterRows * Cell);
        var controlsX = this.filterArea.Right + 34;
        this.upgradeArea = new Rectangle(controlsX, contentTop, MachineRuntimeService.PoweredTransferUpgradeSlotCount * UpgradeCell, UpgradeCell);
        var controlsY = this.filterArea.Y;
        var controlsAvailable = Math.Max(1, this.xPositionOnScreen + this.width - Pad - controlsX);
        var controlColumns = CalculateControlColumns(controlsAvailable);
        var controlWidth = CalculateControlButtonWidth(controlsAvailable, controlColumns);
        this.modeButton = new ClickableComponent(GetControlButtonBounds(controlsX, controlsY, 0, controlColumns, controlWidth), "mode", ModText.Get("ui.poweredTransfer.action.mode", "Mode"));
        this.oreButton = new ClickableComponent(GetControlButtonBounds(controlsX, controlsY, 1, controlColumns, controlWidth), "ore", string.Empty);
        this.qualityButton = new ClickableComponent(GetControlButtonBounds(controlsX, controlsY, 2, controlColumns, controlWidth), "quality", ModText.Get("ui.poweredTransfer.action.quality", "Quality"));
        this.clearButton = new ClickableComponent(GetControlButtonBounds(controlsX, controlsY, 3, controlColumns, controlWidth), "clear", ModText.Get("ui.poweredTransfer.action.clear", "Clear"));

        this.directionButtons.Clear();
        var dirY = controlsY + GetControlRows(4, controlColumns) * (ControlButtonHeight + ControlRowGap) + 10;
        var directionButtonWidth = CalculateDirectionButtonWidth(controlsAvailable);
        var directions = new[]
        {
            (Value: -1, Label: ModText.Get("ui.machine.powered.direction.all", "all")),
            (Value: 0, Label: ModText.Get("ui.machine.powered.direction.up", "up")),
            (Value: 1, Label: ModText.Get("ui.machine.powered.direction.right", "right")),
            (Value: 2, Label: ModText.Get("ui.machine.powered.direction.down", "down")),
            (Value: 3, Label: ModText.Get("ui.machine.powered.direction.left", "left"))
        };
        for (var i = 0; i < directions.Length; i++)
        {
            this.directionButtons.Add(new ClickableComponent(
                new Rectangle(controlsX + i * (directionButtonWidth + DirectionButtonGap), dirY, directionButtonWidth, 36),
                directions[i].Value.ToString(),
                directions[i].Label));
        }

        this.backpackColumns = Math.Clamp(innerW / InventoryCell, 4, 12);
        var backpackRows = Math.Max(3, (int)Math.Ceiling(Game1.player.Items.Count / (double)this.backpackColumns));
        var invW = this.backpackColumns * InventoryCell;
        var invH = backpackRows * InventoryCell;
        this.inventoryArea = new Rectangle(
            innerX + Math.Max(0, (innerW - invW) / 2),
            this.yPositionOnScreen + this.height - Pad - invH,
            invW,
            invH);
    }

    internal static bool LayoutFits(int menuWidth, int menuHeight = 640, int inventorySlotCount = 36)
    {
        var controlsX = Pad + FilterColumns * Cell + 34;
        var controlsAvailable = menuWidth - Pad - controlsX;
        var controlColumns = CalculateControlColumns(controlsAvailable);
        var controlWidth = CalculateControlButtonWidth(controlsAvailable, controlColumns);
        var directionWidth = CalculateDirectionButtonWidth(controlsAvailable);
        var filterRight = Pad + FilterColumns * Cell;
        var upgradeRight = controlsX + MachineRuntimeService.PoweredTransferUpgradeSlotCount * UpgradeCell;
        var controlRight = controlsX + Math.Min(controlColumns, 3) * controlWidth + Math.Max(0, Math.Min(controlColumns, 3) - 1) * ControlButtonGap;
        var directionRight = controlsX + 5 * directionWidth + 4 * DirectionButtonGap;
        var backpackColumns = Math.Clamp((menuWidth - Pad * 2) / InventoryCell, 4, 12);
        var backpackRows = Math.Max(3, (int)Math.Ceiling(Math.Max(1, inventorySlotCount) / (double)backpackColumns));
        var inventoryTop = menuHeight - Pad - backpackRows * InventoryCell;
        var filterBottom = SvsapmeUiText.ContentTopOffset + 58 + FilterRows * Cell;
        var controlBottom = SvsapmeUiText.ContentTopOffset + 58 + GetControlRows(4, controlColumns) * (ControlButtonHeight + ControlRowGap) + 10 + 36;
        return filterRight <= menuWidth - Pad
            && upgradeRight <= menuWidth - Pad
            && controlRight <= menuWidth - Pad
            && directionRight <= menuWidth - Pad
            && Math.Max(filterBottom, controlBottom) + 12 <= inventoryTop
            && controlColumns >= 1
            && directionWidth >= DirectionButtonMinWidth;
    }

    private static int CalculateControlColumns(int availableWidth)
    {
        if (availableWidth >= ControlButtonMinWidth * 3 + ControlButtonGap * 2)
            return 3;
        if (availableWidth >= ControlButtonMinWidth * 2 + ControlButtonGap)
            return 2;
        return 1;
    }

    private static int CalculateControlButtonWidth(int availableWidth, int columns)
    {
        var fitWidth = (availableWidth - Math.Max(0, columns - 1) * ControlButtonGap) / Math.Max(1, columns);
        return Math.Clamp(fitWidth, ControlButtonMinWidth, ControlButtonWidth);
    }

    private static int CalculateDirectionButtonWidth(int availableWidth)
    {
        var fitWidth = (availableWidth - DirectionButtonGap * 4) / 5;
        return Math.Clamp(fitWidth, DirectionButtonMinWidth, DirectionButtonMaxWidth);
    }

    private static int GetControlRows(int count, int columns)
    {
        return Math.Max(1, (int)Math.Ceiling(count / (double)Math.Max(1, columns)));
    }

    private static Rectangle GetControlButtonBounds(int x, int y, int index, int columns, int width)
    {
        var column = index % Math.Max(1, columns);
        var row = index / Math.Max(1, columns);
        return new Rectangle(
            x + column * (width + ControlButtonGap),
            y + row * (ControlButtonHeight + ControlRowGap),
            width,
            ControlButtonHeight);
    }

    private void PositionCloseButton()
    {
        if (this.upperRightCloseButton is null)
            return;

        this.upperRightCloseButton.bounds.X = this.xPositionOnScreen + this.width - 64;
        this.upperRightCloseButton.bounds.Y = this.yPositionOnScreen + 16;
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (this.upperRightCloseButton?.containsPoint(x, y) == true)
        {
            this.exitThisMenu();
            return;
        }

        if (this.modeButton.containsPoint(x, y))
        {
            this.RunAction(() =>
            {
                var success = this.runtime.TryTogglePoweredFilterMode(this.machine, this.location, this.tile, out var message);
                return (success, message);
            });
            return;
        }

        if (this.oreButton.containsPoint(x, y))
        {
            this.RunAction(() =>
            {
                var success = this.runtime.TryTogglePoweredOreDictionaryMode(this.machine, this.location, this.tile, out var message);
                return (success, message);
            });
            return;
        }

        if (this.qualityButton.containsPoint(x, y))
        {
            this.RunAction(() =>
            {
                var success = this.runtime.TryTogglePoweredQualityStrategy(this.machine, this.location, this.tile, out var message);
                return (success, message);
            });
            return;
        }

        if (this.clearButton.containsPoint(x, y))
        {
            this.RunAction(() =>
            {
                var success = this.runtime.TryClearPoweredFilter(this.machine, this.location, this.tile, out var message);
                return (success, message);
            });
            return;
        }

        foreach (var button in this.directionButtons)
        {
            if (!button.containsPoint(x, y) || !int.TryParse(button.name, out var direction))
                continue;

            this.RunAction(() =>
            {
                var success = this.runtime.TrySetPoweredFacingDirection(this.machine, this.location, this.tile, direction, out var message);
                return (success, message);
            });
            return;
        }

        var filterSlot = this.HitFilterSlot(x, y);
        if (filterSlot >= 0)
        {
            this.selectedSlot = filterSlot;
            this.selectedUpgradeSlot = -1;
            Game1.playSound("smallSelect");
            return;
        }

        var upgradeSlot = this.HitUpgradeSlot(x, y);
        if (upgradeSlot >= 0)
        {
            var slots = this.GetCachedViewData().View.UpgradeSlotQualifiedItemIds;
            if (upgradeSlot < slots.Count && !string.IsNullOrWhiteSpace(slots[upgradeSlot]))
                this.RemoveUpgrade(upgradeSlot);
            else
            {
                this.selectedUpgradeSlot = upgradeSlot;
                Game1.playSound("smallSelect");
            }
            return;
        }

        var inventoryIndex = this.HitInventorySlot(x, y);
        if (inventoryIndex >= 0)
            this.HandleInventoryItem(inventoryIndex);
    }

    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        var filterSlot = this.HitFilterSlot(x, y);
        if (filterSlot >= 0)
        {
            this.RunAction(() =>
            {
                var success = this.runtime.TryClearPoweredFilterSlot(this.machine, this.location, this.tile, filterSlot, out var message);
                return (success, message);
            });
            return;
        }

        var upgradeSlot = this.HitUpgradeSlot(x, y);
        if (upgradeSlot >= 0)
        {
            this.RemoveUpgrade(upgradeSlot);
            return;
        }

        base.receiveRightClick(x, y, playSound);
    }

    public override void draw(SpriteBatch b)
    {
        SvsapmeUiText.DrawStardewAE2Frame(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
        SvsapmeUiText.DrawFittedTitle(
            b,
            this.machine.DisplayName,
            new Rectangle(this.xPositionOnScreen + Pad + 12, this.yPositionOnScreen + 18, this.width - Pad * 2 - 70, 52),
            Game1.textColor);

        var (view, network) = this.GetCachedViewData();
        this.DrawFilterSlots(b, view.FilterSlots);
        this.DrawUpgradeSlots(b, view.UpgradeSlotQualifiedItemIds);
        this.DrawControls(b, view, network);
        this.DrawSeparator(b);
        this.DrawInventory(b);
        this.DrawHoverTooltip(b, view.FilterSlots, view.UpgradeSlotQualifiedItemIds);
        this.upperRightCloseButton?.draw(b);
        this.drawMouse(b);
    }

    private void DrawUpgradeSlots(SpriteBatch b, IReadOnlyList<string> slots)
    {
        for (var index = 0; index < MachineRuntimeService.PoweredTransferUpgradeSlotCount; index++)
        {
            var cell = this.GetUpgradeSlotBounds(index);
            DrawInsetBox(b, cell, Color.White * 0.82f);
            var qualifiedItemId = index < slots.Count ? slots[index] : string.Empty;
            if (string.IsNullOrWhiteSpace(qualifiedItemId))
                SvsapmeUiText.DrawGhostUpgradeSlot(b, cell);
            else
                SvsapmeUiText.DrawItemWithAdaptiveCount(b, this.itemIconCache.GetOrCreate(qualifiedItemId), cell, 1, 0.68f);

            if (index == this.selectedUpgradeSlot)
                b.Draw(Game1.staminaRect, new Rectangle(cell.X - 2, cell.Y - 2, cell.Width + 4, cell.Height + 4), Color.Gold * 0.28f);
            SvsapmeUiText.DrawSlotStatusLine(b, cell, string.IsNullOrWhiteSpace(qualifiedItemId) ? PixelStatus.Idle : PixelStatus.Ready);
        }
    }

    private void DrawFilterSlots(SpriteBatch b, IReadOnlyList<PoweredTransferFilterSlotView> slots)
    {
        for (var index = 0; index < FilterColumns * FilterRows; index++)
        {
            var cell = this.GetFilterSlotBounds(index);
            DrawInsetBox(b, cell, Color.White * 0.88f);
            if (index == this.selectedSlot)
                b.Draw(Game1.staminaRect, new Rectangle(cell.X - 2, cell.Y - 2, cell.Width + 4, cell.Height + 4), Color.LightGreen * 0.35f);

            var view = slots.FirstOrDefault(slot => slot.SlotIndex == index);
            SvsapmeUiText.DrawItemWithAdaptiveCount(b, view?.Item, cell, view?.Occupied == true ? 1 : 0, 0.82f, Color.White * 0.58f);
            SvsapmeUiText.DrawSlotStatusLine(b, cell, view?.Occupied == true ? PixelStatus.Ready : PixelStatus.Idle);
        }
    }

    private void DrawControls(SpriteBatch b, PoweredTransferMenuView view, PoweredNetworkStatusView network)
    {
        this.oreButton.label = view.OreDictionaryEnabled
            ? ModText.Get("ui.poweredTransfer.ore.on", "Ore: on")
            : ModText.Get("ui.poweredTransfer.ore.off", "Ore: off");

        var isBlacklist = view.IsBlacklist;
        var hasOreCard = view.UpgradeSlotQualifiedItemIds.Contains("(O)" + ModItemCatalog.SvsapOreDictionaryCard, StringComparer.Ordinal);
        var hasQualityCard = view.UpgradeSlotQualifiedItemIds.Contains("(O)" + ModItemCatalog.SvsapQualityCard, StringComparer.Ordinal);

        DrawButton(b, this.modeButton, true, isBlacklist ? Color.LightGreen : Color.White);
        DrawButton(b, this.oreButton, hasOreCard, view.OreDictionaryEnabled ? Color.LightGreen : Color.White);
        DrawButton(b, this.qualityButton, hasQualityCard);
        DrawButton(b, this.clearButton, true);

        var facing = view.FacingDirection;
        foreach (var button in this.directionButtons)
        {
            var selected = int.TryParse(button.name, out var direction) && direction == facing;
            DrawButton(b, button, true, selected ? Color.LightGreen : Color.White);
        }

        var x = this.filterArea.Right + 34;
        var y = this.filterArea.Bottom + 18;
        SvsapmeUiText.DrawPixelStatusLight(b, x, y + 6, network.Online ? PixelStatus.Ready : PixelStatus.Offline);
        var networkText = network.Online
            ? ModText.Get("ui.powered.networkStatus", "Power online: {{stored}} / {{capacity}}", new { stored = FormatWh(network.StoredWh), capacity = FormatWh(network.CapacityWh) })
            : ModText.Get("ui.powered.network.offline", "Power offline or no energy cell");
        SvsapmeUiText.DrawFittedLine(
            b,
            networkText,
            new Rectangle(x + 18, y, this.xPositionOnScreen + this.width - Pad - x - 18, 22),
            network.Online ? Game1.textColor : Color.Crimson);

        var meter = new Rectangle(x, y + 25, this.xPositionOnScreen + this.width - Pad - x, 16);
        b.Draw(Game1.staminaRect, meter, Color.Black * 0.42f);
        var ratio = network.CapacityWh <= 0 ? 0m : Math.Clamp(network.StoredWh / (decimal)network.CapacityWh, 0m, 1m);
        var fillWidth = (int)((meter.Width - 4) * ratio);
        if (fillWidth > 0)
            b.Draw(Game1.staminaRect, new Rectangle(meter.X + 2, meter.Y + 2, fillWidth, meter.Height - 4), ratio < 0.15m ? Color.Crimson : ratio < 0.4m ? Color.Orange : Color.LimeGreen);

        var stats = new[]
        {
            ModText.Get("ui.powered.performance", "Throughput: {{count}} / interval: {{ticks}} ticks", new { count = view.Throughput.ToString("N0"), ticks = view.TransferIntervalTicks.ToString("N0") }),
            ModText.Get("ui.powered.energy", "Energy: {{wh}} Wh/action", new { wh = view.EnergyPerActionWh.ToString("0.0#") }),
            ModText.Get("ui.powered.direction", "Direction: {{direction}}", new { direction = this.directionButtons.FirstOrDefault(button => button.name == view.FacingDirection.ToString())?.label ?? ModText.Get("ui.machine.powered.direction.all", "all") })
        };
        var statsY = meter.Bottom + 6;
        var availableHeight = Math.Max(28, this.inventoryArea.Y - 18 - statsY);
        SvsapmeUiText.DrawFittedLines(
            b,
            stats,
            new Rectangle(x, statsY, this.xPositionOnScreen + this.width - Pad - x, availableHeight),
            Game1.textColor);
    }

    private void DrawInventory(SpriteBatch b)
    {
        for (var i = 0; i < Game1.player.Items.Count; i++)
        {
            var bounds = this.GetInventorySlotBounds(i);
            if (!this.inventoryArea.Contains(bounds))
                continue;

            DrawInsetBox(b, bounds, Color.White * 0.78f);
            var item = Game1.player.Items[i];
            SvsapmeUiText.DrawItemWithAdaptiveCount(b, item, bounds, item?.Stack ?? 0, 0.68f);
        }
    }

    private void DrawHoverTooltip(SpriteBatch b, IReadOnlyList<PoweredTransferFilterSlotView> slots, IReadOnlyList<string> upgrades)
    {
        var mx = Game1.getMouseX();
        var my = Game1.getMouseY();
        var slotIndex = this.HitFilterSlot(mx, my);
        if (slotIndex >= 0)
        {
            var view = slots.FirstOrDefault(slot => slot.SlotIndex == slotIndex);
            if (view?.Occupied == true)
            {
                var lines = new List<string>();
                if (view.OreGroups.Count > 0)
                    lines.Add(ModText.Get("ui.poweredTransfer.tooltip.oreGroups", "Ore groups: {{groups}}", new { groups = string.Join(", ", view.OreGroups) }));
                IClickableMenu.drawHoverText(b, string.Join(Environment.NewLine, lines), Game1.smallFont, boldTitleText: view.DisplayName);
            }

            return;
        }

        var upgradeIndex = this.HitUpgradeSlot(mx, my);
        if (upgradeIndex >= 0)
        {
            var qualifiedItemId = upgradeIndex < upgrades.Count ? upgrades[upgradeIndex] : string.Empty;
            var upgrade = this.itemIconCache.GetOrCreate(qualifiedItemId);
            if (upgrade is not null)
                IClickableMenu.drawHoverText(b, upgrade.getDescription(), Game1.smallFont, boldTitleText: upgrade.DisplayName);
            else
                IClickableMenu.drawHoverText(b, ModText.Get("ui.powered.upgrade.empty", "Select this slot, then click a supported upgrade card in your backpack."), Game1.smallFont);
            return;
        }

        var inventoryIndex = this.HitInventorySlot(mx, my);
        if (inventoryIndex >= 0 && inventoryIndex < Game1.player.Items.Count)
        {
            var item = Game1.player.Items[inventoryIndex];
            if (item is not null)
                IClickableMenu.drawHoverText(b, item.getDescription(), Game1.smallFont, boldTitleText: item.DisplayName);
        }
    }

    private void DrawSeparator(SpriteBatch b)
    {
        b.Draw(Game1.staminaRect, new Rectangle(this.xPositionOnScreen + Pad, this.inventoryArea.Y - 12, this.width - Pad * 2, 2), Color.SaddleBrown * 0.45f);
    }

    private void SetFilterFromInventory(int inventoryIndex)
    {
        if (inventoryIndex < 0 || inventoryIndex >= Game1.player.Items.Count)
            return;

        var item = Game1.player.Items[inventoryIndex];
        if (item is null)
            return;

        var slot = this.FindTargetFilterSlot();
        this.RunAction(() =>
        {
            var success = this.runtime.TrySetPoweredFilterSlot(this.machine, this.location, this.tile, slot, item.QualifiedItemId, out var message);
            return (success, message);
        });
        this.selectedSlot = Math.Min(slot + 1, FilterColumns * FilterRows - 1);
    }

    private void HandleInventoryItem(int inventoryIndex)
    {
        if (inventoryIndex < 0 || inventoryIndex >= Game1.player.Items.Count)
            return;

        var item = Game1.player.Items[inventoryIndex];
        if (item is null)
            return;

        if (!IsPoweredUpgradeCard(item.QualifiedItemId))
        {
            this.SetFilterFromInventory(inventoryIndex);
            return;
        }

        var slots = this.GetCachedViewData().View.UpgradeSlotQualifiedItemIds;
        var target = this.selectedUpgradeSlot >= 0
            ? this.selectedUpgradeSlot
            : Enumerable.Range(0, MachineRuntimeService.PoweredTransferUpgradeSlotCount)
                .FirstOrDefault(index => index >= slots.Count || string.IsNullOrWhiteSpace(slots[index]), -1);
        if (target < 0)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.poweredUpgrade.full", "All powered upgrade slots are occupied."), HUDMessage.error_type));
            Game1.playSound("cancel");
            return;
        }

        var result = this.runtime.TryInstallPoweredUpgrade(this.machine, this.location, this.tile, target, item.QualifiedItemId);
        if (result.Success && result.ConsumeEscrowedItem)
        {
            item.Stack--;
            if (item.Stack <= 0)
                Game1.player.Items[inventoryIndex] = null;
            this.selectedUpgradeSlot = -1;
        }

        this.ShowResult(result);
    }

    private void RemoveUpgrade(int slotIndex)
    {
        var result = this.runtime.TryRemovePoweredUpgrade(this.machine, this.location, this.tile, slotIndex);
        if (result.Success)
            DeliverReturnedItems(result.ReturnedItems);
        this.selectedUpgradeSlot = -1;
        this.ShowResult(result);
    }

    private void ShowResult(SvsapmeMachineActionApplyResult result)
    {
        this.InvalidateViewCache();
        if (!string.IsNullOrWhiteSpace(result.Message))
            Game1.addHUDMessage(new HUDMessage(result.Message, result.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));
        Game1.playSound(result.Success ? "smallSelect" : "cancel");
    }

    private static void DeliverReturnedItems(IEnumerable<BufferedItemStack> returnedItems)
    {
        foreach (var stack in returnedItems)
        {
            var item = BufferedItemCodec.CreateItem(stack);
            if (!Game1.player.addItemToInventoryBool(item))
                Game1.createItemDebris(item, Game1.player.getStandingPosition(), -1, Game1.currentLocation);
        }
    }

    private static bool IsPoweredUpgradeCard(string qualifiedItemId)
    {
        return qualifiedItemId is
            ("(O)" + ModItemCatalog.SvsapSpeedCard)
            or ("(O)" + ModItemCatalog.SvsapCapacityCard)
            or ("(O)" + ModItemCatalog.SvsapQualityCard)
            or ("(O)" + ModItemCatalog.SvsapOreDictionaryCard);
    }

    private static string FormatWh(long value)
    {
        return Math.Abs(value) >= 1000 ? $"{value / 1000m:0.00} kWh" : $"{value:N0} Wh";
    }

    private int FindTargetFilterSlot()
    {
        var slots = this.GetCachedViewData().View.FilterSlots;
        if (this.selectedSlot >= 0 && this.selectedSlot < FilterColumns * FilterRows)
            return this.selectedSlot;

        return slots.FirstOrDefault(slot => !slot.Occupied)?.SlotIndex ?? 0;
    }

    private int HitFilterSlot(int x, int y)
    {
        if (!this.filterArea.Contains(x, y))
            return -1;

        var column = (x - this.filterArea.X) / Cell;
        var row = (y - this.filterArea.Y) / Cell;
        if (column < 0 || column >= FilterColumns || row < 0 || row >= FilterRows)
            return -1;

        return row * FilterColumns + column;
    }

    private int HitInventorySlot(int x, int y)
    {
        if (!this.inventoryArea.Contains(x, y))
            return -1;

        var column = (x - this.inventoryArea.X) / InventoryCell;
        var row = (y - this.inventoryArea.Y) / InventoryCell;
        var index = row * this.backpackColumns + column;
        return index >= 0 && index < Game1.player.Items.Count ? index : -1;
    }

    private int HitUpgradeSlot(int x, int y)
    {
        if (!this.upgradeArea.Contains(x, y))
            return -1;

        var index = (x - this.upgradeArea.X) / UpgradeCell;
        return index is >= 0 and < MachineRuntimeService.PoweredTransferUpgradeSlotCount ? index : -1;
    }

    private Rectangle GetFilterSlotBounds(int index)
    {
        var column = index % FilterColumns;
        var row = index / FilterColumns;
        return new Rectangle(this.filterArea.X + column * Cell, this.filterArea.Y + row * Cell, Cell - 4, Cell - 4);
    }

    private Rectangle GetInventorySlotBounds(int index)
    {
        var column = index % this.backpackColumns;
        var row = index / this.backpackColumns;
        return new Rectangle(this.inventoryArea.X + column * InventoryCell, this.inventoryArea.Y + row * InventoryCell, InventoryCell - 4, InventoryCell - 4);
    }

    private Rectangle GetUpgradeSlotBounds(int index)
    {
        return new Rectangle(this.upgradeArea.X + index * UpgradeCell, this.upgradeArea.Y, UpgradeCell - 4, UpgradeCell - 4);
    }

    private void RunAction(Func<(bool Success, string Message)> action)
    {
        var result = action();
        this.InvalidateViewCache();
        if (!string.IsNullOrWhiteSpace(result.Message))
            Game1.addHUDMessage(new HUDMessage(result.Message, result.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));
        Game1.playSound(result.Success ? "smallSelect" : "cancel");
    }

    private (PoweredTransferMenuView View, PoweredNetworkStatusView Network) GetCachedViewData()
    {
        var tick = Game1.ticks;
        if (this.cachedAtTick < 0
            || tick < this.cachedAtTick
            || tick - this.cachedAtTick >= ViewRefreshTicks)
        {
            this.cachedView = this.runtime.GetPoweredTransferMenuView(this.machine);
            this.cachedNetwork = this.runtime.GetPoweredNetworkStatus(this.location, this.tile);
            this.cachedAtTick = tick;
        }

        return (this.cachedView, this.cachedNetwork);
    }

    private void InvalidateViewCache()
    {
        this.cachedAtTick = -1;
        this.itemIconCache.Clear();
    }

    private static void DrawPanel(SpriteBatch b, Rectangle panel)
    {
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, PanelSource, panel.X, panel.Y, panel.Width, panel.Height, Color.White, 1f, true);
    }

    private static void DrawInsetBox(SpriteBatch b, Rectangle bounds, Color tint)
    {
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, PanelSource, bounds.X, bounds.Y, bounds.Width, bounds.Height, tint, 1f, false);
    }

    private static void DrawButton(SpriteBatch b, ClickableComponent button, bool enabled, Color? tint = null)
    {
        DrawInsetBox(b, button.bounds, (tint ?? Color.White) * (enabled ? 1f : 0.65f));
        var color = enabled ? Game1.textColor : Color.DimGray;
        SvsapmeUiText.DrawFittedLine(b, button.label, new Rectangle(button.bounds.X + 8, button.bounds.Y + 4, button.bounds.Width - 16, button.bounds.Height - 8), color);
    }
}
