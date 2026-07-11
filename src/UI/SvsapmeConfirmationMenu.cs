using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SVSAPME.Services;
using StardewValley;
using StardewValley.Menus;

namespace SVSAPME.UI;

internal sealed class SvsapmeConfirmationMenu : IClickableMenu
{
    private const int Pad = SvsapmeUiText.ContentPad;
    private readonly IClickableMenu parent;
    private readonly string title;
    private readonly IReadOnlyList<string> lines;
    private readonly Action onConfirm;
    private readonly ClickableComponent confirmButton;
    private readonly ClickableComponent cancelButton;
    private readonly Rectangle messageBounds;
    private bool parentRestored;

    public SvsapmeConfirmationMenu(IClickableMenu parent, string title, IReadOnlyList<string> lines, Action onConfirm)
        : base(
            Math.Max(0, (Game1.uiViewport.Width - GetMenuWidth()) / 2),
            Math.Max(0, (Game1.uiViewport.Height - GetMenuHeight()) / 2),
            GetMenuWidth(),
            GetMenuHeight(),
            true)
    {
        this.parent = parent;
        this.title = title;
        this.lines = lines;
        this.onConfirm = onConfirm;

        var buttonY = this.yPositionOnScreen + this.height - Pad - 42;
        this.messageBounds = new Rectangle(
            this.xPositionOnScreen + Pad,
            this.yPositionOnScreen + 96,
            this.width - Pad * 2,
            buttonY - this.yPositionOnScreen - 112);
        this.cancelButton = new ClickableComponent(
            new Rectangle(this.xPositionOnScreen + this.width - Pad - 252, buttonY, 120, 42),
            "cancel",
            ModText.Get("ui.confirm.cancel", "Cancel"));
        this.confirmButton = new ClickableComponent(
            new Rectangle(this.xPositionOnScreen + this.width - Pad - 120, buttonY, 120, 42),
            "confirm",
            ModText.Get("ui.confirm.confirm", "Confirm"));

        if (this.upperRightCloseButton is not null)
        {
            this.upperRightCloseButton.bounds.X = this.xPositionOnScreen + this.width - 62;
            this.upperRightCloseButton.bounds.Y = this.yPositionOnScreen + 12;
        }
    }

    private static int GetMenuWidth() => Math.Max(1, Math.Min(660, Game1.uiViewport.Width - 32));

    private static int GetMenuHeight() => Math.Max(1, Math.Min(390, Game1.uiViewport.Height - 32));

    public override void draw(SpriteBatch b)
    {
        SvsapmeUiText.DrawStardewAE2Frame(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
        SvsapmeUiText.DrawFittedTitle(
            b,
            this.title,
            new Rectangle(this.xPositionOnScreen + Pad + 8, this.yPositionOnScreen + 18, this.width - Pad * 2 - 70, 48),
            Game1.textColor);
        SvsapmeUiText.DrawWorkspacePanel(b, this.messageBounds, Color.White * 0.82f);
        SvsapmeUiText.DrawFittedLines(
            b,
            this.lines,
            new Rectangle(this.messageBounds.X + 16, this.messageBounds.Y + 16, this.messageBounds.Width - 32, this.messageBounds.Height - 32),
            Game1.textColor);
        DrawButton(b, this.cancelButton, Color.White);
        DrawButton(b, this.confirmButton, Color.LightCoral);
        this.upperRightCloseButton?.draw(b);
        this.drawMouse(b);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (this.upperRightCloseButton?.containsPoint(x, y) == true || this.cancelButton.containsPoint(x, y))
        {
            this.CloseToParent("bigDeSelect");
            return;
        }

        if (!this.confirmButton.containsPoint(x, y))
            return;

        this.CloseToParent("smallSelect");
        this.onConfirm();
    }

    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape)
        {
            this.CloseToParent("bigDeSelect");
            return;
        }

        if (key == Keys.Enter)
        {
            this.CloseToParent("smallSelect");
            this.onConfirm();
            return;
        }

        base.receiveKeyPress(key);
    }

    protected override void cleanupBeforeExit()
    {
        this.RestoreParent();
    }

    private void CloseToParent(string sound)
    {
        this.RestoreParent();
        Game1.playSound(sound);
    }

    private void RestoreParent()
    {
        if (this.parentRestored)
            return;

        this.parentRestored = true;
        Game1.activeClickableMenu = this.parent;
    }

    private static void DrawButton(SpriteBatch b, ClickableComponent button, Color tint)
    {
        IClickableMenu.drawTextureBox(
            b,
            Game1.menuTexture,
            new Rectangle(0, 256, 60, 60),
            button.bounds.X,
            button.bounds.Y,
            button.bounds.Width,
            button.bounds.Height,
            tint,
            1f,
            false);
        SvsapmeUiText.DrawFittedLine(
            b,
            button.label,
            new Rectangle(button.bounds.X + 8, button.bounds.Y + 4, button.bounds.Width - 16, button.bounds.Height - 8),
            Game1.textColor);
    }
}
