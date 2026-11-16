Stop-ScheduledTask -TaskName WindowsPilot -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
taskkill /IM WindowsPilot.exe /F 2>&1 | Out-Null
Start-Sleep -Seconds 1
try { Remove-Item C:\WindowsPilot\app -Recurse -Force -ErrorAction Stop; "CLEANED" } catch { "CLEAN_FAIL " + $_.Exception.Message }
Invoke-WebRequest http://192.168.50.1:8898/pilot-payload.zip -OutFile C:\WindowsPilot\payload.zip -UseBasicParsing
"DL=" + (Get-Item C:\WindowsPilot\payload.zip).Length
Expand-Archive C:\WindowsPilot\payload.zip C:\WindowsPilot\app -Force
"EXTRACTED"
# re-point the task at the bare exe (old tasks wrap it in cmd /c, which pops a
# console window; new payload is WinExe-subsystem so that can't happen)
try {
  $hn = $env:COMPUTERNAME
  Unregister-ScheduledTask -TaskName WindowsPilot -Confirm:$false -ErrorAction SilentlyContinue
  $a = New-ScheduledTaskAction -Execute "C:\WindowsPilot\app\WindowsPilot.exe" -WorkingDirectory "C:\WindowsPilot\app"
  $pr = New-ScheduledTaskPrincipal -UserId ($hn + "\Sandbox") -LogonType Interactive
  # reboot-resilient: AtStartup fires before the autologon token exists, so
  # add AtLogOn + StartWhenAvailable + restarts (lesson from 0.1.4: without
  # these the pilot never came back after a guest reboot).
  $se = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit 0 -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
  $t = @(New-ScheduledTaskTrigger -AtStartup; New-ScheduledTaskTrigger -AtLogOn)
  Register-ScheduledTask -TaskName WindowsPilot -Action $a -Principal $pr -Settings $se -Trigger $t | Out-Null
  "TASK_REPOINTED"
} catch { "TASK_EXC " + $_.Exception.Message }
Start-ScheduledTask -TaskName WindowsPilot
"STARTED"
