using SVSAPME.Models;

namespace SVSAPME.Services;

internal static class MachineReclaimRules
{
    public const string BuildingDemolishReason = "building-demolish-reclaim";
    public const string OrphanReason = "orphan-reclaim";
    public const int MissingMachineGraceDays = 1;
    public const int MaxReclaimPlacementSearchRadius = 16;

    public static bool ShouldQueueMissingMachineReclaim(int missingDays)
    {
        return missingDays > MissingMachineGraceDays;
    }

    public static PendingReclaimCrate? CreateBuildingDemolishReclaim(
        string indoorLocationName,
        int buildingTileX,
        int buildingTileY,
        int tilesWide,
        int tilesHigh,
        IEnumerable<MachineState> machineStates,
        IEnumerable<PendingReclaimCrate> existingReclaims)
    {
        if (string.IsNullOrWhiteSpace(indoorLocationName))
            return null;

        var machineGuids = machineStates
            .Where(state => string.Equals(state.LocationName, indoorLocationName, StringComparison.Ordinal))
            .Select(state => state.MachineGuid)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        if (machineGuids.Count == 0)
            return null;

        var alreadyQueued = existingReclaims.Any(reclaim =>
            string.Equals(reclaim.OriginalLocationName, indoorLocationName, StringComparison.Ordinal)
            && reclaim.MachineGuids.SequenceEqual(machineGuids));
        if (alreadyQueued)
            return null;

        return new PendingReclaimCrate
        {
            ReclaimId = Guid.NewGuid(),
            Reason = BuildingDemolishReason,
            OriginalLocationName = indoorLocationName,
            TileX = buildingTileX + Math.Max(0, tilesWide / 2),
            TileY = buildingTileY + Math.Max(0, tilesHigh / 2),
            MachineGuids = machineGuids
        };
    }

    public static PendingReclaimCrate? CreateOrphanReclaim(
        MachineState state,
        IEnumerable<PendingReclaimCrate> existingReclaims)
    {
        if (state.MachineGuid == Guid.Empty)
            return null;

        var alreadyQueued = existingReclaims.Any(reclaim => reclaim.MachineGuids.Contains(state.MachineGuid));
        if (alreadyQueued)
            return null;

        return new PendingReclaimCrate
        {
            ReclaimId = Guid.NewGuid(),
            Reason = OrphanReason,
            OriginalLocationName = state.LocationName,
            TileX = (int)state.TileX,
            TileY = (int)state.TileY,
            MachineGuids = new List<Guid> { state.MachineGuid }
        };
    }

    public static bool IsConfirmedClaimReason(string? reason)
    {
        return string.Equals(reason, OrphanReason, StringComparison.Ordinal)
            || string.Equals(reason, BuildingDemolishReason, StringComparison.Ordinal);
    }

    public static bool ShouldClaimReclaim(PendingReclaimCrate reclaim, bool includeUnconfirmed)
    {
        return includeUnconfirmed || IsConfirmedClaimReason(reclaim.Reason);
    }

    public static ReclaimItemSummary SummarizeRecoverableItems(
        IEnumerable<PendingReclaimCrate> pendingReclaims,
        IReadOnlyDictionary<Guid, MachineState> machines)
    {
        var seen = new HashSet<Guid>();
        var machineCount = 0;
        var bufferedItemStacks = 0;
        var internalSeedStacks = 0;
        var internalFertilizerStacks = 0;
        var installedModuleStacks = 0;

        foreach (var reclaim in pendingReclaims)
        {
            foreach (var machineGuid in reclaim.MachineGuids)
            {
                if (!seen.Add(machineGuid) || !machines.TryGetValue(machineGuid, out var state))
                    continue;

                machineCount++;
                bufferedItemStacks += state.OutputBuffer.Count;
                if (state.Farm.InternalSeedCount > 0
                    && !string.IsNullOrWhiteSpace(state.Farm.BoundSeedQualifiedItemId))
                {
                    internalSeedStacks++;
                }

                if (state.Farm.InternalFertilizerCount > 0
                    && !string.IsNullOrWhiteSpace(state.Farm.BoundFertilizerQualifiedItemId))
                {
                    internalFertilizerStacks++;
                }

                installedModuleStacks += FarmModuleRules.GetInstalledModuleItems(state.Farm).Count();
            }
        }

        return new ReclaimItemSummary(machineCount, bufferedItemStacks, internalSeedStacks, internalFertilizerStacks, installedModuleStacks);
    }

    public static IEnumerable<(int X, int Y)> EnumerateSpiralTiles(int centerX, int centerY, int maxRadius)
    {
        yield return (centerX, centerY);

        for (var radius = 1; radius <= maxRadius; radius++)
        {
            for (var x = centerX - radius; x <= centerX + radius; x++)
                yield return (x, centerY - radius);

            for (var y = centerY - radius + 1; y <= centerY + radius; y++)
                yield return (centerX + radius, y);

            for (var x = centerX + radius - 1; x >= centerX - radius; x--)
                yield return (x, centerY + radius);

            for (var y = centerY + radius - 1; y >= centerY - radius + 1; y--)
                yield return (centerX - radius, y);
        }
    }
}

internal readonly record struct ReclaimItemSummary(
    int Machines,
    int BufferedItemStacks,
    int InternalSeedStacks,
    int InternalFertilizerStacks,
    int InstalledModuleStacks);
