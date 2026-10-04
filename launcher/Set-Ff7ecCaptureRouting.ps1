<#
.SYNOPSIS
    Adds or removes a dedicated hosts-file block for live FF7EC capture.
    Run in an elevated PowerShell window; this does not start the replay server.
#>
param(
    [Parameter(Mandatory)]
    [ValidateSet("Enable", "Disable")]
    [string]$Action
)

$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell window."
}

$hostsPath = Join-Path $env:SystemRoot "System32\drivers\etc\hosts"
$begin = "# FF7EC-Live-Capture BEGIN"
$end = "# FF7EC-Live-Capture END"
$text = [System.IO.File]::ReadAllText($hostsPath)
$newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }

if ($Action -eq "Enable") {
    if ($text.Contains($begin) -or $text.Contains($end)) {
        throw "Capture hosts block already exists. Disable it before enabling again."
    }
    if ($text.Contains("# FF7EC-Offline-Server")) {
        throw "Offline routing is still active. Run Stop-Ff7ecOffline.ps1 before enabling live capture."
    }
    $configPath = Join-Path $PSScriptRoot "..\src\Ff7ec.Server\appsettings.json"
    $hosts = (Get-Content $configPath -Raw | ConvertFrom-Json).Ff7ec.Hostnames
    foreach ($hostname in $hosts) {
        if ($text -match "(?m)^\s*(?:127\.0\.0\.1|::1)\s+[^\r\n#]*\b$([regex]::Escape($hostname))\b") {
            throw "Existing hosts entry for $hostname detected. Remove it before enabling live capture."
        }
    }
    $lines = @($begin) + @($hosts | ForEach-Object { "127.0.0.1 $_" }) + @($end)
    $block = ($lines -join $newline) + $newline
    $backupDir = Join-Path (& (Join-Path $PSScriptRoot "Get-Ff7ecPreservationRoot.ps1")) "captures"
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    $backup = Join-Path $backupDir ("hosts-before-live-capture-{0}.bak" -f (Get-Date -Format "yyyyMMdd_HHmmss"))
    Copy-Item $hostsPath $backup -ErrorAction Stop
    $separator = if ($text.Length -gt 0 -and -not $text.EndsWith("`n")) { $newline } else { "" }
    [System.IO.File]::AppendAllText($hostsPath, $separator + $block)
    Write-Host "Live capture routing enabled for $($hosts.Count) hosts. Original hosts file: $backup"
} else {
    $start = $text.IndexOf($begin, [StringComparison]::Ordinal)
    $finish = $text.IndexOf($end, [StringComparison]::Ordinal)
    if ($start -lt 0 -or $finish -lt $start) {
        throw "Capture hosts block not found or malformed. No hosts-file changes made."
    }
    $blockEnd = $finish + $end.Length
    if ($text.Substring($blockEnd).StartsWith($newline)) {
        $blockEnd += $newline.Length
    }
    $block = $text.Substring($start, $blockEnd - $start)
    $configPath = Join-Path $PSScriptRoot "..\src\Ff7ec.Server\appsettings.json"
    $hosts = (Get-Content $configPath -Raw | ConvertFrom-Json).Ff7ec.Hostnames
    $expected = (@($begin) + @($hosts | ForEach-Object { "127.0.0.1 $_" }) + @($end)) -join $newline
    if ($block.TrimEnd("`r", "`n") -cne $expected) {
        throw "Capture hosts block changed unexpectedly. No hosts-file changes made."
    }
    [System.IO.File]::WriteAllText($hostsPath, $text.Remove($start, $blockEnd - $start))
    Write-Host "Live capture routing disabled."
}

ipconfig.exe /flushdns | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Hosts file updated, but DNS cache flush failed; run ipconfig.exe /flushdns before launching the game."
}
