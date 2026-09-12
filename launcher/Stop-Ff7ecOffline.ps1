<#
.SYNOPSIS
    Stops FF7EC offline mode: removes the hosts file redirect (single UAC prompt) so this
    machine can reach the real game servers again. Leaves the CA installed and does not
    touch the replay server process - close its window manually when done.
#>

$ErrorActionPreference = "Stop"
Write-Host "=== FF7EC Offline Server teardown ===" -ForegroundColor Cyan

$logPath = Join-Path $env:TEMP "ff7ec-elevated-teardown.log"
$scriptPath = Join-Path $PSScriptRoot "elevated-teardown.ps1"

Write-Host "Requesting elevation to remove hosts redirect..."
Start-Process powershell -Verb RunAs -ArgumentList @(
    "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $scriptPath,
    "-LogPath", $logPath
) -Wait

if (Test-Path $logPath) {
    Get-Content $logPath | ForEach-Object { Write-Host "  $_" }
} else {
    Write-Warning "No log produced - elevation may have been cancelled."
}

Write-Host "Done. Remember to also close the replay server window if it's still running." -ForegroundColor Green
