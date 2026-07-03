using Microsoft.Xna.Framework;

namespace SVSAPME.Services;

internal static class PoweredMachineInterfaceRules
{
    public const long WhPerAction = 1;

    private static readonly Vector2[] PrototypeOffsets =
    {
        new(0, -1),
        new(1, 0),
        new(0, 1),
        new(-1, 0)
    };

    public static int GetPoweredRadius(PoweredMachineTier tier)
    {
        return tier switch
        {
            PoweredMachineTier.Copper => 1,
            PoweredMachineTier.Steel => 2,
            PoweredMachineTier.Gold => 3,
            PoweredMachineTier.Iridium => 4,
            _ => 1
        };
    }

    public static IReadOnlyList<Vector2> GetOffsets(PoweredMachineTier tier, bool powered)
    {
        if (!powered)
            return PrototypeOffsets;

        var radius = GetPoweredRadius(tier);
        var offsets = new List<Vector2>();
        for (var y = -radius; y <= radius; y++)
        {
            for (var x = -radius; x <= radius; x++)
            {
                if (x == 0 && y == 0)
                    continue;

                offsets.Add(new Vector2(x, y));
            }
        }

        return offsets;
    }

    public static bool CanRunPoweredAction(long storedWh)
    {
        return storedWh >= WhPerAction;
    }
}
