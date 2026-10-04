<#
.SYNOPSIS
    Applies, restores, or reports optional local FF7EC override packages.

.DESCRIPTION
    Apply discovers packages and restores removed packages before applying installed ones.
    Recovery state is kept separately, so Restore needs neither packages nor Steam lookup.

.PARAMETER AssetName
    Select one installed or tracked asset. By default, all overrides are managed together.
#>
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet("Apply", "Restore", "Status")]
    [string]$Action,

    [string]$ConfigPath,

    [string]$PackageRoot,

    [string]$AssetName
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "tools\Ff7ec.AssetOverride\Ff7ec.AssetOverride.csproj"

function Invoke-OverrideTool([string[]]$ToolArguments) {
    & dotnet run --project $project -c Release -- @ToolArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Asset override $Action failed with exit code $LASTEXITCODE."
    }
}

$needsGame = $false
$toolArgs = @($Action.ToLowerInvariant())
if ($ConfigPath) {
    if ($PackageRoot) { throw "ConfigPath and PackageRoot cannot be combined." }
    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
        throw "Asset override configuration not found: $ConfigPath"
    }
    $configText = Get-Content -LiteralPath $ConfigPath -Raw
    if ($configText.IndexOf("%FF7EC_PRESERVATION_ROOT%", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        & (Join-Path $PSScriptRoot "Get-Ff7ecPreservationRoot.ps1") | Out-Null
    }
    $needsGame = $configText.IndexOf("%FF7EC_GAME_DIRECTORY%", [StringComparison]::OrdinalIgnoreCase) -ge 0
    $toolArgs += @("--config", $ConfigPath)
} else {
    $serverProject = Join-Path $root "src\Ff7ec.Server"
    $settings = (Get-Content -LiteralPath (Join-Path $serverProject "appsettings.json") -Raw | ConvertFrom-Json).Ff7ec
    $stateDirectory = $settings.AssetOverride.StateDirectory
    if (-not $stateDirectory) { $stateDirectory = Join-Path $settings.DataDirectory "asset-overrides" }
    $stateRoot = [IO.Path]::GetFullPath([IO.Path]::Combine($serverProject, $stateDirectory))
    $toolArgs += @("--state-root", $stateRoot)
    if ($Action -ne "Restore") {
        if (-not $PackageRoot) {
            $PackageRoot = Join-Path (& (Join-Path $PSScriptRoot "Get-Ff7ecPreservationRoot.ps1")) "asset-overrides"
        }
        $PackageRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PackageRoot)
        if ($PackageRoot.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
            $PackageRoot.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Override packages must be stored outside the source repository: $PackageRoot"
        }
        $toolArgs += @("--packages", $PackageRoot)
    }
}
if ($AssetName) { $toolArgs += @("--asset", $AssetName) }
if (-not $ConfigPath -and $Action -eq "Apply") {
    $scan = @(Invoke-OverrideTool (@("status") + $toolArgs[1..($toolArgs.Count - 1)]))
    $needsGame = @($scan | Where-Object { $_ -like "INSTALLED:*" }).Count -gt 0
}
if ($needsGame -and -not $env:FF7EC_GAME_DIRECTORY) {
    $steam = Get-ItemProperty -LiteralPath "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue
    $steamRoot = if ($steam -and $steam.SteamPath) { $steam.SteamPath } else {
        Join-Path ${env:ProgramFiles(x86)} "Steam"
    }
    $libraries = @($steamRoot)
    $libraryFile = Join-Path $steamRoot "steamapps\libraryfolders.vdf"
    if (Test-Path -LiteralPath $libraryFile -PathType Leaf) {
        $libraries += [regex]::Matches((Get-Content -LiteralPath $libraryFile -Raw), '"path"\s*"([^"]+)"') |
            ForEach-Object { $_.Groups[1].Value.Replace('\\', '\') }
    }
    $env:FF7EC_GAME_DIRECTORY = $libraries | ForEach-Object {
        Join-Path $_ "steamapps\common\FF7EC"
    } | Where-Object { Test-Path -LiteralPath (Join-Path $_ "octo") -PathType Container } | Select-Object -First 1
    if (-not $env:FF7EC_GAME_DIRECTORY) {
        throw "FF7EC installation not found in Steam libraries. Set FF7EC_GAME_DIRECTORY to the game folder containing 'octo'."
    }
}
Invoke-OverrideTool $toolArgs
