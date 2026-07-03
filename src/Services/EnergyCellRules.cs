using SVSAPME.Content;

using StardewValley;

namespace SVSAPME.Services;

internal static class EnergyCellRules
{
    public static bool IsEnergyCell(string qualifiedItemId)
    {
        return qualifiedItemId is "(BC)" + ModItemCatalog.CopperEnergyCell
            or "(BC)" + ModItemCatalog.SteelEnergyCell
            or "(BC)" + ModItemCatalog.GoldEnergyCell
            or "(BC)" + ModItemCatalog.IridiumEnergyCell;
    }

    public static bool ShouldSplitChargedStack(string qualifiedItemId, int stack, long storedWh)
    {
        return IsEnergyCell(qualifiedItemId) && stack > 1 && storedWh > 0;
    }

    public static bool HasPersistentCellState(Item? item)
    {
        return item is not null
            && ModItemCatalog.IsSvsapmeBigCraftable(item.QualifiedItemId)
            && (item.modData.ContainsKey(MachineRegistryService.StoredWhKey)
                || item.modData.ContainsKey(MachineRegistryService.MachineGuidKey));
    }

    public static bool CanStackPreservingCellState(Item? left, ISalable? right)
    {
        if (right is not Item rightItem)
            return true;

        if (!ModItemCatalog.IsSvsapmeBigCraftable(left?.QualifiedItemId ?? string.Empty)
            && !ModItemCatalog.IsSvsapmeBigCraftable(rightItem.QualifiedItemId))
        {
            return true;
        }

        return !HasPersistentCellState(left) && !HasPersistentCellState(rightItem);
    }
}
