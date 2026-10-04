param(
    [string]$Repository,
    [string]$FixtureRoot,
    [string]$Tool
)
$ErrorActionPreference = "Stop"
$global:CheckDotnetExe = (Get-Command dotnet -CommandType Application).Source
$global:CheckToolDll = $Tool
$copy = Join-Path $FixtureRoot "repository"
$launcher = Join-Path $copy "launcher"
$server = Join-Path $copy "src\Ff7ec.Server"
New-Item -ItemType Directory -Path $launcher, $server -Force | Out-Null
foreach ($name in @("Set-Ff7ecAssetOverride.ps1", "Get-Ff7ecPreservationRoot.ps1", "Start-Ff7ecOffline.ps1", "Stop-Ff7ecOffline.ps1")) {
    Copy-Item -LiteralPath (Join-Path $Repository "launcher\$name") -Destination (Join-Path $launcher $name) -Force
}
$settings = @{
    Ff7ec = @{
        DataDirectory = (Join-Path $FixtureRoot "data")
        CertDirectory = (Join-Path $FixtureRoot "certs")
        ListenPort = 38443
        Hostnames = @("example.test")
        AssetOverride = @{ StateDirectory = (Join-Path $FixtureRoot "state") }
    }
}
$settings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $server "appsettings.json")
function dotnet {
    $arguments = @($args)
    if ($global:CheckNativeMode) {
        & $global:CheckDotnetExe @arguments
        $global:LASTEXITCODE = $LASTEXITCODE
        return
    }
    $first = [Array]::IndexOf($arguments, "Release") + 1
    if ($first -eq 0) { throw "Expected dotnet run build options." }
    if ($arguments[$first] -eq "--") { $first++ }
    & $global:CheckDotnetExe $global:CheckToolDll @($arguments[$first..($arguments.Count - 1)])
    $global:LASTEXITCODE = $LASTEXITCODE
}
function Get-ItemProperty { throw "Steam lookup must not occur without packages." }
function Start-Process { throw "simulated server startup failure" }

$manager = Join-Path $launcher "Set-Ff7ecAssetOverride.ps1"
$env:FF7EC_GAME_DIRECTORY = $null
$env:FF7EC_PRESERVATION_ROOT = Join-Path $FixtureRoot "private"
$output = @(& $manager Status)
if ($output -notcontains "No installed asset override packages.") { throw "Missing packages status failed." }
& $manager Apply | Out-Null
& $manager Restore | Out-Null
& $manager Apply -PackageRoot (Join-Path $FixtureRoot "missing [package] ' folder") | Out-Null
$env:FF7EC_PRESERVATION_ROOT = Join-Path $copy "invalid-source-root"
& $manager Restore | Out-Null
$env:FF7EC_PRESERVATION_ROOT = Join-Path $FixtureRoot "private"
$rejected = $false
try { & $manager Status -PackageRoot $copy | Out-Null }
catch { $rejected = $true }
if (-not $rejected) { throw "Repository-local package root accepted." }

foreach ($relative in @(
    "tools\Ff7ec.AssetOverride\Ff7ec.AssetOverride.csproj",
    "tools\Ff7ec.AssetOverride\Program.cs",
    "src\Ff7ec.Octo\Ff7ec.Octo.csproj",
    "src\Ff7ec.Octo\OctoCrypto.cs"
)) {
    $destination = Join-Path $copy $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $Repository $relative) -Destination $destination -Force
}
$global:CheckNativeMode = $true
& $manager Apply | Out-Null
$env:FF7EC_GAME_DIRECTORY = Join-Path $FixtureRoot "game"
& $manager Apply -PackageRoot (Join-Path $FixtureRoot "packages") | Out-Null
$applied = @(& $manager Status -PackageRoot (Join-Path $FixtureRoot "packages"))
if ($applied -notcontains "APPLIED: character/003/model/019.d") { throw "Native package apply failed." }
$source = Join-Path $FixtureRoot "packages\package-0\replacement.d"
$savedSource = [IO.File]::ReadAllBytes($source)
Remove-Item -LiteralPath $source
$env:FF7EC_GAME_DIRECTORY = $null
$rejected = $false
try { & $manager Apply -PackageRoot (Join-Path $FixtureRoot "packages") | Out-Null }
catch { $rejected = $true }
if (-not $rejected) { throw "Missing installed source accepted." }
& $manager Restore | Out-Null
[IO.File]::WriteAllBytes($source, $savedSource)
$global:CheckNativeMode = $false

$log = Join-Path $launcher "actions.log"
$mock = @'
param([string]$Action, [string]$AssetName)
$log = Join-Path $PSScriptRoot "actions.log"
Add-Content -LiteralPath $log -Value "$Action $AssetName"
if ($Action -eq "Status") {
    "APPLIED: existing"
    if ((Get-Content -LiteralPath $log) -contains "Apply ") { "APPLIED: new" }
}
if ($Action -eq "Apply") { "Applied override: new" }
'@
$mock | Set-Content -LiteralPath $manager
$start = Join-Path $launcher "Start-Ff7ecOffline.ps1"
try { & $start | Out-Null; throw "Startup failure not surfaced." }
catch {
    if ($_ -notmatch "simulated server startup failure") { throw }
}
$actions = @(Get-Content -LiteralPath $log)
if ($actions -notcontains "Restore new" -or $actions -contains "Restore existing") {
    throw "Failed startup did not restore only newly applied overrides."
}
Remove-Item -LiteralPath $log
try { & $start -SkipAssetOverrides | Out-Null; throw "Startup failure not surfaced." }
catch {
    if ($_ -notmatch "simulated server startup failure") { throw }
}
if (@(Get-Content -LiteralPath $log) -contains "Apply " -or
    @(Get-Content -LiteralPath $log) -notcontains "Restore ") {
    throw "SkipAssetOverrides did not restore active overrides."
}
Remove-Item -LiteralPath $log
try { & (Join-Path $launcher "Stop-Ff7ecOffline.ps1") | Out-Null }
catch {
    if ($_ -notmatch "simulated server startup failure") { throw }
}
if (@(Get-Content -LiteralPath $log) -notcontains "Restore ") { throw "Teardown did not restore tracked overrides." }
Remove-Item -LiteralPath $log
try { & (Join-Path $launcher "Stop-Ff7ecOffline.ps1") -KeepAssetOverrides | Out-Null }
catch {
    if ($_ -notmatch "simulated server startup failure") { throw }
}
if (Test-Path -LiteralPath $log) { throw "KeepAssetOverrides restored tracked overrides." }
"Launcher checks passed."
