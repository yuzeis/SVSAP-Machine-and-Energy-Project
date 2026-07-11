using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SVSAPME.Models;
using SVSAPME.Services;
using StardewValley;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace SVSAPME.UI;

internal sealed class SingleBlockProcessorMenu : IClickableMenu
{
    private const int Pad = SvsapmeUiText.ContentPad;
    private const int SlotSize = 64;
    private const int MaxColumns = 8;
    private const int MaxRows = 8;
    private const int GridLeftOffset = 175;
    private const int LeftPanelWidth = 160;
    private const int RightPanelMinWidth = 170;
    private const int PanelGap = 15;
    private const int GridTopOffset = 96;
    private const int PreviewSlotSize = 38;
    private const int InventoryCell = 52;
    private const int InventorySlotSize = 48;
    private const int InventorySlotCount = 36;
    private const int ViewRefreshTicks = 5;
    private const int PortSlotWidth = 84;
    private const int PortSlotHeight = 30;
    private const int PortSlotGap = 6;
    private const int UpgradeSlotSize = 30;
    private const int UpgradeSlotGap = 4;
    private const int UpgradeLabelWidth = 58;
    private const int HeaderStripGap = 8;
    private static readonly Rectangle PanelSource = new(0, 256, 60, 60);

    private readonly SObject machine;
    private readonly GameLocation location;
    private readonly Vector2 tile;
    private readonly SingleBlockProcessorService service;
    private readonly ClickableComponent prevButton;
    private readonly ClickableComponent nextButton;
    private readonly ClickableComponent collectAllButton;
    private readonly ClickableComponent autoInButton;
    private readonly ClickableComponent autoOutButton;
    private readonly ClickableComponent inputModeButton;
    private readonly ClickableComponent filterModeButton;
    private readonly ClickableComponent addFilterButton;
    private readonly ClickableComponent clearFilterButton;
    private readonly int columns;
    private readonly int rows;
    private readonly int pageSize;
    private readonly SvsapmeItemIconCache itemIconCache = new();
    private IReadOnlyList<ProcessorSlotView> cachedViews = Array.Empty<ProcessorSlotView>();
    private ProcessorDashboardView cachedDashboard = new(false, false, false, 0, 0, 0, false, false, MachineInputModes.AllEligible, MachineFilterModes.Whitelist, 0, 0, 0, 0, 0, 0, 0m, null, null, null, Array.Empty<string>(), 0, ProcessorUpgradeRules.BaseSpeedPermille, 0);
    private int cachedAtTick = -1;
    private int page;
    private string? hoverText;

    private Rectangle inventoryArea;
    private int backpackColumns;

    public SingleBlockProcessorMenu(SObject machine, GameLocation location, Vector2 tile, SingleBlockProcessorService service)
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
        this.service = service;
        var layout = CalculateLayoutShape(this.width, this.height, Game1.player.Items.Count);
        this.columns = layout.Columns;
        this.rows = layout.Rows;
        this.pageSize = layout.PageSize;

        // Build player inventory area layout
        var innerX = this.xPositionOnScreen + Pad;
        var innerW = this.width - Pad * 2;
        this.backpackColumns = Math.Clamp(innerW / InventoryCell, 4, 12);
        var backpackRows = Math.Max(3, (int)Math.Ceiling(Game1.player.Items.Count / (double)this.backpackColumns));
        var invW = this.backpackColumns * InventoryCell;
        var invH = backpackRows * InventoryCell;
        this.inventoryArea = new Rectangle(
            innerX + Math.Max(0, (innerW - invW) / 2),
            this.yPositionOnScreen + this.height - Pad - invH,
            invW,
            invH
        );

        var buttonY = this.inventoryArea.Y - 50;
        this.prevButton = new ClickableComponent(new Rectangle(this.GridBounds.X, buttonY, 110, 42), "prev", "<");
        this.nextButton = new ClickableComponent(new Rectangle(this.GridBounds.X + 122, buttonY, 110, 42), "next", ">");
        this.collectAllButton = new ClickableComponent(new Rectangle(this.xPositionOnScreen + this.width - Pad - 170, buttonY, 170, 42), "collect", ModText.Get("ui.processor.collectAll", "Collect All"));
        var left = this.LeftPanelBounds;
        var controls = SvsapmeUiText.CalculateControlButtonBounds(left, 98, 5);
        this.autoInButton = new ClickableComponent(controls[0], "autoIn", ModText.Get("ui.processor.autoIn", "Auto In"));
        this.inputModeButton = new ClickableComponent(controls[1], "inputMode", ModText.Get("ui.processor.inputMode", "Input Mode"));
        this.filterModeButton = new ClickableComponent(controls[2], "filterMode", ModText.Get("ui.processor.filterMode", "W/B"));
        this.addFilterButton = new ClickableComponent(controls[3], "addFilter", ModText.Get("ui.processor.addFilter", "+ Filter"));
        this.clearFilterButton = new ClickableComponent(controls[4], "clearFilter", ModText.Get("ui.processor.clearFilter", "Clear"));
        var right = this.RightPanelBounds;
        this.autoOutButton = new ClickableComponent(new Rectangle(right.X + 10, right.Y + 98, right.Width - 20, 34), "autoOut", ModText.Get("ui.processor.autoOut", "Auto Out"));
        if (this.upperRightCloseButton is not null)
        {
            this.upperRightCloseButton.bounds.X = this.xPositionOnScreen + this.width - 62;
            this.upperRightCloseButton.bounds.Y = this.yPositionOnScreen + 14;
        }
    }

    private static int GetMenuWidth() => Math.Min(980, Game1.uiViewport.Width - 48);

    private static int GetMenuHeight() => Math.Min(700, Game1.uiViewport.Height - 48);

    internal static ProcessorMenuLayoutShape CalculateLayoutShape(int menuWidth, int menuHeight, int inventorySlotCount = InventorySlotCount)
    {
        var availableGridWidth = menuWidth - Pad * 2 - LeftPanelWidth - RightPanelMinWidth - PanelGap * 2;
        var columns = Math.Clamp(availableGridWidth / SlotSize, 1, MaxColumns);

        var backpackColumns = Math.Clamp((menuWidth - Pad * 2) / InventoryCell, 4, 12);
        var backpackRows = Math.Max(3, (int)Math.Ceiling(Math.Max(1, inventorySlotCount) / (double)backpackColumns));
        var buttonY = menuHeight - Pad - backpackRows * InventoryCell - 50;
        var availableGridHeight = buttonY - (GridTopOffset + 40) - 12;
        var rows = Math.Clamp(availableGridHeight / SlotSize, 1, MaxRows);

        return new ProcessorMenuLayoutShape(columns, rows, Math.Max(1, columns * rows));
    }

    internal static bool LayoutFits(int menuWidth, int menuHeight, int inventorySlotCount = InventorySlotCount)
    {
        var layout = CalculateLayoutShape(menuWidth, menuHeight, inventorySlotCount);
        var requiredWidth = Pad * 2 + LeftPanelWidth + RightPanelMinWidth + PanelGap * 2 + layout.Columns * SlotSize;
        var gridBottom = GridTopOffset + 40 + layout.Rows * SlotSize;
        var backpackColumns = Math.Clamp((menuWidth - Pad * 2) / InventoryCell, 4, 12);
        var backpackRows = Math.Max(3, (int)Math.Ceiling(Math.Max(1, inventorySlotCount) / (double)backpackColumns));
        var buttonY = menuHeight - Pad - backpackRows * InventoryCell - 50;
        var panelTop = GridTopOffset + 40;
        var panelHeight = Math.Max(layout.Rows * SlotSize, buttonY - 8 - panelTop);
        var panel = new Rectangle(Pad, panelTop, LeftPanelWidth, panelHeight);
        var controls = SvsapmeUiText.CalculateControlButtonBounds(panel, 98, 5);
        return requiredWidth <= menuWidth
            && gridBottom + 12 <= buttonY
            && controls.All(control => control.X >= panel.X
                && control.Y >= panel.Y
                && control.Right <= panel.Right
                && control.Bottom <= panel.Bottom);
    }

    internal static bool PortStripFits(int menuWidth)
    {
        var portWidth = 3 * PortSlotWidth + 2 * PortSlotGap;
        var upgradeWidth = UpgradeLabelWidth + 5 * UpgradeSlotSize + 4 * UpgradeSlotGap;
        return upgradeWidth + HeaderStripGap + portWidth <= menuWidth - Pad * 2;
    }

    private Rectangle GridBounds => new(
        this.xPositionOnScreen + Pad + GridLeftOffset,
        this.yPositionOnScreen + GridTopOffset + 40,
        this.columns * SlotSize,
        this.rows * SlotSize);

    private Rectangle LeftPanelBounds => new(
        this.xPositionOnScreen + Pad,
        this.yPositionOnScreen + GridTopOffset + 40,
        LeftPanelWidth,
        Math.Max(this.rows * SlotSize, this.inventoryArea.Y - 58 - (this.yPositionOnScreen + GridTopOffset + 40)));

    private Rectangle RightPanelBounds => new(
        this.GridBounds.Right + PanelGap,
        this.yPositionOnScreen + GridTopOffset + 40,
        Math.Max(1, this.xPositionOnScreen + this.width - Pad - this.GridBounds.Right - PanelGap),
        Math.Max(this.rows * SlotSize, this.inventoryArea.Y - 58 - (this.yPositionOnScreen + GridTopOffset + 40)));

    private Rectangle InputSlotBounds => new(this.LeftPanelBounds.X + 14, this.LeftPanelBounds.Y + 48, PreviewSlotSize, PreviewSlotSize);
    private Rectangle ReadyOutputSlotBounds => new(this.RightPanelBounds.X + 14, this.RightPanelBounds.Y + 48, PreviewSlotSize, PreviewSlotSize);
    private Rectangle BufferedOutputSlotBounds => new(this.ReadyOutputSlotBounds.Right + 8, this.ReadyOutputSlotBounds.Y, PreviewSlotSize, PreviewSlotSize);

    private Rectangle ProcessorPortStripBounds
    {
        get
        {
            var portCount = Math.Max(1, MachinePortCatalog.GetPorts(this.machine.QualifiedItemId).Count);
            var width = portCount * PortSlotWidth + Math.Max(0, portCount - 1) * PortSlotGap;
            return new Rectangle(
                this.xPositionOnScreen + this.width - Pad - width,
                this.yPositionOnScreen + 102,
                width,
                PortSlotHeight);
        }
    }

    private Rectangle ProcessorUpgradeStripBounds
    {
        get
        {
            var capacity = Math.Max(0, this.GetCachedDashboard().UpgradeSlotCapacity);
            var width = UpgradeLabelWidth + capacity * UpgradeSlotSize + Math.Max(0, capacity - 1) * UpgradeSlotGap;
            return new Rectangle(
                this.xPositionOnScreen + Pad,
                this.yPositionOnScreen + 102,
                width,
                UpgradeSlotSize);
        }
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (this.upperRightCloseButton?.containsPoint(x, y) == true)
        {
            this.exitThisMenu();
            return;
        }

        if (!StardewModdingAPI.Context.IsMainPlayer)
        {
            Game1.playSound("cancel");
            return;
        }

        var views = this.GetCachedViews();
        var maxPage = this.GetMaxPage(views.Count);
        if (this.prevButton.containsPoint(x, y))
        {
            this.page = Math.Max(0, this.page - 1);
            Game1.playSound("shwip");
            return;
        }

        if (this.nextButton.containsPoint(x, y))
        {
            this.page = Math.Min(maxPage, this.page + 1);
            Game1.playSound("shwip");
            return;
        }

        if (this.collectAllButton.containsPoint(x, y))
        {
            this.ShowAndDeliver(this.service.TryCollectOutputForRemotePlayer(this.machine, this.location, this.tile, 0));
            return;
        }

        if (this.autoInButton.containsPoint(x, y))
        {
            this.ShowResult(this.service.ToggleProcessorAutoPull(this.machine, this.location, this.tile));
            return;
        }

        if (this.autoOutButton.containsPoint(x, y))
        {
            this.ShowResult(this.service.ToggleProcessorAutoPush(this.machine, this.location, this.tile));
            return;
        }

        if (this.inputModeButton.containsPoint(x, y))
        {
            this.ShowResult(this.service.ToggleProcessorInputMode(this.machine, this.location, this.tile));
            return;
        }

        if (this.filterModeButton.containsPoint(x, y))
        {
            this.ShowResult(this.service.ToggleProcessorFilterMode(this.machine, this.location, this.tile));
            return;
        }

        if (this.addFilterButton.containsPoint(x, y))
        {
            this.ShowResult(this.service.AddHeldProcessorFilter(this.machine, this.location, this.tile, Game1.player.CurrentItem));
            return;
        }

        if (this.clearFilterButton.containsPoint(x, y))
        {
            this.ShowResult(this.service.ClearProcessorFilter(this.machine, this.location, this.tile));
            return;
        }

        var dashboard = this.GetCachedDashboard();
        var upgradeIndex = this.GetUpgradeIndexAt(x, y, dashboard.UpgradeSlotCapacity);
        if (upgradeIndex >= 0)
        {
            if (upgradeIndex < dashboard.UpgradeQualifiedItemIds.Count)
            {
                this.ShowAndDeliver(this.service.TryRemoveProcessorUpgrade(this.machine, this.location, this.tile, upgradeIndex));
            }
            else if (Game1.player.CurrentItem is Item heldUpgrade)
            {
                var result = this.service.TryInstallProcessorUpgrade(this.machine, this.location, this.tile, heldUpgrade.QualifiedItemId);
                this.ShowResult(result);
                if (result.Success && result.ConsumeEscrowedItem)
                    ConsumeInventoryItem(Game1.player.CurrentToolIndex, 1);
            }
            else
            {
                Game1.addHUDMessage(new HUDMessage(
                    ModText.Get("ui.processor.upgrade.empty", "Install a Speed or Capacity Card here; kegs also accept one Quality Card."),
                    HUDMessage.newQuest_type));
            }

            return;
        }

        if (this.InputSlotBounds.Contains(x, y))
        {
            var held = Game1.player.CurrentItem;
            if (held is null)
            {
                this.ShowAndDeliver(this.service.TryExtractProcessorInput(this.machine, this.location, this.tile));
            }
            else
            {
                var buffered = BufferedItemCodec.FromItem(held);
                buffered.Stack = 1;
                var result = this.service.TryLoadInput(this.machine, this.location, this.tile, buffered);
                this.ShowResult(result);
                if (result.Success && result.ConsumeEscrowedItem)
                    ConsumeInventoryItem(Game1.player.CurrentToolIndex, 1);
            }
            return;
        }

        if (this.ReadyOutputSlotBounds.Contains(x, y) || this.BufferedOutputSlotBounds.Contains(x, y))
        {
            this.ShowAndDeliver(this.service.TryCollectOutputForRemotePlayer(this.machine, this.location, this.tile, 0));
            return;
        }

        // Check if player clicked an inventory item slot
        var inventoryIndex = this.HitInventorySlot(x, y);
        if (inventoryIndex >= 0)
        {
            var item = Game1.player.Items[inventoryIndex];
            if (item is not null)
            {
                if (ProcessorUpgradeRules.IsProcessorUpgradeCard(item.QualifiedItemId))
                {
                    var result = this.service.TryInstallProcessorUpgrade(this.machine, this.location, this.tile, item.QualifiedItemId);
                    this.ShowResult(result);
                    if (result.Success && result.ConsumeEscrowedItem)
                        ConsumeInventoryItem(inventoryIndex, 1);
                    return;
                }

                var bulk = Game1.oldKBState.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift)
                    || Game1.oldKBState.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.RightShift);
                var transferCount = bulk ? item.Stack : 1;
                var buffered = BufferedItemCodec.FromItem(item);
                buffered.Stack = transferCount;
                var loadResult = this.service.TryLoadInput(this.machine, this.location, this.tile, buffered);
                if (loadResult.Success)
                {
                    ConsumeInventoryItem(inventoryIndex, transferCount);

                    Game1.addHUDMessage(new HUDMessage(loadResult.Message, HUDMessage.newQuest_type));
                    Game1.playSound("Ship");
                    this.InvalidateCache();
                }
                else
                {
                    Game1.addHUDMessage(new HUDMessage(loadResult.Message, HUDMessage.error_type));
                    Game1.playSound("cancel");
                }
            }
            return;
        }

        var index = this.GetSlotIndexAt(x, y);
        if (index < 0 || index >= views.Count)
            return;

        var view = views[index];
        if (view.Ready)
        {
            this.ShowAndDeliver(this.service.TryCollectOutputForRemotePlayer(this.machine, this.location, this.tile, view.SlotIndex + 1));
            return;
        }

        if (view.CanEject && GetEjectButtonBounds(this.GetWorkSlotBounds(index)).Contains(x, y))
        {
            this.ShowAndDeliver(this.service.TryCollectOutputForRemotePlayer(this.machine, this.location, this.tile, view.SlotIndex + 1));
            return;
        }

        Game1.addHUDMessage(new HUDMessage(FormatSlotHud(view), HUDMessage.newQuest_type));
    }

    public override void receiveScrollWheelAction(int direction)
    {
        var count = this.GetCachedViews().Count;
        var maxPage = this.GetMaxPage(count);
        this.page = direction > 0 ? Math.Max(0, this.page - 1) : Math.Min(maxPage, this.page + 1);
    }

    public override void draw(SpriteBatch b)
    {
        // 1. Draw outer wood frame and tech inset
        SVSAPME.UI.SvsapmeUiText.DrawStardewAE2Frame(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));

        SvsapmeUiText.DrawFittedTitle(
            b,
            this.machine.DisplayName,
            new Rectangle(this.xPositionOnScreen + Pad + 12, this.yPositionOnScreen + 18, this.width - Pad * 2 - 70, 52),
            Game1.textColor);

        var views = this.GetCachedViews();
        var dashboard = this.GetCachedDashboard();
        this.page = Math.Clamp(this.page, 0, this.GetMaxPage(views.Count));
        this.DrawSummary(b, views);
        this.DrawProcessorUpgrades(b, dashboard);
        this.DrawProcessorPorts(b, dashboard);
        this.DrawSidePanels(b, dashboard);
        this.DrawGrid(b, views);
        this.DrawBottomSummary(b, dashboard);

        // Draw Separator line before inventory
        b.Draw(Game1.staminaRect, new Rectangle(this.xPositionOnScreen + Pad, this.inventoryArea.Y - 12, this.width - Pad * 2, 2), Color.SaddleBrown * 0.45f);

        // Draw Player Inventory
        this.DrawInventory(b);

        DrawButton(b, this.prevButton, this.page > 0);
        DrawButton(b, this.nextButton, this.page < this.GetMaxPage(views.Count));
        DrawButton(b, this.collectAllButton, views.Any(view => view.CanCollect));
        this.upperRightCloseButton?.draw(b);
        if (!string.IsNullOrWhiteSpace(this.hoverText))
            IClickableMenu.drawHoverText(b, this.hoverText, Game1.smallFont);

        this.drawMouse(b);
    }

    private void DrawInventory(SpriteBatch b)
    {
        for (var i = 0; i < Game1.player.Items.Count; i++)
        {
            var bounds = this.GetInventorySlotBounds(i);
            if (!this.inventoryArea.Contains(bounds))
                continue;

            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), bounds.X, bounds.Y, bounds.Width, bounds.Height, Color.White * 0.78f, 1f, false);
            var item = Game1.player.Items[i];
            SvsapmeUiText.DrawItemWithAdaptiveCount(b, item, bounds, item?.Stack ?? 0, 0.68f);
        }
    }

    private Rectangle GetInventorySlotBounds(int index)
    {
        var column = index % this.backpackColumns;
        var row = index / this.backpackColumns;
        return new Rectangle(this.inventoryArea.X + column * InventoryCell, this.inventoryArea.Y + row * InventoryCell, InventorySlotSize, InventorySlotSize);
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

    public override void performHoverAction(int x, int y)
    {
        this.hoverText = null;
        var portIndex = this.GetPortIndexAt(x, y);
        var ports = MachinePortCatalog.GetPorts(this.machine.QualifiedItemId);
        if (portIndex >= 0 && portIndex < ports.Count)
        {
            this.hoverText = FormatPortTooltip(ports[portIndex], this.GetCachedDashboard());
            return;
        }

        var dashboard = this.GetCachedDashboard();
        var upgradeIndex = this.GetUpgradeIndexAt(x, y, dashboard.UpgradeSlotCapacity);
        if (upgradeIndex >= 0)
        {
            this.hoverText = upgradeIndex < dashboard.UpgradeQualifiedItemIds.Count
                ? ProcessorUpgradeRules.GetEffectDescription(
                    SingleBlockProcessorRules.GetProcessorKind(this.machine.QualifiedItemId),
                    dashboard.UpgradeQualifiedItemIds[upgradeIndex])
                : ModText.Get(
                    "ui.processor.upgrade.empty",
                    "Install a Speed or Capacity Card here; kegs also accept one Quality Card.");
            return;
        }

        var views = this.GetCachedViews();
        var index = this.GetSlotIndexAt(x, y);
        if (index >= 0 && index < views.Count)
            this.hoverText = FormatSlotTooltip(views[index]);
    }

    private IReadOnlyList<ProcessorSlotView> GetCachedViews()
    {
        this.RefreshCacheIfNeeded();
        return this.cachedViews;
    }

    private ProcessorDashboardView GetCachedDashboard()
    {
        this.RefreshCacheIfNeeded();
        return this.cachedDashboard;
    }

    private void RefreshCacheIfNeeded()
    {
        var tick = Game1.ticks;
        if (this.cachedAtTick >= 0 && tick >= this.cachedAtTick && tick - this.cachedAtTick < ViewRefreshTicks)
            return;

        this.cachedViews = this.service.GetSlotViews(this.machine, this.location, this.tile);
        this.cachedDashboard = this.service.GetDashboard(this.machine, this.location, this.tile);
        this.cachedAtTick = tick;
    }

    private void InvalidateCache()
    {
        this.cachedAtTick = -1;
        this.itemIconCache.Clear();
    }

    private void DrawSidePanels(SpriteBatch b, ProcessorDashboardView dashboard)
    {
        DrawPanel(b, this.LeftPanelBounds, Color.White * 0.92f);
        DrawPanel(b, this.RightPanelBounds, Color.White * 0.92f);
        SvsapmeUiText.DrawFittedLine(b, ModText.Get("ui.processor.inputTitle", "Input"), new Rectangle(this.LeftPanelBounds.X + 12, this.LeftPanelBounds.Y + 12, this.LeftPanelBounds.Width - 24, 28), Game1.textColor);
        SvsapmeUiText.DrawFittedLine(b, ModText.Get("ui.processor.outputTitle", "Output"), new Rectangle(this.RightPanelBounds.X + 12, this.RightPanelBounds.Y + 12, this.RightPanelBounds.Width - 24, 28), Game1.textColor);

        this.DrawProcessorInputPreview(b, dashboard);
        this.DrawProcessorOutputPreview(b, dashboard);
        DrawButton(b, this.autoInButton, true);
        DrawButton(b, this.inputModeButton, true);
        DrawButton(b, this.filterModeButton, true);
        DrawButton(b, this.addFilterButton, Game1.player.CurrentItem is not null);
        DrawButton(b, this.clearFilterButton, dashboard.FilterCount > 0);
        DrawButton(b, this.autoOutButton, true);

        var inputLines = new[]
        {
            SvsapmeUiText.FormatAuto(dashboard.AutoPullFromNetwork),
            SvsapmeUiText.FormatInputMode(dashboard.InputMode),
            SvsapmeUiText.FormatFilterMode(dashboard.FilterMode),
            SvsapmeUiText.FormatFilterCount(dashboard.FilterCount),
            SvsapmeUiText.FormatBufferCount(dashboard.InputBufferStacks)
        };
        this.DrawStatusLines(b, inputLines, new Rectangle(this.LeftPanelBounds.X + 12, this.clearFilterButton.bounds.Bottom + 10, this.LeftPanelBounds.Width - 24, this.LeftPanelBounds.Bottom - this.clearFilterButton.bounds.Bottom - 18));

        var outputLines = new[]
        {
            SvsapmeUiText.FormatAuto(dashboard.AutoPushOutputToNetwork),
            SvsapmeUiText.FormatReadyCount(dashboard.ReadySlots),
            ModText.Get(
                "ui.processor.upgrade.bufferStatus",
                "Buffer {{used}} / card capacity {{capacity}}",
                new
                {
                    used = dashboard.OutputBufferStacks.ToString("N0"),
                    capacity = dashboard.OutputBufferCapacityItems.ToString("N0")
                }),
            ModText.Get(
                "ui.processor.upgrade.speedStatus",
                "Processing speed: {{percent}}%",
                new { percent = (dashboard.SpeedPermille / 10).ToString("N0") })
        };
        this.DrawStatusLines(b, outputLines, new Rectangle(this.RightPanelBounds.X + 12, this.autoOutButton.bounds.Bottom + 10, this.RightPanelBounds.Width - 24, this.RightPanelBounds.Bottom - this.autoOutButton.bounds.Bottom - 18));
    }

    private void DrawStatusLines(SpriteBatch b, IEnumerable<string> lines, Rectangle bounds)
    {
        SvsapmeUiText.DrawFittedLines(b, lines, bounds, Game1.textColor);
    }

    private void DrawProcessorInputPreview(SpriteBatch b, ProcessorDashboardView dashboard)
    {
        var slot = this.InputSlotBounds;
        DrawPanel(b, slot, dashboard.InputBufferStacks > 0 ? Color.White : Color.Gray * 0.55f);
        DrawBufferedPreview(b, dashboard.InputPreview, slot);
        if (dashboard.InputPreview is null)
            SvsapmeUiText.DrawFittedLine(b, ModText.Get("ui.processor.inputSlot.buffer", "In"), new Rectangle(slot.X + 2, slot.Y + 8, slot.Width - 4, 18), Game1.textColor, 0.5f);
    }

    private void DrawProcessorOutputPreview(SpriteBatch b, ProcessorDashboardView dashboard)
    {
        var readySlot = this.ReadyOutputSlotBounds;
        var bufferSlot = this.BufferedOutputSlotBounds;
        DrawPanel(b, readySlot, dashboard.ReadySlots > 0 ? Color.White : Color.Gray * 0.55f);
        DrawPanel(b, bufferSlot, dashboard.OutputBufferStacks > 0 ? Color.White : Color.Gray * 0.55f);
        DrawBufferedPreview(b, dashboard.ReadyPreview, readySlot);
        DrawBufferedPreview(b, dashboard.OutputPreview, bufferSlot);
        if (dashboard.ReadyPreview is null)
            SvsapmeUiText.DrawFittedLine(b, ModText.Get("ui.processor.outputSlot.ready", "Ready"), new Rectangle(readySlot.X + 2, readySlot.Y + 8, readySlot.Width - 4, 18), Game1.textColor, 0.5f);
        if (dashboard.OutputPreview is null)
            SvsapmeUiText.DrawFittedLine(b, ModText.Get("ui.processor.outputSlot.buffer", "Buf"), new Rectangle(bufferSlot.X + 2, bufferSlot.Y + 8, bufferSlot.Width - 4, 18), Game1.textColor, 0.5f);
    }

    private void DrawBufferedPreview(SpriteBatch b, BufferedItemStack? stack, Rectangle slot)
    {
        if (stack is null)
            return;

        SvsapmeUiText.DrawItemWithAdaptiveCount(b, this.itemIconCache.GetOrCreate(stack), slot, stack.Stack, 0.5f);
    }

    private void DrawBottomSummary(SpriteBatch b, ProcessorDashboardView dashboard)
    {
        var top = this.GridBounds.Bottom + 8;
        var height = Math.Min(36, this.prevButton.bounds.Y - top - 6);
        if (height < 20)
            return;

        var bounds = new Rectangle(this.GridBounds.X, top, this.GridBounds.Width, height);
        DrawPanel(b, bounds, Color.White * 0.68f);
        SvsapmeUiText.DrawFittedLine(
            b,
            ModText.Get(
                "ui.processor.bottomSummary",
                "Day value {{value}}g  Active {{active}}  Ready {{ready}}",
                new
                {
                    value = dashboard.EstimatedDailyValue.ToString("0.##"),
                    active = dashboard.ActiveSlots.ToString("N0"),
                    ready = dashboard.ReadySlots.ToString("N0")
                }),
            new Rectangle(bounds.X + 8, bounds.Y + 8, bounds.Width - 16, bounds.Height - 12),
            Game1.textColor,
            0.58f);
    }

    private void DrawProcessorPorts(SpriteBatch b, ProcessorDashboardView dashboard)
    {
        var ports = MachinePortCatalog.GetPorts(this.machine.QualifiedItemId);
        for (var i = 0; i < ports.Count; i++)
        {
            var bounds = this.GetPortSlotBounds(i);
            var status = GetPortStatus(ports[i], dashboard);
            DrawPanel(b, bounds, Color.White * 0.82f);
            SvsapmeUiText.DrawPixelStatusLight(b, bounds.X + 6, bounds.Y + 11, status);
            SvsapmeUiText.DrawSlotStatusLine(b, bounds, status);
            SvsapmeUiText.DrawFittedLine(
                b,
                GetPortShortLabel(ports[i]),
                new Rectangle(bounds.X + 22, bounds.Y + 4, bounds.Width - 28, bounds.Height - 9),
                Game1.textColor,
                0.62f);
        }
    }

    private void DrawProcessorUpgrades(SpriteBatch b, ProcessorDashboardView dashboard)
    {
        if (dashboard.UpgradeSlotCapacity <= 0)
            return;

        var strip = this.ProcessorUpgradeStripBounds;
        SvsapmeUiText.DrawFittedLine(
            b,
            ModText.Get(
                "ui.processor.upgrade.title",
                "Upgrades {{used}}/{{capacity}}",
                new
                {
                    used = dashboard.UpgradeQualifiedItemIds.Count.ToString("N0"),
                    capacity = dashboard.UpgradeSlotCapacity.ToString("N0")
                }),
            new Rectangle(strip.X, strip.Y + 3, UpgradeLabelWidth - 4, strip.Height - 6),
            Game1.textColor,
            0.5f);

        for (var i = 0; i < dashboard.UpgradeSlotCapacity; i++)
        {
            var bounds = this.GetUpgradeSlotBounds(i);
            var installed = i < dashboard.UpgradeQualifiedItemIds.Count;
            DrawPanel(b, bounds, installed ? Color.White : Color.Gray * 0.55f);
            if (installed)
            {
                SvsapmeUiText.DrawItemWithAdaptiveCount(
                    b,
                    this.itemIconCache.GetOrCreate(dashboard.UpgradeQualifiedItemIds[i]),
                    bounds,
                    1,
                    0.38f);
                SvsapmeUiText.DrawSlotStatusLine(b, bounds, PixelStatus.Ready);
            }
            else
            {
                SvsapmeUiText.DrawGhostUpgradeSlot(b, bounds);
                SvsapmeUiText.DrawSlotStatusLine(b, bounds, PixelStatus.Idle);
            }
        }
    }

    private void DrawSummary(SpriteBatch b, IReadOnlyList<ProcessorSlotView> views)
    {
        var active = views.Count(view => view.Output is not null);
        var ready = views.Count(view => view.Ready);
        var text = ModText.Get(
            "ui.processor.summary",
            "Active {{active}} / Ready {{ready}} / Page {{page}}/{{pages}}",
            new
            {
                active = active.ToString("N0"),
                ready = ready.ToString("N0"),
                page = (this.page + 1).ToString("N0"),
                pages = (this.GetMaxPage(views.Count) + 1).ToString("N0")
            });
        SvsapmeUiText.DrawFittedLine(
            b,
            text,
            new Rectangle(this.xPositionOnScreen + Pad + 12, this.yPositionOnScreen + SvsapmeUiText.ContentTopOffset, this.width - Pad * 2 - 82, 24),
            Game1.textColor);
    }

    private void DrawGrid(SpriteBatch b, IReadOnlyList<ProcessorSlotView> views)
    {
        var grid = this.GridBounds;
        for (var i = 0; i < this.pageSize; i++)
        {
            var column = i % this.columns;
            var row = i / this.columns;
            var bounds = new Rectangle(grid.X + column * SlotSize, grid.Y + row * SlotSize, SlotSize, SlotSize);
            var absoluteIndex = this.page * this.pageSize + i;
            var view = absoluteIndex < views.Count ? views[absoluteIndex] : default;
            var hasSlot = absoluteIndex < views.Count;
            var tint = hasSlot ? Color.White : Color.Gray * 0.55f;
            DrawPanel(b, bounds, tint);
            if (!hasSlot)
                continue;

            var stack = view.Output ?? view.Input;
            if (stack is not null)
                SvsapmeUiText.DrawItemWithAdaptiveCount(b, this.itemIconCache.GetOrCreate(stack), bounds, stack.Stack, 0.75f);

            if (view.Ready)
                b.Draw(Game1.staminaRect, new Rectangle(bounds.X + 4, bounds.Y + 4, bounds.Width - 8, 4), Color.LimeGreen * 0.9f);
            else if (view.Total > 0)
                this.DrawProgress(b, bounds, view);

            var status = PixelStatus.Idle;
            if (view.Ready)
                status = PixelStatus.Ready;
            else if (view.Remaining > 0)
                status = PixelStatus.Processing;

            SvsapmeUiText.DrawPixelStatusLight(b, bounds.X + 4, bounds.Y + 12, status);
            SvsapmeUiText.DrawSlotStatusLine(b, bounds, status);
            if (view.CanEject)
                DrawEjectButton(b, GetEjectButtonBounds(bounds));
        }
    }

    private void DrawProgress(SpriteBatch b, Rectangle bounds, ProcessorSlotView view)
    {
        var total = Math.Max(1, view.Total);
        var done = Math.Clamp(total - Math.Max(0, view.Remaining), 0, total);
        var width = (int)Math.Round((bounds.Width - 8) * (done / (double)total), MidpointRounding.AwayFromZero);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X + 4, bounds.Y + 4, bounds.Width - 8, 4), Color.Black * 0.25f);
        if (width > 0)
            b.Draw(Game1.staminaRect, new Rectangle(bounds.X + 4, bounds.Y + 4, width, 4), Color.Orange * 0.9f);
    }

    private void ShowResult(SvsapmeMachineActionApplyResult result)
    {
        this.InvalidateCache();
        Game1.addHUDMessage(new HUDMessage(result.Message, result.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));
        Game1.playSound(result.Success ? "smallSelect" : "cancel");
    }

    private void ShowAndDeliver(SvsapmeMachineActionApplyResult result)
    {
        if (result.Success)
        {
            foreach (var stack in result.ReturnedItems)
            {
                var item = BufferedItemCodec.CreateItem(stack);
                var remainder = Game1.player.addItemToInventory(item);
                if (remainder is not null)
                    Game1.createItemDebris(remainder, Game1.player.Position, Game1.player.FacingDirection, Game1.currentLocation);
            }
        }
        this.ShowResult(result);
    }

    private static void ConsumeInventoryItem(int inventoryIndex, int count)
    {
        if (inventoryIndex < 0 || inventoryIndex >= Game1.player.Items.Count || Game1.player.Items[inventoryIndex] is not Item item)
            return;
        item.Stack -= Math.Max(1, count);
        if (item.Stack <= 0)
            Game1.player.Items[inventoryIndex] = null;
    }

    private int GetSlotIndexAt(int x, int y)
    {
        var grid = this.GridBounds;
        if (!grid.Contains(x, y))
            return -1;

        var column = (x - grid.X) / SlotSize;
        var row = (y - grid.Y) / SlotSize;
        return this.page * this.pageSize + row * this.columns + column;
    }

    private Rectangle GetPortSlotBounds(int index)
    {
        var strip = this.ProcessorPortStripBounds;
        return new Rectangle(
            strip.X + index * (PortSlotWidth + PortSlotGap),
            strip.Y,
            PortSlotWidth,
            PortSlotHeight);
    }

    private Rectangle GetUpgradeSlotBounds(int index)
    {
        var strip = this.ProcessorUpgradeStripBounds;
        return new Rectangle(
            strip.X + UpgradeLabelWidth + index * (UpgradeSlotSize + UpgradeSlotGap),
            strip.Y,
            UpgradeSlotSize,
            UpgradeSlotSize);
    }

    private int GetUpgradeIndexAt(int x, int y, int capacity)
    {
        for (var i = 0; i < capacity; i++)
        {
            if (this.GetUpgradeSlotBounds(i).Contains(x, y))
                return i;
        }

        return -1;
    }

    private int GetPortIndexAt(int x, int y)
    {
        var ports = MachinePortCatalog.GetPorts(this.machine.QualifiedItemId);
        for (var i = 0; i < ports.Count; i++)
        {
            if (this.GetPortSlotBounds(i).Contains(x, y))
                return i;
        }

        return -1;
    }

    private static PixelStatus GetPortStatus(MachinePortDefinition port, ProcessorDashboardView dashboard)
    {
        return port.RoleKey switch
        {
            "ui.machine.port.itemIn" => dashboard.InputBufferStacks > 0
                ? PixelStatus.Ready
                : dashboard.AutoPullFromNetwork
                    ? dashboard.NetworkOnline ? PixelStatus.Processing : PixelStatus.Offline
                    : PixelStatus.Idle,
            "ui.machine.port.itemOut" => dashboard.ReadySlots > 0 || dashboard.OutputBufferStacks > 0
                ? PixelStatus.Ready
                : dashboard.ActiveSlots > 0
                    ? PixelStatus.Processing
                    : PixelStatus.Idle,
            "ui.machine.port.energyIn" => GetEnergyPortStatus(dashboard),
            _ => PixelStatus.Idle
        };
    }

    private static PixelStatus GetEnergyPortStatus(ProcessorDashboardView dashboard)
    {
        if (dashboard.ActiveSlots <= 0)
            return PixelStatus.Idle;
        if (!dashboard.NetworkOnline)
            return PixelStatus.Offline;
        if (!dashboard.EnergyOnline)
            return PixelStatus.Error;
        return dashboard.StoredWh < dashboard.RequiredWhForNextStep
            ? PixelStatus.Warning
            : PixelStatus.Processing;
    }

    private static string GetPortShortLabel(MachinePortDefinition port)
    {
        return port.RoleKey switch
        {
            "ui.machine.port.itemIn" => ModText.Get("ui.machine.port.short.itemIn", "Input"),
            "ui.machine.port.itemOut" => ModText.Get("ui.machine.port.short.itemOut", "Output"),
            "ui.machine.port.energyIn" => ModText.Get("ui.machine.port.short.energyIn", "Power"),
            _ => ModText.Get(port.RoleKey, port.RoleKey)
        };
    }

    private static string FormatPortTooltip(MachinePortDefinition port, ProcessorDashboardView dashboard)
    {
        var status = GetPortStatus(port, dashboard);
        var tooltip = ModText.Get(
            "ui.machine.port.tooltip",
            "{{role}}\nSide: {{side}}\n{{description}}",
            new
            {
                role = ModText.Get(port.RoleKey, port.RoleKey),
                side = ModText.Get(port.SideKey, port.SideKey),
                description = ModText.Get(port.DescriptionKey, port.DescriptionKey)
            });
        var runtime = port.RoleKey == "ui.machine.port.energyIn" && status == PixelStatus.Warning
            ? ModText.Get(
                "ui.machine.port.runtime.energyLow",
                "Insufficient energy: {{stored}} / {{required}} Wh required for the next step",
                new { stored = dashboard.StoredWh.ToString("N0"), required = dashboard.RequiredWhForNextStep.ToString("N0") })
            : ModText.Get(
                "ui.machine.port.runtime",
                "Runtime: {{status}}",
                new { status = GetStatusText(status) });
        return tooltip + Environment.NewLine + runtime;
    }

    private static string GetStatusText(PixelStatus status)
    {
        return status switch
        {
            PixelStatus.Ready => ModText.Get("ui.machine.port.status.ready", "Ready"),
            PixelStatus.Processing => ModText.Get("ui.machine.port.status.processing", "Processing"),
            PixelStatus.Warning => ModText.Get("ui.machine.port.status.warning", "Warning"),
            PixelStatus.Error => ModText.Get("ui.machine.port.status.error", "No energy storage"),
            PixelStatus.Offline => ModText.Get("ui.machine.port.status.offline", "Network offline"),
            _ => ModText.Get("ui.machine.port.status.idle", "Idle")
        };
    }

    private Rectangle GetWorkSlotBounds(int absoluteIndex)
    {
        var localIndex = absoluteIndex - this.page * this.pageSize;
        var column = localIndex % this.columns;
        var row = localIndex / this.columns;
        return new Rectangle(
            this.GridBounds.X + column * SlotSize,
            this.GridBounds.Y + row * SlotSize,
            SlotSize,
            SlotSize);
    }

    private static Rectangle GetEjectButtonBounds(Rectangle slotBounds)
    {
        return new Rectangle(slotBounds.Right - 22, slotBounds.Bottom - 25, 16, 16);
    }

    private static void DrawEjectButton(SpriteBatch b, Rectangle bounds)
    {
        b.Draw(Game1.staminaRect, bounds, Color.Black * 0.72f);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X + 3, bounds.Y + 7, 10, 2), Color.White);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X + 7, bounds.Y + 3, 2, 8), Color.White);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X + 5, bounds.Y + 5, 2, 2), Color.White);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X + 9, bounds.Y + 5, 2, 2), Color.White);
    }

    private static string FormatSlotHud(ProcessorSlotView view)
    {
        if (view.Output is null)
            return ModText.Get("ui.processor.slot.empty", "Empty");

        return ModText.Get(
            "ui.processor.slot.hud",
            "{{item}} ETA {{eta}}",
            new { item = SingleBlockProcessorService.FormatItem(view.Output.QualifiedItemId), eta = view.Eta });
    }

    private static string FormatSlotTooltip(ProcessorSlotView view)
    {
        var input = view.Input is null
            ? ModText.Get("ui.processor.slot.empty", "Empty")
            : FormatStack(view.Input);
        var output = view.Output is null
            ? ModText.Get("ui.processor.slot.empty", "Empty")
            : FormatStack(view.Output);
        var progress = view.Total > 0
            ? $"{Math.Max(0, view.Total - Math.Max(0, view.Remaining)):N0}/{view.Total:N0}"
            : "0/0";
        var tooltip = ModText.Get(
            "ui.processor.tooltip.slot",
            "Input: {{input}}\nOutput: {{output}}\nETA: {{eta}}\nProgress: {{progress}}",
            new { input, output, eta = view.Eta, progress });
        return view.CanEject
            ? tooltip + "\n" + ModText.Get("ui.processor.tooltip.eject", "Use the arrow button to eject the current quality.")
            : tooltip;
    }

    private static string FormatStack(BufferedItemStack stack)
    {
        var quality = stack.Quality switch
        {
            1 => ModText.Get("ui.quality.silver", "Silver"),
            2 => ModText.Get("ui.quality.gold", "Gold"),
            4 => ModText.Get("ui.quality.iridium", "Iridium"),
            _ => ModText.Get("ui.quality.normal", "Normal")
        };
        return $"{SingleBlockProcessorService.FormatItem(stack.QualifiedItemId)} x{stack.Stack:N0} ({quality})";
    }

    private int GetMaxPage(int count)
    {
        return Math.Max(0, (Math.Max(1, count) - 1) / this.pageSize);
    }

    private static void DrawPanel(SpriteBatch b, Rectangle panel, Color tint)
    {
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, PanelSource, panel.X, panel.Y, panel.Width, panel.Height, tint, 1f, true);
    }

    private static void DrawButton(SpriteBatch b, ClickableComponent button, bool enabled)
    {
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, PanelSource, button.bounds.X, button.bounds.Y, button.bounds.Width, button.bounds.Height, enabled ? Color.White : Color.Gray * 0.7f, 1f, false);
        var color = enabled ? Game1.textColor : Color.DimGray;
        SvsapmeUiText.DrawFittedLine(b, button.label, new Rectangle(button.bounds.X + 8, button.bounds.Y + 4, button.bounds.Width - 16, button.bounds.Height - 8), color);
    }

}

internal readonly record struct ProcessorMenuLayoutShape(int Columns, int Rows, int PageSize);
