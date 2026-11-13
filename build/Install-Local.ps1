<#
.SYNOPSIS
    Baut Iseberg aus dem lokalen Quellcode und installiert es für den aktuellen Benutzer.

.DESCRIPTION
    Führt die Tests aus, veröffentlicht eine eigenständige Version (inklusive .NET und PowerShell),
    prüft die erzeugte Iseberg.exe und spiegelt sie anschließend in den Installationsordner.
    Optional wird eine Startmenü-Verknüpfung angelegt. Es sind keine Administratorrechte nötig.

    Der Installationsordner wird gespiegelt: Dateien, die dort liegen und nicht zum neuen Build
    gehören, werden entfernt. Das Skript verweigert deshalb Ordner, die nicht leer sind und keine
    Iseberg.exe enthalten.

.PARAMETER InstallPath
    Zielordner der Installation. Standard: %LOCALAPPDATA%\Programs\Iseberg

.PARAMETER Runtime
    .NET-Laufzeitkennung des Builds. Standard: win-x64

.PARAMETER SkipTests
    Überspringt den Testlauf (spart etwa drei Minuten).

.PARAMETER NoShortcut
    Legt keine Startmenü-Verknüpfung an.

.EXAMPLE
    .\build\Install-Local.ps1
    Testet, baut und installiert Iseberg nach %LOCALAPPDATA%\Programs\Iseberg.

.EXAMPLE
    .\build\Install-Local.ps1 -SkipTests -WhatIf
    Zeigt, was ohne Testlauf installiert würde, ohne etwas zu verändern.

.NOTES
    Kompatibel mit Windows PowerShell 5.1 und PowerShell 7. Benötigt das .NET 10 SDK.
    Protokoll: publish\logs\Install-Local_<COMPUTERNAME>_<USERNAME>_<yyyyMMdd-HHmmss>.log
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateNotNullOrEmpty()]
    [string] $InstallPath = (Join-Path $env:LOCALAPPDATA 'Programs\Iseberg'),

    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',

    [switch] $SkipTests,

    [switch] $NoShortcut
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Iseberg\Iseberg.csproj'
$solution = Join-Path $root 'Iseberg.slnx'
$staging = Join-Path $root "publish\local-$Runtime"
$logDirectory = Join-Path $root 'publish\logs'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logFile = Join-Path $logDirectory ("Install-Local_{0}_{1}_{2}.log" -f $env:COMPUTERNAME, $env:USERNAME, $timestamp)

function Write-Log {
    param([Parameter(Mandatory = $true)][string] $Message)
    $line = '{0}  {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message
    Write-Host $line
    Add-Content -LiteralPath $logFile -Value $line -Encoding UTF8 -WhatIf:$false
}

function Invoke-DotNet {
    # Führt dotnet aus, schreibt die Ausgabe ins Protokoll und bricht bei einem Fehlercode ab.
    param([Parameter(Mandatory = $true)][string[]] $Arguments)
    Write-Log ('dotnet ' + ($Arguments -join ' '))
    & dotnet @Arguments | ForEach-Object {
        Add-Content -LiteralPath $logFile -Value $_ -Encoding UTF8 -WhatIf:$false
        if ($_ -match 'error|Fehler!|\[FAIL\]|Bestanden!|Passed!|Failed!') { Write-Host "    $_" }
    }
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) ist mit Code $LASTEXITCODE fehlgeschlagen. Details: $logFile"
    }
}

function Test-PublishedRuntime {
    # Iseberg.exe prüft mit --check-runtime, ob die eingebettete PowerShell startet.
    param([Parameter(Mandatory = $true)][string] $Executable)
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.Arguments = '--check-runtime'
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $start
    try {
        [void] $process.Start()
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(90000)) {
            $process.Kill()
            throw 'Die Laufzeitprüfung hat nicht innerhalb von 90 Sekunden geantwortet.'
        }
        $text = ($output.Result + $errors.Result).Trim()
        if ($process.ExitCode -ne 0) { throw "Die Laufzeitprüfung ist fehlgeschlagen (Code $($process.ExitCode)): $text" }
        Write-Log $text
    }
    finally { $process.Dispose() }
}

try {
    New-Item -ItemType Directory -Path $logDirectory -Force -WhatIf:$false | Out-Null
    Write-Log "Iseberg lokal installieren: $Runtime -> $InstallPath"

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'dotnet wurde nicht gefunden. Installieren Sie das .NET 10 SDK.'
    }

    $InstallPath = [System.IO.Path]::GetFullPath($InstallPath)
    $installedExe = Join-Path $InstallPath 'Iseberg.exe'
    if ((Test-Path -LiteralPath $InstallPath) -and
        @(Get-ChildItem -LiteralPath $InstallPath -Force | Select-Object -First 1).Count -gt 0 -and
        -not (Test-Path -LiteralPath $installedExe)) {
        throw "Der Zielordner '$InstallPath' ist nicht leer und enthält keine Iseberg.exe. Er wird nicht überschrieben."
    }

    $running = @(Get-Process -Name Iseberg -ErrorAction SilentlyContinue | Where-Object {
            $_.Path -and $_.Path.StartsWith($InstallPath, [System.StringComparison]::OrdinalIgnoreCase) })
    if ($running.Count -gt 0) {
        throw "Iseberg läuft noch aus '$InstallPath' (PID $($running.Id -join ', ')). Bitte schließen und erneut starten."
    }

    if ($SkipTests) { Write-Log 'Tests übersprungen (-SkipTests).' }
    else { Invoke-DotNet -Arguments @('test', $solution, '-c', 'Release', '--nologo') }

    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -WhatIf:$false }
    # Das Veröffentlichen für eine Laufzeit ergänzt die eingecheckte Lockdatei des Editors um einen
    # Laufzeitabschnitt. Der Inhalt wird danach zurückgeschrieben, damit das Repository sauber bleibt.
    $lockFile = Join-Path $root 'src\Iseberg.Editor\packages.lock.json'
    $lockBytes = [System.IO.File]::ReadAllBytes($lockFile)
    try {
        Invoke-DotNet -Arguments @('publish', $project, '-c', 'Release', '-r', $Runtime, '--self-contained', 'true',
            '-o', $staging, '--nologo', '--verbosity', 'minimal')
    }
    finally { [System.IO.File]::WriteAllBytes($lockFile, $lockBytes) }
    Test-PublishedRuntime -Executable (Join-Path $staging 'Iseberg.exe')

    if ($PSCmdlet.ShouldProcess($InstallPath, 'Iseberg-Dateien spiegeln')) {
        New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
        # /MIR entfernt auch veraltete Dateien früherer Builds. Robocopy meldet Erfolg mit Codes unter 8.
        & robocopy $staging $InstallPath /MIR /R:2 /W:2 /NFL /NDL /NJH /NP | ForEach-Object {
            Add-Content -LiteralPath $logFile -Value $_ -Encoding UTF8 -WhatIf:$false
        }
        if ($LASTEXITCODE -ge 8) { throw "Das Kopieren nach '$InstallPath' ist fehlgeschlagen (Robocopy-Code $LASTEXITCODE)." }
        $global:LASTEXITCODE = 0
        Write-Log "Installiert: $installedExe"
    }

    if (-not $NoShortcut) {
        $shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Iseberg.lnk'
        if ($PSCmdlet.ShouldProcess($shortcutPath, 'Startmenü-Verknüpfung anlegen')) {
            $shell = New-Object -ComObject WScript.Shell
            $shortcut = $shell.CreateShortcut($shortcutPath)
            $shortcut.TargetPath = $installedExe
            $shortcut.WorkingDirectory = [Environment]::GetFolderPath('MyDocuments')
            $shortcut.Description = 'Iseberg - PowerShell 7 ISE'
            $shortcut.Save()
            Write-Log "Verknüpfung: $shortcutPath"
        }
    }

    Write-Log 'Fertig.'
}
catch {
    $message = "FEHLER: $($_.Exception.Message)"
    if (Test-Path -LiteralPath $logDirectory) { Add-Content -LiteralPath $logFile -Value $message -Encoding UTF8 -WhatIf:$false }
    Write-Error $message
    exit 1
}
