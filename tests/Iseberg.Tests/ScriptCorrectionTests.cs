using System.Management.Automation;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

// Loads PSScriptAnalyzer, which is CPU-heavy; run outside the parallel pool like ScriptAnalyzerTests.
[Collection(PowerShellPolicyCollection.Name)]
public sealed class ScriptCorrectionTests
{
    private static bool ModuleInstalled()
    {
        using var shell = PowerShell.Create();
        return shell.AddCommand("Get-Module").AddParameter("ListAvailable").AddParameter("Name", "PSScriptAnalyzer").Invoke().Count > 0;
    }

    [Fact]
    public void CorrectionsApplyFromTheEndAndSkipOverlaps()
    {
        const string text = "gci | % { $_ }";
        var corrections = new ScriptCorrection[]
        {
            new(0, 3, "Get-ChildItem", ""),
            new(6, 1, "ForEach-Object", ""),
            new(5, 3, "overlapping", ""),
            new(40, 1, "outside", "")
        };
        Assert.Equal("Get-ChildItem | ForEach-Object { $_ }", ScriptAnalyzerService.ApplyCorrections(text, corrections));
        Assert.Equal(text, ScriptAnalyzerService.ApplyCorrections(text, []));
    }

    [Fact]
    public async Task AliasFindingsCarryTheirReplacementAndEmptySuggestionsAreDropped()
    {
        if (!ModuleInstalled()) return;
        using var analyzer = new ScriptAnalyzerService();
        const string text = "function F {\r\n    gci | % { Write-Host $_ }\r\n}";
        var result = await analyzer.AnalyzeAsync(text, new(false, true), TestContext.Current.CancellationToken);
        var alias = result.Issues.Single(issue => issue.Rule == "PSAvoidUsingCmdletAliases" && text.Substring(issue.Start, 3) == "gci");
        var fix = Assert.Single(alias.Fixes);
        Assert.Equal("gci", text.Substring(fix.Start, fix.Length));
        Assert.Equal("Get-ChildItem", fix.Text);
        Assert.Contains(result.Issues, issue => issue.Rule == "PSAvoidUsingCmdletAliases" && issue.Fixes.Single().Text == "ForEach-Object");
        Assert.Empty(result.Issues.Single(issue => issue.Rule == "PSAvoidUsingWriteHost").Fixes);
    }

    [Fact]
    public async Task FormattingKeepsWindowsLineEndingsAndCanBeLimitedToARange()
    {
        if (!ModuleInstalled()) return;
        using var analyzer = new ScriptAnalyzerService();
        const string text = "function F{\r\nwrite-host 'a'\r\n}\r\nfunction G{\r\nwrite-host 'b'\r\n}";
        var formatted = await analyzer.FormatAsync(text, null, TestContext.Current.CancellationToken);
        Assert.Equal("function F {\r\n    Write-Host 'a'\r\n}\r\nfunction G {\r\n    Write-Host 'b'\r\n}", formatted);

        var firstOnly = await analyzer.FormatAsync(text, [1, 1, 3, 2], TestContext.Current.CancellationToken);
        Assert.StartsWith("function F {\r\n    Write-Host 'a'\r\n}", firstOnly);
        Assert.EndsWith("function G{\r\nwrite-host 'b'\r\n}", firstOnly);
    }

    [Fact]
    public async Task CleanUpExpandsAliasesAndFormats()
    {
        if (!ModuleInstalled()) return;
        using var analyzer = new ScriptAnalyzerService();
        var cleaned = await analyzer.CleanUpAsync("gci -path C:\\|where {$_.Length -gt 1}", TestContext.Current.CancellationToken);
        Assert.Equal("Get-ChildItem -Path C:\\ | Where-Object { $_.Length -gt 1 }", cleaned);
    }
}
