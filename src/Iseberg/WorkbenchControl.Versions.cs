using Avalonia.Styling;
using Iseberg.Core;

namespace Iseberg;

public sealed partial class WorkbenchControl
{
    private string? fileHistoryDirectory;

    /// <summary>
    /// The folder for file versions. Without persistence and without an explicit folder no versions are kept,
    /// so hosts and tests never write into the user's profile by accident.
    /// </summary>
    public string? FileHistoryDirectory
    {
        // Next to the settings file, so a redirected settings path also redirects the history.
        get => fileHistoryDirectory ?? (hostingOptions.EnablePersistence
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsFilePath ?? UserSettings.SettingsPath)) ?? FileHistory.DefaultDirectory, "History")
            : null);
        set => fileHistoryDirectory = value;
    }

    private FileHistory? History => settings.KeepFileVersions && FileHistoryDirectory is { } directory
        ? new FileHistory(directory) { MaxVersions = settings.MaxFileVersions, MaxAgeDays = settings.FileVersionDays }
        : null;

    private bool CanShowFileVersions => displayedFile?.File is { Path: not null, IsRemote: false } && FileHistoryDirectory is not null && !windowClosed;

    private bool CanCompareWithSaved => displayedFile?.File is { Path: not null, IsRemote: false } && !windowClosed;

    /// <summary>Before the first versioned save of an existing file, keeps its state on disk so the original is not lost.</summary>
    private async Task KeepVersionBeforeSaveAsync(string path)
    {
        if (History is not { } history || !File.Exists(path)) return;
        try
        {
            if (history.List(path).Count == 0) await history.AddFromDiskAsync(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowCorrectionNotice(string.Format(UiText.Get("FileVersionFailed"), exception.Message));
        }
    }

    /// <summary>Keeps the saved state as a version. A failure is reported but never fails the save itself.</summary>
    private async Task KeepVersionAfterSaveAsync(ScriptTab tab)
    {
        if (History is not { } history || tab.File is not { Path: { } path, IsRemote: false }) return;
        try
        {
            await history.AddFromDiskAsync(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowCorrectionNotice(string.Format(UiText.Get("FileVersionFailed"), exception.Message));
        }
    }

    private DiffAppearance CurrentDiffAppearance() =>
        new(ScriptEditor.FontFamily, Math.Max(10, settings.FontSize * 4 / 3), ScriptEditor.ActualThemeVariant == ThemeVariant.Dark);

    private async Task ShowFileVersionsAsync()
    {
        if (!CanShowFileVersions || displayedFile is not { } tab || FileHistoryDirectory is not { } directory) return;
        var history = new FileHistory(directory) { MaxVersions = settings.MaxFileVersions, MaxAgeDays = settings.FileVersionDays };
        var window = new FileVersionsWindow(history, tab.File.Path!, ScriptEditorControl.Document.Text, tab.File.SavedVersion, CurrentDiffAppearance());
        var result = await window.ShowDialog<FileVersionResult?>(HostWindow);
        if (window.HistoryCleared) ShowCorrectionNotice(UiText.Get("FileHistoryCleared"));
        if (result is null) return;
        if (result.Choice == FileVersionChoice.OpenAsNew)
        {
            CreateDocument(result.Text);
            return;
        }
        // Replacing the whole text is one edit, so Undo brings the previous text back; nothing is written to disk.
        if (displayedFile != tab) return;
        var document = ScriptEditorControl.Document;
        document.Replace(0, document.TextLength, result.Text);
        ShowCorrectionNotice(string.Format(UiText.Get("FileVersionRestored"), result.SavedUtc.ToLocalTime()));
    }

    private async Task CompareWithSavedAsync()
    {
        if (!CanCompareWithSaved || displayedFile?.File.Path is not { } path) return;
        string saved;
        try { saved = (await ScriptFile.OpenAsync(path)).Text; }
        catch (FileNotFoundException) { saved = ""; }
        var lines = LineDiff.Compare(saved, ScriptEditorControl.Document.Text);
        var window = new DiffWindow(string.Format(UiText.Get("CompareWithSavedTitle"), Path.GetFileName(path)), lines, CurrentDiffAppearance());
        await window.ShowDialog(HostWindow);
    }
}
