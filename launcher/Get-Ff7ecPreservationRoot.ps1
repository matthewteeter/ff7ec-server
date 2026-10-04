<#
.SYNOPSIS
    Selects a writable preservation directory outside the source repository.
#>
[CmdletBinding()]
param([string]$Root = $env:FF7EC_PRESERVATION_ROOT)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$explicitRoot = -not [string]::IsNullOrWhiteSpace($Root)
if ($explicitRoot) {
    $candidates = @($Root)
} else {
    $localData = [Environment]::GetFolderPath("LocalApplicationData")
    if (-not $localData) { throw "Local application data directory is unavailable; set FF7EC_PRESERVATION_ROOT." }
    $candidates = @("E:\FF7EC_Preservation", "C:\FF7EC_Preservation", (Join-Path $localData "FF7EC_Preservation"))
}

foreach ($candidate in $candidates) {
    $probe = $null
    try {
        if (-not $explicitRoot -and -not (Test-Path -LiteralPath ([IO.Path]::GetPathRoot($candidate)))) { continue }
        $path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($candidate)
        if ($path.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
            $path.StartsWith($repositoryRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Preservation storage must be outside the source repository: $path"
        }
        [IO.Directory]::CreateDirectory($path) | Out-Null
        $probe = Join-Path $path (".write-test-" + [Guid]::NewGuid().ToString("N"))
        $stream = [IO.File]::Open($probe, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $stream.Dispose()
        [IO.File]::Delete($probe)
        $probe = $null
        $env:FF7EC_PRESERVATION_ROOT = $path
        return $path
    }
    catch [IO.IOException], [UnauthorizedAccessException] {
        if ($explicitRoot) { throw }
        Write-Warning "Cannot write to preservation directory '$candidate': $($_.Exception.Message). Trying the next location."
    }
    finally {
        if ($probe -and [IO.File]::Exists($probe)) { [IO.File]::Delete($probe) }
    }
}
throw "No writable preservation directory found; set FF7EC_PRESERVATION_ROOT to a writable folder."
