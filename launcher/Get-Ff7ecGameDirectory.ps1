<#
.SYNOPSIS
    Resolves an explicit FF7EC installation or discovers it in Steam libraries.
#>
param([string]$GameDirectory = $env:FF7EC_GAME_DIRECTORY)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($GameDirectory)) { $GameDirectory = $env:FF7EC_GAME_DIRECTORY }
if (-not [string]::IsNullOrWhiteSpace($GameDirectory)) {
    $path = (Resolve-Path -LiteralPath $GameDirectory).ProviderPath
    if (-not (Test-Path -LiteralPath (Join-Path $path "octo") -PathType Container)) {
        throw "FF7EC installation has no Octo content directory: $path"
    }
    return $path
}

$steam = Get-ItemProperty -LiteralPath "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue
$steamRoot = if ($steam -and $steam.SteamPath) { $steam.SteamPath.Replace('/', '\') } else {
    Join-Path ${env:ProgramFiles(x86)} "Steam"
}
$libraries = @($steamRoot)
$libraryFile = Join-Path $steamRoot "steamapps\libraryfolders.vdf"
if (Test-Path -LiteralPath $libraryFile -PathType Leaf) {
    $libraries += [regex]::Matches((Get-Content -LiteralPath $libraryFile -Raw), '"path"\s*"([^"]+)"') |
        ForEach-Object { $_.Groups[1].Value.Replace('\\', '\') }
}
$path = $libraries | ForEach-Object { Join-Path $_ "steamapps\common\FF7EC" } |
    Where-Object { Test-Path -LiteralPath (Join-Path $_ "octo") -PathType Container } | Select-Object -First 1
if (-not $path) {
    throw "FF7EC installation not found in Steam libraries. Set FF7EC_GAME_DIRECTORY or pass -GameDirectory."
}
return [IO.Path]::GetFullPath($path)
