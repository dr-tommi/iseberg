using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Iseberg.Core;

namespace Iseberg;

/// <summary>The certificate and timestamp server chosen for signing.</summary>
public sealed record SignRequest(X509Certificate2 Certificate, string TimestampServer);

/// <summary>Chooses a code-signing certificate from the stores or a PFX file, and a timestamp server.</summary>
public sealed class SignWindow : Window
{
    private readonly List<X509Certificate2> loaded = [];

    /// <summary>Certificates this dialog loaded from PFX files. The caller disposes them after signing.</summary>
    public IReadOnlyList<X509Certificate2> LoadedCertificates => loaded;

    public SignWindow(IReadOnlyList<X509Certificate2> certificates, string timestampServer)
    {
        // The control themes come from per-window styles, so apply them before any content is created.
        DesktopTheme.ApplyWindow(this);
        Title = UiText.Get("SignScript").Replace("_", "").TrimEnd('.');
        Width = 680;
        Height = 460;
        MinWidth = 480;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var items = certificates.Select(certificate => new CertificateItem(certificate)).ToList();
        var list = new ListBox { Name = "CertificateList", ItemsSource = items.ToArray() };
        var empty = new TextBlock { Name = "NoCertificates", Text = UiText.Get("NoSigningCertificates"), TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = items.Count == 0 };
        var timestamp = new TextBox { Name = "TimestampServer", Text = timestampServer, PlaceholderText = UiText.Get("TimestampServerHint") };
        var sign = new Button { Name = "SignButton", Content = UiText.Get("SignButton"), IsDefault = true };
        var pfx = new Button { Name = "LoadPfx", Content = UiText.Get("LoadPfx") };
        var cancel = new Button { Content = UiText.Get("Cancel"), IsCancel = true };
        AutomationProperties.SetName(list, UiText.Get("SigningCertificates"));
        AutomationProperties.SetName(timestamp, UiText.Get("TimestampServer"));
        void Select() => sign.IsEnabled = list.SelectedItem is CertificateItem;
        list.SelectionChanged += (_, _) => Select();
        sign.Click += (_, _) =>
        {
            if (list.SelectedItem is CertificateItem item) Close(new SignRequest(item.Certificate, timestamp.Text?.Trim() ?? ""));
        };
        cancel.Click += (_, _) => Close(null);
        pfx.Click += async (_, _) =>
        {
            try
            {
                if (await LoadPfxAsync() is not { } certificate) return;
                items.Add(new CertificateItem(certificate));
                list.ItemsSource = items.ToArray();
                list.SelectedIndex = items.Count - 1;
                empty.IsVisible = false;
            }
            catch (Exception exception) when (exception is CryptographicException or IOException or InvalidOperationException)
            {
                await Dialogs.ChooseAsync(this, Title ?? "", exception.Message, UiText.Get("OK"));
            }
        };

        var grid = new Grid { RowDefinitions = new("Auto,*,Auto,Auto,Auto"), Margin = new Thickness(12), RowSpacing = 8 };
        grid.Children.Add(new TextBlock { Text = UiText.Get("SigningCertificates") });
        var listArea = new Panel();
        listArea.Children.Add(list);
        listArea.Children.Add(empty);
        Grid.SetRow(listArea, 1); grid.Children.Add(listArea);
        var timestampLabel = new TextBlock { Text = UiText.Get("TimestampServer") };
        Grid.SetRow(timestampLabel, 2); grid.Children.Add(timestampLabel);
        Grid.SetRow(timestamp, 3); grid.Children.Add(timestamp);
        var buttons = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(pfx, Dock.Left);
        buttons.Children.Add(pfx);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        right.Children.Add(sign);
        right.Children.Add(cancel);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        Grid.SetRow(buttons, 4); grid.Children.Add(buttons);
        Content = grid;
        Dialogs.RegisterNames(this);
        list.SelectedIndex = items.Count > 0 ? 0 : -1;
        Select();
        Opened += (_, _) => (items.Count > 0 ? (Control)list : pfx).Focus();
    }

    private async Task<X509Certificate2?> LoadPfxAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = UiText.Get("LoadPfx").TrimEnd('.'),
            FileTypeFilter = [new(UiText.Get("PfxFiles")) { Patterns = ["*.pfx", "*.p12"] }]
        });
        if (files.Count == 0) return null;
        var path = files[0].TryGetLocalPath() ?? throw new IOException(UiText.Get("LocalPfxOnly"));
        // The password is used only to open the file; it is not stored or logged.
        var password = await Dialogs.AskAsync(this, UiText.Get("LoadPfx").TrimEnd('.'),
            string.Format(UiText.Get("PfxPassword"), Path.GetFileName(path)), secret: true);
        if (password is null) return null;
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.DefaultKeySet);
        if (!ScriptSigning.IsUsable(certificate, DateTime.Now))
        {
            certificate.Dispose();
            throw new InvalidOperationException(UiText.Get("PfxNotUsable"));
        }
        loaded.Add(certificate);
        return certificate;
    }

    private sealed class CertificateItem(X509Certificate2 certificate)
    {
        public X509Certificate2 Certificate { get; } = certificate;

        public override string ToString() => string.Format(CultureInfo.CurrentCulture, UiText.Get("CertificateLine"),
            Certificate.GetNameInfo(X509NameType.SimpleName, false), Certificate.GetNameInfo(X509NameType.SimpleName, true),
            Certificate.NotAfter, Certificate.Thumbprint);
    }
}
