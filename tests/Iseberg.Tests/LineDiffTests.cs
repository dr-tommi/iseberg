using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

public sealed class LineDiffTests
{
    private static string Render(IEnumerable<DiffLine> diff) => string.Join("|", diff.Select(line =>
        (line.Kind switch { DiffKind.Added => "+", DiffKind.Removed => "-", _ => " " }) + line.Text));

    // Applying the diff must give back both texts, with consistent line numbers.
    private static void AssertConsistent(string[] oldLines, string[] newLines, IReadOnlyList<DiffLine> diff)
    {
        Assert.Equal(oldLines, diff.Where(line => line.Kind != DiffKind.Added).Select(line => line.Text));
        Assert.Equal(newLines, diff.Where(line => line.Kind != DiffKind.Removed).Select(line => line.Text));
        Assert.Equal(Enumerable.Range(1, oldLines.Length).Cast<int?>(), diff.Where(line => line.OldLine is not null).Select(line => line.OldLine));
        Assert.Equal(Enumerable.Range(1, newLines.Length).Cast<int?>(), diff.Where(line => line.NewLine is not null).Select(line => line.NewLine));
    }

    [Fact]
    public void IdenticalTextsHaveNoChanges()
    {
        var diff = LineDiff.Compare("a\r\nb\r\n", "a\nb");
        Assert.All(diff, line => Assert.Equal(DiffKind.Same, line.Kind));
        Assert.Equal((0, 0), LineDiff.Count(diff));
    }

    [Fact]
    public void ChangedLinesAreRemovedThenAdded()
    {
        var diff = LineDiff.Compare("Get-Item\nSet-Item -WhatIf\nGet-Date", "Get-Item\nSet-Item\nGet-Date\nGet-Host");
        Assert.Equal(" Get-Item|-Set-Item -WhatIf|+Set-Item| Get-Date|+Get-Host", Render(diff));
        Assert.Equal((2, 1), LineDiff.Count(diff));
        Assert.Equal(new DiffLine(DiffKind.Removed, 2, null, "Set-Item -WhatIf"), diff[1]);
        Assert.Equal(new DiffLine(DiffKind.Added, null, 4, "Get-Host"), diff[^1]);
    }

    [Fact]
    public void EmptyTextsAddOrRemoveEverything()
    {
        Assert.Equal("+a|+b", Render(LineDiff.Compare("", "a\nb")));
        Assert.Equal("-a|-b", Render(LineDiff.Compare("a\nb\n", "")));
        Assert.Empty(LineDiff.Compare("", ""));
    }

    [Fact]
    public void FindsAShortestEditScript()
    {
        // The classic Myers example: ABCABBA -> CBABAC needs five edits.
        var a = "ABCABBA".Select(c => c.ToString()).ToArray();
        var b = "CBABAC".Select(c => c.ToString()).ToArray();
        var diff = LineDiff.Compare(a, b);
        var (added, removed) = LineDiff.Count(diff);
        Assert.Equal(5, added + removed);
        AssertConsistent(a, b, diff);
    }

    [Fact]
    public void RandomEditsStayConsistent()
    {
        var random = new Random(4711);
        for (var round = 0; round < 200; round++)
        {
            var a = Enumerable.Range(0, random.Next(0, 40)).Select(_ => "line" + random.Next(0, 6)).ToArray();
            var b = a.Where(_ => random.Next(4) > 0).SelectMany(line => random.Next(5) == 0 ? new[] { line, "new" + random.Next(3) } : [line]).ToArray();
            AssertConsistent(a, b, LineDiff.Compare(a, b));
        }
    }

    [Fact]
    public void VeryDifferentTextsFallBackToReplacingTheBlock()
    {
        var a = Enumerable.Range(0, LineDiff.MaxEdits + 10).Select(i => "old" + i).ToArray();
        var b = Enumerable.Range(0, LineDiff.MaxEdits + 10).Select(i => i % 2 == 0 ? "old" + i : "new" + i).ToArray();
        var diff = LineDiff.Compare(["head", .. a, "tail"], ["head", .. b, "tail"]);
        AssertConsistent(["head", .. a, "tail"], ["head", .. b, "tail"], diff);
        Assert.Equal(DiffKind.Same, diff[0].Kind);
        Assert.Equal(DiffKind.Same, diff[^1].Kind);
    }
}
