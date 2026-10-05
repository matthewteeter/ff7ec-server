param([string]$Repository = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference = "Stop"
$root = Join-Path ([IO.Path]::GetTempPath()) ("ff7ec-launch-check-" + [Guid]::NewGuid().ToString("N"))
$launcher = Join-Path $root "launcher"
$server = Join-Path $root "src\Ff7ec.Server"
New-Item -ItemType Directory -Path $launcher, $server -Force | Out-Null
$global:Ff7ecAccountLauncherCheckCommand = $null
$global:Ff7ecAccountLauncherOverrideCalls = 0
function Start-Process {
    param($FilePath, $ArgumentList, [switch]$PassThru)
    $global:Ff7ecAccountLauncherCheckCommand = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($ArgumentList[-1]))
    throw "fixture-start"
}
function Get-NetTCPConnection { param($LocalPort, $State, $ErrorAction) }
try {
    Copy-Item -LiteralPath (Join-Path $Repository "launcher\Start-Ff7ecOffline.ps1") -Destination $launcher
    'param($Action); $global:Ff7ecAccountLauncherOverrideCalls++' |
        Set-Content -LiteralPath (Join-Path $launcher "Set-Ff7ecAssetOverride.ps1")
    Copy-Item -LiteralPath (Join-Path $Repository "launcher\Get-Ff7ecGameDirectory.ps1") -Destination $launcher
    "'$($root.Replace("'", "''"))'" | Set-Content -LiteralPath (Join-Path $launcher "Get-Ff7ecPreservationRoot.ps1")
    @{ Ff7ec = @{ CertDirectory = "certs"; ListenPort = 443; Hostnames = @("game.test") } } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $server "appsettings.json")
    $json = Join-Path $root "account ' sample.json"
    $assembly = Join-Path $root "protocol ' sample.dll"
    $schema = Join-Path $root "schema ' sample.json"
    $game = Join-Path $root "game ' sample"
    New-Item -ItemType Directory -Path (Join-Path $game "octo"),
        (Join-Path $game "FF7EC_Data\StreamingAssets\MasterData") -Force | Out-Null
    "{}" | Set-Content -LiteralPath (Join-Path $game "FF7EC_Data\StreamingAssets\MasterData\index.json")
    "{}" | Set-Content -LiteralPath $json
    "" | Set-Content -LiteralPath $assembly
    "{}" | Set-Content -LiteralPath $schema
    $start = Join-Path $launcher "Start-Ff7ecOffline.ps1"
    foreach ($arguments in @(
        @{ ProtocolAssemblyPath = $assembly },
        @{ Standalone = $true },
        @{ AccountJsonPath = $json; GameDirectory = $game },
        @{ Standalone = $true; AccountJsonPath = $json; GameDirectory = (Join-Path $root "missing-game") },
        @{ ProtocolSchemaPath = $schema },
        @{ AccountJsonPath = $json; ProtocolAssemblyPath = $assembly; ProtocolSchemaPath = $schema },
        @{ AccountJsonPath = (Join-Path $root "missing.json") }
    )) {
        $failed = $false
        try { & $start -SkipAssetOverrides @arguments | Out-Null }
        catch { $failed = $true }
        if (-not $failed -or $null -ne $global:Ff7ecAccountLauncherCheckCommand) { throw "Invalid export options reached server startup." }
    }
    foreach ($arguments in @(
        @{ AccountJsonPath = $json },
        @{ AccountJsonPath = $json; ProtocolSchemaPath = $schema },
        @{ AccountJsonPath = $json; ProtocolAssemblyPath = $assembly },
        @{ AccountJsonPath = $json; Standalone = $true; GameDirectory = $game }
    )) {
        $global:Ff7ecAccountLauncherCheckCommand = $null
        $global:Ff7ecAccountLauncherOverrideCalls = 0
        try { & $start -SkipAssetOverrides @arguments | Out-Null }
        catch { if ($_ -notmatch "fixture-start") { throw } }
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseInput($global:Ff7ecAccountLauncherCheckCommand, [ref]$tokens, [ref]$errors)
        if ($errors.Count -ne 0) { throw "Export paths generated invalid PowerShell." }
        $strings = @($ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.StringConstantExpressionAst]
        }, $true) | ForEach-Object { $_.Value })
        foreach ($path in @($arguments.Values | Where-Object { $_ -is [string] })) {
            if ($strings -notcontains $path) { throw "Export path was not passed losslessly: $path" }
        }
        foreach ($key in @("ProtocolAssemblyPath", "ProtocolSchemaPath")) {
            if (($global:Ff7ecAccountLauncherCheckCommand -match "Ff7ec__AccountExport__$key") -ne $arguments.ContainsKey($key)) {
                throw "Unexpected protocol override passed to server: $key"
            }
        }
        if ($global:Ff7ecAccountLauncherCheckCommand -notmatch "Ff7ec__AccountExport__JsonPath") {
            throw "Account JSON path was not passed to the server."
        }
        if ($arguments.ContainsKey("Standalone")) {
            if ($global:Ff7ecAccountLauncherCheckCommand -notmatch "Ff7ec__Standalone__Enabled = 'true'" -or
                $global:Ff7ecAccountLauncherCheckCommand -notmatch "Ff7ec__Standalone__GameDirectory" -or
                $global:Ff7ecAccountLauncherCheckCommand -notmatch "Ff7ec__DataDirectory" -or
                $global:Ff7ecAccountLauncherOverrideCalls -ne 0) {
                throw "Standalone startup did not isolate server configuration and overrides."
            }
        }
    }
    "Account-export launcher checks passed."
}
finally {
    Remove-Variable -Name Ff7ecAccountLauncherCheckCommand -Scope Global
    Remove-Variable -Name Ff7ecAccountLauncherOverrideCalls -Scope Global
    foreach ($file in [IO.Directory]::EnumerateFiles($root, "*", [IO.SearchOption]::AllDirectories)) {
        [IO.File]::Delete($file)
    }
    foreach ($directory in @([IO.Directory]::EnumerateDirectories($root, "*", [IO.SearchOption]::AllDirectories) |
        Sort-Object Length -Descending)) { [IO.Directory]::Delete($directory) }
    [IO.Directory]::Delete($root)
}
