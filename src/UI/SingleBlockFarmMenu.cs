using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SVSAPME.Models;
using SVSAPME.Services;
using StardewValley;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace SVSAPME.UI;

internal sealed class SingleBlockFarmMenu : IClickableMenu
{
    private const int Pad = SvsapmeUiText.ContentPad;
    private const int SlotSize = 52;
    private const int MaxColumns = 8;
    private const int MaxRows = 8;
    private const int GridLeftOffset = 170;
    private const int LeftPanelWidth = 152;
    private const int RightPanelMinWidth = 150;
    private const int PanelGap = 18;
    private const int GridTopOffset = 138;
    private const int PreviewSlotSize = 36;
    private const int InventoryCell = 52;
    private const int InventorySlotSize = 48;
    private const int InventorySlotCount = 36;
    private const int ViewRefreshTicks = 5;
    private static readonly Rectangle PanelSource = new(0, 256, 60, 60);

    private readonly SObject machine;
    private readonly GameLocation location;
    private readonly Vector2 tile;
    private readonly SingleBlockFarmService service;
    private readonly ClickableComponent prevButton;
    private readonly ClickableComponent nextButton;
    private readonly ClickableComponent autoInButton;
    private readonly ClickableComponent autoOutButton;
    private readonly ClickableComponent inputModeButton;
    private readonly ClickableComponent filterModeButton;
    private readonly ClickableComponent addFilterButton;
    private readonly ClickableComponent clearFilterButton;
    private readonly ClickableComponent collectAllButton;
    private readonly ClickableComponent uprootModeButton;
    private readonly ClickableComponent clearPlotsButton;
    private readonly int columns;
    private readonly int rows;
    private readonly int pageSize;
    private readonly SvsapmeItemIconCache itemIconCache = new();
    private IReadOnlyList<FarmPlotView> cachedViews = Array.Empty<FarmPlotView>();
    private FarmDashboardView cachedDashboard = new(false, false, false, MachineInputModes.AllEligible, MachineFilterModes.Whitelist, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<string>(), 0m, 0L, null, null, null);
    private int cachedAtTick = -1;
    private int page;
    private bool uprootMode;
    private string? hoverText;

    private Rectangle inventoryArea;
    private int backpackColumns;

    public SingleBlockFarmMenu(SObject machine, GameLocation location, Vector2 tile, SingleBlockFarmService service)
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
        this.prevButton = new ClickableComponent(new Rectangle(this.GridBounds.X, buttonY, 104, 40), "prev", "<");
        this.nextButton = new ClickableComponent(new Rectangle(this.GridBounds.X + 116, buttonY, 104, 40), "next", ">");
        var left = this.LeftPanelBounds;
        var controls = SvsapmeUiText.CalculateControlButtonBounds(left, 92, 5);
        this.autoInButton = new ClickableComponent(controls[0], "autoIn", ModText.Get("ui.processor.autoIn", "Auto In"));
        this.inputModeButton = new ClickableComponent(controls[1], "inputMode", ModText.Get("ui.processor.inputMode", "Input Mode"));
        this.filterModeButton = new ClickableComponent(controls[2], "filterMode", ModText.Get("ui.processor.filterMode", "W/B"));
        this.addFilterButton = new ClickableComponent(controls[3], "addFilter", ModText.Get("ui.processor.addFilter", "+ Held"));
        this.clearFilterButton = new ClickableComponent(controls[4], "clearFilter", ModText.Get("ui.processor.clearFilter", "Clear"));
        var right = this.RightPanelBounds;
        var rightButtonWidth = Math.Max(1, (right.Width - 24) / 2);
        this.autoOutButton = new ClickableComponent(new Rectangle(right.X + 10, right.Y + 96, rightButtonWidth, 30), "autoOut", ModText.Get("ui.processor.autoOut", "Auto Out"));
        this.collectAllButton = new ClickableComponent(new Rectangle(this.autoOutButton.bounds.Right + 4, right.Y + 96, rightButtonWidth, 30), "collect", ModText.Get("ui.processor.collectAll", "Collect All"));
        this.uprootModeButton = new ClickableComponent(new Rectangle(right.X + 10, right.Y + 132, rightButtonWidth, 30), "uprootMode", ModText.Get("ui.farm.uprootMode", "Uproot"));
        this.clearPlotsButton = new ClickableComponent(new Rectangle(this.uprootModeButton.bounds.Right + 4, right.Y + 132, rightButtonWidth, 30), "clearPlots", ModText.Get("ui.farm.clearAll", "Clear All"));
        if (this.upperRightCloseButton is not null)
        {
            this.upperRightCloseButton.bounds.X = this.xPositionOnScreen + this.width - 62;
            this.upperRightCloseButton.bounds.Y = this.yPositionOnScreen + 14;
        }
    }

    private static int GetMenuWidth() => Math.Min(930, Game1.uiViewport.Width - 48);
    private static int GetMenuHeight() => Math.Min(660, Game1.uiViewport.Height - 48);

    internal static FarmMenuLayoutShape CalculateLayoutShape(int menuWidth, int menuHeight, int inventorySlotCount = InventorySlotCount)
    {
        var availableGridWidth = menuWidth - Pad * 2 - LeftPanelWidth - RightPanelMinWidth - PanelGap * 2;
        var columns = Math.Clamp(availableGridWidth / SlotSize, 1, MaxColumns);

        var backpackColumns = Math.Clamp((menuWidth - Pad * 2) / InventoryCell, 4, 12);
        var backpackRows = Math.Max(3, (int)Math.Ceiling(Math.Max(1, inventorySlotCount) / (double)backpackColumns));
        var buttonY = menuHeight - Pad - backpackRows * InventoryCell - 50;
        var availableGridHeight = buttonY - GridTopOffset - 12;
        var rows = Math.Clamp(availableGridHeight / SlotSize, 1, MaxRows);

        return new FarmMenuLayoutShape(columns, rows, Math.Max(1, columns * rows));
    }

    internal static bool LayoutFits(int menuWidth, int menuHeight, int inventorySlotCount = InventorySlotCount)
    {
        var layout = CalculateLayoutShape(menuWidth, menuHeight, inventorySlotCount);
        var requiredWidth = Pad * 2 + LeftPanelWidth + RightPanelMinWidth + PanelGap * 2 + layout.Columns * SlotSize;
        var gridBottom = GridTopOffset + layout.Rows * SlotSize;
        var backpackColumns = Math.Clamp((menuWidth - Pad * 2) / InventoryCell, 4, 12);
        var backpackRows = Math.Max(3, (int)Math.Ceiling(Math.Max(1, inventorySlotCount) / (double)backpackColumns));
        var buttonY = menuHeight - Pad - backpackRows * InventoryCell - 50;
        var panelHeight = Math.Max(layout.Rows * SlotSize, buttonY - 8 - GridTopOffset);
        var panel = new Rectangle(Pad, GridTopOffset, LeftPanelWidth, panelHeight);
        var controls = SvsapmeUiText.CalculateControlButtonBounds(panel, 92, 5);
        return requiredWidth <= menuWidth
            && gridBottom + 12 <= buttonY
            && controls.All(control => control.X >= panel.X
                && control.Y >= panel.Y
                && control.Right <= panel.Right
                && control.Bottom <= panel.Bottom);
    }

    private Rectangle GridBounds => new(this.xPositionOnScreen + Pad + GridLeftOffset, this.yPositionOnScreen + GridTopOffset, this.columns * SlotSize, this.rows * SlotSize);
    private Rectangle LeftPanelBounds => new(this.xPositionOnScreen + Pad, this.yPositionOnScreen + GridTopOffset, LeftPanelWidth, Math.Max(this.rows * SlotSize, this.inventoryArea.Y - 58 - (this.yPositionOnScreen + GridTopOffset)));
    private Rectangle RightPanelBounds => new(this.GridBounds.Right + PanelGap, this.yPositionOnScreen + GridTopOffset, Math.Max(1, this.xPositionOnScreen + this.width - Pad - this.GridBounds.Right - PanelGap), Math.Max(this.rows * SlotSize, this.inventoryArea.Y - 58 - (this.yPositionOnScreen + GridTopOffset)));

    private Rectangle SeedInputSlotBounds => new(this.LeftPanelBounds.X + 14, this.LeftPanelBounds.Y + 48, PreviewSlotSize, PreviewSlotSize);
    private Rectangle FertilizerInputSlotBounds => new(this.SeedInputSlotBounds.Right + 10, this.SeedInputSlotBounds.Y, PreviewSlotSize, PreviewSlotSize);
    private Rectangle OutputSlotBounds => new(this.RightPanelBounds.X + 14, this.RightPanelBounds.Y + 48, PreviewSlotSize, PreviewSlotSize);

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

        if (this.autoInButton.containsPoint(x, y)) { this.Show(this.service.ToggleFarmAutoPull(this.machine, this.location, this.tile)); return; }
        if (this.autoOutButton.containsPoint(x, y)) { this.Show(this.service.ToggleFarmAutoPush(this.machine, this.location, this.tile)); return; }
        if (this.inputModeButton.containsPoint(x, y)) { this.Show(this.service.ToggleFarmInputMode(this.machine, this.location, this.tile)); return; }
        if (this.filterModeButton.containsPoint(x, y)) { this.Show(this.service.ToggleFarmFilterMode(this.machine, this.location, this.tile)); return; }
        if (this.addFilterButton.containsPoint(x, y)) { this.Show(this.service.AddHeldFarmFilter(this.machine, this.location, this.tile, Game1.player.CurrentItem)); return; }
        if (this.clearFilterButton.containsPoint(x, y)) { this.Show(this.service.ClearFarmFilter(this.machine, this.location, this.tile)); return; }
        if (this.collectAllButton.containsPoint(x, y)) { this.ShowAndDeliver(this.service.TryCollectFarmOutput(this.machine, this.location, this.tile)); return; }
        if (this.uprootModeButton.containsPoint(x, y))
        {
            this.uprootMode = !this.uprootMode;
            Game1.playSound("shwip");
            return;
        }
        if (this.clearPlotsButton.containsPoint(x, y))
        {
            this.OpenClearAllConfirmation();
            return;
        }

        if (this.SeedInputSlotBounds.Contains(x, y))
        {
            var held = Game1.player.CurrentItem;
            var result = held is null
                ? this.service.TryExtractFarmInput(this.machine, this.location, this.tile, fertilizer: false)
                : this.service.TryLoadSeed(this.machine, this.location, this.tile, held.QualifiedItemId, Game1.player.FarmingLevel);
            this.ShowAndDeliver(result);
            if (result.Success && result.ConsumeEscrowedItem && held is not null)
                ConsumeCurrentItem(1);
            return;
        }

        if (this.FertilizerInputSlotBounds.Contains(x, y))
        {
            var held = Game1.player.CurrentItem;
            var result = held is null
                ? this.service.TryExtractFarmInput(this.machine, this.location, this.tile, fertilizer: true)
                : this.service.TryLoadFertilizer(this.machine, this.location, this.tile, held.QualifiedItemId);
            this.ShowAndDeliver(result);
            if (result.Success && result.ConsumeEscrowedItem && held is not null)
                ConsumeCurrentItem(1);
            return;
        }

        if (this.OutputSlotBounds.Contains(x, y))
        {
            this.ShowAndDeliver(this.service.TryCollectFarmOutput(this.machine, this.location, this.tile));
            return;
        }

        // Check if player clicked an inventory item slot
        var inventoryIndex = this.HitInventorySlot(x, y);
        if (inventoryIndex >= 0)
        {
            var item = Game1.player.Items[inventoryIndex];
            if (item is not null)
            {
                // We'll let the user click the inventory items to seed/fertilize.
                // Left-click with seeds/fertilizers will feed the farm just like standard world clicks.
                // SvsapmeMachineActionRequest for LoadFarmSeed / LoadFarmFertilizer is supported.
                // First, check what type of item it is:
                var seedType = FarmCropCatalog.TryGetBySeed(item.QualifiedItemId, out _);
                var fertType = FarmModuleRules.IsFertilizer(item.QualifiedItemId);
                var moduleType = FarmModuleRules.TryGetModule(item.QualifiedItemId, out _);

                if (seedType || fertType || moduleType)
                {
                    if (!StardewModdingAPI.Context.IsMainPlayer)
                    {
                        // Farmhand escrow
                        var oldToolIndex = Game1.player.CurrentToolIndex;
                        Game1.player.CurrentToolIndex = inventoryIndex;
                        try
                        {
                            var kind = seedType ? SvsapmeMachineActionKind.LoadFarmSeed :
                                       (fertType ? SvsapmeMachineActionKind.LoadFarmFertilizer : SvsapmeMachineActionKind.InstallFarmModule);
                            var bulk = Game1.oldKBState.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift)
                                || Game1.oldKBState.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.RightShift);
                            var transferCount = moduleType ? 1 : bulk ? item.Stack : 1;
                            if (this.service.TrySendClientAction(this.machine, kind, item.QualifiedItemId, Game1.player.FarmingLevel, transferCount))
                            {
                                Game1.playSound("Ship");
                                this.InvalidateCache();
                            }
                        }
                        finally
                        {
                            Game1.player.CurrentToolIndex = oldToolIndex;
                        }
                    }
                    else
                    {
                        var bulk = Game1.oldKBState.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift)
                            || Game1.oldKBState.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.RightShift);
                        var transferCount = moduleType ? 1 : bulk ? item.Stack : 1;
                        SvsapmeMachineActionApplyResult result;
                        if (seedType)
                            result = this.service.TryLoadSeed(this.machine, this.location, this.tile, item.QualifiedItemId, Game1.player.FarmingLevel, transferCount);
                        else if (fertType)
                            result = this.service.TryLoadFertilizer(this.machine, this.location, this.tile, item.QualifiedItemId, transferCount);
                        else
                            result = this.service.TryInstallModule(this.machine, this.location, this.tile, item.QualifiedItemId);

                        if (result.Success)
                        {
                            if (item.Stack <= transferCount)
                                Game1.player.Items[inventoryIndex] = null;
                            else
                                item.Stack -= transferCount;

                            Game1.addHUDMessage(new HUDMessage(result.Message, HUDMessage.newQuest_type));
                            Game1.playSound("Ship");
                            this.InvalidateCache();
                        }
                        else
                        {
                            Game1.addHUDMessage(new HUDMessage(result.Message, HUDMessage.error_type));
                            Game1.playSound("cancel");
                        }
                    }
                }
            }
            return;
        }

        var plotIndex = this.GetSlotIndexAt(x, y);
        if (plotIndex >= 0 && plotIndex < views.Count)
        {
            var view = views[plotIndex];
            if (this.uprootMode)
            {
                if (string.IsNullOrWhiteSpace(view.SeedQualifiedItemId))
                {
                    Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.farm.plotEmpty", "That farm plot is empty."), HUDMessage.error_type));
                    Game1.playSound("cancel");
                }
                else
                {
                    this.OpenUprootConfirmation(view);
                }
                return;
            }

            if (view.Ready)
            {
                this.ShowAndDeliver(this.service.TryHarvestPlot(this.machine, this.location, this.tile, view.PlotIndex));
                return;
            }

            var held = Game1.player.CurrentItem;
            if (held is not null && FarmCropCatalog.TryGetBySeed(held.QualifiedItemId, out _))
            {
                if (StardewModdingAPI.Context.IsMainPlayer)
                {
                    var result = this.service.TryManualPlant(this.machine, this.location, this.tile, view.PlotIndex, held, Game1.player.FarmingLevel);
                    if (result.Success)
                    {
                        if (held.Stack <= 1)
                            Game1.player.Items[Game1.player.CurrentToolIndex] = null;
                        else
                            held.Stack -= 1;

                        Game1.addHUDMessage(new HUDMessage(result.Message, HUDMessage.newQuest_type));
                        Game1.playSound("dirtySheet");
                        this.InvalidateCache();
                    }
                    else
                    {
                        Game1.addHUDMessage(new HUDMessage(result.Message, HUDMessage.error_type));
                        Game1.playSound("cancel");
                    }
                }
            }
            return;
        }

        // Check if player clicked a module slot to uninstall
        var moduleIndex = this.GetModuleIndexAt(x, y);
        if (moduleIndex >= 0)
        {
            if (StardewModdingAPI.Context.IsMainPlayer)
            {
                var result = this.service.TryRemoveModule(this.machine, this.location, this.tile, moduleIndex);
                if (result.Success)
                {
                    DeliverReturnedItems(result.ReturnedItems);
                    Game1.addHUDMessage(new HUDMessage(result.Message, HUDMessage.newQuest_type));
                    Game1.playSound("coin");
                    this.InvalidateCache();
                }
                else
                {
                    Game1.addHUDMessage(new HUDMessage(result.Message, HUDMessage.error_type));
                    Game1.playSound("cancel");
                }
            }
            return;
        }
    }

    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        if (!StardewModdingAPI.Context.IsMainPlayer)
        {
            Game1.playSound("cancel");
            return;
        }

        var views = this.GetCachedViews();
        var viewIndex = this.GetSlotIndexAt(x, y);
        if (viewIndex < 0 || viewIndex >= views.Count)
            return;

        var heldSeed = Game1.player.CurrentItem is Item held && FarmCropCatalog.TryGetBySeed(held.QualifiedItemId, out _)
            ? held.QualifiedItemId
            : string.Empty;
        this.Show(this.service.ToggleFarmPlotLock(this.machine, this.location, this.tile, views[viewIndex].PlotIndex, heldSeed));
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
        var pages = this.GetMaxPage(views.Count) + 1;
        SvsapmeUiText.DrawFittedLine(
            b,
            ModText.Get(
                "ui.farm.gridSummary",
                "Plots {{occupied}}/{{capacity}} | Page {{page}}/{{pages}}",
                new
                {
                    occupied = dashboard.OccupiedPlots.ToString("N0"),
                    capacity = (dashboard.OccupiedPlots + dashboard.EmptyPlots).ToString("N0"),
                    page = (this.page + 1).ToString("N0"),
                    pages = pages.ToString("N0")
                }),
            new Rectangle(this.GridBounds.X, this.yPositionOnScreen + SvsapmeUiText.ContentTopOffset, this.GridBounds.Width, 24),
            Game1.textColor);
        this.DrawModuleStrip(b, dashboard);
        this.DrawSidePanels(b, dashboard);
        this.DrawGrid(b, views);
        this.DrawBottomSummary(b, dashboard);

        // Draw Separator line before inventory
        b.Draw(Game1.staminaRect, new Rectangle(this.xPositionOnScreen + Pad, this.inventoryArea.Y - 12, this.width - Pad * 2, 2), Color.SaddleBrown * 0.45f);

        // Draw Player Inventory
        this.DrawInventory(b);

        DrawButton(b, this.prevButton, this.page > 0);
        DrawButton(b, this.nextButton, this.page < this.GetMaxPage(views.Count));
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
        var views = this.GetCachedViews();
        var index = this.GetSlotIndexAt(x, y);
        if (index >= 0 && index < views.Count)
            this.hoverText = FormatPlotTooltip(views[index]);
        else
        {
            var moduleIndex = this.GetModuleIndexAt(x, y);
            if (moduleIndex >= 0)
                this.hoverText = moduleIndex < this.GetCachedDashboard().ModuleQualifiedItemIds.Count
                    ? FormatItem(this.GetCachedDashboard().ModuleQualifiedItemIds[moduleIndex])
                    : ModText.Get("ui.farm.moduleSlot.empty", "Empty module slot");
        }
    }

    private IReadOnlyList<FarmPlotView> GetCachedViews()
    {
        this.RefreshCacheIfNeeded();
        return this.cachedViews;
    }

    private FarmDashboardView GetCachedDashboard()
    {
        this.RefreshCacheIfNeeded();
        return this.cachedDashboard;
    }

    private void RefreshCacheIfNeeded()
    {
        var tick = Game1.ticks;
        if (this.cachedAtTick >= 0 && tick >= this.cachedAtTick && tick - this.cachedAtTick < ViewRefreshTicks)
            return;

        this.cachedViews = this.service.GetPlotViews(this.machine, this.location, this.tile);
        this.cachedDashboard = this.service.GetDashboard(this.machine, this.location, this.tile);
        this.cachedAtTick = tick;
    }

    private void InvalidateCache()
    {
        this.cachedAtTick = -1;
        this.itemIconCache.Clear();
    }

    private void DrawSidePanels(SpriteBatch b, FarmDashboardView dashboard)
    {
        DrawPanel(b, this.LeftPanelBounds, Color.White * 0.92f, true);
        DrawPanel(b, this.RightPanelBounds, Color.White * 0.92f, true);
        SvsapmeUiText.DrawFittedLine(b, ModText.Get("ui.processor.inputTitle", "Input"), new Rectangle(this.LeftPanelBounds.X + 12, this.LeftPanelBounds.Y + 12, this.LeftPanelBounds.Width - 24, 28), Game1.textColor);
        SvsapmeUiText.DrawFittedLine(b, ModText.Get("ui.processor.outputTitle", "Output"), new Rectangle(this.RightPanelBounds.X + 12, this.RightPanelBounds.Y + 12, this.RightPanelBounds.Width - 24, 28), Game1.textColor);
        this.DrawFarmInputPreview(b, dashboard);
        this.DrawFarmOutputPreview(b, dashboard);
        DrawButton(b, this.autoInButton, true);
        DrawButton(b, this.inputModeButton, true);
        DrawButton(b, this.filterModeButton, true);
        DrawButton(b, this.addFilterButton, Game1.player.CurrentItem is not null);
        DrawButton(b, this.clearFilterButton, dashboard.FilterCount > 0);
        DrawButton(b, this.autoOutButton, true);
        DrawButton(b, this.collectAllButton, dashboard.OutputBufferStacks > 0);
        DrawButton(b, this.uprootModeButton, dashboard.OccupiedPlots > 0, this.uprootMode ? Color.LightGreen : null);
        DrawButton(b, this.clearPlotsButton, dashboard.OccupiedPlots > 0 || dashboard.LockedPlots > 0);
        this.DrawStatusLines(
            b,
            new[]
            {
                SvsapmeUiText.FormatAuto(dashboard.AutoPullFromNetwork),
                SvsapmeUiText.FormatInputMode(dashboard.InputMode),
                SvsapmeUiText.FormatFilterMode(dashboard.FilterMode),
                SvsapmeUiText.FormatFilterCount(dashboard.FilterCount),
                SvsapmeUiText.FormatInputBufferCount(dashboard.InputBufferStacks),
                SvsapmeUiText.FormatFertilizerCount(dashboard.FertilizerCount)
            },
            new Rectangle(this.LeftPanelBounds.X + 12, this.clearFilterButton.bounds.Bottom + 10, this.LeftPanelBounds.Width - 24, this.LeftPanelBounds.Bottom - this.clearFilterButton.bounds.Bottom - 18));
        this.DrawStatusLines(
            b,
            new[]
            {
                SvsapmeUiText.FormatAuto(dashboard.AutoPushOutputToNetwork),
                SvsapmeUiText.FormatBufferCount(dashboard.OutputBufferStacks),
                SvsapmeUiText.FormatDayValue(dashboard.EstimatedDailyValue)
            },
            new Rectangle(this.RightPanelBounds.X + 12, this.clearPlotsButton.bounds.Bottom + 10, this.RightPanelBounds.Width - 24, this.RightPanelBounds.Bottom - this.clearPlotsButton.bounds.Bottom - 18));
    }

    private void DrawFarmInputPreview(SpriteBatch b, FarmDashboardView dashboard)
    {
        var seedSlot = this.SeedInputSlotBounds;
        var fertilizerSlot = this.FertilizerInputSlotBounds;
        DrawPanel(b, seedSlot, dashboard.InputBufferStacks > 0 ? Color.White : Color.Gray * 0.55f, true);
        DrawPanel(b, fertilizerSlot, dashboard.FertilizerCount > 0 ? Color.White : Color.Gray * 0.55f, true);
        SvsapmeUiText.DrawItemWithAdaptiveCount(b, this.itemIconCache.GetOrCreate(dashboard.InputPreview), seedSlot, dashboard.InputPreview?.Stack ?? 0, 0.48f);
        SvsapmeUiText.DrawItemWithAdaptiveCount(b, this.itemIconCache.GetOrCreate(dashboard.FertilizerPreview), fertilizerSlot, dashboard.FertilizerPreview?.Stack ?? 0, 0.48f);
        if (dashboard.InputPreview is null)
            SvsapmeUiText.DrawFittedLine(b, ModText.Get("ui.farm.inputSlot.seed", "Seed"), new Rectangle(seedSlot.X + 2, seedSlot.Y + 8, seedSlot.Width - 4, 18), Game1.textColor, 0.5f);
        if (dashboard.FertilizerPreview is null)
            SvsapmeUiText.DrawFittedLine(b, ModText.Get("ui.farm.inputSlot.fertilizer", "Fert"), new Rectangle(fertilizerSlot.X + 2, fertilizerSlot.Y + 8, fertilizerSlot.Width - 4, 18), Game1.textColor, 0.5f);
    }

    private void DrawFarmOutputPreview(SpriteBatch b, FarmDashboardView dashboard)
    {
        var outputSlot = this.OutputSlotBounds;
        DrawPanel(b, outputSlot, dashboard.OutputBufferStacks > 0 ? Color.White : Color.Gray * 0.55f, true);
        SvsapmeUiText.DrawItemWithAdaptiveCount(b, this.itemIconCache.GetOrCreate(dashboard.OutputPreview), outputSlot, dashboard.OutputPreview?.Stack ?? 0, 0.48f);
        if (dashboard.OutputPreview is null)
            SvsapmeUiText.DrawFittedLine(b, ModText.Get("ui.farm.outputSlot.buffer", "Out"), new Rectangle(outputSlot.X + 2, outputSlot.Y + 8, outputSlot.Width - 4, 18), Game1.textColor, 0.5f);
    }

    private void DrawModuleStrip(SpriteBatch b, FarmDashboardView dashboard)
    {
        if (dashboard.ModuleSlotsCapacity <= 0)
            return;

        var y = this.GridBounds.Y - 40;
        var labelBounds = new Rectangle(this.GridBounds.X, y + 6, 88, 24);
        SvsapmeUiText.DrawFittedLine(
            b,
            ModText.Get("ui.farm.moduleTitle", "Modules {{used}}/{{capacity}}", new { used = dashboard.ModuleSlotsUsed.ToString("N0"), capacity = dashboard.ModuleSlotsCapacity.ToString("N0") }),
            labelBounds,
            Game1.textColor,
            0.56f);

        for (var i = 0; i < dashboard.ModuleSlotsCapacity; i++)
        {
            var slot = this.GetModuleSlotBounds(i);
            DrawPanel(b, slot, i < dashboard.ModuleQualifiedItemIds.Count ? Color.White : Color.Gray * 0.55f, true);
            if (i < dashboard.ModuleQualifiedItemIds.Count)
                SvsapmeUiText.DrawItemWithAdaptiveCount(b, this.itemIconCache.GetOrCreate(dashboard.ModuleQualifiedItemIds[i]), slot, 1, 0.5f);
            else
                SvsapmeUiText.DrawGhostUpgradeSlot(b, slot);
        }
    }

    private void DrawBottomSummary(SpriteBatch b, FarmDashboardView dashboard)
    {
        var top = this.GridBounds.Bottom + 8;
        var height = Math.Min(36, this.prevButton.bounds.Y - top - 6);
        if (height < 20)
            return;

        var bounds = new Rectangle(this.GridBounds.X, top, this.GridBounds.Width, height);
        DrawPanel(b, bounds, Color.White * 0.68f, false);
        SvsapmeUiText.DrawFittedLine(
            b,
            ModText.Get(
                "ui.farm.bottomSummary",
                "Day value {{value}}g  Energy {{energy}} Wh/day  Input {{input}}  Output {{output}}",
                new
                {
                    value = dashboard.EstimatedDailyValue.ToString("0.##"),
                    energy = dashboard.EstimatedDailyEnergyWh.ToString("N0"),
                    input = dashboard.InputBufferStacks.ToString("N0"),
                    output = dashboard.OutputBufferStacks.ToString("N0")
                }),
            new Rectangle(bounds.X + 8, bounds.Y + 8, bounds.Width - 16, bounds.Height - 12),
            Game1.textColor,
            0.58f);
    }

    private void DrawGrid(SpriteBatch b, IReadOnlyList<FarmPlotView> views)
    {
        var grid = this.GridBounds;
        for (var i = 0; i < this.pageSize; i++)
        {
            var column = i % this.columns;
            var row = i / this.columns;
            var bounds = new Rectangle(grid.X + column * SlotSize, grid.Y + row * SlotSize, SlotSize, SlotSize);
            var index = this.page * this.pageSize + i;
            var hasSlot = index < views.Count;
            DrawPanel(b, bounds, hasSlot ? Color.White : Color.Gray * 0.55f, true);
            if (!hasSlot)
                continue;

            var view = views[index];
            if (!string.IsNullOrWhiteSpace(view.HarvestQualifiedItemId))
                SvsapmeUiText.DrawItemWithAdaptiveCount(b, this.itemIconCache.GetOrCreate(view.HarvestQualifiedItemId), bounds, 1, 0.68f);
            if (view.RequiredUnits > 0)
            {
                var done = Math.Clamp(view.ProgressUnits / (decimal)view.RequiredUnits, 0, 1);
                b.Draw(Game1.staminaRect, new Rectangle(bounds.X + 4, bounds.Y + 4, bounds.Width - 8, 4), Color.Black * 0.25f);
                b.Draw(Game1.staminaRect, new Rectangle(bounds.X + 4, bounds.Y + 4, (int)((bounds.Width - 8) * done), 4), view.Ready ? Color.LimeGreen : Color.Orange);
            }

            var status = PixelStatus.Idle;
            if (view.Ready)
                status = PixelStatus.Ready;
            else if (view.RequiredUnits > 0)
                status = PixelStatus.Processing;

            SvsapmeUiText.DrawPixelStatusLight(b, bounds.X + 4, bounds.Y + 12, status);
            SvsapmeUiText.DrawSlotStatusLine(b, bounds, status);
            if (view.IsLocked)
                SvsapmeUiText.DrawPixelLock(b, bounds.Right - 12, bounds.Y + 3);
        }
    }

    private void DrawStatusLines(SpriteBatch b, IEnumerable<string> lines, Rectangle bounds)
    {
        SvsapmeUiText.DrawFittedLines(b, lines, bounds, Game1.textColor);
    }

    private void Show(SvsapmeMachineActionApplyResult result)
    {
        this.InvalidateCache();
        Game1.addHUDMessage(new HUDMessage(result.Message, result.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));
        Game1.playSound(result.Success ? "smallSelect" : "cancel");
    }

    private void ShowAndDeliver(SvsapmeMachineActionApplyResult result)
    {
        if (result.Success)
            DeliverReturnedItems(result.ReturnedItems);
        this.Show(result);
    }

    private void OpenUprootConfirmation(FarmPlotView view)
    {
        var lines = new List<string>
        {
            ModText.Get(
                "ui.farm.uproot.confirmBody",
                "Remove plot {{plot}} and its {{crop}} crop?",
                new { plot = (view.PlotIndex + 1).ToString("N0"), crop = FormatItem(view.HarvestQualifiedItemId) }),
            ModText.Get("ui.farm.destructive.noRefund", "The crop, fertilizer, progress, and plot lock will not be returned.")
        };
        if (this.GetCachedDashboard().AutoPullFromNetwork)
            lines.Add(ModText.Get("ui.farm.destructive.autoPullWarning", "Automatic input is enabled, so this plot may be planted again during the next farm cycle."));

        Game1.activeClickableMenu = new SvsapmeConfirmationMenu(
            this,
            ModText.Get("ui.farm.uproot.confirmTitle", "Confirm Uproot"),
            lines,
            () => this.Show(this.service.TryUprootFarmPlot(this.machine, this.location, this.tile, view.PlotIndex)));
    }

    private void OpenClearAllConfirmation()
    {
        var dashboard = this.GetCachedDashboard();
        if (dashboard.OccupiedPlots <= 0 && dashboard.LockedPlots <= 0)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.farm.noPlotsToClear", "There are no planted or locked plots to clear."), HUDMessage.error_type));
            Game1.playSound("cancel");
            return;
        }

        var lines = new List<string>
        {
            ModText.Get(
                "ui.farm.clearAll.confirmBody",
                "Clear all {{count}} planted farm plots?",
                new { count = dashboard.OccupiedPlots.ToString("N0") }),
            ModText.Get("ui.farm.destructive.noRefund", "The crop, fertilizer, progress, and plot lock will not be returned.")
        };
        if (dashboard.LockedPlots > 0)
        {
            lines.Add(ModText.Get(
                "ui.farm.clearAll.lockCount",
                "Plot locks to clear: {{count}}.",
                new { count = dashboard.LockedPlots.ToString("N0") }));
        }
        if (dashboard.AutoPullFromNetwork)
            lines.Add(ModText.Get("ui.farm.destructive.autoPullWarning", "Automatic input is enabled, so empty plots may be planted again during the next farm cycle."));

        Game1.activeClickableMenu = new SvsapmeConfirmationMenu(
            this,
            ModText.Get("ui.farm.clearAll.confirmTitle", "Confirm Clear All"),
            lines,
            () => this.Show(this.service.TryClearFarmPlots(this.machine, this.location, this.tile)));
    }

    private static void DeliverReturnedItems(IEnumerable<BufferedItemStack> returnedItems)
    {
        foreach (var stack in returnedItems)
        {
            var item = BufferedItemCodec.CreateItem(stack);
            var remainder = Game1.player.addItemToInventory(item);
            if (remainder is not null)
                Game1.createItemDebris(remainder, Game1.player.Position, Game1.player.FacingDirection, Game1.currentLocation);
        }
    }

    private static void ConsumeCurrentItem(int count)
    {
        var index = Game1.player.CurrentToolIndex;
        var item = Game1.player.CurrentItem;
        if (item is null)
            return;
        item.Stack -= Math.Max(1, count);
        if (item.Stack <= 0 && index >= 0 && index < Game1.player.Items.Count)
            Game1.player.Items[index] = null;
    }

    private int GetMaxPage(int count)
    {
        return Math.Max(0, (Math.Max(1, count) - 1) / this.pageSize);
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

    private Rectangle GetModuleSlotBounds(int index)
    {
        return new Rectangle(this.GridBounds.X + 94 + index * (PreviewSlotSize + 6), this.GridBounds.Y - 40, PreviewSlotSize, PreviewSlotSize);
    }

    private int GetModuleIndexAt(int x, int y)
    {
        var dashboard = this.GetCachedDashboard();
        for (var i = 0; i < dashboard.ModuleSlotsCapacity; i++)
        {
            if (this.GetModuleSlotBounds(i).Contains(x, y))
                return i;
        }

        return -1;
    }

    private static string FormatPlotTooltip(FarmPlotView view)
    {
        var seed = string.IsNullOrWhiteSpace(view.SeedQualifiedItemId)
            ? ModText.Get("ui.processor.slot.empty", "Empty")
            : FormatItem(view.SeedQualifiedItemId);
        var harvest = string.IsNullOrWhiteSpace(view.HarvestQualifiedItemId)
            ? ModText.Get("ui.processor.slot.empty", "Empty")
            : FormatItem(view.HarvestQualifiedItemId);
        var fertilizer = string.IsNullOrWhiteSpace(view.FertilizerQualifiedItemId)
            ? ModText.Get("ui.farm.fertilizerNone", "Fertilizer: none bound")
            : FormatItem(view.FertilizerQualifiedItemId);
        var progress = view.RequiredUnits > 0
            ? $"{Math.Min(view.ProgressUnits, view.RequiredUnits):N0}/{view.RequiredUnits:N0}"
            : "0/0";
        return ModText.Get(
            "ui.farm.tooltip.plot",
            "Seed: {{seed}}\nHarvest: {{harvest}}\nETA: {{eta}}\nProgress: {{progress}}\nFertilizer: {{fertilizer}}\nLock: {{lock}}",
            new
            {
                seed,
                harvest,
                eta = view.Eta,
                progress,
                fertilizer,
                @lock = view.IsLocked ? FormatItem(view.LockedSeedQualifiedItemId) : ModText.Get("ui.farm.unlocked", "Unlocked")
            });
    }

    private static string FormatItem(string qualifiedItemId)
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

    private static void DrawPanel(SpriteBatch b, Rectangle panel, Color tint, bool drawShadow)
    {
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, PanelSource, panel.X, panel.Y, panel.Width, panel.Height, tint, 1f, drawShadow);
    }

    private static void DrawButton(SpriteBatch b, ClickableComponent button, bool enabled, Color? tint = null)
    {
        DrawPanel(b, button.bounds, enabled ? tint ?? Color.White : Color.Gray * 0.7f, false);
        var color = enabled ? Game1.textColor : Color.DimGray;
        SvsapmeUiText.DrawFittedLine(b, button.label, new Rectangle(button.bounds.X + 8, button.bounds.Y + 4, button.bounds.Width - 16, button.bounds.Height - 8), color);
    }

}

internal readonly record struct FarmMenuLayoutShape(int Columns, int Rows, int PageSize);
