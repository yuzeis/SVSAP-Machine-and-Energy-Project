using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SVSAPME.Content;
using SVSAPME.Models;
using SVSAPME.Services;
using StardewValley;
using StardewValley.Menus;

namespace SVSAPME.UI;

/// <summary>Structured farmhand UI for host-authoritative SVSAPME machines.</summary>
internal sealed class RemoteMachineControlMenu : IClickableMenu, IRemoteMachineMenu
{
    private const int Pad = 20;
    private const int PageSize = 30;
    private const int CellSize = 50;
    private const int InventoryCellSize = 52;
    private const int RefreshIntervalTicks = 30;
    private static readonly Rectangle PanelSource = new(0, 256, 60, 60);

    private readonly Func<SvsapmeMachineActionRequest, bool> sendAction;
    private readonly Func<Guid, int, int, bool> requestSnapshot;
    private readonly Func<Guid, bool> isPending;
    private readonly ClickableComponent previousButton;
    private readonly ClickableComponent nextButton;
    private SvsapmeMachineSnapshotResponse snapshot;
    private long appliedRevision;
    private int requestedOffset;
    private int selectedFilterSlot;
    private int selectedUpgradeSlot = -1;
    private int lastRefreshTick;
    private string? hoverText;

    public RemoteMachineControlMenu(
        SvsapmeMachineSnapshotResponse snapshot,
        Func<SvsapmeMachineActionRequest, bool> sendAction,
        Func<Guid, int, int, bool> requestSnapshot,
        Func<Guid, bool> isPending)
        : base(
            Math.Max(0, (Game1.uiViewport.Width - GetMenuWidth()) / 2),
            Math.Max(0, (Game1.uiViewport.Height - GetMenuHeight()) / 2),
            GetMenuWidth(),
            GetMenuHeight(),
            true)
    {
        this.snapshot = snapshot;
        this.appliedRevision = snapshot.Revision;
        this.sendAction = sendAction;
        this.requestSnapshot = requestSnapshot;
        this.isPending = isPending;
        this.requestedOffset = ResolveOffset(snapshot);
        this.lastRefreshTick = Game1.ticks;

        var navigationY = this.InventoryBounds.Y - 46;
        this.previousButton = new ClickableComponent(
            new Rectangle(this.xPositionOnScreen + this.width / 2 - 116, navigationY, 104, 38),
            "previous",
            "<");
        this.nextButton = new ClickableComponent(
            new Rectangle(this.xPositionOnScreen + this.width / 2 + 12, navigationY, 104, 38),
            "next",
            ">");

        if (this.upperRightCloseButton is not null)
        {
            this.upperRightCloseButton.bounds.X = this.xPositionOnScreen + this.width - 62;
            this.upperRightCloseButton.bounds.Y = this.yPositionOnScreen + 12;
        }
    }

    public Guid MachineGuid => this.snapshot.MachineGuid;

    public int SnapshotOffset => this.requestedOffset;

    public int SnapshotLimit => PageSize;

    private Rectangle ContentBounds => new(
        this.xPositionOnScreen + Pad,
        this.yPositionOnScreen + 76,
        this.width - Pad * 2,
        this.InventoryBounds.Y - this.yPositionOnScreen - 130);

    private Rectangle InventoryBounds
    {
        get
        {
            const int columns = 12;
            const int rows = 3;
            var width = columns * InventoryCellSize;
            return new Rectangle(
                this.xPositionOnScreen + (this.width - width) / 2,
                this.yPositionOnScreen + this.height - Pad - rows * InventoryCellSize,
                width,
                rows * InventoryCellSize);
        }
    }

    public void ApplySnapshot(SvsapmeMachineSnapshotResponse next)
    {
        if (!next.Success || next.MachineGuid != this.MachineGuid || next.Revision < this.appliedRevision)
            return;

        this.snapshot = next;
        this.appliedRevision = next.Revision;
        this.requestedOffset = ResolveOffset(next);
        this.lastRefreshTick = Game1.ticks;
    }

    public override void update(GameTime time)
    {
        base.update(time);
        if (Game1.ticks - this.lastRefreshTick < RefreshIntervalTicks || this.isPending(this.MachineGuid))
            return;

        this.lastRefreshTick = Game1.ticks;
        this.requestSnapshot(this.MachineGuid, this.requestedOffset, PageSize);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (this.upperRightCloseButton?.containsPoint(x, y) == true)
        {
            this.exitThisMenu();
            return;
        }

        if (this.previousButton.containsPoint(x, y))
        {
            this.RequestPage(Math.Max(0, this.requestedOffset - PageSize));
            return;
        }

        if (this.nextButton.containsPoint(x, y) && this.requestedOffset + PageSize < this.GetCapacity())
        {
            this.RequestPage(this.requestedOffset + PageSize);
            return;
        }

        if (this.isPending(this.MachineGuid))
        {
            Game1.playSound("cancel");
            return;
        }

        if (this.TryHandleActionButton(x, y))
            return;

        if (this.TryHandleMachineSlot(x, y))
            return;

        var inventoryIndex = this.HitInventorySlot(x, y);
        if (inventoryIndex >= 0)
        {
            this.HandleInventoryItem(inventoryIndex);
            return;
        }

        switch (this.snapshot.MenuKind)
        {
            case SvsapmeMachineMenuKind.Farm:
                this.HandleFarmCell(x, y);
                break;
            case SvsapmeMachineMenuKind.Processor:
                this.HandleProcessorCell(x, y);
                break;
            case SvsapmeMachineMenuKind.PoweredTransfer:
                this.HandlePoweredCell(x, y);
                break;
        }
    }

    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        if (this.isPending(this.MachineGuid))
            return;

        if (this.snapshot.MenuKind == SvsapmeMachineMenuKind.Farm)
        {
            var cellIndex = this.HitWorkCell(x, y);
            var plot = cellIndex >= 0 && cellIndex < (this.snapshot.Farm?.Plots.Count ?? 0)
                ? this.snapshot.Farm!.Plots[cellIndex]
                : null;
            if (plot is not null)
                this.Send(SvsapmeMachineActionKind.ToggleFarmPlotLock, plot.PlotIndex);
        }
        else if (this.snapshot.MenuKind == SvsapmeMachineMenuKind.PoweredTransfer)
        {
            var filterIndex = this.HitFilterCell(x, y);
            if (filterIndex >= 0)
                this.Send(SvsapmeMachineActionKind.ClearPoweredFilterSlot, filterIndex);
        }
    }

    public override void receiveScrollWheelAction(int direction)
    {
        var offset = direction > 0
            ? Math.Max(0, this.requestedOffset - PageSize)
            : Math.Min(this.GetLastPageOffset(), this.requestedOffset + PageSize);
        this.RequestPage(offset);
    }

    public override void performHoverAction(int x, int y)
    {
        this.hoverText = null;
        var inventoryIndex = this.HitInventorySlot(x, y);
        if (inventoryIndex >= 0 && Game1.player.Items[inventoryIndex] is Item inventoryItem)
        {
            this.hoverText = inventoryItem.DisplayName;
            return;
        }

        if (this.snapshot.MenuKind == SvsapmeMachineMenuKind.PoweredTransfer)
        {
            var upgradeIndex = this.HitUpgradeCell(x, y);
            if (upgradeIndex >= 0)
            {
                var upgrades = this.snapshot.PoweredTransfer?.InstalledUpgradeQualifiedItemIds;
                var qualifiedItemId = upgrades is not null && upgradeIndex < upgrades.Count ? upgrades[upgradeIndex] : string.Empty;
                this.hoverText = string.IsNullOrWhiteSpace(qualifiedItemId)
                    ? ModText.Get("ui.powered.upgrade.empty", "Select this slot, then click a supported upgrade card in your backpack.")
                    : FormatItem(qualifiedItemId, qualifiedItemId);
                return;
            }

            var filterIndex = this.HitFilterCell(x, y);
            if (filterIndex >= 0)
            {
                var filter = this.snapshot.PoweredTransfer?.FilterSlots.FirstOrDefault(slot => slot.SlotIndex == filterIndex);
                this.hoverText = string.IsNullOrWhiteSpace(filter?.DisplayName)
                    ? ModText.Get("ui.common.empty", "Empty")
                    : filter.DisplayName;
                return;
            }
        }

        if (this.snapshot.MenuKind == SvsapmeMachineMenuKind.EnergyMonitor)
        {
            var device = this.HitEnergyDevice(x, y);
            if (device is not null)
            {
                IReadOnlyList<string> details = device.Details.Count > 0
                    ? device.Details
                    : new[] { ModText.Get("ui.energyMeter.deviceTotal", "Today total: {{value}}", new { value = FormatWh(device.TotalWh) }) };
                this.hoverText = device.DisplayName + Environment.NewLine + string.Join(Environment.NewLine, details);
                return;
            }
        }

        var cellIndex = this.HitWorkCell(x, y);
        if (cellIndex < 0)
            return;

        if (this.snapshot.MenuKind == SvsapmeMachineMenuKind.Farm
            && cellIndex < (this.snapshot.Farm?.Plots.Count ?? 0))
        {
            var plot = this.snapshot.Farm!.Plots[cellIndex];
            var name = FormatItem(plot.HarvestQualifiedItemId, plot.SeedQualifiedItemId);
            var progress = plot.RequiredUnits <= 0 ? 0m : Math.Clamp(plot.ProgressUnits / (decimal)plot.RequiredUnits, 0m, 1m);
            this.hoverText = ModText.Get(
                "ui.remoteFarm.plotTooltip",
                "{{item}}\nProgress: {{progress}}%\n{{lock}}",
                new
                {
                    item = name,
                    progress = (progress * 100m).ToString("0"),
                    @lock = plot.IsLocked
                        ? ModText.Get("ui.remoteFarm.locked", "Locked")
                        : ModText.Get("ui.remoteFarm.unlocked", "Unlocked")
                });
        }
        else if (this.snapshot.MenuKind == SvsapmeMachineMenuKind.Processor
                 && cellIndex < (this.snapshot.Processor?.Slots.Count ?? 0))
        {
            var slot = this.snapshot.Processor!.Slots[cellIndex];
            var input = slot.Input is null ? ModText.Get("ui.common.empty", "Empty") : FormatBufferedItem(slot.Input);
            var output = slot.Output is null ? ModText.Get("ui.common.empty", "Empty") : FormatBufferedItem(slot.Output);
            var remaining = Math.Max(0, slot.Remaining);
            this.hoverText = ModText.Get(
                "ui.remoteProcessor.slotTooltip",
                "Input: {{input}}\nOutput: {{output}}\nRemaining: {{remaining}} minutes",
                new { input, output, remaining });
            if (slot.CanEject)
                this.hoverText += "\n" + ModText.Get("ui.processor.tooltip.eject", "Use the arrow button to eject the current quality.");
        }
    }

    public override void draw(SpriteBatch b)
    {
        IClickableMenu.drawTextureBox(
            b,
            Game1.menuTexture,
            PanelSource,
            this.xPositionOnScreen,
            this.yPositionOnScreen,
            this.width,
            this.height,
            Color.White,
            1f,
            true);

        var title = string.IsNullOrWhiteSpace(this.snapshot.DisplayName)
            ? ModText.Get("ui.machine.remoteTitle", "SVSAPME Machine")
            : this.snapshot.DisplayName;
        Utility.drawTextWithShadow(
            b,
            title,
            Game1.dialogueFont,
            new Vector2(this.xPositionOnScreen + Pad + 8, this.yPositionOnScreen + 22),
            Game1.textColor);

        var pending = this.isPending(this.MachineGuid);
        SvsapmeUiText.DrawPixelStatusLight(
            b,
            this.xPositionOnScreen + this.width - 92,
            this.yPositionOnScreen + 34,
            pending ? PixelStatus.Processing : PixelStatus.Ready);

        this.DrawContent(b);
        this.DrawInventory(b);
        DrawButton(b, this.previousButton, this.requestedOffset > 0);
        DrawButton(b, this.nextButton, this.requestedOffset + PageSize < this.GetCapacity());
        this.upperRightCloseButton?.draw(b);

        if (!string.IsNullOrWhiteSpace(this.hoverText))
            IClickableMenu.drawHoverText(b, this.hoverText, Game1.smallFont);

        this.drawMouse(b);
    }

    private void DrawContent(SpriteBatch b)
    {
        switch (this.snapshot.MenuKind)
        {
            case SvsapmeMachineMenuKind.Farm:
                this.DrawFarm(b);
                break;
            case SvsapmeMachineMenuKind.Processor:
                this.DrawProcessor(b);
                break;
            case SvsapmeMachineMenuKind.PoweredTransfer:
                this.DrawPoweredTransfer(b);
                break;
            case SvsapmeMachineMenuKind.EnergyMonitor:
                this.DrawEnergyMonitor(b);
                break;
            default:
                this.DrawGeneric(b);
                break;
        }
    }

    private void DrawFarm(SpriteBatch b)
    {
        var farm = this.snapshot.Farm;
        if (farm is null)
        {
            this.DrawUnavailable(b);
            return;
        }

        var (left, center, right) = this.GetThreeColumns();
        DrawPanel(b, left);
        DrawPanel(b, center);
        DrawPanel(b, right);
        DrawSectionTitle(b, left, ModText.Get("ui.singleBlock.input", "Input"));
        DrawSectionTitle(b, right, ModText.Get("ui.singleBlock.output", "Output"));

        var seedSlot = new Rectangle(left.X + 12, left.Y + 36, 52, 52);
        var fertilizerSlot = new Rectangle(left.X + 76, left.Y + 36, 52, 52);
        DrawSlot(b, seedSlot, farm.InputBuffer.FirstOrDefault(), farm.InputBuffer.Count > 0 ? PixelStatus.Ready : PixelStatus.Idle);
        DrawSlot(b, fertilizerSlot, string.IsNullOrWhiteSpace(farm.FertilizerQualifiedItemId) ? null : new BufferedItemStack { QualifiedItemId = farm.FertilizerQualifiedItemId, Stack = farm.FertilizerCount }, farm.FertilizerCount > 0 ? PixelStatus.Ready : PixelStatus.Idle);

        foreach (var button in this.GetFarmButtons())
            DrawButton(b, button.Component, button.Enabled);

        var output = new Rectangle(right.X + (right.Width - 56) / 2, right.Y + 38, 56, 56);
        DrawSlot(b, output, farm.OutputBuffer.FirstOrDefault(), farm.OutputBuffer.Count > 0 ? PixelStatus.Ready : PixelStatus.Idle);

        var grid = this.WorkGridBounds;
        for (var i = 0; i < PageSize; i++)
        {
            var bounds = GetGridCell(grid, i);
            var plot = i < farm.Plots.Count ? farm.Plots[i] : null;
            DrawWorkCell(b, bounds, plot is null ? PixelStatus.Idle : plot.Ready ? PixelStatus.Ready : PixelStatus.Processing);
            if (plot is null)
                continue;

            var itemId = !string.IsNullOrWhiteSpace(plot.HarvestQualifiedItemId)
                ? plot.HarvestQualifiedItemId
                : plot.SeedQualifiedItemId;
            DrawQualifiedItem(b, itemId, bounds, 0.62f);
            DrawProgress(b, bounds, plot.ProgressUnits, plot.RequiredUnits);
            if (plot.IsLocked)
                SvsapmeUiText.DrawPixelLock(b, bounds.X + 3, bounds.Y + 3);
        }

        var page = this.requestedOffset / PageSize + 1;
        var pages = Math.Max(1, (int)Math.Ceiling(farm.PlotCapacity / (double)PageSize));
        SvsapmeUiText.DrawFittedLine(
            b,
            ModText.Get("ui.common.page", "Page {{page}}/{{pages}}", new { page, pages }),
            new Rectangle(center.X + 8, center.Y + 6, center.Width - 16, 24),
            Game1.textColor);

        SvsapmeUiText.DrawFittedLines(
            b,
            new[]
            {
                ModText.Get("ui.singleBlock.dayValue", "{{value}}g/day", new { value = farm.EstimatedDailyValue.ToString("0") }),
                ModText.Get("ui.singleBlock.energyDay", "{{value}} Wh/day", new { value = farm.EstimatedDailyEnergyWh.ToString("N0") })
            },
            new Rectangle(right.X + 10, right.Bottom - 62, right.Width - 20, 52),
            Game1.textColor);

        this.DrawFarmModules(b, farm, center);
    }

    private void DrawProcessor(SpriteBatch b)
    {
        var processor = this.snapshot.Processor;
        if (processor is null)
        {
            this.DrawUnavailable(b);
            return;
        }

        var (left, center, right) = this.GetThreeColumns();
        DrawPanel(b, left);
        DrawPanel(b, center);
        DrawPanel(b, right);
        DrawSectionTitle(b, left, ModText.Get("ui.singleBlock.input", "Input"));
        DrawSectionTitle(b, right, ModText.Get("ui.singleBlock.output", "Output"));

        var input = new Rectangle(left.X + (left.Width - 58) / 2, left.Y + 36, 58, 58);
        DrawSlot(b, input, processor.InputBuffer.FirstOrDefault(), processor.InputBuffer.Count > 0 ? PixelStatus.Ready : PixelStatus.Idle);
        foreach (var button in this.GetProcessorButtons())
            DrawButton(b, button.Component, button.Enabled);

        var output = new Rectangle(right.X + (right.Width - 58) / 2, right.Y + 36, 58, 58);
        DrawSlot(b, output, processor.OutputBuffer.FirstOrDefault(), processor.OutputBuffer.Count > 0 ? PixelStatus.Ready : PixelStatus.Idle);

        var grid = this.WorkGridBounds;
        for (var i = 0; i < PageSize; i++)
        {
            var bounds = GetGridCell(grid, i);
            var slot = i < processor.Slots.Count ? processor.Slots[i] : null;
            var status = slot is null || (slot.Input is null && slot.Output is null)
                ? PixelStatus.Idle
                : slot.Ready
                    ? PixelStatus.Ready
                    : PixelStatus.Processing;
            DrawWorkCell(b, bounds, status);
            if (slot is null)
                continue;

            DrawBufferedItem(b, slot.Output ?? slot.Input, bounds, 0.62f);
            DrawProgress(b, bounds, Math.Max(0, slot.Total - slot.Remaining), slot.Total);
            if (slot.CanEject)
                DrawEjectButton(b, GetEjectButtonBounds(bounds));
        }

        var page = this.requestedOffset / PageSize + 1;
        var pages = Math.Max(1, (int)Math.Ceiling(processor.SlotCapacity / (double)PageSize));
        SvsapmeUiText.DrawFittedLine(
            b,
            ModText.Get("ui.common.page", "Page {{page}}/{{pages}}", new { page, pages }),
            new Rectangle(center.X + 8, center.Y + 6, center.Width - 16, 24),
            Game1.textColor);

        SvsapmeUiText.DrawFittedLine(
            b,
            ModText.Get("ui.singleBlock.dayValue", "{{value}}g/day", new { value = processor.EstimatedDailyValue.ToString("0") }),
            new Rectangle(right.X + 10, right.Bottom - 36, right.Width - 20, 24),
            Game1.textColor);
    }

    private void DrawPoweredTransfer(SpriteBatch b)
    {
        var powered = this.snapshot.PoweredTransfer;
        if (powered is null)
        {
            this.DrawUnavailable(b);
            return;
        }

        var content = this.ContentBounds;
        var filterPanel = new Rectangle(content.X, content.Y, 230, content.Height);
        var statusPanel = new Rectangle(filterPanel.Right + 16, content.Y, content.Width - filterPanel.Width - 16, content.Height);
        DrawPanel(b, filterPanel);
        DrawPanel(b, statusPanel);
        DrawSectionTitle(b, filterPanel, ModText.Get("ui.powered.filter.title", "Filters"));
        DrawSectionTitle(b, statusPanel, ModText.Get("ui.powered.status.title", "Status"));

        for (var i = 0; i < 9; i++)
        {
            var cell = GetFilterCell(filterPanel, i);
            var view = powered.FilterSlots.FirstOrDefault(slot => slot.SlotIndex == i);
            DrawWorkCell(b, cell, string.IsNullOrWhiteSpace(view?.QualifiedItemId) ? PixelStatus.Idle : PixelStatus.Ready);
            DrawQualifiedItem(b, view?.QualifiedItemId ?? string.Empty, cell, 0.68f, Color.White * 0.58f);
            if (i == this.selectedFilterSlot)
                DrawSelection(b, cell);
        }

        foreach (var button in this.GetPoweredButtons())
            DrawButton(b, button.Component, button.Enabled);

        var networkStatus = powered.NetworkOnline
            ? ModText.Get("ui.powered.networkStatus", "Power online: {{stored}} / {{capacity}}", new { stored = FormatWh(powered.StoredWh), capacity = FormatWh(powered.CapacityWh) })
            : ModText.Get("ui.powered.network.offline", "Power offline or no energy cell");
        SvsapmeUiText.DrawPixelStatusLight(b, statusPanel.X + 16, statusPanel.Y + 45, powered.NetworkOnline ? PixelStatus.Ready : PixelStatus.Offline);
        SvsapmeUiText.DrawFittedLine(
            b,
            networkStatus,
            new Rectangle(statusPanel.X + 34, statusPanel.Y + 36, statusPanel.Width - 50, 26),
            powered.NetworkOnline ? Game1.textColor : Color.Crimson);

        var meter = new Rectangle(statusPanel.X + 16, statusPanel.Y + 66, statusPanel.Width - 32, 18);
        b.Draw(Game1.staminaRect, meter, Color.Black * 0.42f);
        var powerRatio = powered.CapacityWh <= 0 ? 0m : Math.Clamp(powered.StoredWh / (decimal)powered.CapacityWh, 0m, 1m);
        var powerFillWidth = (int)((meter.Width - 4) * powerRatio);
        if (powerFillWidth > 0)
            b.Draw(Game1.staminaRect, new Rectangle(meter.X + 2, meter.Y + 2, powerFillWidth, meter.Height - 4), powerRatio < 0.15m ? Color.Crimson : powerRatio < 0.4m ? Color.Orange : Color.LimeGreen);

        var lines = new[]
        {
            ModText.Get("ui.powered.performance", "Throughput: {{count}} / interval: {{ticks}} ticks", new { count = powered.Throughput.ToString("N0"), ticks = powered.TransferIntervalTicks.ToString("N0") }),
            ModText.Get("ui.powered.energy", "Energy: {{wh}} Wh/action", new { wh = powered.EnergyPerActionWh.ToString("0.0#") }),
            ModText.Get("ui.powered.direction", "Direction: {{direction}}", new { direction = FormatDirection(powered.FacingDirection) })
        };
        SvsapmeUiText.DrawFittedLines(
            b,
            lines,
            new Rectangle(statusPanel.X + 16, statusPanel.Y + 92, statusPanel.Width - 32, 54),
            Game1.textColor);

        var upgradesY = statusPanel.Y + 154;
        for (var i = 0; i < powered.UpgradeSlotCapacity; i++)
        {
            var cell = new Rectangle(statusPanel.X + 18 + i * 58, upgradesY, 52, 52);
            var qualifiedItemId = i < powered.InstalledUpgradeQualifiedItemIds.Count
                ? powered.InstalledUpgradeQualifiedItemIds[i]
                : string.Empty;
            DrawWorkCell(b, cell, string.IsNullOrWhiteSpace(qualifiedItemId) ? PixelStatus.Idle : PixelStatus.Ready);
            if (string.IsNullOrWhiteSpace(qualifiedItemId))
                SvsapmeUiText.DrawGhostUpgradeSlot(b, cell);
            else
                DrawQualifiedItem(b, qualifiedItemId, cell, 0.68f);
            if (i == this.selectedUpgradeSlot)
                DrawSelection(b, cell);
        }
    }

    private void DrawEnergyMonitor(SpriteBatch b)
    {
        var energy = this.snapshot.EnergyMonitor;
        if (energy is null)
        {
            this.DrawUnavailable(b);
            return;
        }

        var content = this.ContentBounds;
        DrawPanel(b, content);
        var status = energy.Online
            ? string.IsNullOrWhiteSpace(energy.StatusText) ? ModText.Get("ui.machine.network.online", "Network online") : energy.StatusText
            : string.IsNullOrWhiteSpace(energy.StatusText) ? ModText.Get("ui.powered.network.offline", "Power offline or no energy cell") : energy.StatusText;
        SvsapmeUiText.DrawPixelStatusLight(b, content.X + 22, content.Y + 17, energy.Online ? PixelStatus.Ready : PixelStatus.Offline);
        SvsapmeUiText.DrawFittedLine(
            b,
            status,
            new Rectangle(content.X + 40, content.Y + 8, content.Width - 62, 26),
            energy.Online ? Game1.textColor : Color.Crimson);

        var meter = new Rectangle(content.X + 22, content.Y + 42, content.Width - 44, 34);
        b.Draw(Game1.staminaRect, meter, Color.Black * 0.45f);
        var ratio = energy.CapacityWh <= 0 ? 0m : Math.Clamp(energy.StoredWh / (decimal)energy.CapacityWh, 0m, 1m);
        var fillWidth = (int)((meter.Width - 6) * ratio);
        if (fillWidth > 0)
            b.Draw(Game1.staminaRect, new Rectangle(meter.X + 3, meter.Y + 3, fillWidth, meter.Height - 6), ratio < 0.15m ? Color.Crimson : ratio < 0.4m ? Color.Orange : Color.LimeGreen);
        SvsapmeUiText.DrawFittedLine(
            b,
            ModText.Get("ui.energyMeter.capacity", "{{stored}} / {{capacity}} kWh", new { stored = (energy.StoredWh / 1000m).ToString("0.00"), capacity = (energy.CapacityWh / 1000m).ToString("0.00") }),
            meter,
            Color.White);

        var summary = new[]
        {
            ModText.Get("ui.energyMeter.lastTick", "Last flow: +{{generated}} / -{{consumed}} / net {{net}}", new { generated = FormatWh(energy.LastTickGeneratedWh), consumed = FormatWh(energy.LastTickConsumedWh), net = FormatSignedWh(energy.LastTickGeneratedWh - energy.LastTickConsumedWh) }),
            ModText.Get("ui.energyMeter.today", "Today: +{{generated}} / -{{consumed}} / net {{net}}", new { generated = FormatWh(energy.TodayGeneratedWh), consumed = FormatWh(energy.TodayConsumedWh), net = FormatSignedWh(energy.TodayGeneratedWh - energy.TodayConsumedWh) })
        };
        SvsapmeUiText.DrawFittedLines(b, summary, new Rectangle(content.X + 22, meter.Bottom + 16, content.Width - 44, 54), Game1.textColor);

        var half = (content.Width - 60) / 2;
        var devicesY = meter.Bottom + 80;
        var devicesHeight = Math.Max(80, content.Bottom - devicesY - 14);
        this.DrawEnergyDevices(b, new Rectangle(content.X + 22, devicesY, half, devicesHeight), ModText.Get("ui.energyMeter.producers", "Producers"), energy.Producers);
        this.DrawEnergyDevices(b, new Rectangle(content.X + 38 + half, devicesY, half, devicesHeight), ModText.Get("ui.energyMeter.consumers", "Consumers"), energy.Consumers);
    }

    private void DrawGeneric(SpriteBatch b)
    {
        DrawPanel(b, this.ContentBounds);
        SvsapmeUiText.DrawFittedLines(b, this.snapshot.Lines, new Rectangle(this.ContentBounds.X + 18, this.ContentBounds.Y + 18, this.ContentBounds.Width - 36, this.ContentBounds.Height - 36), Game1.textColor);
    }

    private void DrawUnavailable(SpriteBatch b)
    {
        DrawPanel(b, this.ContentBounds);
        SvsapmeUiText.DrawFittedLine(b, ModText.Get("ui.machine.snapshotUnavailable", "Machine details are unavailable."), new Rectangle(this.ContentBounds.X + 20, this.ContentBounds.Y + 20, this.ContentBounds.Width - 40, 36), Color.Crimson);
    }

    private void DrawInventory(SpriteBatch b)
    {
        var inventory = this.InventoryBounds;
        for (var i = 0; i < Math.Min(36, Game1.player.Items.Count); i++)
        {
            var bounds = this.GetInventorySlotBounds(i);
            IClickableMenu.drawTextureBox(b, Game1.menuTexture, PanelSource, bounds.X, bounds.Y, bounds.Width, bounds.Height, Color.White * 0.82f, 1f, false);
            Game1.player.Items[i]?.drawInMenu(b, new Vector2(bounds.X + 4, bounds.Y + 4), 0.72f, 1f, 0.86f, StackDrawType.Draw, Color.White, true);
        }

        b.Draw(Game1.staminaRect, new Rectangle(inventory.X, inventory.Y - 10, inventory.Width, 2), Color.SaddleBrown * 0.55f);
    }

    private void DrawFarmModules(SpriteBatch b, SvsapmeFarmMenuSnapshot farm, Rectangle center)
    {
        var capacity = Math.Max(farm.ModuleSlotCapacity, farm.InstalledModuleQualifiedItemIds.Count);
        for (var i = 0; i < capacity; i++)
        {
            var cell = GetFarmModuleCell(center, capacity, i);
            DrawWorkCell(b, cell, i < farm.InstalledModuleQualifiedItemIds.Count ? PixelStatus.Ready : PixelStatus.Idle);
            if (i < farm.InstalledModuleQualifiedItemIds.Count)
                DrawQualifiedItem(b, farm.InstalledModuleQualifiedItemIds[i], cell, 0.46f);
        }
    }

    private void DrawEnergyDevices(SpriteBatch b, Rectangle bounds, string title, IReadOnlyList<SvsapmeEnergyDeviceSnapshot> devices)
    {
        DrawPanel(b, bounds);
        DrawSectionTitle(b, bounds, title);
        var lines = devices.Take(7).Select(device => ModText.Get("ui.energyMeter.deviceLine", "{{name}}: {{value}}", new { name = device.DisplayName, value = FormatWh(device.TotalWh) }));
        SvsapmeUiText.DrawFittedLines(b, lines, new Rectangle(bounds.X + 12, bounds.Y + 36, bounds.Width - 24, bounds.Height - 46), Game1.textColor);
    }

    private SvsapmeEnergyDeviceSnapshot? HitEnergyDevice(int x, int y)
    {
        var energy = this.snapshot.EnergyMonitor;
        if (energy is null)
            return null;

        var content = this.ContentBounds;
        var meter = new Rectangle(content.X + 22, content.Y + 42, content.Width - 44, 34);
        var half = (content.Width - 60) / 2;
        var producers = new Rectangle(content.X + 22, meter.Bottom + 80, half, content.Height - 172);
        var consumers = new Rectangle(content.X + 38 + half, meter.Bottom + 80, half, content.Height - 172);
        return HitEnergyDeviceRow(producers, energy.Producers, x, y)
            ?? HitEnergyDeviceRow(consumers, energy.Consumers, x, y);
    }

    private static SvsapmeEnergyDeviceSnapshot? HitEnergyDeviceRow(
        Rectangle bounds,
        IReadOnlyList<SvsapmeEnergyDeviceSnapshot> devices,
        int x,
        int y)
    {
        for (var index = 0; index < Math.Min(7, devices.Count); index++)
        {
            var row = new Rectangle(bounds.X + 10, bounds.Y + 36 + index * SvsapmeUiText.SmallLineHeight, bounds.Width - 20, SvsapmeUiText.SmallLineHeight);
            if (row.Contains(x, y))
                return devices[index];
        }

        return null;
    }

    private bool TryHandleActionButton(int x, int y)
    {
        var buttons = this.snapshot.MenuKind switch
        {
            SvsapmeMachineMenuKind.Farm => this.GetFarmButtons(),
            SvsapmeMachineMenuKind.Processor => this.GetProcessorButtons(),
            SvsapmeMachineMenuKind.PoweredTransfer => this.GetPoweredButtons(),
            _ => Array.Empty<RemoteActionButton>()
        };

        foreach (var button in buttons)
        {
            if (!button.Component.containsPoint(x, y))
                continue;

            if (!button.Enabled)
            {
                Game1.playSound("cancel");
                return true;
            }

            this.Send(button.ActionKind, direction: button.Direction);
            return true;
        }

        return false;
    }

    private bool TryHandleMachineSlot(int x, int y)
    {
        var (left, center, right) = this.GetThreeColumns();
        if (this.snapshot.MenuKind == SvsapmeMachineMenuKind.Farm && this.snapshot.Farm is { } farm)
        {
            var seedSlot = new Rectangle(left.X + 12, left.Y + 36, 52, 52);
            if (seedSlot.Contains(x, y) && farm.InputBuffer.Count > 0)
            {
                this.Send(SvsapmeMachineActionKind.ExtractFarmSeed);
                return true;
            }

            var fertilizerSlot = new Rectangle(left.X + 76, left.Y + 36, 52, 52);
            if (fertilizerSlot.Contains(x, y) && farm.FertilizerCount > 0)
            {
                this.Send(SvsapmeMachineActionKind.ExtractFarmFertilizer);
                return true;
            }

            var outputSlot = new Rectangle(right.X + (right.Width - 56) / 2, right.Y + 38, 56, 56);
            if (outputSlot.Contains(x, y) && farm.OutputBuffer.Count > 0)
            {
                this.Send(SvsapmeMachineActionKind.CollectFarmOutput);
                return true;
            }

            var capacity = Math.Max(farm.ModuleSlotCapacity, farm.InstalledModuleQualifiedItemIds.Count);
            for (var i = 0; i < farm.InstalledModuleQualifiedItemIds.Count; i++)
            {
                if (!GetFarmModuleCell(center, capacity, i).Contains(x, y))
                    continue;

                this.Send(SvsapmeMachineActionKind.RemoveFarmModule, i);
                return true;
            }
        }
        else if (this.snapshot.MenuKind == SvsapmeMachineMenuKind.Processor && this.snapshot.Processor is { } processor)
        {
            var inputSlot = new Rectangle(left.X + (left.Width - 58) / 2, left.Y + 36, 58, 58);
            if (inputSlot.Contains(x, y) && processor.InputBuffer.Count > 0)
            {
                this.Send(SvsapmeMachineActionKind.ExtractProcessorInput);
                return true;
            }

            var outputSlot = new Rectangle(right.X + (right.Width - 58) / 2, right.Y + 36, 58, 58);
            if (outputSlot.Contains(x, y)
                && (processor.OutputBuffer.Count > 0 || processor.Slots.Any(slot => slot.CanCollect)))
            {
                this.Send(SvsapmeMachineActionKind.CollectProcessorOutput);
                return true;
            }
        }

        return false;
    }

    private void HandleInventoryItem(int inventoryIndex)
    {
        var item = Game1.player.Items[inventoryIndex];
        if (item is null)
            return;

        var shift = Game1.oldKBState.IsKeyDown(Keys.LeftShift) || Game1.oldKBState.IsKeyDown(Keys.RightShift);
        var count = shift ? item.Stack : 1;
        var action = SvsapmeMachineActionKind.None;
        var slot = -1;

        switch (this.snapshot.MenuKind)
        {
            case SvsapmeMachineMenuKind.Farm:
                if (FarmCropCatalog.TryGetBySeed(item.QualifiedItemId, out _))
                    action = SvsapmeMachineActionKind.LoadFarmSeed;
                else if (FarmModuleRules.IsFertilizer(item.QualifiedItemId))
                    action = SvsapmeMachineActionKind.LoadFarmFertilizer;
                else if (FarmModuleRules.TryGetModule(item.QualifiedItemId, out _))
                    action = SvsapmeMachineActionKind.InstallFarmModule;
                else
                    action = SvsapmeMachineActionKind.AddFarmFilter;
                break;
            case SvsapmeMachineMenuKind.Processor:
                action = SvsapmeMachineActionKind.LoadProcessorInput;
                break;
            case SvsapmeMachineMenuKind.PoweredTransfer:
                action = this.selectedUpgradeSlot >= 0
                    ? SvsapmeMachineActionKind.InstallPoweredUpgrade
                    : SvsapmeMachineActionKind.SetPoweredFilterSlot;
                slot = action == SvsapmeMachineActionKind.InstallPoweredUpgrade ? this.selectedUpgradeSlot : this.selectedFilterSlot;
                count = 1;
                break;
        }

        if (action == SvsapmeMachineActionKind.None)
            return;

        var request = SvsapmeMachineActionRequestFactory.CreateWithItem(
            this.MachineGuid,
            action,
            item,
            count,
            slot,
            Game1.player.FarmingLevel,
            this.requestedOffset,
            PageSize);

        var oldToolIndex = Game1.player.CurrentToolIndex;
        Game1.player.CurrentToolIndex = inventoryIndex;
        try
        {
            if (this.sendAction(request))
                Game1.playSound("Ship");
        }
        finally
        {
            Game1.player.CurrentToolIndex = oldToolIndex;
        }
    }

    private void HandleFarmCell(int x, int y)
    {
        var cellIndex = this.HitWorkCell(x, y);
        if (cellIndex < 0 || cellIndex >= (this.snapshot.Farm?.Plots.Count ?? 0))
            return;

        var plot = this.snapshot.Farm!.Plots[cellIndex];
        if (plot.Ready)
        {
            this.Send(SvsapmeMachineActionKind.HarvestFarmPlot, plot.PlotIndex);
            return;
        }

        var item = Game1.player.CurrentItem;
        if (item is null || !FarmCropCatalog.TryGetBySeed(item.QualifiedItemId, out _))
            return;

        var request = SvsapmeMachineActionRequestFactory.CreateWithItem(
            this.MachineGuid,
            SvsapmeMachineActionKind.PlantFarmPlot,
            item,
            1,
            plot.PlotIndex,
            Game1.player.FarmingLevel,
            this.requestedOffset,
            PageSize);
        this.sendAction(request);
    }

    private void HandleProcessorCell(int x, int y)
    {
        var cellIndex = this.HitWorkCell(x, y);
        if (cellIndex < 0 || cellIndex >= (this.snapshot.Processor?.Slots.Count ?? 0))
            return;

        var slot = this.snapshot.Processor!.Slots[cellIndex];
        if (slot.Ready)
        {
            this.Send(SvsapmeMachineActionKind.CollectProcessorOutput, slot.SlotIndex);
            return;
        }

        var bounds = GetGridCell(this.WorkGridBounds, cellIndex);
        if (slot.CanEject && GetEjectButtonBounds(bounds).Contains(x, y))
            this.Send(SvsapmeMachineActionKind.CollectProcessorOutput, slot.SlotIndex);
    }

    private void HandlePoweredCell(int x, int y)
    {
        var filterIndex = this.HitFilterCell(x, y);
        if (filterIndex >= 0)
        {
            this.selectedFilterSlot = filterIndex;
            this.selectedUpgradeSlot = -1;
            Game1.playSound("shwip");
            return;
        }

        var upgradeIndex = this.HitUpgradeCell(x, y);
        if (upgradeIndex < 0)
            return;

        var upgradeSlots = this.snapshot.PoweredTransfer?.InstalledUpgradeQualifiedItemIds;
        var installed = upgradeSlots is not null
            && upgradeIndex < upgradeSlots.Count
            && !string.IsNullOrWhiteSpace(upgradeSlots[upgradeIndex]);
        if (installed)
            this.Send(SvsapmeMachineActionKind.RemovePoweredUpgrade, upgradeIndex);
        else
        {
            this.selectedUpgradeSlot = upgradeIndex;
            this.selectedFilterSlot = -1;
            Game1.playSound("shwip");
        }
    }

    private IReadOnlyList<RemoteActionButton> GetFarmButtons()
    {
        var farm = this.snapshot.Farm;
        if (farm is null)
            return Array.Empty<RemoteActionButton>();

        var (left, _, right) = this.GetThreeColumns();
        return new[]
        {
            MakeButton(left, 104, ModText.Get("ui.processor.autoIn", "Auto In") + ": " + SvsapmeUiText.FormatAuto(farm.AutoPullFromNetwork), SvsapmeMachineActionKind.ToggleFarmAutoPull),
            MakeButton(left, 144, ModText.Get("ui.processor.inputMode", "Input Mode") + ": " + SvsapmeUiText.FormatInputMode(farm.InputMode), SvsapmeMachineActionKind.ToggleFarmInputMode),
            MakeButton(left, 184, ModText.Get("ui.processor.filterMode", "W/B") + ": " + SvsapmeUiText.FormatFilterMode(farm.FilterMode), SvsapmeMachineActionKind.ToggleFarmFilterMode),
            MakeButton(left, 224, ModText.Get("ui.processor.clearFilter", "Clear Filter"), SvsapmeMachineActionKind.ClearFarmFilter),
            MakeButton(right, 104, ModText.Get("ui.processor.autoOut", "Auto Out") + ": " + SvsapmeUiText.FormatAuto(farm.AutoPushOutputToNetwork), SvsapmeMachineActionKind.ToggleFarmAutoPush),
            MakeButton(right, 144, ModText.Get("ui.processor.collectAll", "Collect All"), SvsapmeMachineActionKind.CollectFarmOutput, farm.OutputBuffer.Count > 0)
        };
    }

    private IReadOnlyList<RemoteActionButton> GetProcessorButtons()
    {
        var processor = this.snapshot.Processor;
        if (processor is null)
            return Array.Empty<RemoteActionButton>();

        var (left, _, right) = this.GetThreeColumns();
        return new[]
        {
            MakeButton(left, 106, ModText.Get("ui.processor.autoIn", "Auto In") + ": " + SvsapmeUiText.FormatAuto(processor.AutoPullFromNetwork), SvsapmeMachineActionKind.ToggleProcessorAutoPull),
            MakeButton(left, 146, ModText.Get("ui.processor.inputMode", "Input Mode") + ": " + SvsapmeUiText.FormatInputMode(processor.InputMode), SvsapmeMachineActionKind.ToggleProcessorInputMode),
            MakeButton(left, 186, ModText.Get("ui.processor.filterMode", "W/B") + ": " + SvsapmeUiText.FormatFilterMode(processor.FilterMode), SvsapmeMachineActionKind.ToggleProcessorFilterMode),
            MakeButton(left, 226, ModText.Get("ui.processor.clearFilter", "Clear Filter"), SvsapmeMachineActionKind.ClearProcessorFilter),
            MakeButton(left, 266, ModText.Get("ui.processor.extractInput", "Take Input"), SvsapmeMachineActionKind.ExtractProcessorInput, processor.InputBuffer.Count > 0),
            MakeButton(right, 106, ModText.Get("ui.processor.autoOut", "Auto Out") + ": " + SvsapmeUiText.FormatAuto(processor.AutoPushOutputToNetwork), SvsapmeMachineActionKind.ToggleProcessorAutoPush),
            MakeButton(right, 146, ModText.Get("ui.processor.collectAll", "Collect All"), SvsapmeMachineActionKind.CollectProcessorOutput, processor.OutputBuffer.Count > 0 || processor.Slots.Any(slot => slot.CanCollect))
        };
    }

    private IReadOnlyList<RemoteActionButton> GetPoweredButtons()
    {
        var powered = this.snapshot.PoweredTransfer;
        if (powered is null)
            return Array.Empty<RemoteActionButton>();

        var content = this.ContentBounds;
        var statusPanel = new Rectangle(content.X + 246, content.Y, content.Width - 246, content.Height);
        var hasOreCard = powered.InstalledUpgradeQualifiedItemIds.Contains("(O)" + ModItemCatalog.SvsapOreDictionaryCard, StringComparer.Ordinal);
        var hasQualityCard = powered.InstalledUpgradeQualifiedItemIds.Contains("(O)" + ModItemCatalog.SvsapQualityCard, StringComparer.Ordinal);
        var definitions = new[]
        {
            (Label: powered.IsBlacklist ? ModText.Get("ui.powered.mode.blacklist", "Blacklist") : ModText.Get("ui.powered.mode.whitelist", "Whitelist"), Action: SvsapmeMachineActionKind.TogglePoweredFilterMode, Enabled: true, Direction: -1),
            (Label: ModText.Get("ui.powered.oreDictionary", "Ore dictionary") + ": " + SvsapmeUiText.FormatAuto(powered.OreDictionaryEnabled), Action: SvsapmeMachineActionKind.TogglePoweredOreDictionaryMode, Enabled: hasOreCard, Direction: -1),
            (Label: ModText.Get("ui.powered.quality", "Quality") + ": " + powered.QualityStrategy, Action: SvsapmeMachineActionKind.CyclePoweredQualityStrategy, Enabled: hasQualityCard, Direction: -1),
            (Label: ModText.Get("ui.powered.rotate", "Rotate"), Action: SvsapmeMachineActionKind.SetPoweredFacingDirection, Enabled: true, Direction: (powered.FacingDirection + 1) % 4),
            (Label: ModText.Get("ui.processor.clearFilter", "Clear Filter"), Action: SvsapmeMachineActionKind.ClearPoweredFilters, Enabled: true, Direction: -1)
        };

        const int columns = 2;
        const int gap = 8;
        var width = Math.Max(80, (statusPanel.Width - 20 - gap) / columns);
        var result = new List<RemoteActionButton>(definitions.Length);
        for (var i = 0; i < definitions.Length; i++)
        {
            var definition = definitions[i];
            var bounds = new Rectangle(
                statusPanel.X + 10 + i % columns * (width + gap),
                statusPanel.Y + 218 + i / columns * 40,
                width,
                34);
            result.Add(new RemoteActionButton(
                new ClickableComponent(bounds, definition.Action.ToString(), definition.Label),
                definition.Action,
                definition.Enabled,
                definition.Direction));
        }

        return result;
    }

    private void Send(SvsapmeMachineActionKind kind, int slotIndex = -1, int direction = -1)
    {
        var request = SvsapmeMachineActionRequestFactory.Create(this.MachineGuid, kind, slotIndex, this.requestedOffset, PageSize);
        request.Direction = direction;
        if (this.sendAction(request))
            Game1.playSound("shwip");
    }

    private void RequestPage(int offset)
    {
        offset = Math.Clamp(offset, 0, this.GetLastPageOffset());
        if (offset == this.requestedOffset && Game1.ticks - this.lastRefreshTick < RefreshIntervalTicks)
            return;

        this.requestedOffset = offset;
        this.lastRefreshTick = Game1.ticks;
        if (this.requestSnapshot(this.MachineGuid, offset, PageSize))
            Game1.playSound("shwip");
    }

    private (Rectangle Left, Rectangle Center, Rectangle Right) GetThreeColumns()
    {
        var content = this.ContentBounds;
        const int sideWidth = 154;
        const int gap = 12;
        var centerWidth = content.Width - sideWidth * 2 - gap * 2;
        return (
            new Rectangle(content.X, content.Y, sideWidth, content.Height),
            new Rectangle(content.X + sideWidth + gap, content.Y, centerWidth, content.Height),
            new Rectangle(content.Right - sideWidth, content.Y, sideWidth, content.Height));
    }

    private Rectangle WorkGridBounds
    {
        get
        {
            var (_, center, _) = this.GetThreeColumns();
            const int columns = 6;
            const int rows = 5;
            return new Rectangle(
                center.X + (center.Width - columns * CellSize) / 2,
                center.Y + 34,
                columns * CellSize,
                rows * CellSize);
        }
    }

    private int HitWorkCell(int x, int y)
    {
        var grid = this.WorkGridBounds;
        if (!grid.Contains(x, y))
            return -1;

        var column = (x - grid.X) / CellSize;
        var row = (y - grid.Y) / CellSize;
        var index = row * 6 + column;
        return index is >= 0 and < PageSize ? index : -1;
    }

    private static Rectangle GetEjectButtonBounds(Rectangle slotBounds)
    {
        return new Rectangle(slotBounds.Right - 19, slotBounds.Bottom - 22, 14, 14);
    }

    private static void DrawEjectButton(SpriteBatch b, Rectangle bounds)
    {
        b.Draw(Game1.staminaRect, bounds, Color.Black * 0.72f);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X + 2, bounds.Y + 6, 10, 2), Color.White);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X + 6, bounds.Y + 2, 2, 8), Color.White);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X + 4, bounds.Y + 4, 2, 2), Color.White);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X + 8, bounds.Y + 4, 2, 2), Color.White);
    }

    private int HitFilterCell(int x, int y)
    {
        if (this.snapshot.MenuKind != SvsapmeMachineMenuKind.PoweredTransfer)
            return -1;

        var panel = new Rectangle(this.ContentBounds.X, this.ContentBounds.Y, 230, this.ContentBounds.Height);
        for (var i = 0; i < 9; i++)
        {
            if (GetFilterCell(panel, i).Contains(x, y))
                return i;
        }

        return -1;
    }

    private int HitUpgradeCell(int x, int y)
    {
        var powered = this.snapshot.PoweredTransfer;
        if (powered is null)
            return -1;

        var content = this.ContentBounds;
        var statusPanel = new Rectangle(content.X + 246, content.Y, content.Width - 246, content.Height);
        var yPosition = statusPanel.Y + 154;
        for (var i = 0; i < powered.UpgradeSlotCapacity; i++)
        {
            if (new Rectangle(statusPanel.X + 18 + i * 58, yPosition, 52, 52).Contains(x, y))
                return i;
        }

        return -1;
    }

    private Rectangle GetInventorySlotBounds(int index)
    {
        const int columns = 12;
        var column = index % columns;
        var row = index / columns;
        return new Rectangle(this.InventoryBounds.X + column * InventoryCellSize, this.InventoryBounds.Y + row * InventoryCellSize, 48, 48);
    }

    private int HitInventorySlot(int x, int y)
    {
        if (!this.InventoryBounds.Contains(x, y))
            return -1;

        var column = (x - this.InventoryBounds.X) / InventoryCellSize;
        var row = (y - this.InventoryBounds.Y) / InventoryCellSize;
        var index = row * 12 + column;
        return index >= 0 && index < Math.Min(36, Game1.player.Items.Count) ? index : -1;
    }

    private int GetCapacity()
    {
        return this.snapshot.MenuKind switch
        {
            SvsapmeMachineMenuKind.Farm => this.snapshot.Farm?.PlotCapacity ?? 0,
            SvsapmeMachineMenuKind.Processor => this.snapshot.Processor?.SlotCapacity ?? 0,
            _ => PageSize
        };
    }

    private int GetLastPageOffset()
    {
        var capacity = this.GetCapacity();
        return capacity <= 0 ? 0 : Math.Max(0, ((capacity - 1) / PageSize) * PageSize);
    }

    private static int ResolveOffset(SvsapmeMachineSnapshotResponse snapshot)
    {
        return Math.Max(0, snapshot.MenuKind switch
        {
            SvsapmeMachineMenuKind.Farm => snapshot.Farm?.Offset ?? 0,
            SvsapmeMachineMenuKind.Processor => snapshot.Processor?.Offset ?? 0,
            _ => 0
        });
    }

    private static RemoteActionButton MakeButton(Rectangle panel, int yOffset, string label, SvsapmeMachineActionKind action, bool enabled = true, int direction = -1)
    {
        return new RemoteActionButton(
            new ClickableComponent(new Rectangle(panel.X + 10, panel.Y + yOffset, panel.Width - 20, 34), action.ToString(), label),
            action,
            enabled,
            direction);
    }

    private static Rectangle GetGridCell(Rectangle grid, int index)
    {
        return new Rectangle(grid.X + index % 6 * CellSize, grid.Y + index / 6 * CellSize, CellSize - 4, CellSize - 4);
    }

    private static Rectangle GetFilterCell(Rectangle panel, int index)
    {
        return new Rectangle(panel.X + 28 + index % 3 * 58, panel.Y + 46 + index / 3 * 58, 52, 52);
    }

    private static Rectangle GetFarmModuleCell(Rectangle center, int capacity, int index)
    {
        return new Rectangle(center.Right - 8 - capacity * 42 + index * 42, center.Y - 44, 38, 38);
    }

    private static void DrawPanel(SpriteBatch b, Rectangle bounds)
    {
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, PanelSource, bounds.X, bounds.Y, bounds.Width, bounds.Height, Color.White * 0.9f, 1f, false);
    }

    private static void DrawSectionTitle(SpriteBatch b, Rectangle panel, string text)
    {
        SvsapmeUiText.DrawFittedLine(b, text, new Rectangle(panel.X + 10, panel.Y + 8, panel.Width - 20, 24), Game1.textColor);
    }

    private static void DrawWorkCell(SpriteBatch b, Rectangle bounds, PixelStatus status)
    {
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, PanelSource, bounds.X, bounds.Y, bounds.Width, bounds.Height, Color.White * 0.82f, 1f, false);
        SvsapmeUiText.DrawSlotStatusLine(b, bounds, status);
    }

    private static void DrawSlot(SpriteBatch b, Rectangle bounds, BufferedItemStack? stack, PixelStatus status)
    {
        DrawWorkCell(b, bounds, status);
        DrawBufferedItem(b, stack, bounds, 0.7f);
    }

    private static void DrawBufferedItem(SpriteBatch b, BufferedItemStack? stack, Rectangle bounds, float scale)
    {
        if (stack is null || string.IsNullOrWhiteSpace(stack.QualifiedItemId) || stack.Stack <= 0)
            return;

        try
        {
            var item = BufferedItemCodec.CreateItem(stack);
            item.drawInMenu(b, new Vector2(bounds.X + 5, bounds.Y + 4), scale, 1f, 0.88f, StackDrawType.Draw, Color.White, true);
        }
        catch
        {
            // A missing content pack item should not make the remote control unusable.
        }
    }

    private static void DrawQualifiedItem(SpriteBatch b, string qualifiedItemId, Rectangle bounds, float scale, Color? tint = null)
    {
        if (string.IsNullOrWhiteSpace(qualifiedItemId))
            return;

        try
        {
            ItemRegistry.Create(qualifiedItemId).drawInMenu(
                b,
                new Vector2(bounds.X + 5, bounds.Y + 4),
                scale,
                1f,
                0.86f,
                StackDrawType.Hide,
                tint ?? Color.White,
                true);
        }
        catch
        {
            // A missing content pack item should not make the remote control unusable.
        }
    }

    private static void DrawProgress(SpriteBatch b, Rectangle bounds, long progress, long required)
    {
        var ratio = required <= 0 ? 0m : Math.Clamp(progress / (decimal)required, 0m, 1m);
        var track = new Rectangle(bounds.X + 4, bounds.Bottom - 8, bounds.Width - 8, 3);
        b.Draw(Game1.staminaRect, track, Color.Black * 0.4f);
        if (ratio > 0m)
            b.Draw(Game1.staminaRect, new Rectangle(track.X, track.Y, Math.Max(1, (int)(track.Width * ratio)), track.Height), Color.LimeGreen * 0.9f);
    }

    private static void DrawSelection(SpriteBatch b, Rectangle bounds)
    {
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, bounds.Width, 2), Color.Gold);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Bottom - 2, bounds.Width, 2), Color.Gold);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, 2, bounds.Height), Color.Gold);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.Right - 2, bounds.Y, 2, bounds.Height), Color.Gold);
    }

    private static void DrawButton(SpriteBatch b, ClickableComponent button, bool enabled)
    {
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, PanelSource, button.bounds.X, button.bounds.Y, button.bounds.Width, button.bounds.Height, enabled ? Color.White : Color.Gray * 0.7f, 1f, false);
        SvsapmeUiText.DrawFittedLine(b, button.label, new Rectangle(button.bounds.X + 8, button.bounds.Y + 4, button.bounds.Width - 16, button.bounds.Height - 8), enabled ? Game1.textColor : Color.Gray);
    }

    private static string FormatItem(string preferredId, string fallbackId)
    {
        var qualifiedItemId = string.IsNullOrWhiteSpace(preferredId) ? fallbackId : preferredId;
        if (string.IsNullOrWhiteSpace(qualifiedItemId))
            return ModText.Get("ui.common.empty", "Empty");

        try
        {
            return ItemRegistry.Create(qualifiedItemId).DisplayName;
        }
        catch
        {
            return qualifiedItemId;
        }
    }

    private static string FormatBufferedItem(BufferedItemStack stack)
    {
        try
        {
            return BufferedItemCodec.CreateItem(stack).DisplayName;
        }
        catch
        {
            return stack.QualifiedItemId;
        }
    }

    private static string FormatDirection(int direction)
    {
        return direction switch
        {
            0 => ModText.Get("ui.powered.direction.up", "Up"),
            1 => ModText.Get("ui.powered.direction.right", "Right"),
            2 => ModText.Get("ui.powered.direction.down", "Down"),
            3 => ModText.Get("ui.powered.direction.left", "Left"),
            _ => ModText.Get("ui.powered.direction.none", "Not set")
        };
    }

    private static string FormatWh(long value)
    {
        return Math.Abs(value) >= 1000 ? $"{value / 1000m:0.00} kWh" : $"{value:N0} Wh";
    }

    private static string FormatSignedWh(long value)
    {
        return value >= 0 ? "+" + FormatWh(value) : "-" + FormatWh(Math.Abs(value));
    }

    internal static bool PoweredLayoutFits(int menuWidth, int contentHeight)
    {
        var contentWidth = menuWidth - Pad * 2;
        var statusWidth = contentWidth - 246;
        var upgradeRight = 18 + (MachineRuntimeService.PoweredTransferUpgradeSlotCount - 1) * 58 + 52;
        const int buttonBottom = 218 + 2 * 40 + 34;
        return statusWidth >= 188
            && upgradeRight <= statusWidth
            && buttonBottom <= contentHeight;
    }

    private static int GetMenuWidth() => Math.Min(980, Math.Max(640, Game1.uiViewport.Width - 48));

    private static int GetMenuHeight() => Math.Min(700, Math.Max(600, Game1.uiViewport.Height - 48));

    private sealed record RemoteActionButton(
        ClickableComponent Component,
        SvsapmeMachineActionKind ActionKind,
        bool Enabled,
        int Direction = -1);
}
