using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iseberg.Core;

public enum RiskLevel { Medium, High }

/// <summary>
/// A command-name pattern with PowerShell wildcards. With <see cref="Methods"/>, only calls whose -Method argument is
/// one of them match, so a read-only GET request stays unmarked.
/// </summary>
public sealed record RiskRule(string Pattern, string Reason, IReadOnlyList<string>? Methods = null);

/// <summary>The user-maintained rule file: patterns marked as high and as medium risk.</summary>
public sealed record RiskRuleSet(IReadOnlyList<RiskRule> High, IReadOnlyList<RiskRule> Medium)
{
    public static RiskRuleSet Empty { get; } = new([], []);
}

/// <summary>A risky command call, located by zero-based UTF-16 offset of its command name.</summary>
public sealed record RiskFinding(int Start, int Length, RiskLevel Level, string Command, string Pattern, string Reason, bool WhatIf);

/// <summary>Finds calls to risky commands with the parser, and loads the rules from a JSON file users edit themselves.</summary>
public static class RiskRules
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Aliases = new(() =>
        InitialSessionState.CreateDefault2().Commands.OfType<SessionStateAliasEntry>()
            .GroupBy(alias => alias.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Definition, StringComparer.OrdinalIgnoreCase));

    public static string UserPath => Path.Combine(Path.GetDirectoryName(UserSettings.SettingsPath)!, "RiskRules.json");

    /// <summary>A starting point for administrators of Microsoft 365, Teams, Exchange, Active Directory and Windows.</summary>
    public static RiskRuleSet Default { get; } = new(
        High:
        [
            new("Set-Cs*", "Changes the Teams configuration of the tenant"),
            new("New-Cs*", "Creates Teams configuration in the tenant"),
            new("Grant-Cs*", "Assigns Teams policies in the tenant"),
            new("Remove-Cs*", "Removes Teams configuration from the tenant"),
            new("New-Mg*", "Creates objects through Microsoft Graph"),
            new("Update-Mg*", "Changes objects through Microsoft Graph"),
            new("Set-Mg*", "Changes objects through Microsoft Graph"),
            new("Remove-Mg*", "Deletes objects through Microsoft Graph"),
            new("Invoke-MgGraphRequest", "Writes through Microsoft Graph", ["Post", "Patch", "Put", "Delete"]),
            new("Invoke-RestMethod", "Writes through a REST API", ["Post", "Patch", "Put", "Delete"]),
            new("Invoke-WebRequest", "Writes through a web API", ["Post", "Patch", "Put", "Delete"]),
            new("Set-Mailbox*", "Changes Exchange mailboxes"),
            new("Remove-Mailbox*", "Removes Exchange mailboxes or their settings"),
            new("Add-MailboxPermission", "Grants access to an Exchange mailbox"),
            new("Remove-*", "Deletes data or configuration"),
            new("Clear-*", "Clears data or configuration"),
            new("Format-Volume", "Erases a volume"),
            new("Stop-Computer", "Shuts down a computer"),
            new("Restart-Computer", "Restarts a computer"),
            new("Set-ExecutionPolicy", "Changes the PowerShell execution policy")
        ],
        Medium:
        [
            new("Invoke-Expression", "Runs text as code that cannot be checked here"),
            new("Invoke-Command", "Runs code on other computers or sessions"),
            new("Start-Process", "Starts another program"),
            new("Stop-Service", "Stops a Windows service"),
            new("Restart-Service", "Restarts a Windows service"),
            new("Stop-Process", "Ends a process"),
            new("Move-Item", "Moves files or other items"),
            new("Rename-Item", "Renames files or other items"),
            new("Set-Content", "Overwrites file content"),
            new("Set-AD*", "Changes Active Directory objects"),
            new("Disable-AD*", "Disables Active Directory objects")
        ]);

    /// <summary>
    /// Creates the rule file with the default rules if it does not exist. An existing file is never changed. This is
    /// synchronous on purpose: the workbench calls it on the UI thread, where blocking on an asynchronous write deadlocks.
    /// </summary>
    public static void EnsureFile(string? path = null)
    {
        path ??= UserPath;
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, Serialize(Default), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path)) { }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static Task EnsureFileAsync(string? path = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => EnsureFile(path), cancellationToken);

    public static string Serialize(RiskRuleSet rules) => JsonSerializer.Serialize(rules, JsonOptions);

    /// <summary>Reads rules from JSON. Comments and trailing commas are allowed; invalid rules are reported.</summary>
    public static RiskRuleSet Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        RiskRuleSet? rules;
        try { rules = JsonSerializer.Deserialize<RiskRuleSet>(json, JsonOptions); }
        catch (JsonException exception) { throw new InvalidDataException(exception.Message, exception); }
        if (rules is null) throw new InvalidDataException("The risk rule file is empty.");
        var all = (rules.High ?? []).Concat(rules.Medium ?? []).ToArray();
        if (all.Any(rule => rule is null || string.IsNullOrWhiteSpace(rule.Pattern)))
            throw new InvalidDataException("Every risk rule needs a Pattern.");
        return new(rules.High ?? [], rules.Medium ?? []);
    }

    public static async Task<RiskRuleSet> LoadAsync(string? path = null, CancellationToken cancellationToken = default) =>
        Parse(await File.ReadAllTextAsync(path ?? UserPath, cancellationToken));

    /// <summary>
    /// Finds calls to commands matching the rules. Aliases of built-in commands are resolved, the higher level wins,
    /// and a call with -WhatIf is one level lower, because it only reports what it would do.
    /// </summary>
    public static IReadOnlyList<RiskFinding> Find(string text, RiskRuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.High.Count + rules.Medium.Count == 0 || string.IsNullOrWhiteSpace(text)) return [];
        var ast = Parser.ParseInput(text, out _, out _);
        var findings = new List<RiskFinding>();
        foreach (var command in ast.FindAll(node => node is CommandAst, searchNestedScriptBlocks: true).Cast<CommandAst>())
        {
            if (command.GetCommandName() is not { Length: > 0 } written) continue;
            var name = Aliases.Value.TryGetValue(written, out var resolved) ? resolved : written;
            var match = Match(rules.High, RiskLevel.High) ?? Match(rules.Medium, RiskLevel.Medium);
            if (match is not var (rule, level)) continue;
            var whatIf = HasWhatIf(command);
            if (whatIf)
            {
                if (level == RiskLevel.Medium) continue;
                level = RiskLevel.Medium;
            }
            var extent = command.CommandElements[0].Extent;
            findings.Add(new(extent.StartOffset, extent.EndOffset - extent.StartOffset, level, name, rule.Pattern, rule.Reason, whatIf));

            (RiskRule Rule, RiskLevel Level)? Match(IReadOnlyList<RiskRule> candidates, RiskLevel candidateLevel)
            {
                foreach (var candidate in candidates)
                    if (WildcardPattern.Get(candidate.Pattern, WildcardOptions.IgnoreCase).IsMatch(name) &&
                        (candidate.Methods is not { Count: > 0 } methods || methods.Contains(MethodOf(command) ?? "", StringComparer.OrdinalIgnoreCase)))
                        return (candidate, candidateLevel);
                return null;
            }
        }
        return findings;
    }

    private static bool HasWhatIf(CommandAst command) => command.CommandElements.OfType<CommandParameterAst>().Any(parameter =>
        parameter.ParameterName.Equals("WhatIf", StringComparison.OrdinalIgnoreCase) &&
        parameter.Argument is not VariableExpressionAst { VariablePath.UserPath: "false" });

    /// <summary>The literal value of -Method, or null when the call has none or computes it.</summary>
    private static string? MethodOf(CommandAst command)
    {
        var elements = command.CommandElements;
        for (var index = 0; index < elements.Count; index++)
        {
            if (elements[index] is not CommandParameterAst { ParameterName: var parameterName } parameter ||
                !"Method".StartsWith(parameterName, StringComparison.OrdinalIgnoreCase) || parameterName.Length < 2) continue;
            var argument = parameter.Argument ?? (index + 1 < elements.Count ? elements[index + 1] : null);
            return argument is StringConstantExpressionAst constant ? constant.Value : argument?.Extent.Text;
        }
        return null;
    }
}
