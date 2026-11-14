using System.Globalization;
using System.Text;

namespace Iseberg.Core;

/// <summary>A script template: a .ps1 file whose name is shown and whose text starts a new script.</summary>
public sealed record ScriptTemplate(string Name, string Path, string Text);

/// <summary>A template with its placeholders filled in, and where the caret should start.</summary>
public sealed record ExpandedScriptTemplate(string Text, int CaretOffset);

/// <summary>
/// Loads script templates from a folder and fills in their placeholders. Templates are plain .ps1 files, so users
/// add, edit or remove them in a file manager; the folder is seeded with starter templates only when it is created.
/// </summary>
public static class ScriptTemplateCatalog
{
    public const string CursorPlaceholder = "{{Cursor}}";

    /// <summary>Scripts from templates are saved as UTF-8 with BOM so Windows PowerShell 5.1 reads non-ASCII text correctly.</summary>
    public static ScriptEncoding Encoding { get; } = ScriptEncoding.Choices.Single(choice => choice is { CodePage: 65001, EmitBom: true });

    public static string UserDirectory => Path.Combine(Path.GetDirectoryName(UserSettings.SettingsPath)!, "Templates");

    /// <summary>Creates the folder with the starter templates if it does not exist yet. Existing folders are left alone.</summary>
    public static async Task EnsureDirectoryAsync(string? directory = null, CancellationToken cancellationToken = default)
    {
        directory ??= UserDirectory;
        if (Directory.Exists(directory)) return;
        var staging = directory + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (name, text) in StarterTemplates)
                await File.WriteAllTextAsync(Path.Combine(staging, name + ".ps1"), text.ReplaceLineEndings("\r\n"),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
            // Publish the folder in one step so a failed seed never leaves a half-filled folder that is then kept.
            Directory.Move(staging, directory);
        }
        catch (IOException) when (Directory.Exists(directory))
        {
            // Another instance created the folder first; keep its content.
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>Loads the .ps1 files of a folder, sorted by name. A missing folder has no templates.</summary>
    public static async Task<IReadOnlyList<ScriptTemplate>> LoadAsync(string? directory = null, CancellationToken cancellationToken = default)
    {
        directory ??= UserDirectory;
        if (!Directory.Exists(directory)) return [];
        var templates = new List<ScriptTemplate>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.ps1", SearchOption.TopDirectoryOnly))
        {
            var file = await ScriptFile.OpenAsync(path);
            cancellationToken.ThrowIfCancellationRequested();
            templates.Add(new(Path.GetFileNameWithoutExtension(path), path, file.Text));
        }
        return templates.OrderBy(template => template.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>
    /// Replaces {{Date}}, {{Author}} and {{Computer}}, and removes the first {{Cursor}}, whose position becomes the
    /// caret offset. Unknown placeholders stay as written so templates can contain other double-brace text.
    /// </summary>
    public static ExpandedScriptTemplate Expand(string text, DateTime now, string author, string computer)
    {
        ArgumentNullException.ThrowIfNull(text);
        var expanded = text
            .Replace("{{Date}}", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{Author}}", author, StringComparison.Ordinal)
            .Replace("{{Computer}}", computer, StringComparison.Ordinal);
        var caret = expanded.IndexOf(CursorPlaceholder, StringComparison.Ordinal);
        if (caret < 0) return new(expanded, expanded.Length);
        expanded = expanded.Remove(caret, CursorPlaceholder.Length).Replace(CursorPlaceholder, "", StringComparison.Ordinal);
        return new(expanded, caret);
    }

    /// <summary>Expands a template for the current user and machine.</summary>
    public static ExpandedScriptTemplate Expand(ScriptTemplate template) =>
        Expand(template.Text, DateTime.Now, Environment.UserName, Environment.MachineName);

    /// <summary>Windows PowerShell 5.1-compatible starting points, written once into a new template folder.</summary>
    public static IReadOnlyList<(string Name, string Text)> StarterTemplates { get; } =
    [
        ("Advanced Function", """
            function Verb-Noun {
                <#
                .SYNOPSIS
                    {{Cursor}}

                .DESCRIPTION

                .PARAMETER Name

                .EXAMPLE
                    Verb-Noun -Name 'Value'

                .NOTES
                    Author:  {{Author}}
                    Created: {{Date}}
                #>
                [CmdletBinding(SupportsShouldProcess = $true)]
                param(
                    [Parameter(Mandatory = $true, ValueFromPipeline = $true)]
                    [ValidateNotNullOrEmpty()]
                    [string] $Name
                )

                begin {
                }

                process {
                    try {
                        if ($PSCmdlet.ShouldProcess($Name)) {
                        }
                    }
                    catch {
                        Write-Error -ErrorRecord $_
                    }
                }

                end {
                }
            }

            """),
        ("Script with Logging", """
            <#
            .SYNOPSIS
                {{Cursor}}

            .DESCRIPTION

            .EXAMPLE
                .\Script.ps1

            .NOTES
                Author:  {{Author}}
                Created: {{Date}}
                Requires Windows PowerShell 5.1 or later. Save as UTF-8 with BOM.
            #>
            [CmdletBinding()]
            param(
                [ValidateNotNullOrEmpty()]
                [string] $LogDirectory = (Join-Path -Path $PSScriptRoot -ChildPath 'Logs')
            )

            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'

            $logName = 'Script_{0}_{1}_{2}.log' -f $env:COMPUTERNAME, $env:USERNAME, (Get-Date -Format 'yyyyMMdd-HHmmss')
            $logFile = Join-Path -Path $LogDirectory -ChildPath $logName

            function Write-Log {
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)]
                    [string] $Message,

                    [ValidateSet('Info', 'Warning', 'Error')]
                    [string] $Level = 'Info'
                )
                $line = '{0}  [{1}]  {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Message
                Add-Content -LiteralPath $logFile -Value $line -Encoding UTF8
                switch ($Level) {
                    'Warning' { Write-Warning -Message $Message }
                    'Error'   { Write-Error -Message $Message -ErrorAction Continue }
                    default   { Write-Verbose -Message $Message }
                }
            }

            try {
                New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
                Write-Log -Message 'Start'

                Write-Log -Message 'Done'
            }
            catch {
                Write-Log -Message $_.Exception.Message -Level Error
                exit 1
            }

            """)
    ];
}
