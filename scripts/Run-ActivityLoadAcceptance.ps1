<#
.SYNOPSIS
    Runs the Phase 11 normative event-load scenario (ТЗ §9.2) against the shipped Activity Center.

.DESCRIPTION
    Builds and runs LLMWorkGUI.ActivityLoadDriver, the load driver for the Phase 11 exit criteria.

    The driver composes the production host over a temporary application-data root, shows the shipped
    MainWindow with the unified workspace shell, opens the Activity Center through the shell's own command
    and drives the shipped product controls. The stream enters through the product ingestion boundary,
    IActivityCenterService.Append, with unique event ids and eight execution identities - not through
    AppendProjection, which collapses repeated updates of one execution onto a single row.

    It never launches the unpackaged production App, never opens the real %LOCALAPPDATA%\LLMWorkGUI,
    never contacts an external provider, never reads a credential and never records a reviewer verdict.

    Evidence is written into a directory under %TEMP%: a markdown report, a phase trace that is flushed at
    every boundary, a rendered WPF screenshot and a dump of the shown text tree. The screenshot directory
    is always inside the run root, so the reference fixtures under tests\LLMWorkGUI.Ui.Tests\Screenshots
    can never be a target of this run.

.PARAMETER Mode
    smoke (default) runs a short deterministic pass over the same code paths. full runs the normative
    profile: 100 000 already-saved events, 50 events/second over 30 minutes, messages up to 256 KiB. A
    smoke report marks every full-profile criterion NOT_TESTED; it is never an acceptance run.

.PARAMETER Seed
    Overrides the already-saved event count. Intended for diagnosing a suspected hang under a bounded
    child timeout. A reduced run is labelled REDUCED in its report and must not be cited as a profile
    result.

.PARAMETER StreamSeconds
    Overrides the stream duration, with the same caveat as -Seed.

.PARAMETER RunRoot
    Directory under %TEMP% that holds the temporary app data, the evidence and the report. A fresh
    timestamped directory is used when it is not given.

.PARAMETER TimeoutSeconds
    Child-process budget. A timeout attempts process-tree termination and requires confirmed root exit;
    unconfirmed termination is reported separately and retains an explicit cleanup requirement.

.EXAMPLE
    pwsh -File scripts\Run-ActivityLoadAcceptance.ps1 -Mode smoke

.EXAMPLE
    pwsh -File scripts\Run-ActivityLoadAcceptance.ps1 -Mode full -TimeoutSeconds 3600

.EXAMPLE
    pwsh -File scripts\Run-ActivityLoadAcceptance.ps1 -Mode smoke -Seed 10 -StreamSeconds 1 -TimeoutSeconds 60
#>
[CmdletBinding()]
param(
    [ValidateSet('smoke', 'full')]
    [string] $Mode = 'smoke',

    [int] $Seed = 0,

    [double] $StreamSeconds = 0,

    [string] $RunRoot,

    [ValidateRange(1, 2147483)]
    [int] $TimeoutSeconds = 600
)

$ErrorActionPreference = 'Stop'
# A normal full profile alone streams for thirty minutes. Preserve explicit diagnostic budgets.
if ($Mode -eq 'full' -and -not $PSBoundParameters.ContainsKey('TimeoutSeconds')) { $TimeoutSeconds = 3600 }
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'LLMWorkGUI.AcceptancePaths.ps1')
$project = Join-Path $repoRoot 'tools\LLMWorkGUI.ActivityLoadDriver\LLMWorkGUI.ActivityLoadDriver.csproj'

if (-not $RunRoot) {
    $stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
    $RunRoot = Join-Path ([System.IO.Path]::GetTempPath()) "llm-workflow-runs\LLMWorkGUI\phase11-activity-load-$stamp"
}

$RunRoot = New-LlmEvidenceRunDirectory -Path $RunRoot -Root ([System.IO.Path]::GetTempPath())

# The evidence of a load run is never a reference fixture of the shipped visual tests.
$env:LLMWORKGUI_UPDATE_REFERENCE_SCREENSHOTS = $null

Write-Host "Building $project"
& dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "The activity-load driver did not build." }

$exe = Join-Path (Split-Path -Parent $project) "bin\Release\net10.0-windows\LLMWorkGUI.ActivityLoadDriver.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "The driver executable was not produced at $exe." }

$driverArgs = @('--mode', $Mode, '--run-root', $RunRoot)
if ($Seed -gt 0) { $driverArgs += @('--seed', "$Seed") }
if ($StreamSeconds -gt 0) { $driverArgs += @('--stream-seconds', "$StreamSeconds") }

Write-Host "Running the Phase 11 activity-load driver: mode=$Mode timeout=${TimeoutSeconds}s"
Write-Host "Run root: $RunRoot"

$stdout = Join-Path $RunRoot 'stdout.txt'
$stderr = Join-Path $RunRoot 'stderr.txt'
$process = Start-Process -FilePath $exe -ArgumentList $driverArgs -PassThru -NoNewWindow `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr

$completion = Wait-LlmAcceptanceProcess -Process $process -TimeoutSeconds $TimeoutSeconds
$timedOut = $completion.TimedOut

$reportPath = Join-Path $RunRoot 'activity-load-report.md'
$tracePath = Join-Path $RunRoot 'driver-trace.log'

Write-Host ''
Write-Host "--- phase trace (tail) ---"
if (Test-Path -LiteralPath $tracePath) {
    Get-Content -LiteralPath $tracePath -Encoding UTF8 | Select-Object -Last 25
} else {
    Write-Warning "No phase trace was written; the driver did not reach its first boundary."
}

Write-Host ''
if ($timedOut) {
    Write-Error "The run exceeded its ${TimeoutSeconds}s budget and is NOT a result."
    exit 1
}

if (Test-Path -LiteralPath $reportPath) {
    Write-Host ''
    Write-Host "--- criteria ---"
    Get-Content -LiteralPath $reportPath -Encoding UTF8 |
        Select-String -Pattern '^\| \S+ \| .* \*\*(PASS|FAIL|NOT_TESTED)\*\*|^\*\*Summary' |
        ForEach-Object { $_.Line }
    Write-Host ''
    Write-Host "Report written to $reportPath"
    Write-Host "Screenshots written to $(Join-Path $RunRoot 'screenshots')"
    exit $process.ExitCode
}

Write-Error "The driver produced no report. See $stderr"
exit 1
