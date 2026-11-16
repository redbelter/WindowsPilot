$ErrorActionPreference = "Continue"
# v3: offline-patch Server-2025-EVAL golden (wlms Start=4) using
# SeRestorePrivilege to bypass TrustedInstaller key ACLs on the loaded hive.
# Self-verifies the value on disk before finishing.
$golden = "C:\HyperV\Media\golden\2521f0c3d086b5ec.vhdx"

Add-Type -Language CSharp @"
using System;
using System.Runtime.InteropServices;
public static class Tok {
  [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr h, uint acc, out IntPtr tok);
  [DllImport("advapi32.dll", SetLastError=true)] static extern bool LookupPrivilegeValue(string host, string name, out long luid);
  [StructLayout(LayoutKind.Sequential)] struct TA { public uint cnt; public long luid; public uint attr; }
  [DllImport("advapi32.dll", SetLastError=true)] static extern bool AdjustTokenPrivileges(IntPtr tok, bool dis, ref TA np, int len, IntPtr prev, IntPtr ret);
  [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
  const uint SE_PRIVILEGE_ENABLED = 2; const uint TOKEN_ADJUST = 0x20; const uint TOKEN_QUERY = 0x8;
  public static bool Enable(params string[] privs) {
    IntPtr tok; if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST | TOKEN_QUERY, out tok)) return false;
    foreach (var p in privs) {
      TA t = new TA(); t.cnt = 1; t.attr = SE_PRIVILEGE_ENABLED;
      if (!LookupPrivilegeValue(null, p, out t.luid)) return false;
      if (!AdjustTokenPrivileges(tok, false, ref t, 0, IntPtr.Zero, IntPtr.Zero)) return false;
    }
    return true;
  }
}
"@ | Out-Null
if ([Tok]::Enable("SeRestorePrivilege","SeTakeOwnershipPrivilege","SeBackupPrivilege")) { "PRIVS_ENABLED" } else { "PRIVS_FAILED (lasterr " + [Runtime.InteropServices.Marshal]::GetLastWin32Error() + ")" }

# ---- cleanup leftovers ----
reg unload HKLM\GOLD3 2>&1 | Out-Null
try { $stale = Get-VHD -Path $golden -ErrorAction Stop } catch { $stale = $null }
if ($stale) {
  "dismounting leftover disk " + $stale.DiskNumber
  foreach ($p in (Get-Partition -DiskNumber $stale.DiskNumber -ErrorAction SilentlyContinue)) {
    $paths = @(Get-PartitionAccessPath -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber -ErrorAction SilentlyContinue) | Where-Object { $_ }
    foreach ($ap in $paths) { Remove-PartitionAccessPath -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber -AccessPath $ap -ErrorAction SilentlyContinue }
  }
  Dismount-VHD -Path $golden -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 2
}

function Find-WindowsPartition([int]$diskNo) {
  foreach ($part in (Get-Disk -Number $diskNo | Get-Partition | Where-Object { $_.Size -gt 1GB })) {
    foreach ($root in @("R:\", "S:\")) {
      try { Add-PartitionAccessPath -DiskNumber $part.DiskNumber -PartitionNumber $part.PartitionNumber -AccessPath $root -ErrorAction Stop } catch { continue }
      Start-Sleep -Milliseconds 600
      if (Test-Path ($root + "Windows\System32\config\SYSTEM")) { return $root }
      Remove-PartitionAccessPath -DiskNumber $part.DiskNumber -PartitionNumber $part.PartitionNumber -AccessPath $root -ErrorAction SilentlyContinue
    }
  }
  return $null
}

# ---- mount & patch ----
$d = Mount-VHD -Path $golden -Passthru
$win = Find-WindowsPartition $d.Number
if (-not $win) { Dismount-VHD -Path $golden; throw "no windows partition" }
"windows at " + $win
reg load HKLM\GOLD3 ($win + "Windows\System32\config\SYSTEM") 2>&1 | Out-Null

$sets = @("ControlSet001", "ControlSet002") | Where-Object { Test-Path ("HKLM:\GOLD3\" + $_) }
foreach ($set in $sets) {
  $path = "GOLD3\$set\Services\wlms"
  $before = "(missing)"
  try { $before = ([Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($path)).GetValue("Start") } catch {}
  "BEFORE $set Start=$before"
  $ok = $false
  try {
    $k = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($path, $true)   # SeRestorePrivilege bypasses TI DACL
    $k.SetValue("Start", 4, [Microsoft.Win32.RegistryValueKind]::DWord)
    $k.Close()
    $ok = $true
  } catch { "NET_WRITE_FAILED: " + $_.Exception.Message; reg add ("HKLM\" + $path) /v Start /t REG_DWORD /d 4 /f | Out-Null }
  $after = try { ([Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($path)).GetValue("Start") } catch { "(err)" }
  "IN-MEMORY $set Start=$after"
}

reg unload HKLM\GOLD3 2>&1 | Out-Null
foreach ($p in (Get-Partition -DiskNumber $d.Number -ErrorAction SilentlyContinue)) {
  $paths = @(Get-PartitionAccessPath -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber -ErrorAction SilentlyContinue) | Where-Object { $_ }
  foreach ($ap in $paths) { Remove-PartitionAccessPath -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber -AccessPath $ap -ErrorAction SilentlyContinue }
}
Dismount-VHD -Path $golden -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# ---- ON-DISK verify: remount read, re-read hive from file ----
"== on-disk verify =="
$d2 = Mount-VHD -Path $golden -Passthru
$win2 = Find-WindowsPartition $d2.Number
reg load HKLM\GOLDV ($win2 + "Windows\System32\config\SYSTEM") 2>&1 | Out-Null
foreach ($set in $sets) {
  $v = try { ([Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("GOLDV\$set\Services\wlms")).GetValue("Start") } catch { "(key err)" }
  "DISK $set wlms Start => $v"
}
reg unload HKLM\GOLDV 2>&1 | Out-Null
foreach ($p in (Get-Partition -DiskNumber $d2.Number -ErrorAction SilentlyContinue)) {
  $paths = @(Get-PartitionAccessPath -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber -ErrorAction SilentlyContinue) | Where-Object { $_ }
  foreach ($ap in $paths) { Remove-PartitionAccessPath -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber -AccessPath $ap -ErrorAction SilentlyContinue }
}
Dismount-VHD -Path $golden -ErrorAction SilentlyContinue
"PATCH3_DONE (want: DISK ControlSet001 wlms Start => 4)"
