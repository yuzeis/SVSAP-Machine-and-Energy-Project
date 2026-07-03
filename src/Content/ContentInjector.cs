using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.BigCraftables;
using StardewValley.GameData.Objects;

namespace SVSAPME.Content;

internal sealed class ContentInjector
{
    private const string FreeRecipeIngredientList = "(O)388 0";
    private const string ObjectSpriteAsset = "Mods/" + ModItemCatalog.UniqueId + "/Items";
    private const string BigCraftableSpriteAsset = "Mods/" + ModItemCatalog.UniqueId + "/BigCraftables";
    private const string ObjectSpriteResource = "SVSAPME.Assets.Items.png";
    private const string BigCraftableSpriteResource = "SVSAPME.Assets.BigCraftables.png";

    private readonly Func<ModConfig> getConfig;

    public ContentInjector(Func<ModConfig> getConfig)
    {
        this.getConfig = getConfig;
    }

    public void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo(ObjectSpriteAsset))
        {
            e.LoadFrom(() => LoadEmbeddedTexture(ObjectSpriteResource), AssetLoadPriority.Exclusive);
            return;
        }

        if (e.NameWithoutLocale.IsEquivalentTo(BigCraftableSpriteAsset))
        {
            e.LoadFrom(() => LoadEmbeddedTexture(BigCraftableSpriteResource), AssetLoadPriority.Exclusive);
            return;
        }

        if (e.NameWithoutLocale.IsEquivalentTo("Data/Objects"))
        {
            e.Edit(asset =>
            {
                var data = asset.AsDictionary<string, ObjectData>().Data;
                var spriteIndex = 0;
                foreach (var item in ModItemCatalog.ObjectItems)
                {
                    data[item.Id] = new ObjectData
                    {
                        Name = item.DisplayName,
                        DisplayName = ModText.Get("items." + ModItemCatalog.GetLocalKey(item.Id) + ".name", item.DisplayName),
                        Description = ModText.Get("items." + ModItemCatalog.GetLocalKey(item.Id) + ".description", item.Description),
                        Type = "Basic",
                        Category = item.Category,
                        Price = item.Price,
                        Texture = ObjectSpriteAsset,
                        SpriteIndex = spriteIndex++,
                        Edibility = -300,
                        ContextTags = item.ContextTags.ToList()
                    };
                }
            });

            return;
        }

        if (e.NameWithoutLocale.IsEquivalentTo("Data/BigCraftables"))
        {
            e.Edit(asset =>
            {
                var data = asset.AsDictionary<string, BigCraftableData>().Data;
                var spriteIndex = 0;
                foreach (var item in ModItemCatalog.BigCraftables)
                {
                    data[item.Id] = new BigCraftableData
                    {
                        Name = item.DisplayName,
                        DisplayName = ModText.Get("machines." + ModItemCatalog.GetLocalKey(item.Id) + ".name", item.DisplayName),
                        Description = ModText.Get("machines." + ModItemCatalog.GetLocalKey(item.Id) + ".description", item.Description),
                        Price = item.Price,
                        Fragility = 0,
                        CanBePlacedIndoors = true,
                        CanBePlacedOutdoors = true,
                        Texture = BigCraftableSpriteAsset,
                        SpriteIndex = spriteIndex++,
                        ContextTags = new List<string> { "color_gray" }
                    };
                }
            });

            return;
        }

        if (e.NameWithoutLocale.IsEquivalentTo("Data/CraftingRecipes"))
        {
            e.Edit(asset =>
            {
                var data = asset.AsDictionary<string, string>().Data;
                foreach (var pair in this.GetCraftingRecipes())
                    data[pair.Key] = pair.Value;
            });
        }
    }

    private IEnumerable<KeyValuePair<string, string>> GetCraftingRecipes()
    {
        var recipeCostMode = this.getConfig().GetRecipeCostMode();
        foreach (var pair in ModItemCatalog.CraftingRecipes)
        {
            var raw = recipeCostMode switch
            {
                RecipeCostModes.Debug => MakeRecipeFree(pair.Value),
                RecipeCostModes.Casual => ReduceIngredientCosts(pair.Value),
                _ => pair.Value
            };

            yield return new KeyValuePair<string, string>(
                pair.Key,
                recipeCostMode == RecipeCostModes.Debug ? raw : ApplySkillRequirement(pair.Key, raw));
        }
    }

    private static string MakeRecipeFree(string raw)
    {
        var parts = raw.Split('/');
        if (parts.Length == 0)
            return raw;

        parts[0] = FreeRecipeIngredientList;
        return string.Join("/", parts);
    }

    private static string ReduceIngredientCosts(string raw)
    {
        var parts = raw.Split('/');
        if (parts.Length == 0)
            return raw;

        var tokens = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
            return raw;

        var reduced = new List<string>();
        for (var i = 0; i + 1 < tokens.Length; i += 2)
        {
            reduced.Add(tokens[i]);
            if (int.TryParse(tokens[i + 1], out var count) && count > 1)
                reduced.Add(Math.Max(1, (count + 1) / 2).ToString());
            else
                reduced.Add(tokens[i + 1]);
        }

        if (tokens.Length % 2 != 0)
            reduced.Add(tokens[^1]);

        parts[0] = string.Join(" ", reduced);
        return string.Join("/", parts);
    }

    private static string ApplySkillRequirement(string recipeName, string raw)
    {
        if (!ModItemCatalog.CraftingRecipeSkillRequirements.TryGetValue(recipeName, out var requirement))
            return raw;

        var parts = raw.Split('/');
        if (parts.Length < 5)
            return raw;

        parts[4] = requirement;
        return string.Join("/", parts);
    }

    private static Texture2D LoadEmbeddedTexture(string resourceName)
    {
        var assembly = typeof(ContentInjector).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded SVSAPME texture resource: {resourceName}");
        return Texture2D.FromStream(Game1.graphics.GraphicsDevice, stream);
    }
}
