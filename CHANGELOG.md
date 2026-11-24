# Changelog

## 0.1.7
- `pilot_run` / act verb `run`: headless program execution with captured `{exit_code,stdout,stderr,ms}` — agents no longer need remote PowerShell or scheduled-task tricks for anything that doesn't touch the desktop. Goes through the same governed seam (arm + denylist + rate), audits as `pilot_run`, kills its process tree on `timeout_s` (default 30, max 600). Console Operator tab gets a Run button. Rig covers stdout capture + nonzero exit (31 assertions).

## 0.1.6
- REST: every route replies inside the `{ok,data}` / `{ok:false,error,msg,hint}` envelope (`/api/events`, `/api/log`, `/api/audit`, `/api/jobs`, `/api/job`, `/api/act` job handle, panic, config, arm/disarm). Clients unwrap once.
- REST: JSON-shaped 404 bodies (was empty `Results.NotFound()`); bad arm scope returns 400 with the accepted value list instead of an opaque 500.
- Shots are pruned to `audit.retain_days` (was: JPEG per verify-cycle forever on always-on guests).
- Audit-write failure no longer masks a successful mutation — logged, verify bundle kept.
- UIA queue: timed-out ops skip instead of executing late against dead refs (deadline travels with the queued item).
- Hotkey watchdog retries window creation and re-registers Ctrl+Alt+Pause after session reconnects.
- Input-health clock: `GetTickCount64` (old `GetTickCount` wrapped at ~49.7 days uptime).
- Startup resilience: token-store/auto-arm init failures reach pilot.log instead of the void; data-dir fallback to per-user temp.
- Console: blob leak fixed (frames revoked), one-slot frame coalescer, staleness tracked, WS reconnect with jittered backoff + instant re-link on wake/online; fetch timeout on every call.
- Tests: smoke + rig unwrap the shared envelope; rig adds server-side `wait` verb checks, jobs-list, envelope-uniformity, arm-TTL expiry (28 assertions).

## 0.1.5
- Input-lane health in `/api/status` (`input.ok` from the foreground-window lane), honest about what it can detect.

## 0.1.4
- Windowless GUI-subsystem build (WinExe) + crash-safe file logger to `%LOCALAPPDATA%\WindowsPilot\pilot.log`.

## 0.1.3
- Web console parity with the MCP surface; `/api/arm` replies inside the `{ok,data}` envelope.

## 0.1.1
- Live Screen tab fix (phash accumulation), instant paint on WS join.

## 0.1.0
- Initial: MCP + web console + REST mirror; governed PilotEngine seam; UIA3 (FlaUI 4) perception, SendInput actuation, GDI capture, set-of-marks vision, arm/panic governance, audit trail.
