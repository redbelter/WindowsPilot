$ErrorActionPreference = "Continue"
$log = "C:\prov.log"
function P($m) { ("{0} {1}" -f (Get-Date -Format "HH:mm:ss"), $m) | Add-Content $log }
if (Test-Path C:\prov.done) { Unregister-ScheduledTask -TaskName ProvOnce -Confirm:$false -ErrorAction SilentlyContinue; exit 0 }
P "START boot=$(((Get-Date)-(Get-CimInstance Win32_OperatingSystem).LastBootUpTime).TotalSeconds.ToString('F0')+'s')"

# 1) network
New-NetIPAddress -InterfaceAlias "Ethernet" -IPAddress 192.168.50.20 -PrefixLength 24 -ErrorAction SilentlyContinue | Out-Null
$ip = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object PrefixOrigin -eq "Manual" | Select-Object -First 1).IPAddress
P "IP=$ip"

# 2) stop the eval killer
try { $r = Invoke-CimMethod -ClassName SoftwareLicensingService -MethodName Rearm; P "REARM=$($r.ReturnValue)" } catch { P "REARM_EXC $($_.Exception.Message)" }
try { Set-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Services\wlms Start -Value 4 -Force; P "WLMS_OFF" } catch { P "WLMS_EXC $($_.Exception.Message)" }
try {
  if (-not (Get-ScheduledTask WlmsWatchdog -ErrorAction SilentlyContinue)) {
    $a = New-ScheduledTaskAction -Execute "taskkill.exe" -Argument "/IM wlms.exe /F"
    $t = New-ScheduledTaskTrigger -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration (New-TimeSpan -Days 9999) -At (Get-Date)
    $p = New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName WlmsWatchdog -Action $a -Trigger $t -Principal $p -Force | Out-Null
  }
  Start-ScheduledTask -TaskName WlmsWatchdog -ErrorAction SilentlyContinue
  P "DOG=on"
} catch { P "DOG_EXC $($_.Exception.Message)" }
taskkill /IM wlms.exe /F 2>$null | Out-Null

# 3) quiet the update orchestrator during provisioning
try { Stop-Service UsoSvc -Force -ErrorAction SilentlyContinue; Set-Service UsoSvc -StartupType Manual -ErrorAction SilentlyContinue; P "USO=off" } catch { P "USO_EXC" }

# 4) payload
New-Item -ItemType Directory -Force C:\WindowsPilot | Out-Null
try {
  [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072
  Invoke-WebRequest http://192.168.50.1:18888/pilot-payload.zip -OutFile C:\WindowsPilot\payload.zip -UseBasicParsing
  P "DL=$((Get-Item C:\WindowsPilot\payload.zip).Length)"
} catch { P "DL_EXC $($_.Exception.Message)"; P "PROVISION_FAILED"; exit 1 }
Expand-Archive C:\WindowsPilot\payload.zip C:\WindowsPilot\app -Force
P "EXTRACTED"

# 5) config + tokens
$cfg = @{ bind="0.0.0.0"; port=8791; governance=@{ allow_mcp_arm=$true; auto_arm="startup"; rate_per_min=300; rate_burst=60 }; capture=@{ prefer="gdi" }; rig=@{ profile=$true } } | ConvertTo-Json -Depth 5
$dir = "C:\Users\Sandbox\AppData\Local\WindowsPilot"
New-Item -ItemType Directory -Force $dir | Out-Null
Set-Content -Path (Join-Path $dir "pilot.json") -Value $cfg -Encoding UTF8
Set-Content -Path (Join-Path $dir "tokens.json") -Value (ConvertTo-Json @{ "act-rig-1" = "act"; "ro-rig-1" = "ro"; "drive-rig-1" = "drive" }) -Encoding ASCII
P "CFG_WRITTEN"

# 6) session-1 task with boot trigger (survives reboots)
$hn = $env:COMPUTERNAME
Unregister-ScheduledTask -TaskName WindowsPilot -Confirm:$false -ErrorAction SilentlyContinue
$act = New-ScheduledTaskAction -Execute "cmd.exe" -Argument "/c C:\WindowsPilot\app\WindowsPilot.exe >> C:\WindowsPilot\pilot.log 2>&1" -WorkingDirectory "C:\WindowsPilot\app"
$pri = New-ScheduledTaskPrincipal -UserId ($hn + "\Sandbox") -LogonType Interactive
$set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit 0 -MultipleInstances IgnoreNew
$t1 = New-ScheduledTaskTrigger -AtStartup
Register-ScheduledTask -TaskName WindowsPilot -Action $act -Principal $pri -Settings $set -Trigger $t1 | Out-Null
Start-ScheduledTask -TaskName WindowsPilot
P "PILOT=started"
# self-cleanup: provisioning complete -> remove the boot-repeaters
Unregister-ScheduledTask -TaskName ProvOnce -Confirm:$false -ErrorAction SilentlyContinue
P "PROVISION_DONE"
