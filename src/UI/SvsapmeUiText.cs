using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SVSAPME.Models;
using SVSAPME.Services;
using StardewValley;
using StardewValley.Menus;

namespace SVSAPME.UI;

internal static class SvsapmeUiText
{
    public const int FramePad = 24;
    public const int FrameHeaderHeight = 40;
    public const int FrameContentInset = 12;
    public const int FrameBevelWidth = 2;
    public static int VisualContentGutter => FrameContentInset - FrameBevelWidth;
    public const int ContentPad = FramePad + FrameContentInset;
    public const int ContentTopOffset = FramePad + FrameHeaderHeight + FrameContentInset;

    private static readonly List<string> SlotGeometryViolations = new();
    private static readonly Rectangle PanelCenterSource = new(12, 268, 36, 36);
    private static int qualityOverlayDrawCount;

    public static void DrawPixelStatusLight(SpriteBatch b, int x, int y, PixelStatus status)
    {
        var lightColor = GetStatusColor(status);

        // Draw 8x8 round status light
        // Draw shadow/outline first
        b.Draw(Game1.staminaRect, new Rectangle(x - 1, y - 1, 10, 10), Color.Black * 0.45f);
        // Draw base color
        b.Draw(Game1.staminaRect, new Rectangle(x, y, 8, 8), lightColor);
        // Draw inner glow/specular dot
        b.Draw(Game1.staminaRect, new Rectangle(x + 1, y + 1, 2, 2), Color.White * 0.7f);
    }

    public static void DrawSlotStatusLine(SpriteBatch b, Rectangle slotBounds, PixelStatus status)
    {
        var lightColor = GetStatusColor(status);
        b.Draw(Game1.staminaRect, new Rectangle(slotBounds.X + 2, slotBounds.Bottom - 4, slotBounds.Width - 4, 2), lightColor * 0.85f);
    }

    public static void DrawGhostUpgradeSlot(SpriteBatch b, Rectangle slot)
    {
        var inner = new Rectangle(slot.X + 8, slot.Y + 8, Math.Max(1, slot.Width - 16), Math.Max(1, slot.Height - 16));
        b.Draw(Game1.staminaRect, inner, Color.SteelBlue * 0.12f);
        b.Draw(Game1.staminaRect, new Rectangle(inner.X + 4, inner.Y + 4, Math.Max(1, inner.Width - 8), 3), Color.White * 0.14f);
        b.Draw(Game1.staminaRect, new Rectangle(inner.X + 4, inner.Bottom - 7, Math.Max(1, inner.Width - 8), 3), Color.White * 0.1f);
    }

    public static void DrawPixelLock(SpriteBatch b, int x, int y)
    {
        var color = Color.DarkOrange;
        b.Draw(Game1.staminaRect, new Rectangle(x + 2, y, 4, 1), color);
        b.Draw(Game1.staminaRect, new Rectangle(x + 1, y + 1, 1, 3), color);
        b.Draw(Game1.staminaRect, new Rectangle(x + 6, y + 1, 1, 3), color);
        b.Draw(Game1.staminaRect, new Rectangle(x, y + 3, 8, 6), color * 0.9f);
        b.Draw(Game1.staminaRect, new Rectangle(x + 3, y + 5, 2, 2), Color.Black * 0.55f);
    }

    private static Color GetStatusColor(PixelStatus status)
    {
        return status switch
        {
            PixelStatus.Idle => Color.Gray * 0.6f,
            PixelStatus.Ready => Color.LimeGreen,
            PixelStatus.Processing => Color.Yellow,
            PixelStatus.Warning => Color.Orange,
            PixelStatus.Error => Color.Crimson,
            PixelStatus.Offline => Color.Red * 0.6f,
            PixelStatus.Disabled => Color.DarkGray * 0.4f,
            _ => Color.Gray
        };
    }

    public static void DrawStardewAE2Frame(SpriteBatch b, Rectangle panel)
    {
        // 1. Draw outer wood border panel
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), panel.X, panel.Y, panel.Width, panel.Height, Color.White, 1f, true);

        // 2. Draw inner metal tech frame with Cool Grey background
        var inner = new Rectangle(
            panel.X + FramePad,
            panel.Y + FramePad + FrameHeaderHeight,
            panel.Width - FramePad * 2,
            panel.Height - FramePad * 2 - FrameHeaderHeight);
        if (inner.Width > 0 && inner.Height > 0)
        {
            DrawWorkspacePanel(b, inner, new Color(202, 212, 218));
        }
    }

    public static void DrawWorkspacePanel(SpriteBatch b, Rectangle bounds, Color tint)
    {
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), bounds.X, bounds.Y, bounds.Width, bounds.Height, tint, 1f, false);
        var center = new Rectangle(
            bounds.X + FrameBevelWidth,
            bounds.Y + FrameBevelWidth,
            Math.Max(1, bounds.Width - FrameBevelWidth * 2),
            Math.Max(1, bounds.Height - FrameBevelWidth * 2));
        b.Draw(Game1.menuTexture, center, PanelCenterSource, tint);
    }

    public static Rectangle GetFrameContentBounds(Rectangle panel)
    {
        return new Rectangle(
            panel.X + ContentPad,
            panel.Y + ContentTopOffset,
            Math.Max(1, panel.Width - ContentPad * 2),
            Math.Max(1, panel.Height - ContentTopOffset - ContentPad));
    }

    public static int SmallLineHeight => Math.Max(22, (int)Math.Ceiling(Game1.smallFont.MeasureString("Ay").Y) + 4);

    public static void DrawFittedTitle(SpriteBatch b, string text, Rectangle bounds, Color color)
    {
        if (string.IsNullOrWhiteSpace(text) || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var value = text.Trim();
        var size = Game1.dialogueFont.MeasureString(value);
        if (size.X <= 0 || size.Y <= 0)
            return;

        var scale = Math.Min(1f, Math.Min(bounds.Width / size.X, bounds.Height / size.Y));
        if (scale < 0.68f)
        {
            scale = 0.68f;
            value = Ellipsize(Game1.dialogueFont, value, bounds.Width / scale);
            size = Game1.dialogueFont.MeasureString(value);
        }

        var y = bounds.Y + Math.Max(0, (bounds.Height - size.Y * scale) / 2f);
        Utility.drawTextWithShadow(b, value, Game1.dialogueFont, new Vector2(bounds.X, y), color, scale);
    }

    public static void DrawFittedLine(SpriteBatch b, string text, Rectangle bounds, Color color, float minScale = 0.62f)
    {
        if (string.IsNullOrWhiteSpace(text) || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var font = Game1.smallFont;
        var value = text.Trim();
        var size = font.MeasureString(value);
        if (size.X <= 0 || size.Y <= 0)
            return;

        var scale = Math.Min(1f, Math.Min(bounds.Width / size.X, bounds.Height / size.Y));
        if (scale < minScale)
        {
            scale = minScale;
            value = Ellipsize(font, value, bounds.Width / scale);
            size = font.MeasureString(value);
        }

        var y = bounds.Y + Math.Max(0, (bounds.Height - size.Y * scale) / 2f);
        b.DrawString(font, value, new Vector2(bounds.X, y), color, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
    }

    public static void DrawFittedLines(SpriteBatch b, IEnumerable<string> lines, Rectangle bounds, Color color)
    {
        var lineHeight = SmallLineHeight;
        var y = bounds.Y;
        foreach (var line in lines.Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            if (y + lineHeight > bounds.Bottom)
                break;

            DrawFittedLine(b, line, new Rectangle(bounds.X, y, bounds.Width, lineHeight), color);
            y += lineHeight;
        }
    }

    public static string FormatAuto(bool enabled)
    {
        return enabled
            ? ModText.Get("ui.singleBlock.auto.on", "ON")
            : ModText.Get("ui.singleBlock.auto.off", "OFF");
    }

    public static string FormatInputMode(string mode)
    {
        return mode == MachineInputModes.Filter
            ? ModText.Get("ui.singleBlock.mode.filter", "Filter")
            : ModText.Get("ui.singleBlock.mode.all", "All");
    }

    public static string FormatFilterMode(string mode)
    {
        return mode == MachineFilterModes.Blacklist
            ? ModText.Get("ui.singleBlock.filter.deny", "Deny")
            : ModText.Get("ui.singleBlock.filter.allow", "Allow");
    }

    public static string FormatFilterCount(int count)
    {
        return ModText.Get("ui.singleBlock.filter.count", "Filter {{count}}", new { count = count.ToString("N0") });
    }

    public static string FormatBufferCount(int count)
    {
        return ModText.Get("ui.singleBlock.buffer.count", "Buffer {{count}}", new { count = count.ToString("N0") });
    }

    public static string FormatInputBufferCount(int count)
    {
        return ModText.Get("ui.singleBlock.inputBuffer.count", "Input {{count}}", new { count = count.ToString("N0") });
    }

    public static string FormatFertilizerCount(int count)
    {
        return ModText.Get("ui.singleBlock.fertilizer.count", "Fert {{count}}", new { count = count.ToString("N0") });
    }

    public static string FormatReadyCount(int count)
    {
        return ModText.Get("ui.singleBlock.ready.count", "Ready {{count}}", new { count = count.ToString("N0") });
    }

    public static string FormatDayValue(decimal value)
    {
        return ModText.Get("ui.singleBlock.dayValue", "{{value}}g/day", new { value = value.ToString("0") });
    }

    public static string FormatItemCount(long count)
    {
        if (count < 1000)
            return count.ToString(CultureInfo.InvariantCulture);
        if (count < 1_000_000)
        {
            var thousands = Math.Round(count / 1000d, 1, MidpointRounding.AwayFromZero);
            if (thousands >= 1000d)
                return "1M";

            return thousands.ToString("0.#", CultureInfo.InvariantCulture) + "K";
        }

        return (count / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M";
    }

    public static Item? TryCreateItem(BufferedItemStack? stack)
    {
        if (stack is null || string.IsNullOrWhiteSpace(stack.QualifiedItemId) || stack.Stack <= 0)
            return null;

        try
        {
            return BufferedItemCodec.CreateItem(stack);
        }
        catch
        {
            return null;
        }
    }

    public static Item? TryCreateItem(string? qualifiedItemId)
    {
        if (string.IsNullOrWhiteSpace(qualifiedItemId))
            return null;

        try
        {
            return ItemRegistry.Create(qualifiedItemId);
        }
        catch
        {
            return null;
        }
    }

    public static void DrawItemWithAdaptiveCount(
        SpriteBatch b,
        Item? item,
        Rectangle bounds,
        long count,
        float maximumScale = 1f,
        Color? tint = null)
    {
        if (item is null || bounds.Width <= 0 || bounds.Height <= 0 || count <= 0)
            return;

        const int inset = 4;
        var scale = Math.Min(maximumScale, Math.Min((bounds.Width - inset * 2) / 64f, (bounds.Height - inset * 2) / 64f));
        scale = Math.Max(0.1f, scale);
        var iconSize = 64f * scale;
        var iconCenter = new Vector2(bounds.X + bounds.Width / 2f, bounds.Y + bounds.Height / 2f);
        var iconBoundsPosition = iconCenter - new Vector2(iconSize / 2f);
        // drawInMenu treats location as the top-left of an unscaled 64px menu slot.
        var menuSlotPosition = iconCenter - new Vector2(32f);
        RecordSlotGeometry(
            bounds,
            new Rectangle(
                (int)Math.Floor(iconBoundsPosition.X),
                (int)Math.Floor(iconBoundsPosition.Y),
                (int)Math.Ceiling(iconSize),
                (int)Math.Ceiling(iconSize)),
            "item");
        var originalStack = item.Stack;

        try
        {
            item.drawInMenu(
                b,
                menuSlotPosition,
                scale,
                1f,
                0.86f,
                StackDrawType.Hide,
                tint ?? Color.White,
                true);
        }
        finally
        {
            item.Stack = originalStack;
        }

        DrawItemQuality(b, item, bounds, scale, tint ?? Color.White);
        DrawCompactItemCount(b, bounds, count);
    }

    private static void DrawItemQuality(SpriteBatch b, Item item, Rectangle bounds, float itemScale, Color tint)
    {
        if (item.Quality <= 0)
            return;

        var quality = Math.Clamp(item.Quality, 1, 4);
        var pulse = quality == 4
            ? (float)((Math.Cos(Game1.currentGameTime.TotalGameTime.TotalMilliseconds * Math.PI / 512d) + 1d) * 0.05d)
            : 0f;
        var starScale = 3f * itemScale * (1f + pulse);
        var starSize = 8f * starScale;
        const int inset = 3;
        var center = new Vector2(
            bounds.Left + inset + starSize / 2f,
            bounds.Bottom - inset - starSize / 2f);
        var starBounds = new Rectangle(
            (int)Math.Floor(center.X - starSize / 2f),
            (int)Math.Floor(center.Y - starSize / 2f),
            (int)Math.Ceiling(starSize),
            (int)Math.Ceiling(starSize));
        RecordSlotGeometry(bounds, starBounds, "quality");
        qualityOverlayDrawCount++;

        b.Draw(
            Game1.mouseCursors,
            center,
            new Rectangle(338 + (quality - 1) * 8, 400, 8, 8),
            tint,
            0f,
            new Vector2(4f),
            starScale,
            SpriteEffects.None,
            0.98f);
    }

    public static void ResetSlotGeometryDiagnostics()
    {
        SlotGeometryViolations.Clear();
        qualityOverlayDrawCount = 0;
    }

    public static IReadOnlyList<string> GetSlotGeometryViolations()
    {
        return SlotGeometryViolations.ToArray();
    }

    public static int GetQualityOverlayDrawCount()
    {
        return qualityOverlayDrawCount;
    }

    internal static IReadOnlyList<Rectangle> CalculateControlButtonBounds(
        Rectangle panel,
        int topOffset,
        int count,
        int preferredHeight = 28)
    {
        if (count <= 0)
            return Array.Empty<Rectangle>();

        const int inset = 10;
        const int gap = 4;
        var top = panel.Y + Math.Max(0, topOffset);
        var availableHeight = Math.Max(1, panel.Bottom - 10 - top);
        var oneColumnHeight = count * preferredHeight + Math.Max(0, count - 1) * gap;
        var columns = oneColumnHeight <= availableHeight ? 1 : 2;
        var rows = (int)Math.Ceiling(count / (double)columns);
        var width = Math.Max(1, (panel.Width - inset * 2 - gap * (columns - 1)) / columns);
        var height = Math.Max(1, Math.Min(preferredHeight, (availableHeight - gap * (rows - 1)) / rows));
        var result = new Rectangle[count];
        for (var index = 0; index < count; index++)
        {
            var row = index / columns;
            var column = index % columns;
            result[index] = new Rectangle(
                panel.X + inset + column * (width + gap),
                top + row * (height + gap),
                width,
                height);
        }

        return result;
    }

    private static void DrawCompactItemCount(SpriteBatch b, Rectangle bounds, long count)
    {
        if (count <= 1)
            return;

        if (count < 1000)
        {
            var value = (int)count;
            var digitScale = bounds.Width >= 60 && bounds.Height >= 60 ? 3f : 2f;
            var width = Utility.getWidthOfTinyDigitString(value, digitScale);
            while (digitScale > 1f && width > bounds.Width - 8)
            {
                digitScale -= 0.25f;
                width = Utility.getWidthOfTinyDigitString(value, digitScale);
            }

            var height = (int)Math.Ceiling(8f * digitScale);
            var countBounds = new Rectangle(bounds.Right - width - 4, bounds.Bottom - height - 5, width, height);
            RecordSlotGeometry(bounds, countBounds, "count");
            Utility.drawTinyDigits(value, b, new Vector2(countBounds.X, countBounds.Y), digitScale, 0.99f, Color.White);
            return;
        }

        var text = FormatItemCount(count);
        var preferredScale = bounds.Width >= 60 ? 0.68f : 0.54f;
        var naturalSize = Game1.smallFont.MeasureString(text);
        var scale = Math.Min(preferredScale, Math.Min((bounds.Width - 8) / naturalSize.X, (bounds.Height - 8) / naturalSize.Y));
        scale = Math.Max(0.28f, scale);
        var size = naturalSize * scale;
        var position = new Vector2(bounds.Right - size.X - 4, bounds.Bottom - size.Y - 4);
        RecordSlotGeometry(
            bounds,
            new Rectangle((int)Math.Floor(position.X), (int)Math.Floor(position.Y), (int)Math.Ceiling(size.X), (int)Math.Ceiling(size.Y)),
            "compact count");
        Utility.drawTextWithShadow(
            b,
            text,
            Game1.smallFont,
            position,
            Color.White,
            scale);
    }

    private static void RecordSlotGeometry(Rectangle bounds, Rectangle content, string contentKind)
    {
        if (content.Left >= bounds.Left
            && content.Top >= bounds.Top
            && content.Right <= bounds.Right
            && content.Bottom <= bounds.Bottom)
        {
            return;
        }

        SlotGeometryViolations.Add($"{contentKind} {content} exceeds slot {bounds}");
    }

    private static string Ellipsize(SpriteFont font, string text, float maxWidth)
    {
        const string suffix = "...";
        if (font.MeasureString(text).X <= maxWidth)
            return text;

        var suffixWidth = font.MeasureString(suffix).X;
        if (suffixWidth >= maxWidth)
            return suffix;

        var value = text;
        while (value.Length > 1 && font.MeasureString(value).X + suffixWidth > maxWidth)
            value = value[..^1];

        return value + suffix;
    }
}

internal sealed class SvsapmeItemIconCache
{
    private const int MaxEntries = 512;
    private readonly Dictionary<string, Item?> entries = new(StringComparer.Ordinal);

    public Item? GetOrCreate(BufferedItemStack? stack)
    {
        if (stack is null || string.IsNullOrWhiteSpace(stack.QualifiedItemId))
            return null;

        var key = string.Join(
            "|",
            "buffered",
            stack.QualifiedItemId,
            stack.Quality,
            stack.PreservedParentSheetIndex,
            stack.PreserveType,
            stack.Price,
            stack.Edibility,
            stack.Category,
            stack.Type,
            stack.Name,
            stack.Color);
        return this.GetOrCreate(key, () => SvsapmeUiText.TryCreateItem(stack));
    }

    public Item? GetOrCreate(string? qualifiedItemId)
    {
        if (string.IsNullOrWhiteSpace(qualifiedItemId))
            return null;

        return this.GetOrCreate("item|" + qualifiedItemId, () => SvsapmeUiText.TryCreateItem(qualifiedItemId));
    }

    public void Clear() => this.entries.Clear();

    private Item? GetOrCreate(string key, Func<Item?> factory)
    {
        if (this.entries.TryGetValue(key, out var cached))
            return cached;

        if (this.entries.Count >= MaxEntries)
            this.entries.Clear();

        var created = factory();
        this.entries[key] = created;
        return created;
    }
}
