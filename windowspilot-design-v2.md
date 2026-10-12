# WindowsPilot — Full System Design
### Windows UX automation exposed through MCP + Web UI — v0.2 (thorough) — 2026-10-11

> One process per target Windows machine. It sees (UIA tree + screenshots), acts
> (SendInput + control patterns), is driven by LLM agents over **MCP at `/mcp`**,
> and by humans over a **same-origin web console at `/`** — with governance
> (arm state, kill switch, audit) enforced once, at the shared backend seam.
> The architecture is a deliberate clone of the verified HermesHyperV product
> (`:5003`): ASP.NET Core minimal-API service + `ModelContextProtocol` C# SDK
> (`AddMcpServer().WithHttpTransport().WithTools<T>()` + `MapMcp("/mcp")`) +
> `wwwroot/` static SPA + `/api/jobs` async jobs + `/api/log?since=N` activity log.

---

## Table of contents
1. Verified ground truth
2. Goals / non-goals
3. Architecture
4. Capability layer (perceive / act / see)
5. Element registry (the ref system)
6. Coordinate & DPI contract
7. Hybrid vision: set-of-marks
8. Governance (arm, kill switch, audit, guardrails)
9. Concurrency & threading model
10. MCP tool surface — full schemas & payloads
11. REST/WS surface — full route table & wire formats
12. Web console UX spec
13. Security model
14. Session placement, install & lifecycle
15. Configuration reference
16. Error taxonomy
17. Wait/event system
18. Performance budgets
19. Test rig on the :5003 hyperv MCP (full suite)
20. Build plan & exit criteria
21. Alternatives considered & rejected
22. Future: fleet, recording, RDP backend
23. Risks & known walls

---

## 1. Verified ground truth (probed 2026-10-11, nothing below is guessed)

| # | Fact | Evidence |
|---|------|----------|
| G1 | HermesHyperV = one LocalSystem service exposing MCP+REST+static UI on :5003, zero windows | `C:\Users\red\tools\hyperv-svc\src\HermesHyperV\Program.cs` (44 lines), `/api/status` ok |
| G2 | MCP streamable-HTTP at `/mcp`, 11 tools, C# `ModelContextProtocol` SDK | raw JSON-RPC `tools/list` → create_vm, run_ps, wait_ready, … |
| G3 | Dashboard JS polls `/api/vms,/api/status,/api/jobs,/api/log`; POSTs spawn jobs returning `{job:id}` | `Api/ApiEndpoints.cs`, `Engine/JobStore.cs` |
| G4 | `/ws/term` WebSocket bridges browser xterm.js ⇄ guest; "you can reach the port" is the whole auth story (loopback bind) | `Api/TerminalWs.cs` |
| G5 | `run_ps` executes inside a *Windows* guest with **exit code + stdout** (`UI-RUN-OK`), via PowerShell Direct — no guest network/WinRM needed | job `9298a3dc`, status done, exit 0 |
| G6 | Fresh Win11 EE VM from golden cache in ~20 s (`create_vm` job on `mcp-sandbox-63614`); guest auto-login `Sandbox/Sandbox!2345` | `/api/jobs` result |
| G7 | Guests on internal NAT `192.168.50.0/24`, host = `.1`; host reaches any guest port directly | `mcp-lnx1` at 192.168.50.10 |
| G8 | `wait_ready` polls FirstLogonCommands marker = "desktop genuinely usable" (not just kernel-up) | tool description, live |
| G9 | Sandbox guest egress proxy exists at :8899 (`SandboxProxy`, HTTP/CONNECT) | Program.cs line 29 |
| G10 | UIA-over-pythonnet (FlaUI v4) already proven on this machine; STA-thread discipline documented as a hard lesson | `desktop-automation-framework` skill |

**Implication:** the entire build-test loop — provision a disposable Windows desktop,
deploy code into it, run it, drive it as a network client, wipe it — already exists and
costs ~20 s + ~2–4 min boot per cycle, and never touches the operator's machine.

## 2. Goals / non-goals

**Goals**
- G-1 An LLM (Hermes, Claude, any MCP host) can complete real desktop tasks: edit files in
  Notepad, navigate Explorer, fill forms in a Chromium app, draw in mPaint by coordinates.
- G-2 A human can watch the same session live in a browser, inspect the element tree the
  agent sees, and take manual control (drive mode) or veto the agent instantly.
- G-3 Every mutation is governed: nothing injects input unless armed; everything is audited;
  a kill switch works even when the agent loop is wedged.
- G-4 Testable end-to-end in disposable VMs via the existing hyperv MCP; CI-able assertions.
- G-5 One codebase, one seam — web UI and MCP cannot diverge in behavior.

**Non-goals (v1)** — secure-desktop/UAC interaction (§23), anti-cheat/overlapped-window
apps that ignore SendInput, audio/video input, multi-machine fleet (P3), clipboard
synchronization beyond a guarded paste helper, remote access across untrusted networks
(loopback/LAN trust model like hyperv, plus tokens).

## 3. Architecture

```
                     ┌─────────────────────────────────────────────────────────────┐
                     │ TARGET WINDOWS DESKTOP (Session 1, user "Sandbox"/real user) │
                     │                                                             │
  Hermes / Claude ──▶│ :8791/mcp ──┐   PilotTools [McpServerToolType]              │
  (MCP client)      │             ├──▶ ┌──────────────────────────────┐            │
                     │ :8791/api/* ─┘   │        GovernedBackend       │            │
  Browser (human) ──▶│ :8791/  (SPA)    │  ┌────────────────────────┐  │            │
  dashboard.html     │ :8791/ws/*       │  │ arm state machine      │  │            │
                     │ :8791/events     │  │ kill switch (3 paths)  │  │            │
                     │                  │  │ audit JSONL + shots    │  │            │
                     │                  │  │ rate limiter, denylist │  │            │
                     │                  │  └──────────┬─────────────┘  │            │
                     │                  │      ┌──────┴───────┐        │            │
                     │                  │      ▼              ▼        │            │
                     │                  │  UIA3 engine   InputInjector │ ScreenBridge│
                     │                  │  (FlaUI, STA   (SendInput,   │ (WGC →      │
                     │                  │   thread +      global lock) │  PrintWin → │
                     │                  │  ElementRegistry)            │   GDI)      │
                     │                  └──────────────────────────────┘            │
                     │  installed as: logon Scheduled Task (never Session-0 svc)   │
                     └─────────────────────────────────────────────────────────────┘
```

**One seam rule (load-bearing).** Every capability is a plain method on
`PilotEngine`. `[McpServerTool]` wrappers in `PilotTools` and minimal-API lambdas in
`PilotApi` are *adapters only* — they parse arguments and call the same method.
Governance is a decorator applied *inside* the seam (`EngineAct()` never bypasses
`Guard.Check()`), so an MCP host and the web UI are physically incapable of different
behavior. This is exactly how HermesHyperV's `run_ps` serves both hosts (G2/G3), and
it is the fix for the #1 gap found in the UIStalker/Deskhand analysis (governance
wasn't at the seam).

**Process shape (clone of the proven 44-line Program.cs):**
```csharp
var builder = WebApplication.CreateBuilder(...);          // content root pinned (task-launch safe)
builder.WebHost.UseUrls($"http://+:{port}");              // LAN bind needed for host→guest dev
// token auth middleware (hyperv skipped it: loopback+disposable; we cannot)
builder.Services.AddSingleton<PilotSettings>();
builder.Services.AddSingleton<PilotEngine>();             // the seam
builder.Services.AddSingleton<JobStore>();                // verbatim reuse of hyperv JobStore shape
builder.Services.AddSingleton<EventHub>();                // ring 500 + SSE
builder.Services.AddHostedService<FrameStreamService>();  // WGC capture loop
builder.Services.AddMcpServer().WithHttpTransport().WithTools<PilotTools>();
var app = builder.Build();
app.UseStaticFiles();                                     // wwwroot/console.html
app.MapPilotApi();                                        // /api/* mirrors tools
app.MapViewWs();                                          // /ws/view  frame stream
app.MapDriveWs();                                         // /ws/drive input channel
app.MapEventsSse();                                       // /events   SSE (Safari/enterprise proxy fallback)
app.MapMcp("/mcp");
await app.RunAsync();
```
Project layout mirrors HermesHyperV so both products share one house style:
```
WindowsPilot/
  src/Pilot/
    Program.cs
    Api/PilotApi.cs  ViewWs.cs  DriveWs.cs  EventsSse.cs
    Engine/PilotEngine.cs  Uia/ (TreeWalker, Registry, Patterns, FindQuery)
           Input/ (SendInput, VkMap, Chords)  Capture/ (Wgc, PrintWindow, Gdi, Jpeg)
           Vision/ (Marks, Diff)  Governance/ (ArmState, KillSwitch, Audit, Denylist, Redact)
    Tools/PilotTools.cs            # [McpServerTool] adapters only
    wwwroot/ console.html  app.js  marks.js  xterm vendored? (no — same zero-build DNA)
    selftest/ Suite.cs             # T-xx assertions, runnable headless inside guest
  scripts/rig/ provision.sh deploy.sh teardown.sh   # calls :5003 MCP (see §19)
```

## 4. Capability layer

### 4.1 Perceive — UIA3 engine
- **Binding:** FlaUI.Core v4 in-process (.NET 8). (pythonnet path proven in G10 but kept
  as fallback only — in-process avoids the STA cross-runspace tax.)
- **Per-call timeout** (default 5 s): UIA calls on hung apps can block indefinitely;
  walker runs on the dedicated UIA thread (G10), timeout abandons the task, marks the
  provider process `sluggish` (surfaced in `pilot_status`), returns `timeout` error.
  Never `Task.WaitAll` on the UIA thread itself (self-deadlock).
- **Tree walk policy:** breadth-capped (`max_nodes`, default 2000, hard cap 20 000),
  depth-capped (default 25), per-node cost-capped (total budget ms, default 2000 —
  stops gracefully, returns `truncated: true` + `cursor` to continue with
  `expand(parent_ref)`). Lazy by default: `get_tree(hwnd)` returns depth-1 children;
  the LLM expands what it cares about. (Full-desktop walks on a Chrome window = 50k+
  nodes and multiple seconds — this policy is what keeps it usable.)
- **Per-node fields:** `ref, name, automation_id, class_name, control_type, enabled,
  offscreen, rect{x,y,w,h} (physical px, virtual-screen space), patterns[]
  (supported subset of invoke,value,toggle,expand,select,scroll,rangevalue,transform,
  text), actionable (bool), text_snippet (first 80 chars if ValuePattern/TextPattern)`.
- **Filters:** `find_elements({name_regex, name_contains, automation_id, class_name,
  control_type, process, hwnd, max})` server-side; LLM never paginates a raw tree.

### 4.2 Act — Input injector
- **Only** `SendInput` (no SetCursorPos+mouse_event splits — event ordering races).
- Mouse: `move(x,y)` absolute; `down/up(button)` separate verbs (→ drag =
  move→down→move…→up; the *only* primitive that works on canvas apps);
  `click(x,y,button,clicks)`; `scroll(x,y,delta_y,delta_x)`; capture flag
  (`MOUSEEVENTF_ABSOLUTE|MOVE`) normalized over the **virtual screen**:
  `nx = X*65535/(virtualW-1)` with `virtualW` recomputed per call (monitors change).
- Keyboard: `press(chord)` — chord grammar `ctrl+alt+t`, `f5`, `win+r`, sequence form
  `ctrl+s,ctrl+w` (comma-separated, sent with 25 ms inter-key delay, configurable);
  VK map generated from `VirtualKey` enum + Shift/AltGraph layout resolution via
  `ToUnicode`/`VkKeyScanEx` against the *foreground* keyboard layout.
- `type_text(text, mode)`: modes `auto|keys|unicode|paste`:
  `keys` = SendInput scancodes (fast, most apps); `unicode` = per-char via
  `KEYEVENTF_UNICODE` (punctuation-safe); `paste` = clipboard set + `ctrl+v`
  (fastest for >200 chars; **clipboard restore-after** + `IsPassword`-property
  elements auto-refuse paste, `value_set` used instead — a password box never
  receives our clipboard).
- **Global input lock:** one async mutex serializes all injectors (agent actions,
  drive mode, human pattern clicks) — SendInput is process-global, interleaved
  injections = scrambled gestures. Holders tagged `agent|drive|human-ui` for audit.

### 4.3 See — Screen bridge
Tier ladder (per monitor): **WGC** (`Windows.Graphics.Capture`, per-monitor
`Direct3D11CaptureFramePool`, correct on GPU-composited/DWM-clipped windows, no black
frames) → **PrintWindow** with `PW_RENDERFULLCONTENT|PW_CLIENTONLY?` (per-window,
works minimized-ish) → **GDI BitBlt** of `GetDC(NULL)` (last resort; black on some
DRM/hardware overlays). Tier used is reported (`capture_tier: wgc|printwindow|gdi`) —
silent tier downgrade has burned every desktop-automation project ever.
- `capture_screen(monitor=0, region?)`, `capture_window(hwnd)`, `capture_element(ref)`
  (rect crop of owning monitor — WGC-per-element is a later optimization).
- Encode: JPEG q70 via `System.Windows.Media.Imaging` or `Magick.NET`-free path
  (`SixLabors.ImageSharp`); LLM default longest-edge **1568 px** (vision-model sweet
  spot), q configurable. **Every** returned image carries
  `{width, height, source_origin{x,y}, scale, monitor, capture_tier}` — the
  inverse-transform for vision-picked coords (§6).
- **Frame stream:** dedicated capture thread feeds a `Latest<T>` slot (drop-oldest,
  never queue); `/ws/view` sends JPEG binary frames with a 1-frame lookahead; rate
  2 fps idle / 6 fps while acting or driving / burst `capture_burst(n)` for diffs.
  Idle CPU target ≤5 % (encoder on 960×540 base + client upscales; full-res only on
  demand). Change detection: 64×36 grayscale downsample, hash diff < 0.5 % ⇒ hold last
  frame (browser shows "still").

## 5. Element registry (the ref system)

Every element handed to any host is a **ref**, never a pointer.

```
ref = "e-" + base32(shortid)          e.g. "e-4kq7zx"
Entry { Id, Element (strong, per-ref TTL), Fingerprint, Created, LastHit, Hwnd }
Fingerprint {                       # re-resolution hint when stale
  hwnd, control_type, name, automation_id, class_name,
  path: [ (control_type, index_among_siblings_same_type), ... ]   # ≤6 levels deep
}
```
- Store: `ConcurrentDictionary`, sweep every 30 s, TTL 10 min idle (LLM turns are
  minutes apart; one turn must survive a tree walk + 10 clicks without re-find).
- **Stale handling:** UIA throws `COMException` on dead refs; registry catches →
  returns `{error:"stale_ref", hint:"re-run pilot_find_elements", was:<fingerprint>}`.
  The fingerprint is deliberately returned: `find_elements(name_regex: was.name)` +
  path-index disambiguation restores the element in one cheap call. (Worse systems
  silently re-resolve and can *act on the wrong element* — we refuse to, by design.)
- Window refs (`w-`) resolve to hwnd and validate `IsWindow`; app refs (`p-`) to pid.
- Registry is per-process; a WindowsPilot restart invalidates refs (documented, and
  `pilot_status` returns `uptime`/`generation` — bumping `generation` tells a
  long-running agent loop "all your refs are dead, re-observe").

## 6. Coordinate & DPI contract (where every automation project dies)

1. Process is **Per-Monitor-V2 DPI aware** (`SetProcessDpiAwarenessContext
   (DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)`, called *first thing* in
   `Main`, before any window/UIA/capture call — G10 lesson).
2. All coordinates on the wire = **physical pixels, virtual-screen space**
   (top-left of primary monitor = (0,0); negative on extended-left setups — rect
   fields carry the sign honestly).
3. Screenshots define their own frame: response always includes
   `{source_origin, scale}`. A vision model picks pixel `(px,py)` in *our returned
   image*; server computes `X = ox + px/scale` before injection. The web overlay
   uses the identical transform (same math, one shared table).
4. SendInput normalization per §4.2 over current virtual-screen bounds, recomputed
   per call (hot-plug, resolution flip).
5. ElementFromPoint is fed physical coords — valid precisely because of (1).
6. Web console: browser sends CSS px → JS applies `devicePixelRatio` + canvas
   letterbox offset *in the browser* (documented in marks.js header) — never trust
   the browser alone, server re-runs its own transform from the frame header it
   emitted (double transform audit in T-13: draw in mPaint at 200 % DPI, assert pixel).

## 7. Hybrid vision — set-of-marks

`pilot_screenshot(marks:true)` pipeline:
```
UIA snapshot (actionable elements, current foreground window ∪ desktop level,
   ≤ MAX_MARKS=150) → NMS dedupe (drop elements fully covered by an actionable
   ancestor; prefer deepest interactive) → draw index boxes (2 px, #f43f5e,
   label chip top-left, alternating offset to avoid label collisions)
   → return {image, scale, transform, marks:[{n, ref, rect, control_type, name≤40}]}
```
- Agent clicks mark *n* → resolves to `e-…` ref → **pattern-first action if available,
  else click at rect center** (never "pixel trust": the mark box is a pointer to a ref,
  not a coordinate).
- Canvas UIs (Discord-style, G10 lesson from the user's own environment): tree
  collapses to ~0 actionable nodes → `marks` returns empty → screenshot degrades to
  plain + `hint:"no_uia_marks, use coordinate clicks"` — coordinate path already
  exists, so the vision loop keeps working.
- `clicks_at` batch: `pilot_click(marks:[1,4,9], delay_ms:30)` for sequences the
  model is confident about (halves round-trips; verification returns the *last* shot
  + a compact per-step action ack).

## 8. Governance

### 8.1 Arm state machine
```
            arm(scope,ttl)                    expire / ttl           panic
 DISARMED ───────────────────▶ ARMED(read+act) ─────────────▶ DISARMED ◀── (instant, from anywhere)
    ▲   │ DRIFT_DETECTED(human input observed while agent mid-batch)
    └───┴──▶ PAUSED (agent actions queue; human owns input; resume button)
 ARMED(drive)     — mutually exclusive with ARMED(read+act); token handoff
```
- Who may arm: web UI button (default) **or** MCP `pilot_arm` *only if*
  `governance.allow_mcp_arm:true` (test rigs set true in config — the rig is
  scripted and has no human; §19). Arm scopes: `read` (implicit, always),
  `act` (injects + patterns), `drive` (browser ⇄ input), each with TTL
  (default 15 min, re-arm cheap).
- **Drift detection:** input monitor (low-level LL hook, read-only) distinguishes
  injected (`LLMHF_INJECTED`, free) vs human events; human input arriving mid-agent-
  batch ⇒ PAUSED + event `agent_paused_by_human` (prevents elbow-fighting and it is
  the humane thing).

### 8.2 Kill switch (three independent paths — any one suffices)
1. Web console: big **STOP** (button disabled only by the browser being down).
2. MCP `pilot_panic` (agent can panic itself on confusion).
3. **Hotkey in-session:** `Ctrl+Alt+Pause` (RegisterHotKey on a hidden message-only
   window; Pause key = nobody presses it by accident).
Effect (deterministic, <100 ms, asserted in T-09): disarm → input mutex poisoned so
queued injections are dropped without executing → running action loop gets a
cancelled CancellationToken *checked between every SendInput event* (not just
between tool calls) → toast + `audit: PANIC` line.

### 8.3 Audit
JSONL, append-only, `%LOCALAPPDATA%\WindowsPilot\audit\audit-YYYYMMDD.jsonl` (rotated
daily, `audit_retain_days: 30`):
```json
{"ts":"2026-10-11T14:22:31.842-07:00","seq":4181,"host":"mcp|web","tok":"act",
 "tool":"pilot_type_text","args_red":{"text":"[REDACTED len 12]","ref":"e-4kq7zx"},
 "target":{"hwnd":"0x10428","proc":"notepad.exe","el":"Edit1/Document/Text"},
 "guard":{"armed":"act","deny":"pass","rate":"12/60"},
 "res":{"ok":true,"code":null,"ms":212,"after_shot":"shots/4181-a.jpg"}}
```
- Redaction: any arg named `*text*/*value*/*keys*` is length-only in the log when the
  target element has `IsPassword` or `deny.redact_text:true` (default true — full text
  audit is opt-in). Shots referenced by id, never inlined.
- `GET /api/audit?since_seq&tool&host&limit` tails it; `pilot_get_audit` mirrors it.
- Replay: `POST /api/replay {seq}` re-runs a logged action **in dry-run** (resolves
  fingerprint, shows what *would* happen; re-execution requires an explicit
  `execute:true` + fresh arm).

### 8.4 Guardrails
- **Denylist** (pattern+coordinate actions refuse, error `denied_app`): foreground
  process matches `consent.exe, UserAccountBroker, CredentialDialogHost,
  Windows.Security.Credentials.UI.UserConsentVerifier` (UAC family — the secure-desktop
  wall surfaced honestly, §23), plus user config globs. Read-only tools never deny.
- Rate limiter: token bucket on mutation tools (default 60 acts/min burst 20; 429-style
  `rate_limited` with `retry_after_s`).
- Supervised mode (`governance.supervised:true`): every mutation returns first as a
  `pending_approval` job; approve/deny in the Operator tab (the human-in-the-loop demo
  mode; off by default in test rig, on by default in real-user installs).
- Capability flags: `pilot_status` lists enabled tools — future RDP backend /
  secure-desktop capture appear as flags, agents adapt without tool-name churn.

## 9. Concurrency & threading

| Thread | Lives | Rules |
|---|---|---|
| `uia` | dedicated **STA** thread + work queue | every FlaUI call runs here with 5 s per-op timeout (G10: STA discipline non-negotiable) |
| `input` | dedicated thread owning the input mutex | SendInput sequence emitted event-by-event with panic-check between events |
| `capture` | dedicated thread, own D3D11 device + message pump | WGC frame pool bound here; never on `uia` (re-entrancy deadlock history) |
| `events` | hosted service | UIA focus-change event handlers (UIA-side threadpool) **enqueue only**, never call back into `uia` synchronously |
| pool | Kestrel/async | JSON, jobs (JobStore `Spawn` pattern verbatim from G3) |

- All public engine methods are async and safe to call concurrently; mutation
  serialization comes from the input mutex + per-foreground-window action lock
  (two agents acting on the same window serialize; different windows overlap).
- `generation` counter: every engine restart (crash-watchdog) bumps it; stale-agent
  detection is one comparison.

## 10. MCP tool surface — full schemas

Conventions: refs are strings (`e-/w-/p-`); rects `{x,y,w,h}` physical px;
actions return a **verification bundle** unless `verify:false`:
```json
{"ok":true,"ms":212,"before_shot":"…","after_shot":"…","shot_scale":0.5,
 "tree_delta":[{"op":"+","el":{...}},{"op":"~","ref":"e-…","changed":["name"]}]}
```
`tree_delta` = shallow diff (≤30 items) of foreground window's actionable set,
computed server-side on the `uia` thread — this is the agent's grounding signal,
and it is the single biggest reason agent loops stop going blind.

```
── read ────────────────────────────────────────────────────────────────
pilot_status()
  → {ok, version, uptime_s, generation, armed:"read|act|drive",
     session:{name:"RDP-Tcp#0|Console",id:1,desktop:"Default"},
     monitors:[{index,x,y,w,h,dpi,scale_tier}], capture_tier:"wgc",
     enabled_tools:[...], pending_approval:0}
pilot_get_windows({minimized?:false})
  → {windows:[{ref:"w-…",hwnd,title,process,pid,fg,is_maximized,rect}]}
pilot_get_foreground() → same row + {focused:"e-…"}
pilot_get_tree({ref?|hwnd?, depth:1, max_nodes:2000, budget_ms:2000})
  → {root:"w-…",nodes:[{ref,name,automation_id,class_name,control_type,enabled,
     offscreen,rect,patterns,actionable,text_snippet}],truncated,cursor:"e-parent"}
pilot_find_elements({name_regex?,name_contains?,automation_id?,class_name?,
     control_type?,process?,hwnd?,max:50}) → {elements:[node…],scanned_ms}
pilot_get_element({ref}) → node + {resolvable:true}
pilot_get_properties({ref}) → full UIA property dict (raw names, one level deep)
pilot_element_at({x,y}) → node  (hit-test, physical coords)
pilot_screenshot({monitor:0, region?, marks:true, max_edge:1568, quality:70})
  → {image_b64|image_url, width,height,scale,source_origin,monitor,capture_tier,
     marks:[{n,ref,rect,control_type,name}]}
   # image_url preferred when caller is HTTP-reachable (returns absolute URL — one
   # fewer 500 KB base64 through the model context)
pilot_get_events({since:0,max:100}) → {events:[{n,t,type,detail…}],n}  (JobStore.LogSince shape, G3)
pilot_wait_for({condition, ...}) → §17
pilot_get_audit({since_seq?,tool?,host?,limit:50}) → audit tail
── act (all gated: armed≥act; all audited; all return verification bundle) ──
pilot_click({ref?|x?,y?, button:"left",clicks:1, mark?, verify:true})
pilot_drag({ref?|x1,y1,x2,y2, steps:12, hold_ms:120, duration_ms:400})
pilot_scroll({ref?|x?,y?, delta_y, delta_x:0, verify:false})
pilot_type_text({text, ref?, mode:"auto|keys|unicode|paste", verify:"auto"})
   # ref given → SetFocus first (pattern SetValue preferred for editable value
   #   elements; keys mode when only TextPattern exists)
pilot_press_keys({keys:"ctrl+alt+t", repeat:1, verify:false})
pilot_set_value({ref, value, verify:"auto"})     # ValuePattern path, no focus steal
pilot_invoke({ref}) / pilot_toggle({ref}) / pilot_expand({ref, expand:true})
pilot_select({ref, parent_ref?})                 # SelectionItemPattern
── lifecycle/app ──────────────────────────────────────────────────────
pilot_launch_app({cmd, args?, workdir?, wait_for_window_ms:8000})
  → {app:"p-…",window:"w-…"}   # CreateProcess in-session; foreground-wait built in
pilot_kill_app({ref|pid|process_name})           # refuses denylist, audited
pilot_arm({scope:"act", ttl_min:15, why})         # gated by config.allow_mcp_arm
pilot_panic() → disarm-all, drains queue         # §8.2, always allowed
```

Docstrings on every tool carry *when to use* (the `wait_ready` description in G2 is the
house style — "THIS is 'the VM is ready'"). Tool descriptions are the API contract for
LLMs; we spend prose there on purpose, e.g. `click`:
> "Prefer ref (pattern-first) over x,y. Refs die when UI rebuilds — a `stale_ref`
> error means re-run find_elements with `was` fingerprint, then retry. For canvas UIs
> (marks empty), x,y coords from the returned image scale."

## 11. REST/WS surface (web console & any REST-ophile)

Mirrors the hyperv shapes exactly so the same dashboard-JS muscle memory applies (G3):

```
GET  /api/status            → {ok,data:{…pilot_status…}}             4 s cache (VmsCache pattern)
GET  /api/windows           → {ok,data:{windows:[…]}}
GET  /api/tree?ref=&depth=  → {ok,data:{nodes:[…]}}
GET  /api/element?ref=      → {ok,data:node}
POST /api/find {query}      → {ok,data:{elements}}
POST /api/act {tool,args}   → {job:"a3f9…"}  (single mutation entry-point; tool ∈
                            click|drag|scroll|type|keys|value|invoke|toggle|expand|
                            select|launch|kill) — job result = verification bundle
POST /api/arm {scope,ttl}   → {ok,data:{armed,expires}}
POST /api/panic {}          → {ok}  (no auth arm needed — panic is auth-free like hyperv purge is loud)
GET  /api/jobs              → {jobs:[{id,name,status,started,result,error}]}   (verbatim)
GET  /api/log?since=N       → {log:[{n,t,m,k}],n}                     (verbatim, 300-line ring)
GET  /api/audit?since_seq=&limit=  → {items:[…],n}
GET  /api/shots/<id>.jpg    → audit/verification screenshots
GET  /api/config            → redacted effective config (paths, ports, flags — never tokens)
WS   /ws/view?fps=&q=       → binary frames; JSON ctrl: {type:"hdr",scale,origin,seq}
                              {type:"marks",marks:[…]}; client can {cmd:"pause"}
WS   /ws/drive?token=DR     → uplink: {k:"move"|"down"|"up"|"wheel"|"key",…}
                              (browser-side DPI transform, §6.6); server grants ONLY
                              while ARMED(drive) and holds the input mutex; any
                              agent arm attempt while driving → refused `drive_locked`
SSE  /events?since=N        → EventHub fan-out (same seq space as /api/log)
```

Everything the web UI can do is the same engine call the MCP tool makes; the UI never
gets a privileged path — the only asymmetry is *authentication* (§13).

## 12. Web console UX spec (single page, zero build step — hyperv `wwwroot` DNA, G1/G4)

```
┌────────────────────────────────────────────────────────────────────────────┐
│ ● Console  [Sandbox@WIN-DEV]  armed:act ⏱14:32  tier:wgc  [STOP■] [ARM▾]  │ ← pinned bar
├──────────────┬─────────────────────────────────────────────────────────────┤
│ TABS         │  (content)                                                  │
│ Screen       │                                                             │
│ Inspect      │                                                             │
│ Operator     │                                                             │
│ Audit        │                                                             │
│ Target       │                                                             │
├──────────────┴─────────────────────────────────────────────────────────────┤
│ activity log (polls /api/log?since=N — verbatim hyperv pill pattern)       │
└────────────────────────────────────────────────────────────────────────────┘
```

**Screen tab.** `<canvas>` frame stream; overlay canvas on top draws marks (toggle),
agent-action ghosts (last action's rect flashes red/green by result for 2 s),
selection (from Inspect hover). **DRIVE** toggle → button turns orange, cursor
hidden, pointer+key events → `/ws/drive`; agent arms refused while driving;
`Esc Esc` or clicking out releases drive. Drag-from-canvas = `pilot_drag` preview
rubber-band (drive off) → sends real drag on release (tiny "test drag" affordance).

**Inspect tab.** Left: virtualized lazy tree (same `/api/tree` paging; ~2 ms row
render via DOM recycling; icons per control type). Center-right: properties table
(raw UIA props, pattern buttons enabled per `patterns[]`: invoke/toggle/expand/
value-input). **Hover-sync:** mouse over tree row → highlight rect on Screen tab;
`crosshair` button → next canvas click pick-activates `pilot_element_at` → tree
scrolls to that node. Every row shows ref (`e-…`, click-to-copy) — refs are *shareable
across tabs, agents, audit lines*: the same currency everywhere.

**Operator tab.** The trust tab. Timeline of agent turns (grouped by
`host:"mcp"` + session hint): each entry = tool, args summary, duration,
guard verdict, before/after shots side-by-side (click→lightbox diff),
`tree_delta` compact chip list. Supervised mode: pending approvals float to top
with inline approve/deny. Banner strip: last 3 `drift`/`stale_ref`/`rate_limited`
warnings. Arm/disarm/panic controls duplicated here (this is the tab an operator
actually lives in).

**Audit tab.** Filterable tail (tool/host/ok-only), CSV export link, "open shots
folder" (path is on-disk truth), retain-days widget.

**Target tab.** `pilot_status` details: session/desktop, monitors + DPI table,
capture tier ladder with last downgrade reason (if any), engine uptime/generation,
`[restart engine]` (task re-trigger — never kills the user's apps), config path
(printed, not editable — config is a file, on purpose, like hyperv's env-vars).

Polling: bars/status/windows every 3 s (hyperv cadence); frames WS; events SSE with
`/api/log` fallback. Theme via CSS vars (the app theme trick from the chat-UI work).

## 13. Security model

| Plane | Mechanism |
|---|---|
| Bind | default `0.0.0.0:8791` in dev/test (host→guest reachability, G7); `pilot bind loopback` for real desktops |
| Auth | **bearer tokens**, 3 scopes: `ro` (GET/tree/shots), `act` (mutations; still needs armed), `drive` (ws/drive). Issued on first run → printed in startup log line + written `%LOCALAPPDATA%\WindowsPilot\tokens.json`; rotate via CLI `pilot token rotate ro` |
| TLS | v1: none on loopback/LAN-trust (hyperv precedent, G4); P2+: self-signed + `--require-tls` behind YARP or caddy — documented, not faked |
| Insecure defaults never silent | LAN bind + no TLS ⇒ startup log prints a LOUD banner line (audit `kind:"warn"`) + Target-tab badge |
| UAC | agent runs **unelevated** by default; `pilot_launch_app(elevate:true)` → ShellExecuteW runas → consent prompt is `denied_app` territory for the agent but the *user* can approve by hand (drive mode) — honest wall, §23 |
| Refs/tokens | refs carry no authority (they're just handles); authority is token+arm, checked at the seam every call, never cached across calls |
| Clipboard | paste-mode restores prior clipboard; refuse-paste rule on password elements (§4.2) |

## 14. Session placement, install, lifecycle

- **Never Session-0.** UIA/SendInput/WGC need the interactive window station (G10's
  #1 failure mode; hyperv sidesteps it by *not* touching desktops — we can't).
- Install = **logon-triggered Scheduled Task** (proven hyperv technique family, G6):
  ```
  schtasks /Create /TN WindowsPilot /SC ONLOGON /RL LIMITED ^
    /TR "%LOCALAPPDATA%\WindowsPilot\pilot.exe --config ...\pilot.yaml"
  ```
  (registry Run key alternative; Task Scheduler gives us
  "restart if exited" + instance-collision guard "Do not start new instance").
- **Watchdog:** second task at 1-min repetition: `pilot.exe --ensure` → GETs
  `/api/status`; dead ⇒ relaunch. `generation` bump (engine restart) tells agents
  refs are void (§5).
- **Multi-session (RDP vs console):** engine binds the desktop it launched into;
  `/api/status.session` names it (`Console` vs `RDP-Tcp#N`); if the console user
  switches, the agent follows the *active* session's Default desktop via
  WTS events — P1 asserts the visible case (auto-login single session, G6), P2 does
  session-switch handoff.
- Self-update: deferred — the rig re-deploys whole builds (delete+create VM, G6).

## 15. Configuration reference (`pilot.yaml`, env-overridable `PILOT_*`)

```yaml
bind: 0.0.0.0            # or loopback
port: 8791
tokens: {source: "tokens.json", scopes: [ro, act, drive]}
governance:
  allow_mcp_arm: false   # true ONLY in rig profiles
  auto_arm: never        # never|startup|always(always=dev-only, LOUD banner)
  supervised: false      # approve-every-action mode
  rate_per_min: 60
  denylist_extra: []
uia: {per_op_timeout_ms: 5000, max_nodes: 2000, budget_ms: 2000, max_depth: 25}
capture: {prefer: wgc, idle_fps: 2, active_fps: 6, jpeg_quality: 70, max_edge: 1568}
audit: {dir: default, retain_days: 30, redact_text: true, save_shots: true}
vision: {marks: true, max_marks: 150}
rig: {profile: false}    # true: allow_mcp_arm + localhost-any + verbose logs
```
CLI: `pilot.exe serve | token rotate <scope> | status | ensure | url` (last prints
LAN URL — used by the rig's deploy step, §19).

## 16. Error taxonomy (every error is machine-branchable)

| code | meaning | retry policy surfaced in `hint` |
|---|---|---|
| `stale_ref` | element gone | re-find with returned `was` fingerprint |
| `not_found` | query matched 0 | broaden query; `get_tree` first |
| `pattern_unsupported` | e.g. toggle on Invoke-only | try `invoke` / coordinate click |
| `not_armed` | mutation without arm | arm via web UI (or `pilot_arm` if allowed) |
| `drive_locked` | human holds input | release drive in console first |
| `denied_app` | foreground on denylist | **do not retry** — human action required |
| `uac_pending` | consent window detected | surface to human, stop looping |
| `rate_limited` | bucket empty | `retry_after_s` honored |
| `element_offscreen` | rect outside monitors | scroll container, re-find |
| `capture_failed` | all tiers failed | reports per-tier reasons |
| `timeout` | UIA provider too slow | mark app sluggish; retry once |
| `session_lost` | agent left interactive station | restart in-session (never fixable remotely) |
| `job_error` | JobStore error field set | see `result.error` |

Envelope always: `{ok:false, error:"<code>", msg, hint, retry:bool}` — plus MCP
`ToolError` raised so MCP clients see failure states (the fastmcp-skill lesson:
failed calls RAISE).

## 17. Wait/event system (kills LLM polling loops)

`pilot_wait_for({condition, timeout_s:30, interval_ms?})`, conditions:
```
element_appears {query, within:"w-ref|any"}     element_gone   {…same}
window_opened   {title_regex|process}           window_closed  {…}
window_foreground {ref|title_regex}             focused {query}
text_matches    {ref, regex|contains}
pixel_stable    {region, for_ms:800}            # "the animation finished"
action_settled  {since_ms}                      # no UIA events for N ms
```
Server-side loop on the events thread (UIA focus/structure-change events + 100 ms
diff fallback); returns matched ref/element or `wait_timeout` (not an error —
`ok:true, matched:false`, so agents can branch without exception noise).
`EventHub` ring (500) + SSE, event types:
`focus_changed, window_opened/closed/moved, process_started/exited, armed_changed,
agent_acting (start/stop + tool), drift, panic, generation_bump, capture_tier_change`.

## 18. Performance budgets (asserted in the rig, §19)

| Path | Budget | Measured by |
|---|---|---|
| `pilot_screenshot` first byte (1080p, marks off) | ≤ 120 ms | T-04 loop p95 |
| `pilot_screenshot(marks)` total | ≤ 900 ms | T-07 |
| single action + verification bundle (tree-delta included) | ≤ 350 ms + app time | T-02 |
| `get_tree` depth-1 focused window | ≤ 400 ms | T-03 |
| full find (20k-node desktop, budgeted) | ≤ budget_ms (graceful stop) | T-11 |
| frame stream CPU idle | ≤ 5 % one core | T-14 (typeperf) |
| kill-switch ⇒ last queued input dropped | ≤ 100 ms | T-09 |
| `wait_for(window_opened)` real latency + 50 ms | — | T-05 |

## 19. Test rig — driven 100 % through the :5003 hyperv MCP (host never used as target)

### 19.1 Rig contract (scripts/rig/*.sh — plain curl/JSON-RPC against :5003)
```
provision.sh <name>:
  1. POST :5003/mcp  create_vm {name, iso:"win11ent-2382600→'win11ent-26h2'", cpu:4, memory_mb:8192}
  2. wait_ready  (FirstLogonCommands marker, G8 — never assume heartbeat ⇒ ready)
  3. deploy:   run_ps loop — build payload (publish zip ~60 MB):
               a) SMB/pivot: guest → http://192.168.50.1:8000 (host-stage http.server
                  over the C:\pilot\stage share) — PowerShell Direct base64-chunk
                  fallback exists (T-16 proves the chunked path) for zero-network deploys
               b) expand to %LOCALAPPDATA%\WindowsPilot, register logon task (§14),
                  schtasks /Run, health: curl 192.168.50.x:8791/api/status until ok
  4. report  target URL (http://<guest-ip>:8791) → human opens browser, real console demo
teardown.sh:  delete_vm | purge_all (always; VMs are fixtures, G6)
```
### 19.2 Suite (each = assertions runnable headless via run_ps + a raw MCP client; human-tab notes where the console must visibly show it)

| ID | Test | Assert |
|---|---|---|
| T-01 | session truth | `/api/status.session` = auto-login Console desktop (G6); kill+relaunch agent ⇒ `generation` bump, refs invalid |
| T-02 | notepad E2E | launch_app(notepad) → tree finds `Edit`/`DirectUIHWND` text area → `type_text("hello windowspilot")` → `text_matches` → `press_keys(ctrl+a,delete)` → value gone (tree_delta shows `-`) |
| T-03 | tree contract | depth-1 tree of Explorer ≤ 400 ms; `max_nodes` honored; `truncated+cursor` continue works to full set |
| T-04 | capture tiers | forced `capture.prefer` per tier, all three return identical-size PNG on primary; tier field correct; wgc on a GPU-composited window (edge://) non-black (mean-luma check) |
| T-05 | window wait | launch mspaint → `wait_for(window_opened)` <3 s after process; kill_app ⇒ `window_closed` |
| T-06 | pattern actions | Notepad word-wrap: find menu → expand → find ToggleMenuItem → state via `get_properties` → toggle → state flips (assert on ToggleState, not pixels) |
| T-07 | marks | screenshot(marks) on Notepad ⇒ ≥1 mark whose ref resolves; click by mark id ⇒ focus on that element |
| T-08 | coordinate draw | mspaint: brush, `drag` arc (400 ms) at known coords ⇒ pixel assert via `capture_window` (sample points on expected path, tolerance) — **the canvas-app proof** |
| T-09 | kill switch | `press_keys` loop of 200; `pilot_panic` at #50 ⇒ loop stops ≤100 ms; input mutex shows `dropped:N>0`; audit has `PANIC`; re-arm needed to continue |
| T-10 | drift | agent mid-batch + TTY-injected *human* input (SyntheticaTst or SendInput marked human via injected-flag spoof test: use a real console app that fires a mouse event with LLMHF flags cleared — if tooling can't fake it, assert with drive-mode takeover path) ⇒ PAUSED, human path keeps working |
| T-11 | hostile tree | open Task Manager details (deep tree), budget_ms:500 ⇒ returns truncated, ≤700 ms, engine still responsive on other windows |
| T-12 | stale-ref | capture ref → close window → action ⇒ `stale_ref` + `was` fingerprint; agent re-find by fingerprint name succeeds first try |
| T-13 | DPI | guest set to 200 % scale (run_ps: registry+logoff or SetDisplayConfig), T-08 re-run via *drive-mode* coords from browser-side transform ⇒ same pixel assert (proves §6.6 double transform) |
| T-14 | budgets | T-02/03/04 in loop ×50, p95 vs §18 table; frame-stream CPU via `typeperf` ≤5 % |
| T-15 | governance matrix | every mutation tool × {disarmed, act, drive-locked} ⇒ correct error code (§16) with zero injected events (assert input-mutex counter flat) |
| T-16 | zero-network deploy | block egress (no proxy), base64-chunked PowerShell-Direct deploy path completes + health-checks |
| T-17 | web console smoke | puppeteer *host-side* against guest console: tabs render, drive-mode hand-off button, tree-hover sync rect, Operator tab shows the T-02 run's before/after shots |
| T-18 | chaos | `Stop-Process -Force` engine ⇒ watchdog ≤ 90 s respawn ⇒ `generation` bump ⇒ agent's old ref errors carry `stale_ref`+generation hint; VM untouched |

### 19.3 Regression posture
- Every bug fixed = a new T-xx or a new assert on an existing one (mutation-suite
  hygiene lesson from the Vencord work: never hand-patch without the suite re-run).
- Clean-VM guarantee: golden child disk ⇒ T-01..T-18 run from factory Windows in one
  `rig.sh all` invocation; red suite ⇒ `purge_all`, fix, re-run — the 20 s VM makes
  "always test on clean" the cheap option, not the disciplined one.
- Demo target (P1 exit): one command → fresh Win11 VM → Hermes (MCP client on host)
  writes a paragraph in Notepad, draws an arc in Paint, all visible in the browser
  console live — then `purge_all`. The whole loop never touches the operator desktop.

## 20. Build plan & exit criteria (each phase's suite gates the next)

| Phase | Scope | Exit (all in-guest, all gated on :5003 rig) |
|---|---|---|
| **P0 spike** (~2 d) | Program.cs clone + `pilot_status` + `capture_screen` 3 tiers + `click/drag/type/keys` + raw `/ws/view` frames | T-01, T-04, T-08 green in a fresh VM; human sees live screen from host browser |
| **P1 MVP** (~1 w) | UIA3+registry+tree/find, 18 tools, REST mirror, console tabs Screen+Inspect+Operator(min), verification bundles, wait_for, launch/kill | T-02,03,05,06,07,11,12,13,14,15,17,18 green; demo run above |
| **P2 safety/vision** (~1 w) | arm states + 3 kill paths + drift + audit+shots+replay + denylist + supervised mode + marks + rate limit + drive mode hardening | T-09,10,16 + marks-on-Discord-like-canvas vision demo; audit replay bit-for-bit on second VM |
| **P3 fleet** (later) | hub process (registry+token broker) fronting N pilots; single console grid; SSE federation; recorder/replay of human drives; per-machine capability flags | drive 2 VMs from one host browser; recorded 20-step drive replays identically on a fresh VM (T-19,20) |

## 21. Alternatives considered & rejected (and why)

| Decision | Chosen | Rejected | Why |
|---|---|---|---|
| Host shape | single exe, MCP+REST+SPA same port (G1 clone) | separate MCP stdio + sidecar web | stdio MCP can't serve the browser anyway; hyperv proved the single-port product shape end-to-end on this machine |
| MCP stack | `ModelContextProtocol` C# SDK (`WithTools<T>`) | FastMCP/python + .NET bridge | SDK already proven here (G2); bridge = two error models, two lifetimes |
| UIA binding | FlaUI in-process .NET | uiautomation/pywinauto via pythonnet | G10 proved the capability but paid STA+interop tax twice; in-proc FlaUI is strictly faster, same API surface we validated |
| Capture | WGC→PrintWindow→GDI ladder | DXGI desktop duplication | DD is console-session-only (fails headless/RDP parity), per-monitor pain; WGC correct where DD blind; GDI kept only as smoke-test fallback |
| Input | SendInput only | UI Automation `TransformPattern` drags, `mouse_event`, pyautogui | pattern-transform ≠ real input (apps know); legacy APIs race; SendInput is what remote-desktop tools use |
| Vision strategy | marks-over-UIA with ref resolution | pure pixel-LLM ("Cua-style") | ref-first is deterministic, auditable, DPI-safe (numbers, not vibes); pixel path kept only where tree is empty |
| Frame transport | WS binary JPEG drop-oldest | WebRTC, MJPEG/HTTP | WS = zero deps, works through the same auth as hyperv term WS (G4); WebRTC P3 if remote latency demands it |
| Refs | server-side registry + fingerprint re-find | return raw tree paths every call; caching UIA elements forever | re-find-on-stale *refuses to guess* — wrong-element actuation is the worst failure mode available |
| Install | logon task (Session 1) | LocalSystem service (hyperv's model!) | hyperv never touches a desktop so Session 0 is fine *there*; UIA/SendInput/WGC physically require the interactive station (G10) |

## 22. Future (deliberately not v1)
- **Fleet hub** (P3): mirrors how hyperv ambitions grew; hub holds token minting, agent
  registry (`GET hub/api/agents` → pilot URLs+caps), one console grid.
- **Recorder/replay:** capture human drive sessions (input + UIA snapshots + shots) →
  deterministic replay with per-step `wait_for` (self-healing: fingerprint re-resolve
  with `ask_agent:true` fallback — "which of these 3 candidates?" — closes the loop
  between deterministic replay and LLM improvisation).
- **Secure-desktop capture** (OpenInputDesktop+GDI on MTA thread, per UIStalker Ph-10
  design): read-only; injection stays impossible by policy even when possible by API.
- **AT-SPI backend** on Linux guests (`mcp-lnx1` is already up, G7) to prove the seam's
  OS-agnostic claim before anyone pays for it.
- **Approval workflows** (Operator-tab gate → Hermes kanban card → human approves →
  agent resumes) — the seam's `pending_approval` is already the right hook.

## 23. Risks & known walls (honest ledger)
1. **UAC/secure desktop** — UIA blind by OS design; consent windows are `denied_app`
   + `uac_pending`. Only humans can pass them (drive mode, by hand). Not a bug.
2. **Session-0 misinstall** — rig's T-01 exists precisely because this failure is
   silent-half-way (windows visible, elements dead). Never a classic service. Ever.
3. **Anti-cheat/some DRM** ignore SendInput — fallback ladder documented (patterns →
   TextPattern → guarded paste).
4. **Chromium trees are slow** — budgeted walks (T-11) + lazy depth + find-first policy
   are the mitigation; never expose an unbudgeted tree walk.
5. **Mixed-DPI multi-monitor** — solved-by-contract (§6), asserted (T-13), but every
   future coordinate feature must go through the transform table or die in review.
6. **Clipboard privacy** — paste mode touches user clipboard; restore-after + refuse
   on password elements + `redact_text` audit default; the *user* can see every shot
   the agent kept (`save_shots:true` default — agents don't get to be sneaky).
7. **Model loops going feral** — rate limiter + supervised mode + the fact that
   `stale_ref/not_found` carry `retry:bool` so a well-behaved client stops. Last line:
   the STOP button and Ctrl+Alt+Pause need neither token nor model cooperation.

---
*v0.2 expands v0.1 (kept at ux-mcp-design.md) with: full tool/REST/WS schemas, the
ref/registry + fingerprint algorithm, coordinate/DPI contract, threading table,
governance state machine + audit schema, wire-level UI spec, error taxonomy,
wait/event vocabulary, perf budgets, an 18-test rig suite mapped onto the verified
:5003 capabilities, decision log with rejections, and the honest-wall ledger.*
