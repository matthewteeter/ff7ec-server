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
$root = Split-Path -Parent $PSScriptRoot
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

# 2. Wait for the server to bind. Checking only for the certificate is insufficient once
#    it already exists: dotnet may fail to start while the launcher still redirects all
#    game traffic to an empty port.
Write-Host "Waiting for replay server on port $($appsettings.Ff7ec.ListenPort)..." -NoNewline
$serverReady = $false
for ($waited = 0; $waited -lt 60; $waited++) {
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $connect = $client.BeginConnect("127.0.0.1", $appsettings.Ff7ec.ListenPort, $null, $null)
        if ($connect.AsyncWaitHandle.WaitOne(500) -and $client.Connected) {
            $client.EndConnect($connect)
            $serverReady = $true
            break
        }
    } catch {
        # The server is still starting; retry below.
    } finally {
        $client.Dispose()
    }
    Start-Sleep -Milliseconds 500
    Write-Host "." -NoNewline
}
Write-Host ""
if (-not $serverReady -or -not (Test-Path $caCerPath)) {
    throw "Replay server did not become ready - check the server window for startup errors."
}

# 3. Elevated setup: install CA + hosts redirect, single UAC prompt.
Write-Host "Requesting elevation to install CA cert + hosts redirect (one UAC prompt)..."
$logPath = Join-Path $env:TEMP "ff7ec-elevated-setup.log"
$scriptPath = Join-Path $PSScriptRoot "elevated-setup.ps1"
$hostArg = ($hostnames -join ",")
Remove-Item $logPath -Force -ErrorAction SilentlyContinue
Start-Process powershell -Verb RunAs -ArgumentList @(
    "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $scriptPath,
    "-CaCerPath", $caCerPath,
    "-HostnamesCsv", $hostArg,
    "-LogPath", $logPath
) -Wait

if (-not (Test-Path $logPath)) {
    throw "Elevated setup produced no log; elevation may have been cancelled."
}
$result = Get-Content $logPath
$result | ForEach-Object { Write-Host "  $_" }
if ($result -notcontains "SUCCESS") {
    throw "Elevated setup failed - check the log above."
}

$hostsPath = "$env:SystemRoot\System32\drivers\etc\hosts"
$hostsLines = Get-Content $hostsPath
$missingHosts = @($hostnames | Where-Object { $hostsLines -notcontains "127.0.0.1 $_" })
if ($missingHosts.Count -gt 0) {
    throw "Hosts redirect verification failed for: $($missingHosts -join ', ')"
}

Write-Host ""
Write-Host "FF7EC offline mode is active. Server window PID: $($serverProc.Id)" -ForegroundColor Green
Write-Host "Run Stop-Ff7ecOffline.ps1 when you're done to remove the hosts redirect."

if ($LaunchGame) {
    Write-Host "Launching FF7 Ever Crisis via Steam..."
    Start-Process "steam://rungameid/2484110"
}
