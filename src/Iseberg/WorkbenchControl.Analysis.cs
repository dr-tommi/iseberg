using System.Management.Automation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Iseberg.Core;

namespace Iseberg;

/// <summary>A finding anchored in a document, so it follows edits until the next analysis replaces it.</summary>
public sealed record ScriptIssueMarker(TextAnchor Start, TextAnchor End, ScriptIssueSeverity Severity, string Rule, string Message)
{
    public bool IsVisible => !Start.IsDeleted && !End.IsDeleted && End.Offset > Start.Offset;
}

/// <summary>Underlines script issues with a wavy line: red for errors, amber for warnings, blue for information.</summary>
public sealed class ScriptIssueRenderer(Func<IEnumerable<ScriptIssueMarker>> markers) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Selection;

    public static Color ColorOf(ScriptIssueSeverity severity) => severity switch
    {
        ScriptIssueSeverity.Error => Color.Parse("#E51400"),
        ScriptIssueSeverity.Warning => Color.Parse("#C08A00"),
        _ => Color.Parse("#1C7CD5")
    };

    public void Draw(TextView textView, DrawingContext context)
    {
        if (!textView.VisualLinesValid || textView.Document is null) return;
        foreach (var marker in markers())
        {
            if (!marker.IsVisible || marker.Start.Document != textView.Document) continue;
            var pen = new Pen(new SolidColorBrush(ColorOf(marker.Severity)), 1);
            var segment = new TextSegment { StartOffset = marker.Start.Offset, EndOffset = marker.End.Offset };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                var geometry = new StreamGeometry();
                using (var sink = geometry.Open())
                {
                    var y = rect.Bottom - 1;
                    sink.BeginFigure(new Point(rect.Left, y), isFilled: false);
                    var up = true;
                    for (var x = rect.Left + 2; x < rect.Right + 2; x += 2, up = !up)
                        sink.LineTo(new Point(Math.Min(x, rect.Right), up ? y - 2 : y));
                    sink.EndFigure(isClosed: false);
                }
                context.DrawGeometry(null, pen, geometry);
            }
        }
    }
}

public sealed partial class WorkbenchControl
{
    private ScriptAnalyzerService? scriptAnalyzer;
    private CancellationTokenSource? issueCancellation;
    private IReadOnlyList<ScriptIssueMarker> syntaxMarkers = [];
    private IReadOnlyList<ScriptIssueMarker> issueMarkers = [];
    private string? analyzerNotice;

    /// <summary>Syntax errors and analyzer findings currently underlined in the script editor.</summary>
    public IEnumerable<ScriptIssueMarker> ScriptIssueMarkers => syntaxMarkers.Concat(issueMarkers);

    private void ConfigureScriptIssues()
    {
        var textView = ScriptEditor.TextArea.TextView;
        textView.BackgroundRenderers.Add(new ScriptIssueRenderer(() => ScriptIssueMarkers));
        textView.PointerMoved += (_, e) =>
        {
            var position = textView.GetPositionFloor(e.GetPosition(textView) + textView.ScrollOffset);
            var message = position is { } found && textView.Document is { } document
                ? ScriptIssueMessageAt(document.GetOffset(found.Location)) : null;
            if (Equals(ToolTip.GetTip(textView), message)) return;
            ToolTip.SetTip(textView, message);
            ToolTip.SetIsOpen(textView, message is not null);
        };
        textView.PointerExited += (_, _) => { ToolTip.SetIsOpen(textView, false); ToolTip.SetTip(textView, null); };
    }

    /// <summary>The messages of all visible issues that cover an offset, or null when there are none.</summary>
    public string? ScriptIssueMessageAt(int offset)
    {
        var messages = ScriptIssueMarkers.Where(marker => marker.IsVisible && marker.Start.Document == ScriptEditorControl.Document &&
                marker.Start.Offset <= offset && offset < marker.End.Offset)
            .Select(marker => marker.Rule.Length > 0 ? $"{marker.Message} ({marker.Rule})" : marker.Message).Distinct().ToArray();
        return messages.Length == 0 ? null : string.Join(Environment.NewLine, messages);
    }

    private ScriptIssueMarker CreateMarker(TextDocument document, int start, int length, ScriptIssueSeverity severity, string rule, string message)
    {
        start = Math.Clamp(start, 0, document.TextLength);
        var end = Math.Clamp(start + Math.Max(1, length), start, document.TextLength);
        // A zero-length error at the end of the text is shown on the last character instead.
        if (end == start && start > 0) start--;
        var startAnchor = document.CreateAnchor(start);
        startAnchor.MovementType = AnchorMovementType.AfterInsertion;
        var endAnchor = document.CreateAnchor(end);
        endAnchor.MovementType = AnchorMovementType.BeforeInsertion;
        return new(startAnchor, endAnchor, severity, rule, message);
    }

    /// <summary>Called whenever the editor publishes parser results: updates syntax underlines and restarts analysis.</summary>
    private void RefreshScriptIssues(EditorAnalysisResult result)
    {
        var document = ScriptEditorControl.Document;
        // Pending results carry no diagnostics; the anchored underlines stay until the parser has finished.
        if (result.State == EditorAnalysisState.Available)
            syntaxMarkers = result.Diagnostics.Select(diagnostic => CreateMarker(document, diagnostic.Span.Start, diagnostic.Span.Length,
                ScriptIssueSeverity.Error, "", diagnostic.Message)).ToArray();
        else if (syntaxMarkers.Any(marker => marker.Start.Document != document)) syntaxMarkers = [];
        if (issueMarkers.Any(marker => marker.Start.Document != document)) issueMarkers = [];
        RefreshDiagnosticsLine();
        ScheduleScriptIssues();
    }

    private void ScheduleScriptIssues()
    {
        issueCancellation?.Cancel();
        issueCancellation = null;
        var options = new ScriptAnalyzerOptions(settings.CheckWindowsPowerShellCompatibility, settings.UseScriptAnalyzerRules);
        if (windowClosed || !options.Enabled)
        {
            if (issueMarkers.Count == 0 && analyzerNotice is null) return;
            issueMarkers = [];
            analyzerNotice = null;
            RefreshDiagnosticsLine();
            ScriptEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
            return;
        }
        var cancellation = issueCancellation = new CancellationTokenSource();
        var document = ScriptEditorControl.Document;
        _ = AnalyzeScriptIssuesAsync(document, document.Text, options, cancellation);
    }

    private async Task AnalyzeScriptIssuesAsync(TextDocument document, string text, ScriptAnalyzerOptions options, CancellationTokenSource cancellation)
    {
        try
        {
            // Wait for a pause in typing; the next change cancels this run.
            await Task.Delay(500, cancellation.Token);
            scriptAnalyzer ??= new();
            var result = await scriptAnalyzer.AnalyzeAsync(text, options, cancellation.Token);
            if (cancellation.IsCancellationRequested || windowClosed || ScriptEditorControl.Document != document) return;
            issueMarkers = result.Issues.Select(issue => CreateMarker(document, issue.Start, issue.Length, issue.Severity, issue.Rule, issue.Message)).ToArray();
            analyzerNotice = result.ModuleAvailable ? null : string.Format(UiText.Get("ScriptAnalyzerUnavailable"), result.ModuleError);
            RefreshDiagnosticsLine();
            ScriptEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or RuntimeException)
        {
            System.Diagnostics.Trace.TraceError("Script analysis failed: {0}", exception);
        }
        finally
        {
            if (issueCancellation == cancellation) issueCancellation = null;
            cancellation.Dispose();
        }
    }

    private void RefreshDiagnosticsLine()
    {
        var document = ScriptEditorControl.Document;
        var lines = ScriptIssueMarkers.Where(marker => marker.IsVisible && marker.Start.Document == document)
            .OrderBy(marker => marker.Start.Offset).ThenByDescending(marker => marker.Severity)
            .Select(marker => string.Format(UiText.Get("DiagnosticLine"), marker.Start.Line, marker.Message)).Distinct().ToList();
        var shown = lines.Take(3).ToList();
        if (lines.Count > shown.Count) shown.Add(string.Format(UiText.Get("DiagnosticMore"), lines.Count - shown.Count));
        if (analyzerNotice is not null) shown.Add(analyzerNotice);
        Diagnostics.IsVisible = shown.Count > 0;
        Diagnostics.Text = string.Join("  |  ", shown);
    }

    private void DisposeScriptIssues()
    {
        issueCancellation?.Cancel();
        issueCancellation = null;
        scriptAnalyzer?.Dispose();
        scriptAnalyzer = null;
    }
}
