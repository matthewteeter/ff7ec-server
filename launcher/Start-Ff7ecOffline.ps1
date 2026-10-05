<#
.SYNOPSIS
    One-click FF7EC offline mode: starts the replay server (so it generates/loads its
    certs), installs the CA + hosts redirects with a single UAC prompt, then optionally
    launches the game via Steam.

.PARAMETER LaunchGame
    Also start FF7EC through Steam once offline mode is active.

.PARAMETER SkipAssetOverrides
    Restore any tracked overrides and start offline mode using original assets.

.PARAMETER AccountJsonPath
    Use a private exported account JSON file instead of captured account responses.

.PARAMETER ProtocolAssemblyPath
    Optional developer override using the matching client's DummyDll\Command.Domain.dll.

.PARAMETER ProtocolSchemaPath
    Optional override for the bundled versioned protocol-schema JSON file.

.PARAMETER Standalone
    Use exported account JSON and installed content without loading any captures.

.PARAMETER GameDirectory
    FF7EC installation for standalone mode. Otherwise discover it in Steam libraries.

.PARAMETER MasterDataBackupDirectory
    Read-only Steam content backup containing MasterData and LocalizeText directories.
#>
param(
    [switch]$LaunchGame,
    [switch]$SkipAssetOverrides,
    [string]$AccountJsonPath,
    [string]$ProtocolAssemblyPath,
    [string]$ProtocolSchemaPath,
    [switch]$Standalone,
    [string]$GameDirectory,
    [string]$MasterDataBackupDirectory
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$serverProject = Join-Path $root "src\Ff7ec.Server"
$appsettings = Get-Content -LiteralPath (Join-Path $serverProject "appsettings.json") -Raw | ConvertFrom-Json
$certDir = [IO.Path]::GetFullPath([IO.Path]::Combine($serverProject, $appsettings.Ff7ec.CertDirectory))
$hostnames = $appsettings.Ff7ec.Hostnames
$caCerPath = Join-Path $certDir "ff7ec-offline-ca.cer"
if ($Standalone -and [string]::IsNullOrWhiteSpace($AccountJsonPath)) {
    throw "Standalone mode requires -AccountJsonPath."
}
if (-not $Standalone -and -not [string]::IsNullOrWhiteSpace($GameDirectory)) {
    throw "-GameDirectory requires -Standalone."
}
if (-not $Standalone -and -not [string]::IsNullOrWhiteSpace($MasterDataBackupDirectory)) {
    throw "-MasterDataBackupDirectory requires -Standalone."
}
if (-not [string]::IsNullOrWhiteSpace($MasterDataBackupDirectory)) {
    $MasterDataBackupDirectory = (Resolve-Path -LiteralPath $MasterDataBackupDirectory).ProviderPath
    if (-not (Test-Path -LiteralPath (Join-Path $MasterDataBackupDirectory "MasterData\master_catalog.json") -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $MasterDataBackupDirectory "LocalizeText") -PathType Container)) {
        throw "-MasterDataBackupDirectory must contain MasterData\master_catalog.json and LocalizeText."
    }
}
if ([string]::IsNullOrWhiteSpace($AccountJsonPath) -and
    (-not [string]::IsNullOrWhiteSpace($ProtocolAssemblyPath) -or -not [string]::IsNullOrWhiteSpace($ProtocolSchemaPath))) {
    throw "Protocol overrides require -AccountJsonPath."
}
if (-not [string]::IsNullOrWhiteSpace($ProtocolAssemblyPath) -and -not [string]::IsNullOrWhiteSpace($ProtocolSchemaPath)) {
    throw "Choose either -ProtocolSchemaPath or -ProtocolAssemblyPath, not both."
}
if (-not [string]::IsNullOrWhiteSpace($AccountJsonPath)) {
    $AccountJsonPath = (Resolve-Path -LiteralPath $AccountJsonPath).ProviderPath
}
if (-not [string]::IsNullOrWhiteSpace($ProtocolAssemblyPath)) {
    $ProtocolAssemblyPath = (Resolve-Path -LiteralPath $ProtocolAssemblyPath).ProviderPath
}
if (-not [string]::IsNullOrWhiteSpace($ProtocolSchemaPath)) {
    $ProtocolSchemaPath = (Resolve-Path -LiteralPath $ProtocolSchemaPath).ProviderPath
}
if ($Standalone) {
    $GameDirectory = & (Join-Path $PSScriptRoot "Get-Ff7ecGameDirectory.ps1") -GameDirectory $GameDirectory
    if (-not (Test-Path -LiteralPath (Join-Path $GameDirectory "FF7EC_Data\StreamingAssets\MasterData\index.json") -PathType Leaf)) {
        throw "Standalone mode requires installed masterdata: $GameDirectory"
    }
    $standaloneStateRoot = Join-Path (& (Join-Path $PSScriptRoot "Get-Ff7ecPreservationRoot.ps1")) "single-player"
}

Write-Host "=== FF7EC Offline Server launcher ===" -ForegroundColor Cyan

$overrideManager = Join-Path $PSScriptRoot "Set-Ff7ecAssetOverride.ps1"
$newlyAppliedOverrides = [System.Collections.Generic.List[string]]::new()
try {
    if ($Standalone) {
        Write-Host "Standalone single-player: captures and launcher-managed asset overrides are disabled."
    } elseif (-not $SkipAssetOverrides) {
        $status = @(& $overrideManager Status)
        $previouslyApplied = @($status | Where-Object { $_ -like "APPLIED:*" } |
            ForEach-Object { $_.Substring("APPLIED: ".Length) })
        $status | ForEach-Object { Write-Host "  $_" }
        & $overrideManager Apply | ForEach-Object {
            Write-Host $_
            if ($_ -cmatch "^(?:Applied override|Applied server-only override|Asset override is already applied): (.+)$") {
                $asset = $Matches[1]
                if ($previouslyApplied -cnotcontains $asset -and -not $newlyAppliedOverrides.Contains($asset)) {
                    $newlyAppliedOverrides.Add($asset)
                }
            }
        }
    } else {
        & $overrideManager Restore
    }

# 1. Start the replay server in its own visible window (so REPLAY/GAP log lines are
#    visible while you play), which also generates the CA/leaf certs on first run.
if ($Standalone) { Write-Host "Starting standalone single-player server..." }
else { Write-Host "Starting replay server..." }
if (Get-NetTCPConnection -LocalPort $appsettings.Ff7ec.ListenPort -State Listen -ErrorAction SilentlyContinue) {
    throw "Port $($appsettings.Ff7ec.ListenPort) is already in use. Stop the existing server before starting this mode."
}
$serverCommand = "Set-Location -LiteralPath '$($serverProject.Replace("'", "''"))'; dotnet run --no-launch-profile"
if ($Standalone) {
    $serverCommand = "`$env:Ff7ec__Standalone__Enabled = 'true'; " +
        "`$env:Ff7ec__Standalone__GameDirectory = '$($GameDirectory.Replace("'", "''"))'; " +
        "`$env:Ff7ec__Standalone__MasterDataBackupDirectory = '$($MasterDataBackupDirectory.Replace("'", "''"))'; " +
        "`$env:Ff7ec__DataDirectory = '$($standaloneStateRoot.Replace("'", "''"))'; " +
        "`$env:Ff7ec__GapsDirectory = '$((Join-Path $standaloneStateRoot 'gaps').Replace("'", "''"))'; " + $serverCommand
} else {
    $serverCommand = "`$env:Ff7ec__Standalone__Enabled = 'false'; " + $serverCommand
}
if (-not [string]::IsNullOrWhiteSpace($AccountJsonPath)) {
    $serverCommand = "`$env:Ff7ec__AccountExport__JsonPath = '$($AccountJsonPath.Replace("'", "''"))'; " + $serverCommand
}
if (-not [string]::IsNullOrWhiteSpace($ProtocolAssemblyPath)) {
    $serverCommand = "`$env:Ff7ec__AccountExport__ProtocolAssemblyPath = '$($ProtocolAssemblyPath.Replace("'", "''"))'; " + $serverCommand
}
if (-not [string]::IsNullOrWhiteSpace($ProtocolSchemaPath)) {
    $serverCommand = "`$env:Ff7ec__AccountExport__ProtocolSchemaPath = '$($ProtocolSchemaPath.Replace("'", "''"))'; " + $serverCommand
}
$encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($serverCommand))
$serverProc = Start-Process powershell.exe -ArgumentList @(
    "-NoExit", "-EncodedCommand", $encodedCommand
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
if (-not $serverReady -or -not (Test-Path -LiteralPath $caCerPath)) {
    throw "Replay server did not become ready - check the server window for startup errors."
}

# 3. Elevated setup: install CA + hosts redirect, single UAC prompt.
Write-Host "Requesting elevation to install CA cert + hosts redirect (one UAC prompt)..."
$logPath = Join-Path $env:TEMP "ff7ec-elevated-setup.log"
$scriptPath = Join-Path $PSScriptRoot "elevated-setup.ps1"
$hostArg = ($hostnames -join ",")
Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue
Start-Process powershell.exe -Verb RunAs -ArgumentList @(
    "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$scriptPath`"",
    "-CaCerPath", "`"$caCerPath`"",
    "-HostnamesCsv", $hostArg,
    "-LogPath", "`"$logPath`""
) -Wait

if (-not (Test-Path -LiteralPath $logPath)) {
    throw "Elevated setup produced no log; elevation may have been cancelled."
}
$result = Get-Content -LiteralPath $logPath
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
}
catch {
    if ($newlyAppliedOverrides.Count -gt 0) {
        Write-Warning "Offline startup failed; restoring only the asset overrides applied by this launch."
        for ($i = $newlyAppliedOverrides.Count - 1; $i -ge 0; $i--) {
            try { & $overrideManager Restore -AssetName $newlyAppliedOverrides[$i] }
            catch { Write-Warning "Automatic asset restoration also failed: $_" }
        }
    }
    throw
}
