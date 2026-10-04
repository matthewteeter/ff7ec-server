<#
.SYNOPSIS
    Applies, restores, or reports the configured FF7EC local asset override.

.DESCRIPTION
    The game must be closed for Apply and Restore. Applying preserves the original Octo
    manifest and cache blob under data\asset-overrides, wraps the replacement in FF7EC's
    Octo asset encryption, and updates only that asset's local manifest metadata.
#>
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet("Apply", "Restore", "Status")]
    [string]$Action,

    [string]$ConfigPath = (Join-Path $PSScriptRoot "asset-overrides.json")
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "tools\Ff7ec.AssetOverride\Ff7ec.AssetOverride.csproj"

if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw "Asset override configuration not found: $ConfigPath"
}

$configText = Get-Content -LiteralPath $ConfigPath -Raw
if ($configText.IndexOf("%FF7EC_PRESERVATION_ROOT%", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    & (Join-Path $PSScriptRoot "Get-Ff7ecPreservationRoot.ps1") | Out-Null
}
if ($configText.IndexOf("%FF7EC_GAME_DIRECTORY%", [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
    -not $env:FF7EC_GAME_DIRECTORY) {
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
& dotnet run --project $project -c Release -- $Action.ToLowerInvariant() --config $ConfigPath
if ($LASTEXITCODE -ne 0) {
    throw "Asset override $Action failed with exit code $LASTEXITCODE."
}
