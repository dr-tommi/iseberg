using System.Management.Automation.Language;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

// One test loads PSScriptAnalyzer, which is CPU-heavy; run outside the parallel pool like ScriptAnalyzerTests.
[Collection(PowerShellPolicyCollection.Name)]
public sealed class ScriptTemplateTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "iseberg-templates-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PlaceholdersAreFilledAndTheCursorMarksTheCaret()
    {
        var expanded = ScriptTemplateCatalog.Expand("# {{Author}} on {{Computer}}, {{Date}}\n{{Cursor}}x{{Cursor}} {{Other}}",
            new DateTime(2026, 3, 7, 23, 59, 0), "tom", "PC1");
        Assert.Equal("# tom on PC1, 2026-03-07\nx {{Other}}", expanded.Text);
        Assert.Equal(expanded.Text.IndexOf('x'), expanded.CaretOffset);
        Assert.Equal(3, ScriptTemplateCatalog.Expand("abc", DateTime.Now, "", "").CaretOffset);
    }

    [Fact]
    public async Task StarterTemplatesAreWrittenOnceAsUtf8WithBom()
    {
        await ScriptTemplateCatalog.EnsureDirectoryAsync(directory, TestContext.Current.CancellationToken);
        var templates = await ScriptTemplateCatalog.LoadAsync(directory, TestContext.Current.CancellationToken);
        Assert.Equal(["Advanced Function", "Script with Logging"], templates.Select(template => template.Name));
        foreach (var template in templates)
        {
            var bytes = await File.ReadAllBytesAsync(template.Path, TestContext.Current.CancellationToken);
            Assert.Equal(Encoding.UTF8.GetPreamble(), bytes.Take(3));
            Assert.Contains("\r\n", template.Text);
        }

        // An existing folder is the user's: deleted or edited templates are not restored.
        File.Delete(templates[0].Path);
        await File.WriteAllTextAsync(Path.Combine(directory, "Mine.ps1"), "# mine", TestContext.Current.CancellationToken);
        await ScriptTemplateCatalog.EnsureDirectoryAsync(directory, TestContext.Current.CancellationToken);
        Assert.Equal(["Mine", "Script with Logging"],
            (await ScriptTemplateCatalog.LoadAsync(directory, TestContext.Current.CancellationToken)).Select(template => template.Name));
    }

    [Fact]
    public async Task MissingFolderHasNoTemplates() =>
        Assert.Empty(await ScriptTemplateCatalog.LoadAsync(directory, TestContext.Current.CancellationToken));

    [Fact]
    public async Task StarterTemplatesParseAndPassTheWindowsPowerShellChecks()
    {
        foreach (var (name, text) in ScriptTemplateCatalog.StarterTemplates)
        {
            var script = ScriptTemplateCatalog.Expand(text, DateTime.Now, "tom", "PC1").Text;
            Parser.ParseInput(script, out _, out var errors);
            Assert.True(errors.Length == 0, $"{name}: {string.Join("; ", errors.Select(error => error.Message))}");
            Assert.Empty(ScriptAnalyzerService.FindUnsupportedSyntax(script));
            Assert.DoesNotContain("{{", script);
        }
        using var analyzer = new ScriptAnalyzerService();
        foreach (var (name, text) in ScriptTemplateCatalog.StarterTemplates)
        {
            var result = await analyzer.AnalyzeAsync(ScriptTemplateCatalog.Expand(text, DateTime.Now, "tom", "PC1").Text,
                new(WindowsPowerShellCompatibility: true, DefaultRules: false), TestContext.Current.CancellationToken);
            if (!result.ModuleAvailable) return;
            Assert.True(result.Issues.Count == 0, $"{name}: {string.Join("; ", result.Issues.Select(issue => issue.Message))}");
        }
    }

    [AvaloniaFact]
    public async Task CreatingFromATemplateOpensAnUntitledUtf8BomScriptAtTheCursor()
    {
        var control = new WorkbenchControl(new() { EnableCommandsPane = false }) { TemplateDirectory = directory };
        var host = new Window { Content = control };
        host.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        try
        {
            host.Show();
            await control.InitializeAsync();
            var tab = control.CreateFromTemplate(new("Test", Path.Combine(directory, "Test.ps1"), "# {{Author}}\n{{Cursor}}Get-Date\n"));
            Assert.Same(tab, control.Workbench.SelectedSession!.SelectedFile);
            Assert.Null(tab.File.Path);
            Assert.StartsWith("Untitled", tab.File.Name);
            Assert.Equal($"# {Environment.UserName}\nGet-Date\n", tab.Document.Text);
            Assert.Equal(ScriptTemplateCatalog.Encoding, tab.File.EncodingChoice);
            Assert.Equal(tab.Document.Text.IndexOf("Get-Date", StringComparison.Ordinal), control.ScriptEditorView.CaretOffset);
        }
        finally
        {
            await control.DisposeAsync();
            host.Close();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
