using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Iseberg.Core;

namespace Iseberg;

/// <summary>A risky command call anchored in a document, so it follows edits until the next scan replaces it.</summary>
public sealed record RiskMarker(TextAnchor Start, TextAnchor End, RiskLevel Level, string Command, string Message)
{
    public bool IsVisible => !Start.IsDeleted && !End.IsDeleted && End.Offset > Start.Offset;
}

/// <summary>Shades risky commands behind the text: light red for high risk, light orange for medium risk.</summary>
public sealed class RiskRenderer(Func<IEnumerable<RiskMarker>> markers) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public static Color ColorOf(RiskLevel level) => level == RiskLevel.High ? Color.FromArgb(70, 229, 20, 0) : Color.FromArgb(70, 230, 150, 0);

    public void Draw(TextView textView, DrawingContext context)
    {
        if (!textView.VisualLinesValid || textView.Document is null || DesktopTheme.HighContrast) return;
        foreach (var marker in markers())
        {
            if (!marker.IsVisible || marker.Start.Document != textView.Document) continue;
            var brush = new SolidColorBrush(ColorOf(marker.Level));
            var segment = new TextSegment { StartOffset = marker.Start.Offset, EndOffset = marker.End.Offset };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                context.DrawRectangle(brush, null, new Rect(rect.Left - 1, rect.Top, rect.Width + 2, rect.Height), 2, 2);
        }
    }
}

public sealed partial class WorkbenchControl
{
    private IReadOnlyList<RiskMarker> riskMarkers = [];
    private RiskRuleSet riskRules = RiskRuleSet.Empty;
    private DateTime riskRulesWritten;
    private string? riskRulesNotice;

    /// <summary>The file the risk rules are read from. Hosts and tests may redirect it.</summary>
    public string RiskRulesPath { get; set; } = RiskRules.UserPath;

    /// <summary>Risky commands currently shaded in the script editor.</summary>
    public IReadOnlyList<RiskMarker> RiskMarkers => riskMarkers;

    private void ConfigureRisks() =>
        ScriptEditor.TextArea.TextView.BackgroundRenderers.Add(new RiskRenderer(() => riskMarkers));

    /// <summary>Reads the rule file when it is new or changed; the first use creates it with the default rules.</summary>
    private void LoadRiskRules()
    {
        try
        {
            if (!File.Exists(RiskRulesPath))
            {
                if (!hostingOptions.EnablePersistence && RiskRulesPath == RiskRules.UserPath)
                {
                    // Hosts without persistence get the defaults without writing a file in the user's profile.
                    riskRules = RiskRules.Default;
                    return;
                }
                RiskRules.EnsureFile(RiskRulesPath);
            }
            var written = File.GetLastWriteTimeUtc(RiskRulesPath);
            if (written == riskRulesWritten && riskRulesNotice is null) return;
            riskRules = RiskRules.Parse(File.ReadAllText(RiskRulesPath));
            riskRulesWritten = written;
            riskRulesNotice = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Keep the last rules that worked, and say why the file was not used.
            riskRulesNotice = string.Format(UiText.Get("RiskRulesInvalid"), exception.Message);
        }
    }

    private void RefreshRiskMarkers()
    {
        var document = ScriptEditorControl.Document;
        if (!settings.MarkRiskyCommands || windowClosed)
        {
            riskMarkers = [];
            riskRulesNotice = null;
        }
        else
        {
            LoadRiskRules();
            riskMarkers = RiskRules.Find(document.Text, riskRules).Select(finding =>
            {
                var start = document.CreateAnchor(Math.Min(finding.Start, document.TextLength));
                start.MovementType = AnchorMovementType.AfterInsertion;
                var end = document.CreateAnchor(Math.Min(finding.Start + finding.Length, document.TextLength));
                end.MovementType = AnchorMovementType.BeforeInsertion;
                return new RiskMarker(start, end, finding.Level, finding.Command, DescribeRisk(finding));
            }).ToArray();
        }
        ScriptEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private static string DescribeRisk(RiskFinding finding) => string.Format(UiText.Get(finding.Level == RiskLevel.High ? "RiskHigh" : "RiskMedium"),
        finding.Reason, finding.Pattern) + (finding.WhatIf ? " " + UiText.Get("RiskWhatIf") : "");

    private IEnumerable<string> RiskMessagesAt(int offset) => riskMarkers
        .Where(marker => marker.IsVisible && marker.Start.Document == ScriptEditorControl.Document && marker.Start.Offset <= offset && offset < marker.End.Offset)
        .Select(marker => marker.Message);

    /// <summary>The summary for the diagnostics line, such as "2 high-risk commands", or null without risky commands.</summary>
    private IEnumerable<string> RiskSummary()
    {
        if (riskRulesNotice is not null) yield return riskRulesNotice;
        var visible = riskMarkers.Where(marker => marker.IsVisible && marker.Start.Document == ScriptEditorControl.Document).ToArray();
        var high = visible.Count(marker => marker.Level == RiskLevel.High);
        var medium = visible.Length - high;
        if (high > 0) yield return string.Format(UiText.Get("RiskCountHigh"), high);
        if (medium > 0) yield return string.Format(UiText.Get("RiskCountMedium"), medium);
    }

    /// <summary>Asks before running text with high-risk commands. Returns false when the user cancels.</summary>
    private async Task<bool> ConfirmRiskyRunAsync(string text)
    {
        if (!settings.ConfirmRiskyRuns) return true;
        LoadRiskRules();
        var high = RiskRules.Find(text, riskRules).Where(finding => finding.Level == RiskLevel.High).ToArray();
        if (high.Length == 0) return true;
        var names = string.Join(", ", high.Select(finding => finding.Command).Distinct(StringComparer.OrdinalIgnoreCase).Take(5));
        var run = UiText.Get("RunAnyway");
        return await Dialogs.ChooseAsync(HostWindow, UiText.Get("RiskyRunTitle"),
            string.Format(UiText.Get("RiskyRunPrompt"), high.Length, names), run, UiText.Get("Cancel")) == run;
    }

    private async Task EditRiskRulesAsync()
    {
        await RiskRules.EnsureFileAsync(RiskRulesPath);
        await OpenFileAsync(RiskRulesPath);
    }
}
