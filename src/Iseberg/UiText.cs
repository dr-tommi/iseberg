using System.Globalization;
using System.Resources;
using Avalonia.Markup.Xaml;

namespace Iseberg;

public static class UiText
{
    private static readonly ResourceManager Resources = new("Iseberg.Resources.Strings", typeof(UiText).Assembly);
    /// <summary>True when the display language is not the English the fixed dialog layouts were measured for.</summary>
    public static bool IsTranslated => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";
    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture)
        ?? throw new MissingManifestResourceException($"Missing UI resource: {key}");

    /// <summary>
    /// Returns the current-language text when <paramref name="text"/> is one of the given resources in any
    /// shipped language. Settings store some built-in names as text, which may predate a language change.
    /// </summary>
    public static string Translate(string text, params string[] keys)
    {
        foreach (var key in keys)
            foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("de") })
                if (Resources.GetString(key, culture) == text) return Get(key);
        return text;
    }
}

public sealed class UiExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => UiText.Get(key);
}
