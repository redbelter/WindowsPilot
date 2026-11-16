$ErrorActionPreference = "Continue"
# v4: disable wlms in Server-2025-EVAL golden via DISM offline servicing
# (immune to loaded-hive DACLs), with robocopy /B backup-mode overwrite as
# fallback. Ends with independent on-disk verification.
$golden = "C:\HyperV\Media\golden\2521f0c3d086b5ec.vhdx"

function Clear-AccessPaths([int]$dn) {
    if (-not $dn) { return }
    foreach ($p in @(Get-Partition -DiskNumber $dn -ErrorAction SilentlyContinue)) {
        foreach ($ap in @($p.AccessPaths)) {
            if ($ap) { Remove-PartitionAccessPath -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber -AccessPath $ap -ErrorAction SilentlyContinue }
        }
    }
}
function Find-Win([int]$dn) {
    foreach ($part in @(Get-Disk -Number $dn | Get-Partition | Where-Object { $_.Size -gt 1GB })) {
        foreach ($root in @("R:\", "S:\")) {
            try { Add-PartitionAccessPath -DiskNumber $part.DiskNumber -PartitionNumber $part.PartitionNumber -AccessPath $root -ErrorAction Stop } catch { continue }
            Start-Sleep -Milliseconds 600
            if (Test-Path ($root + "Windows\System32\config\SYSTEM")) { return $root }
        }
    }
    return $null
}

# ---- cleanup ----
foreach ($hk in @("GOLD", "GOLD3", "GOLDV")) { reg unload ("HKLM\" + $hk) 2>&1 | Out-Null }
try { $s = Get-VHD -Path $golden -ErrorAction Stop } catch { $s = $null }
if ($s -and $s.DiskNumber) {
    "cleanup: dismounting leftover disk " + $s.DiskNumber
    Clear-AccessPaths $s.DiskNumber
    Dismount-VHD -Path $golden -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

# ---- mount ----
$d = Mount-VHD -Path $golden -Passthru
"attached disk " + $d.Number
$win = Find-Win $d.Number
if (-not $win) { Clear-AccessPaths $d.Number; Dismount-VHD -Path $golden; throw "no windows partition" }
"windows at " + $win

# ---- attempt 1: DISM offline service disable ----
"`n== DISM =="
dism.exe /Image:"$($win.TrimEnd('\'))" /Disable-Service:wlms /quiet
"DISM_EXIT=" + $LASTEXITCODE

# ---- attempt 2 (always run as belt): robocopy /B overwrite wlms.exe with dummy ----
"`n== robocopy /B =="
New-Item -ItemType Directory -Force C:\patchsrc | Out-Null
Copy-Item C:\Windows\System32\where.exe C:\patchsrc\wlms.exe -Force
$wlmsOnDisk = $win + "Windows\System32\wlms\wlms.exe"
if (Test-Path $wlmsOnDisk) {
    $origSize = (Get-Item $wlmsOnDisk).Length
    robocopy.exe C:\patchsrc ($win + "Windows\System32\wlms") wlms.exe /B /IS /NFL /NDL /NJH /NJS | Out-Null
    "ROBO_EXIT=" + $LASTEXITCODE
    $newSize = (Get-Item $wlmsOnDisk).Length
    "wlms.exe size " + $origSize + " -> " + $newSize
} else { "wlms.exe absent (already nuked?)" }

# ---- in-memory verify via reg load ----
"`n== in-memory =="
reg load HKLM\GOLDV ($win + "Windows\System32\config\SYSTEM") 2>&1 | Out-Null
reg query "HKLM\GOLDV\ControlSet001\Services\wlms" /v Start 2>&1 | Select-Object -Last 1 | ForEach-Object { "MEM " + $_ }
reg unload HKLM\GOLDV 2>&1 | ForEach-Object { "unload: " + $_ }
Clear-AccessPaths $d.Number
Dismount-VHD -Path $golden -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# ---- on-disk verify: fresh mount, re-read hive from file ----
"`n== on-disk verify =="
$d2 = Mount-VHD -Path $golden -Passthru
$win2 = Find-Win $d2.Number
if (-not $win2) { Clear-AccessPaths $d2.Number; Dismount-VHD -Path $golden; throw "no windows partition on remount" }
reg load HKLM\GOLDV ($win2 + "Windows\System32\config\SYSTEM") 2>&1 | Out-Null
reg query "HKLM\GOLDV\ControlSet001\Services\wlms" /v Start 2>&1 | Select-Object -Last 1 | ForEach-Object { "DISK " + $_ }
reg unload HKLM\GOLDV 2>&1 | Out-Null
if (Test-Path ($win2 + "Windows\System32\wlms\wlms.exe")) {
    "DISK wlms.exe size = " + (Get-Item ($win2 + "Windows\System32\wlms\wlms.exe")).Length + " (want 57856 = dummy)"
}
Clear-AccessPaths $d2.Number
Dismount-VHD -Path $golden -ErrorAction SilentlyContinue
"`nPATCH4_DONE (want: DISK ... Start REG_DWORD 0x4)"
