<#
.SYNOPSIS
    Installs the local mitmproxy CA for the current user if it is not trusted.
    Run only if you trust the mitmproxy installation on this machine.
#>
$ErrorActionPreference = "Stop"
$caPath = Join-Path $HOME ".mitmproxy\mitmproxy-ca-cert.cer"
if (-not (Test-Path $caPath -PathType Leaf)) { throw "mitmproxy CA not found: $caPath" }
$ca = [Security.Cryptography.X509Certificates.X509Certificate2]::new($caPath)
$trusted = Get-ChildItem Cert:\CurrentUser\Root, Cert:\LocalMachine\Root |
    Where-Object Thumbprint -eq $ca.Thumbprint
if ($trusted) {
    Write-Host "Local mitmproxy CA is already trusted."
    return
}
& certutil.exe -user -addstore -f Root $caPath
if ($LASTEXITCODE -ne 0) { throw "Could not install the mitmproxy CA (certutil exit $LASTEXITCODE)." }
$installed = Get-ChildItem Cert:\CurrentUser\Root | Where-Object Thumbprint -eq $ca.Thumbprint
if (-not $installed) { throw "certutil succeeded but the mitmproxy CA is not in CurrentUser Trusted Root." }
Write-Host "Installed the local mitmproxy CA for this Windows user."
