$ErrorActionPreference = "Continue"
$ProgressPreference = "SilentlyContinue"
$log = "C:\prov.log"
if (-not (Test-Path $log)) { New-Item -ItemType File -Path $log -Force | Out-Null }
# keep the log ASCII: PS Direct output transport mangles UTF-16 payloads
function P($m) { ((Get-Date -Format "HH:mm:ss") + " " + $m) | Out-File -Append -Encoding ascii $log }
if (Test-Path C:\prov.done) { Unregister-ScheduledTask -TaskName ProvOnce -Confirm:$false -ErrorAction SilentlyContinue; exit 0 }
P ("START up=" + ((Get-Date)-(Get-CimInstance Win32_OperatingSystem).LastBootUpTime).TotalSeconds.ToString("F0"))

# ---- stop eval-license enforcement FIRST (wlms reboots this box ~hourly) ----
try { $r = Invoke-CimMethod -ClassName SoftwareLicensingService -MethodName Rearm; P ("REARM=" + $r.ReturnValue) } catch { P ("REARM_EXC " + $_.Exception.Message) }
try { cscript.exe //b //nologo C:\Windows\System32\slmgr.vbs /rearm | Out-Null; P SLMGR_DONE } catch { P SLMGR_EXC }
try { Set-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Services\wlms Start 4 -Force; P WLMS_OFF } catch { P WLMS_EXC }
try { taskkill /IM wlms.exe /F | Out-Null; P KILLED } catch { P KILL_EXC }
# permanent watchdog: kill wlms every minute (belt+suspenders for eval reboots)
try {
  if (-not (Get-ScheduledTask WlmsWatchdog -ErrorAction SilentlyContinue)) {
    $aw = New-ScheduledTaskAction -Execute taskkill.exe -Argument "/IM wlms.exe /F"
    $tw = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration (New-TimeSpan -Days 9999)
    $pw = New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount -RunLevel Highest
    $sw = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit 0 -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName WlmsWatchdog -Action $aw -Trigger $tw -Principal $pw -Settings $sw -Force | Out-Null
  }
  P DOG_ON
} catch { P DOG_EXC }
try { Stop-Service UsoSvc -Force -ErrorAction SilentlyContinue; Set-Service UsoSvc Manual -ErrorAction SilentlyContinue; P USO_OFF } catch { P USO_EXC }

# ---- static IP on the internal vSwitch (no DHCP there) ----
# keep any address already on the 50-net (manually assigned, e.g. .21);
# otherwise default to .20.
try {
  $alias = (Get-NetAdapter | Where-Object Status -eq Up | Select-Object -First 1).Name
  if (-not (Get-NetIPAddress -InterfaceAlias $alias -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.IPAddress -like "192.168.50.*" })) {
    New-NetIPAddress -InterfaceAlias $alias -IPAddress 192.168.50.20 -PrefixLength 24 | Out-Null }
  P ("IP_OK alias=" + $alias)
} catch { P ("IP_EXC " + $_.Exception.Message) }

# ---- firewall: allow pilot port from LAN ----
try { netsh.exe advfirewall firewall add rule name="WindowsPilot" dir=in action=allow protocol=TCP localport=8791 profile=any | Out-Null; P FW_OPEN } catch { P FW_EXC }

# ---- payload: prefer pre-seeded local zip, else download ----
# version gate: skip when the running pilot already matches served version.json
# (the tick re-runs this script often; the gate makes it a cheap status compare)
$cur = ''
try { $cur = (Invoke-RestMethod http://localhost:8791/api/status?token=*** -TimeoutSec 5).data.version } catch { }
$want = ''
try { $want = (Invoke-RestMethod http://192.168.50.1:8898/version.json -TimeoutSec 15).version } catch { }
$current = $false
if ($cur -and $want) {
  $pa = @($cur -split '\.' | Where-Object { $_ -match '^\d+$' })
  $pb = @($want -split '\.' | Where-Object { $_ -match '^\d+$' })
  $current = $true
  for ($i = 0; $i -lt [Math]::Min($pa.Count, $pb.Count); $i++) { if ($pa[$i] -ne $pb[$i]) { $current = $false; break } }
}
if ($current) { P "CURRENT $cur"; exit 0 }
P "UPDATE cur=$cur want=$want"
if ((-not (Test-Path C:\WindowsPilot\app\WindowsPilot.exe)) -or $want) {
  New-Item -ItemType Directory -Force C:\WindowsPilot | Out-Null
  if ((-not (Test-Path C:\WindowsPilot\payload.zip)) -or $want) {
    try { Invoke-WebRequest http://192.168.50.1:8898/pilot-payload.zip -OutFile C:\WindowsPilot\payload.zip -UseBasicParsing; P ("DL=" + (Get-Item C:\WindowsPilot\payload.zip).Length) }
    catch { P ("DL_EXC " + $_.Exception.Message); exit 0 }   # retry next tick
  }
  try { Stop-ScheduledTask -TaskName WindowsPilot -ErrorAction SilentlyContinue; Stop-Process -Name WindowsPilot -Force -ErrorAction SilentlyContinue; Remove-Item C:\WindowsPilot\app -Recurse -Force -ErrorAction SilentlyContinue; Expand-Archive C:\WindowsPilot\payload.zip C:\WindowsPilot\app -Force; P EXTRACTED }
  catch { P ("EXTRACT_EXC " + $_.Exception.Message); exit 0 }
}

# ---- config + tokens ----
try {
  $cfg = @{ bind="0.0.0.0"; port=8791; governance=@{ allow_mcp_arm=$true; auto_arm="startup"; rate_per_min=300; rate_burst=60 }; capture=@{ prefer="gdi" }; rig=@{ profile=$true } } | ConvertTo-Json -Depth 5
  $dir = "C:\Users\Sandbox\AppData\Local\WindowsPilot"
  New-Item -ItemType Directory -Force $dir | Out-Null
  Set-Content -Path (Join-Path $dir "pilot.json") -Value $cfg -Encoding UTF8
  Set-Content -Path (Join-Path $dir "tokens.json") -Value (ConvertTo-Json @{ "act-rig-1"="act"; "ro-rig-1"="ro"; "drive-rig-1"="drive" }) -Encoding ASCII
  P CFG
} catch { P ("CFG_EXC " + $_.Exception.Message) }

# ---- WindowsPilot scheduled task (Session 1, boot trigger) ----
# Direct exe launch: the exe is WinExe (GUI subsystem, never allocates a
# console) — a `cmd /c exe >> log` wrapper used to pop a console window that
# Windows Terminal adopted. Logs now go to %LOCALAPPDATA%\WindowsPilot\pilot.log.
try {
  $hn = $env:COMPUTERNAME
  Unregister-ScheduledTask -TaskName WindowsPilot -Confirm:$false -ErrorAction SilentlyContinue
  $a = New-ScheduledTaskAction -Execute "C:\WindowsPilot\app\WindowsPilot.exe" -WorkingDirectory "C:\WindowsPilot\app"
  $pr = New-ScheduledTaskPrincipal -UserId ($hn + "\Sandbox") -LogonType Interactive
  # StartWhenAvailable + RestartCount matter: the boot trigger fires before the
  # autologon session has a usable token, so without retry the pilot never came
  # up after a reboot (task result 1073807364 = task not run, instance ended).
  $se = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit 0 -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
  $t = @(New-ScheduledTaskTrigger -AtStartup; New-ScheduledTaskTrigger -AtLogOn)
  Register-ScheduledTask -TaskName WindowsPilot -Action $a -Principal $pr -Settings $se -Trigger $t | Out-Null
  Start-ScheduledTask -TaskName WindowsPilot
  P PILOT_STARTED
  # self-update tick: converge on whatever PilotServe (:8898) publishes; the
  # version gate in prov_local/install keeps each tick cheap.
  if (-not (Get-ScheduledTask WindowsPilotUpdate -ErrorAction SilentlyContinue)) {
    $au = New-ScheduledTaskAction -Execute powershell.exe -Argument ('-NoProfile -NonInteractive -WindowStyle Hidden -Command ' + [char]39 + "`$env:PILOT_BIND='0.0.0.0'; Invoke-WebRequest 'http://192.168.50.1:8898/install.ps1' -UseBasicParsing -TimeoutSec 20 | Invoke-Expression" + [char]39)
    $tw2 = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 30)
    $su2 = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit 0 -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName WindowsPilotUpdate -Action $au -Trigger $tw2 -Principal $pr -Settings $su2 -Force | Out-Null
    P UPDATE_TASK_ON
  }
} catch { P ("TASK_EXC " + $_.Exception.Message); exit 0 }

New-Item -ItemType File -Force C:\prov.done | Out-Null
Unregister-ScheduledTask -TaskName ProvOnce -Confirm:$false -ErrorAction SilentlyContinue
P PROV_DONE
