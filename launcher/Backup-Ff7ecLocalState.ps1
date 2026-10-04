<#
.SYNOPSIS
    Moves optional offline party and story overlays aside to reveal fresh capture state.
#>
param([string]$BackupRoot)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$files = @(
    (Join-Path $root "data\party-settings.json")
    (Join-Path $root "data\story-state.json")
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
if (-not $files) {
    Write-Host "No local party or story overlays to move."
    return
}
if (-not $BackupRoot) {
    $BackupRoot = Join-Path (& (Join-Path $PSScriptRoot "Get-Ff7ecPreservationRoot.ps1")) "state-backups"
}
$backup = Join-Path $BackupRoot (Get-Date -Format "yyyyMMdd_HHmmss")
if (Test-Path -LiteralPath $backup) { throw "State backup destination already exists: $backup" }
New-Item -ItemType Directory -Path $backup -Force | Out-Null
foreach ($file in $files) {
    Move-Item -LiteralPath $file -Destination $backup -ErrorAction Stop
}
Write-Host "Local overlays moved to $backup. Asset overrides were not changed."
