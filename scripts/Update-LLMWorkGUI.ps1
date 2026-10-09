# LLMWorkGUI updater.
#
# Atomically replaces the installed application files while preserving the user data directory
# (SQLite database, settings, sessions) and creating a rollback snapshot of the previous version.
#
# The updater never modifies the registry, external CLI installations, or any machine-wide state.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDirectory,

    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\LLMWorkGUI'),

    [string]$DataDirectory,

    [string]$RollbackDirectory,

    [string]$ShortcutRoot,

    [string]$DesktopShortcutRoot,

    [switch]$NoShortcuts,

    [switch]$NoDesktopShortcut,

    [switch]$Json
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LLMWorkGUI.Packaging.psm1') -Force

function Write-LlmJsonLine {
    param([Parameter(Mandatory = $true)][hashtable]$Value)

    Write-Output ($Value | ConvertTo-Json -Compress -Depth 8)
}

function Get-LlmErrorCode {
    param([Parameter(Mandatory = $true)][string]$Message)

    $separator = $Message.IndexOf(':')
    if ($separator -gt 0) {
        return $Message.Substring(0, $separator)
    }

    return 'UPDATE_FAILED'
}

try {
    $result = Invoke-LlmUpdate `
        -SourceDirectory $SourceDirectory `
        -InstallDirectory $InstallDirectory `
        -DataDirectory $DataDirectory `
        -RollbackDirectory $RollbackDirectory `
        -CreateShortcuts (-not $NoShortcuts) `
        -CreateDesktopShortcut (-not $NoDesktopShortcut) `
        -ShortcutRoot $ShortcutRoot `
        -DesktopShortcutRoot $DesktopShortcutRoot

    if ($Json) {
        Write-LlmJsonLine -Value $result
    }
    else {
        Write-Host "LLM Work GUI updated from $($result.previousVersion) to $($result.version)."
        Write-Host "Rollback snapshot: $($result.rollbackDirectory)."
    }

    exit 0
}
catch {
    $message = $_.Exception.Message
    if ($Json) {
        Write-LlmJsonLine -Value @{
            success    = $false
            operation  = 'update'
            product    = 'LLMWorkGUI'
            error      = $message
            errorCode  = (Get-LlmErrorCode -Message $message)
            timestampUtc = (Get-Date).ToUniversalTime().ToString('o')
        }
    }
    else {
        [Console]::Error.WriteLine($message)
    }

    exit 1
}
