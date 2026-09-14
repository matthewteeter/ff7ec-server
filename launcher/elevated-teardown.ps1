<#
.SYNOPSIS
    Elevated helper: removes the FF7EC offline-mode hosts file redirects (the "# FF7EC-
    Offline-Server" block added by elevated-setup.ps1). Leaves the root CA installed
    (harmless, and re-used next time) - only the hosts redirect needs to come out to let
    the machine reach the real game servers again while they're still live.
#>
param(
    [Parameter(Mandatory)] [string]$LogPath
)

$log = [System.Collections.Generic.List[string]]::new()
function Log($msg) { $log.Add($msg); }

try {
    $hostsPath = "$env:SystemRoot\System32\drivers\etc\hosts"
    $lines = [System.Collections.Generic.List[string]](Get-Content $hostsPath)
    $marker = "# FF7EC-Offline-Server"
    $filtered = [System.Collections.Generic.List[string]]::new()
    $skipping = $false
    foreach ($line in $lines) {
        if ($line -eq $marker) { $skipping = $true; continue }
        if ($skipping -and $line -eq "") { $skipping = $false; continue }
        if (-not $skipping) { $filtered.Add($line) }
    }
    Set-Content -Path $hostsPath -Value $filtered -Encoding ASCII
    ipconfig.exe /flushdns | Out-Null
    Log "Hosts file FF7EC redirect block removed."
    Log "Windows DNS cache flushed."
    Log "SUCCESS"
}
catch {
    Log "ERROR: $($_.Exception.Message)"
}
finally {
    $log | Out-File -FilePath $LogPath -Encoding utf8
}
