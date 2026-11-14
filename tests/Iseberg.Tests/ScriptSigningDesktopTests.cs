using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

[Collection(PowerShellPolicyCollection.Name)]
public sealed class ScriptSigningDesktopTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "iseberg-signing-ui-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public async Task SignWindowReturnsTheChosenCertificateAndTimestampServer()
    {
        using var certificate = ScriptSigningTests.CreateCertificate(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        var host = new Window();
        host.Show();
        try
        {
            var window = new SignWindow([certificate], "http://timestamp.example.test");
            var result = window.ShowDialog<SignRequest?>(host);
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.FindControl<TextBlock>("NoCertificates")!.IsVisible);
            Assert.Equal("http://timestamp.example.test", window.FindControl<TextBox>("TimestampServer")!.Text);
            window.FindControl<TextBox>("TimestampServer")!.Text = "  http://tsa.example.test  ";
            window.FindControl<Button>("SignButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var request = await result;
            Assert.NotNull(request);
            Assert.Same(certificate, request.Certificate);
            Assert.Equal("http://tsa.example.test", request.TimestampServer);

            var empty = new SignWindow([], "");
            var emptyResult = empty.ShowDialog<SignRequest?>(host);
            Dispatcher.UIThread.RunJobs();
            Assert.True(empty.FindControl<TextBlock>("NoCertificates")!.IsVisible);
            Assert.False(empty.FindControl<Button>("SignButton")!.IsEnabled);
            empty.Close(null);
            Assert.Null(await emptyResult);
        }
        finally { host.Close(); }
    }

    [AvaloniaFact]
    public async Task SavingEditsToASignedScriptSaysTheSignatureMustBeRenewed()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "signed.ps1");
        await File.WriteAllTextAsync(path, "Write-Output 'signed'\r\n", TestContext.Current.CancellationToken);
        using (var certificate = ScriptSigningTests.CreateCertificate(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1)))
            await ScriptSigning.SignAsync(path, certificate, null, TestContext.Current.CancellationToken);

        var control = new WorkbenchControl(new() { EnableCommandsPane = false });
        var host = new Window { Content = control };
        host.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        try
        {
            host.Show();
            await control.InitializeAsync();
            var untitled = control.CreateDocument("Write-Output 'unsaved'");
            Assert.False(MenuItem(control, "SignScript").IsEnabled);

            await control.OpenFileAsync(path);
            var tab = control.Workbench.SelectedSession!.SelectedFile!;
            Dispatcher.UIThread.RunJobs();
            Assert.True(MenuItem(control, "SignScript").IsEnabled);
            Assert.True(MenuItem(control, "CheckSignature").IsEnabled);

            tab.Document.Replace(0, "Write-Output 'signed'".Length, "Write-Output 'edited'");
            Assert.True(await control.SaveDocumentAsync(tab));
            var status = control.FindControl<TextBlock>("StatusText")!;
            await WaitFor(() => status.Text == UiText.Get("SignatureInvalidatedBySave"));
            Assert.Equal("HashMismatch", (await ScriptSigning.GetAsync(path, TestContext.Current.CancellationToken)).Status);
            Assert.Null(untitled.File.Path);
        }
        finally
        {
            await control.DisposeAsync();
            host.Close();
        }
    }

    [Fact]
    public void EverySignatureStatusHasADescription()
    {
        foreach (var status in Enum.GetNames<System.Management.Automation.SignatureStatus>())
            Assert.DoesNotContain(status + ":", WorkbenchControl.DescribeSignature(new(status, "message", null, null, null)));
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
