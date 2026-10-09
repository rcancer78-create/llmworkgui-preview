# LLMWorkGUI publish helper.
#
# Produces the self-contained win-x64 distribution consumed by Install-LLMWorkGUI.ps1:
#   scripts\Publish-LLMWorkGUI.ps1 -Version 1.0.0
#
# The helper never touches the registry or installed external CLI tools; it only writes to the
# requested output directory and emits version.json so the installer can record the package version.

[CmdletBinding()]
param(
    [string]$OutputDirectory,

    [string]$Configuration = 'Release',

    [string]$RuntimeIdentifier = 'win-x64',

    [string]$Version = '1.0.0',

    [switch]$FrameworkDependent,

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'LLMWorkGUI.AcceptancePaths.ps1')
$projectPath = Join-Path $repositoryRoot 'src\LLMWorkGUI.App\LLMWorkGUI.App.csproj'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot "artifacts\publish\$RuntimeIdentifier"
}

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "PUBLISH_FAILED: project '$projectPath' was not found."
}

$OutputDirectory = Assert-LlmNoReparsePath -Path $OutputDirectory
$publishIdentity = $OutputDirectory.TrimEnd('\', '/').ToUpperInvariant()
$publishHasher = [System.Security.Cryptography.SHA256]::Create()
try { $publishDigest = [BitConverter]::ToString($publishHasher.ComputeHash(
    [System.Text.Encoding]::UTF8.GetBytes($publishIdentity))).Replace('-', '') }
finally { $publishHasher.Dispose() }
$publishLock = [System.Threading.Mutex]::new($false, "Global\LLMWorkGUI.Publish.$publishDigest")
$publishLockHeld = $false
try {
    try { $publishLockHeld = $publishLock.WaitOne(0) }
    catch [System.Threading.AbandonedMutexException] { $publishLockHeld = $true }
    if (-not $publishLockHeld) { throw 'PUBLISH_BUSY: another publisher owns this output directory.' }
    # Hold ownership from validation through activation; concurrent writers must not turn a
    # directory rename into a nested payload move. This is serialization, not a crash journal.
if (Test-Path -LiteralPath $OutputDirectory) {
    if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
        throw 'PUBLISH_FAILED: output must be a directory.'
    }
    if (@(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0) {
        $oldMarker = Assert-LlmNoReparsePath -Path (Join-Path $OutputDirectory 'version.json')
        try { $oldVersion = Get-Content -LiteralPath $oldMarker -Raw | ConvertFrom-Json }
        catch { throw 'PUBLISH_FAILED: existing output is not a recognized product distribution.' }
        if ($oldVersion.product -ne 'LLMWorkGUI') {
            throw 'PUBLISH_FAILED: existing output belongs to another product.'
        }
    }
}
$publishStaging = "$OutputDirectory.publish-$([Guid]::NewGuid().ToString('N'))"
$previousOutput = $null

$selfContained = 'true'
if ($FrameworkDependent) {
    $selfContained = 'false'
}

$arguments = @(
    'publish',
    $projectPath,
    '-c', $Configuration,
    '-r', $RuntimeIdentifier,
    '--self-contained', $selfContained,
    '-o', $publishStaging,
    "-p:Version=$Version",
    "-p:InformationalVersion=$Version",
    '-p:ContinuousIntegrationBuild=true'
)

if ($SkipBuild) {
    $arguments += '--no-build'
}

Write-Host "Publishing LLM Work GUI $Version ($RuntimeIdentifier, self-contained=$selfContained)..."

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "PUBLISH_FAILED: 'dotnet publish' exited with code $LASTEXITCODE."
}

$commit = $null
try {
    $commit = (& git -C $repositoryRoot rev-parse HEAD 2>$null).Trim()
}
catch {
    $commit = $null
}

$versionDocument = [ordered]@{
    product            = 'LLMWorkGUI'
    version            = $Version
    configuration      = $Configuration
    runtimeIdentifier  = $RuntimeIdentifier
    selfContained      = ($selfContained -eq 'true')
    publishedAtUtc     = (Get-Date).ToUniversalTime().ToString('o')
    commit             = $commit
}

$versionFile = Join-Path $publishStaging 'version.json'
$versionDocument | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $versionFile -Encoding UTF8
Import-Module (Join-Path $PSScriptRoot 'LLMWorkGUI.Packaging.psm1') -Force
Write-LlmPublishedHashes -Directory $publishStaging

if (Test-Path -LiteralPath $OutputDirectory) {
    $previousOutput = "$OutputDirectory.previous-$([Guid]::NewGuid().ToString('N'))"
    Move-Item -LiteralPath $OutputDirectory -Destination $previousOutput
}
try { [System.IO.Directory]::Move($publishStaging, $OutputDirectory) }
catch {
    if ($null -ne $previousOutput -and -not (Test-Path -LiteralPath $OutputDirectory)) {
        Move-Item -LiteralPath $previousOutput -Destination $OutputDirectory
    }
    throw
}
$versionFile = Join-Path $OutputDirectory 'version.json'
if ($null -ne $previousOutput) { Write-Host "Previous distribution preserved: $previousOutput" }

Write-Host "Distribution ready: $OutputDirectory"
Write-Host "Version manifest:   $versionFile"
}
finally {
    if ($publishLockHeld) { $publishLock.ReleaseMutex() }
    $publishLock.Dispose()
}
