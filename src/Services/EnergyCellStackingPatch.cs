using HarmonyLib;
using StardewModdingAPI;
using StardewValley;

namespace SVSAPME.Services;

internal static class EnergyCellStackingPatch
{
    public static void Apply(string uniqueId, IMonitor monitor)
    {
        try
        {
            var target = AccessTools.Method(typeof(Item), nameof(Item.canStackWith), new[] { typeof(ISalable) });
            if (target is null)
            {
                monitor.Log("SVSAPME could not patch Item.canStackWith(ISalable); charged energy cells may still need inventory normalization fallback.", LogLevel.Warn);
                return;
            }

            var harmony = new Harmony(uniqueId);
            harmony.Patch(
                target,
                prefix: new HarmonyMethod(typeof(EnergyCellStackingPatch), nameof(CanStackWithPrefix)));
        }
        catch (Exception ex)
        {
            monitor.Log($"SVSAPME could not apply the energy-cell stacking Harmony patch: {ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
        }
    }

    private static bool CanStackWithPrefix(Item __instance, ISalable other, ref bool __result)
    {
        try
        {
            if (EnergyCellRules.CanStackPreservingCellState(__instance, other))
                return true;

            __result = false;
            return false;
        }
        catch
        {
            return true;
        }
    }
}
