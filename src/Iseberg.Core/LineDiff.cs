namespace Iseberg.Core;

/// <summary>Whether a diff line is in both texts, only in the old text, or only in the new text.</summary>
public enum DiffKind { Same, Removed, Added }

/// <summary>One line of a unified diff with its line numbers in the old and the new text (1-based).</summary>
public sealed record DiffLine(DiffKind Kind, int? OldLine, int? NewLine, string Text);

/// <summary>A line-based diff using Myers' O(ND) algorithm, with a coarse fallback for very different texts.</summary>
public static class LineDiff
{
    /// <summary>Beyond this many edits the diff stops searching for the shortest script and replaces the middle block.</summary>
    public const int MaxEdits = 2000;

    /// <summary>Splits text at any line ending; a final line ending does not start another line.</summary>
    public static string[] SplitLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return [];
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    public static IReadOnlyList<DiffLine> Compare(string oldText, string newText) => Compare(SplitLines(oldText), SplitLines(newText));

    public static IReadOnlyList<DiffLine> Compare(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        ArgumentNullException.ThrowIfNull(oldLines);
        ArgumentNullException.ThrowIfNull(newLines);
        // Common head and tail lines need no search; most edits touch a small part of a script.
        var prefix = 0;
        while (prefix < oldLines.Count && prefix < newLines.Count && oldLines[prefix] == newLines[prefix]) prefix++;
        var suffix = 0;
        while (suffix < oldLines.Count - prefix && suffix < newLines.Count - prefix &&
               oldLines[oldLines.Count - 1 - suffix] == newLines[newLines.Count - 1 - suffix]) suffix++;

        var result = new List<DiffLine>(Math.Max(oldLines.Count, newLines.Count) + 8);
        for (var i = 0; i < prefix; i++) result.Add(new(DiffKind.Same, i + 1, i + 1, oldLines[i]));
        var a = Slice(oldLines, prefix, oldLines.Count - prefix - suffix);
        var b = Slice(newLines, prefix, newLines.Count - prefix - suffix);
        foreach (var (kind, x, y) in Middle(a, b))
            result.Add(kind switch
            {
                DiffKind.Same => new(kind, prefix + x + 1, prefix + y + 1, a[x]),
                DiffKind.Removed => new(kind, prefix + x + 1, null, a[x]),
                _ => new(kind, null, prefix + y + 1, b[y])
            });
        for (var i = suffix; i > 0; i--)
            result.Add(new(DiffKind.Same, oldLines.Count - i + 1, newLines.Count - i + 1, oldLines[^i]));
        return result;
    }

    /// <summary>The number of added and removed lines in a diff.</summary>
    public static (int Added, int Removed) Count(IEnumerable<DiffLine> diff)
    {
        int added = 0, removed = 0;
        foreach (var line in diff)
        {
            if (line.Kind == DiffKind.Added) added++;
            else if (line.Kind == DiffKind.Removed) removed++;
        }
        return (added, removed);
    }

    private static string[] Slice(IReadOnlyList<string> lines, int start, int count)
    {
        var slice = new string[Math.Max(0, count)];
        for (var i = 0; i < slice.Length; i++) slice[i] = lines[start + i];
        return slice;
    }

    private static IEnumerable<(DiffKind Kind, int X, int Y)> Middle(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0 || m == 0 || Search(a, b) is not { } trace)
        {
            // Nothing in common, or too many edits to search: the old block is replaced by the new block.
            for (var x = 0; x < n; x++) yield return (DiffKind.Removed, x, 0);
            for (var y = 0; y < m; y++) yield return (DiffKind.Added, 0, y);
            yield break;
        }
        var edits = new List<(DiffKind, int, int)>(n + m);
        int cx = n, cy = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var v = trace[d];
            var k = cx - cy;
            var previousK = k == -d || k != d && V(v, d, k - 1) < V(v, d, k + 1) ? k + 1 : k - 1;
            var previousX = V(v, d, previousK);
            var previousY = previousX - previousK;
            while (cx > previousX && cy > previousY)
            {
                cx--; cy--;
                edits.Add((DiffKind.Same, cx, cy));
            }
            if (d > 0)
                edits.Add(cx == previousX ? (DiffKind.Added, cx, cy - 1) : (DiffKind.Removed, cx - 1, cy));
            cx = previousX;
            cy = previousY;
        }
        edits.Reverse();
        foreach (var edit in edits) yield return edit;
    }

    // trace[d] holds the furthest x per diagonal k in -(d+1)..(d+1) before round d.
    private static int V(int[] snapshot, int d, int k) => snapshot[k + d + 1];

    private static List<int[]>? Search(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length, max = n + m;
        var offset = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        for (var d = 0; d <= Math.Min(max, MaxEdits); d++)
        {
            var snapshot = new int[2 * d + 3];
            Array.Copy(v, offset - d - 1, snapshot, 0, snapshot.Length);
            trace.Add(snapshot);
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || k != d && v[offset + k - 1] < v[offset + k + 1] ? v[offset + k + 1] : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[offset + k] = x;
                if (x >= n && y >= m) return trace;
            }
        }
        return null;
    }
}
