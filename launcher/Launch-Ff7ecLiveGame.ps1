<#
.SYNOPSIS
    Launches FF7EC through Steam with live-capture routing or the Frida fallback.
#>
param([switch]$UseFrida)

$ErrorActionPreference = "Stop"
$hostsFile = Join-Path $env:SystemRoot "System32\drivers\etc\hosts"
if (-not $UseFrida -and -not ((Get-Content $hostsFile) -contains "# FF7EC-Live-Capture BEGIN")) {
    throw "Enable live-capture routing before launching FF7EC."
}
if ($UseFrida -and ((Get-Content $hostsFile) -contains "# FF7EC-Live-Capture BEGIN")) {
    throw "Live-capture routing is already enabled; omit -UseFrida."
}
if (Get-Process FF7EC -ErrorAction SilentlyContinue) {
    throw "FF7EC is already running; close it before capturing a fresh boot."
}
$listener = Get-NetTCPConnection -LocalPort 443 -State Listen -ErrorAction SilentlyContinue
if (-not $listener) { throw "Nothing is listening on port 443. Start Start-Ff7ecLiveCapture.ps1 first." }
if ($UseFrida) {
    $hook = "E:\FF7EC_Preservation\work\redirect_dns.js"
    if (-not (Test-Path $hook -PathType Leaf)) { throw "Frida redirect hook not found: $hook" }
    if (-not (Get-Command frida -ErrorAction SilentlyContinue)) { throw "frida is not installed or not on PATH." }
}
Start-Process "steam://rungameid/2484110"
if ($UseFrida) {
    for ($attempt = 0; $attempt -lt 1200; $attempt++) {
        $game = Get-Process FF7EC -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($game) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $game) { throw "FF7EC did not appear within two minutes; check Steam." }
    Write-Host "Attaching Frida to FF7EC process $($game.Id). Keep this window open until the game exits."
    & frida -p $game.Id -l $hook
    if ($LASTEXITCODE -ne 0) { throw "Frida exited with code $LASTEXITCODE; account traffic may not have been captured." }
} else {
    Write-Host "Launched FF7EC via Steam. Wait for the account snapshot to appear in the mitmdump window."
}
