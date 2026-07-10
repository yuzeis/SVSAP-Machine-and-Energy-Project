using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace SVSAPME.UI;

internal sealed class EnergyMonitorMenu : IClickableMenu
{
    private const int Pad = 24;
    private static readonly Rectangle PanelSource = new(0, 256, 60, 60);
    private readonly Func<EnergyMonitorView> getView;
    private string? hoverTitle;
    private IReadOnlyList<string> hoverLines = Array.Empty<string>();

    public EnergyMonitorMenu(Func<EnergyMonitorView> getView)
        : base(
            Math.Max(0, (Game1.uiViewport.Width - GetMenuWidth()) / 2),
            Math.Max(0, (Game1.uiViewport.Height - GetMenuHeight()) / 2),
            GetMenuWidth(),
            GetMenuHeight(),
            true)
    {
        this.getView = getView;
        if (this.upperRightCloseButton is not null)
        {
            this.upperRightCloseButton.bounds.X = this.xPositionOnScreen + this.width - 62;
            this.upperRightCloseButton.bounds.Y = this.yPositionOnScreen + 14;
        }
    }

    public override void draw(SpriteBatch b)
    {
        var panel = new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height);
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, PanelSource, panel.X, panel.Y, panel.Width, panel.Height, Color.White, 1f, true);
        Utility.drawTextWithShadow(b, ModText.Get("ui.energyMeter.title", "Energy Meter"), Game1.dialogueFont, new Vector2(panel.X + Pad + 8, panel.Y + 24), Game1.textColor);

        var view = this.getView();
        var content = new Rectangle(panel.X + Pad, panel.Y + 86, panel.Width - Pad * 2, panel.Height - 116);
        DrawInset(b, content);

        var light = !view.Online ? PixelStatus.Offline : view.CapacityWh > 0 && view.StoredWh * 10 < view.CapacityWh ? PixelStatus.Warning : PixelStatus.Ready;
        SvsapmeUiText.DrawPixelStatusLight(b, content.X + 16, content.Y + 17, light);
        SvsapmeUiText.DrawFittedLine(b, view.StatusText, new Rectangle(content.X + 34, content.Y + 8, content.Width - 50, 28), view.Online ? Game1.textColor : Color.Crimson);

        var meter = new Rectangle(content.X + 18, content.Y + 48, content.Width - 36, 34);
        b.Draw(Game1.staminaRect, meter, Color.Black * 0.45f);
        var ratio = view.CapacityWh <= 0 ? 0m : Math.Clamp(view.StoredWh / (decimal)view.CapacityWh, 0m, 1m);
        var fill = new Rectangle(meter.X + 3, meter.Y + 3, (int)((meter.Width - 6) * ratio), meter.Height - 6);
        if (fill.Width > 0)
            b.Draw(Game1.staminaRect, fill, ratio < 0.15m ? Color.Crimson : ratio < 0.4m ? Color.Orange : Color.LimeGreen);
        SvsapmeUiText.DrawFittedLine(
            b,
            ModText.Get("ui.energyMeter.capacity", "{{stored}} / {{capacity}} kWh ({{percent}})", new { stored = (view.StoredWh / 1000m).ToString("0.00"), capacity = (view.CapacityWh / 1000m).ToString("0.00"), percent = ratio.ToString("P0") }),
            meter,
            Color.White);

        var summaryY = meter.Bottom + 14;
        SvsapmeUiText.DrawFittedLines(
            b,
            new[]
            {
                ModText.Get("ui.energyMeter.lastTick", "Last flow: +{{generated}} / -{{consumed}} / net {{net}}", new { generated = FormatWh(view.LastGeneratedWh), consumed = FormatWh(view.LastConsumedWh), net = FormatSignedWh(view.LastGeneratedWh - view.LastConsumedWh) }),
                ModText.Get("ui.energyMeter.today", "Today: +{{generated}} / -{{consumed}} / net {{net}}", new { generated = FormatWh(view.TodayGeneratedWh), consumed = FormatWh(view.TodayConsumedWh), net = FormatSignedWh(view.TodayGeneratedWh - view.TodayConsumedWh) })
            },
            new Rectangle(content.X + 18, summaryY, content.Width - 36, 58),
            Game1.textColor);

        var listY = summaryY + 66;
        var listWidth = (content.Width - 54) / 2;
        DrawDeviceList(b, new Rectangle(content.X + 18, listY, listWidth, content.Bottom - listY - 18), ModText.Get("ui.energyMeter.producers", "Producers"), view.Producers);
        DrawDeviceList(b, new Rectangle(content.X + 36 + listWidth, listY, listWidth, content.Bottom - listY - 18), ModText.Get("ui.energyMeter.consumers", "Consumers"), view.Consumers);

        this.upperRightCloseButton?.draw(b);
        if (!string.IsNullOrWhiteSpace(this.hoverTitle))
            IClickableMenu.drawHoverText(b, string.Join(Environment.NewLine, this.hoverLines), Game1.smallFont, boldTitleText: this.hoverTitle);
        this.drawMouse(b);
    }

    public override void performHoverAction(int x, int y)
    {
        this.hoverTitle = null;
        this.hoverLines = Array.Empty<string>();
        var view = this.getView();
        var content = new Rectangle(this.xPositionOnScreen + Pad, this.yPositionOnScreen + 86, this.width - Pad * 2, this.height - 116);
        var meter = new Rectangle(content.X + 18, content.Y + 48, content.Width - 36, 34);
        var listY = meter.Bottom + 80;
        var listWidth = (content.Width - 54) / 2;
        var producers = new Rectangle(content.X + 18, listY, listWidth, content.Bottom - listY - 18);
        var consumers = new Rectangle(content.X + 36 + listWidth, listY, listWidth, content.Bottom - listY - 18);
        var device = HitDeviceRow(producers, view.Producers, x, y) ?? HitDeviceRow(consumers, view.Consumers, x, y);
        if (device is null)
            return;

        this.hoverTitle = device.DisplayName;
        this.hoverLines = device.Details.Count > 0
            ? device.Details
            : new[] { ModText.Get("ui.energyMeter.deviceTotal", "Today total: {{value}}", new { value = FormatWh(device.Wh) }) };
    }

    private static EnergyMonitorDeviceView? HitDeviceRow(Rectangle bounds, IReadOnlyList<EnergyMonitorDeviceView> devices, int x, int y)
    {
        var rows = Math.Min(7, devices.Count);
        for (var index = 0; index < rows; index++)
        {
            var row = new Rectangle(bounds.X + 10, bounds.Y + 36 + index * SvsapmeUiText.SmallLineHeight, bounds.Width - 20, SvsapmeUiText.SmallLineHeight);
            if (row.Contains(x, y))
                return devices[index];
        }

        return null;
    }

    private static void DrawDeviceList(SpriteBatch b, Rectangle bounds, string title, IReadOnlyList<EnergyMonitorDeviceView> devices)
    {
        DrawInset(b, bounds);
        SvsapmeUiText.DrawFittedLine(b, title, new Rectangle(bounds.X + 10, bounds.Y + 8, bounds.Width - 20, 24), Game1.textColor);
        var lines = devices.Take(7).Select(device => ModText.Get("ui.energyMeter.deviceLine", "{{name}}: {{value}}", new { name = device.DisplayName, value = FormatWh(device.Wh) }));
        SvsapmeUiText.DrawFittedLines(b, lines, new Rectangle(bounds.X + 12, bounds.Y + 38, bounds.Width - 24, bounds.Height - 48), Game1.textColor);
    }

    private static void DrawInset(SpriteBatch b, Rectangle bounds)
    {
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, PanelSource, bounds.X, bounds.Y, bounds.Width, bounds.Height, Color.White * 0.88f, 1f, false);
    }

    private static string FormatWh(long value) => Math.Abs(value) >= 1000 ? $"{value / 1000m:0.00} kWh" : $"{value:N0} Wh";
    private static string FormatSignedWh(long value) => value >= 0 ? "+" + FormatWh(value) : "-" + FormatWh(Math.Abs(value));
    private static int GetMenuWidth() => Math.Min(760, Game1.uiViewport.Width - 48);
    private static int GetMenuHeight() => Math.Min(640, Game1.uiViewport.Height - 48);
}

internal sealed record EnergyMonitorView(
    bool Online,
    string StatusText,
    long StoredWh,
    long CapacityWh,
    long LastGeneratedWh,
    long LastConsumedWh,
    long TodayGeneratedWh,
    long TodayConsumedWh,
    string Warning,
    IReadOnlyList<EnergyMonitorDeviceView> Producers,
    IReadOnlyList<EnergyMonitorDeviceView> Consumers);

internal sealed record EnergyMonitorDeviceView(string DeviceId, string DisplayName, long Wh, IReadOnlyList<string> Details);
