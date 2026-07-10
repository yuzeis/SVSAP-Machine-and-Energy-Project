using SVSAPME.Models;
using StardewValley;

namespace SVSAPME.Services;

internal static class SvsapmeMachineActionRequestFactory
{
    public static SvsapmeMachineActionRequest Create(
        Guid machineGuid,
        SvsapmeMachineActionKind actionKind,
        int slotIndex = -1,
        int offset = 0,
        int limit = 64)
    {
        return new SvsapmeMachineActionRequest
        {
            TransactionId = Guid.NewGuid(),
            MachineGuid = machineGuid,
            ActionKind = actionKind,
            SlotIndex = slotIndex,
            Offset = Math.Max(0, offset),
            Limit = Math.Clamp(limit, 1, 64)
        };
    }

    public static SvsapmeMachineActionRequest CreateWithItem(
        Guid machineGuid,
        SvsapmeMachineActionKind actionKind,
        Item item,
        int count,
        int slotIndex = -1,
        int farmingLevel = 0,
        int offset = 0,
        int limit = 64)
    {
        var buffered = BufferedItemCodec.FromItem(item);
        var request = Create(machineGuid, actionKind, slotIndex, offset, limit);
        request.QualifiedItemId = buffered.QualifiedItemId;
        request.Count = Math.Clamp(count, 1, Math.Max(1, item.Stack));
        request.FarmingLevel = Math.Clamp(farmingLevel, 0, 10);
        request.Quality = buffered.Quality;
        request.PreservedParentSheetIndex = buffered.PreservedParentSheetIndex;
        request.PreserveType = buffered.PreserveType;
        request.Price = buffered.Price;
        request.Edibility = buffered.Edibility;
        request.Category = buffered.Category;
        request.Type = buffered.Type;
        request.Name = buffered.Name;
        request.DisplayName = buffered.DisplayName;
        request.Color = buffered.Color;
        request.ModData = buffered.ModData;
        return request;
    }
}
