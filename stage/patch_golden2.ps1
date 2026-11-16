$ErrorActionPreference = "Stop"
# Offline-patch the Server-2025-EVAL golden: disable wlms (eval license
# enforcer that force-reboots every child VM hourly). Safe: golden currently
# has no referencing VMs (all sandboxes deleted).
$golden = "C:\HyperV\Media\golden\2521f0c3d086b5ec.vhdx"   # hash(key)=ISO|edition|64GB
if (-not (Test-Path $golden)) { throw "golden not found: $golden" }
"TARGET $golden"

# clear any mount left by a previous failed attempt
Dismount-VHD -Path $golden -ErrorAction SilentlyContinue
if (Test-Path "R:\") { throw "R:\ already in use - free it first" }
if (Test-Path "S:\") { throw "S:\ already in use - free it first" }

$disk = Mount-VHD -Path $golden -Passthru
"ATTACHED disk " + $disk.Number
$hit = $false
foreach ($part in (Get-Disk -Number $disk.Number | Get-Partition | Where-Object { $_.Size -gt 1GB })) {
    foreach ($root in @("R:\", "S:\")) {
        try { $part | Add-PartitionAccessPath -AccessPath $root -ErrorAction Stop } catch { continue }
        Start-Sleep -Milliseconds 600
        if (Test-Path ($root + "Windows\System32\config\SYSTEM")) { $win = $root; $hit = $true; break }
    }
    if ($hit) { break }
    Remove-PartitionAccessPath -AccessPath "R:\" -ErrorAction SilentlyContinue
    Remove-PartitionAccessPath -AccessPath "S:\" -ErrorAction SilentlyContinue
}
if (-not $hit) { Dismount-VHD -Path $golden; throw "no Windows partition found" }
"FOUND windows at " + $win

reg load HKLM\GOLD ($win + "Windows\System32\config\SYSTEM")
if ($LASTEXITCODE -ne 0) { Dismount-VHD -Path $golden; throw "reg load failed" }
try {
    $n = 0
    foreach ($set in @("ControlSet001","ControlSet002")) {
        $k = "HKLM\GOLD\$set\Services\wlms"
        reg query $k 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            reg add $k /v Start /t REG_DWORD /d 4 /f | Out-Null
            "$set\wlms Start=4 OK"; $n++
        } else { "$set\wlms absent (skip)" }
    }
    if ($n -eq 0) { throw "wlms key not present in any control set - image differs from expectation" }
} finally {
    reg unload HKLM\GOLD | Out-Null
    Remove-PartitionAccessPath -AccessPath $win -ErrorAction SilentlyContinue
    Dismount-VHD -Path $golden -ErrorAction SilentlyContinue
}
"GOLD_PATCHED (wlms disabled in $n control set(s))"
"ALL_DONE - you can close this window"
