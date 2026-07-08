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
    private const int Pad = 24;
    private const int SlotSize = 64;
    private const int MaxColumns = 8;
    private const int MaxRows = 8;
    private const int GridLeftOffset = 178;
    private const int LeftPanelWidth = 160;
    private const int RightPanelMinWidth = 170;
    private const int PanelGap = 18;
    private const int GridTopOffset = 96;
    private const int BottomButtonOffset = 66;
    private const int ViewRefreshTicks = 5;
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
    private IReadOnlyList<ProcessorSlotView> cachedViews = Array.Empty<ProcessorSlotView>();
    private ProcessorDashboardView cachedDashboard = new(false, false, false, MachineInputModes.AllEligible, MachineFilterModes.Whitelist, 0, 0, 0, 0, 0, 0, 0m);
    private int cachedAtTick = -1;
    private int page;

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
        var layout = CalculateLayoutShape(this.width, this.height);
        this.columns = layout.Columns;
        this.rows = layout.Rows;
        this.pageSize = layout.PageSize;
        var buttonY = this.yPositionOnScreen + this.height - 66;
        this.prevButton = new ClickableComponent(new Rectangle(this.GridBounds.X, buttonY, 110, 42), "prev", "<");
        this.nextButton = new ClickableComponent(new Rectangle(this.GridBounds.X + 122, buttonY, 110, 42), "next", ">");
        this.collectAllButton = new ClickableComponent(new Rectangle(this.xPositionOnScreen + this.width - Pad - 170, buttonY, 170, 42), "collect", ModText.Get("ui.processor.collectAll", "Collect All"));
        var left = this.LeftPanelBounds;
        this.autoInButton = new ClickableComponent(new Rectangle(left.X + 10, left.Y + 54, left.Width - 20, 34), "autoIn", ModText.Get("ui.processor.autoIn", "Auto In"));
        this.inputModeButton = new ClickableComponent(new Rectangle(left.X + 10, left.Y + 96, left.Width - 20, 34), "inputMode", ModText.Get("ui.processor.inputMode", "Input Mode"));
        this.filterModeButton = new ClickableComponent(new Rectangle(left.X + 10, left.Y + 138, left.Width - 20, 34), "filterMode", ModText.Get("ui.processor.filterMode", "W/B"));
        this.addFilterButton = new ClickableComponent(new Rectangle(left.X + 10, left.Y + 180, left.Width - 20, 34), "addFilter", ModText.Get("ui.processor.addFilter", "+ Filter"));
        this.clearFilterButton = new ClickableComponent(new Rectangle(left.X + 10, left.Y + 222, left.Width - 20, 34), "clearFilter", ModText.Get("ui.processor.clearFilter", "Clear"));
        var right = this.RightPanelBounds;
        this.autoOutButton = new ClickableComponent(new Rectangle(right.X + 10, right.Y + 54, right.Width - 20, 34), "autoOut", ModText.Get("ui.processor.autoOut", "Auto Out"));
        if (this.upperRightCloseButton is not null)
        {
            this.upperRightCloseButton.bounds.X = this.xPositionOnScreen + this.width - 62;
            this.upperRightCloseButton.bounds.Y = this.yPositionOnScreen + 14;
        }
    }

    private static int GetMenuWidth() => Math.Min(980, Game1.uiViewport.Width - 48);

    private static int GetMenuHeight() => Math.Min(700, Game1.uiViewport.Height - 48);

    internal static ProcessorMenuLayoutShape CalculateLayoutShape(int menuWidth, int menuHeight)
    {
        var availableGridWidth = menuWidth - Pad * 2 - LeftPanelWidth - RightPanelMinWidth - PanelGap * 2;
        var columns = Math.Clamp(availableGridWidth / SlotSize, 1, MaxColumns);

        var buttonY = menuHeight - BottomButtonOffset;
        var availableGridHeight = buttonY - GridTopOffset - 12;
        var rows = Math.Clamp(availableGridHeight / SlotSize, 1, MaxRows);

        return new ProcessorMenuLayoutShape(columns, rows, Math.Max(1, columns * rows));
    }

    internal static bool LayoutFits(int menuWidth, int menuHeight)
    {
        var layout = CalculateLayoutShape(menuWidth, menuHeight);
        var requiredWidth = Pad * 2 + LeftPanelWidth + RightPanelMinWidth + PanelGap * 2 + layout.Columns * SlotSize;
        var gridBottom = GridTopOffset + layout.Rows * SlotSize;
        var buttonY = menuHeight - BottomButtonOffset;
        return requiredWidth <= menuWidth && gridBottom + 12 <= buttonY;
    }

    private Rectangle GridBounds => new(
        this.xPositionOnScreen + Pad + GridLeftOffset,
        this.yPositionOnScreen + GridTopOffset,
        this.columns * SlotSize,
        this.rows * SlotSize);

    private Rectangle LeftPanelBounds => new(
        this.xPositionOnScreen + Pad,
        this.yPositionOnScreen + GridTopOffset,
        LeftPanelWidth,
        this.rows * SlotSize);

    private Rectangle RightPanelBounds => new(
        this.GridBounds.Right + PanelGap,
        this.yPositionOnScreen + GridTopOffset,
        Math.Max(1, this.xPositionOnScreen + this.width - Pad - this.GridBounds.Right - PanelGap),
        this.rows * SlotSize);

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (this.upperRightCloseButton?.containsPoint(x, y) == true)
        {
            this.exitThisMenu();
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
            var result = this.service.TryCollectOutput(this.machine, this.location, this.tile, Game1.player, 0);
            Game1.addHUDMessage(new HUDMessage(result.Message, result.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));
            Game1.playSound(result.Success ? "coin" : "cancel");
            this.InvalidateCache();
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

        var index = this.GetSlotIndexAt(x, y);
        if (index < 0 || index >= views.Count)
            return;

        var view = views[index];
        if (view.Ready)
        {
            var result = this.service.TryCollectOutput(this.machine, this.location, this.tile, Game1.player, view.SlotIndex + 1);
            Game1.addHUDMessage(new HUDMessage(result.Message, result.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));
            Game1.playSound(result.Success ? "coin" : "cancel");
            this.InvalidateCache();
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
        DrawPanel(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height), Color.White);
        Utility.drawTextWithShadow(
            b,
            this.machine.DisplayName,
            Game1.dialogueFont,
            new Vector2(this.xPositionOnScreen + Pad, this.yPositionOnScreen + 26),
            Game1.textColor);

        var views = this.GetCachedViews();
        var dashboard = this.GetCachedDashboard();
        this.page = Math.Clamp(this.page, 0, this.GetMaxPage(views.Count));
        this.DrawSummary(b, views);
        this.DrawSidePanels(b, dashboard);
        this.DrawGrid(b, views);
        DrawButton(b, this.prevButton, this.page > 0);
        DrawButton(b, this.nextButton, this.page < this.GetMaxPage(views.Count));
        DrawButton(b, this.collectAllButton, views.Any(view => view.Ready));
        this.upperRightCloseButton?.draw(b);
        this.drawMouse(b);
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
    }

    private void DrawSidePanels(SpriteBatch b, ProcessorDashboardView dashboard)
    {
        DrawPanel(b, this.LeftPanelBounds, Color.White * 0.92f);
        DrawPanel(b, this.RightPanelBounds, Color.White * 0.92f);
        b.DrawString(Game1.smallFont, ModText.Get("ui.processor.inputTitle", "Input"), new Vector2(this.LeftPanelBounds.X + 12, this.LeftPanelBounds.Y + 18), Game1.textColor);
        b.DrawString(Game1.smallFont, ModText.Get("ui.processor.outputTitle", "Output"), new Vector2(this.RightPanelBounds.X + 12, this.RightPanelBounds.Y + 18), Game1.textColor);

        DrawButton(b, this.autoInButton, true);
        DrawButton(b, this.inputModeButton, true);
        DrawButton(b, this.filterModeButton, true);
        DrawButton(b, this.addFilterButton, Game1.player.CurrentItem is not null);
        DrawButton(b, this.clearFilterButton, dashboard.FilterCount > 0);
        DrawButton(b, this.autoOutButton, true);

        var inputLines = new[]
        {
            dashboard.AutoPullFromNetwork ? "ON" : "OFF",
            dashboard.InputMode,
            dashboard.FilterMode,
            $"Filter {dashboard.FilterCount:N0}",
            $"Buffer {dashboard.InputBufferStacks:N0}"
        };
        this.DrawTinyLines(b, inputLines, this.LeftPanelBounds.X + 12, this.LeftPanelBounds.Y + 270);

        var outputLines = new[]
        {
            dashboard.AutoPushOutputToNetwork ? "Auto ON" : "Auto OFF",
            $"Ready {dashboard.ReadySlots:N0}",
            $"Buffer {dashboard.OutputBufferStacks:N0}",
            $"Day {dashboard.EstimatedDailyValue:0}g"
        };
        this.DrawTinyLines(b, outputLines, this.RightPanelBounds.X + 12, this.RightPanelBounds.Y + 104);
    }

    private void DrawTinyLines(SpriteBatch b, IEnumerable<string> lines, int x, int y)
    {
        var currentY = y;
        foreach (var line in lines)
        {
            b.DrawString(Game1.tinyFont, line, new Vector2(x, currentY), Game1.textColor);
            currentY += 24;
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
        b.DrawString(Game1.smallFont, text, new Vector2(this.xPositionOnScreen + Pad, this.yPositionOnScreen + 70), Game1.textColor);
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
            {
                var item = BufferedItemCodec.CreateItem(stack);
                item.drawInMenu(b, new Vector2(bounds.X + 8, bounds.Y + 6), 0.75f);
            }

            if (view.Ready)
                b.Draw(Game1.staminaRect, new Rectangle(bounds.X + 4, bounds.Y + 4, bounds.Width - 8, 4), Color.LimeGreen * 0.9f);
            else if (view.Total > 0)
                this.DrawProgress(b, bounds, view);

            var eta = view.Eta;
            var size = Game1.tinyFont.MeasureString(eta);
            var scale = size.X > bounds.Width - 8 ? Math.Max(0.55f, (bounds.Width - 8) / size.X) : 1f;
            b.DrawString(
                Game1.tinyFont,
                eta,
                new Vector2(bounds.X + (bounds.Width - size.X * scale) / 2f, bounds.Bottom - 18),
                view.Ready ? Color.DarkGreen : Game1.textColor,
                0f,
                Vector2.Zero,
                scale,
                SpriteEffects.None,
                1f);
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

    private int GetSlotIndexAt(int x, int y)
    {
        var grid = this.GridBounds;
        if (!grid.Contains(x, y))
            return -1;

        var column = (x - grid.X) / SlotSize;
        var row = (y - grid.Y) / SlotSize;
        return this.page * this.pageSize + row * this.columns + column;
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
        var size = Game1.smallFont.MeasureString(button.label);
        var scale = size.X > button.bounds.Width - 18 ? Math.Max(0.62f, (button.bounds.Width - 18) / size.X) : 1f;
        b.DrawString(
            Game1.smallFont,
            button.label,
            new Vector2(button.bounds.X + (button.bounds.Width - size.X * scale) / 2f, button.bounds.Y + (button.bounds.Height - size.Y * scale) / 2f),
            color,
            0f,
            Vector2.Zero,
            scale,
            SpriteEffects.None,
            1f);
    }
}

internal readonly record struct ProcessorMenuLayoutShape(int Columns, int Rows, int PageSize);
