# LLMWorkGUI per-user installer.
#
# Installs a self-contained distribution into the current user profile without elevation:
#   - files:      %LOCALAPPDATA%\Programs\LLMWorkGUI
#   - app data:   %LOCALAPPDATA%\LLMWorkGUI (created, never overwritten)
#   - shortcuts:  Start Menu + Desktop
#
# The installer never modifies the registry, external CLI installations, or any machine-wide state.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDirectory,

    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\LLMWorkGUI'),

    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'LLMWorkGUI'),

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

    return 'INSTALL_FAILED'
}

try {
    $result = Invoke-LlmInstall `
        -SourceDirectory $SourceDirectory `
        -InstallDirectory $InstallDirectory `
        -DataDirectory $DataDirectory `
        -ShortcutRoot $ShortcutRoot `
        -DesktopShortcutRoot $DesktopShortcutRoot `
        -CreateShortcuts (-not $NoShortcuts) `
        -CreateDesktopShortcut (-not $NoDesktopShortcut)

    if ($Json) {
        Write-LlmJsonLine -Value $result
    }
    else {
        Write-Host "LLM Work GUI $($result.version) installed to $($result.installDirectory)."
        Write-Host "User data directory preserved at $($result.dataDirectory)."
    }

    exit 0
}
catch {
    $message = $_.Exception.Message
    if ($Json) {
        Write-LlmJsonLine -Value @{
            success    = $false
            operation  = 'install'
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
