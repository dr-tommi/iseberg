using AvaloniaEdit.Document;
using Iseberg.Core;

namespace Iseberg;

public sealed partial class WorkbenchControl
{
    /// <summary>Actions that rewrite the script text and need an editable script.</summary>
    internal static bool IsCorrectionAction(string action) => action is "FormatDocument" or "QuickFix" or "CleanUp";

    private bool CanCorrectScript => displayedFile is not null && !ScriptEditor.IsReadOnly && !windowClosed;

    /// <summary>The outcome of the last correction; kept in the Ready status until the user types or switches files.</summary>
    private string? correctionNotice;

    private void ConfigureCorrections()
    {
        ScriptEditor.TextArea.TextEntered += (_, _) => ClearCorrectionNotice();
    }

    private void ClearCorrectionNotice()
    {
        if (correctionNotice is null) return;
        correctionNotice = null;
        RefreshState();
    }

    private void ShowCorrectionNotice(string message)
    {
        correctionNotice = message;
        StatusText.Text = message;
    }

    /// <summary>Formats the selection, or the whole script when nothing is selected.</summary>
    private async Task FormatDocumentAsync()
    {
        if (!CanCorrectScript) return;
        var document = ScriptEditor.Document;
        var text = document.Text;
        int[]? range = null;
        if (ScriptEditor.SelectionLength > 0)
        {
            var start = document.GetLocation(ScriptEditor.SelectionStart);
            var end = document.GetLocation(ScriptEditor.SelectionStart + ScriptEditor.SelectionLength);
            range = [start.Line, start.Column, end.Line, end.Column];
        }
        scriptAnalyzer ??= new();
        var formatted = await scriptAnalyzer.FormatAsync(text, range);
        ApplyCorrectedText(document, text, formatted, UiText.Get("ScriptFormatted"));
    }

    /// <summary>Applies the suggested correction of the finding at the caret.</summary>
    private async Task QuickFixAsync()
    {
        if (!CanCorrectScript) return;
        var document = ScriptEditor.Document;
        var text = document.Text;
        var caret = ScriptEditor.CaretOffset;
        scriptAnalyzer ??= new();
        // Analyze the current text with the default rules, so fixes are found even while their underlines are off.
        var result = await scriptAnalyzer.AnalyzeAsync(text, new(WindowsPowerShellCompatibility: false, DefaultRules: true));
        if (!result.ModuleAvailable)
        {
            ApplyCorrectedText(document, text, null, "");
            return;
        }
        var fix = result.Issues
            .Where(issue => issue.Start <= caret && caret <= issue.Start + issue.Length)
            .SelectMany(issue => issue.Fixes)
            .FirstOrDefault(correction => correction.Start <= caret && caret <= correction.Start + correction.Length) ??
            result.Issues.Where(issue => issue.Start <= caret && caret <= issue.Start + issue.Length)
                .SelectMany(issue => issue.Fixes).FirstOrDefault();
        if (fix is null)
        {
            ShowCorrectionNotice(UiText.Get("NoQuickFix"));
            return;
        }
        var corrected = ScriptAnalyzerService.ApplyCorrections(text, [fix]);
        ApplyCorrectedText(document, text, corrected,
            string.Format(UiText.Get("QuickFixApplied"), text.Substring(fix.Start, fix.Length), fix.Text));
    }

    /// <summary>Applies every available correction, then formats the script.</summary>
    private async Task CleanUpAsync()
    {
        if (!CanCorrectScript) return;
        var document = ScriptEditor.Document;
        var text = document.Text;
        scriptAnalyzer ??= new();
        var cleaned = await scriptAnalyzer.CleanUpAsync(text);
        ApplyCorrectedText(document, text, cleaned, UiText.Get("ScriptCleanedUp"));
    }

    /// <summary>
    /// Replaces the text that changed between the original and the corrected script as one undo step. Nothing is
    /// replaced when the script was edited or switched meanwhile, or when PSScriptAnalyzer is unavailable (null).
    /// </summary>
    private void ApplyCorrectedText(TextDocument document, string original, string? corrected, string status)
    {
        if (windowClosed) return;
        if (corrected is null)
        {
            ShowCorrectionNotice(string.Format(UiText.Get("ScriptAnalyzerUnavailable"), scriptAnalyzer?.ModuleError));
            return;
        }
        if (ScriptEditor.Document != document || document.Text != original || ScriptEditor.IsReadOnly)
        {
            ShowCorrectionNotice(UiText.Get("CorrectionDiscarded"));
            return;
        }
        if (corrected == original)
        {
            ShowCorrectionNotice(UiText.Get("NothingToCorrect"));
            return;
        }
        // Replace only the changed middle part, so the caret, folds and anchored markers outside it are kept.
        var prefix = 0;
        var limit = Math.Min(original.Length, corrected.Length);
        while (prefix < limit && original[prefix] == corrected[prefix]) prefix++;
        var suffix = 0;
        while (suffix < limit - prefix && original[^(suffix + 1)] == corrected[^(suffix + 1)]) suffix++;
        document.Replace(prefix, original.Length - prefix - suffix, corrected.Substring(prefix, corrected.Length - prefix - suffix));
        ShowCorrectionNotice(status);
        ScriptEditor.TextArea.Focus();
    }
}
