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

& dotnet run --project $project -c Release -- $Action.ToLowerInvariant() --config $ConfigPath
if ($LASTEXITCODE -ne 0) {
    throw "Asset override $Action failed with exit code $LASTEXITCODE."
}
