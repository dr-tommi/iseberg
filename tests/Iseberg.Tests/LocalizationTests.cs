using System.Collections;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

internal static class TestCulture
{
    /// <summary>Tests assert English text, so they must not follow the display language of the machine running them.</summary>
    [ModuleInitializer]
    internal static void UseEnglish()
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }
}

public sealed class LocalizationTests
{
    private static readonly ResourceManager Resources = new("Iseberg.Resources.Strings", typeof(UiText).Assembly);

    private static Dictionary<string, string> Load(CultureInfo culture) =>
        Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!.Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!);

    [Fact]
    public void GermanTranslatesEveryEnglishKeyWithTheSamePlaceholders()
    {
        var english = Load(CultureInfo.InvariantCulture);
        var german = Load(CultureInfo.GetCultureInfo("de"));
        Assert.Equal(english.Keys.Order(), german.Keys.Order());
        static string[] Placeholders(string text) => Regex.Matches(text, @"\{\d+\}").Select(match => match.Value).Order().ToArray();
        foreach (var (key, text) in english)
        {
            Assert.False(string.IsNullOrWhiteSpace(german[key]), key);
            Assert.Equal(Placeholders(text), Placeholders(german[key]));
        }
    }

    [Fact]
    public void GermanMenusHaveUniqueAccessKeys()
    {
        var german = Load(CultureInfo.GetCultureInfo("de"));
        var menus = new Dictionary<string, string[]>
        {
            ["Main"] = ["FileMenu", "EditMenu", "ViewMenu", "ToolsMenu", "DebugMenu", "AddonsMenu", "HelpMenu"],
            ["File"] = ["New", "NewFromTemplate", "Open", "Save", "SaveAs", "SaveAll", "Encoding", "Reload", "Print", "SignScript", "CheckSignature", "Close", "RecentFiles",
                "NewSession", "NewRemoteSession", "OpenRemoteFile", "ExitRemoteSession", "CloseSession", "Exit"],
            ["Edit"] = ["Undo", "Redo", "Cut", "Copy", "Paste", "SelectAll", "Find", "Replace", "GoToLine", "MatchBrace", "SelectBrace",
                "FormatDocument", "QuickFix", "CleanUp"],
            ["View"] = ["PaneTop", "PaneRight", "PaneMaximized", "Commands", "LineNumbers", "WordWrap", "Fold", "FocusScript",
                "FocusConsole", "ZoomIn", "ZoomOut"],
            ["Tools"] = ["Run", "RunSelection", "Stop", "Complete", "ShowCommand", "Snippets", "CreateSnippet", "ImportSnippets",
                "ExportSnippets", "Clear", "Profiles", "AutoProfiles", "ExecutionPolicy", "OptionsMenu"],
            ["Debug"] = ["Breakpoint", "DebuggerPanes", "NewBreakpoint", "BreakAll", "RemoveBreakpoints", "StepInto", "StepOver",
                "StepOut", "Continue", "Stop"],
            ["Help"] = ["Help", "CheckForUpdatesMenu", "About"]
        };
        foreach (var (menu, keys) in menus)
        {
            var accessKeys = keys.Select(key => german[key]).Where(text => text.Contains('_'))
                .Select(text => char.ToUpperInvariant(text[text.IndexOf('_') + 1])).ToArray();
            Assert.True(accessKeys.Distinct().Count() == accessKeys.Length, $"{menu}: {new string(accessKeys)}");
        }
    }

    [Fact]
    public void UiTextFollowsTheThreadDisplayLanguage()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("_Datei", UiText.Get("FileMenu"));
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            Assert.Equal("_File", UiText.Get("FileMenu"));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public void LanguageCodesResolveToSupportedCulturesOnly()
    {
        Assert.Equal("de", UiLanguage.Resolve("de")!.Name);
        Assert.Equal("en", UiLanguage.Resolve("en")!.Name);
        Assert.Null(UiLanguage.Resolve(""));
        Assert.Null(UiLanguage.Resolve("fr"));
        Assert.Equal(["", "en", "de"], UiLanguage.Codes);
        Assert.Equal("Deutsch", UiLanguage.DisplayName("de"));
    }

    [Fact]
    public async Task SavedLanguageIsReadBeforeTheWorkbenchLoadsSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iseberg-language-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            Assert.Null(UiLanguage.ReadCode(path));
            await new UserSettings { Language = "de" }.SaveAsync(path);
            Assert.Equal("de", UiLanguage.ReadCode(path));
            Assert.Equal("de", (await UserSettings.LoadAsync(path)).Language);
            await File.WriteAllTextAsync(path, "{ not json");
            Assert.Null(UiLanguage.ReadCode(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void UnsupportedSavedLanguageFallsBackToTheSystemLanguage()
    {
        var settings = new UserSettings { Language = "fr" };
        settings.Normalize();
        Assert.Equal("", settings.Language);
    }
}
