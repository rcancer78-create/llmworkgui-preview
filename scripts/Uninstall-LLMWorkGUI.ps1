# LLMWorkGUI uninstaller.
#
# Removes the application files and shortcuts. The user data directory is preserved unless
# -RemoveUserData is explicitly requested.

[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\LLMWorkGUI'),

    [string]$DataDirectory,

    [string]$StartMenuShortcutRoot,

    [string]$DesktopShortcutRoot,

    [switch]$RemoveUserData,

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

    return 'UNINSTALL_FAILED'
}

try {
    $result = Invoke-LlmUninstall `
        -InstallDirectory $InstallDirectory `
        -DataDirectory $DataDirectory `
        -RemoveUserData:$RemoveUserData `
        -ShortcutRoots @($StartMenuShortcutRoot, $DesktopShortcutRoot)

    if ($Json) {
        Write-LlmJsonLine -Value $result
    }
    else {
        Write-Host "LLM Work GUI removed from $($result.installDirectory)."
        if ($result.dataPreserved) {
            Write-Host "User data preserved at $($result.dataDirectory)."
        }
        else {
            Write-Host "User data removed from $($result.dataDirectory)."
        }
    }

    exit 0
}
catch {
    $message = $_.Exception.Message
    if ($Json) {
        Write-LlmJsonLine -Value @{
            success    = $false
            operation  = 'uninstall'
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
