<#
.SYNOPSIS
    Runs the reproducible UiInspector visual suite (TASK-070) and verifies its artifacts.

.DESCRIPTION
    Executes only the LLMWorkGUI.Ui.Tests.UiInspectorVisualTests runner against the Release build,
    then verifies that:
      * the inspection manifest exists and reports 114 combinations, 0 clipped and 0 leaks;
      * all 114 expected PNG inspection screenshots exist under docs/work/screenshots;
      * the test compares all 114 rendered images pixel by pixel to the candidate baseline;
      * the 36 reference fixtures under tests/LLMWorkGUI.Ui.Tests/Screenshots are byte-for-byte
        unchanged by the run;
      * all 36 delivered fixture hashes match docs/acceptance/SCREENSHOT_MANIFEST.json.

    The script never sets LLMWORKGUI_UPDATE_REFERENCE_SCREENSHOTS, so the reference fixture folder
    is only ever read.

.EXAMPLE
    pwsh -File scripts/Run-UiInspector.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot

$testProject = Join-Path $repoRoot 'tests\LLMWorkGUI.Ui.Tests\LLMWorkGUI.Ui.Tests.csproj'
$screenshotsDirectory = Join-Path $repoRoot 'docs\work\screenshots'
$manifestPath = Join-Path $screenshotsDirectory 'inspection-manifest.json'
$referenceDirectory = Join-Path $repoRoot 'tests\LLMWorkGUI.Ui.Tests\Screenshots'
$resultsDirectory = Join-Path $repoRoot 'TestResults\UiInspector'
$trxName = "ui-inspector-$([Guid]::NewGuid().ToString('N')).trx"
$trxPath = Join-Path $resultsDirectory $trxName

$expectedScreenStems = @(
    'mainwindow_workspace',
    'mainwindow_projects',
    'mainwindow_providersaccounts',
    'mainwindow_models',
    'mainwindow_quotas',
    'mainwindow_sessions',
    'mainwindow_runs',
    'mainwindow_workflows',
    'mainwindow_healthcenter',
    'mainwindow_settingsdiagnostics',
    'mainwindow_workflowadaptationdialog',
    'onboarding_step1_welcome',
    'onboarding_step2_clidetection',
    'onboarding_step3_catalogdiscovery',
    'onboarding_step4_safemode',
    'commandpalette_empty',
    'commandpalette_match',
    'commandpalette_nomatch',
    'unifiedshell_threepane'
)

$themes = @('dark', 'light')
$dpis = @('100dpi', '150dpi', '200dpi')

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$FilePath)

    $stream = [System.IO.File]::OpenRead($FilePath)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hash = $sha256.ComputeHash($stream)
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    return ([System.BitConverter]::ToString($hash)).Replace('-', '')
}

function Get-DirectoryFingerprint {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return @()
    }

    return @(
        Get-ChildItem -LiteralPath $Path -File -Recurse |
            Sort-Object -Property FullName |
            ForEach-Object {
                [pscustomobject]@{
                    Path = $_.FullName
                    Hash = Get-FileSha256 -FilePath $_.FullName
                }
            }
    )
}

Write-Host '=== UiInspector reproducible run (TASK-070) ===' -ForegroundColor Cyan

if ($env:LLMWORKGUI_UPDATE_REFERENCE_SCREENSHOTS) {
    # Make absolutely sure the reference fixtures cannot be redirected during this run.
    Remove-Item Env:\LLMWORKGUI_UPDATE_REFERENCE_SCREENSHOTS -ErrorAction SilentlyContinue
}

if (-not (Test-Path -LiteralPath $referenceDirectory -PathType Container)) {
    throw "Screenshot fixture directory is missing: $referenceDirectory"
}

$referenceBefore = Get-DirectoryFingerprint -Path $referenceDirectory
New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null

Push-Location -LiteralPath $repoRoot
try {
    & dotnet test $testProject -c Release --filter 'FullyQualifiedName~UiInspectorVisualTests' `
        --logger "trx;LogFileName=$trxName" --results-directory $resultsDirectory
    $testExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

if ($testExitCode -ne 0) {
    Write-Host "FAIL: dotnet test exited with code $testExitCode." -ForegroundColor Red
    exit $testExitCode
}

if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
    throw "UiInspector test result is missing: $trxPath"
}

[xml]$trx = Get-Content -LiteralPath $trxPath -Raw
$inspectorResults = @($trx.SelectNodes("//*[local-name()='UnitTestResult']") | Where-Object {
    $_.testName -like '*UiInspector_AllNineteenScreensAcrossThemesAndDpi_ReportsCleanEvidence*'
})
if ($inspectorResults.Count -ne 1 -or $inspectorResults[0].outcome -ne 'Passed') {
    throw "UiInspector visual test did not report exactly one passing TRX result. Found $($inspectorResults.Count)."
}

if (-not (Test-Path -LiteralPath $manifestPath)) {
    Write-Host "FAIL: the inspection manifest was not written to $manifestPath." -ForegroundColor Red
    exit 1
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

if ($manifest.totalCombinations -ne 114) {
    Write-Host "FAIL: expected 114 combinations, found $($manifest.totalCombinations)." -ForegroundColor Red
    exit 1
}

if ($manifest.clippedCount -ne 0) {
    Write-Host "FAIL: expected 0 clipped elements, found $($manifest.clippedCount)." -ForegroundColor Red
    exit 1
}

if ($manifest.leaksCount -ne 0) {
    Write-Host "FAIL: expected 0 English leaks, found $($manifest.leaksCount)." -ForegroundColor Red
    exit 1
}

if ($manifest.changedPixels -ne 0) {
    throw "Expected 0 changed pixels against the visual baseline, found $($manifest.changedPixels)."
}

$missing = New-Object System.Collections.Generic.List[string]

foreach ($stem in $expectedScreenStems) {
    foreach ($theme in $themes) {
        foreach ($dpi in $dpis) {
            $fileName = '{0}_{1}_{2}.png' -f $stem, $theme, $dpi
            $filePath = Join-Path $screenshotsDirectory $fileName

            if (-not (Test-Path -LiteralPath $filePath)) {
                $missing.Add($filePath)
            }
        }
    }
}

if ($missing.Count -gt 0) {
    Write-Host "FAIL: $($missing.Count) inspection screenshot(s) are missing:" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

$referenceAfter = Get-DirectoryFingerprint -Path $referenceDirectory
$referenceDrift = Compare-Object -ReferenceObject $referenceBefore -DifferenceObject $referenceAfter -Property Path, Hash

if ($referenceDrift) {
    Write-Host 'FAIL: tests/LLMWorkGUI.Ui.Tests/Screenshots was modified by the run:' -ForegroundColor Red
    $referenceDrift | ForEach-Object { Write-Host "  - $($_.Path)" -ForegroundColor Red }
    exit 1
}

& (Join-Path $scriptRoot 'Update-ScreenshotManifest.ps1') -Verify

Write-Host 'PASS: 114/114 combinations, clipped = 0, leaks = 0.' -ForegroundColor Green
Write-Host 'PASS: 114/114 decoded-pixel baseline comparisons, changed pixels = 0.' -ForegroundColor Green
Write-Host "PASS: 114 inspection screenshots verified under $screenshotsDirectory." -ForegroundColor Green
Write-Host 'PASS: reference fixtures in tests/LLMWorkGUI.Ui.Tests/Screenshots are unchanged.' -ForegroundColor Green

exit 0
