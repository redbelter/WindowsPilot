# WindowsPilot

Windows UX automation exposed through **MCP** and a **web console**. A single
ASP.NET Core service runs inside an interactive Windows session (Session 1) and
serves three faces of the same engine:

- **`/mcp`** — MCP server (`pilot_*` tools) for agents
- **`/`** — zero-build web console (live screen, set-of-marks, inspector, act, audit)
- **`/api/*`** — REST mirror of every tool, bearer-token auth

All governance (arm scopes, kill switch, rate limits, denylists, audit) lives in
one in-process seam (`PilotEngine`), so **neither MCP nor REST can bypass it**.

## Architecture

```
MCP tools  ─┐
REST /api  ─┼─► PilotEngine ─► UiaService (STA UIA3 thread, FlaUI 4)
Web console┘        │          InputInjector (SendInput, kill-switch checks)
                    │          CaptureService (GDI/PrintWindow ladder)
                    │          Vision (set-of-marks overlay)
                    ├─► Governance: ArmState · KillSwitch · Guard · AuditLog
                    └─► Waiter (server-side wait conditions)
```

Key contracts:

- **Element refs** — UIA elements are opaque refs (`e17`); stale refs fail loudly
  with a fingerprint, never silently guess.
- **DPI contract** — process is PerMonitorV2; every frame carries
  `{width, height, origin, scale}`; device pixels everywhere on the wire.
- **Governance state machine** — `DISARMED → ARMED(read|act|drive)`; `drive`
  (human raw-input mode) is mutually exclusive with agent actuation.
  Panic: `Ctrl+Alt+Pause` hotkey or `/api/panic`.
- **Actionable-first UIA** — trees/find return elements with supported
  *wire* patterns (`invoke`, `value`, `toggle`, ...), normalized from FlaUI
  `PatternId.Name`.
- **Set-of-marks vision** — screenshots can carry numbered overlay boxes;
  `click_mark` maps a mark back to its element ref.
- **MCP errors** — tools return `CallToolResult` with `IsError` +
  `StructuredContent` (the SDK sanitizes thrown exceptions, so error taxonomy
  rides in the result, e.g. `stale_ref`, `not_armed`, `denied`, `input_dropped`).

## Build

```
dotnet publish src/Pilot -c Release -r win-x64 --self-contained true -o stage/pilot
```

Targets `net10.0-windows10.0.19041.0` (min `10.0.17763`). Dependencies:
FlaUI.Core/UIA3 4.0, ModelContextProtocol.AspNetCore 2.2, System.Drawing.Common.

## Deploy (must run in Session 1)

WindowsPilot needs an interactive desktop session — a Session-0 service is
UIA/SendInput blind. Register it as an **Interactive** scheduled task:

```powershell
# Launch the bare exe: the build is WinExe (GUI subsystem) and NEVER allocates
# a console; a `cmd /c` wrapper would pop a visible terminal on the desktop.
# Logs go to %LOCALAPPDATA%\WindowsPilot\pilot.log.
$a  = New-ScheduledTaskAction -Execute "C:\WindowsPilot\app\WindowsPilot.exe" -WorkingDirectory "C:\WindowsPilot\app"
$pr = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\<user>" -LogonType Interactive
# AtStartup alone misses autologon sessions (token not ready); the logon
# trigger + StartWhenAvailable + RestartCount make the pilot survive reboots.
$se = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -StartWhenAvailable -ExecutionTimeLimit 0 -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName WindowsPilot -Action $a -Principal $pr -Settings $se -Trigger @(New-ScheduledTaskTrigger -AtStartup; New-ScheduledTaskTrigger -AtLogOn)
```

Config: `%LOCALAPPDATA%\WindowsPilot\pilot.json` (snake_case — see
`config/pilot.example.json`). Tokens: `tokens.json` next to it
(`{"<token>": "ro|act|drive"}`); requests use `Authorization: Bearer <token>`
or `?token=<token>`.

## REST / MCP surface

| Route | Notes |
|---|---|
| `GET /api/status` | arm state, session id, version, uptime |
| `GET /api/windows` · `/api/foreground` | taskbar-semantic window list (EnumWindows) |
| `GET /api/tree?ref=` · `/api/find` · `/api/element?ref=` | actionable-first UIA |
| `POST /api/screenshot` | `marks: true` → set-of-marks overlay |
| `GET /api/shots/{id}` | captured JPEG |
| `POST /api/act` | single verb: click/type/set_value/keypress/scroll/check/uncheck/toggle/invoke/expand/collapse/select/focus/launch/wait/click_mark/arm/disarm/panic/hold_mouse/... |
| `POST /api/jobs` | `{steps:[...]}` sequential action scripts (kill-switch honored between steps) |
| `POST /api/wait` | server-side conditions: element/window/foreground/focused/text/pixel/action_settled |
| `GET /api/audit` | JSONL audit tail (`csv=1` for export) |
| `GET /ws/view` | live frame WebSocket |

See `windowspilot-design-v2.md` for the full v0.2 spec (schemas, threading,
governance, error taxonomy, performance budgets).

## Verified

End-to-end rig against a Hyper-V Server-2025 guest: **24/24** — status,
arm/act scopes, launch+find Notepad, UIA value write read back
(`HELLO-PILOT-42`), coordinate click, screenshot marks, `stale_ref`
self-heal, panic hotkey, MCP `tools/call` with structured errors, audit
trail, console serving.

## Portable smoke tests

`tests/windowspilot_smoke.py` runs against **any** machine where WindowsPilot is
listening — stdlib Python 3 only, no test framework, no Hyper-V coupling:

```
python tests/windowspilot_smoke.py --base http://<vm>:8791 [--token ***] [--only notepad,calculator]
```

* **health** — server answers, interactive session (not Session 0), act scope
  reachable (arms itself if disarmed).
* **notepad** — launches Notepad, finds the editor (modern `Document` or classic
  `Edit`), types a unique marker, reads it back through UIA text (not pixels),
  kills it and verifies the window is gone.
* **calculator** — launches Calculator (modern XAML app or classic `calc.exe` /
  `win32calc` on Server SKUs), computes 7×8 by **invoking UIA buttons**, and
  verifies the display reads **56** — reading numbers by regex so localized
  displays don't break it. Locates buttons by AutomationId first (language
  independent), falling back to name patterns for the classic app.

All three exercise the same governed seam the agents use (launch/type/click/
kill through `/api/act` jobs, findings through `/api/find`).

## Governance rig

`tests/windowspilot_rig.py` is the deeper contract check (24 assertions: arm
state machine, panic hotkey, verify bundles with before/after shots, audit
trail, coordinate clicks, MCP `tools/list` + `tools/call` with structured
errors, console serving). It needs a **rig-profile** target
(`PILOT_RIG=1` or `governance.allow_mcp_arm: true` in `pilot.json`) since it
exercises `/api/arm` directly:

```
python tests/windowspilot_rig.py --base http://<vm>:8791 --token ***
```
