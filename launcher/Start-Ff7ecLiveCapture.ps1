<#
.SYNOPSIS
    Creates a fresh, private capture session and runs mitmdump in this window.
    Keep this window open until you exit the game, then press Ctrl+C.
#>
param(
    [string]$CaptureRoot
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$addon = Join-Path $root "tools\mitm_ff7ec_addon.py"
$ca = Join-Path $HOME ".mitmproxy\mitmproxy-ca-cert.cer"
$hostsFile = Join-Path $env:SystemRoot "System32\drivers\etc\hosts"

if (-not (Test-Path -LiteralPath $addon -PathType Leaf)) { throw "Capture addon not found: $addon" }
if (-not (Test-Path -LiteralPath $ca -PathType Leaf)) { throw "mitmproxy CA not found: $ca" }
if (-not (Get-Command mitmdump -ErrorAction SilentlyContinue)) { throw "mitmdump is not installed or not on PATH." }
if (Get-Process FF7EC -ErrorAction SilentlyContinue) { throw "Close FF7EC before starting the capture." }
if ((Get-Content -LiteralPath $hostsFile) -contains "# FF7EC-Offline-Server") {
    throw "Offline routing is enabled. Run Stop-Ff7ecOffline.ps1 before capturing."
}
if ((Get-Content -LiteralPath $hostsFile) -contains "# FF7EC-Live-Capture BEGIN") {
    throw "Live-capture routing is already enabled. Disable it before starting a new session."
}
if (Get-NetTCPConnection -LocalPort 443 -State Listen -ErrorAction SilentlyContinue) {
    throw "Port 443 is in use. Close the offline server or an earlier mitmdump before capturing."
}

$started = [DateTimeOffset]::UtcNow
if (-not $CaptureRoot) {
    $CaptureRoot = Join-Path (& (Join-Path $PSScriptRoot "Get-Ff7ecPreservationRoot.ps1")) "captures"
}
$CaptureRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($CaptureRoot)
$session = Join-Path $CaptureRoot (Get-Date -Format "yyyyMMdd_HHmmss")
if (Test-Path -LiteralPath $session) { throw "Capture session already exists: $session. Retry in a second." }
$replayStore = Join-Path $root "captures"
if (-not (Test-Path -LiteralPath $replayStore -PathType Container)) { throw "Replay store not found: $replayStore" }
New-Item -ItemType Directory -Path $session -Force | Out-Null
Copy-Item -LiteralPath $replayStore -Destination (Join-Path $session "replay-store-before") -Recurse -ErrorAction Stop
$raw = Join-Path $session "capture.mitm"
$stage = Join-Path $session "staged-replay"
$manifest = Join-Path $session "capture-session.json"
@{
    startedAtUtc = $started.ToString("o")
    rawCapture = $raw
    stagedReplay = $stage
} | ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding UTF8

$priorOut = [Environment]::GetEnvironmentVariable("FF7EC_CAPTURE_STORE_OUT", "Process")
try {
    $env:FF7EC_CAPTURE_STORE_OUT = $stage
    Write-Host "Session: $session"
    Write-Host "Raw traffic: $raw"
    Write-Host "Staged responses: $stage"
    Write-Host "Start live-capture routing and launch the game in another window."
    Write-Host "After exiting FF7EC, press Ctrl+C here. Then disable routing and run Finish-Ff7ecLiveCapture.ps1."
    & mitmdump --mode "reverse:https://game-q74z3cyn.app.gl.ffviiec.com@443" `
        --set keep_host_header=true --ssl-insecure --scripts $addon --save-stream-file $raw
    if ($LASTEXITCODE -ne 0) { throw "mitmdump exited with code $LASTEXITCODE. Inspect its output before importing." }
}
finally {
    [Environment]::SetEnvironmentVariable("FF7EC_CAPTURE_STORE_OUT", $priorOut, "Process")
}
