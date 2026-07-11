using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;

namespace SVSAPME.Services;

internal static class ChestAccessHelper
{
    private static readonly HashSet<Chest> PendingRequests = new(ReferenceEqualityComparer.Instance);

    public static void Reset() => PendingRequests.Clear();

    public static bool IsSupportedNetworkChest(Chest chest)
    {
        return chest.SpecialChestType == Chest.SpecialChestTypes.None
            || chest.SpecialChestType.ToString().Contains("Big", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryRunWithLock(Chest chest, Action action)
    {
        if (!IsSupportedNetworkChest(chest) || IsOpenInLocalMenu(chest))
            return false;
        if (PendingRequests.Contains(chest))
            return true;

        var mutex = chest.GetMutex();
        if (mutex.IsLockHeld())
        {
            action();
            return true;
        }

        if (mutex.IsLocked())
            return false;

        PendingRequests.Add(chest);
        try
        {
            mutex.RequestLock(
                () =>
                {
                    PendingRequests.Remove(chest);
                    try
                    {
                        if (IsSupportedNetworkChest(chest) && !IsOpenInLocalMenu(chest))
                            action();
                    }
                    finally
                    {
                        if (mutex.IsLockHeld())
                            mutex.ReleaseLock();
                    }
                },
                () => PendingRequests.Remove(chest));
            return true;
        }
        catch
        {
            PendingRequests.Remove(chest);
            throw;
        }
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
