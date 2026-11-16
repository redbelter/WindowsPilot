$ErrorActionPreference = "Continue"
# Finalize + verify the golden patch: flush/unload any leftover hive,
# dismount leftovers, then remount READ and print the wlms Start value.
$golden = "C:\HyperV\Media\golden\2521f0c3d086b5ec.vhdx"

"== cleanup =="
reg unload HKLM\GOLD 2>&1 | ForEach-Object { "unload(GOLD): " + $_ }
$att = $null
try { $att = Get-VHD -Path $golden -ErrorAction Stop } catch { "not attached" }
if ($att) {
  "still attached as disk " + $att.DiskNumber
  foreach ($p in (Get-Partition -DiskNumber $att.DiskNumber -ErrorAction SilentlyContinue)) {
    foreach ($ap in @($p.AccessPaths)) {
      Remove-PartitionAccessPath -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber -AccessPath $ap -ErrorAction SilentlyContinue
    }
  }
  Dismount-VHD -Path $golden -ErrorAction SilentlyContinue
  "dismounted leftover"
}
Start-Sleep -Seconds 2

"== verify =="
$d = Mount-VHD -Path $golden -Passthru
$win = $null
foreach ($part in (Get-Disk -Number $d.Number | Get-Partition | Where-Object { $_.Size -gt 1GB })) {
  $added = $null
  foreach ($root in @("R:\", "S:\")) {
    try { Add-PartitionAccessPath -DiskNumber $part.DiskNumber -PartitionNumber $part.PartitionNumber -AccessPath $root -ErrorAction Stop; $added = $root; break } catch {}
  }
  if (-not $added) { continue }
  Start-Sleep -Milliseconds 600
  if (Test-Path ($added + "Windows\System32\config\SYSTEM")) { $win = $added; break }
  Remove-PartitionAccessPath -DiskNumber $part.DiskNumber -PartitionNumber $part.PartitionNumber -AccessPath $added -ErrorAction SilentlyContinue
}
if (-not $win) { Dismount-VHD -Path $golden; throw "no windows partition found" }
"windows at " + $win
reg load HKLM\GOLDV ($win + "Windows\System32\config\SYSTEM") 2>&1 | Out-Null
foreach ($set in @("ControlSet001", "ControlSet002")) {
  $q = (reg query ("HKLM\GOLDV\" + $set + "\Services\wlms") /v Start 2>&1) -join " "
  ($set + " wlms Start => " + $q)
}
reg unload HKLM\GOLDV 2>&1 | Out-Null
foreach ($p in (Get-Partition -DiskNumber $d.Number -ErrorAction SilentlyContinue)) {
  foreach ($ap in @($p.AccessPaths)) {
    Remove-PartitionAccessPath -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber -AccessPath $ap -ErrorAction SilentlyContinue
  }
}
Dismount-VHD -Path $golden -ErrorAction SilentlyContinue
"VERIFY_DONE - you can close this window"
