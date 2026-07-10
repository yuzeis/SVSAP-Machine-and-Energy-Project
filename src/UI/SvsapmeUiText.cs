using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SVSAPME.Models;
using StardewValley;
using StardewValley.Menus;

namespace SVSAPME.UI;

internal static class SvsapmeUiText
{
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
        var pad = 24;
        var inner = new Rectangle(panel.X + pad, panel.Y + pad + 40, panel.Width - pad * 2, panel.Height - pad * 2 - 40);
        if (inner.Width > 0 && inner.Height > 0)
        {
            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), inner.X, inner.Y, inner.Width, inner.Height, new Color(42, 45, 52), 1f, false);
        }
    }

    public static int SmallLineHeight => Math.Max(22, (int)Math.Ceiling(Game1.smallFont.MeasureString("Ay").Y) + 4);

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
