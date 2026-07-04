using StardewModdingAPI;

namespace SVSAPME;

internal static class ModText
{
    private static ITranslationHelper? translations;

    public static void Load(ITranslationHelper helper)
    {
        translations = helper;
    }

    public static string Get(string key, string fallback)
    {
        var translation = translations?.Get(key);
        return translation is not null && translation.HasValue()
            ? translation.ToString()
            : fallback;
    }

    public static string Get(string key, string fallback, object tokens)
    {
        var translation = translations?.Get(key, tokens);
        return translation is not null && translation.HasValue()
            ? translation.ToString()
            : FormatFallback(fallback, tokens);
    }

    private static string FormatFallback(string text, object tokens)
    {
        foreach (var property in tokens.GetType().GetProperties())
        {
            var value = property.GetValue(tokens)?.ToString() ?? string.Empty;
            text = text.Replace("{{" + property.Name + "}}", value, StringComparison.Ordinal);
        }

        return text;
    }
}
