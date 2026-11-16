$ErrorActionPreference = "Stop"
# Find golden via a live differencing child if possible, else patch all goldens.
$goldens = @()
$child = Get-ChildItem C:\HyperV\VMs -Directory -ErrorAction SilentlyContinue |
    ForEach-Object { Join-Path $_.FullName "disk.vhdx" } |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($child) {
    $p = (Get-VHD -Path $child).ParentPath
    if ($p -and (Test-Path $p)) { $goldens = @($p) }
}
if (-not $goldens) { $goldens = @(Get-ChildItem C:\HyperV\Media\golden\*.vhdx | Select-Object -Expand FullName) }
foreach ($g in $goldens) {
    "PATCHING $g"
    Mount-VHD -Path $g -Passthru | Get-Disk | Get-Partition |
        Where-Object IsSystem -or Where OperatingSystem | Select-Object -First 1 |
        Add-PartitionAccessPath -AccessPath "R:\" -ErrorAction Stop | Out-Null
    # fallback: any partition containing \Windows
    if (-not (Test-Path "R:\Windows\System32\config\SYSTEM")) {
        Dismount-VHD -Path $g
        $found = $false
        Mount-VHD -Path $g -Passthru | Get-Disk | Get-Partition | ForEach-Object {
            if (-not $found) {
                try {
                    $_ | Add-PartitionAccessPath -AccessPath "R:\" -ErrorAction Stop
                    Start-Sleep -Milliseconds 400
                    if (Test-Path "R:\Windows\System32\config\SYSTEM") { $found = $true }
                } catch {}
            }
        }
        if (-not $found) { throw "no Windows partition found on $g" }
    }
    reg load HKLM\GOLD "R:\Windows\System32\config\SYSTEM" | Out-Null
    try {
        foreach ($set in @("ControlSet001","ControlSet002")) {
            if (Test-Path "HKLM:\GOLD\$set\Services\wlms") {
                reg add "HKLM\GOLD\$set\Services\wlms" /v Start /t REG_DWORD /d 4 /f | Out-Null
                "  $set wlms Start=4 OK"
            }
        }
        # belt: also stop the rearm-countdown task scheduler hook (license renewal)
        foreach ($set in @("ControlSet001","ControlSet002")) {
            if (Test-Path "HKLM:\GOLD\$set\Services\swprv") { }  # no-op, keep restore point svc
        }
    } finally {
        reg unload HKLM\GOLD | Out-Null
        Dismount-VHD -Path $g | Out-Null
    }
    "GOLD_PATCHED $g"
}
"ALL_DONE"
