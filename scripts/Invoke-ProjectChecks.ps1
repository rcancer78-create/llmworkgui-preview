[CmdletBinding()]
param(
    [string]$TempRoot = 'D:\work\LLMWorkGUI-temp\runtime',
    [string]$NuGetRoot = 'D:\work\LLMWorkGUI-temp\nuget',
    [string]$ResultsDirectory,
    [string]$Filter,
    [string[]]$Suites = @('Domain', 'Application', 'Backends.Contract', 'Gateway', 'Integration', 'Ui'),
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$tempPath = [IO.Path]::GetFullPath($TempRoot)
if ([IO.Path]::GetPathRoot($tempPath) -ine 'D:\') { throw 'Project temporary files must be on D:.' }
$nugetPath = [IO.Path]::GetFullPath($NuGetRoot)
if ([IO.Path]::GetPathRoot($nugetPath) -ine 'D:\') { throw 'Project NuGet caches must be on D:.' }
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
$runTemp = Join-Path $tempPath $runId
if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $root ('artifacts/checks/' + $runId) }
$results = [IO.Path]::GetFullPath($ResultsDirectory)
if ([IO.Path]::GetPathRoot($results) -ine 'D:\') { throw 'Project check artifacts must be on D:.' }
$projects = @{
    Domain = 'LLMWorkGUI.Domain.Tests'
    Application = 'LLMWorkGUI.Application.Tests'
    'Backends.Contract' = 'LLMWorkGUI.Backends.ContractTests'
    Gateway = 'LLMGateway.Tests'
    Integration = 'LLMWorkGUI.IntegrationTests'
    Ui = 'LLMWorkGUI.Ui.Tests'
}
foreach ($suite in $Suites) { if (-not $projects.ContainsKey($suite)) { throw "Unknown suite: $suite" } }
New-Item -ItemType Directory -Force -Path $runTemp, $results | Out-Null
$previous = @{TEMP=$env:TEMP; TMP=$env:TMP; LLMWORKGUI_SCREENSHOT_DIR=$env:LLMWORKGUI_SCREENSHOT_DIR; NUGET_PACKAGES=$env:NUGET_PACKAGES; NUGET_HTTP_CACHE_PATH=$env:NUGET_HTTP_CACHE_PATH}
$codes = [Collections.Generic.List[object]]::new()
try {
    $env:TEMP = $runTemp
    $env:TMP = $runTemp
    $env:NUGET_PACKAGES = Join-Path $nugetPath 'packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $nugetPath 'http-cache'
    $env:LLMWORKGUI_SCREENSHOT_DIR = Join-Path $results 'screenshots'
    if (-not $SkipBuild) {
        & dotnet build (Join-Path $root 'LLMWorkGUI.sln') -c Release --no-incremental *> (Join-Path $results 'release-build.log')
        if ($LASTEXITCODE -ne 0) { throw 'Clean Release build failed; see release-build.log.' }
    }
    foreach ($suite in $Suites) {
        $name = $projects[$suite]
        $trxPath = Join-Path $results ($suite.ToLowerInvariant()+'-final.trx')
        if (Test-Path -LiteralPath $trxPath) { throw "Refusing to reuse an existing test result: $trxPath" }
        $arguments = @('test', (Join-Path $root "tests/$name/$name.csproj"), '-c', 'Release', '--no-build', '--no-restore', '--logger', ('trx;LogFileName='+$suite.ToLowerInvariant()+'-final.trx'), '--results-directory', $results)
        if ($Filter) { $arguments += @('--filter', $Filter) }
        & dotnet @arguments *> (Join-Path $results ($suite.ToLowerInvariant()+'-final.log'))
        $code = $LASTEXITCODE
        $codes.Add([pscustomobject]@{suite=$suite;exitCode=$code})
        $codes | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $results 'suite-exit-codes.json') -Encoding utf8
        if ($code -ne 0) { throw "Suite failed: $suite; see its log and TRX." }
        if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) { throw "Suite produced no TRX: $suite; no test success is proven." }
        [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
        $counters = $trx.TestRun.ResultSummary.Counters
        if ($null -eq $counters -or [int]$counters.total -le 0 -or [int]$counters.passed -ne [int]$counters.total -or $trx.TestRun.ResultSummary.outcome -ne 'Completed') {
            throw "Suite did not pass every test: $suite; see its TRX."
        }
    }
    Write-Output "Checks passed. Results: $results"
}
finally {
    foreach ($name in $previous.Keys) {
        $value = if ($null -eq $previous[$name]) { [NullString]::Value } else { $previous[$name] }
        [Environment]::SetEnvironmentVariable($name, $value, 'Process')
    }
}
