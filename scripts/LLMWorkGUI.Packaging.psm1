# LLMWorkGUI per-user packaging primitives.
#
# The module implements the deployment lifecycle shared by Install/Update/Rollback/Uninstall
# scripts. It never touches the global registry, external CLI installations, or the user data
# directory beyond creating it when it is missing.
#
# All output is ASCII; use -Verbose for diagnostics.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ProductName = 'LLMWorkGUI'
$script:ShortcutName = 'LLM Work GUI.lnk'
$script:StateFileName = 'install-state.json'
$script:ExecutableCandidates = @('LLMWorkGUI.App.exe', 'LLMWorkGUI.exe')
$script:UnspecifiedVersion = '0.0.0-unspecified'

function Get-LlmDefaultInstallDirectory {
    return (Join-Path $env:LOCALAPPDATA 'Programs\LLMWorkGUI')
}

function Get-LlmDefaultDataDirectory {
    return (Join-Path $env:LOCALAPPDATA 'LLMWorkGUI')
}

function Get-LlmDefaultShortcutRoot {
    return (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs')
}

function Get-LlmDefaultDesktopShortcutRoot {
    return [Environment]::GetFolderPath('Desktop')
}

function Get-LlmRollbackDirectory {
    param([Parameter(Mandatory = $true)][string]$InstallDirectory)

    return "$InstallDirectory.rollback"
}

function ConvertTo-LlmNormalizedPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $full = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetPathRoot($full)
    if ($full.Length -gt $root.Length) {
        $full = $full.TrimEnd([char[]]@('\', '/'))
    }

    if ([string]::IsNullOrEmpty($full)) {
        $full = $root
    }

    return $full
}

function Test-LlmPathWithin {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root)

    $normalizedPath = ConvertTo-LlmNormalizedPath -Path $Path
    $normalizedRoot = ConvertTo-LlmNormalizedPath -Path $Root

    if ([string]::Equals($normalizedPath, $normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    $prefix = $normalizedRoot
    if (-not $prefix.EndsWith([string][System.IO.Path]::DirectorySeparatorChar)) {
        $prefix = $prefix + [System.IO.Path]::DirectorySeparatorChar
    }

    return $normalizedPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function Resolve-LlmReparsePointTarget {
    param([Parameter(Mandatory = $true)]$Item)

    $target = $null
    try {
        $rawTarget = $Item.Target
        if ($rawTarget -is [System.Array]) {
            $target = [string]$rawTarget[0]
        }
        else {
            $target = [string]$rawTarget
        }
    }
    catch {
        $target = $null
    }

    if ([string]::IsNullOrWhiteSpace($target)) {
        throw "ROLLBACK_PATH_INVALID: reparse point '$($Item.FullName)' has no resolvable target."
    }

    if ($target.StartsWith('\\?\UNC\', [System.StringComparison]::OrdinalIgnoreCase)) {
        $target = '\\' + $target.Substring(8)
    }
    elseif ($target.StartsWith('\\?\', [System.StringComparison]::OrdinalIgnoreCase)) {
        $target = $target.Substring(4)
    }

    if (-not [System.IO.Path]::IsPathRooted($target)) {
        $target = Join-Path (Split-Path -Parent $Item.FullName) $target
    }

    return ConvertTo-LlmNormalizedPath -Path $target
}

function Get-LlmCanonicalReparseTarget {
    param(
        [Parameter(Mandatory = $true)]$Item,
        [Parameter(Mandatory = $false)]$Visited = $null,
        [Parameter(Mandatory = $false)][string]$Origin = '')

    $target = Resolve-LlmReparsePointTarget -Item $Item
    $guard = 0

    while ($true) {
        $targetItem = Get-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
        if ($null -eq $targetItem -or (($targetItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0)) {
            return $target
        }

        if ($null -ne $Visited) {
            $targetItemPath = ConvertTo-LlmNormalizedPath -Path $targetItem.FullName
            if (-not $Visited.Add($targetItemPath)) {
                throw "ROLLBACK_PATH_INVALID: reparse point '$targetItemPath' was visited more than once while resolving '$Origin'."
            }
        }

        if ($guard -ge 40) {
            throw "ROLLBACK_PATH_INVALID: reparse-point chain for '$($Item.FullName)' is too deep."
        }

        $target = Resolve-LlmReparsePointTarget -Item $targetItem
        $guard++
    }
}

function Get-LlmCanonicalPathWalk {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $false)]$Visited = $null)

    $root = [System.IO.Path]::GetPathRoot($Path)
    if ([string]::IsNullOrEmpty($root)) {
        return ConvertTo-LlmNormalizedPath -Path $Path
    }

    $relative = $Path.Substring($root.Length)
    $segments = @($relative -split '[\\/]' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $current = $root

    foreach ($segment in $segments) {
        $combined = Join-Path -Path $current -ChildPath $segment
        $item = Get-Item -LiteralPath $combined -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            if ($null -ne $Visited) {
                $itemPath = ConvertTo-LlmNormalizedPath -Path $item.FullName
                if (-not $Visited.Add($itemPath)) {
                    throw "ROLLBACK_PATH_INVALID: reparse point '$itemPath' was visited more than once while resolving '$Path'."
                }
            }

            $current = Get-LlmCanonicalReparseTarget -Item $item -Visited $Visited -Origin $Path
        }
        else {
            $current = $combined
        }
    }

    return ConvertTo-LlmNormalizedPath -Path $current
}

function Resolve-LlmCanonicalPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolved = ConvertTo-LlmNormalizedPath -Path $Path
    $visited = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    for ($pass = 0; $pass -lt 10; $pass++) {
        $walked = Get-LlmCanonicalPathWalk -Path $resolved -Visited $visited
        if ([string]::Equals($walked, $resolved, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $resolved
        }

        $resolved = $walked
    }

    throw "ROLLBACK_PATH_INVALID: reparse-point resolution for '$Path' did not stabilize (cycle or chain too deep)."
}

function Get-LlmProtectedRoots {
    $roots = @()

    try {
        foreach ($drive in [System.IO.DriveInfo]::GetDrives()) {
            if ($null -ne $drive) {
                $roots += $drive.RootDirectory.FullName
            }
        }
    }
    catch {
    }

    $specialRoots = @(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows),
        $env:SystemRoot,
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::Desktop),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    )

    $userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    if (-not [string]::IsNullOrWhiteSpace($userProfile)) {
        $specialRoots += (Join-Path $userProfile 'Downloads')
    }

    foreach ($root in $specialRoots) {
        if (-not [string]::IsNullOrWhiteSpace($root)) {
            $roots += $root
        }
    }

    return @($roots | Select-Object -Unique)
}

function Test-LlmPathProtectedRoot {
    param([Parameter(Mandatory = $true)][string]$CanonicalPath)

    $canonical = ConvertTo-LlmNormalizedPath -Path $CanonicalPath

    foreach ($root in (Get-LlmProtectedRoots)) {
        $canonicalRoot = $null
        try {
            $canonicalRoot = Resolve-LlmCanonicalPath -Path $root
        }
        catch {
            continue
        }

        if (Test-LlmPathWithin -Path $canonicalRoot -Root $canonical) {
            return $canonicalRoot
        }
    }

    $systemRoots = @(
        $env:SystemRoot,
        [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    )

    foreach ($root in $systemRoots) {
        if ([string]::IsNullOrWhiteSpace($root)) {
            continue
        }

        $canonicalRoot = $null
        try {
            $canonicalRoot = Resolve-LlmCanonicalPath -Path $root
        }
        catch {
            continue
        }

        if (Test-LlmPathWithin -Path $canonical -Root $canonicalRoot) {
            return $canonicalRoot
        }
    }

    $canonicalLocalAppData = New-Object System.Collections.ArrayList
    foreach ($localRoot in @(
            $env:LOCALAPPDATA,
            [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData))) {
        if ([string]::IsNullOrWhiteSpace($localRoot)) {
            continue
        }

        try {
            [void]$canonicalLocalAppData.Add((Resolve-LlmCanonicalPath -Path $localRoot))
        }
        catch {
        }
    }

    $profileRoots = @(
        $env:USERPROFILE,
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    )

    foreach ($root in $profileRoots) {
        if ([string]::IsNullOrWhiteSpace($root)) {
            continue
        }

        $canonicalRoot = $null
        try {
            $canonicalRoot = Resolve-LlmCanonicalPath -Path $root
        }
        catch {
            continue
        }

        if (-not (Test-LlmPathWithin -Path $canonical -Root $canonicalRoot)) {
            continue
        }

        $insideLocalAppData = $false
        foreach ($localRoot in $canonicalLocalAppData) {
            if (Test-LlmPathWithin -Path $canonical -Root $localRoot) {
                $insideLocalAppData = $true
                break
            }
        }

        if (-not $insideLocalAppData) {
            return $canonicalRoot
        }
    }

    return $null
}

function Remove-LlmPathSafe {
    param([Parameter(Mandatory = $true)][string]$Path)

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -eq $item) {
        return
    }

    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        if ($item.PSIsContainer) {
            [System.IO.Directory]::Delete($item.FullName, $false)
        }
        else {
            [System.IO.File]::Delete($item.FullName)
        }

        return
    }

    if (-not $item.PSIsContainer) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
            $item.Attributes = $item.Attributes -band (-bnot [System.IO.FileAttributes]::ReadOnly)
        }

        [System.IO.File]::Delete($item.FullName)
        return
    }

    foreach ($child in @(Get-ChildItem -LiteralPath $item.FullName -Force -ErrorAction SilentlyContinue)) {
        Remove-LlmPathSafe -Path $child.FullName
    }

    [System.IO.Directory]::Delete($item.FullName, $false)
}

function Assert-LlmRollbackPathSafe {
    param(
        [Parameter(Mandatory = $true)][string]$RollbackDirectory,
        [Parameter(Mandatory = $true)][string[]]$ProtectedPaths)

    $canonicalRollback = Resolve-LlmCanonicalPath -Path $RollbackDirectory

    $protectedRoot = Test-LlmPathProtectedRoot -CanonicalPath $canonicalRollback
    if (-not [string]::IsNullOrWhiteSpace($protectedRoot)) {
        throw "ROLLBACK_PATH_INVALID: rollback directory '$RollbackDirectory' (resolves to '$canonicalRollback') equals or contains protected root '$protectedRoot'."
    }

    foreach ($protectedPath in $ProtectedPaths) {
        if ([string]::IsNullOrWhiteSpace($protectedPath)) {
            continue
        }

        $canonicalProtected = Resolve-LlmCanonicalPath -Path $protectedPath
        if ((Test-LlmPathWithin -Path $canonicalRollback -Root $canonicalProtected) -or
            (Test-LlmPathWithin -Path $canonicalProtected -Root $canonicalRollback)) {
            throw "ROLLBACK_PATH_INVALID: rollback directory '$RollbackDirectory' (resolves to '$canonicalRollback') overlaps protected path '$protectedPath' (resolves to '$canonicalProtected')."
        }
    }

    return $canonicalRollback
}

function Test-LlmRollbackOwnedByState {
    param(
        [Parameter(Mandatory = $true)][string]$CanonicalRollbackPath,
        [Parameter(Mandatory = $false)]$State)

    if ($null -eq $State) {
        return $false
    }

    $property = $State.PSObject.Properties['rollbackDirectory']
    if ($null -eq $property) {
        return $false
    }

    $recorded = [string]$property.Value
    if ([string]::IsNullOrWhiteSpace($recorded)) {
        return $false
    }

    try {
        $recordedCanonical = Resolve-LlmCanonicalPath -Path $recorded
    }
    catch {
        return $false
    }

    return [string]::Equals($recordedCanonical, $CanonicalRollbackPath, [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-LlmRollbackSnapshot {
    param([Parameter(Mandatory = $true)][string]$Directory)

    $stateFile = Join-Path $Directory $script:StateFileName
    if (-not (Test-Path -LiteralPath $stateFile -PathType Leaf)) {
        return $false
    }

    $snapshotState = $null
    try {
        $snapshotState = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
    }
    catch {
        return $false
    }

    if ($null -eq $snapshotState) {
        return $false
    }

    $productProperty = $snapshotState.PSObject.Properties['product']
    if ($null -eq $productProperty) {
        return $false
    }

    $product = [string]$productProperty.Value
    if ([string]::IsNullOrWhiteSpace($product)) {
        return $false
    }

    return [string]::Equals($product, $script:ProductName, [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-LlmExecutable {
    param([Parameter(Mandatory = $true)][string]$Directory)

    foreach ($candidate in $script:ExecutableCandidates) {
        $path = Join-Path $Directory $candidate
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            return $path
        }
    }

    return $null
}

function Read-LlmPackageVersion {
    param([Parameter(Mandatory = $true)][string]$Directory)

    $versionFile = Join-Path $Directory 'version.json'
    if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) {
        return $script:UnspecifiedVersion
    }

    try {
        $document = Get-Content -LiteralPath $versionFile -Raw | ConvertFrom-Json
        if ($null -ne $document -and -not [string]::IsNullOrWhiteSpace([string]$document.version)) {
            return [string]$document.version
        }
    }
    catch {
        return $script:UnspecifiedVersion
    }

    return $script:UnspecifiedVersion
}

function Assert-LlmPackage {
    param([Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $false)][string]$ExpectedManifestHash)

    if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
        throw "SOURCE_INVALID: source directory '$SourceDirectory' does not exist."
    }

    $executable = Get-LlmExecutable -Directory $SourceDirectory
    if ($null -eq $executable) {
        $expected = $script:ExecutableCandidates -join ', '
        throw "SOURCE_INVALID: no product executable ($expected) found in '$SourceDirectory'."
    }

    Assert-LlmPublishedHashes -Directory $SourceDirectory -ExpectedManifestHash $ExpectedManifestHash
    return $executable
}

function Write-LlmPublishedHashes {
    param([Parameter(Mandatory = $true)][string]$Directory)

    $hashes = Get-LlmTreeHashes -Directory $Directory
    $hashes.Remove('package-sha256.json')
    [ordered]@{ format = 1; algorithm = 'SHA256'; files = $hashes } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Directory 'package-sha256.json') -Encoding UTF8
}

function Get-LlmPublishedManifestHash {
    param([Parameter(Mandatory = $true)][string]$Directory)

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { return $null }
    $root = Get-Item -LiteralPath $Directory -Force -ErrorAction Stop
    if (($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'VALIDATION_FAILED: package trees must not contain reparse points.'
    }
    $path = Join-Path $Directory 'package-sha256.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $entry = Get-Item -LiteralPath $path -Force -ErrorAction Stop
    if ($entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'VALIDATION_FAILED: published inventory must be an ordinary file.'
    }
    return Get-LlmFileHash -Path $path
}

function Assert-LlmPublishedHashes {
    param([Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $false)][string]$ExpectedManifestHash)

    $manifest = Join-Path $Directory 'package-sha256.json'
    # Legacy RC distributions remain installable; new publishes always carry this inventory.
    $manifestHash = Get-LlmPublishedManifestHash -Directory $Directory
    if (-not [string]::IsNullOrEmpty($ExpectedManifestHash) -and $manifestHash -cne $ExpectedManifestHash) {
        throw 'VALIDATION_FAILED: the originally validated published inventory disappeared or changed during transfer.'
    }
    if ($null -eq $manifestHash) { return }
    $actual = Get-LlmTreeHashes -Directory $Directory
    $actual.Remove('package-sha256.json')
    try {
        $document = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        if ($document.format -ne 1 -or $document.algorithm -ne 'SHA256') { throw 'Unsupported inventory.' }
        $expected = @($document.files.PSObject.Properties)
        if ($expected.Count -eq 0 -or $expected.Count -ne $actual.Count) { throw 'Inventory differs.' }
        foreach ($entry in $expected) {
            if ([string]$entry.Value -notmatch '^[0-9a-fA-F]{64}$' -or
                -not $actual.ContainsKey($entry.Name) -or [string]$actual[$entry.Name] -ne [string]$entry.Value) {
                throw 'Hash differs.'
            }
        }
        if ((Get-LlmPublishedManifestHash -Directory $Directory) -cne $manifestHash) {
            throw 'Inventory changed during validation.'
        }
    }
    catch { throw 'VALIDATION_FAILED: published package SHA256 inventory is invalid or differs from its files.' }
}

function Read-LlmState {
    param([Parameter(Mandatory = $true)][string]$InstallDirectory)

    $stateFile = Join-Path $InstallDirectory $script:StateFileName
    if (-not (Test-Path -LiteralPath $stateFile -PathType Leaf)) {
        return $null
    }

    try {
        return (Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json)
    }
    catch {
        return $null
    }
}

function Write-LlmState {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][hashtable]$State)

    $State.fileHashes = Get-LlmTreeHashes -Directory $Directory
    $stateFile = Join-Path $Directory $script:StateFileName
    $json = $State | ConvertTo-Json -Depth 8
    # A sibling stays on the same volume but outside the inventoried payload.
    # Interruption before replacement therefore leaves the existing marker intact.
    $pendingState = "$Directory.install-state-$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $bytes = ([System.Text.UTF8Encoding]::new($false)).GetBytes($json + [Environment]::NewLine)
        $stream = [System.IO.FileStream]::new($pendingState, [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally { $stream.Dispose() }
        if ([System.IO.File]::Exists($stateFile)) {
            [System.IO.File]::Replace($pendingState, $stateFile, [NullString]::Value)
        }
        else { [System.IO.File]::Move($pendingState, $stateFile) }
    }
    finally {
        if ([System.IO.File]::Exists($pendingState)) { [System.IO.File]::Delete($pendingState) }
    }
}

function Copy-LlmDirectoryTree {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination)

    if (Test-Path -LiteralPath $Destination) {
        throw "COPY_FAILED: destination '$Destination' already exists."
    }

    # Inventory validation plus a fresh per-node check. These checks do not pin filesystem identities.
    [void](Get-LlmTreeHashes -Directory $Source)
    $pending = New-Object 'System.Collections.Generic.Stack[object]'
    $pending.Push([pscustomobject]@{ Source = $Source; Destination = $Destination })
    while ($pending.Count -gt 0) {
        $node = $pending.Pop()
        $entry = Get-Item -LiteralPath $node.Source -Force -ErrorAction Stop
        if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'VALIDATION_FAILED: package copying refuses reparse points.'
        }
        if ($entry.PSIsContainer) {
            New-Item -ItemType Directory -Path $node.Destination -ErrorAction Stop | Out-Null
            foreach ($child in @(Get-ChildItem -LiteralPath $entry.FullName -Force -ErrorAction Stop)) {
                $pending.Push([pscustomobject]@{ Source = $child.FullName; Destination = (Join-Path $node.Destination $child.Name) })
            }
        }
        else {
            Copy-Item -LiteralPath $entry.FullName -Destination $node.Destination -ErrorAction Stop
        }
    }
}

function Get-LlmTreeHashes {
    param([Parameter(Mandatory = $true)][string]$Directory)

    $root = [System.IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($root)
    $hashes = @{}
    while ($pending.Count -gt 0) {
        $path = $pending.Pop()
        $entry = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'VALIDATION_FAILED: package trees must not contain reparse points.'
        }
        if ($entry.PSIsContainer) {
            foreach ($child in @(Get-ChildItem -LiteralPath $path -Force -ErrorAction Stop)) {
                $pending.Push($child.FullName)
            }
        }
        else {
            $relative = $entry.FullName.Substring($root.Length + 1).Replace('\', '/')
            if ($relative -ne $script:StateFileName) {
                $hashes[$relative] = Get-LlmFileHash -Path $entry.FullName
            }
        }
    }
    return $hashes
}

function Assert-LlmTreeHashes {
    param([Parameter(Mandatory = $true)][string]$Directory, [Parameter(Mandatory = $true)]$State)

    $recorded = $State.PSObject.Properties['fileHashes']
    if ($null -eq $recorded -or $null -eq $recorded.Value) {
        throw 'VALIDATION_FAILED: package file inventory is missing.'
    }
    $expected = @($recorded.Value.PSObject.Properties)
    $actual = Get-LlmTreeHashes -Directory $Directory
    if ($expected.Count -eq 0 -or $expected.Count -ne $actual.Count) {
        throw 'VALIDATION_FAILED: package file inventory differs.'
    }
    foreach ($entry in $expected) {
        if (-not $actual.ContainsKey($entry.Name) -or [string]$actual[$entry.Name] -ne [string]$entry.Value) {
            throw 'VALIDATION_FAILED: package file hash mismatch.'
        }
    }
}

function Get-LlmFileHash {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $stream = [System.IO.File]::OpenRead($fullPath)
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $bytes = $sha.ComputeHash($stream)
            return ([System.BitConverter]::ToString($bytes) -replace '-','').ToLowerInvariant()
        }
        finally {
            $sha.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-LlmProcessExecutablePath {
    param([Parameter(Mandatory = $true)]$Process)

    # A fixed fail-closed fault seam can only refuse a mutation. Never evaluate
    # environment contents as PowerShell or accept a fabricated executable path.
    if ($env:LLMWORKGUI_TEST_PROCESS_PATH_FAILURE -eq 'unavailable') { return $null }
    if ($env:LLMWORKGUI_TEST_PROCESS_PATH_FAILURE -eq 'access-denied') { throw 'Process path unavailable.' }

    $modulePath = $null
    try {
        $modulePath = $Process.MainModule.FileName
    }
    catch {
        try {
            $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $($Process.Id)" -ErrorAction SilentlyContinue
            if ($null -ne $cim -and -not [string]::IsNullOrWhiteSpace($cim.ExecutablePath)) {
                $modulePath = $cim.ExecutablePath
            }
        }
        catch { }
    }

    return $modulePath
}

function Test-LlmProcessRunning {
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)

    $name = [System.IO.Path]::GetFileNameWithoutExtension($ExecutablePath)
    if ([string]::IsNullOrWhiteSpace($name)) {
        return $false
    }

    # The process name alone is not a safe identity: any unrelated executable that happens to share
    # the product name (another installation, a build output, a test fixture) would otherwise make the
    # lifecycle refuse a legitimate operation with APP_RUNNING. Candidates are therefore matched by the
    # canonical path of their main module, and only a real path match counts as "running".
    $targetPath = $null
    try {
        $targetPath = Resolve-LlmCanonicalPath -Path $ExecutablePath
    }
    catch {
        $targetPath = ConvertTo-LlmNormalizedPath -Path $ExecutablePath
    }

    # Fail closed when the candidate set cannot be enumerated: an unknown process state must never
    # allow a destructive lifecycle operation.
    $processes = @()
    try {
        $processes = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
    }
    catch {
        return $true
    }

    if ($processes.Count -eq 0) {
        return $false
    }

    foreach ($process in $processes) {
        $modulePath = $null
        try {
            $modulePath = Get-LlmProcessExecutablePath -Process $process
        }
        catch {
            return $true
        }

        # An unknown executable path cannot be proven to be a known-different executable, so the
        # candidate is treated as potentially blocking (APP_RUNNING) rather than skipped.
        if ([string]::IsNullOrWhiteSpace($modulePath)) {
            return $true
        }

        $canonicalModulePath = $null
        try {
            $canonicalModulePath = Resolve-LlmCanonicalPath -Path $modulePath
        }
        catch {
            $canonicalModulePath = ConvertTo-LlmNormalizedPath -Path $modulePath
        }

        if ([string]::Equals($canonicalModulePath, $targetPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    # Every candidate resolved to a known-different path: the installed application is not running.
    return $false
}

function Assert-LlmNotRunning {
    param([Parameter(Mandatory = $true)][string]$InstallDirectory)

    $executable = Get-LlmExecutable -Directory $InstallDirectory
    if ($null -ne $executable -and (Test-LlmProcessRunning -ExecutablePath $executable)) {
        throw "APP_RUNNING: '$([System.IO.Path]::GetFileName($executable))' is running; close the application and retry."
    }
}

function New-LlmShortcut {
    param(
        [Parameter(Mandatory = $true)][string]$ShortcutPath,
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $false)][string]$Description = 'LLM Work GUI',
        [Parameter(Mandatory = $false)][hashtable]$UndoLog)

    if (Test-Path -LiteralPath $ShortcutPath) {
        $oldTarget = Get-LlmShortcutTarget -ShortcutPath $ShortcutPath
        if ([string]::IsNullOrWhiteSpace($oldTarget) -or
            -not [string]::Equals((Resolve-LlmCanonicalPath -Path $oldTarget),
                (Resolve-LlmCanonicalPath -Path $TargetPath), [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'SHORTCUT_NOT_OWNED: refusing to replace an unrelated shortcut.'
        }
    }
    if ($null -ne $UndoLog -and -not $UndoLog.ContainsKey($ShortcutPath)) {
        $UndoLog[$ShortcutPath] = if (Test-Path -LiteralPath $ShortcutPath -PathType Leaf) {
            [System.IO.File]::ReadAllBytes($ShortcutPath)
        } else { $null }
    }

    $directory = Split-Path -Parent $ShortcutPath
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $shortcut.TargetPath = $TargetPath
        $shortcut.WorkingDirectory = $WorkingDirectory
        $shortcut.Description = $Description
        $shortcut.IconLocation = "$TargetPath,0"
        $shortcut.Save()
    }
    finally {
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }

    return $ShortcutPath
}

function Restore-LlmShortcuts {
    param([Parameter(Mandatory = $true)][hashtable]$UndoLog)
    foreach ($path in $UndoLog.Keys) {
        try {
            if ($null -eq $UndoLog[$path]) {
                Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
            }
            else { [System.IO.File]::WriteAllBytes($path, [byte[]]$UndoLog[$path]) }
        }
        catch { Write-Warning "Unable to restore shortcut after failed operation: $path" }
    }
}

function Assert-LlmOwnedInstallTarget {
    param([Parameter(Mandatory = $true)][string]$InstallDirectory)
    if (-not (Test-LlmRollbackSnapshot -Directory $InstallDirectory)) {
        throw 'INSTALL_NOT_OWNED: a readable LLMWorkGUI installation marker is required.'
    }
    $canonical = Resolve-LlmCanonicalPath -Path $InstallDirectory
    $state = Read-LlmState -InstallDirectory $InstallDirectory
    $binding = $state.PSObject.Properties['installDirectory']
    $recorded = if ($null -ne $binding) { [string]$binding.Value } else { '' }
    # A copied marker is not authority to delete or replace an unrelated directory.
    # Windows PowerShell lacks Path.IsPathFullyQualified; require a drive or UNC root.
    if ([string]::IsNullOrWhiteSpace($recorded) -or $recorded -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+)') {
        throw 'INSTALL_NOT_OWNED: the installation marker must record an absolute installation directory.'
    }
    $recordedCanonical = Resolve-LlmCanonicalPath -Path $recorded
    if (-not [string]::Equals($canonical, $recordedCanonical, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'INSTALL_NOT_OWNED: the installation marker belongs to another directory.'
    }
    if (-not [string]::IsNullOrWhiteSpace((Test-LlmPathProtectedRoot -CanonicalPath $canonical))) {
        throw 'INSTALL_PATH_INVALID: a protected root cannot be an installation target.'
    }
}

function Get-LlmShortcutTarget {
    param([Parameter(Mandatory = $true)][string]$ShortcutPath)

    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        return $shortcut.TargetPath
    }
    finally {
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
}

function Remove-LlmShortcuts {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $false)][string[]]$Roots = @())

    $removed = New-Object System.Collections.ArrayList

    foreach ($root in $Roots) {
        if ([string]::IsNullOrWhiteSpace($root) -or -not (Test-Path -LiteralPath $root -PathType Container)) {
            continue
        }

        Get-ChildItem -LiteralPath $root -Filter '*.lnk' -Force -ErrorAction SilentlyContinue | ForEach-Object {
            $path = $_.FullName
            $matchesName = ($_.Name -eq $script:ShortcutName)
            $matchesTarget = $false

            try {
                $target = Get-LlmShortcutTarget -ShortcutPath $path
                if (-not [string]::IsNullOrWhiteSpace($target)) {
                    $fullTarget = [System.IO.Path]::GetFullPath($target)
                    $fullInstall = [System.IO.Path]::GetFullPath($InstallDirectory)
                    $matchesTarget = Test-LlmPathWithin -Path $fullTarget -Root $fullInstall
                }
            }
            catch {
                $matchesTarget = $false
            }

            if ($matchesName -and $matchesTarget) {
                Remove-Item -LiteralPath $path -Force -ErrorAction Stop
                if (Test-Path -LiteralPath $path) { throw "SHORTCUT_REMOVE_FAILED: '$path' still exists." }
                [void]$removed.Add($path)
            }
        }
    }

    return @($removed)
}

function Test-LlmInstallation {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $false)][string[]]$ShortcutPaths = @())

    $executable = Get-LlmExecutable -Directory $InstallDirectory
    if ($null -eq $executable) {
        throw "VALIDATION_FAILED: the installed executable was not found in '$InstallDirectory'."
    }

    $state = Read-LlmState -InstallDirectory $InstallDirectory
    if ($null -eq $state) {
        throw "VALIDATION_FAILED: '$InstallDirectory' has no $script:StateFileName."
    }

    $recordedHash = [string]$state.executableHash
    if ([string]::IsNullOrWhiteSpace($recordedHash)) {
        throw 'VALIDATION_FAILED: executable hash is missing.'
    }
    else {
        $actualHash = Get-LlmFileHash -Path $executable
        if ($recordedHash -ne $actualHash) {
            throw "VALIDATION_FAILED: executable hash mismatch after installation."
        }
    }

    Assert-LlmTreeHashes -Directory $InstallDirectory -State $state

    foreach ($shortcut in @($ShortcutPaths)) {
        if ([string]::IsNullOrWhiteSpace($shortcut)) {
            continue
        }

        if (-not (Test-Path -LiteralPath $shortcut -PathType Leaf)) {
            throw "VALIDATION_FAILED: shortcut '$shortcut' is missing."
        }

        $target = Get-LlmShortcutTarget -ShortcutPath $shortcut
        if ([string]::IsNullOrWhiteSpace($target)) {
            throw "VALIDATION_FAILED: shortcut '$shortcut' has no target."
        }

        if ([System.IO.Path]::GetFullPath($target) -ne [System.IO.Path]::GetFullPath($executable)) {
            throw "VALIDATION_FAILED: shortcut '$shortcut' does not target the installed executable."
        }
    }

    return @('executable-present', 'state-readable', 'executable-hash-verified', 'file-inventory-verified')
}

function New-LlmResult {
    param(
        [Parameter(Mandatory = $true)][string]$Operation,
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $false)][string]$DataDirectory,
        [Parameter(Mandatory = $false)][hashtable]$Extra = @{})

    $result = @{
        success         = $true
        operation       = $Operation
        product         = $script:ProductName
        version         = $Version
        installDirectory = $InstallDirectory
        dataDirectory   = $DataDirectory
        timestampUtc    = (Get-Date).ToUniversalTime().ToString('o')
    }

    foreach ($key in $Extra.Keys) {
        $result[$key] = $Extra[$key]
    }

    return $result
}

function Enter-LlmPackagingLease {
    param([Parameter(Mandatory = $true)][string]$InstallDirectory)

    $canonical = Resolve-LlmCanonicalPath -Path $InstallDirectory
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($canonical.ToUpperInvariant())
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha256.ComputeHash($bytes)).Replace('-', '') }
    finally { $sha256.Dispose() }
    $mutex = [System.Threading.Mutex]::new($false, ('Local\LLMWorkGUI.Packaging.' + $hash))
    $acquired = $false
    try {
        try { $acquired = $mutex.WaitOne(0) }
        catch [System.Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) { throw 'PACKAGING_BUSY: another lifecycle operation owns this installation.' }
        return $mutex
    } catch {
        $mutex.Dispose()
        throw
    }
}

# The journal is an interruption aid, not deletion authority. Recovery only moves
# validated snapshots between exact names; it never deletes a journal-named tree.
function Get-LlmSnapshotIdentity {
    param([string]$Directory)
    if (-not (Test-Path -LiteralPath $Directory)) { return $null }
    if (-not (Test-Path -LiteralPath $Directory -PathType Container) -or
        -not (Test-LlmRollbackSnapshot -Directory $Directory)) {
        throw 'PACKAGING_RECOVERY_REQUIRED: snapshot ownership is invalid.'
    }
    return [pscustomobject]@{ marker = (Get-LlmFileHash -Path (Join-Path $Directory $script:StateFileName)); payload = (Get-LlmPayloadHash -Directory $Directory) }
}

function Get-LlmPayloadHash {
    param([string]$Directory)
    $hashes = Get-LlmTreeHashes -Directory $Directory
    $inventory = @($hashes.Keys | Sort-Object -CaseSensitive | ForEach-Object {
        [ordered]@{ path = $_; hash = $hashes[$_] }
    }) | ConvertTo-Json -Depth 4 -Compress
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $payload = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($inventory))) -replace '-', '').ToLowerInvariant()
    } finally { $sha.Dispose() }
    return $payload
}

function Test-LlmSnapshotIdentity {
    param($Actual, $Expected)
    return $null -ne $Actual -and $null -ne $Expected -and
        $Actual.marker -ceq $Expected.marker -and $Actual.payload -ceq $Expected.payload
}

function Write-LlmPackagingJournal {
    param([string]$InstallDirectory, [hashtable]$Journal)
    $path = "$InstallDirectory.packaging-transaction.json"
    $pending = "$path.$($Journal.id).tmp"
    if (Test-Path -LiteralPath $path) { throw 'PACKAGING_RECOVERY_REQUIRED: an unresolved transaction already exists.' }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($Journal | ConvertTo-Json -Depth 5 -Compress))
    if ($bytes.Length -gt 16384) { throw 'PACKAGING_RECOVERY_REQUIRED: transaction journal is too large.' }
    $stream = [IO.FileStream]::new($pending, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
    [IO.File]::Move($pending, $path)
}

function Close-LlmPackagingJournal {
    param([string]$InstallDirectory, [string]$Id)
    $path = "$InstallDirectory.packaging-transaction.json"
    # Retain the resolved record. Never overwrite an existing receipt.
    [IO.File]::Move($path, "$InstallDirectory.packaging-resolved-$Id.json")
}

function Read-LlmPackagingJournal {
    param([string]$Path)
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        # The limit applies to actual bytes, including growth between stat and open.
        $bytes = New-Object byte[] 16385
        $count = 0
        while ($count -lt $bytes.Length) {
            $read = $stream.Read($bytes, $count, $bytes.Length - $count)
            if ($read -eq 0) { break }
            $count += $read
        }
        if ($count -gt 16384) { throw 'Transaction journal is too large.' }
        return ([Text.UTF8Encoding]::new($false, $true)).GetString($bytes, 0, $count) | ConvertFrom-Json
    } finally { $stream.Dispose() }
}

function Repair-LlmPackagingTransaction {
    param([string]$InstallDirectory, [string]$AuthorizedRollback)
    $install = Resolve-LlmCanonicalPath -Path $InstallDirectory
    $journalPath = "$install.packaging-transaction.json"
    if (-not (Test-Path -LiteralPath $journalPath)) { return $null }
    try {
        $item = Get-Item -LiteralPath $journalPath -Force -ErrorAction Stop
        if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $item.Length -gt 16384) {
            throw 'Invalid journal file.'
        }
        $journal = Read-LlmPackagingJournal -Path $journalPath
        $keys = @('format','id','operation','install','rollback','old','candidate','priorRollback','shortcuts')
        if ($null -eq $journal -or @($journal.PSObject.Properties).Count -ne $keys.Count) { throw 'Invalid journal schema.' }
        foreach ($key in $keys) { if ($null -eq $journal.PSObject.Properties[$key]) { throw 'Invalid journal schema.' } }
        if ($journal.format -ne 1 -or $journal.id -cnotmatch '^[0-9a-f]{32}$' -or
            $journal.operation -cnotin @('update','rollback') -or $journal.shortcuts -isnot [bool] -or
            -not [string]::Equals([string]$journal.install, $install, [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::IsNullOrWhiteSpace((Test-LlmPathProtectedRoot -CanonicalPath $install))) { throw 'Invalid transaction binding.' }
        foreach ($identity in @($journal.old, $journal.candidate, $journal.priorRollback)) {
            if ($null -eq $identity) { continue }
            if (@($identity.PSObject.Properties).Count -ne 2 -or $identity.marker -cnotmatch '^[0-9a-f]{64}$' -or
                $identity.payload -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid snapshot identity.' }
        }
        if ($null -eq $journal.old -or $null -eq $journal.candidate) { throw 'Missing snapshot identity.' }
        if (Test-Path -LiteralPath "$install.packaging-resolved-$($journal.id).json") { throw 'Resolved receipt destination is occupied.' }
        $superseded = "$install.superseded-$($journal.id)"
        $staging = "$install.staging-$($journal.id)"
        $rollback = [string]$journal.rollback
        $rollbackStaging = "$rollback.staging-$($journal.id)"
        $rollbackSuperseded = "$rollback.superseded-$($journal.id)"
        foreach ($path in @($install, $superseded, $staging, $rollback, $rollbackStaging, $rollbackSuperseded)) {
            if (-not [string]::Equals((ConvertTo-LlmNormalizedPath -Path $path), (Resolve-LlmCanonicalPath -Path $path),
                    [StringComparison]::OrdinalIgnoreCase)) { throw 'Reparse path is not accepted for recovery.' }
        }
        $oldLocation = if (Test-Path -LiteralPath $superseded) { $superseded } else { $install }
        $oldState = Read-LlmState -InstallDirectory $oldLocation
        $allowedRollback = if (-not [string]::IsNullOrWhiteSpace($AuthorizedRollback)) {
            Resolve-LlmCanonicalPath -Path $AuthorizedRollback
        } elseif ($null -ne $oldState -and -not [string]::IsNullOrWhiteSpace([string]$oldState.rollbackDirectory)) {
            Resolve-LlmCanonicalPath -Path ([string]$oldState.rollbackDirectory)
        } else { Resolve-LlmCanonicalPath -Path (Get-LlmRollbackDirectory -InstallDirectory $install) }
        $defaultRollback = Resolve-LlmCanonicalPath -Path (Get-LlmRollbackDirectory -InstallDirectory $install)
        if (-not [string]::Equals($rollback, $allowedRollback, [StringComparison]::OrdinalIgnoreCase) -and
            -not ([string]::IsNullOrWhiteSpace($AuthorizedRollback) -and
                [string]::Equals($rollback, $defaultRollback, [StringComparison]::OrdinalIgnoreCase))) {
            throw 'Rollback path requires the original explicit authorization.'
        }
        if ($null -eq $oldState -or [string]::IsNullOrWhiteSpace([string]$oldState.dataDirectory) -or
            -not [string]::Equals((Resolve-LlmCanonicalPath -Path ([string]$oldState.installDirectory)), $install, [StringComparison]::OrdinalIgnoreCase)) { throw 'Missing old install/data binding.' }
        [void](Assert-LlmRollbackPathSafe -RollbackDirectory $rollback -ProtectedPaths @($install, [string]$oldState.dataDirectory))
        foreach ($sidecar in @($superseded, $staging, $rollbackStaging, $rollbackSuperseded)) {
            if ((Test-LlmPathWithin -Path $sidecar -Root ([string]$oldState.dataDirectory)) -or
                (Test-LlmPathWithin -Path ([string]$oldState.dataDirectory) -Root $sidecar)) { throw 'Recovery overlaps user data.' }
        }
        $current = Get-LlmSnapshotIdentity -Directory $install
        $old = Get-LlmSnapshotIdentity -Directory $superseded
        $candidate = Get-LlmSnapshotIdentity -Directory $staging
        $rb = Get-LlmSnapshotIdentity -Directory $rollback
        $rbStage = Get-LlmSnapshotIdentity -Directory $rollbackStaging
        $rbOld = Get-LlmSnapshotIdentity -Directory $rollbackSuperseded
        # Validate every participant before performing even the first repair move.
        if ($null -ne $old -and -not (Test-LlmSnapshotIdentity $old $journal.old)) { throw 'Old snapshot changed.' }
        if ($null -ne $candidate -and -not (Test-LlmSnapshotIdentity $candidate $journal.candidate)) { throw 'Candidate changed.' }
        if ($null -ne $rbStage -and -not (Test-LlmSnapshotIdentity $rbStage $journal.old)) { throw 'Rollback staging changed.' }
        if ($null -ne $rbOld -and -not (Test-LlmSnapshotIdentity $rbOld $journal.priorRollback)) { throw 'Previous rollback changed.' }
        foreach ($path in @($install, $superseded, $staging, $rollback, $rollbackStaging, $rollbackSuperseded)) {
            if (Test-Path -LiteralPath $path) { Assert-LlmNotRunning -InstallDirectory $path }
        }
        $completed = $false
        if ($journal.operation -eq 'update') {
            $currentOld = Test-LlmSnapshotIdentity $current $journal.old
            $currentNew = Test-LlmSnapshotIdentity $current $journal.candidate
            if ($null -ne $current -and -not $currentOld -and -not $currentNew) { throw 'Installation changed.' }
            if ($currentOld -and $null -ne $old) { throw 'Duplicate old installation.' }
            if (-not $currentOld -and $null -eq $old) { throw 'Old installation unavailable.' }
            if ($currentNew -and $null -ne $candidate) { throw 'Duplicate new installation.' }
            $rbIsOld = Test-LlmSnapshotIdentity $rb $journal.old
            $rbIsPrior = Test-LlmSnapshotIdentity $rb $journal.priorRollback
            if ($null -ne $rb -and -not $rbIsOld -and -not $rbIsPrior) { throw 'Rollback changed.' }
            if ($currentNew -and $rbIsOld -and $null -eq $rbStage) {
                $newState = Read-LlmState -InstallDirectory $install
                if ($newState.previousVersion -cne $oldState.version -or
                    -not [string]::Equals((Resolve-LlmCanonicalPath -Path ([string]$newState.installDirectory)), $install, [StringComparison]::OrdinalIgnoreCase) -or
                    -not [string]::Equals([string]$newState.dataDirectory, [string]$oldState.dataDirectory, [StringComparison]::OrdinalIgnoreCase) -or
                    -not [string]::Equals([string]$newState.rollbackDirectory, $rollback, [StringComparison]::OrdinalIgnoreCase) -or
                    $newState.rollbackVersion -cne $oldState.version) { throw 'Completed pair binding changed.' }
                [void](Test-LlmInstallation -InstallDirectory $install)
                [void](Test-LlmInstallation -InstallDirectory $rollback)
                $completed = $true
            } else {
                if ($rbIsOld -and -not $rbIsPrior) { throw 'Promoted rollback contradicts an incomplete installation.' }
                if ($currentNew -and $journal.shortcuts) { throw 'Interrupted shortcut publication requires operator inspection.' }
                if ($null -ne $journal.priorRollback -and $null -eq $rbOld -and -not $rbIsPrior) { throw 'Previous rollback unavailable.' }
                if ($null -ne $rbOld -and $null -ne $rb -and $null -ne $rbStage) { throw 'Rollback destination is occupied.' }
                if ($currentNew) { Move-Item -LiteralPath $install -Destination $staging }
                if ($null -ne $rbOld) {
                    if ($null -ne $rb) { Move-Item -LiteralPath $rollback -Destination $rollbackStaging }
                    Move-Item -LiteralPath $rollbackSuperseded -Destination $rollback
                }
                if (-not $currentOld) { Move-Item -LiteralPath $superseded -Destination $install }
            }
        } else {
            if ($null -ne $candidate -or $null -ne $rbStage -or $null -ne $rbOld) { throw 'Unexpected rollback sidecar.' }
            $currentOld = Test-LlmSnapshotIdentity $current $journal.old
            $currentCandidate = Test-LlmSnapshotIdentity $current $journal.candidate
            $rbCandidate = Test-LlmSnapshotIdentity $rb $journal.candidate
            if ($null -ne $rb -and -not $rbCandidate) { throw 'Rollback snapshot changed.' }
            if ($currentOld -and ($null -ne $old -or -not $rbCandidate)) { throw 'Contradictory original installation.' }
            if (-not $currentOld -and $null -eq $old) { throw 'Original installation unavailable.' }
            if ($null -ne $current -and -not $currentOld -and -not $currentCandidate) {
                $restored = Read-LlmState -InstallDirectory $install
                if ($current.payload -cne $journal.candidate.payload -or $null -ne $rb -or
                    $restored.product -cne $script:ProductName -or $restored.previousVersion -cne $oldState.version -or
                    -not [string]::Equals((Resolve-LlmCanonicalPath -Path ([string]$restored.installDirectory)), $install, [StringComparison]::OrdinalIgnoreCase) -or
                    -not [string]::Equals([string]$restored.dataDirectory, [string]$oldState.dataDirectory, [StringComparison]::OrdinalIgnoreCase) -or
                    $null -ne $restored.rollbackDirectory -or $null -ne $restored.rollbackVersion) { throw 'Restored installation changed.' }
                [void](Test-LlmInstallation -InstallDirectory $install)
                $completed = $true
            } elseif (-not $currentOld) {
                if ($currentCandidate -and $null -ne $rb) { throw 'Duplicate rollback candidate.' }
                if ($null -eq $current -and -not $rbCandidate) { throw 'Rollback candidate unavailable.' }
                if ($currentCandidate) { Move-Item -LiteralPath $install -Destination $rollback }
                Move-Item -LiteralPath $superseded -Destination $install
            }
        }
        Close-LlmPackagingJournal -InstallDirectory $install -Id $journal.id
        return [pscustomobject]@{ completed = $completed; operation = $journal.operation; candidate = $journal.candidate }
    } catch {
        throw "PACKAGING_RECOVERY_REQUIRED: unresolved transaction retained for inspection. $($_.Exception.Message)"
    }
}

function Invoke-LlmInstall {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $true)][string]$DataDirectory,
        [Parameter(Mandatory = $false)][string]$ShortcutRoot,
        [Parameter(Mandatory = $false)][string]$DesktopShortcutRoot,
        [Parameter(Mandatory = $false)][bool]$CreateShortcuts = $true,
        [Parameter(Mandatory = $false)][bool]$CreateDesktopShortcut = $true)

    $lease = Enter-LlmPackagingLease -InstallDirectory $InstallDirectory
    try {
    [void](Repair-LlmPackagingTransaction -InstallDirectory $InstallDirectory)
    $publishedManifestHash = Get-LlmPublishedManifestHash -Directory $SourceDirectory
    $executable = Assert-LlmPackage -SourceDirectory $SourceDirectory -ExpectedManifestHash $publishedManifestHash
    $version = Read-LlmPackageVersion -Directory $SourceDirectory

    if (Test-Path -LiteralPath $InstallDirectory) {
        $state = Read-LlmState -InstallDirectory $InstallDirectory
        $installedVersion = if ($null -ne $state) { [string]$state.version } else { 'unknown' }
        throw "ALREADY_INSTALLED: '$InstallDirectory' already exists (version '$installedVersion'); use Update-LLMWorkGUI.ps1."
    }

    $canonicalInstall = Resolve-LlmCanonicalPath -Path $InstallDirectory
    $canonicalData = Resolve-LlmCanonicalPath -Path $DataDirectory
    if (-not [string]::IsNullOrWhiteSpace((Test-LlmPathProtectedRoot -CanonicalPath $canonicalInstall))) {
        throw 'INSTALL_PATH_INVALID: installation directory resolves to a protected root.'
    }
    if ((Test-LlmPathWithin -Path $canonicalInstall -Root $canonicalData) -or
        (Test-LlmPathWithin -Path $canonicalData -Root $canonicalInstall)) {
        throw "INSTALL_PATH_INVALID: installation and user data directories must not overlap."
    }

    if (-not (Test-Path -LiteralPath $DataDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
    }

    $parent = Split-Path -Parent $InstallDirectory
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    $staging = "$InstallDirectory.staging-$([Guid]::NewGuid().ToString('N'))"
    $createdShortcuts = New-Object System.Collections.ArrayList
    $activated = $false
    $shortcutUndo = @{}

    try {
        Copy-LlmDirectoryTree -Source $SourceDirectory -Destination $staging
        Assert-LlmPublishedHashes -Directory $staging -ExpectedManifestHash $publishedManifestHash

        $state = @{
            product           = $script:ProductName
            version           = $version
            installedAtUtc    = (Get-Date).ToUniversalTime().ToString('o')
            installDirectory  = $InstallDirectory
            dataDirectory     = $DataDirectory
            previousVersion   = $null
            rollbackDirectory = $null
            rollbackVersion   = $null
            executable        = [System.IO.Path]::GetFileName($executable)
            executableHash    = (Get-LlmFileHash -Path (Join-Path $staging ([System.IO.Path]::GetFileName($executable))))
        }
        Write-LlmState -Directory $staging -State $state

        Move-Item -LiteralPath $staging -Destination $InstallDirectory
        $activated = $true

        if ($CreateShortcuts -or $CreateDesktopShortcut) {
            $installedExecutable = Get-LlmExecutable -Directory $InstallDirectory
            if ($CreateShortcuts) {
                $root = if ([string]::IsNullOrWhiteSpace($ShortcutRoot)) { Get-LlmDefaultShortcutRoot } else { $ShortcutRoot }
                $path = New-LlmShortcut -ShortcutPath (Join-Path $root $script:ShortcutName) -TargetPath $installedExecutable -WorkingDirectory $InstallDirectory -UndoLog $shortcutUndo
                [void]$createdShortcuts.Add($path)
            }

            if ($CreateDesktopShortcut) {
                $desktopRoot = if ([string]::IsNullOrWhiteSpace($DesktopShortcutRoot)) { Get-LlmDefaultDesktopShortcutRoot } else { $DesktopShortcutRoot }
                $path = New-LlmShortcut -ShortcutPath (Join-Path $desktopRoot $script:ShortcutName) -TargetPath $installedExecutable -WorkingDirectory $InstallDirectory -UndoLog $shortcutUndo
                [void]$createdShortcuts.Add($path)
            }
        }

        $validationChecks = Test-LlmInstallation -InstallDirectory $InstallDirectory -ShortcutPaths @($createdShortcuts)

        return New-LlmResult -Operation 'install' -InstallDirectory $InstallDirectory -Version $version -DataDirectory $DataDirectory -Extra @{
            shortcuts        = @($createdShortcuts)
            validated        = $true
            validationChecks = @($validationChecks)
            fileCount        = @(Get-ChildItem -LiteralPath $InstallDirectory -Recurse -Force -File).Count
        }
    }
    catch {
        if ($activated) {
            Restore-LlmShortcuts -UndoLog $shortcutUndo
            Remove-LlmPathSafe -Path $InstallDirectory
        }
        Remove-LlmPathSafe -Path $staging
        throw
    }
    } finally { $lease.ReleaseMutex(); $lease.Dispose() }
}

function Invoke-LlmUpdate {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $false)][string]$DataDirectory,
        [Parameter(Mandatory = $false)][string]$RollbackDirectory,
        [Parameter(Mandatory = $false)][bool]$CreateShortcuts = $false,
        [Parameter(Mandatory = $false)][string]$ShortcutRoot,
        [Parameter(Mandatory = $false)][bool]$CreateDesktopShortcut = $false,
        [Parameter(Mandatory = $false)][string]$DesktopShortcutRoot)

    $lease = Enter-LlmPackagingLease -InstallDirectory $InstallDirectory
    try {
    $publishedManifestHash = Get-LlmPublishedManifestHash -Directory $SourceDirectory
    $newExecutable = Assert-LlmPackage -SourceDirectory $SourceDirectory -ExpectedManifestHash $publishedManifestHash
    $newVersion = Read-LlmPackageVersion -Directory $SourceDirectory
    $repaired = Repair-LlmPackagingTransaction -InstallDirectory $InstallDirectory -AuthorizedRollback $RollbackDirectory
    if ($null -ne $repaired -and $repaired.completed -and $repaired.operation -eq 'update' -and
        (Get-LlmPayloadHash -Directory $SourceDirectory) -ceq $repaired.candidate.payload) {
        $recoveredState = Read-LlmState -InstallDirectory $InstallDirectory
        return New-LlmResult -Operation 'update' -InstallDirectory $InstallDirectory -Version ([string]$recoveredState.version) -DataDirectory ([string]$recoveredState.dataDirectory) -Extra @{
            recovered = $true; validated = $true; validationChecks = @(Test-LlmInstallation -InstallDirectory $InstallDirectory)
            previousVersion = $recoveredState.previousVersion; rollbackDirectory = $recoveredState.rollbackDirectory; rollbackVersion = $recoveredState.rollbackVersion
            fileCount = (Get-LlmTreeHashes -Directory $InstallDirectory).Count + 1; shortcuts = @()
        }
    }

    if (-not (Test-Path -LiteralPath $InstallDirectory -PathType Container)) {
        throw "NOT_INSTALLED: '$InstallDirectory' does not exist; use Install-LLMWorkGUI.ps1."
    }

    Assert-LlmOwnedInstallTarget -InstallDirectory $InstallDirectory
    Assert-LlmNotRunning -InstallDirectory $InstallDirectory

    $state = Read-LlmState -InstallDirectory $InstallDirectory
    $previousVersion = if ($null -ne $state -and -not [string]::IsNullOrWhiteSpace([string]$state.version)) { [string]$state.version } else { 'unknown' }
    $resolvedDataDirectory = if ($null -ne $state -and -not [string]::IsNullOrWhiteSpace([string]$state.dataDirectory)) { [string]$state.dataDirectory } else { $DataDirectory }
    if ([string]::IsNullOrWhiteSpace($resolvedDataDirectory)) {
        $resolvedDataDirectory = Get-LlmDefaultDataDirectory
    }

    $canonicalInstall = Resolve-LlmCanonicalPath -Path $InstallDirectory
    $canonicalData = Resolve-LlmCanonicalPath -Path $resolvedDataDirectory
    if ((Test-LlmPathWithin -Path $canonicalInstall -Root $canonicalData) -or
        (Test-LlmPathWithin -Path $canonicalData -Root $canonicalInstall)) {
        throw 'UPDATE_PATH_INVALID: installation and user data directories must not overlap.'
    }

    $resolvedRollback = if ([string]::IsNullOrWhiteSpace($RollbackDirectory)) { Get-LlmRollbackDirectory -InstallDirectory $InstallDirectory } else { $RollbackDirectory }

    $canonicalRollback = Assert-LlmRollbackPathSafe -RollbackDirectory $resolvedRollback -ProtectedPaths @(
        (ConvertTo-LlmNormalizedPath -Path $SourceDirectory),
        (ConvertTo-LlmNormalizedPath -Path $InstallDirectory),
        (ConvertTo-LlmNormalizedPath -Path $resolvedDataDirectory)
    )

    if (Test-Path -LiteralPath $resolvedRollback) {
        if (-not (Test-LlmRollbackOwnedByState -CanonicalRollbackPath $canonicalRollback -State $state)) {
            throw "ROLLBACK_NOT_OWNED: existing path '$resolvedRollback' is not a rollback snapshot recorded in $script:StateFileName; refusing to replace it."
        }
    }

    if (-not (Test-Path -LiteralPath $resolvedDataDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $resolvedDataDirectory -Force | Out-Null
    }

    $transactionId = [Guid]::NewGuid().ToString('N')
    $staging = "$canonicalInstall.staging-$transactionId"
    $superseded = "$canonicalInstall.superseded-$transactionId"
    $rollbackStaging = "$canonicalRollback.staging-$transactionId"
    $rollbackSuperseded = "$canonicalRollback.superseded-$transactionId"
    $rollbackActivated = $false
    $committed = $false
    $shortcutUndo = @{}

    try {
        Copy-LlmDirectoryTree -Source $InstallDirectory -Destination $rollbackStaging
        Copy-LlmDirectoryTree -Source $SourceDirectory -Destination $staging
        Assert-LlmPublishedHashes -Directory $staging -ExpectedManifestHash $publishedManifestHash

        $newState = @{
            product           = $script:ProductName
            version           = $newVersion
            installedAtUtc    = if ($null -ne $state) { [string]$state.installedAtUtc } else { (Get-Date).ToUniversalTime().ToString('o') }
            updatedAtUtc      = (Get-Date).ToUniversalTime().ToString('o')
            installDirectory  = $InstallDirectory
            dataDirectory     = $resolvedDataDirectory
            previousVersion   = $previousVersion
            rollbackDirectory = $canonicalRollback
            rollbackVersion   = $previousVersion
            executable        = [System.IO.Path]::GetFileName($newExecutable)
            executableHash    = (Get-LlmFileHash -Path (Join-Path $staging ([System.IO.Path]::GetFileName($newExecutable))))
        }
        Write-LlmState -Directory $staging -State $newState

        Write-LlmPackagingJournal -InstallDirectory $canonicalInstall -Journal @{
            format = 1; id = $transactionId; operation = 'update'; install = $canonicalInstall; rollback = $canonicalRollback
            old = (Get-LlmSnapshotIdentity -Directory $InstallDirectory); candidate = (Get-LlmSnapshotIdentity -Directory $staging)
            priorRollback = (Get-LlmSnapshotIdentity -Directory $resolvedRollback); shortcuts = ($CreateShortcuts -or $CreateDesktopShortcut)
        }

        Move-Item -LiteralPath $InstallDirectory -Destination $superseded
        try {
            Move-Item -LiteralPath $staging -Destination $InstallDirectory
        }
        catch {
            Move-Item -LiteralPath $superseded -Destination $InstallDirectory
            throw "UPDATE_FAILED: unable to activate the staged update; previous installation restored. $($_.Exception.Message)"
        }

        $createdShortcuts = New-Object System.Collections.ArrayList
        if ($CreateShortcuts -or $CreateDesktopShortcut) {
            $installedExecutable = Get-LlmExecutable -Directory $InstallDirectory
            if ($CreateShortcuts) {
                $root = if ([string]::IsNullOrWhiteSpace($ShortcutRoot)) { Get-LlmDefaultShortcutRoot } else { $ShortcutRoot }
                $path = New-LlmShortcut -ShortcutPath (Join-Path $root $script:ShortcutName) -TargetPath $installedExecutable -WorkingDirectory $InstallDirectory -UndoLog $shortcutUndo
                [void]$createdShortcuts.Add($path)
            }

            if ($CreateDesktopShortcut) {
                $desktopRoot = if ([string]::IsNullOrWhiteSpace($DesktopShortcutRoot)) { Get-LlmDefaultDesktopShortcutRoot } else { $DesktopShortcutRoot }
                $path = New-LlmShortcut -ShortcutPath (Join-Path $desktopRoot $script:ShortcutName) -TargetPath $installedExecutable -WorkingDirectory $InstallDirectory -UndoLog $shortcutUndo
                [void]$createdShortcuts.Add($path)
            }
        }

        $validationChecks = Test-LlmInstallation -InstallDirectory $InstallDirectory -ShortcutPaths @($createdShortcuts)

        # Preserve the last known rollback until the replacement installation
        # has passed every check, including shortcuts.
        if (Test-Path -LiteralPath $resolvedRollback) {
            Move-Item -LiteralPath $resolvedRollback -Destination $rollbackSuperseded
        }
        Move-Item -LiteralPath $rollbackStaging -Destination $resolvedRollback
        $rollbackActivated = $true
        $committed = $true
        Close-LlmPackagingJournal -InstallDirectory $canonicalInstall -Id $transactionId

        # Activation and its rollback now agree. Cleanup failure must not undo
        # only half of that committed pair. The retained sidecars remain owned.
        foreach ($obsolete in @($superseded, $rollbackSuperseded)) {
            try { Remove-LlmPathSafe -Path $obsolete }
            catch { Write-Warning "Committed update retained an obsolete snapshot for later cleanup: $obsolete" }
        }

        return New-LlmResult -Operation 'update' -InstallDirectory $InstallDirectory -Version $newVersion -DataDirectory $resolvedDataDirectory -Extra @{
            previousVersion   = $previousVersion
            rollbackDirectory = $canonicalRollback
            rollbackVersion   = $previousVersion
            shortcuts         = @($createdShortcuts)
            validated         = $true
            validationChecks  = @($validationChecks)
            fileCount         = @(Get-ChildItem -LiteralPath $InstallDirectory -Recurse -Force -File).Count
        }
    }
    catch {
        if ($committed) { throw }
        Restore-LlmShortcuts -UndoLog $shortcutUndo
        if (Test-Path -LiteralPath $superseded -PathType Container) {
            Remove-LlmPathSafe -Path $InstallDirectory
            Move-Item -LiteralPath $superseded -Destination $InstallDirectory
        }
        if (Test-Path -LiteralPath $rollbackSuperseded -PathType Container) {
            if ($rollbackActivated) { Remove-LlmPathSafe -Path $resolvedRollback }
            Move-Item -LiteralPath $rollbackSuperseded -Destination $resolvedRollback
        }
        Remove-LlmPathSafe -Path $rollbackStaging
        Remove-LlmPathSafe -Path $staging
        if (Test-Path -LiteralPath "$canonicalInstall.packaging-transaction.json") {
            [void](Repair-LlmPackagingTransaction -InstallDirectory $InstallDirectory -AuthorizedRollback $resolvedRollback)
        }
        throw
    }
    } finally { $lease.ReleaseMutex(); $lease.Dispose() }
}

function Invoke-LlmRollback {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $false)][string]$RollbackDirectory)

    $lease = Enter-LlmPackagingLease -InstallDirectory $InstallDirectory
    try {
    $repaired = Repair-LlmPackagingTransaction -InstallDirectory $InstallDirectory -AuthorizedRollback $RollbackDirectory
    if ($null -ne $repaired -and $repaired.completed -and $repaired.operation -eq 'rollback') {
        $recoveredState = Read-LlmState -InstallDirectory $InstallDirectory
        return New-LlmResult -Operation 'rollback' -InstallDirectory $InstallDirectory -Version ([string]$recoveredState.version) -DataDirectory ([string]$recoveredState.dataDirectory) -Extra @{
            recovered = $true; validated = $true; validationChecks = @(Test-LlmInstallation -InstallDirectory $InstallDirectory)
            rolledBackFromVersion = $recoveredState.previousVersion
            fileCount = (Get-LlmTreeHashes -Directory $InstallDirectory).Count + 1
        }
    }
    if (-not (Test-Path -LiteralPath $InstallDirectory -PathType Container)) {
        throw "NOT_INSTALLED: '$InstallDirectory' does not exist; nothing to roll back."
    }

    Assert-LlmOwnedInstallTarget -InstallDirectory $InstallDirectory
    Assert-LlmNotRunning -InstallDirectory $InstallDirectory

    $state = Read-LlmState -InstallDirectory $InstallDirectory
    $resolvedRollback = if (-not [string]::IsNullOrWhiteSpace($RollbackDirectory)) { $RollbackDirectory } elseif ($null -ne $state -and -not [string]::IsNullOrWhiteSpace([string]$state.rollbackDirectory)) { [string]$state.rollbackDirectory } else { Get-LlmRollbackDirectory -InstallDirectory $InstallDirectory }
    $dataDirectory = if ($null -ne $state -and -not [string]::IsNullOrWhiteSpace([string]$state.dataDirectory)) { [string]$state.dataDirectory } else { Get-LlmDefaultDataDirectory }

    $canonicalInstall = Resolve-LlmCanonicalPath -Path $InstallDirectory
    $canonicalData = Resolve-LlmCanonicalPath -Path $dataDirectory
    if ((Test-LlmPathWithin -Path $canonicalInstall -Root $canonicalData) -or
        (Test-LlmPathWithin -Path $canonicalData -Root $canonicalInstall)) {
        throw 'ROLLBACK_PATH_INVALID: installation and user data directories must not overlap.'
    }
    [void](Assert-LlmRollbackPathSafe -RollbackDirectory $resolvedRollback -ProtectedPaths @($dataDirectory, $InstallDirectory))

    if (-not (Test-Path -LiteralPath $resolvedRollback -PathType Container)) {
        throw "ROLLBACK_NOT_AVAILABLE: rollback snapshot '$resolvedRollback' does not exist."
    }

    $canonicalRollback = Resolve-LlmCanonicalPath -Path $resolvedRollback
    if (-not (Test-LlmRollbackOwnedByState -CanonicalRollbackPath $canonicalRollback -State $state) -or
        -not (Test-LlmRollbackSnapshot -Directory $resolvedRollback)) {
        throw 'ROLLBACK_NOT_AVAILABLE: snapshot ownership is not verified.'
    }
    [void](Test-LlmInstallation -InstallDirectory $resolvedRollback)

    $rollbackExecutable = Get-LlmExecutable -Directory $resolvedRollback
    if ($null -eq $rollbackExecutable) {
        throw "ROLLBACK_NOT_AVAILABLE: rollback snapshot '$resolvedRollback' contains no product executable."
    }

    $currentVersion = if ($null -ne $state -and -not [string]::IsNullOrWhiteSpace([string]$state.version)) { [string]$state.version } else { 'unknown' }
    $rollbackVersion = Read-LlmPackageVersion -Directory $resolvedRollback
    $rollbackStatePath = Join-Path $resolvedRollback $script:StateFileName
    $originalRollbackState = if (Test-Path -LiteralPath $rollbackStatePath -PathType Leaf) { [System.IO.File]::ReadAllBytes($rollbackStatePath) } else { $null }
    $rollbackStateWritten = $false

    $transactionId = [Guid]::NewGuid().ToString('N')
    $superseded = "$canonicalInstall.superseded-$transactionId"
    Write-LlmPackagingJournal -InstallDirectory $canonicalInstall -Journal @{
        format = 1; id = $transactionId; operation = 'rollback'; install = $canonicalInstall; rollback = $canonicalRollback
        old = (Get-LlmSnapshotIdentity -Directory $InstallDirectory); candidate = (Get-LlmSnapshotIdentity -Directory $resolvedRollback)
        priorRollback = $null; shortcuts = $false
    }

    Move-Item -LiteralPath $InstallDirectory -Destination $superseded
    try {
        Move-Item -LiteralPath $resolvedRollback -Destination $InstallDirectory
    }
    catch {
        Move-Item -LiteralPath $superseded -Destination $InstallDirectory
        [void](Repair-LlmPackagingTransaction -InstallDirectory $InstallDirectory -AuthorizedRollback $resolvedRollback)
        throw "ROLLBACK_FAILED: unable to activate the rollback snapshot; current installation restored. $($_.Exception.Message)"
    }

    try {
        $restoredState = @{
            product           = $script:ProductName
            version           = $rollbackVersion
            installedAtUtc    = (Get-Date).ToUniversalTime().ToString('o')
            updatedAtUtc      = (Get-Date).ToUniversalTime().ToString('o')
            installDirectory  = $InstallDirectory
            dataDirectory     = $dataDirectory
            previousVersion   = $currentVersion
            rollbackDirectory = $null
            rollbackVersion   = $null
            executable        = [System.IO.Path]::GetFileName($rollbackExecutable)
            executableHash    = (Get-LlmFileHash -Path (Join-Path $InstallDirectory ([System.IO.Path]::GetFileName($rollbackExecutable))))
        }
        Write-LlmState -Directory $InstallDirectory -State $restoredState
        $rollbackStateWritten = $true
        $validationChecks = Test-LlmInstallation -InstallDirectory $InstallDirectory
    }
    catch {
        Move-Item -LiteralPath $InstallDirectory -Destination $resolvedRollback
        Move-Item -LiteralPath $superseded -Destination $InstallDirectory
        if ($rollbackStateWritten) {
            if ($null -ne $originalRollbackState) { [System.IO.File]::WriteAllBytes($rollbackStatePath, [byte[]]$originalRollbackState) }
            else { Remove-Item -LiteralPath $rollbackStatePath -Force }
        }
        [void](Repair-LlmPackagingTransaction -InstallDirectory $InstallDirectory -AuthorizedRollback $resolvedRollback)
        throw
    }

    Close-LlmPackagingJournal -InstallDirectory $canonicalInstall -Id $transactionId
    try { Remove-LlmPathSafe -Path $superseded }
    catch { Write-Warning "Committed rollback retained an obsolete snapshot for later cleanup: $superseded" }

    return New-LlmResult -Operation 'rollback' -InstallDirectory $InstallDirectory -Version $rollbackVersion -DataDirectory $dataDirectory -Extra @{
        rolledBackFromVersion = $currentVersion
        validated             = $true
        validationChecks      = @($validationChecks)
        fileCount             = @(Get-ChildItem -LiteralPath $InstallDirectory -Recurse -Force -File).Count
    }
    } finally { $lease.ReleaseMutex(); $lease.Dispose() }
}

function Invoke-LlmUninstall {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $false)][string]$DataDirectory,
        [Parameter(Mandatory = $false)][bool]$RemoveUserData = $false,
        [Parameter(Mandatory = $false)][string[]]$ShortcutRoots = @())

    $lease = Enter-LlmPackagingLease -InstallDirectory $InstallDirectory
    try {
    [void](Repair-LlmPackagingTransaction -InstallDirectory $InstallDirectory)
    if (-not (Test-Path -LiteralPath $InstallDirectory -PathType Container)) {
        throw "NOT_INSTALLED: '$InstallDirectory' does not exist; nothing to uninstall."
    }

    $state = Read-LlmState -InstallDirectory $InstallDirectory
    if ($null -eq $state -or -not (Test-LlmRollbackSnapshot -Directory $InstallDirectory)) {
        throw "UNINSTALL_OWNERSHIP_INVALID: a readable LLMWorkGUI install-state.json is required; refusing to delete '$InstallDirectory'."
    }

    Assert-LlmOwnedInstallTarget -InstallDirectory $InstallDirectory

    $canonicalInstall = Resolve-LlmCanonicalPath -Path $InstallDirectory
    $protectedInstallRoot = Test-LlmPathProtectedRoot -CanonicalPath $canonicalInstall
    if (-not [string]::IsNullOrWhiteSpace($protectedInstallRoot)) {
        throw "UNINSTALL_PATH_INVALID: installation directory resolves to a protected root; refusing to delete '$InstallDirectory'."
    }

    Assert-LlmNotRunning -InstallDirectory $InstallDirectory

    $dataProperty = $state.PSObject.Properties['dataDirectory']
    $storedDataDirectory = if ($null -ne $dataProperty) { [string]$dataProperty.Value } else { $null }
    if ($RemoveUserData -and [string]::IsNullOrWhiteSpace($storedDataDirectory) -and [string]::IsNullOrWhiteSpace($DataDirectory)) {
        throw "UNINSTALL_PATH_INVALID: removing user data requires a recorded or explicitly supplied data directory."
    }
    $resolvedDataDirectory = if (-not [string]::IsNullOrWhiteSpace($storedDataDirectory)) { $storedDataDirectory } elseif (-not [string]::IsNullOrWhiteSpace($DataDirectory)) { $DataDirectory } else { Get-LlmDefaultDataDirectory }
    $canonicalData = Resolve-LlmCanonicalPath -Path $resolvedDataDirectory
    if ((Test-LlmPathWithin -Path $canonicalInstall -Root $canonicalData) -or
        (Test-LlmPathWithin -Path $canonicalData -Root $canonicalInstall)) {
        throw "UNINSTALL_PATH_INVALID: installation directory '$InstallDirectory' (resolves to '$canonicalInstall') overlaps user data directory '$resolvedDataDirectory' (resolves to '$canonicalData'); refusing to delete either."
    }

    if ($RemoveUserData) {
        # The installation marker is mutable metadata, not authority to delete arbitrary data.
        # Custom data cleanup requires the caller's exact path; otherwise only the product default is allowed.
        $authorizedDataDirectory = if (-not [string]::IsNullOrWhiteSpace($DataDirectory)) { $DataDirectory } else { Get-LlmDefaultDataDirectory }
        $canonicalAuthorizedData = Resolve-LlmCanonicalPath -Path $authorizedDataDirectory
        if (-not [string]::Equals($canonicalData, $canonicalAuthorizedData, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'UNINSTALL_DATA_AUTHORITY_INVALID: recorded data directory does not match the explicitly authorized directory or product default; refusing deletion.'
        }
        $protectedDataRoot = Test-LlmPathProtectedRoot -CanonicalPath $canonicalData
        if (-not [string]::IsNullOrWhiteSpace($protectedDataRoot)) {
            throw "UNINSTALL_PATH_INVALID: data directory '$resolvedDataDirectory' (resolves to '$canonicalData') is a protected root ('$protectedDataRoot'); refusing to delete."
        }
    }

    $defaultRollback = Get-LlmRollbackDirectory -InstallDirectory $InstallDirectory

    $rollbackCandidates = New-Object System.Collections.ArrayList
    [void]$rollbackCandidates.Add([pscustomobject]@{ Path = $defaultRollback; FromState = $false })
    if ($null -ne $state -and -not [string]::IsNullOrWhiteSpace([string]$state.rollbackDirectory)) {
        [void]$rollbackCandidates.Add([pscustomobject]@{ Path = [string]$state.rollbackDirectory; FromState = $true })
    }

    $roots = @($ShortcutRoots | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($roots.Count -eq 0) {
        $roots = @((Get-LlmDefaultShortcutRoot), (Get-LlmDefaultDesktopShortcutRoot))
    }

    $removedShortcuts = Remove-LlmShortcuts -InstallDirectory $InstallDirectory -Roots $roots

    $removedRollback = $false
    foreach ($candidate in $rollbackCandidates) {
        $candidatePath = [string]$candidate.Path
        if ([string]::IsNullOrWhiteSpace($candidatePath)) {
            continue
        }

        if (-not (Test-Path -LiteralPath $candidatePath -PathType Container)) {
            continue
        }

        $canonicalCandidate = $null
        try {
            $canonicalCandidate = Assert-LlmRollbackPathSafe -RollbackDirectory $candidatePath -ProtectedPaths @($resolvedDataDirectory, $InstallDirectory)
        }
        catch {
            continue
        }

        if (-not (Test-LlmRollbackOwnedByState -CanonicalRollbackPath $canonicalCandidate -State $state) -or
            -not (Test-LlmRollbackSnapshot -Directory $candidatePath)) {
            continue
        }

        try {
            Remove-LlmPathSafe -Path $candidatePath
            $removedRollback = $true
        }
        catch {
            continue
        }
    }

    $dataRemoved = $false
    if ($RemoveUserData -and (Test-Path -LiteralPath $resolvedDataDirectory -PathType Container)) {
        Remove-LlmPathSafe -Path $resolvedDataDirectory
        $dataRemoved = $true
    }

    # Keep the ownership marker until the requested data cleanup succeeds, so a
    # transient data lock leaves an installation that can still be uninstalled.
    Remove-LlmPathSafe -Path $InstallDirectory

    return New-LlmResult -Operation 'uninstall' -InstallDirectory $InstallDirectory -Version 'removed' -DataDirectory $resolvedDataDirectory -Extra @{
        removedShortcuts = @($removedShortcuts)
        removedRollback  = $removedRollback
        dataPreserved    = (-not $dataRemoved)
        dataRemoved      = $dataRemoved
    }
    } finally { $lease.ReleaseMutex(); $lease.Dispose() }
}

Export-ModuleMember -Function @(
    'Write-LlmPublishedHashes',
    'Get-LlmDefaultInstallDirectory',
    'Get-LlmDefaultDataDirectory',
    'Get-LlmDefaultShortcutRoot',
    'Get-LlmDefaultDesktopShortcutRoot',
    'Get-LlmRollbackDirectory',
    'Invoke-LlmInstall',
    'Invoke-LlmUpdate',
    'Invoke-LlmRollback',
    'Invoke-LlmUninstall'
)
