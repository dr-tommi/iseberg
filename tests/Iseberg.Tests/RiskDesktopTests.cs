using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

[Collection(PowerShellPolicyCollection.Name)]
public sealed class RiskDesktopTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "iseberg-risk-ui-" + Guid.NewGuid().ToString("N"));

    // Unsaved test documents would otherwise also ask to be saved before running.
    private WorkbenchControl CreateControl(UserSettings? settings = null) =>
        new(new() { EnableCommandsPane = false, Preferences = settings ?? new UserSettings { PromptToSaveBeforeRun = false } })
        { RiskRulesPath = Path.Combine(directory, "RiskRules.json") };

    [AvaloniaFact]
    public async Task RiskyCommandsAreShadedExplainedAndCounted()
    {
        var control = CreateControl();
        var host = new Window { Content = control };
        host.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        try
        {
            host.Show();
            await control.InitializeAsync();
            const string text = "Get-CsOnlineUser\nSet-CsOnlineVoiceRoute -Identity Test\nStop-Service Spooler";
            var tab = control.CreateDocument(text);
            await control.ScriptEditorView.AnalyzeAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.True(File.Exists(control.RiskRulesPath));
            Assert.Equal([RiskLevel.High, RiskLevel.Medium], control.RiskMarkers.Select(marker => marker.Level));
            var high = control.RiskMarkers[0];
            Assert.Equal("Set-CsOnlineVoiceRoute", tab.Document.GetText(high.Start.Offset, high.End.Offset - high.Start.Offset));
            Assert.Contains("Set-Cs*", control.ScriptIssueMessageAt(high.Start.Offset + 2));
            Assert.Null(control.ScriptIssueMessageAt(2));
            var diagnostics = control.FindControl<TextBlock>("Diagnostics")!;
            Assert.Contains(string.Format(UiText.Get("RiskCountHigh"), 1), diagnostics.Text);
            Assert.Contains(string.Format(UiText.Get("RiskCountMedium"), 1), diagnostics.Text);

            // Edited rules apply on the next scan; invalid rules keep the previous ones and say so.
            await File.WriteAllTextAsync(control.RiskRulesPath, """{ "High": [ { "Pattern": "Get-CsOnlineUser", "Reason": "test" } ] }""",
                TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(control.RiskRulesPath, DateTime.UtcNow.AddMinutes(1));
            tab.Document.Insert(0, "# changed\n");
            await control.ScriptEditorView.AnalyzeAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["Get-CsOnlineUser"], control.RiskMarkers.Select(marker => marker.Command));
            await File.WriteAllTextAsync(control.RiskRulesPath, "{ broken", TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(control.RiskRulesPath, DateTime.UtcNow.AddMinutes(2));
            tab.Document.Insert(0, "# again\n");
            await control.ScriptEditorView.AnalyzeAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["Get-CsOnlineUser"], control.RiskMarkers.Select(marker => marker.Command));
            Assert.Contains(UiText.Get("RiskRulesInvalid")[..20], diagnostics.Text);

            Click(control, "MarkRisks");
            await WaitFor(() => control.RiskMarkers.Count == 0);
            Assert.DoesNotContain("Get-CsOnlineUser", diagnostics.Text ?? "");
        }
        finally
        {
            await control.DisposeAsync();
            host.Close();
        }
    }

    [AvaloniaFact]
    public async Task RunningHighRiskCommandsAsksFirst()
    {
        var control = CreateControl();
        var host = new Window { Content = control };
        host.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        try
        {
            host.Show();
            await control.InitializeAsync();
            var marker = Path.Combine(directory, "ran.txt");
            Directory.CreateDirectory(directory);
            // Remove-Item -WhatIf is only medium risk; Remove-Variable is high risk but harmless here.
            control.CreateDocument($"Remove-Item 'C:\\nothing' -WhatIf -ErrorAction SilentlyContinue\nRemove-Variable -Name unused -ErrorAction SilentlyContinue\nSet-Content -LiteralPath '{marker}' -Value ran");

            var canceled = control.RunDocumentAsync();
            await WaitFor(() => host.OwnedWindows.Count == 1);
            var prompt = host.OwnedWindows.Single();
            Assert.Contains("Remove-Variable", string.Join(" ", prompt.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text)));
            ClickChoice(prompt, UiText.Get("Cancel"));
            await canceled;
            Assert.False(File.Exists(marker));

            var confirmed = control.RunDocumentAsync();
            await WaitFor(() => host.OwnedWindows.Count == 1);
            ClickChoice(host.OwnedWindows.Single(), UiText.Get("RunAnyway"));
            await confirmed;
            await WaitFor(() => File.Exists(marker));
        }
        finally
        {
            await control.DisposeAsync();
            host.Close();
        }
    }

    [AvaloniaFact]
    public async Task ThePromptCanBeTurnedOff()
    {
        var control = CreateControl(new UserSettings { ConfirmRiskyRuns = false, PromptToSaveBeforeRun = false });
        var host = new Window { Content = control };
        host.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        try
        {
            host.Show();
            await control.InitializeAsync();
            control.CreateDocument("Remove-Variable -Name unused -ErrorAction SilentlyContinue");
            await control.RunDocumentAsync();
            Assert.Empty(host.OwnedWindows);
        }
        finally
        {
            await control.DisposeAsync();
            host.Close();
        }
    }

    private static void Click(WorkbenchControl control, string action) => control.FindControl<Menu>("WorkbenchMenu")!.Items.OfType<MenuItem>()
        .SelectMany(top => top.Items.OfType<MenuItem>()).Single(item => item.Tag as string == action)
        .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static void ClickChoice(Window window, string label) => window.GetLogicalDescendants().OfType<Button>()
        .Single(button => button.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Assert.True(condition());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
