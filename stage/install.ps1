# WindowsPilot one-shot installer — designed for PS Remoting:
#   Invoke-Command -ComputerName BOX { iwr http://HOST:18888/install.ps1 -UseBasi cParsing | iex }
# or:  powershell -File install.ps1 -Base http://HOST:18888
# Idempotent: re-running re-extracts payload + re-registers the task. ASCII only
# (Direct/PS-remoting transports mangle UTF-16 in captured output). Needs an
# interactive-session target: Session 0 is UIA/SendInput blind.

param(
  [string]$Base = 'http://192.168.50.1:18888',   # root serving pilot-payload.zip
  [int]$Port = 8791,
  [string]$Bind = '',                             # '' = 0.0.0.0 LAN, or 127.0.0.1
  [string]$User = '',                             # task principal; '' = current user
  [string]$TokenAct = 'act-rig-1',
  [switch]$Loopback                               # shorthand for -Bind 127.0.0.1
)
$ErrorActionPreference = 'Continue'
if ($Loopback -and -not $Bind) { $Bind = '127.0.0.1' }
if (-not $Bind) { $Bind = '0.0.0.0' }
if (-not $User) {
  # PS remoting / PS Direct may run as SYSTEM — the task still must run in the
  # interactive console session, so resolve the console owner instead.
  if ($env:USERNAME -and $env:USERNAME -ne 'SYSTEM') { $User = "$env:COMPUTERNAME\$env:USERNAME" }
  else {
    try { $User = (Get-CimInstance Win32_ComputerSystem).UserName } catch { }
    if (-not $User) { $User = "$env:COMPUTERNAME\Sandbox" }
  }
}

$log = 'C:\WindowsPilot\install.log'
New-Item -ItemType Directory -Force C:\WindowsPilot | Out-Null
function P($m) { ((Get-Date -Format 'HH:mm:ss') + ' ' + $m) | Out-File -Append -Encoding ascii $log }
P "INSTALL base=$Base port=$Port bind=$Bind user=$User"

# ---- stop a previous instance so files unlock ----
Stop-ScheduledTask -TaskName WindowsPilot -ErrorAction SilentlyContinue
Stop-Process -Name WindowsPilot -Force -ErrorAction SilentlyContinue
Start-Sleep 1

# ---- payload ----
try { Invoke-WebRequest "$Base/pilot-payload.zip" -OutFile C:\WindowsPilot\payload.zip -UseBasicParsing }
catch { P ("DL_FAIL " + $_.Exception.Message); Write-Output "FAIL download from $Base : $($_.Exception.Message)"; exit 1 }
P ("DL=" + (Get-Item C:\WindowsPilot\payload.zip).Length)
Remove-Item C:\WindowsPilot\app -Recurse -Force -ErrorAction SilentlyContinue
try { Expand-Archive C:\WindowsPilot\payload.zip C:\WindowsPilot\app -Force; P EXTRACTED }
catch { P ("EX_FAIL " + $_.Exception.Message); Write-Output "FAIL extract: $($_.Exception.Message)"; exit 1 }

# ---- config + tokens (rig-style defaults so a fresh box answers immediately) ----
# Resolve the PRINCIPAL's LocalAppData, not the installing account's: under PS
# remoting the installer may run as SYSTEM while the task runs as the console
# user — pilot.json must land in the profile that actually starts the exe.
$pname = ($User -split '\\')[-1]
$homeDir = try { Split-Path $env:SystemRoot -Parent } catch { 'C:' }
$pdir = if ($pname -eq $env:USERNAME -and $env:USERNAME) { $env:LOCALAPPDATA }
        elseif ($pname) { "$homeDir\Users\$pname\AppData\Local" }
        else { $env:LOCALAPPDATA }
$dir = $pdir + '\WindowsPilot'
New-Item -ItemType Directory -Force $dir | Out-Null
$cfg = @{ bind=$Bind; port=$Port; governance=@{ allow_mcp_arm=$true; auto_arm='startup'; rate_per_min=300; rate_burst=60 }; capture=@{ prefer='gdi' }; rig=@{ profile=$true } } | ConvertTo-Json -Depth 5
Set-Content -Path ($dir + '\pilot.json') -Value $cfg -Encoding UTF8
Set-Content -Path ($dir + '\tokens.json') -Value (ConvertTo-Json @{ $TokenAct='act'; 'ro-rig-1'='ro'; 'drive-rig-1'='drive' }) -Encoding ASCII
P CFG

# ---- firewall: open the API port (harmless when firewall is off) ----
try { netsh.exe advfirewall firewall add rule name='WindowsPilot' dir=in action=allow protocol=TCP localport=$Port profile=any | Out-Null } catch { P FW_EXC }

# ---- session-1 task: bare WinExe (never allocates a console), boot+logon,
# ---- StartWhenAvailable + restarts so it survives reboots/late tokens ----
Unregister-ScheduledTask -TaskName WindowsPilot -Confirm:$false -ErrorAction SilentlyContinue
$a  = New-ScheduledTaskAction -Execute 'C:\WindowsPilot\app\WindowsPilot.exe' -WorkingDirectory 'C:\WindowsPilot\app'
$pr = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive
$se = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit 0 -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
$t  = @(New-ScheduledTaskTrigger -AtStartup; New-ScheduledTaskTrigger -AtLogOn)
Register-ScheduledTask -TaskName WindowsPilot -Action $a -Principal $pr -Settings $se -Trigger $t | Out-Null
P TASK_REGISTERED
Start-ScheduledTask -TaskName WindowsPilot

# ---- verify: poll status, print the answer so the remote caller sees proof ----
$ver = ''
for ($i = 0; $i -lt 15; $i++) {
  Start-Sleep 2
  try {
    $r = Invoke-RestMethod ("http://localhost:$Port/api/status?token=$TokenAct") -TimeoutSec 5
    if ($r.ok) { $ver = $r.data.version; break }
  } catch { }
}
if ($ver) {
  P "OK version=$ver"
  Write-Output ("WindowsPilot up v$ver on :$Port  ->  http://<this-box>:$Port/?token=$TokenAct")
  exit 0
}
P NOT_UP
Write-Output 'WARN task started but /api/status silent yet — check C:\WindowsPilot\install.log and ' + $dir + '\pilot.log'
exit 0
