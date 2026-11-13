using System.Globalization;
using System.Text.Json;

namespace Iseberg;

/// <summary>Selects the display language used by <see cref="UiText"/>.</summary>
public static class UiLanguage
{
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;
    private static readonly CultureInfo? SystemDefaultThreadCulture = CultureInfo.DefaultThreadCurrentUICulture;

    /// <summary>Language codes offered in Options. The empty code follows the operating system.</summary>
    public static IReadOnlyList<string> Codes { get; } = ["", "en", "de"];

    /// <summary>The name of a language in that language, so it stays readable whatever is currently displayed.</summary>
    public static string DisplayName(string code) => code switch
    {
        "en" => "English",
        "de" => "Deutsch",
        _ => UiText.Get("LanguageSystem")
    };

    /// <summary>The culture for a language code, or null when the code means "follow the operating system".</summary>
    public static CultureInfo? Resolve(string? code) => code is "en" or "de" ? CultureInfo.GetCultureInfo(code) : null;

    /// <summary>Applies a language to this thread and to threads created later. Windows already open keep their text.</summary>
    public static void Apply(string? code)
    {
        var culture = Resolve(code);
        CultureInfo.DefaultThreadCurrentUICulture = culture ?? SystemDefaultThreadCulture;
        CultureInfo.CurrentUICulture = culture ?? SystemCulture;
    }

    /// <summary>
    /// Reads the saved language code from a settings file, or null when the file has none. Unreadable settings
    /// are left for the workbench to report when it loads them.
    /// </summary>
    public static string? ReadCode(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("Language", out var language) &&
                language.ValueKind == JsonValueKind.String ? language.GetString() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Trace.TraceWarning("Display language could not be read from settings: {0}", exception);
            return null;
        }
    }

    /// <summary>Applies the language saved in a settings file. Call this before any window is built.</summary>
    public static void ApplyFromSettings(string path)
    {
        if (ReadCode(path) is { } code) Apply(code);
    }
}
