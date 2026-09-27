<#
.SYNOPSIS
    Verifies and imports the most recent private live capture.
    Run after exiting FF7EC, stopping mitmdump and disabling capture routing.
#>
param(
    [string]$CaptureRoot = "E:\FF7EC_Preservation\captures",
    [string]$SessionDirectory
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$hostsFile = Join-Path $env:SystemRoot "System32\drivers\etc\hosts"
if ((Get-Content $hostsFile) -contains "# FF7EC-Live-Capture BEGIN") {
    throw "Disable live-capture routing first with Set-Ff7ecCaptureRouting.ps1 -Action Disable."
}
if (Get-Process FF7EC -ErrorAction SilentlyContinue) { throw "Exit FF7EC before importing." }
if (Get-NetTCPConnection -LocalPort 443 -State Listen -ErrorAction SilentlyContinue) {
    throw "Port 443 is still in use. Stop mitmdump (and any offline server) before importing."
}
if (-not (Get-Command python -ErrorAction SilentlyContinue)) { throw "Python is not installed or not on PATH." }

if (-not $SessionDirectory) {
    $SessionDirectory = Get-ChildItem $CaptureRoot -Directory -ErrorAction Stop |
        Where-Object { Test-Path (Join-Path $_.FullName "capture-session.json") } |
        Sort-Object Name -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $SessionDirectory) { throw "No capture sessions found under $CaptureRoot." }
$session = (Resolve-Path $SessionDirectory -ErrorAction Stop).Path
$manifest = Join-Path $session "capture-session.json"
if (-not (Test-Path $manifest -PathType Leaf)) { throw "Capture session manifest missing: $manifest" }
$info = Get-Content $manifest -Raw | ConvertFrom-Json
$raw = Join-Path $session "capture.mitm"
$stage = Join-Path $session "staged-replay"
if ($info.rawCapture -ne $raw -or $info.stagedReplay -ne $stage) {
    throw "Capture session paths do not match the manifest: $session"
}
if (-not (Test-Path $raw -PathType Leaf) -or (Get-Item $raw).Length -eq 0) {
    throw "Raw capture missing or empty: $raw"
}
$title = @(Get-ChildItem $stage -Recurse -Filter "POST_api_pvt_user_title_*.meta.json" -File -ErrorAction Stop |
    Where-Object {
        $candidate = Get-Content $_.FullName -Raw | ConvertFrom-Json
        $candidate.host -eq "game-q74z3cyn.app.gl.ffviiec.com" -and
        $candidate.method -eq "POST" -and
        $candidate.pathAndQuery.Split("?")[0] -eq "/api/pvt/user/title"
    })
if ($title.Count -ne 1) { throw "Expected exactly one staged account snapshot, found $($title.Count)." }
$meta = Get-Content $title.FullName -Raw | ConvertFrom-Json
$captured = [DateTimeOffset]::FromUnixTimeMilliseconds([long]($meta.capturedAt * 1000))
$started = [DateTimeOffset]::Parse($info.startedAtUtc)
$body = $title.FullName -replace '\.meta\.json$', '.body.bin'
if ($captured -lt $started -or $meta.statusCode -ne 200 -or
    -not (Test-Path $body -PathType Leaf) -or (Get-Item $body).Length -eq 0) {
    throw "Staged account snapshot is stale, unsuccessful, or missing its body."
}

$importer = Join-Path $root "tools\export_capture_store.py"
Write-Host "Checking raw capture in $session (started $started)..."
& python $importer $raw --check-only --require-user-title
if ($LASTEXITCODE -ne 0) { throw "Raw capture lacks a successful account snapshot. Active replay store unchanged." }
Write-Host "Fresh account snapshot: $captured. Importing to $root\captures ..."
& python $importer $raw --require-user-title --out (Join-Path $root "captures")
if ($LASTEXITCODE -ne 0) { throw "Import failed. Check the active replay store before using it." }

$imported = Join-Path (Join-Path $root "captures") $title.FullName.Substring($stage.Length + 1)
if (-not (Test-Path $imported) -or
    (Get-FileHash $imported -Algorithm SHA256).Hash -ne (Get-FileHash $title.FullName -Algorithm SHA256).Hash) {
    throw "Imported account snapshot does not match the staged capture."
}
Write-Host "Imported fresh account data. Restart the offline server before validating in the game."
