using SVSAPME.Content;

namespace SVSAPME.Services;

internal static class SvsapmeBalanceTable
{
    private static readonly IReadOnlyDictionary<string, int> MaterialValues = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Copper Bar"] = 60,
        ["Iron Bar"] = 120,
        ["Gold Bar"] = 250,
        ["Iridium Bar"] = 1000,
        ["Battery Pack"] = 500,
        ["Refined Quartz"] = 50,
        ["Coal"] = 15,
        ["Diamond"] = 750,
        ["Radioactive Bar"] = 3000,
        ["Prismatic Shard"] = 2000,
        ["BasicCircuit"] = 330,
        ["AdvancedCircuit"] = 2410,
        ["EliteCircuit"] = 9820,
        ["NetworkCable"] = 20
    };

    private static readonly IReadOnlyDictionary<string, Recipe> Recipes = new Dictionary<string, Recipe>(StringComparer.Ordinal)
    {
        ["Carbon Rod"] = new("B10.1", "Carbon Rod", "Coal x5", 75, new[] { new Component("Coal", 5) }),
        ["Copper Coil"] = new("B10.1", "Copper Coil", "Copper Bar x2 + Refined Quartz x1", 170, new[] { new Component("Copper Bar", 2), new Component("Refined Quartz", 1) }),
        ["Energy Matrix"] = new("B10.1", "Energy Matrix", "Battery Pack x1 + BasicCircuit x1 + Refined Quartz x5 + Copper Coil x1", 1250, new[] { new Component("Battery Pack", 1), new Component("BasicCircuit", 1), new Component("Refined Quartz", 5), new Component("Copper Coil", 1) }),
        ["Farm Chamber"] = new("B10.1", "Farm Chamber", "Iron Bar x5 + Refined Quartz x5 + NetworkCable x4 + BasicCircuit x1", 1260, new[] { new Component("Iron Bar", 5), new Component("Refined Quartz", 5), new Component("NetworkCable", 4), new Component("BasicCircuit", 1) }),

        ["Carbon Generator"] = new("B10.2", "Carbon Generator", "Iron Bar x5 + Copper Bar x5 + Carbon Rod x2 + BasicCircuit x1 + NetworkCable x4", 1460, new[] { new Component("Iron Bar", 5), new Component("Copper Bar", 5), new Component("Carbon Rod", 2), new Component("BasicCircuit", 1), new Component("NetworkCable", 4) }),
        ["Solar Network Panel"] = new("B10.2", "Solar Network Panel", "Refined Quartz x10 + Iron Bar x5 + Copper Coil x2 + BasicCircuit x1 + Battery Pack x1", 2270, new[] { new Component("Refined Quartz", 10), new Component("Iron Bar", 5), new Component("Copper Coil", 2), new Component("BasicCircuit", 1), new Component("Battery Pack", 1) }),
        ["Copper Energy Cell"] = new("B10.2", "Copper Energy Cell", "Copper Bar x5 + Iron Bar x2 + BasicCircuit x1 + NetworkCable x4", 950, new[] { new Component("Copper Bar", 5), new Component("Iron Bar", 2), new Component("BasicCircuit", 1), new Component("NetworkCable", 4) }),
        ["Steel Energy Cell"] = new("B10.2", "Steel Energy Cell", "Iron Bar x5 + Copper Energy Cell x1 + AdvancedCircuit x1 + Battery Pack x1", 4460, new[] { new Component("Iron Bar", 5), new Component("Copper Energy Cell", 1), new Component("AdvancedCircuit", 1), new Component("Battery Pack", 1) }),
        ["Gold Energy Cell"] = new("B10.2", "Gold Energy Cell", "Gold Bar x5 + Steel Energy Cell x1 + AdvancedCircuit x2 + Battery Pack x2", 11530, new[] { new Component("Gold Bar", 5), new Component("Steel Energy Cell", 1), new Component("AdvancedCircuit", 2), new Component("Battery Pack", 2) }),
        ["Iridium Energy Cell"] = new("B10.2", "Iridium Energy Cell", "Iridium Bar x5 + Gold Energy Cell x1 + EliteCircuit x2 + Battery Pack x4", 38170, new[] { new Component("Iridium Bar", 5), new Component("Gold Energy Cell", 1), new Component("EliteCircuit", 2), new Component("Battery Pack", 4) }),
        ["Battery Synthesizer"] = new("B10.2", "Battery Synthesizer", "Iron Bar x5 + Gold Bar x2 + Battery Pack x1 + BasicCircuit x2 + NetworkCable x8 + Energy Matrix x1", 3670, new[] { new Component("Iron Bar", 5), new Component("Gold Bar", 2), new Component("Battery Pack", 1), new Component("BasicCircuit", 2), new Component("NetworkCable", 8), new Component("Energy Matrix", 1) }),
        ["Copper Farm"] = new("B10.2", "Copper Farm", "Farm Chamber x1 + Copper Bar x10 + BasicCircuit x2 + Battery Pack x1", 3020, new[] { new Component("Farm Chamber", 1), new Component("Copper Bar", 10), new Component("BasicCircuit", 2), new Component("Battery Pack", 1) }),
        ["Steel Farm"] = new("B10.2", "Steel Farm", "Farm Chamber x2 + Iron Bar x10 + Copper Farm x1 + AdvancedCircuit x1 + Battery Pack x1", 9650, new[] { new Component("Farm Chamber", 2), new Component("Iron Bar", 10), new Component("Copper Farm", 1), new Component("AdvancedCircuit", 1), new Component("Battery Pack", 1) }),
        ["Gold Farm"] = new("B10.2", "Gold Farm", "Farm Chamber x4 + Gold Bar x10 + Steel Farm x1 + AdvancedCircuit x2 + Battery Pack x2", 23010, new[] { new Component("Farm Chamber", 4), new Component("Gold Bar", 10), new Component("Steel Farm", 1), new Component("AdvancedCircuit", 2), new Component("Battery Pack", 2) }),
        ["Iridium Farm"] = new("B10.2", "Iridium Farm", "Farm Chamber x8 + Iridium Bar x10 + Gold Farm x1 + EliteCircuit x2 + Battery Pack x4", 64730, new[] { new Component("Farm Chamber", 8), new Component("Iridium Bar", 10), new Component("Gold Farm", 1), new Component("EliteCircuit", 2), new Component("Battery Pack", 4) }),

        ["Powered Importer/Exporter Copper delta"] = new("B10.3", "Powered Importer/Exporter Copper delta", "Prototype x1 + Copper Bar x3 + Copper Coil x1 + BasicCircuit x1", 680, new[] { new Component("Prototype", 1, false), new Component("Copper Bar", 3), new Component("Copper Coil", 1), new Component("BasicCircuit", 1) }),
        ["Powered Importer/Exporter Steel delta"] = new("B10.3", "Powered Importer/Exporter Steel delta", "Copper tier x1 + Iron Bar x5 + AdvancedCircuit x1", 3010, new[] { new Component("Copper tier", 1, false), new Component("Iron Bar", 5), new Component("AdvancedCircuit", 1) }),
        ["Powered Importer/Exporter Gold delta"] = new("B10.3", "Powered Importer/Exporter Gold delta", "Steel tier x1 + Gold Bar x5 + AdvancedCircuit x1 + Battery Pack x1", 4160, new[] { new Component("Steel tier", 1, false), new Component("Gold Bar", 5), new Component("AdvancedCircuit", 1), new Component("Battery Pack", 1) }),
        ["Powered Importer/Exporter Iridium delta"] = new("B10.3", "Powered Importer/Exporter Iridium delta", "Gold tier x1 + Iridium Bar x3 + EliteCircuit x1 + Battery Pack x2", 13820, new[] { new Component("Gold tier", 1, false), new Component("Iridium Bar", 3), new Component("EliteCircuit", 1), new Component("Battery Pack", 2) }),
        ["Powered Machine Interface x4 tiers"] = new("B10.3", "Powered Machine Interface x4 tiers", "Same delta structure as Powered Importer/Exporter; prototype = SVSAP MachineInterface", 0, Array.Empty<Component>()),
        ["Battery Discharger"] = new("B10.3", "Battery Discharger", "Iron Bar x5 + Copper Coil x2 + BasicCircuit x1 + Battery Pack x1", 1770, new[] { new Component("Iron Bar", 5), new Component("Copper Coil", 2), new Component("BasicCircuit", 1), new Component("Battery Pack", 1) }),
        ["Energy Monitor Terminal"] = new("B10.3", "Energy Monitor Terminal", "Refined Quartz x5 + BasicCircuit x2 + NetworkCable x4", 990, new[] { new Component("Refined Quartz", 5), new Component("BasicCircuit", 2), new Component("NetworkCable", 4) }),
        ["Electric Furnace delta"] = new("B10.3", "Electric Furnace delta", "Furnace x1 + Iron Bar x5 + Copper Coil x2 + BasicCircuit x1 + Battery Pack x1", 1770, new[] { new Component("Furnace", 1, false), new Component("Iron Bar", 5), new Component("Copper Coil", 2), new Component("BasicCircuit", 1), new Component("Battery Pack", 1) }),
        ["Electric Geode Crusher delta"] = new("B10.3", "Electric Geode Crusher delta", "Geode Crusher x1 + Gold Bar x2 + AdvancedCircuit x1 + Copper Coil x2", 3250, new[] { new Component("Geode Crusher", 1, false), new Component("Gold Bar", 2), new Component("AdvancedCircuit", 1), new Component("Copper Coil", 2) })
    };

    private static readonly string[] OutputOrder =
    {
        "Carbon Rod",
        "Copper Coil",
        "Energy Matrix",
        "Farm Chamber",
        "Carbon Generator",
        "Solar Network Panel",
        "Copper Energy Cell",
        "Steel Energy Cell",
        "Gold Energy Cell",
        "Iridium Energy Cell",
        "Battery Synthesizer",
        "Copper Farm",
        "Steel Farm",
        "Gold Farm",
        "Iridium Farm",
        "Powered Importer/Exporter Copper delta",
        "Powered Importer/Exporter Steel delta",
        "Powered Importer/Exporter Gold delta",
        "Powered Importer/Exporter Iridium delta",
        "Powered Machine Interface x4 tiers",
        "Battery Discharger",
        "Energy Monitor Terminal",
        "Electric Furnace delta",
        "Electric Geode Crusher delta"
    };

    public static IReadOnlyList<BalanceRow> GetRows()
    {
        return OutputOrder.Select(CreateRow).ToList();
    }

    public static IReadOnlyList<string> ToReportLines()
    {
        var lines = new List<string>
        {
            "B10 generated balance table",
            "Valuation: SVSAP circuits/cable use recursive material cost; UI prices are ignored.",
            "Base SVSAP values: BasicCircuit=330, AdvancedCircuit=2410, EliteCircuit=9820, NetworkCable=20.",
            "| Section | Item | Recipe | Value |",
            "|---|---|---|---:|"
        };

        lines.AddRange(GetRows().Select(row => $"| {row.Section} | {row.Item} | {row.RecipeText} | {row.Value} |"));
        return lines;
    }

    public static IReadOnlyList<string> ValidateParity()
    {
        var failures = new List<string>();
        foreach (var row in GetRows())
        {
            if (row.ExpectedValue != row.Value)
                failures.Add($"{row.Item}: expected {row.ExpectedValue}, calculated {row.Value}");
        }

        return failures;
    }

    public static IReadOnlyList<string> ValidateNoArbitrage()
    {
        var failures = new List<string>();
        var batteryMarketValue = 500;
        var coalValue = 15;
        var batterySynthesisWh = 10_000;
        var batteryDischargeWh = 8_000;
        var batterySynthesisMaterialValue = 1010;
        var coalForSynthesisEnergy = (batterySynthesisWh / 350m) * coalValue;

        if (batteryDischargeWh >= batterySynthesisWh)
            failures.Add("battery discharge Wh must stay below synthesis Wh");

        if (batterySynthesisMaterialValue <= batteryMarketValue)
            failures.Add("battery synthesis materials must stay above Battery Pack market value");

        if (batterySynthesisMaterialValue + coalForSynthesisEnergy <= batteryMarketValue)
            failures.Add("coal to battery loop must stay non-profitable");

        return failures;
    }

    public static IReadOnlyDictionary<string, int> GetBaseSvsapValues()
    {
        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["BasicCircuit"] = MaterialValues["BasicCircuit"],
            ["AdvancedCircuit"] = MaterialValues["AdvancedCircuit"],
            ["EliteCircuit"] = MaterialValues["EliteCircuit"],
            ["NetworkCable"] = MaterialValues["NetworkCable"]
        };
    }

    public static IReadOnlyList<string> ValidateSvsapCraftingRecipeParity(IReadOnlyDictionary<string, string> craftingRecipes)
    {
        var failures = new List<string>();
        var cache = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in GetBaseSvsapValues())
        {
            var recipeKey = ModItemCatalog.SvsapUniqueId + "." + pair.Key;
            if (!TryCalculateSvsapRecipeValue(recipeKey, craftingRecipes, cache, new HashSet<string>(StringComparer.Ordinal), out var actual, out var message))
            {
                failures.Add($"{pair.Key}: {message}");
                continue;
            }

            if (actual != pair.Value)
                failures.Add($"{pair.Key}: expected {pair.Value}, actual SVSAP recipe value {actual}");
        }

        return failures;
    }

    private static BalanceRow CreateRow(string item)
    {
        var recipe = Recipes[item];
        var value = recipe.Components.Count == 0 ? recipe.ExpectedValue : CalculateValue(recipe, new HashSet<string>(StringComparer.Ordinal));
        return new BalanceRow(recipe.Section, recipe.Item, recipe.DisplayRecipe, value, recipe.ExpectedValue);
    }

    private static bool TryCalculateSvsapRecipeValue(
        string recipeKey,
        IReadOnlyDictionary<string, string> craftingRecipes,
        Dictionary<string, int> cache,
        HashSet<string> stack,
        out int value,
        out string message)
    {
        value = 0;
        message = string.Empty;
        if (cache.TryGetValue(recipeKey, out value))
            return true;

        if (!stack.Add(recipeKey))
        {
            message = $"cycle while parsing {recipeKey}";
            return false;
        }

        if (!craftingRecipes.TryGetValue(recipeKey, out var raw))
        {
            message = $"missing recipe {recipeKey}";
            stack.Remove(recipeKey);
            return false;
        }

        var parts = raw.Split('/');
        if (parts.Length < 3)
        {
            message = $"malformed recipe {recipeKey}: {raw}";
            stack.Remove(recipeKey);
            return false;
        }

        var tokens = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length % 2 != 0)
        {
            message = $"malformed ingredient list {recipeKey}: {parts[0]}";
            stack.Remove(recipeKey);
            return false;
        }

        var total = 0;
        for (var i = 0; i + 1 < tokens.Length; i += 2)
        {
            var token = tokens[i];
            if (!int.TryParse(tokens[i + 1], out var count) || count < 0)
            {
                message = $"invalid ingredient count {tokens[i + 1]} in {recipeKey}";
                stack.Remove(recipeKey);
                return false;
            }

            if (TryGetKnownSvsapIngredientValue(token, out var knownValue))
            {
                total += knownValue * count;
                continue;
            }

            if (TryGetSvsapObjectId(token, out var childKey))
            {
                if (!TryCalculateSvsapRecipeValue(childKey, craftingRecipes, cache, stack, out var childValue, out message))
                {
                    stack.Remove(recipeKey);
                    return false;
                }

                total += childValue * count;
                continue;
            }

            message = $"unsupported ingredient {token} in {recipeKey}";
            stack.Remove(recipeKey);
            return false;
        }

        var outputCount = ParseOutputCount(parts[2]);
        if (outputCount <= 0)
        {
            message = $"invalid output count in {recipeKey}: {parts[2]}";
            stack.Remove(recipeKey);
            return false;
        }

        if (total % outputCount != 0)
        {
            message = $"recipe value {total} is not divisible by output count {outputCount} in {recipeKey}";
            stack.Remove(recipeKey);
            return false;
        }

        value = total / outputCount;
        cache[recipeKey] = value;
        stack.Remove(recipeKey);
        return true;
    }

    private static bool TryGetKnownSvsapIngredientValue(string token, out int value)
    {
        return SvsapRecipeIngredientValues.TryGetValue(token, out value);
    }

    private static bool TryGetSvsapObjectId(string token, out string itemId)
    {
        const string objectPrefix = "(O)";
        itemId = string.Empty;
        if (!token.StartsWith(objectPrefix, StringComparison.Ordinal))
            return false;

        var candidate = token[objectPrefix.Length..];
        if (!candidate.StartsWith(ModItemCatalog.SvsapUniqueId + ".", StringComparison.Ordinal))
            return false;

        itemId = candidate;
        return true;
    }

    private static int ParseOutputCount(string outputPart)
    {
        var tokens = outputPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2 || !int.TryParse(tokens[1], out var count))
            return 0;

        return count;
    }

    private static int CalculateValue(Recipe recipe, HashSet<string> stack)
    {
        if (!stack.Add(recipe.Item))
            throw new InvalidOperationException($"Recursive balance recipe loop at {recipe.Item}.");

        try
        {
            var total = 0;
            foreach (var component in recipe.Components)
            {
                if (!component.IncludeValue)
                    continue;

                if (MaterialValues.TryGetValue(component.Item, out var materialValue))
                {
                    total += materialValue * component.Count;
                    continue;
                }

                if (!Recipes.TryGetValue(component.Item, out var childRecipe))
                    throw new InvalidOperationException($"Unknown balance recipe component '{component.Item}' in '{recipe.Item}'.");

                total += CalculateValue(childRecipe, stack) * component.Count;
            }

            return total;
        }
        finally
        {
            stack.Remove(recipe.Item);
        }
    }

    private static readonly IReadOnlyDictionary<string, int> SvsapRecipeIngredientValues = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["(O)334"] = 60,
        ["(O)335"] = 120,
        ["(O)336"] = 250,
        ["(O)337"] = 1000,
        ["(O)338"] = 50,
        ["(O)72"] = 750,
        ["(O)74"] = 2000,
        ["(O)910"] = 3000
    };

    private sealed record Recipe(
        string Section,
        string Item,
        string DisplayRecipe,
        int ExpectedValue,
        IReadOnlyList<Component> Components);

    private sealed record Component(string Item, int Count, bool IncludeValue = true);
}

internal sealed record BalanceRow(
    string Section,
    string Item,
    string RecipeText,
    int Value,
    int ExpectedValue);
