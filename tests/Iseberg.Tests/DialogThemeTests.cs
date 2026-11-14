using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

public sealed class DialogThemeTests
{
    /// <summary>
    /// Control themes come from per-window styles. A dialog that adds them only after building its content shows
    /// input fields, lists and check boxes without templates: they render as empty space.
    /// </summary>
    [AvaloniaFact]
    public void DialogInputsReceiveTheirControlTemplates()
    {
        var windows = new Window[]
        {
            new SnippetWindow([new("Title", "Description", "", "Get-Date")]),
            new TemplateWindow([new("Template", "Template.ps1", "Get-Date")], Path.GetTempPath()),
            new ReplaceWindow(new TextEditor()),
            new EncodingWindow(new(65001, false), canReload: true),
            new RemoteConnectionWindow()
        };
        foreach (var window in windows)
        {
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var inputs = window.GetVisualDescendants().OfType<Control>()
                    // Only the dialog's own controls; parts inside templates, such as a ComboBox's hidden editor, may stay empty.
                    .Where(control => control is TextBox or ListBox or CheckBox or ComboBox && control.TemplatedParent is null).ToArray();
                Assert.NotEmpty(inputs);
                Assert.All(inputs, control => Assert.True(control.GetVisualChildren().Any(),
                    $"{window.GetType().Name}: {control.GetType().Name} '{control.Name}' has no template."));
            }
            finally { window.Close(); }
        }
    }
}
