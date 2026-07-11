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
    private const int Pad = SvsapmeUiText.ContentPad;
    private const int PageSize = 30;
    private const int CellSize = 50;
    private const int InventoryCellSize = 48;
    private const int RefreshIntervalTicks = 30;
    private const int SnapshotRequestTimeoutTicks = 180;
    private const int ProcessorPortSlotWidth = 62;
    private const int ProcessorPortSlotHeight = 34;
    private const int ProcessorPortSlotGap = 4;
    private const int ProcessorUpgradeSlotSize = 32;
    private const int ProcessorUpgradeSlotGap = 4;
    private const int NestedHeaderInset = 10;
    private const int WorkHeaderHeight = 92;
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
    private bool farmUprootMode;
    private int lastRefreshTick;
    private bool snapshotRequestPending;
    private int snapshotRequestAtTick;
    private string? hoverText;
    private readonly Guid menuSessionId;
    private long lastAppliedRequestSequence;
    private readonly SvsapmeItemIconCache itemIconCache = new();

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
        this.menuSessionId = snapshot.MenuSessionId;
        this.lastAppliedRequestSequence = snapshot.RequestSequence;
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

    public bool MatchesSnapshotContext(SvsapmeMachineSnapshotResponse candidate)
    {
        return RemoteSnapshotSessionRules.Matches(this.menuSessionId, candidate.MenuSessionId)
            && candidate.MachineGuid == this.MachineGuid;
    }

    public bool TryApplyRefreshSnapshot(SvsapmeMachineSnapshotResponse next)
    {
        if (!this.MatchesSnapshotContext(next))
            return false;

        if (!RemoteSnapshotSessionRules.IsNewer(this.lastAppliedRequestSequence, next.RequestSequence))
            return false;

        this.snapshotRequestPending = false;
        this.lastAppliedRequestSequence = next.RequestSequence;
        this.lastRefreshTick = Game1.ticks;
        this.ApplySnapshot(next);
        return true;
    }

    public void MarkSnapshotRequestFailed(long requestSequence)
    {
        if (!RemoteSnapshotSessionRules.IsNewer(this.lastAppliedRequestSequence, requestSequence))
            return;

        this.lastAppliedRequestSequence = requestSequence;
        this.snapshotRequestPending = false;
        this.lastRefreshTick = Game1.ticks;
    }

    public bool TryApplyActionResponse(SvsapmeMachineActionResponse response)
    {
        if (response.MachineGuid != this.MachineGuid
            || !RemoteSnapshotSessionRules.Matches(this.menuSessionId, response.MenuSessionId)
            || !RemoteSnapshotSessionRules.IsNewer(this.lastAppliedRequestSequence, response.RequestSequence))
        {
            return false;
        }

        this.lastAppliedRequestSequence = response.RequestSequence;
        this.snapshotRequestPending = false;
        this.lastRefreshTick = Game1.ticks;
        if (response.Snapshot is not null && this.MatchesSnapshotContext(response.Snapshot))
            this.ApplySnapshot(response.Snapshot);

        return true;
    }

    private Rectangle ContentBounds => new(
        this.xPositionOnScreen + Pad,
        this.yPositionOnScreen + 76,
        this.width - Pad * 2,
        this.InventoryBounds.Y - this.yPositionOnScreen - 130);

    private Rectangle InventoryBounds
    {
        get
        {
            var columns = this.InventoryColumns;
            var rows = Math.Max(3, (int)Math.Ceiling(Game1.player.Items.Count / (double)columns));
            var width = columns * InventoryCellSize;
            return new Rectangle(
                this.xPositionOnScreen + (this.width - width) / 2,
                this.yPositionOnScreen + this.height - Pad - rows * InventoryCellSize,
                width,
                rows * InventoryCellSize);
        }
    }

    private int InventoryColumns => Math.Clamp((this.width - Pad * 2) / InventoryCellSize, 4, 12);

    internal static bool ProcessorPortStripFits(int menuWidth)
    {
        var contentWidth = menuWidth - Pad * 2;
        const int gap = 12;
        var sideWidth = Math.Clamp((contentWidth - 6 * 36 - gap * 2) / 2, 136, 154);
        var centerWidth = contentWidth - sideWidth * 2 - gap * 2;
        var portWidth = 3 * ProcessorPortSlotWidth + 2 * ProcessorPortSlotGap;
        var upgradeWidth = 5 * ProcessorUpgradeSlotSize + 4 * ProcessorUpgradeSlotGap;
        return portWidth <= centerWidth - 16 && upgradeWidth <= centerWidth - 16;
    }

    internal static bool NestedWorkHeaderKeepsSafeGutter()
    {
        var visibleTopGutter = NestedHeaderInset - SvsapmeUiText.FrameBevelWidth;
        var headerItemBottom = NestedHeaderInset
            + ProcessorPortSlotHeight
            + 6
            + ProcessorUpgradeSlotSize;
        return visibleTopGutter >= 8
            && headerItemBottom + 8 <= WorkHeaderHeight;
    }

    public void ApplySnapshot(SvsapmeMachineSnapshotResponse next)
    {
        if (!next.Success || next.MachineGuid != this.MachineGuid || next.Revision < this.appliedRevision)
            return;

        this.snapshot = next;
        this.appliedRevision = next.Revision;
        this.requestedOffset = ResolveOffset(next);
        this.lastRefreshTick = Game1.ticks;
        this.snapshotRequestPending = false;
    }

    public override void update(GameTime time)
    {
        base.update(time);
        var tick = Game1.ticks;
        if (this.isPending(this.MachineGuid))
            return;

        if (this.snapshotRequestPending)
        {
            if (!RemoteSnapshotSessionRules.HasTimedOut(this.snapshotRequestAtTick, tick, SnapshotRequestTimeoutTicks))
                return;

            this.snapshotRequestPending = false;
        }

        if (tick >= this.lastRefreshTick && tick - this.lastRefreshTick < RefreshIntervalTicks)
            return;

        this.RequestSnapshot(this.requestedOffset);
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

        if (this.snapshot.MenuKind == SvsapmeMachineMenuKind.Processor)
        {
            var upgradeIndex = this.HitProcessorUpgrade(x, y);
            var processor = this.snapshot.Processor;
            if (processor is not null && upgradeIndex >= 0)
            {
                this.hoverText = upgradeIndex < processor.InstalledUpgradeQualifiedItemIds.Count
                    ? ProcessorUpgradeRules.GetEffectDescription(
                        SingleBlockProcessorRules.GetProcessorKind(this.snapshot.QualifiedItemId),
                        processor.InstalledUpgradeQualifiedItemIds[upgradeIndex])
                    : ModText.Get(
                        "ui.processor.upgrade.empty",
                        "Install a Speed or Capacity Card here; kegs also accept one Quality Card.");
                return;
            }

            var portIndex = this.HitProcessorPort(x, y);
            var ports = MachinePortCatalog.GetPorts(this.snapshot.QualifiedItemId);
            if (portIndex >= 0 && portIndex < ports.Count)
            {
                this.hoverText = FormatPortTooltip(ports[portIndex], this.snapshot.Processor!);
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
        SvsapmeUiText.DrawStardewAE2Frame(
            b,
            new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));

        var title = string.IsNullOrWhiteSpace(this.snapshot.DisplayName)
            ? ModText.Get("ui.machine.remoteTitle", "SVSAPME Machine")
            : this.snapshot.DisplayName;
        SvsapmeUiText.DrawFittedTitle(
            b,
            title,
            new Rectangle(this.xPositionOnScreen + Pad + 8, this.yPositionOnScreen + 14, this.width - Pad * 2 - 98, 52),
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
            DrawButton(
                b,
                button.Component,
                button.Enabled,
                button.ActionKind == SvsapmeMachineActionKind.UprootFarmPlot && this.farmUprootMode ? Color.LightGreen : null);

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
        var moduleCapacity = Math.Max(farm.ModuleSlotCapacity, farm.InstalledModuleQualifiedItemIds.Count);
        var moduleHeaderWidth = moduleCapacity > 0 ? moduleCapacity * 38 + 8 : 0;
        SvsapmeUiText.DrawFittedLine(
            b,
            ModText.Get("ui.common.page", "Page {{page}}/{{pages}}", new { page, pages }),
            new Rectangle(center.X + NestedHeaderInset + 2, center.Y + NestedHeaderInset, Math.Max(24, center.Width - 24 - moduleHeaderWidth), 24),
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
        var ports = MachinePortCatalog.GetPorts(this.snapshot.QualifiedItemId);
        var portStripWidth = ports.Count * ProcessorPortSlotWidth + Math.Max(0, ports.Count - 1) * ProcessorPortSlotGap;
        var pageWidth = center.Width - NestedHeaderInset * 2 - portStripWidth - 6;
        if (pageWidth >= 24)
        {
            SvsapmeUiText.DrawFittedLine(
                b,
                ModText.Get("ui.common.page", "Page {{page}}/{{pages}}", new { page, pages }),
                new Rectangle(center.X + NestedHeaderInset, center.Y + NestedHeaderInset + 4, pageWidth, 24),
                Game1.textColor);
        }
        this.DrawProcessorPorts(b, processor, center);
        this.DrawProcessorUpgrades(b, processor, center);

        SvsapmeUiText.DrawFittedLine(
            b,
            ModText.Get("ui.singleBlock.dayValue", "{{value}}g/day", new { value = processor.EstimatedDailyValue.ToString("0") }),
            new Rectangle(right.X + 10, right.Bottom - 54, right.Width - 20, 22),
            Game1.textColor);
        SvsapmeUiText.DrawFittedLine(
            b,
            ModText.Get(
                "ui.processor.upgrade.remoteStatus",
                "Speed {{percent}}% / buffer {{buffer}}",
                new
                {
                    percent = (processor.SpeedPermille / 10).ToString("N0"),
                    buffer = processor.OutputBufferCapacityItems.ToString("N0")
                }),
            new Rectangle(right.X + 10, right.Bottom - 30, right.Width - 20, 20),
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
        var compact = statusPanel.Height < 300;
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
        var statusLine = new Rectangle(statusPanel.X + 34, statusPanel.Y + (compact ? 34 : 36), statusPanel.Width - 50, compact ? 22 : 26);
        SvsapmeUiText.DrawPixelStatusLight(b, statusPanel.X + 16, statusPanel.Y + (compact ? 41 : 45), powered.NetworkOnline ? PixelStatus.Ready : PixelStatus.Offline);
        SvsapmeUiText.DrawFittedLine(
            b,
            networkStatus,
            statusLine,
            powered.NetworkOnline ? Game1.textColor : Color.Crimson);

        var meter = new Rectangle(statusPanel.X + 16, statusPanel.Y + (compact ? 58 : 66), statusPanel.Width - 32, compact ? 14 : 18);
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
        if (compact)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                SvsapmeUiText.DrawFittedLine(
                    b,
                    lines[i],
                    new Rectangle(statusPanel.X + 16, statusPanel.Y + 76 + i * 18, statusPanel.Width - 32, 18),
                    Game1.textColor);
            }
        }
        else
        {
            SvsapmeUiText.DrawFittedLines(
                b,
                lines,
                new Rectangle(statusPanel.X + 16, statusPanel.Y + 92, statusPanel.Width - 32, 54),
                Game1.textColor);
        }

        for (var i = 0; i < powered.UpgradeSlotCapacity; i++)
        {
            var cell = GetPoweredUpgradeCell(statusPanel, i);
            var qualifiedItemId = i < powered.InstalledUpgradeQualifiedItemIds.Count
                ? powered.InstalledUpgradeQualifiedItemIds[i]
                : string.Empty;
            DrawWorkCell(b, cell, string.IsNullOrWhiteSpace(qualifiedItemId) ? PixelStatus.Idle : PixelStatus.Ready);
            if (string.IsNullOrWhiteSpace(qualifiedItemId))
                SvsapmeUiText.DrawGhostUpgradeSlot(b, cell);
            else
                DrawQualifiedItem(b, qualifiedItemId, cell, compact ? 0.58f : 0.68f);
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
            ? !string.IsNullOrWhiteSpace(energy.LastWarning)
                ? energy.LastWarning
                : string.IsNullOrWhiteSpace(energy.StatusText) ? ModText.Get("ui.machine.network.online", "Network online") : energy.StatusText
            : string.IsNullOrWhiteSpace(energy.StatusText) ? ModText.Get("ui.powered.network.offline", "Power offline or no energy cell") : energy.StatusText;
        var energyStatus = ResolveEnergyStatus(energy.Online, energy.LastWarning, energy.StoredWh, energy.CapacityWh);
        SvsapmeUiText.DrawPixelStatusLight(b, content.X + 22, content.Y + 17, energyStatus);
        SvsapmeUiText.DrawFittedLine(
            b,
            status,
            new Rectangle(content.X + 40, content.Y + 8, content.Width - 62, 26),
            energyStatus == PixelStatus.Offline ? Color.Crimson : energyStatus == PixelStatus.Warning ? Color.DarkOrange : Game1.textColor);

        var meter = new Rectangle(content.X + 22, content.Y + 42, content.Width - 44, 34);
        b.Draw(Game1.staminaRect, meter, Color.Black * 0.45f);
        var ratio = energy.CapacityWh <= 0 ? 0m : Math.Clamp(energy.StoredWh / (decimal)energy.CapacityWh, 0m, 1m);
        var fillWidth = (int)((meter.Width - 6) * ratio);
        if (fillWidth > 0)
            b.Draw(Game1.staminaRect, new Rectangle(meter.X + 3, meter.Y + 3, fillWidth, meter.Height - 6), ratio < 0.15m ? Color.Crimson : ratio < 0.4m ? Color.Orange : Color.LimeGreen);
        SvsapmeUiText.DrawFittedLine(
            b,
            ModText.Get(
                "ui.energyMeter.capacity",
                "{{stored}} / {{capacity}} kWh ({{percent}})",
                new
                {
                    stored = (energy.StoredWh / 1000m).ToString("0.00"),
                    capacity = (energy.CapacityWh / 1000m).ToString("0.00"),
                    percent = ratio.ToString("P0")
                }),
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
        for (var i = 0; i < Game1.player.Items.Count; i++)
        {
            var bounds = this.GetInventorySlotBounds(i);
            IClickableMenu.drawTextureBox(b, Game1.menuTexture, PanelSource, bounds.X, bounds.Y, bounds.Width, bounds.Height, Color.White * 0.82f, 1f, false);
            var item = Game1.player.Items[i];
            SvsapmeUiText.DrawItemWithAdaptiveCount(b, item, bounds, item?.Stack ?? 0, 0.64f);
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
        var lines = devices.Take(GetVisibleEnergyRowCount(bounds, devices.Count)).Select(device => ModText.Get("ui.energyMeter.deviceLine", "{{name}}: {{value}}", new { name = device.DisplayName, value = FormatWh(device.TotalWh) }));
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
        for (var index = 0; index < GetVisibleEnergyRowCount(bounds, devices.Count); index++)
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

            if (button.ActionKind == SvsapmeMachineActionKind.UprootFarmPlot)
            {
                this.farmUprootMode = !this.farmUprootMode;
                Game1.playSound("shwip");
                return true;
            }

            if (button.ActionKind == SvsapmeMachineActionKind.ClearFarmPlots)
            {
                this.OpenRemoteClearAllConfirmation();
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

            var upgradeIndex = this.HitProcessorUpgrade(x, y);
            if (upgradeIndex >= 0 && upgradeIndex < processor.InstalledUpgradeQualifiedItemIds.Count)
            {
                this.Send(SvsapmeMachineActionKind.RemoveProcessorUpgrade, upgradeIndex);
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
                action = ProcessorUpgradeRules.IsProcessorUpgradeCard(item.QualifiedItemId)
                    ? SvsapmeMachineActionKind.InstallProcessorUpgrade
                    : SvsapmeMachineActionKind.LoadProcessorInput;
                if (action == SvsapmeMachineActionKind.InstallProcessorUpgrade)
                    count = 1;
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
            this.SendActionRequest(request, "Ship");
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
        if (this.farmUprootMode)
        {
            if (string.IsNullOrWhiteSpace(plot.SeedQualifiedItemId))
            {
                Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.farm.plotEmpty", "That farm plot is empty."), HUDMessage.error_type));
                Game1.playSound("cancel");
            }
            else
            {
                this.OpenRemoteUprootConfirmation(plot);
            }
            return;
        }

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
        this.SendActionRequest(request, "Ship");
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
            MakeButton(left, 96, ModText.Get("ui.processor.autoIn", "Auto In") + ": " + SvsapmeUiText.FormatAuto(farm.AutoPullFromNetwork), SvsapmeMachineActionKind.ToggleFarmAutoPull),
            MakeButton(left, 132, ModText.Get("ui.processor.inputMode", "Input Mode") + ": " + SvsapmeUiText.FormatInputMode(farm.InputMode), SvsapmeMachineActionKind.ToggleFarmInputMode),
            MakeButton(left, 168, ModText.Get("ui.processor.filterMode", "W/B") + ": " + SvsapmeUiText.FormatFilterMode(farm.FilterMode), SvsapmeMachineActionKind.ToggleFarmFilterMode),
            MakeButton(left, 204, ModText.Get("ui.processor.clearFilter", "Clear Filter"), SvsapmeMachineActionKind.ClearFarmFilter),
            MakeButton(right, 96, ModText.Get("ui.processor.autoOut", "Auto Out") + ": " + SvsapmeUiText.FormatAuto(farm.AutoPushOutputToNetwork), SvsapmeMachineActionKind.ToggleFarmAutoPush),
            MakeButton(right, 132, ModText.Get("ui.processor.collectAll", "Collect All"), SvsapmeMachineActionKind.CollectFarmOutput, farm.OutputBuffer.Count > 0),
            MakeButton(right, 168, ModText.Get("ui.farm.uprootMode", "Uproot"), SvsapmeMachineActionKind.UprootFarmPlot, farm.OccupiedPlots > 0),
            MakeButton(right, 204, ModText.Get("ui.farm.clearAll", "Clear All"), SvsapmeMachineActionKind.ClearFarmPlots, farm.OccupiedPlots > 0 || farm.LockedPlots > 0)
        };
    }

    private void OpenRemoteUprootConfirmation(SvsapmeFarmPlotSnapshot plot)
    {
        var lines = new List<string>
        {
            ModText.Get(
                "ui.farm.uproot.confirmBody",
                "Remove plot {{plot}} and its {{crop}} crop?",
                new
                {
                    plot = (plot.PlotIndex + 1).ToString("N0"),
                    crop = FormatItem(plot.HarvestQualifiedItemId, plot.SeedQualifiedItemId)
                }),
            ModText.Get("ui.farm.destructive.noRefund", "The crop, fertilizer, progress, and plot lock will not be returned.")
        };
        if (this.snapshot.Farm?.AutoPullFromNetwork == true)
            lines.Add(ModText.Get("ui.farm.destructive.autoPullWarning", "Automatic input is enabled, so this plot may be planted again during the next farm cycle."));

        Game1.activeClickableMenu = new SvsapmeConfirmationMenu(
            this,
            ModText.Get("ui.farm.uproot.confirmTitle", "Confirm Uproot"),
            lines,
            () => this.Send(SvsapmeMachineActionKind.UprootFarmPlot, plot.PlotIndex));
    }

    private void OpenRemoteClearAllConfirmation()
    {
        var farm = this.snapshot.Farm;
        if (farm is null || farm.OccupiedPlots <= 0 && farm.LockedPlots <= 0)
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
                new { count = farm.OccupiedPlots.ToString("N0") }),
            ModText.Get("ui.farm.destructive.noRefund", "The crop, fertilizer, progress, and plot lock will not be returned.")
        };
        if (farm.LockedPlots > 0)
        {
            lines.Add(ModText.Get(
                "ui.farm.clearAll.lockCount",
                "Plot locks to clear: {{count}}.",
                new { count = farm.LockedPlots.ToString("N0") }));
        }
        if (farm.AutoPullFromNetwork)
            lines.Add(ModText.Get("ui.farm.destructive.autoPullWarning", "Automatic input is enabled, so empty plots may be planted again during the next farm cycle."));

        Game1.activeClickableMenu = new SvsapmeConfirmationMenu(
            this,
            ModText.Get("ui.farm.clearAll.confirmTitle", "Confirm Clear All"),
            lines,
            () => this.Send(SvsapmeMachineActionKind.ClearFarmPlots));
    }

    private IReadOnlyList<RemoteActionButton> GetProcessorButtons()
    {
        var processor = this.snapshot.Processor;
        if (processor is null)
            return Array.Empty<RemoteActionButton>();

        var (left, _, right) = this.GetThreeColumns();
        return new[]
        {
            MakeButton(left, 96, ModText.Get("ui.processor.autoIn", "Auto In") + ": " + SvsapmeUiText.FormatAuto(processor.AutoPullFromNetwork), SvsapmeMachineActionKind.ToggleProcessorAutoPull),
            MakeButton(left, 132, ModText.Get("ui.processor.inputMode", "Input Mode") + ": " + SvsapmeUiText.FormatInputMode(processor.InputMode), SvsapmeMachineActionKind.ToggleProcessorInputMode),
            MakeButton(left, 168, ModText.Get("ui.processor.filterMode", "W/B") + ": " + SvsapmeUiText.FormatFilterMode(processor.FilterMode), SvsapmeMachineActionKind.ToggleProcessorFilterMode),
            MakeButton(left, 204, ModText.Get("ui.processor.clearFilter", "Clear Filter"), SvsapmeMachineActionKind.ClearProcessorFilter),
            MakeButton(left, 240, ModText.Get("ui.processor.extractInput", "Take Input"), SvsapmeMachineActionKind.ExtractProcessorInput, processor.InputBuffer.Count > 0),
            MakeButton(right, 96, ModText.Get("ui.processor.autoOut", "Auto Out") + ": " + SvsapmeUiText.FormatAuto(processor.AutoPushOutputToNetwork), SvsapmeMachineActionKind.ToggleProcessorAutoPush),
            MakeButton(right, 132, ModText.Get("ui.processor.collectAll", "Collect All"), SvsapmeMachineActionKind.CollectProcessorOutput, processor.OutputBuffer.Count > 0 || processor.Slots.Any(slot => slot.CanCollect))
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
            (Label: ModText.Get("ui.powered.rotate", "Rotate"), Action: SvsapmeMachineActionKind.SetPoweredFacingDirection, Enabled: true, Direction: GetNextFacingDirection(powered.FacingDirection)),
            (Label: ModText.Get("ui.processor.clearFilter", "Clear Filter"), Action: SvsapmeMachineActionKind.ClearPoweredFilters, Enabled: true, Direction: -1)
        };

        var compact = statusPanel.Height < 300;
        var columns = statusPanel.Width >= (compact ? 218 : 276) ? 3 : 2;
        const int gap = 8;
        var width = Math.Max(compact ? 60 : 80, (statusPanel.Width - 20 - gap * (columns - 1)) / columns);
        var rows = (int)Math.Ceiling(definitions.Length / (double)columns);
        var buttonHeight = compact ? 28 : 30;
        var rowStride = compact ? 32 : 36;
        var totalHeight = (rows - 1) * rowStride + buttonHeight;
        var preferredStartY = GetPoweredUpgradeCell(statusPanel, 0).Bottom + 4;
        var buttonStartY = Math.Min(preferredStartY, statusPanel.Bottom - totalHeight - 4);
        var result = new List<RemoteActionButton>(definitions.Length);
        for (var i = 0; i < definitions.Length; i++)
        {
            var definition = definitions[i];
            var bounds = new Rectangle(
                statusPanel.X + 10 + i % columns * (width + gap),
                buttonStartY + i / columns * rowStride,
                width,
                buttonHeight);
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
        this.SendActionRequest(request, "shwip");
    }

    private void RequestPage(int offset)
    {
        offset = Math.Clamp(offset, 0, this.GetLastPageOffset());
        var tick = Game1.ticks;
        if (offset == this.requestedOffset
            && tick >= this.lastRefreshTick
            && tick - this.lastRefreshTick < RefreshIntervalTicks)
            return;

        this.requestedOffset = offset;
        if (this.RequestSnapshot(offset))
            Game1.playSound("shwip");
    }

    private bool SendActionRequest(SvsapmeMachineActionRequest request, string successSound)
    {
        if (this.snapshotRequestPending)
        {
            Game1.playSound("cancel");
            return false;
        }

        request.MenuSessionId = this.menuSessionId;
        var sent = this.sendAction(request);
        Game1.playSound(sent ? successSound : "cancel");
        return sent;
    }

    private bool RequestSnapshot(int offset)
    {
        if (this.isPending(this.MachineGuid))
            return false;

        var tick = Game1.ticks;
        if (this.snapshotRequestPending
            && !RemoteSnapshotSessionRules.HasTimedOut(this.snapshotRequestAtTick, tick, SnapshotRequestTimeoutTicks))
        {
            return false;
        }

        this.lastRefreshTick = tick;
        this.snapshotRequestAtTick = tick;
        this.snapshotRequestPending = this.requestSnapshot(this.MachineGuid, offset, PageSize);
        return this.snapshotRequestPending;
    }

    private (Rectangle Left, Rectangle Center, Rectangle Right) GetThreeColumns()
    {
        var content = this.ContentBounds;
        const int gap = 12;
        var sideWidth = Math.Clamp((content.Width - 6 * 36 - gap * 2) / 2, 136, 154);
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
            var cellSize = Math.Clamp(
                Math.Min(
                    Math.Max(1, (center.Width - 8) / columns),
                    Math.Max(1, (center.Height - WorkHeaderHeight - 4) / rows)),
                32,
                CellSize);
            return new Rectangle(
                center.X + (center.Width - columns * cellSize) / 2,
                center.Y + WorkHeaderHeight,
                columns * cellSize,
                rows * cellSize);
        }
    }

    private int HitWorkCell(int x, int y)
    {
        var grid = this.WorkGridBounds;
        if (!grid.Contains(x, y))
            return -1;

        var cellSize = Math.Max(1, grid.Width / 6);
        var column = (x - grid.X) / cellSize;
        var row = (y - grid.Y) / cellSize;
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
        for (var i = 0; i < powered.UpgradeSlotCapacity; i++)
        {
            if (GetPoweredUpgradeCell(statusPanel, i).Contains(x, y))
                return i;
        }

        return -1;
    }

    private int HitProcessorPort(int x, int y)
    {
        if (this.snapshot.MenuKind != SvsapmeMachineMenuKind.Processor)
            return -1;

        var (_, center, _) = this.GetThreeColumns();
        var ports = MachinePortCatalog.GetPorts(this.snapshot.QualifiedItemId);
        for (var i = 0; i < ports.Count; i++)
        {
            if (GetProcessorPortCell(center, ports.Count, i).Contains(x, y))
                return i;
        }

        return -1;
    }

    private int HitProcessorUpgrade(int x, int y)
    {
        if (this.snapshot.MenuKind != SvsapmeMachineMenuKind.Processor
            || this.snapshot.Processor is not { } processor)
        {
            return -1;
        }

        var (_, center, _) = this.GetThreeColumns();
        for (var i = 0; i < processor.UpgradeSlotCapacity; i++)
        {
            if (GetProcessorUpgradeCell(center, processor.UpgradeSlotCapacity, i).Contains(x, y))
                return i;
        }

        return -1;
    }

    private Rectangle GetInventorySlotBounds(int index)
    {
        var columns = this.InventoryColumns;
        var column = index % columns;
        var row = index / columns;
        return new Rectangle(this.InventoryBounds.X + column * InventoryCellSize, this.InventoryBounds.Y + row * InventoryCellSize, InventoryCellSize - 4, InventoryCellSize - 4);
    }

    private int HitInventorySlot(int x, int y)
    {
        if (!this.InventoryBounds.Contains(x, y))
            return -1;

        var column = (x - this.InventoryBounds.X) / InventoryCellSize;
        var row = (y - this.InventoryBounds.Y) / InventoryCellSize;
        var index = row * this.InventoryColumns + column;
        return index >= 0 && index < Game1.player.Items.Count ? index : -1;
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
        var cellSize = Math.Max(1, grid.Width / 6);
        return new Rectangle(grid.X + index % 6 * cellSize, grid.Y + index / 6 * cellSize, cellSize - 4, cellSize - 4);
    }

    private static Rectangle GetFilterCell(Rectangle panel, int index)
    {
        return new Rectangle(panel.X + 28 + index % 3 * 58, panel.Y + 46 + index / 3 * 58, 52, 52);
    }

    private static Rectangle GetPoweredUpgradeCell(Rectangle statusPanel, int index)
    {
        var compact = statusPanel.Height < 300;
        var size = compact ? 44 : 52;
        var stride = compact ? 48 : 58;
        return new Rectangle(statusPanel.X + 18 + index * stride, statusPanel.Y + (compact ? 134 : 154), size, size);
    }

    private static Rectangle GetFarmModuleCell(Rectangle center, int capacity, int index)
    {
        return new Rectangle(center.Right - NestedHeaderInset - capacity * 38 + index * 38, center.Y + NestedHeaderInset, 34, 34);
    }

    private static Rectangle GetProcessorPortCell(Rectangle center, int count, int index)
    {
        var totalWidth = count * ProcessorPortSlotWidth + Math.Max(0, count - 1) * ProcessorPortSlotGap;
        return new Rectangle(
            center.Right - NestedHeaderInset - totalWidth + index * (ProcessorPortSlotWidth + ProcessorPortSlotGap),
            center.Y + NestedHeaderInset,
            ProcessorPortSlotWidth,
            ProcessorPortSlotHeight);
    }

    private static Rectangle GetProcessorUpgradeCell(Rectangle center, int count, int index)
    {
        var totalWidth = count * ProcessorUpgradeSlotSize + Math.Max(0, count - 1) * ProcessorUpgradeSlotGap;
        return new Rectangle(
            center.X + (center.Width - totalWidth) / 2 + index * (ProcessorUpgradeSlotSize + ProcessorUpgradeSlotGap),
            center.Y + NestedHeaderInset + ProcessorPortSlotHeight + 6,
            ProcessorUpgradeSlotSize,
            ProcessorUpgradeSlotSize);
    }

    private void DrawProcessorPorts(SpriteBatch b, SvsapmeProcessorMenuSnapshot processor, Rectangle center)
    {
        var ports = MachinePortCatalog.GetPorts(this.snapshot.QualifiedItemId);
        for (var i = 0; i < ports.Count; i++)
        {
            var bounds = GetProcessorPortCell(center, ports.Count, i);
            var status = GetPortStatus(ports[i], processor);
            DrawWorkCell(b, bounds, status);
            SvsapmeUiText.DrawPixelStatusLight(b, bounds.X + 5, bounds.Y + 12, status);
            SvsapmeUiText.DrawFittedLine(
                b,
                GetPortShortLabel(ports[i]),
                new Rectangle(bounds.X + 20, bounds.Y + 5, bounds.Width - 24, bounds.Height - 10),
                Game1.textColor,
                0.56f);
        }
    }

    private void DrawProcessorUpgrades(SpriteBatch b, SvsapmeProcessorMenuSnapshot processor, Rectangle center)
    {
        for (var i = 0; i < processor.UpgradeSlotCapacity; i++)
        {
            var bounds = GetProcessorUpgradeCell(center, processor.UpgradeSlotCapacity, i);
            var installed = i < processor.InstalledUpgradeQualifiedItemIds.Count;
            DrawWorkCell(b, bounds, installed ? PixelStatus.Ready : PixelStatus.Idle);
            if (installed)
            {
                DrawQualifiedItem(b, processor.InstalledUpgradeQualifiedItemIds[i], bounds, 0.4f);
            }
            else
            {
                SvsapmeUiText.DrawGhostUpgradeSlot(b, bounds);
            }
        }
    }

    private static PixelStatus GetPortStatus(MachinePortDefinition port, SvsapmeProcessorMenuSnapshot processor)
    {
        var active = processor.Slots.Count(slot => !slot.Ready && (slot.Input is not null || slot.Output is not null));
        var ready = processor.Slots.Count(slot => slot.Ready);
        return port.RoleKey switch
        {
            "ui.machine.port.itemIn" => processor.InputBuffer.Count > 0
                ? PixelStatus.Ready
                : processor.AutoPullFromNetwork
                    ? processor.NetworkOnline ? PixelStatus.Processing : PixelStatus.Offline
                    : PixelStatus.Idle,
            "ui.machine.port.itemOut" => ready > 0 || processor.OutputBuffer.Count > 0
                ? PixelStatus.Ready
                : active > 0
                    ? PixelStatus.Processing
                    : PixelStatus.Idle,
            "ui.machine.port.energyIn" => GetEnergyPortStatus(processor, active),
            _ => PixelStatus.Idle
        };
    }

    private static PixelStatus GetEnergyPortStatus(SvsapmeProcessorMenuSnapshot processor, int active)
    {
        if (active <= 0)
            return PixelStatus.Idle;
        if (!processor.NetworkOnline)
            return PixelStatus.Offline;
        if (!processor.EnergyOnline)
            return PixelStatus.Error;
        return processor.StoredWh < processor.RequiredWhForNextStep
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

    private static string FormatPortTooltip(MachinePortDefinition port, SvsapmeProcessorMenuSnapshot processor)
    {
        var status = GetPortStatus(port, processor);
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
                new { stored = processor.StoredWh.ToString("N0"), required = processor.RequiredWhForNextStep.ToString("N0") })
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

    private static void DrawPanel(SpriteBatch b, Rectangle bounds)
    {
        SvsapmeUiText.DrawWorkspacePanel(b, bounds, Color.White * 0.9f);
    }

    private static void DrawSectionTitle(SpriteBatch b, Rectangle panel, string text)
    {
        SvsapmeUiText.DrawFittedLine(b, text, new Rectangle(panel.X + 12, panel.Y + 10, panel.Width - 24, 24), Game1.textColor);
    }

    private static void DrawWorkCell(SpriteBatch b, Rectangle bounds, PixelStatus status)
    {
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, PanelSource, bounds.X, bounds.Y, bounds.Width, bounds.Height, Color.White * 0.82f, 1f, false);
        SvsapmeUiText.DrawSlotStatusLine(b, bounds, status);
    }

    private void DrawSlot(SpriteBatch b, Rectangle bounds, BufferedItemStack? stack, PixelStatus status)
    {
        DrawWorkCell(b, bounds, status);
        DrawBufferedItem(b, stack, bounds, 0.7f);
    }

    private void DrawBufferedItem(SpriteBatch b, BufferedItemStack? stack, Rectangle bounds, float scale)
    {
        if (stack is null || string.IsNullOrWhiteSpace(stack.QualifiedItemId) || stack.Stack <= 0)
            return;

        SvsapmeUiText.DrawItemWithAdaptiveCount(b, this.itemIconCache.GetOrCreate(stack), bounds, stack.Stack, scale);
    }

    private void DrawQualifiedItem(SpriteBatch b, string qualifiedItemId, Rectangle bounds, float scale, Color? tint = null)
    {
        if (string.IsNullOrWhiteSpace(qualifiedItemId))
            return;

        SvsapmeUiText.DrawItemWithAdaptiveCount(b, this.itemIconCache.GetOrCreate(qualifiedItemId), bounds, 1, scale, tint);
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

    private static void DrawButton(SpriteBatch b, ClickableComponent button, bool enabled, Color? tint = null)
    {
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, PanelSource, button.bounds.X, button.bounds.Y, button.bounds.Width, button.bounds.Height, enabled ? tint ?? Color.White : Color.Gray * 0.7f, 1f, false);
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
            _ => ModText.Get("ui.machine.powered.direction.all", "All")
        };
    }

    internal static int GetNextFacingDirection(int current)
    {
        return current is >= -1 and < 3 ? current + 1 : -1;
    }

    internal static PixelStatus ResolveEnergyStatus(bool online, string? warning, long storedWh, long capacityWh)
    {
        if (!online)
            return PixelStatus.Offline;
        if (!string.IsNullOrWhiteSpace(warning)
            || capacityWh > 0 && storedWh / (decimal)capacityWh < 0.1m)
            return PixelStatus.Warning;
        return PixelStatus.Ready;
    }

    internal static int GetVisibleEnergyRowCount(Rectangle bounds, int availableCount)
    {
        var availableHeight = Math.Max(0, bounds.Height - 46);
        return Math.Min(Math.Max(0, availableCount), availableHeight / Math.Max(1, SvsapmeUiText.SmallLineHeight));
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
        var statusPanel = new Rectangle(0, 0, statusWidth, contentHeight);
        var compact = contentHeight < 300;
        var lastUpgrade = GetPoweredUpgradeCell(statusPanel, MachineRuntimeService.PoweredTransferUpgradeSlotCount - 1);
        var buttonColumns = statusWidth >= (compact ? 218 : 276) ? 3 : 2;
        var buttonRows = (int)Math.Ceiling(5d / buttonColumns);
        var buttonHeight = compact ? 28 : 30;
        var rowStride = compact ? 32 : 36;
        var totalButtonHeight = (buttonRows - 1) * rowStride + buttonHeight;
        var buttonStart = Math.Min(lastUpgrade.Bottom + 4, contentHeight - totalButtonHeight - 4);
        var buttonBottom = buttonStart + totalButtonHeight;
        var lastFilter = GetFilterCell(new Rectangle(0, 0, 230, contentHeight), 8);
        return statusWidth >= 188
            && lastFilter.Bottom <= contentHeight
            && lastUpgrade.Right <= statusWidth
            && buttonStart >= lastUpgrade.Bottom + 4
            && buttonBottom <= contentHeight - 4;
    }

    private static int GetMenuWidth() => Math.Max(1, Math.Min(980, Game1.uiViewport.Width - 32));

    private static int GetMenuHeight() => Math.Max(1, Math.Min(700, Game1.uiViewport.Height - 32));

    private sealed record RemoteActionButton(
        ClickableComponent Component,
        SvsapmeMachineActionKind ActionKind,
        bool Enabled,
        int Direction = -1);
}
