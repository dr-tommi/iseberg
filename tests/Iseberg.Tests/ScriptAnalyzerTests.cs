using System.Collections;
using System.Management.Automation;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

public sealed class ScriptAnalyzerTests
{
    private static readonly ScriptAnalyzerOptions CompatibilityOnly = new(WindowsPowerShellCompatibility: true, DefaultRules: false);

    /// <summary>The module is not bundled, so engine tests only assert when the machine provides it.</summary>
    private static bool ModuleInstalled()
    {
        using var shell = PowerShell.Create();
        return shell.AddCommand("Get-Module").AddParameter("ListAvailable").AddParameter("Name", "PSScriptAnalyzer").Invoke().Count > 0;
    }

    [Fact]
    public void BuiltInChecksFindPowerShell7SyntaxTheModuleDoesNotReport()
    {
        const string text = "$a = ${b}?.Length\n$c = ${d}?[0]\nGet-Date &\nfunction F { end { 1 } clean { 2 } }\n$e = $f.Length";
        var issues = ScriptAnalyzerService.FindUnsupportedSyntax(text);
        Assert.Equal(4, issues.Count);
        Assert.All(issues, issue =>
        {
            Assert.Equal(ScriptAnalyzerService.BuiltInRule, issue.Rule);
            Assert.Equal(ScriptIssueSeverity.Error, issue.Severity);
            Assert.Contains("Windows PowerShell 5.1", issue.Message);
        });
        Assert.Equal("${b}?.Length", text.Substring(issues[0].Start, issues[0].Length));
        Assert.Contains(issues, issue => text.Substring(issue.Start, issue.Length) == "${d}?[0]");
        Assert.Contains(issues, issue => text.Substring(issue.Start, issue.Length) == "Get-Date &");
        Assert.Contains(issues, issue => text.Substring(issue.Start, issue.Length) == "clean { 2 }");
    }

    [Fact]
    public void BuiltInChecksAcceptWindowsPowerShellSyntax()
    {
        Assert.Empty(ScriptAnalyzerService.FindUnsupportedSyntax(
            "param([string] $Name)\ntry { Get-Item -LiteralPath $Name -ErrorAction Stop } catch { Write-Error $_ }\n& $command"));
    }

    [Theory]
    [InlineData("a\nbc\ndef", 1, 1, 0)]
    [InlineData("a\nbc\ndef", 2, 2, 3)]
    [InlineData("a\r\nbc\r\ndef", 3, 1, 7)]
    [InlineData("a\rbc", 2, 1, 2)]
    [InlineData("a\nbc", 9, 9, 4)]
    public void LineAndColumnMapToOffsets(string text, int line, int column, int expected) =>
        Assert.Equal(expected, ScriptAnalyzerService.OffsetOf(text, line, column));

    [Fact]
    public void SettingsLimitTheModuleToTheChosenChecks()
    {
        Assert.Null(ScriptAnalyzerService.CreateSettings(new(false, true)));
        var compatibility = ScriptAnalyzerService.CreateSettings(CompatibilityOnly)!;
        Assert.Equal(new[] { "PSUseCompatibleSyntax", "PSUseCompatibleCommands" }, (string[])compatibility["IncludeRules"]!);
        var rules = (Hashtable)compatibility["Rules"]!;
        Assert.Equal(new[] { "5.1" }, (string[])((Hashtable)rules["PSUseCompatibleSyntax"]!)["TargetVersions"]!);
        Assert.False(ScriptAnalyzerService.CreateSettings(new(true, true))!.ContainsKey("IncludeRules"));
    }

    [Fact]
    public async Task DisabledOrEmptyAnalysisDoesNotLoadTheModule()
    {
        using var analyzer = new ScriptAnalyzerService();
        var disabled = await analyzer.AnalyzeAsync("$a ?? 1", new(false, false), TestContext.Current.CancellationToken);
        Assert.Empty(disabled.Issues);
        Assert.True(disabled.ModuleAvailable);
        Assert.Empty((await analyzer.AnalyzeAsync("  ", CompatibilityOnly, TestContext.Current.CancellationToken)).Issues);
    }

    [Fact]
    public async Task ModuleReportsWindowsPowerShellIncompatibilitiesInTheEmbeddedEngine()
    {
        using var analyzer = new ScriptAnalyzerService();
        const string text = "$a = $null ?? 'x'\r\n$b = $true ? 1 : 2\r\n1..3 | ForEach-Object -Parallel { $_ }\r\n$c = ${a}?.Length\r\nGet-ChildItem";
        var result = await analyzer.AnalyzeAsync(text, CompatibilityOnly, TestContext.Current.CancellationToken);
        // The built-in check works with or without the module.
        Assert.Contains(result.Issues, issue => issue.Rule == ScriptAnalyzerService.BuiltInRule &&
            text.Substring(issue.Start, issue.Length) == "${a}?.Length");
        if (!ModuleInstalled())
        {
            Assert.False(result.ModuleAvailable);
            Assert.False(string.IsNullOrWhiteSpace(result.ModuleError));
            return;
        }
        Assert.True(result.ModuleAvailable, result.ModuleError);
        Assert.Contains(result.Issues, issue => issue.Rule == "PSUseCompatibleSyntax" && issue.Severity == ScriptIssueSeverity.Error &&
            text.Substring(issue.Start, issue.Length) == "$null ?? 'x'");
        Assert.Contains(result.Issues, issue => issue.Rule == "PSUseCompatibleSyntax" &&
            text.Substring(issue.Start, issue.Length) == "$true ? 1 : 2");
        Assert.Contains(result.Issues, issue => issue.Rule == "PSUseCompatibleCommands" && issue.Severity == ScriptIssueSeverity.Warning &&
            text.Substring(issue.Start, issue.Length) == "-Parallel");
        Assert.Equal(result.Issues.OrderBy(issue => issue.Start).Select(issue => issue.Start), result.Issues.Select(issue => issue.Start));
        // Default rules stay off unless requested: the bare cmdlet call is not flagged.
        Assert.DoesNotContain(result.Issues, issue => issue.Rule.StartsWith("PSAvoid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DefaultRulesRunWithoutCompatibilityChecks()
    {
        if (!ModuleInstalled()) return;
        using var analyzer = new ScriptAnalyzerService();
        const string text = "gci\n$a = $null ?? 'x'";
        var result = await analyzer.AnalyzeAsync(text, new(false, true), TestContext.Current.CancellationToken);
        Assert.True(result.ModuleAvailable, result.ModuleError);
        Assert.Contains(result.Issues, issue => issue.Rule == "PSAvoidUsingCmdletAliases" && text.Substring(issue.Start, issue.Length) == "gci");
        Assert.DoesNotContain(result.Issues, issue => issue.Rule == "PSUseCompatibleSyntax");
    }
}
