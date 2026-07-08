using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SVSAPME.Services;
using StardewValley;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace SVSAPME.UI;

internal sealed class PoweredTransferMenu : IClickableMenu
{
    private const int Pad = 28;
    private const int Cell = 64;
    private const int FilterColumns = 3;
    private const int FilterRows = 3;
    private const int ControlButtonWidth = 150;
    private const int ControlButtonHeight = 42;
    private const int ControlButtonGap = 10;
    private const int ControlRowGap = 12;
    private const int DirectionButtonMaxWidth = 80;
    private const int DirectionButtonMinWidth = 48;
    private const int DirectionButtonGap = 6;
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
    private Rectangle inventoryArea;
    private int backpackColumns;
    private int selectedSlot;

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

    private static int GetMenuWidth() => Math.Min(1040, Game1.uiViewport.Width - 80);

    private static int GetMenuHeight() => Math.Min(760, Game1.uiViewport.Height - 80);

    private void BuildLayout()
    {
        var innerX = this.xPositionOnScreen + Pad;
        var innerW = this.width - Pad * 2;
        var top = this.yPositionOnScreen + 24;

        this.filterArea = new Rectangle(innerX, top + 92, FilterColumns * Cell, FilterRows * Cell);
        var controlsX = this.filterArea.Right + 34;
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

        this.backpackColumns = Math.Clamp(innerW / Cell, 4, 12);
        var backpackRows = Math.Max(3, (int)Math.Ceiling(Game1.player.Items.Count / (double)this.backpackColumns));
        var invW = this.backpackColumns * Cell;
        var invH = backpackRows * Cell;
        this.inventoryArea = new Rectangle(
            innerX + Math.Max(0, (innerW - invW) / 2),
            this.yPositionOnScreen + this.height - Pad - invH,
            invW,
            invH);
    }

    internal static bool LayoutFits(int menuWidth)
    {
        var controlsX = Pad + FilterColumns * Cell + 34;
        var controlsAvailable = menuWidth - Pad - controlsX;
        var controlColumns = CalculateControlColumns(controlsAvailable);
        var controlWidth = CalculateControlButtonWidth(controlsAvailable, controlColumns);
        var directionWidth = CalculateDirectionButtonWidth(controlsAvailable);
        var filterRight = Pad + FilterColumns * Cell;
        var controlRight = controlsX + Math.Min(controlColumns, 3) * controlWidth + Math.Max(0, Math.Min(controlColumns, 3) - 1) * ControlButtonGap;
        var directionRight = controlsX + 5 * directionWidth + 4 * DirectionButtonGap;
        return filterRight <= menuWidth - Pad
            && controlRight <= menuWidth - Pad
            && directionRight <= menuWidth - Pad
            && controlColumns >= 1
            && directionWidth >= DirectionButtonMinWidth;
    }

    private static int CalculateControlColumns(int availableWidth)
    {
        if (availableWidth >= ControlButtonWidth * 3 + ControlButtonGap * 2)
            return 3;
        if (availableWidth >= ControlButtonWidth * 2 + ControlButtonGap)
            return 2;
        return 1;
    }

    private static int CalculateControlButtonWidth(int availableWidth, int columns)
    {
        var fitWidth = (availableWidth - Math.Max(0, columns - 1) * ControlButtonGap) / Math.Max(1, columns);
        return Math.Clamp(fitWidth, 72, ControlButtonWidth);
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
            this.RunAction(() => this.runtime.TryTogglePoweredFilterMode(this.machine, this.location, this.tile, out var message) ? message : message);
            return;
        }

        if (this.oreButton.containsPoint(x, y))
        {
            this.RunAction(() => this.runtime.TryTogglePoweredOreDictionaryMode(this.machine, this.location, this.tile, out var message) ? message : message);
            return;
        }

        if (this.qualityButton.containsPoint(x, y))
        {
            this.RunAction(() => this.runtime.TryTogglePoweredQualityStrategy(this.machine, this.location, this.tile, out var message) ? message : message);
            return;
        }

        if (this.clearButton.containsPoint(x, y))
        {
            this.RunAction(() => this.runtime.TryClearPoweredFilter(this.machine, this.location, this.tile, out var message) ? message : message);
            return;
        }

        foreach (var button in this.directionButtons)
        {
            if (!button.containsPoint(x, y) || !int.TryParse(button.name, out var direction))
                continue;

            this.RunAction(() => this.runtime.TrySetPoweredFacingDirection(this.machine, this.location, this.tile, direction, out var message) ? message : message);
            return;
        }

        var filterSlot = this.HitFilterSlot(x, y);
        if (filterSlot >= 0)
        {
            this.selectedSlot = filterSlot;
            var view = this.runtime.GetPoweredFilterSlotViews(this.machine).FirstOrDefault(slot => slot.SlotIndex == filterSlot);
            if (view?.Occupied == true)
                this.RunAction(() => this.runtime.TryClearPoweredFilterSlot(this.machine, this.location, this.tile, filterSlot, out var message) ? message : message);
            else
                Game1.playSound("smallSelect");
            return;
        }

        var inventoryIndex = this.HitInventorySlot(x, y);
        if (inventoryIndex >= 0)
            this.SetFilterFromInventory(inventoryIndex);
    }

    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        var filterSlot = this.HitFilterSlot(x, y);
        if (filterSlot >= 0)
        {
            this.RunAction(() => this.runtime.TryClearPoweredFilterSlot(this.machine, this.location, this.tile, filterSlot, out var message) ? message : message);
            return;
        }

        base.receiveRightClick(x, y, playSound);
    }

    public override void draw(SpriteBatch b)
    {
        DrawPanel(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
        Utility.drawTextWithShadow(b, this.machine.DisplayName, Game1.dialogueFont, new Vector2(this.xPositionOnScreen + Pad, this.yPositionOnScreen + 26), Game1.textColor);

        var slots = this.runtime.GetPoweredFilterSlotViews(this.machine);
        this.DrawFilterSlots(b, slots);
        this.DrawControls(b);
        this.DrawSeparator(b);
        this.DrawInventory(b);
        this.DrawHoverTooltip(b, slots);
        this.upperRightCloseButton?.draw(b);
        this.drawMouse(b);
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
            view?.Item?.drawInMenu(b, new Vector2(cell.X + 8, cell.Y + 8), 1f, 1f, 0.86f, StackDrawType.Hide, Color.White, true);
        }
    }

    private void DrawControls(SpriteBatch b)
    {
        this.oreButton.label = this.runtime.IsPoweredOreDictionaryModeEnabled(this.machine)
            ? ModText.Get("ui.poweredTransfer.ore.on", "Ore: on")
            : ModText.Get("ui.poweredTransfer.ore.off", "Ore: off");

        DrawButton(b, this.modeButton, true);
        DrawButton(b, this.oreButton, true);
        DrawButton(b, this.qualityButton, true);
        DrawButton(b, this.clearButton, true);

        var facing = this.runtime.GetPoweredFacingDirection(this.machine);
        foreach (var button in this.directionButtons)
        {
            var selected = int.TryParse(button.name, out var direction) && direction == facing;
            DrawButton(b, button, true, selected ? Color.LightGreen : Color.White);
        }

        var x = this.filterArea.Right + 34;
        var y = this.filterArea.Bottom + 24;
        foreach (var line in this.runtime.DescribePoweredConfigurationLines(this.machine).Take(7))
        {
            b.DrawString(Game1.smallFont, line, new Vector2(x, y), Game1.textColor);
            y += 26;
        }
    }

    private void DrawInventory(SpriteBatch b)
    {
        for (var i = 0; i < Game1.player.Items.Count; i++)
        {
            var bounds = this.GetInventorySlotBounds(i);
            if (!this.inventoryArea.Contains(bounds))
                continue;

            DrawInsetBox(b, bounds, Color.White * 0.78f);
            Game1.player.Items[i]?.drawInMenu(b, new Vector2(bounds.X + 8, bounds.Y + 8), 1f, 1f, 0.86f, StackDrawType.Draw, Color.White, true);
        }
    }

    private void DrawHoverTooltip(SpriteBatch b, IReadOnlyList<PoweredTransferFilterSlotView> slots)
    {
        var mx = Game1.getMouseX();
        var my = Game1.getMouseY();
        var slotIndex = this.HitFilterSlot(mx, my);
        if (slotIndex >= 0)
        {
            var view = slots.FirstOrDefault(slot => slot.SlotIndex == slotIndex);
            if (view?.Occupied == true)
            {
                var lines = new List<string> { view.QualifiedItemId };
                if (view.OreGroups.Count > 0)
                    lines.Add(ModText.Get("ui.poweredTransfer.tooltip.oreGroups", "Ore groups: {{groups}}", new { groups = string.Join(", ", view.OreGroups) }));
                IClickableMenu.drawHoverText(b, string.Join(Environment.NewLine, lines), Game1.smallFont, boldTitleText: view.DisplayName);
            }

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
        this.RunAction(() => this.runtime.TrySetPoweredFilterSlot(this.machine, this.location, this.tile, slot, item.QualifiedItemId, out var message) ? message : message);
        this.selectedSlot = Math.Min(slot + 1, FilterColumns * FilterRows - 1);
    }

    private int FindTargetFilterSlot()
    {
        var slots = this.runtime.GetPoweredFilterSlotViews(this.machine);
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

        var column = (x - this.inventoryArea.X) / Cell;
        var row = (y - this.inventoryArea.Y) / Cell;
        var index = row * this.backpackColumns + column;
        return index >= 0 && index < Game1.player.Items.Count ? index : -1;
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
        return new Rectangle(this.inventoryArea.X + column * Cell, this.inventoryArea.Y + row * Cell, Cell - 4, Cell - 4);
    }

    private void RunAction(Func<string?> action)
    {
        var message = action();
        if (!string.IsNullOrWhiteSpace(message))
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.newQuest_type));
        Game1.playSound("smallSelect");
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
        var size = Game1.smallFont.MeasureString(button.label);
        var maxWidth = Math.Max(1, button.bounds.Width - 18);
        var scale = size.X > maxWidth ? Math.Max(0.62f, maxWidth / size.X) : 1f;
        var x = button.bounds.X + (button.bounds.Width - size.X * scale) / 2f;
        var y = button.bounds.Y + (button.bounds.Height - size.Y * scale) / 2f;
        Utility.drawTextWithShadow(b, button.label, Game1.smallFont, new Vector2(x, y), color, scale);
    }
}
