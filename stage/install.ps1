# WindowsPilot one-shot installer/updater — designed for PS Remoting:
#   Invoke-Command -ComputerName BOX { Invoke-WebRequest http://HOST:8898/install.ps1 -UseBasicParsing | Invoke-Expression }
# or:  powershell -File install.ps1 -Base http://HOST:8898
# Idempotent + version-gated: compares served version.json against the live
# /api/status and skips cheaply when already current (-Force re-extracts).
# Registers a self-update task so boxes converge on whatever the serve root
# publishes — rebuild once on the host, every guest picks it up on its tick.
# ASCII only (Direct/PS-remoting transports mangle UTF-16 in captured output). Needs an
# interactive-session target: Session 0 is UIA/SendInput blind.

param(
  [string]$Base = 'http://192.168.50.1:8898',   # root serving pilot-payload.zip + version.json
  [int]$Port = 8791,
  [string]$Bind = '',                             # '' = 0.0.0.0 LAN, or 127.0.0.1
  [string]$User = '',                             # task principal; '' = current user
  [string]$TokenAct = 'act-rig-1',
  [int]$EveryMin = 30,                            # self-update tick minutes
  [switch]$Force,                                 # re-extract even when versions match
  [switch]$Loopback                               # shorthand for -Bind 127.0.0.1
)
$ErrorActionPreference = 'Continue'
# env fallbacks: the self-update tick re-fetches this script with defaults only,
# so the registering run bakes its resolved values into PILOT_* env vars.
if ($env:PILOT_BASE)   { $Base = $env:PILOT_BASE }
if ($env:PILOT_PORT)   { try { $Port = [int]$env:PILOT_PORT } catch { } }
if ($env:PILOT_BIND)   { $Bind = $env:PILOT_BIND }
if ($env:PILOT_USER)   { $User = $env:PILOT_USER }
if ($env:PILOT_TOKEN)  { $TokenAct = $env:PILOT_TOKEN }
if ($env:PILOT_EVERY)  { try { $EveryMin = [int]$env:PILOT_EVERY } catch { } }
if ($env:PILOT_FORCE)  { $Force = $true }
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
$pr = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive

# ---- self-update tick (registered before the version gate so already-current
# ---- boxes still get it): re-run this installer every $EveryMin. The version
# ---- gate makes each tick cheap, and boxes converge on whatever $Base publishes.
try {
  Unregister-ScheduledTask -TaskName WindowsPilotUpdate -Confirm:$false -ErrorAction SilentlyContinue
  # bake resolved values into the command itself: the tick re-fetches install.ps1
  # which reads PILOT_* env first, so later re-registrations keep this box's shape.
  $pre  = "`$env:PILOT_BASE='$Base'; `$env:PILOT_PORT='$Port'; `$env:PILOT_BIND='$Bind'; `$env:PILOT_USER='$User'; `$env:PILOT_TOKEN='$TokenAct'; `$env:PILOT_EVERY='$EveryMin'; "
  $cmd  = $pre + "Invoke-WebRequest '$Base/install.ps1' -UseBasicParsing -TimeoutSec 20 | Invoke-Expression"
  $au   = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -NonInteractive -WindowStyle Hidden -Command ' + [char]39 + $cmd + [char]39)
  $tu   = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes $EveryMin)
  $su   = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit 0 -MultipleInstances IgnoreNew
  Register-ScheduledTask -TaskName WindowsPilotUpdate -Action $au -Principal $pr -Settings $su -Trigger $tu | Out-Null
} catch { }

$log = 'C:\WindowsPilot\install.log'
New-Item -ItemType Directory -Force C:\WindowsPilot | Out-Null
function P($m) { ((Get-Date -Format 'HH:mm:ss') + ' ' + $m) | Out-File -Append -Encoding ascii $log }

function VParts($s) { @(("$s" -split '\.' | Where-Object { $_ -match '^\d+$' }) | ForEach-Object { [int]$_ }) }
function SameVer($a, $b) {
  $pa = VParts $a; $pb = VParts $b
  if (-not $pa -or -not $pb) { return $false }
  $n = [Math]::Min($pa.Count, $pb.Count)
  for ($i = 0; $i -lt $n; $i++) { if ($pa[$i] -ne $pb[$i]) { return $false } }
  return $true
}

# ---- current version from the live pilot (empty on first install) ----
$cur = ''
try {
  $r = Invoke-RestMethod "http://localhost:$Port/api/status?token=$TokenAct" -TimeoutSec 5
  if ($r.ok) { $cur = $r.data.version }
} catch { }

# ---- served version; skip cheaply when already current ----
$want = ''
try { $want = (Invoke-RestMethod "$Base/version.json" -TimeoutSec 15).version } catch { P ("VERFETCH_EXC " + $_.Exception.Message) }
if (-not $Force -and $cur -and $want -and (SameVer $cur $want)) {
  P "CURRENT $cur want=$want"
  Write-Output "WindowsPilot up to date v$cur on :$Port - nothing to do."
  exit 0
}
P "UPDATE cur=$cur want=$want base=$Base port=$Port bind=$Bind user=$User"

# ---- payload: download WHILE the pilot still runs (serve root offline must not
# ---- leave the box pilot-less: stop only after bytes are on disk) ----
try { Invoke-WebRequest "$Base/pilot-payload.zip" -OutFile C:\WindowsPilot\payload.zip -UseBasicParsing }
catch { P ("DL_FAIL " + $_.Exception.Message); Write-Output "FAIL download from $Base : $($_.Exception.Message)"; exit 1 }
P ("DL=" + (Get-Item C:\WindowsPilot\payload.zip).Length)

# ---- stop a previous instance so files unlock ----
Stop-ScheduledTask -TaskName WindowsPilot -ErrorAction SilentlyContinue
Stop-Process -Name WindowsPilot -Force -ErrorAction SilentlyContinue
Start-Sleep 1
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
$cfgFile = $dir + '\pilot.json'
# keep existing config on re-deploy (operators may have tuned bind/port); seed only when missing
if (-not (Test-Path $cfgFile)) {
  $cfg = @{ bind=$Bind; port=$Port; governance=@{ allow_mcp_arm=$true; auto_arm='startup'; rate_per_min=300; rate_burst=60 }; capture=@{ prefer='gdi' }; rig=@{ profile=$true } } | ConvertTo-Json -Depth 5
  Set-Content -Path $cfgFile -Value $cfg -Encoding UTF8
}
$tokFile = $dir + '\tokens.json'
if (-not (Test-Path $tokFile)) {
  Set-Content -Path $tokFile -Value (ConvertTo-Json @{ $TokenAct='act'; 'ro-rig-1'='ro'; 'drive-rig-1'='drive' }) -Encoding ASCII
}
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
