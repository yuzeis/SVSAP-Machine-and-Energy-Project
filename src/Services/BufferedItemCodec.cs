using SVSAPME.Models;
using StardewValley;

namespace SVSAPME.Services;

internal static class BufferedItemCodec
{
    public static BufferedItemStack FromItem(Item item)
    {
        return new BufferedItemStack
        {
            QualifiedItemId = item.QualifiedItemId,
            Stack = item.Stack,
            Quality = item.Quality,
            ModData = item.modData.Pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };
    }

    public static Item CreateItem(BufferedItemStack stack)
    {
        var item = ItemRegistry.Create(stack.QualifiedItemId, Math.Max(1, stack.Stack), stack.Quality);
        foreach (var pair in stack.ModData)
            item.modData[pair.Key] = pair.Value;

        return item;
    }
}
