using System.Management.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace Iseberg.Tests;

[Collection(PowerShellPolicyCollection.Name)]
public sealed class ScriptCorrectionDesktopTests
{
    private static bool ModuleInstalled()
    {
        using var shell = PowerShell.Create();
        return shell.AddCommand("Get-Module").AddParameter("ListAvailable").AddParameter("Name", "PSScriptAnalyzer").Invoke().Count > 0;
    }

    [AvaloniaFact]
    public async Task FormatAndQuickFixRewriteTheScriptAsSingleUndoSteps()
    {
        if (!ModuleInstalled()) return;
        var control = new WorkbenchControl(new() { EnableCommandsPane = false });
        var host = new Window { Content = control };
        host.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        try
        {
            host.Show();
            await control.InitializeAsync();
            const string original = "function F{\r\ngci|% {$_}\r\n}";
            var tab = control.CreateDocument(original);
            var editor = control.ScriptEditorView;

            Click(control, "FormatDocument");
            await WaitFor(() => tab.Document.Text != original);
            Assert.Equal("function F {\r\n    gci | % { $_ }\r\n}", tab.Document.Text);
            Assert.Contains("Ctrl+Z", control.FindControl<TextBlock>("StatusText")!.Text);
            editor.TextEditor.Undo();
            Assert.Equal(original, tab.Document.Text);
            editor.TextEditor.Redo();

            var formatted = tab.Document.Text;
            editor.CaretOffset = formatted.IndexOf("gci", StringComparison.Ordinal) + 1;
            Click(control, "QuickFix");
            await WaitFor(() => tab.Document.Text != formatted);
            Assert.Equal("function F {\r\n    Get-ChildItem | % { $_ }\r\n}", tab.Document.Text);
            Assert.Contains("'gci'", control.FindControl<TextBlock>("StatusText")!.Text);
            editor.TextEditor.Undo();
            Assert.Equal(formatted, tab.Document.Text);

            editor.CaretOffset = 0;
            Click(control, "QuickFix");
            await WaitFor(() => control.FindControl<TextBlock>("StatusText")!.Text == UiText.Get("NoQuickFix"));
            Assert.Equal(formatted, tab.Document.Text);
        }
        finally
        {
            await control.DisposeAsync();
            host.Close();
        }
    }

    [AvaloniaFact]
    public async Task CleanUpIsDiscardedWhenTheScriptChangesMeanwhile()
    {
        if (!ModuleInstalled()) return;
        var control = new WorkbenchControl(new() { EnableCommandsPane = false });
        var host = new Window { Content = control };
        host.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        try
        {
            host.Show();
            await control.InitializeAsync();
            var tab = control.CreateDocument("gci|where {$_}");
            Click(control, "CleanUp");
            // Edit before the background clean-up returns.
            tab.Document.Insert(0, "# edited\r\n");
            await WaitFor(() => control.FindControl<TextBlock>("StatusText")!.Text == UiText.Get("CorrectionDiscarded"));
            Assert.Equal("# edited\r\ngci|where {$_}", tab.Document.Text);

            Click(control, "CleanUp");
            await WaitFor(() => tab.Document.Text.Contains("Get-ChildItem", StringComparison.Ordinal));
            Assert.Equal("# edited\r\nGet-ChildItem | Where-Object { $_ }", tab.Document.Text);
        }
        finally
        {
            await control.DisposeAsync();
            host.Close();
        }
    }

    private static void Click(WorkbenchControl control, string action)
    {
        var item = control.FindControl<Menu>("WorkbenchMenu")!.Items.OfType<MenuItem>()
            .SelectMany(top => top.Items.OfType<MenuItem>()).Single(candidate => candidate.Tag as string == action);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
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
