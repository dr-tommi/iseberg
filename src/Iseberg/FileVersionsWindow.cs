using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Iseberg.Core;
using Iseberg.Editor;

namespace Iseberg;

/// <summary>What the user chose to do with a file version.</summary>
public enum FileVersionChoice { Restore, OpenAsNew }

/// <summary>The chosen version's text and what to do with it.</summary>
public sealed record FileVersionResult(FileVersionChoice Choice, string Text, DateTime SavedUtc);

/// <summary>Appearance of the diff text, taken from the script editor.</summary>
public sealed record DiffAppearance(FontFamily FontFamily, double FontSize, bool Dark)
{
    public static DiffAppearance Default { get; } = new(new FontFamily("Consolas, Cascadia Code, DejaVu Sans Mono, Menlo, monospace"), 13, false);
}

/// <summary>A read-only unified diff: removed lines on red, added lines on green, with old and new line numbers.</summary>
public sealed class DiffView : Border
{
    private IReadOnlyList<DiffLine> lines = [];

    public TextEditor Editor { get; }

    /// <summary>The lines currently shown.</summary>
    public IReadOnlyList<DiffLine> Lines => lines;

    public DiffView(DiffAppearance appearance)
    {
        Editor = new TextEditor
        {
            Name = "DiffText",
            IsReadOnly = true,
            ShowLineNumbers = false,
            FontFamily = appearance.FontFamily,
            FontSize = appearance.FontSize,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            SyntaxHighlighting = DesktopTheme.HighContrast ? null : EditorHighlighting.Create(appearance.Dark),
        };
        if (!DesktopTheme.HighContrast)
        {
            Editor.Background = new SolidColorBrush(appearance.Dark ? Color.Parse("#1E1E1E") : Colors.White);
            Editor.Foreground = new SolidColorBrush(appearance.Dark ? Color.Parse("#E0E0E0") : Colors.Black);
        }
        AutomationProperties.SetName(Editor, UiText.Get("DiffText"));
        Editor.TextArea.LeftMargins.Insert(0, new DiffMargin(() => lines, appearance));
        Editor.TextArea.TextView.BackgroundRenderers.Add(new DiffRenderer(() => lines));
        BorderThickness = new Thickness(1);
        BorderBrush = Brushes.Gray;
        Child = Editor;
    }

    public void Show(IReadOnlyList<DiffLine> diff)
    {
        lines = diff;
        Editor.Document.Text = string.Join("\n", diff.Select(line => line.Text));
        Editor.ScrollToHome();
        // Start at the first change, so a long unchanged head does not hide it.
        var first = diff.Select((line, index) => (line, index)).FirstOrDefault(pair => pair.line.Kind != DiffKind.Same);
        if (first.line is not null) Editor.ScrollToLine(Math.Max(1, first.index + 1 - 3));
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        foreach (var margin in Editor.TextArea.LeftMargins) margin.InvalidateVisual();
    }

    public static Color ColorOf(DiffKind kind) => kind == DiffKind.Added ? Color.FromArgb(60, 0, 170, 0) : Color.FromArgb(60, 220, 0, 0);

    private sealed class DiffRenderer(Func<IReadOnlyList<DiffLine>> lines) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext context)
        {
            if (!textView.VisualLinesValid || DesktopTheme.HighContrast) return;
            var diff = lines();
            foreach (var visual in textView.VisualLines)
            {
                var index = visual.FirstDocumentLine.LineNumber - 1;
                if (index >= diff.Count || diff[index].Kind == DiffKind.Same) continue;
                var top = visual.VisualTop - textView.ScrollOffset.Y;
                context.FillRectangle(new SolidColorBrush(ColorOf(diff[index].Kind)),
                    new Rect(0, top, Math.Max(textView.Bounds.Width, 1), visual.Height));
            }
        }
    }

    /// <summary>Shows the old and new line numbers and a +/- sign left of each diff line.</summary>
    private sealed class DiffMargin(Func<IReadOnlyList<DiffLine>> lines, DiffAppearance appearance) : AbstractMargin
    {
        private readonly Typeface typeface = new(appearance.FontFamily);

        private FormattedText Format(string text, IBrush brush) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, appearance.FontSize, brush);

        private static string Label(DiffLine line) =>
            $"{line.OldLine?.ToString(CultureInfo.InvariantCulture) ?? "",5} {line.NewLine?.ToString(CultureInfo.InvariantCulture) ?? "",5} " +
            (line.Kind switch { DiffKind.Added => "+", DiffKind.Removed => "-", _ => " " });

        protected override Size MeasureOverride(Size availableSize) =>
            new(Format("00000 00000 + ", Brushes.Gray).WidthIncludingTrailingWhitespace, 0);

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null) oldTextView.VisualLinesChanged -= Redraw;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null) newTextView.VisualLinesChanged += Redraw;
            InvalidateVisual();
        }

        private void Redraw(object? sender, EventArgs e) => InvalidateVisual();

        public override void Render(DrawingContext context)
        {
            if (TextView is not { VisualLinesValid: true } textView) return;
            var diff = lines();
            var brush = DesktopTheme.HighContrast ? DesktopTheme.Brush("ControlTextBrush") : appearance.Dark ? Brushes.LightGray : Brushes.DimGray;
            foreach (var visual in textView.VisualLines)
            {
                var index = visual.FirstDocumentLine.LineNumber - 1;
                if (index >= diff.Count) continue;
                var top = visual.VisualTop - textView.ScrollOffset.Y;
                if (!DesktopTheme.HighContrast && diff[index].Kind != DiffKind.Same)
                    context.FillRectangle(new SolidColorBrush(ColorOf(diff[index].Kind)), new Rect(0, top, Bounds.Width, visual.Height));
                context.DrawText(Format(Label(diff[index]), brush), new Point(0, top));
            }
        }
    }
}

/// <summary>Lists the saved versions of a script and shows what changed against the editor or the previous version.</summary>
public sealed class FileVersionsWindow : Window
{
    private readonly List<VersionItem> items;
    private readonly ListBox list;
    private readonly ComboBox target;
    private readonly DiffView diff;
    private readonly TextBlock summary;
    private readonly Button restore;
    private readonly Button openAsNew;
    private readonly Button clear;
    private readonly string currentText;

    /// <summary>The diff view, for tests.</summary>
    public DiffView DiffView => diff;

    /// <summary>True after the user deleted the history of the file.</summary>
    public bool HistoryCleared { get; private set; }

    public FileVersionsWindow(FileHistory history, string filePath, string currentText, string? savedHash, DiffAppearance appearance)
    {
        // The control themes come from per-window styles, so apply them before any content is created.
        DesktopTheme.ApplyWindow(this);
        this.currentText = currentText;
        Title = string.Format(UiText.Get("FileVersionsTitle"), Path.GetFileName(filePath));
        Width = 1000;
        Height = 640;
        MinWidth = 640;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        items = [];
        var versions = history.List(filePath);
        foreach (var version in versions)
        {
            string text;
            try { text = ScriptFile.FromBytes(filePath, File.ReadAllBytes(version.StoragePath)).Text; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
            { continue; }
            items.Add(new VersionItem(version, text, version.Hash == savedHash));
        }
        foreach (var item in items) item.Changes = LineDiff.Count(LineDiff.Compare(item.Text, currentText));

        list = new ListBox { Name = "VersionList", ItemsSource = items.ToArray() };
        AutomationProperties.SetName(list, UiText.Get("FileVersionsList"));
        var empty = new TextBlock { Name = "NoVersions", Text = UiText.Get("NoFileVersions"), TextWrapping = TextWrapping.Wrap, IsVisible = items.Count == 0, Margin = new Thickness(4) };
        target = new ComboBox
        {
            Name = "CompareTarget",
            ItemsSource = new[] { UiText.Get("CompareWithEditor"), UiText.Get("CompareWithPrevious") },
            SelectedIndex = 0,
            MinWidth = 260
        };
        AutomationProperties.SetName(target, UiText.Get("CompareTargetName"));
        summary = new TextBlock { Name = "DiffSummary", VerticalAlignment = VerticalAlignment.Center };
        diff = new DiffView(appearance);
        restore = new Button { Name = "RestoreVersion", Content = UiText.Get("RestoreVersion") };
        openAsNew = new Button { Name = "OpenVersion", Content = UiText.Get("OpenVersionAsNew") };
        clear = new Button { Name = "ClearHistory", Content = UiText.Get("ClearFileHistory"), IsEnabled = items.Count > 0 };
        var close = new Button { Content = UiText.Get("CloseButton"), IsCancel = true };

        list.SelectionChanged += (_, _) => Refresh();
        target.SelectionChanged += (_, _) => Refresh();
        restore.Click += (_, _) => { if (Selected is { } item) Close(new FileVersionResult(FileVersionChoice.Restore, item.Text, item.Version.SavedUtc)); };
        openAsNew.Click += (_, _) => { if (Selected is { } item) Close(new FileVersionResult(FileVersionChoice.OpenAsNew, item.Text, item.Version.SavedUtc)); };
        close.Click += (_, _) => Close(null);
        clear.Click += async (_, _) =>
        {
            if (await Dialogs.ChooseAsync(this, UiText.Get("ClearFileHistory"), string.Format(UiText.Get("ClearFileHistoryPrompt"), items.Count),
                    UiText.Get("DeleteButton"), UiText.Get("Cancel")) != UiText.Get("DeleteButton"))
                return;
            history.Clear(filePath);
            HistoryCleared = true;
            Close(null);
        };

        var left = new DockPanel { Margin = new Thickness(0, 0, 4, 0) };
        var listLabel = new TextBlock { Text = UiText.Get("FileVersionsList"), Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(listLabel, Dock.Top);
        left.Children.Add(listLabel);
        var listArea = new Panel();
        listArea.Children.Add(list);
        listArea.Children.Add(empty);
        left.Children.Add(listArea);

        var right = new DockPanel { Margin = new Thickness(4, 0, 0, 0) };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(target);
        header.Children.Add(summary);
        DockPanel.SetDock(header, Dock.Top);
        right.Children.Add(header);
        right.Children.Add(diff);

        var body = new Grid { ColumnDefinitions = new("300,Auto,*") };
        body.Children.Add(left);
        var splitter = new GridSplitter { Width = 4, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 1); body.Children.Add(splitter);
        Grid.SetColumn(right, 2); body.Children.Add(right);

        var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(clear, Dock.Left);
        buttons.Children.Add(clear);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(restore);
        actions.Children.Add(openAsNew);
        actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Right);
        buttons.Children.Add(actions);

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(body);
        Content = root;
        Dialogs.RegisterNames(this);
        list.SelectedIndex = items.Count > 0 ? 0 : -1;
        Refresh();
        Opened += (_, _) => (items.Count > 0 ? (Control)list : close).Focus();
    }

    private VersionItem? Selected => list.SelectedItem as VersionItem;

    private void Refresh()
    {
        restore.IsEnabled = openAsNew.IsEnabled = Selected is not null;
        if (Selected is not { } item)
        {
            diff.Show([]);
            summary.Text = "";
            return;
        }
        IReadOnlyList<DiffLine> lines;
        if (target.SelectedIndex == 1)
        {
            // The previous version is the next older one in the list; the oldest version is compared with an empty file.
            var index = items.IndexOf(item);
            var previous = index + 1 < items.Count ? items[index + 1].Text : "";
            lines = LineDiff.Compare(previous, item.Text);
        }
        else
            lines = LineDiff.Compare(item.Text, currentText);
        diff.Show(lines);
        var (added, removed) = LineDiff.Count(lines);
        summary.Text = added == 0 && removed == 0 ? UiText.Get("DiffIdentical") : string.Format(UiText.Get("DiffCounts"), added, removed);
    }

    private sealed class VersionItem(FileVersion version, string text, bool saved)
    {
        public FileVersion Version { get; } = version;
        public string Text { get; } = text;
        public (int Added, int Removed) Changes { get; set; }

        public override string ToString()
        {
            var line = string.Format(CultureInfo.CurrentCulture, UiText.Get("FileVersionLine"),
                Version.SavedUtc.ToLocalTime(), FormatSize(Version.Size), Changes.Added, Changes.Removed);
            return saved ? line + " " + UiText.Get("FileVersionSaved") : line;
        }
    }

    internal static string FormatSize(long bytes) => bytes < 1024
        ? string.Format(CultureInfo.CurrentCulture, "{0} B", bytes)
        : string.Format(CultureInfo.CurrentCulture, "{0:0.#} KB", bytes / 1024.0);
}

/// <summary>Shows a single diff, such as unsaved edits against the file on disk.</summary>
public sealed class DiffWindow : Window
{
    public DiffView DiffView { get; }

    public DiffWindow(string title, IReadOnlyList<DiffLine> lines, DiffAppearance appearance)
    {
        DesktopTheme.ApplyWindow(this);
        Title = title;
        Width = 900;
        Height = 600;
        MinWidth = 480;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        DiffView = new DiffView(appearance);
        var (added, removed) = LineDiff.Count(lines);
        var summary = new TextBlock
        {
            Name = "DiffSummary",
            Text = added == 0 && removed == 0 ? UiText.Get("DiffIdentical") : string.Format(UiText.Get("DiffCounts"), added, removed),
            Margin = new Thickness(0, 0, 0, 6)
        };
        var close = new Button { Content = UiText.Get("CloseButton"), IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        close.Click += (_, _) => Close();
        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(summary, Dock.Top);
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(summary);
        root.Children.Add(close);
        root.Children.Add(DiffView);
        Content = root;
        Dialogs.RegisterNames(this);
        DiffView.Show(lines);
        Opened += (_, _) => close.Focus();
    }
}
