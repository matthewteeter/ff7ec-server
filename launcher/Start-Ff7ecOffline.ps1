<#
.SYNOPSIS
    One-click FF7EC offline mode: starts the replay server (so it generates/loads its
    certs), installs the CA + hosts redirects with a single UAC prompt, then optionally
    launches the game via Steam.

.PARAMETER LaunchGame
    Also start FF7EC through Steam once offline mode is active.
#>
param(
    [switch]$LaunchGame
)

$ErrorActionPreference = "Stop"
$root = "E:\FF7EC-Server"
$serverProject = Join-Path $root "src\Ff7ec.Server"
$appsettings = Get-Content (Join-Path $serverProject "appsettings.json") -Raw | ConvertFrom-Json
$certDir = $appsettings.Ff7ec.CertDirectory
$hostnames = $appsettings.Ff7ec.Hostnames
$caCerPath = Join-Path $certDir "ff7ec-offline-ca.cer"

Write-Host "=== FF7EC Offline Server launcher ===" -ForegroundColor Cyan

# 1. Start the replay server in its own visible window (so REPLAY/GAP log lines are
#    visible while you play), which also generates the CA/leaf certs on first run.
Write-Host "Starting replay server..."
$serverProc = Start-Process powershell -ArgumentList @(
    "-NoExit", "-Command",
    "Set-Location '$serverProject'; dotnet run --no-launch-profile"
) -PassThru

# 2. Wait for the CA cert to exist (server generates it during startup, before binding Kestrel).
Write-Host "Waiting for server certificate..." -NoNewline
$waited = 0
while (-not (Test-Path $caCerPath) -and $waited -lt 60) {
    Start-Sleep -Seconds 1
    $waited++
    Write-Host "." -NoNewline
}
Write-Host ""
if (-not (Test-Path $caCerPath)) {
    Write-Error "Timed out waiting for $caCerPath - check the server window for errors."
    exit 1
}

# 3. Elevated setup: install CA + hosts redirect, single UAC prompt.
Write-Host "Requesting elevation to install CA cert + hosts redirect (one UAC prompt)..."
$logPath = Join-Path $env:TEMP "ff7ec-elevated-setup.log"
$scriptPath = Join-Path $PSScriptRoot "elevated-setup.ps1"
$hostArg = ($hostnames -join ",")
Start-Process powershell -Verb RunAs -ArgumentList @(
    "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $scriptPath,
    "-CaCerPath", $caCerPath,
    "-HostnamesCsv", $hostArg,
    "-LogPath", $logPath
) -Wait

if (Test-Path $logPath) {
    $result = Get-Content $logPath
    $result | ForEach-Object { Write-Host "  $_" }
    if ($result -notcontains "SUCCESS") {
        Write-Warning "Elevated setup did not report success - check the log above."
    }
} else {
    Write-Warning "No log produced - elevation may have been cancelled."
}

Write-Host ""
Write-Host "FF7EC offline mode is active. Server window PID: $($serverProc.Id)" -ForegroundColor Green
Write-Host "Run Stop-Ff7ecOffline.ps1 when you're done to remove the hosts redirect."

if ($LaunchGame) {
    Write-Host "Launching FF7 Ever Crisis via Steam..."
    Start-Process "steam://rungameid/2484110"
}
