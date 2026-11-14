using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;

namespace Iseberg.Core;

public enum ScriptIssueSeverity { Information, Warning, Error }

/// <summary>A finding located by zero-based UTF-16 offset in the analyzed text.</summary>
public sealed record ScriptIssue(string Rule, string Message, ScriptIssueSeverity Severity, int Start, int Length);

/// <summary>Which optional checks run. Both are opt-in so PowerShell 7 authors are not flagged by default.</summary>
public sealed record ScriptAnalyzerOptions(bool WindowsPowerShellCompatibility, bool DefaultRules)
{
    public bool Enabled => WindowsPowerShellCompatibility || DefaultRules;
}

/// <summary>
/// Issues found, and whether the PSScriptAnalyzer module could be used. Built-in compatibility checks are
/// reported even when the module is unavailable; <see cref="ModuleError"/> then says why.
/// </summary>
public sealed record ScriptAnalyzerResult(IReadOnlyList<ScriptIssue> Issues, bool ModuleAvailable, string? ModuleError);

/// <summary>
/// Runs PSScriptAnalyzer in a private runspace, so analysis neither blocks nor changes the user's sessions.
/// The module is not bundled: it is imported from the machine's module path when first needed.
/// </summary>
public sealed class ScriptAnalyzerService : IDisposable
{
    /// <summary>The Windows PowerShell 5.1 command profile shipped with PSScriptAnalyzer (Windows Server 2019).</summary>
    public const string WindowsPowerShellProfile = "win-8_x64_10.0.17763.0_5.1.17763.316_x64_4.0.30319.42000_framework";
    public const string BuiltInRule = "IsebergUseCompatibleSyntax";

    private readonly SemaphoreSlim gate = new(1, 1);
    private Runspace? runspace;
    private bool moduleChecked;
    private string? moduleError;
    private bool disposed;

    /// <summary>Analyzes text. Calls are serialized; a canceled call stops the running pipeline.</summary>
    public async Task<ScriptAnalyzerResult> AnalyzeAsync(string text, ScriptAnalyzerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!options.Enabled || string.IsNullOrWhiteSpace(text)) return new([], true, null);
        var issues = new List<ScriptIssue>();
        if (options.WindowsPowerShellCompatibility) issues.AddRange(FindUnsupportedSyntax(text));
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var moduleIssues = await Task.Run(() => InvokeAnalyzer(text, options, cancellationToken), cancellationToken);
            if (moduleIssues is not null) issues.AddRange(WithoutScriptDefinedCommands(text, moduleIssues));
            return new(issues.OrderBy(issue => issue.Start).ThenByDescending(issue => issue.Severity).ToArray(),
                moduleError is null, moduleError);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// PowerShell 7 syntax that PSScriptAnalyzer's PSUseCompatibleSyntax rule does not report: null-conditional
    /// member and index access, the background operator, and the clean block.
    /// </summary>
    public static IReadOnlyList<ScriptIssue> FindUnsupportedSyntax(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var ast = Parser.ParseInput(text, out _, out _);
        var issues = new List<ScriptIssue>();
        void Add(IScriptExtent extent, string syntax, bool includeTrailingAmpersand = false)
        {
            var end = extent.EndOffset;
            if (includeTrailingAmpersand)
            {
                // The pipeline extent stops before its background operator.
                var operatorOffset = end;
                while (operatorOffset < text.Length && text[operatorOffset] is ' ' or '\t') operatorOffset++;
                if (operatorOffset < text.Length && text[operatorOffset] == '&') end = operatorOffset + 1;
            }
            issues.Add(new(BuiltInRule, $"The {syntax} is not available in Windows PowerShell 5.1.",
                ScriptIssueSeverity.Error, extent.StartOffset, Math.Max(1, end - extent.StartOffset)));
        }
        foreach (var node in ast.FindAll(_ => true, searchNestedScriptBlocks: true))
        {
            switch (node)
            {
                case MemberExpressionAst { NullConditional: true } member:
                    Add(member.Extent, "null-conditional member access syntax '?.'");
                    break;
                case IndexExpressionAst { NullConditional: true } index:
                    Add(index.Extent, "null-conditional index syntax '?[]'");
                    break;
                case PipelineAst { Background: true } pipeline:
                    Add(pipeline.Extent, "background operator '&'", includeTrailingAmpersand: true);
                    break;
                case ScriptBlockAst { CleanBlock: { } clean }:
                    Add(clean.Extent, "'clean' block");
                    break;
            }
        }
        return issues;
    }

    /// <summary>
    /// PSUseCompatibleCommands reports calls to functions the script defines itself when another platform profile
    /// knows a command of that name (for example Write-Log). Those calls are not compatibility problems.
    /// </summary>
    public static IEnumerable<ScriptIssue> WithoutScriptDefinedCommands(string text, IEnumerable<ScriptIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(issues);
        var functions = Parser.ParseInput(text, out _, out _)
            .FindAll(node => node is FunctionDefinitionAst, searchNestedScriptBlocks: true)
            .Cast<FunctionDefinitionAst>().Select(function => function.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The finding covers the whole command call, so compare the command name it starts with.
        string CommandName(ScriptIssue issue)
        {
            var end = issue.Start;
            while (end < text.Length && end < issue.Start + issue.Length && !char.IsWhiteSpace(text[end]) && text[end] is not (';' or '|' or ')' or '}'))
                end++;
            return text[issue.Start..end];
        }
        return functions.Count == 0 ? issues : issues.Where(issue => issue.Rule != "PSUseCompatibleCommands" ||
            issue.Start >= text.Length || !functions.Contains(CommandName(issue)));
    }

    /// <summary>The PSScriptAnalyzer settings for the chosen checks, or null when only its default rules run.</summary>
    public static Hashtable? CreateSettings(ScriptAnalyzerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.WindowsPowerShellCompatibility) return null;
        var settings = new Hashtable
        {
            ["Rules"] = new Hashtable
            {
                ["PSUseCompatibleSyntax"] = new Hashtable { ["Enable"] = true, ["TargetVersions"] = new[] { "5.1" } },
                ["PSUseCompatibleCommands"] = new Hashtable { ["Enable"] = true, ["TargetProfiles"] = new[] { WindowsPowerShellProfile } }
            }
        };
        if (!options.DefaultRules) settings["IncludeRules"] = new[] { "PSUseCompatibleSyntax", "PSUseCompatibleCommands" };
        return settings;
    }

    /// <summary>Converts a one-based line and column, as PowerShell reports them, to a zero-based offset.</summary>
    public static int OffsetOf(string text, int line, int column)
    {
        ArgumentNullException.ThrowIfNull(text);
        var offset = 0;
        for (var current = 1; current < line && offset < text.Length; current++)
        {
            var end = text.IndexOfAny(['\r', '\n'], offset);
            if (end < 0) return text.Length;
            offset = end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
        }
        return Math.Clamp(offset + Math.Max(0, column - 1), 0, text.Length);
    }

    private List<ScriptIssue>? InvokeAnalyzer(string text, ScriptAnalyzerOptions options, CancellationToken cancellationToken)
    {
        if (!EnsureModule()) return null;
        using var shell = PowerShell.Create();
        shell.Runspace = runspace;
        shell.AddCommand("Invoke-ScriptAnalyzer").AddParameter("ScriptDefinition", text);
        if (CreateSettings(options) is { } settings) shell.AddParameter("Settings", settings);
        using var registration = cancellationToken.Register(() => shell.Stop());
        var issues = new List<ScriptIssue>();
        try
        {
            foreach (var record in shell.Invoke())
            {
                var severity = Convert.ToString(record.Properties["Severity"]?.Value);
                // The editor already reports parser errors itself.
                if (severity == "ParseError" || record.Properties["Extent"]?.Value is not IScriptExtent extent) continue;
                var start = OffsetOf(text, extent.StartLineNumber, extent.StartColumnNumber);
                var end = OffsetOf(text, extent.EndLineNumber, extent.EndColumnNumber);
                issues.Add(new(Convert.ToString(record.Properties["RuleName"]?.Value) ?? "",
                    Convert.ToString(record.Properties["Message"]?.Value) ?? "",
                    severity switch
                    {
                        "Error" => ScriptIssueSeverity.Error,
                        "Information" => ScriptIssueSeverity.Information,
                        _ => ScriptIssueSeverity.Warning
                    }, start, Math.Max(1, end - start)));
            }
        }
        catch (Exception exception) when (exception is RuntimeException or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            moduleError = exception.Message;
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return issues;
    }

    private bool EnsureModule()
    {
        if (moduleChecked) return moduleError is null;
        moduleChecked = true;
        try
        {
            runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
            runspace.Open();
            using var shell = PowerShell.Create();
            shell.Runspace = runspace;
            var module = shell.AddCommand("Get-Module").AddParameter("ListAvailable").AddParameter("Name", "PSScriptAnalyzer")
                .Invoke<PSModuleInfo>().OrderByDescending(candidate => candidate.Version).FirstOrDefault();
            if (module is null)
            {
                moduleError = "The PSScriptAnalyzer module is not installed.";
                return false;
            }
            // Import the binary module directly. Its script module wrapper only adds argument completers, and
            // loading that script would require loosening the execution policy for the whole process.
            const string assembly = "Microsoft.Windows.PowerShell.ScriptAnalyzer.dll";
            var path = Path.Combine(module.ModuleBase, "PSv" + runspace.Version.Major, assembly);
            if (!File.Exists(path)) path = Path.Combine(module.ModuleBase, assembly);
            shell.Commands.Clear();
            shell.AddCommand("Import-Module").AddParameter("Name", path).AddParameter("ErrorAction", "Stop");
            shell.Invoke();
        }
        catch (Exception exception) when (exception is RuntimeException or InvalidOperationException or IOException)
        {
            moduleError = exception.Message;
        }
        return moduleError is null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // A running analysis holds the gate; wait briefly so its pipeline is not torn down underneath it.
        if (!gate.Wait(TimeSpan.FromSeconds(2))) return;
        try { runspace?.Dispose(); }
        finally { gate.Release(); }
    }
}
