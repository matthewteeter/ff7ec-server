<#
.SYNOPSIS
    Elevated helper: installs the FF7EC Offline Server's root CA into the Windows Trusted
    Root store and redirects the game's real API hostnames to 127.0.0.1 in the hosts file.

    Not meant to be run directly - Start-Ff7ecOffline.ps1 launches this via Start-Process
    -Verb RunAs so the single UAC prompt covers both steps.
#>
param(
    [Parameter(Mandatory)] [string]$CaCerPath,
    [Parameter(Mandatory)] [string]$HostnamesCsv,
    [Parameter(Mandatory)] [string]$LogPath
)
$Hostnames = $HostnamesCsv.Split(",", [StringSplitOptions]::RemoveEmptyEntries)

$log = [System.Collections.Generic.List[string]]::new()
function Log($msg) { $log.Add($msg); }

try {
    # --- 1. Install root CA into Trusted Root (idempotent: skip if already present) ---
    $newCert = Get-PfxCertificate -FilePath $CaCerPath
    $existing = Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Thumbprint -eq $newCert.Thumbprint }
    if ($existing) {
        Log "CA already installed in Trusted Root (thumbprint $($newCert.Thumbprint))."
    } else {
        Import-Certificate -FilePath $CaCerPath -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
        Log "Installed CA into Trusted Root (thumbprint $($newCert.Thumbprint))."
    }

    # --- 2. Redirect tracked hostnames to 127.0.0.1 in the hosts file ---
    $hostsPath = "$env:SystemRoot\System32\drivers\etc\hosts"
    $lines = [System.Collections.Generic.List[string]](Get-Content $hostsPath)
    $marker = "# FF7EC-Offline-Server"
    # Remove any previous FF7EC block first so re-runs don't accumulate duplicates.
    $filtered = [System.Collections.Generic.List[string]]::new()
    $skipping = $false
    foreach ($line in $lines) {
        if ($line -eq $marker) { $skipping = $true; continue }
        if ($skipping -and $line -eq "") { $skipping = $false; continue }
        if (-not $skipping) { $filtered.Add($line) }
    }
    $filtered.Add($marker)
    foreach ($h in $Hostnames) { $filtered.Add("127.0.0.1 $h") }
    $filtered.Add("")
    Set-Content -Path $hostsPath -Value $filtered -Encoding ASCII
    Log "Hosts file updated: $($Hostnames.Count) hostnames now point to 127.0.0.1."

    Log "SUCCESS"
}
catch {
    Log "ERROR: $($_.Exception.Message)"
}
finally {
    $log | Out-File -FilePath $LogPath -Encoding utf8
}
