using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

[Collection(PowerShellPolicyCollection.Name)]
public sealed class FileVersionsDesktopTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "iseberg-versions-ui-" + Guid.NewGuid().ToString("N"));

    private string HistoryDirectory => Path.Combine(directory, "history");

    private WorkbenchControl CreateControl(UserSettings? settings = null, bool history = true)
    {
        var control = new WorkbenchControl(new() { EnableCommandsPane = false, Preferences = settings ?? new UserSettings { PromptToSaveBeforeRun = false } });
        if (history) control.FileHistoryDirectory = HistoryDirectory;
        return control;
    }

    private async Task<(WorkbenchControl Control, Window Host, ScriptTab Tab, string Path)> OpenAsync(WorkbenchControl control, string text)
    {
        var host = new Window { Content = control };
        host.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        host.Show();
        await control.InitializeAsync();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Script.ps1");
        await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        await control.OpenFileAsync(path);
        Dispatcher.UIThread.RunJobs();
        return (control, host, control.Workbench.SelectedSession!.SelectedFile!, path);
    }

    [AvaloniaFact]
    public async Task SavingKeepsTheOriginalAndEachNewState()
    {
        var (control, host, tab, path) = await OpenAsync(CreateControl(), "Get-Date\n");
        try
        {
            var history = new FileHistory(HistoryDirectory);
            Assert.Empty(history.List(path));
            tab.Document.Insert(tab.Document.TextLength, "Get-Host\n");
            Assert.True(await control.SaveDocumentAsync(tab));
            Assert.Equal(2, history.List(path).Count);
            // Saving an unchanged file adds nothing.
            Assert.True(await control.SaveDocumentAsync(tab));
            Assert.Equal(2, history.List(path).Count);
            tab.Document.Insert(0, "# third\n");
            Assert.True(await control.SaveDocumentAsync(tab));
            var versions = history.List(path);
            Assert.Equal(3, versions.Count);
            Assert.Equal("Get-Date\n", await File.ReadAllTextAsync(versions[^1].StoragePath, TestContext.Current.CancellationToken));
            Assert.Equal(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken), await FileHistory.ReadAsync(versions[0]));
        }
        finally
        {
            await control.DisposeAsync();
            host.Close();
        }
    }

    [AvaloniaFact]
    public async Task VersionsCanBeComparedRestoredAndOpened()
    {
        var (control, host, tab, path) = await OpenAsync(CreateControl(), "Get-Date\nGet-Host\n");
        try
        {
            tab.Document.Replace(0, "Get-Date".Length, "Get-Item");
            Assert.True(await control.SaveDocumentAsync(tab));
            tab.Document.Insert(tab.Document.TextLength, "Get-Process\n");
            Assert.True(MenuItem(control, "FileVersions").IsEnabled);

            // The newest version is the saved file; the editor adds one line.
            Click(control, "FileVersions");
            var window = await WaitForWindow<FileVersionsWindow>(host);
            var list = window.FindControl<ListBox>("VersionList")!;
            Assert.Equal(2, list.ItemCount);
            Assert.Contains(UiText.Get("FileVersionSaved"), list.Items[0]!.ToString());
            Assert.Equal((1, 0), LineDiff.Count(window.DiffView.Lines));
            Assert.Equal(string.Format(UiText.Get("DiffCounts"), 1, 0), window.FindControl<TextBlock>("DiffSummary")!.Text);

            // The original against the previous (none) shows the whole file; against the editor the rename and the new line.
            list.SelectedIndex = 1;
            Assert.Equal((2, 1), LineDiff.Count(window.DiffView.Lines));
            window.FindControl<ComboBox>("CompareTarget")!.SelectedIndex = 1;
            Assert.Equal((2, 0), LineDiff.Count(window.DiffView.Lines));
            Press(window, "RestoreVersion");
            await WaitFor(() => tab.Document.Text == "Get-Date\nGet-Host\n");
            Assert.True(tab.File.IsDirty);
            // Restoring is a single edit that Undo takes back; the file on disk is unchanged.
            tab.Document.UndoStack.Undo();
            Assert.Equal("Get-Item\nGet-Host\nGet-Process\n", tab.Document.Text);
            Assert.Equal("Get-Item\nGet-Host\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

            Click(control, "FileVersions");
            window = await WaitForWindow<FileVersionsWindow>(host);
            window.FindControl<ListBox>("VersionList")!.SelectedIndex = 1;
            Press(window, "OpenVersion");
            await WaitFor(() => control.Workbench.SelectedSession!.SelectedFile != tab);
            Assert.Equal("Get-Date\nGet-Host\n", control.Workbench.SelectedSession!.SelectedFile!.Document.Text);
            Assert.Null(control.Workbench.SelectedSession!.SelectedFile!.File.Path);
        }
        finally
        {
            await control.DisposeAsync();
            host.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheHistoryCanBeDeleted()
    {
        var (control, host, tab, path) = await OpenAsync(CreateControl(), "Get-Date\n");
        try
        {
            tab.Document.Insert(0, "# edit\n");
            Assert.True(await control.SaveDocumentAsync(tab));
            Click(control, "FileVersions");
            var window = await WaitForWindow<FileVersionsWindow>(host);
            Press(window, "ClearHistory");
            await WaitFor(() => window.OwnedWindows.Count == 1);
            var prompt = window.OwnedWindows.Single();
            prompt.GetLogicalDescendants().OfType<Button>().Single(button => button.Content as string == UiText.Get("DeleteButton"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var status = control.FindControl<TextBlock>("StatusText")!;
            await WaitFor(() => status.Text == UiText.Get("FileHistoryCleared"));
            Assert.Empty(new FileHistory(HistoryDirectory).List(path));
        }
        finally
        {
            await control.DisposeAsync();
            host.Close();
        }
    }

    [AvaloniaFact]
    public async Task UnsavedEditsCanBeComparedWithTheFile()
    {
        var (control, host, tab, _) = await OpenAsync(CreateControl(), "Get-Date\nGet-Host\n");
        try
        {
            tab.Document.Replace(0, "Get-Date".Length, "Get-Item");
            Click(control, "CompareWithSaved");
            var window = await WaitForWindow<DiffWindow>(host);
            Assert.Equal([DiffKind.Removed, DiffKind.Added, DiffKind.Same], window.DiffView.Lines.Select(line => line.Kind));
            Assert.Equal("Get-Date", window.DiffView.Lines[0].Text);
            Assert.Equal("Get-Item", window.DiffView.Lines[1].Text);
            window.Close();
            await WaitFor(() => host.OwnedWindows.Count == 0);
        }
        finally
        {
            await control.DisposeAsync();
            host.Close();
        }
    }

    [AvaloniaFact]
    public async Task NoVersionsAreKeptWhenTurnedOffOrWithoutAFolder()
    {
        var (control, host, tab, path) = await OpenAsync(CreateControl(new UserSettings { KeepFileVersions = false, PromptToSaveBeforeRun = false }), "Get-Date\n");
        try
        {
            tab.Document.Insert(0, "# edit\n");
            Assert.True(await control.SaveDocumentAsync(tab));
            Assert.Empty(new FileHistory(HistoryDirectory).List(path));
            Click(control, "KeepFileVersions");
            tab.Document.Insert(0, "# again\n");
            Assert.True(await control.SaveDocumentAsync(tab));
            Assert.Equal(2, new FileHistory(HistoryDirectory).List(path).Count);
        }
        finally
        {
            await control.DisposeAsync();
            host.Close();
        }

        // Hosts without persistence and without a folder keep nothing and hide nothing in the user's profile.
        var bare = CreateControl(history: false);
        var bareHost = new Window { Content = bare };
        try
        {
            bareHost.Show();
            await bare.InitializeAsync();
            Assert.Null(bare.FileHistoryDirectory);
            await bare.OpenFileAsync(path);
            Dispatcher.UIThread.RunJobs();
            Assert.False(MenuItem(bare, "FileVersions").IsEnabled);
        }
        finally
        {
            await bare.DisposeAsync();
            bareHost.Close();
        }
    }

    private static MenuItem MenuItem(WorkbenchControl control, string action)
    {
        var item = control.FindControl<Menu>("WorkbenchMenu")!.Items.OfType<MenuItem>()
            .SelectMany(top => top.Items.OfType<MenuItem>()).Single(candidate => candidate.Tag as string == action);
        // Menu states refresh when a submenu opens.
        ((MenuItem)item.Parent!).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.MenuItem.SubmenuOpenedEvent));
        Dispatcher.UIThread.RunJobs();
        return item;
    }

    private static void Click(WorkbenchControl control, string action) => control.FindControl<Menu>("WorkbenchMenu")!.Items.OfType<MenuItem>()
        .SelectMany(top => top.Items.OfType<MenuItem>()).Single(item => item.Tag as string == action)
        .RaiseEvent(new RoutedEventArgs(Avalonia.Controls.MenuItem.ClickEvent));

    private static void Press(Window window, string name) =>
        window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task<T> WaitForWindow<T>(Window host) where T : Window
    {
        await WaitFor(() => host.OwnedWindows.OfType<T>().Any());
        return host.OwnedWindows.OfType<T>().Single();
    }

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
