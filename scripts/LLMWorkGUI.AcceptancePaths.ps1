# Shared preflight: run before creating evidence directories or starting a build.
# These path checks reject observed links; they do not pin filesystem identities against replacement.
function Assert-LlmNoReparsePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    for ($component = $fullPath; -not [string]::IsNullOrEmpty($component);
        $component = [System.IO.Path]::GetDirectoryName($component)) {
        try {
            $attributes = [System.IO.File]::GetAttributes($component)
            if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'EVIDENCE_PATH_INVALID: the path traverses a filesystem link.'
            }
        }
        catch [System.IO.FileNotFoundException] { }
        catch [System.IO.DirectoryNotFoundException] { }
    }
    return $fullPath
}

function Assert-LlmEvidencePath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($fullRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'EVIDENCE_PATH_INVALID: the path must be inside its isolated root.'
    }
    return Assert-LlmNoReparsePath -Path $fullPath
}

function New-LlmEvidenceRunDirectory {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Root)
    $resolved = Assert-LlmEvidencePath -Path $Path -Root $Root
    if ([IO.Directory]::Exists($resolved)) {
        $entries = [IO.Directory]::EnumerateFileSystemEntries($resolved).GetEnumerator()
        try {
            if ($entries.MoveNext()) { throw 'EVIDENCE_ROOT_NOT_FRESH: an acceptance run must not reuse existing evidence or application data.' }
        } finally { $entries.Dispose() }
    }
    [void][IO.Directory]::CreateDirectory($resolved)
    # CreateNew arbitrates callers that raced the empty-directory observation. The marker remains
    # as immutable admission evidence; a later invocation must use another run root.
    $claim = [IO.File]::Open((Join-Path $resolved '.evidence-run-owner'), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes([Guid]::NewGuid().ToString('N'))
        $claim.Write($bytes, 0, $bytes.Length)
        $claim.Flush($true)
    } finally { $claim.Dispose() }
    return $resolved
}

function Wait-LlmAcceptanceProcess {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][ValidateRange(1, 2147483)][int]$TimeoutSeconds)

    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not $Process.WaitForExit(100)) {
        if ($clock.Elapsed.TotalSeconds -lt $TimeoutSeconds) { continue }
        # Verify root termination before returning a completed receipt. A timeout is never acceptance.
        try { $Process.Kill($true) }
        catch { try { $Process.Kill() } catch { } }
        if (-not $Process.WaitForExit(15000) -or -not $Process.HasExited) {
            throw "CHILD_TIMEOUT_STOP_UNCONFIRMED: owned process $($Process.Id) has not exited; cleanup remains required."
        }
        return [pscustomobject]@{ TimedOut = $true; ExitCode = $Process.ExitCode }
    }
    return [pscustomobject]@{ TimedOut = $false; ExitCode = $Process.ExitCode }
}
