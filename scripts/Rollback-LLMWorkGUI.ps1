# LLMWorkGUI rollback.
#
# Restores the previous application version from the rollback snapshot created by the updater.
# The user data directory (SQLite database, settings, sessions) is not touched.

[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\LLMWorkGUI'),

    [string]$RollbackDirectory,

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

    return 'ROLLBACK_FAILED'
}

try {
    $result = Invoke-LlmRollback `
        -InstallDirectory $InstallDirectory `
        -RollbackDirectory $RollbackDirectory

    if ($Json) {
        Write-LlmJsonLine -Value $result
    }
    else {
        Write-Host "LLM Work GUI rolled back to $($result.version)."
    }

    exit 0
}
catch {
    $message = $_.Exception.Message
    if ($Json) {
        Write-LlmJsonLine -Value @{
            success    = $false
            operation  = 'rollback'
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
