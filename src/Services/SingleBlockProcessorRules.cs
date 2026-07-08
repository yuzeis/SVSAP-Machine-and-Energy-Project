using SVSAPME.Content;
using SVSAPME.Models;
using StardewValley;
using SObject = StardewValley.Object;

namespace SVSAPME.Services;

internal enum SingleBlockProcessorKind
{
    None,
    Keg,
    Cask
}

internal static class SingleBlockProcessorRules
{
    private const int KegMinutesPerDay = 1200;
    private const int FruitCategory = -79;
    private const int VegetableCategory = -75;

    public static bool IsProcessorMachine(string qualifiedItemId)
    {
        return GetProcessorKind(qualifiedItemId) != SingleBlockProcessorKind.None;
    }

    public static bool IsKegMachine(string qualifiedItemId)
    {
        return GetProcessorKind(qualifiedItemId) == SingleBlockProcessorKind.Keg;
    }

    public static bool IsCaskMachine(string qualifiedItemId)
    {
        return GetProcessorKind(qualifiedItemId) == SingleBlockProcessorKind.Cask;
    }

    public static SingleBlockProcessorKind GetProcessorKind(string qualifiedItemId)
    {
        return qualifiedItemId switch
        {
            "(BC)" + ModItemCatalog.CopperKeg or
            "(BC)" + ModItemCatalog.SteelKeg or
            "(BC)" + ModItemCatalog.GoldKeg or
            "(BC)" + ModItemCatalog.IridiumKeg => SingleBlockProcessorKind.Keg,

            "(BC)" + ModItemCatalog.CopperCask or
            "(BC)" + ModItemCatalog.SteelCask or
            "(BC)" + ModItemCatalog.GoldCask or
            "(BC)" + ModItemCatalog.IridiumCask => SingleBlockProcessorKind.Cask,

            _ => SingleBlockProcessorKind.None
        };
    }

    public static ProcessorTierInfo GetTier(string qualifiedItemId)
    {
        var tier = qualifiedItemId switch
        {
            "(BC)" + ModItemCatalog.SteelKeg or "(BC)" + ModItemCatalog.SteelCask => EnergyTier.Steel,
            "(BC)" + ModItemCatalog.GoldKeg or "(BC)" + ModItemCatalog.GoldCask => EnergyTier.Gold,
            "(BC)" + ModItemCatalog.IridiumKeg or "(BC)" + ModItemCatalog.IridiumCask => EnergyTier.Iridium,
            _ => EnergyTier.Copper
        };

        return EnergyTierTable.Processors.Single(entry => entry.Tier == tier);
    }

    public static void NormalizeSlots(SingleBlockProcessorMachineState processor, ProcessorTierInfo tier)
    {
        processor.Slots ??= new();
        while (processor.Slots.Count < tier.Slots)
            processor.Slots.Add(new SingleBlockProcessorSlotState());

        if (processor.Slots.Count > tier.Slots)
        {
            var overflow = processor.Slots.Skip(tier.Slots).ToList();
            processor.Slots.RemoveRange(tier.Slots, processor.Slots.Count - tier.Slots);
            foreach (var slot in overflow)
                MoveSlotPayloadToBuffer(processor, slot);
        }
    }

    public static bool TryCreateJob(
        SingleBlockProcessorKind kind,
        Item input,
        out SingleBlockProcessorSlotState slot,
        out string message)
    {
        return kind switch
        {
            SingleBlockProcessorKind.Keg => TryCreateKegJob(input, out slot, out message),
            SingleBlockProcessorKind.Cask => TryCreateCaskJob(input, out slot, out message),
            _ => Fail(out slot, out message, ModText.Get("hud.processor.notProcessor", "Target machine is not a Single-Block Keg or Cask."))
        };
    }

    public static void AdvanceKegSlot(SingleBlockProcessorSlotState slot, int elapsedMinutes)
    {
        if (!IsWorking(slot) || slot.RemainingMinutes <= 0)
            return;

        slot.RemainingMinutes = Math.Max(0, slot.RemainingMinutes - Math.Max(0, elapsedMinutes));
    }

    public static void AdvanceCaskSlot(SingleBlockProcessorSlotState slot, int elapsedDays)
    {
        if (!IsWorking(slot) || slot.RemainingDays <= 0)
            return;

        slot.RemainingDays = Math.Max(0, slot.RemainingDays - Math.Max(0, elapsedDays));
    }

    public static bool IsWorking(SingleBlockProcessorSlotState slot)
    {
        return slot.Input is not null && slot.Output is not null;
    }

    public static bool IsReady(SingleBlockProcessorSlotState slot)
    {
        return IsWorking(slot) && slot.RemainingMinutes <= 0 && slot.RemainingDays <= 0;
    }

    public static int CountActive(SingleBlockProcessorMachineState processor)
    {
        return processor.Slots.Count(IsWorking);
    }

    public static int CountReady(SingleBlockProcessorMachineState processor)
    {
        return processor.Slots.Count(IsReady);
    }

    public static int CountEmpty(SingleBlockProcessorMachineState processor)
    {
        return processor.Slots.Count(slot => !IsWorking(slot));
    }

    public static void MoveSlotPayloadToBuffer(SingleBlockProcessorMachineState processor, SingleBlockProcessorSlotState slot)
    {
        var recoverable = GetRecoverableStack(slot);
        if (recoverable is not null)
            processor.OutputBuffer.Add(recoverable);

        ClearSlot(slot);
    }

    public static BufferedItemStack? GetRecoverableStack(SingleBlockProcessorSlotState slot)
    {
        if (IsReady(slot) && slot.Output is not null)
            return slot.Output;

        if (slot.Input is not null)
            return slot.Input;

        return null;
    }

    public static void ClearSlot(SingleBlockProcessorSlotState slot)
    {
        slot.Input = null;
        slot.Output = null;
        slot.InputCount = 0;
        slot.RemainingMinutes = 0;
        slot.TotalMinutes = 0;
        slot.RemainingDays = 0;
        slot.TotalDays = 0;
        slot.TargetQuality = 4;
    }

    public static string FormatEta(SingleBlockProcessorSlotState slot)
    {
        if (!IsWorking(slot))
            return ModText.Get("ui.processor.slot.empty", "Empty");

        if (IsReady(slot))
            return ModText.Get("ui.processor.slot.ready", "Ready");

        if (slot.RemainingDays > 0)
            return ModText.Get("ui.processor.eta.days", "{{days}}d", new { days = slot.RemainingDays.ToString("N0") });

        var minutes = Math.Max(0, slot.RemainingMinutes);
        var days = minutes / KegMinutesPerDay;
        var remainder = minutes % KegMinutesPerDay;
        var hours = remainder / 60;
        return days > 0
            ? ModText.Get("ui.processor.eta.daysHours", "{{days}}d {{hours}}h", new { days = days.ToString("N0"), hours = hours.ToString("N0") })
            : ModText.Get("ui.processor.eta.hours", "{{hours}}h", new { hours = Math.Max(1, hours).ToString("N0") });
    }

    private static bool TryCreateKegJob(Item input, out SingleBlockProcessorSlotState slot, out string message)
    {
        if (!TryGetKegRecipe(input, out var recipe))
            return Fail(out slot, out message, ModText.Get("hud.processor.keg.unsupported", "This item cannot be processed by the Single-Block Keg."));

        if (input.Stack < recipe.InputCount)
        {
            return Fail(
                out slot,
                out message,
                ModText.Get("hud.processor.keg.needMore", "This recipe needs {{count}} item(s).", new { count = recipe.InputCount.ToString("N0") }));
        }

        var output = CreateKegOutput(input, recipe);

        var inputOne = input.getOne();
        inputOne.Stack = recipe.InputCount;
        slot = new SingleBlockProcessorSlotState
        {
            Input = BufferedItemCodec.FromItem(inputOne),
            Output = BufferedItemCodec.FromItem(output),
            InputCount = recipe.InputCount,
            RemainingMinutes = recipe.Minutes,
            TotalMinutes = recipe.Minutes
        };
        message = string.Empty;
        return true;
    }

    private static Item CreateKegOutput(Item input, KegProcessorRecipe recipe)
    {
        var output = recipe.PreserveType switch
        {
            SObject.PreserveType.Wine when input is SObject wineInput => ItemRegistry.GetObjectTypeDefinition().CreateFlavoredWine((SObject)wineInput.getOne()),
            SObject.PreserveType.Juice when input is SObject juiceInput => ItemRegistry.GetObjectTypeDefinition().CreateFlavoredJuice((SObject)juiceInput.getOne()),
            _ => ItemRegistry.Create(recipe.OutputQualifiedItemId, recipe.OutputCount)
        };

        output.Stack = recipe.OutputCount;
        if (output is SObject outputObject)
        {
            if (!string.IsNullOrWhiteSpace(recipe.PreservedParentSheetIndex))
                outputObject.preservedParentSheetIndex.Value = recipe.PreservedParentSheetIndex;
            if (recipe.PreserveType.HasValue)
                outputObject.preserve.Value = recipe.PreserveType.Value;
        }

        return output;
    }

    private static bool TryCreateCaskJob(Item input, out SingleBlockProcessorSlotState slot, out string message)
    {
        if (!TryGetCaskSpec(input, out var spec))
            return Fail(out slot, out message, ModText.Get("hud.processor.cask.unsupported", "This item cannot be aged by the Single-Block Cask."));

        if (input.Quality >= 4)
            return Fail(out slot, out message, ModText.Get("hud.processor.cask.alreadyIridium", "This item is already iridium quality."));

        var days = GetCaskDaysRemaining(spec, input.Quality);
        var output = input.getOne();
        output.Stack = 1;
        output.Quality = 4;
        slot = new SingleBlockProcessorSlotState
        {
            Input = BufferedItemCodec.FromItem(input.getOne()),
            Output = BufferedItemCodec.FromItem(output),
            InputCount = 1,
            RemainingDays = days,
            TotalDays = days,
            TargetQuality = 4
        };
        message = string.Empty;
        return true;
    }

    private static bool TryGetKegRecipe(Item input, out KegProcessorRecipe recipe)
    {
        var preservedParent = ToParentSheetIndex(input);
        recipe = input.QualifiedItemId switch
        {
            "(O)262" => new("(O)262", 1, "(O)346", 1, 1_750, string.Empty, null),
            "(O)304" => new("(O)304", 1, "(O)303", 1, 2_250, string.Empty, null),
            "(O)340" => new("(O)340", 1, "(O)459", 1, 600, string.Empty, null),
            "(O)433" => new("(O)433", 5, "(O)395", 1, 120, string.Empty, null),
            "(O)815" => new("(O)815", 1, "(O)614", 1, 180, string.Empty, null),
            _ => input is SObject obj && obj.Category == FruitCategory
                ? new(input.QualifiedItemId, 1, "(O)348", 1, 10_000, preservedParent, SObject.PreserveType.Wine)
                : input is SObject vegetable && vegetable.Category == VegetableCategory
                    ? new(input.QualifiedItemId, 1, "(O)350", 1, 6_000, preservedParent, SObject.PreserveType.Juice)
                    : null!
        };

        return recipe is not null;
    }

    private static bool TryGetCaskSpec(Item input, out CaskProcessorSpec spec)
    {
        spec = input.QualifiedItemId switch
        {
            "(O)348" => new("(O)348", 56),
            "(O)303" => new("(O)303", 34),
            "(O)346" => new("(O)346", 28),
            "(O)459" => new("(O)459", 28),
            "(O)424" => new("(O)424", 14),
            "(O)426" => new("(O)426", 14),
            _ => null!
        };

        return spec is not null;
    }

    private static int GetCaskDaysRemaining(CaskProcessorSpec spec, int inputQuality)
    {
        var totalDays = Math.Max(1, spec.TotalDaysToIridium);
        return inputQuality switch
        {
            2 => Math.Max(1, totalDays / 2),
            1 => Math.Max(1, totalDays * 3 / 4),
            _ => totalDays
        };
    }

    private static string ToParentSheetIndex(Item input)
    {
        if (input is SObject obj && !string.IsNullOrWhiteSpace(obj.preservedParentSheetIndex.Value))
            return obj.preservedParentSheetIndex.Value;

        var qualifiedId = input.QualifiedItemId;
        return qualifiedId.StartsWith("(O)", StringComparison.Ordinal)
            ? qualifiedId[3..]
            : input.ItemId;
    }

    private static bool Fail(out SingleBlockProcessorSlotState slot, out string message, string failure)
    {
        slot = new SingleBlockProcessorSlotState();
        message = failure;
        return false;
    }
}

internal sealed record KegProcessorRecipe(
    string InputQualifiedItemId,
    int InputCount,
    string OutputQualifiedItemId,
    int OutputCount,
    int Minutes,
    string PreservedParentSheetIndex,
    SObject.PreserveType? PreserveType);

internal sealed record CaskProcessorSpec(
    string QualifiedItemId,
    int TotalDaysToIridium);
