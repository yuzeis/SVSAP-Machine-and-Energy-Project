using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace SVSAPME.UI;

internal sealed class SvsapmeStatusMenu : IClickableMenu
{
    private const int Pad = 28;
    private const int LineHeight = 28;
    private static readonly Rectangle PanelSource = new(0, 256, 60, 60);

    private readonly string title;
    private readonly Func<IReadOnlyList<string>> getLines;
    private readonly List<SvsapmeMenuAction> actions;
    private readonly List<ClickableComponent> actionButtons = new();
    private int scrollOffset;

    public SvsapmeStatusMenu(
        string title,
        Func<IReadOnlyList<string>> getLines,
        IEnumerable<SvsapmeMenuAction>? actions = null)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - GetMenuWidth()) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - GetMenuHeight()) / 2),
            width: GetMenuWidth(),
            height: GetMenuHeight(),
            showUpperRightCloseButton: true)
    {
        this.title = title;
        this.getLines = getLines;
        this.actions = actions?.ToList() ?? new List<SvsapmeMenuAction>();
        this.BuildLayout();
        this.PositionCloseButton();
    }

    private static int GetMenuWidth() => Math.Min(760, Game1.uiViewport.Width - 80);

    private static int GetMenuHeight() => Math.Min(620, Game1.uiViewport.Height - 80);

    private Rectangle ContentBounds => new(
        this.xPositionOnScreen + Pad,
        this.yPositionOnScreen + 92,
        this.width - Pad * 2,
        this.height - 166 - (this.actionButtons.Count > 0 ? 58 : 0));

    private void BuildLayout()
    {
        this.actionButtons.Clear();
        if (this.actions.Count == 0)
            return;

        var buttonWidth = Math.Min(190, Math.Max(130, (this.width - Pad * 2 - (this.actions.Count - 1) * 12) / this.actions.Count));
        var y = this.yPositionOnScreen + this.height - 70;
        var x = this.xPositionOnScreen + Pad;
        foreach (var action in this.actions)
        {
            this.actionButtons.Add(new ClickableComponent(new Rectangle(x, y, buttonWidth, 44), action.Label, action.Label));
            x += buttonWidth + 12;
        }
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

        for (var i = 0; i < this.actionButtons.Count && i < this.actions.Count; i++)
        {
            if (!this.actionButtons[i].containsPoint(x, y) || !this.actions[i].IsEnabled())
                continue;

            var message = this.actions[i].Execute();
            if (!string.IsNullOrWhiteSpace(message))
                Game1.addHUDMessage(new HUDMessage(message, HUDMessage.newQuest_type));
            Game1.playSound("smallSelect");
            return;
        }
    }

    public override void receiveScrollWheelAction(int direction)
    {
        var lines = this.GetWrappedLines();
        var visibleRows = Math.Max(1, this.ContentBounds.Height / LineHeight);
        var maxOffset = Math.Max(0, lines.Count - visibleRows);
        this.scrollOffset = direction > 0
            ? Math.Max(0, this.scrollOffset - 1)
            : Math.Min(maxOffset, this.scrollOffset + 1);
    }

    public override void draw(SpriteBatch b)
    {
        var panel = new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height);
        DrawPanel(b, panel);
        Utility.drawTextWithShadow(
            b,
            this.title,
            Game1.dialogueFont,
            new Vector2(this.xPositionOnScreen + Pad, this.yPositionOnScreen + 28),
            Game1.textColor);

        var content = this.ContentBounds;
        DrawInsetBox(b, content, Color.White * 0.88f);

        var lines = this.GetWrappedLines();
        var visibleRows = Math.Max(1, content.Height / LineHeight);
        this.scrollOffset = Math.Clamp(this.scrollOffset, 0, Math.Max(0, lines.Count - visibleRows));
        var drawCount = Math.Min(visibleRows, Math.Max(0, lines.Count - this.scrollOffset));
        for (var i = 0; i < drawCount; i++)
        {
            b.DrawString(
                Game1.smallFont,
                lines[this.scrollOffset + i],
                new Vector2(content.X + 16, content.Y + 14 + i * LineHeight),
                Game1.textColor);
        }

        if (lines.Count > visibleRows)
        {
            var marker = $"{this.scrollOffset + 1}-{this.scrollOffset + drawCount}/{lines.Count}";
            var size = Game1.smallFont.MeasureString(marker);
            b.DrawString(Game1.smallFont, marker, new Vector2(content.Right - size.X - 12, content.Bottom - size.Y - 8), Color.DimGray);
        }

        for (var i = 0; i < this.actionButtons.Count && i < this.actions.Count; i++)
            DrawButton(b, this.actionButtons[i], this.actions[i].IsEnabled());

        this.upperRightCloseButton?.draw(b);
        this.drawMouse(b);
    }

    private List<string> GetWrappedLines()
    {
        var width = Math.Max(160, this.ContentBounds.Width - 32);
        var result = new List<string>();
        foreach (var line in this.getLines())
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                result.Add(string.Empty);
                continue;
            }

            result.AddRange(WrapLine(line, width));
        }

        return result;
    }

    private static IEnumerable<string> WrapLine(string line, int maxWidth)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            yield break;

        var current = words[0];
        for (var i = 1; i < words.Length; i++)
        {
            var candidate = current + " " + words[i];
            if (Game1.smallFont.MeasureString(candidate).X <= maxWidth)
            {
                current = candidate;
                continue;
            }

            yield return current;
            current = words[i];
        }

        yield return current;
    }

    private static void DrawPanel(SpriteBatch b, Rectangle panel)
    {
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, PanelSource, panel.X, panel.Y, panel.Width, panel.Height, Color.White, 1f, true);
    }

    private static void DrawInsetBox(SpriteBatch b, Rectangle bounds, Color tint)
    {
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, PanelSource, bounds.X, bounds.Y, bounds.Width, bounds.Height, tint, 1f, false);
    }

    private static void DrawButton(SpriteBatch b, ClickableComponent button, bool enabled)
    {
        DrawInsetBox(b, button.bounds, enabled ? Color.White : Color.Gray * 0.7f);
        var color = enabled ? Game1.textColor : Color.DimGray;
        var size = Game1.smallFont.MeasureString(button.label);
        var maxWidth = Math.Max(1, button.bounds.Width - 18);
        var scale = size.X > maxWidth ? Math.Max(0.62f, maxWidth / size.X) : 1f;
        var x = button.bounds.X + (button.bounds.Width - size.X * scale) / 2f;
        var y = button.bounds.Y + (button.bounds.Height - size.Y * scale) / 2f;
        Utility.drawTextWithShadow(b, button.label, Game1.smallFont, new Vector2(x, y), color, scale);
    }
}

internal sealed class SvsapmeMenuAction
{
    private readonly Func<bool>? isEnabled;

    public SvsapmeMenuAction(string label, Func<string?> execute, Func<bool>? isEnabled = null)
    {
        this.Label = label;
        this.Execute = execute;
        this.isEnabled = isEnabled;
    }

    public string Label { get; }
    public Func<string?> Execute { get; }
    public bool IsEnabled() => this.isEnabled?.Invoke() ?? true;
}
