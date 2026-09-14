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
Remove-Item $logPath -Force -ErrorAction SilentlyContinue
Start-Process powershell -Verb RunAs -ArgumentList @(
    "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $scriptPath,
    "-LogPath", $logPath
) -Wait

if (-not (Test-Path $logPath)) {
    throw "Elevated teardown produced no log; elevation may have been cancelled."
}
$result = Get-Content $logPath
$result | ForEach-Object { Write-Host "  $_" }
if ($result -notcontains "SUCCESS") {
    throw "Elevated teardown failed - check the log above."
}

Write-Host "Done. Remember to also close the replay server window if it's still running." -ForegroundColor Green
