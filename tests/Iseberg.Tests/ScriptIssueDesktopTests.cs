using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

[Collection(PowerShellPolicyCollection.Name)]
public sealed class ScriptIssueDesktopTests
{
    [AvaloniaFact]
    public async Task CompatibilityIssuesAreUnderlinedListedAndFollowEdits()
    {
        var control = new WorkbenchControl(new()
        {
            EnableCommandsPane = false,
            Preferences = new UserSettings { CheckWindowsPowerShellCompatibility = true }
        });
        var host = new Window { Content = control };
        host.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        try
        {
            host.Show();
            await control.InitializeAsync();
            const string text = "$value = 1\n$length = ${value}?.Length";
            var document = control.CreateDocument(text);
            await control.ScriptEditorView.AnalyzeAsync();
            // The built-in check needs no module, so this holds on machines without PSScriptAnalyzer too.
            await WaitFor(() => control.ScriptIssueMarkers.Any(marker => marker.Rule == ScriptAnalyzerService.BuiltInRule));
            var marker = control.ScriptIssueMarkers.Single(candidate => candidate.Rule == ScriptAnalyzerService.BuiltInRule);
            Assert.Equal(ScriptIssueSeverity.Error, marker.Severity);
            Assert.Equal("${value}?.Length", document.Document.GetText(marker.Start.Offset, marker.End.Offset - marker.Start.Offset));
            Assert.Contains("Windows PowerShell 5.1", control.ScriptIssueMessageAt(marker.Start.Offset + 1));
            Assert.Null(control.ScriptIssueMessageAt(0));
            var diagnostics = control.FindControl<TextBlock>("Diagnostics")!;
            Assert.True(diagnostics.IsVisible);
            Assert.StartsWith("Line 2: ", diagnostics.Text);

            // The underline moves with the text until the next analysis replaces it.
            var offset = marker.Start.Offset;
            document.Document.Insert(0, "# comment\n");
            Assert.Equal(offset + 10, marker.Start.Offset);

            document.Document.Text = "$length = $value.Length";
            await control.ScriptEditorView.AnalyzeAsync();
            await WaitFor(() => !control.ScriptIssueMarkers.Any());
            Assert.False(diagnostics.IsVisible);
        }
        finally
        {
            await control.DisposeAsync();
            host.Close();
        }
    }

    [AvaloniaFact]
    public async Task SyntaxErrorsAreUnderlinedWithoutOptionalAnalysis()
    {
        var control = new WorkbenchControl(new() { EnableCommandsPane = false });
        var host = new Window { Content = control };
        host.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        try
        {
            host.Show();
            await control.InitializeAsync();
            control.CreateDocument("$a = ${b}?.Length\nif (");
            await control.ScriptEditorView.AnalyzeAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.NotEmpty(control.ScriptIssueMarkers);
            Assert.All(control.ScriptIssueMarkers, marker => Assert.Equal("", marker.Rule));
            await Task.Delay(800);
            Dispatcher.UIThread.RunJobs();
            // Compatibility checks are opt-in: PowerShell 7 syntax is not flagged by default.
            Assert.DoesNotContain(control.ScriptIssueMarkers, marker => marker.Rule == ScriptAnalyzerService.BuiltInRule);
        }
        finally
        {
            await control.DisposeAsync();
            host.Close();
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Assert.True(condition());
    }
}
