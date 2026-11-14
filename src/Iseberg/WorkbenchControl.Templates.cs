using Avalonia.Platform.Storage;
using Iseberg.Core;

namespace Iseberg;

public sealed partial class WorkbenchControl
{
    /// <summary>Folder the template picker reads. Hosts and tests may redirect it.</summary>
    public string TemplateDirectory { get; set; } = ScriptTemplateCatalog.UserDirectory;

    private async Task NewFromTemplateAsync()
    {
        if (Workbench.SelectedSession is null) return;
        await ScriptTemplateCatalog.EnsureDirectoryAsync(TemplateDirectory);
        var templates = await ScriptTemplateCatalog.LoadAsync(TemplateDirectory);
        var window = new TemplateWindow(templates, TemplateDirectory);
        window.OpenFolderRequested += async (_, _) =>
        {
            try
            {
                if (await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(TemplateDirectory)) != true)
                    throw new InvalidOperationException(UiText.Get("TemplateFolderOpenFailed"));
            }
            catch (InvalidOperationException exception) { await Dialogs.ChooseAsync(window, window.Title ?? "", exception.Message, UiText.Get("OK")); }
        };
        var template = await window.ShowDialog<ScriptTemplate?>(HostWindow);
        if (template is not null) CreateFromTemplate(template);
    }

    /// <summary>Opens a new untitled script with the template's text, saved later as UTF-8 with BOM.</summary>
    public ScriptTab CreateFromTemplate(ScriptTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (Workbench.SelectedSession is not { } session) throw new InvalidOperationException(UiText.Get("SelectPowerShellTab"));
        var expanded = ScriptTemplateCatalog.Expand(template);
        var file = ScriptFile.CreateUntitled(NextUntitledName(), ScriptTemplateCatalog.Encoding);
        file.Text = expanded.Text;
        file.CaretOffset = expanded.CaretOffset;
        var tab = new ScriptTab(file);
        session.Files.Add(tab);
        session.SelectedFile = tab;
        DisplayFile();
        ScriptEditor.TextArea.Focus();
        return tab;
    }
}
