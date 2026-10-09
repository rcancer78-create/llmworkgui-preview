<#
.SYNOPSIS
    Writes or verifies the SHA-256 inventory of the 36 delivered UI screenshot fixtures.
.EXAMPLE
    pwsh -File scripts/Update-ScreenshotManifest.ps1
.EXAMPLE
    pwsh -File scripts/Update-ScreenshotManifest.ps1 -Verify
#>
[CmdletBinding()]
param([switch]$Verify)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$fixtureDirectory = Join-Path $repoRoot 'tests\LLMWorkGUI.Ui.Tests\Screenshots'
$manifestPath = Join-Path $repoRoot 'docs\acceptance\SCREENSHOT_MANIFEST.json'
$files = @(Get-ChildItem -LiteralPath $fixtureDirectory -File -Filter '*.png' | Sort-Object -Property Name)
if ($files.Count -ne 36) {
    throw "Expected 36 screenshot fixtures; found $($files.Count)."
}

$entries = @($files | ForEach-Object {
    [ordered]@{
        name = $_.Name
        bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
})

if ($Verify) {
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'Screenshot manifest is missing.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.fileCount -ne $files.Count -or @($manifest.files).Count -ne $files.Count) {
        throw 'Screenshot manifest file count differs from the fixture directory.'
    }
    foreach ($index in 0..($files.Count - 1)) {
        $actual = $entries[$index]
        $expected = $manifest.files[$index]
        if ($expected.name -cne $actual.name -or $expected.bytes -ne $actual.bytes -or $expected.sha256 -cne $actual.sha256) {
            throw "Screenshot manifest mismatch: $($actual.name)."
        }
    }
    Write-Host "PASS: $($files.Count)/$($files.Count) screenshot fixture hashes match." -ForegroundColor Green
    return
}

$manifest = [ordered]@{
    schema = 'llmworkgui/screenshot-manifest/1'
    description = 'SHA-256 inventory of delivered UI screenshot fixtures. These files are not an approved pixel-regression baseline.'
    generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    directory = 'tests/LLMWorkGUI.Ui.Tests/Screenshots'
    fileCount = $files.Count
    files = $entries
}
$json = $manifest | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($manifestPath, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
Write-Host "Wrote $($files.Count) screenshot hashes to $manifestPath." -ForegroundColor Green
