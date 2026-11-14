using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Iseberg.Core;

namespace Iseberg;

/// <summary>Chooses a script template. Closes with the template, or with null when canceled.</summary>
public sealed class TemplateWindow : Window
{
    /// <summary>Raised when the user asks to open the template folder; the dialog stays open.</summary>
    public event EventHandler? OpenFolderRequested;

    public TemplateWindow(IReadOnlyList<ScriptTemplate> templates, string directory)
    {
        // The control themes come from per-window styles, so apply them before any content is created.
        DesktopTheme.ApplyWindow(this);
        Title = UiText.Get("NewFromTemplate").Replace("_", "").TrimEnd('.');
        Width = 700;
        Height = 520;
        MinWidth = 500;
        MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var list = new ListBox { Name = "TemplateList", ItemsSource = templates, DisplayMemberBinding = new Avalonia.Data.Binding(nameof(ScriptTemplate.Name)) };
        var location = new TextBlock
        {
            Name = "TemplateLocation", TextWrapping = TextWrapping.Wrap,
            Text = templates.Count == 0 ? string.Format(UiText.Get("NoTemplates"), directory) : directory
        };
        var preview = new TextBox
        {
            Name = "TemplatePreview", IsReadOnly = true, AcceptsReturn = true,
            FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, Menlo, monospace")
        };
        var create = new Button { Name = "CreateFromTemplate", Content = UiText.Get("CreateFromTemplate"), IsDefault = true };
        var folder = new Button { Name = "OpenTemplateFolder", Content = UiText.Get("OpenTemplateFolder") };
        var cancel = new Button { Content = UiText.Get("Cancel"), IsCancel = true };
        AutomationProperties.SetName(list, UiText.Get("TemplatesTitle"));
        AutomationProperties.SetName(preview, UiText.Get("TemplatePreview"));
        void Select()
        {
            var template = list.SelectedItem as ScriptTemplate;
            preview.Text = template?.Text ?? "";
            create.IsEnabled = template is not null;
        }
        list.SelectionChanged += (_, _) => Select();
        list.DoubleTapped += (_, _) => { if (list.SelectedItem is ScriptTemplate template) Close(template); };
        create.Click += (_, _) => Close(list.SelectedItem as ScriptTemplate);
        folder.Click += (_, _) => OpenFolderRequested?.Invoke(this, EventArgs.Empty);
        cancel.Click += (_, _) => Close(null);

        var grid = new Grid { RowDefinitions = new("*,Auto,2*,Auto"), Margin = new Thickness(12), RowSpacing = 8 };
        grid.Children.Add(list);
        Grid.SetRow(location, 1); grid.Children.Add(location);
        Grid.SetRow(preview, 2); grid.Children.Add(preview);
        var buttons = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(folder, Dock.Left);
        buttons.Children.Add(folder);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        right.Children.Add(create);
        right.Children.Add(cancel);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        Grid.SetRow(buttons, 3); grid.Children.Add(buttons);
        Content = grid;
        Dialogs.RegisterNames(this);
        list.SelectedIndex = templates.Count > 0 ? 0 : -1;
        Select();
        Opened += (_, _) => list.Focus();
    }
}
