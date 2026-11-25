# Changelog

## 0.1.9
- OCR side-car: `POST /api/screenshot` returns `words:[{text,x,y,w,h}]` (image-space px) alongside the JPEG via the built-in `Windows.Media.Ocr` engine — offline, deterministic, ~100 ms, no external round-trips. Covers self-drawn canvases (FL Studio) that expose almost nothing over UIA. Any OCR failure degrades to an empty list; the shot always succeeds. `ocr:false` opts out.
- `focus_window` act verb + `pilot_focus_window` MCP tool: force foreground by process name (optional title substring) with `SetForegroundWindow`+`BringWindowToTop`+`ShowWindow(RESTORE)` and a re-verify poll — replaces the click-around-the-edge dismiss dance when Narrator/Search overlays steal focus. Replies `{hwnd,focused,foreground_title}`.
- Shot-space clicks: `click` accepts `shot:true` and consumes screenshot-space px, inverse-mapping through the last shot's scale + source origin (`LastShotMap`) — clients no longer multiply by 1/scale themselves.
- Stale-ref auto-rebind: a dead ref gets one cheap re-find against the fresh top-window forest (automation_id/name/class + last-known rect proximity via `FingerprintDto.Rect`); the OLD ref keeps working on the fresh element. Only a missed rebind throws `stale_ref`, and the error carries the fingerprint.
- `wait_until` gains `ocr_text` (contains/regex against live OCR, whitespace-tolerant) and `pixel_stable` (two frames `for_ms` apart, diff ratio under threshold) — collapses fixed sleep chains into one bounded call.
- Rig: 35→39 assertions covering all four surfaces.

## 0.1.8
- File surface: `GET /api/files` (browse), `GET /api/file` (read; utf-8/utf-16 sniff with BOM strip, binary → base64, `max_chars` head-slice + `truncated` flag), `POST /api/file` / act verb `write_file` (writes text or b64, creates parent dirs, audited). Relative paths anchor on the pilot data dir. MCP parity: `pilot_list_files` / `pilot_read_file` / `pilot_write_file`. Console gets a Files tab (browse, preview, download, multi-upload). Rig 31→35 assertions.

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
