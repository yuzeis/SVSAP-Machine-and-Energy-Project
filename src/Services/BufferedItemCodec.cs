using SVSAPME.Models;
using StardewValley;
using StardewValley.Objects;
using SObject = StardewValley.Object;

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
            PreservedParentSheetIndex = item is SObject obj ? obj.preservedParentSheetIndex.Value : string.Empty,
            PreserveType = item is SObject preserveObject && preserveObject.preserve.Value.HasValue
                ? (int)preserveObject.preserve.Value.Value
                : null,
            Price = item is SObject priceObject ? priceObject.Price : null,
            Edibility = item is SObject edibleObject ? edibleObject.Edibility : null,
            Category = item is SObject categoryObject ? categoryObject.Category : null,
            Type = item is SObject typeObject ? typeObject.Type : string.Empty,
            Name = item is SObject nameObject ? nameObject.Name : item.Name,
            Color = item is ColoredObject colored ? colored.color.Value.PackedValue : null,
            ModData = item.modData.Pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };
    }

    public static Item CreateItem(BufferedItemStack stack)
    {
        var item = ItemRegistry.Create(stack.QualifiedItemId, Math.Max(1, stack.Stack), stack.Quality);
        if (item is SObject obj)
        {
            obj.Quality = stack.Quality;
            if (!string.IsNullOrWhiteSpace(stack.PreservedParentSheetIndex))
                obj.preservedParentSheetIndex.Value = stack.PreservedParentSheetIndex;
            if (stack.PreserveType.HasValue)
                obj.preserve.Value = (SObject.PreserveType)stack.PreserveType.Value;
            if (stack.Price.HasValue)
                obj.Price = stack.Price.Value;
            if (stack.Edibility.HasValue)
                obj.Edibility = stack.Edibility.Value;
            if (stack.Category.HasValue)
                obj.Category = stack.Category.Value;
            if (!string.IsNullOrWhiteSpace(stack.Type))
                obj.Type = stack.Type;
            if (!string.IsNullOrWhiteSpace(stack.Name))
                obj.Name = stack.Name;
        }

        if (item is ColoredObject colored && stack.Color is not null)
            colored.color.Value = new Microsoft.Xna.Framework.Color(stack.Color.Value);

        foreach (var pair in stack.ModData)
            item.modData[pair.Key] = pair.Value;

        return item;
    }
}
