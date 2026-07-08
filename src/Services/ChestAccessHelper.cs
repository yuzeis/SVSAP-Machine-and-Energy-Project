using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;

namespace SVSAPME.Services;

internal static class ChestAccessHelper
{
    public static bool IsSupportedNetworkChest(Chest chest)
    {
        return chest.SpecialChestType == Chest.SpecialChestTypes.None
            || chest.SpecialChestType.ToString().Contains("Big", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryAcquireImmediate(Chest chest, out ChestAccessLease lease)
    {
        lease = ChestAccessLease.NoOp;

        if (!IsSupportedNetworkChest(chest) || IsOpenInLocalMenu(chest))
            return false;

        var mutex = chest.GetMutex();
        return !mutex.IsLocked() || mutex.IsLockHeld();
    }

    private static bool IsOpenInLocalMenu(Chest chest)
    {
        if (Game1.activeClickableMenu is not ItemGrabMenu menu)
            return false;

        return ReferenceEquals(menu.context, chest)
            || ReferenceEquals(menu.sourceItem, chest)
            || ReferenceEquals(menu.ItemsToGrabMenu, chest.Items);
    }
}

internal sealed class ChestAccessLease : IDisposable
{
    public static readonly ChestAccessLease NoOp = new(null);

    private readonly Action? release;
    private bool released;

    public ChestAccessLease(Action? release)
    {
        this.release = release;
    }

    public void Dispose()
    {
        if (this.released)
            return;

        this.released = true;
        this.release?.Invoke();
    }
}
