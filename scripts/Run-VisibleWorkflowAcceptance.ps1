<#
.SYNOPSIS
    Runs the repeatable visible-desktop acceptance of a pinned built-in workflow run.

.DESCRIPTION
    Builds and runs LLMWorkGUI.VisibleWorkflowHarness, the visible acceptance harness for TASK
    "Phase 10: repeatable visible WPF acceptance of a pinned workflow".

    The harness composes the production host over a temporary application-data root, shows the shipped
    MainWindow with the unified workspace shell (normal chrome, ShowActivated = true) and drives the shipped
    product controls. It never launches the unpackaged production App. The configured data root is isolated;
    before/after fingerprints establish net file-byte state only, not absence of reads or transient writes.
    The harness never records a reviewer verdict, a session, a route or a fake backend
    observation.

    Screenshots are rendered WPF captures of the shown window and are written into a directory outside the
    checkout. The script explicitly unsets LLMWORKGUI_UPDATE_REFERENCE_SCREENSHOTS, so the reference
    screenshot fixtures under tests/LLMWorkGUI.Ui.Tests/Screenshots can never be a target of this run.

.PARAMETER Mode
    visible (default) keeps the window on the desktop for a human until it is closed or the hold timeout
    elapses; ci runs the same walk and closes the window itself.

.PARAMETER HoldSeconds
    How long visible mode keeps the window open. Ignored in ci mode.

.PARAMETER RunRoot
    Directory under %TEMP% that holds the temporary app data, the documents, the screenshots and the report.
    A fresh timestamped directory is used when it is not given.

.EXAMPLE
    pwsh -File scripts/Run-VisibleWorkflowAcceptance.ps1 -Mode ci

.EXAMPLE
    pwsh -File scripts/Run-VisibleWorkflowAcceptance.ps1 -Mode visible -HoldSeconds 900
#>
[CmdletBinding()]
param(
    [ValidateSet('visible', 'ci')]
    [string]$Mode = 'visible',

    [int]$HoldSeconds = 900,

    [string]$RunRoot,

    [ValidateRange(1, 2147483)]
    [int]$TimeoutSeconds = 1800,

    [switch]$AllUi
)

$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptRoot 'LLMWorkGUI.AcceptancePaths.ps1')
$repoRoot = Split-Path -Parent $scriptRoot
$project = Join-Path $repoRoot 'tools\LLMWorkGUI.VisibleWorkflowHarness\LLMWorkGUI.VisibleWorkflowHarness.csproj'

if (-not $RunRoot) {
    $RunRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("llm-workflow-runs\LLMWorkGUI\phase10-visible-acceptance-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
}

$resolvedRunRoot = New-LlmEvidenceRunDirectory -Path $RunRoot -Root ([System.IO.Path]::GetTempPath())

$screenshotDirectory = Join-Path $resolvedRunRoot 'screenshots'
$screenshotDirectory = Assert-LlmEvidencePath -Path $screenshotDirectory -Root $resolvedRunRoot
[void][System.IO.Directory]::CreateDirectory($screenshotDirectory)

# Reference fixtures are never a target of an acceptance run. The harness refuses to start while this is set,
# so the run script unsets it before anything else happens.
Remove-Item Env:\LLMWORKGUI_UPDATE_REFERENCE_SCREENSHOTS -ErrorAction SilentlyContinue

$env:LLMWORKGUI_SCREENSHOT_DIR = $screenshotDirectory

# Fingerprint delivered references to detect net file-byte changes.
$fixtureDirectory = Join-Path $repoRoot 'tests\LLMWorkGUI.Ui.Tests\Screenshots'

function Get-DirectoryFingerprint {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return @()
    }

    return @(
        Get-ChildItem -LiteralPath $Path -File -Recurse |
            Sort-Object -Property FullName |
            ForEach-Object { '{0}|{1}' -f $_.FullName, (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    )
}

$fixturesBefore = @(Get-DirectoryFingerprint -Path $fixtureDirectory)

# Fingerprints detect net file-byte changes; they do not audit reads or transient writes.
$userAppData = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'LLMWorkGUI'
$userAppDataBefore = @(Get-DirectoryFingerprint -Path $userAppData)

Write-Host '=== Phase 10 visible workflow acceptance ===' -ForegroundColor Cyan
Write-Host ("Run root : {0}" -f $resolvedRunRoot)
Write-Host ("Mode     : {0}" -f $Mode)

$runFailure = $null
$isolationFailure = $null
Push-Location -LiteralPath $repoRoot
try {
    & dotnet build $project -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) {
        throw "The harness build failed with exit code $LASTEXITCODE."
    }

    $executable = Join-Path $repoRoot 'tools\LLMWorkGUI.VisibleWorkflowHarness\bin\Release\net10.0-windows\LLMWorkGUI.VisibleWorkflowHarness.exe'

    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "The harness executable was not produced at $executable."
    }

    $arguments = @('--mode', $Mode, '--run-root', $resolvedRunRoot, '--screenshot-dir', $screenshotDirectory)
    if ($AllUi) { $arguments += '--all-ui' }

    if ($Mode -eq 'visible') {
        $arguments += @('--hold-seconds', $HoldSeconds)
    }

    Write-Host ("Command  : {0} {1}" -f $executable, ($arguments -join ' '))

    # The harness is a GUI-subsystem executable, so the shell does not wait for it on its own.
    $startOptions = @{ FilePath = $executable; ArgumentList = $arguments; WorkingDirectory = $repoRoot; PassThru = $true }
    if ($Mode -eq 'ci') { $startOptions.WindowStyle = 'Hidden' }
    $process = Start-Process @startOptions
    try {
        $budget = if ($Mode -eq 'visible') { [int][Math]::Min(2147483, [Math]::Max($TimeoutSeconds, [long]$HoldSeconds + 300)) } else { $TimeoutSeconds }
        $completion = Wait-LlmAcceptanceProcess -Process $process -TimeoutSeconds $budget
        if ($completion.TimedOut) { throw 'CHILD_TIMEOUT: the harness exceeded its budget and was terminated; this run is not acceptance evidence.' }
        $harnessExitCode = $completion.ExitCode
    } finally { $process.Dispose() }
}
catch { $runFailure = $_ }
finally {
    Pop-Location
    try {
        $fixturesAfter = @(Get-DirectoryFingerprint -Path $fixtureDirectory)
        $userAppDataAfter = @(Get-DirectoryFingerprint -Path $userAppData)
        $fixtureDrift = @(Compare-Object -ReferenceObject $fixturesBefore -DifferenceObject $fixturesAfter)
        $userAppDataDrift = @(Compare-Object -ReferenceObject $userAppDataBefore -DifferenceObject $userAppDataAfter)
        [ordered]@{ scope='Net file-byte fingerprints only; no proof about reads or transient writes'; fixtureFilesBefore=$fixturesBefore.Count; fixtureFilesAfter=$fixturesAfter.Count; fixtureDriftCount=$fixtureDrift.Count; userDataFilesBefore=$userAppDataBefore.Count; userDataFilesAfter=$userAppDataAfter.Count; userDataDriftCount=$userAppDataDrift.Count; executionFailed=($null -ne $runFailure) } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $resolvedRunRoot 'isolation-audit.json') -Encoding utf8
        if ($fixtureDrift.Count -or $userAppDataDrift.Count) { throw 'ISOLATION_DRIFT: protected file bytes changed during the acceptance attempt.' }
    } catch { $isolationFailure = $_ }
}
if ($null -ne $runFailure -and $null -ne $isolationFailure) {
    throw [AggregateException]::new('Acceptance execution and isolation verification failed.', [Exception[]]@($runFailure.Exception, $isolationFailure.Exception))
}
if ($null -ne $runFailure) { throw $runFailure }
if ($null -ne $isolationFailure) { throw $isolationFailure }

$reportPath = Join-Path $resolvedRunRoot 'acceptance-report.md'
if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
    throw "The acceptance report was not written to $reportPath."
}

$report = Get-Content -LiteralPath $reportPath -Raw

Write-Host ''
Write-Host ("Report     : {0}" -f $reportPath)
Write-Host ("Screenshots: {0}" -f $screenshotDirectory)
Write-Host 'PASS: the reference screenshot fixtures are byte-identical after the run.' -ForegroundColor Green
Write-Host 'PASS: the real user application-data directory is unchanged after the run.' -ForegroundColor Green

if ($report -notmatch '- Result: \*\*(\d+) PASS\*\*, \*\*(\d+) FAIL\*\*, \*\*(\d+) NOT_TESTED\*\*') {
    throw 'ACCEPTANCE_REPORT_INVALID: the harness report has no recognizable result summary.'
}
$passedSteps = [int]$Matches[1]
$failedSteps = [int]$Matches[2]
$notTestedSteps = [int]$Matches[3]
Write-Host ("Result    : {0} PASS, {1} FAIL, {2} NOT_TESTED" -f $passedSteps, $failedSteps, $notTestedSteps) -ForegroundColor Cyan
if ($failedSteps -gt 0 -or $passedSteps -eq 0) {
    throw 'ACCEPTANCE_REPORT_FAILED: the harness report does not establish a successful bounded walk.'
}

if ($harnessExitCode -ne 0) {
    Write-Host ("FAIL: the harness exited with code {0}." -f $harnessExitCode) -ForegroundColor Red
    exit $harnessExitCode
}

Write-Host 'PASS: the visible acceptance walk completed with no failed step.' -ForegroundColor Green
exit 0
