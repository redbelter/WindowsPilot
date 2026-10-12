using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsPilot.Core;
using WindowsPilot.Engine;
using WindowsPilot.Engine.Governance;
using WindowsPilot.Engine.Uia;
using WindowsPilot.Engine.Vision;

namespace WindowsPilot;

/// <summary>
/// The MCP surface: [McpServerTool] adapters ONLY (design §3). Every body
/// parses args and calls PilotEngine — the seam — after stamping a
/// CallContext(Host="mcp"). PilotExceptions propagate as MCP tool errors
/// (failed calls RAISE — clients must see failure states). Docstrings here
/// ARE the agent-facing contract; prose goes where it prevents loops.
/// </summary>
[McpServerToolType]
public sealed class PilotTools(PilotEngine engine, ArmState arm, KillSwitch kill,
    AuditLog audit, EventHub events, PilotSettings cfg, TokenStore tokens)
{
    static CallContext Cx() => new("mcp", "act");

    // ================================ read ================================

    [McpServerTool, Description("Agent + host health: arm state, kill-switch trips, session truth, monitors/DPI, capture tier, tool list. Check `session.interactive` FIRST — everything else is meaningless if false (Session-0 misinstall).")]
    public Task<CallToolResult> Pilot_status() =>
        Guard(async () => await engine.StatusAsync());

    [McpServerTool, Description("Top-level windows: ref, hwnd, title, process, foreground flag, rect. Start here; tree-walk a window ref next.")]
    public Task<CallToolResult> Pilot_get_windows([Description("include minimized windows")] bool minimized = false) =>
        Guard(async () => await engine.GetWindowsAsync(minimized));

    [McpServerTool, Description("Foreground window (ref + title) and the focused element ref. Cheapest way to re-anchor after a switch.")]
    public Task<CallToolResult> Pilot_get_foreground() =>
        Guard(async () => await engine.GetForegroundAsync());

    [McpServerTool, Description(
        "LAZY UIA tree of one window (default: foreground). depth=1 children only — expand what you care about. " +
        "Nodes carry ref + supported patterns + rect + text_snippet. If truncated:true, raise budget_ms or expand per node. " +
        "NEVER walk the whole desktop; walk a window.")]
    public Task<CallToolResult> Pilot_get_tree(
        [Description("window ref from get_windows (or any element ref to expand a subtree)")] string? @ref = null,
        [Description("hwnd hex from get_windows, alternative to ref")] string? hwnd = null,
        [Description("depth of children below root (0-12), default 1")] int depth = 1,
        [Description("max nodes (default 2000)")] int max_nodes = 2000,
        [Description("time budget ms (default 2000)")] int budget_ms = 2000) =>
        Guard(async () => await engine.GetTreeAsync(hwnd, @ref, depth, max_nodes, budget_ms));

    [McpServerTool, Description(
        "Find elements by name/automation_id/class/control_type — SERVER-SIDE, budgeted. Prefer this over tree walks. " +
        "Empty query matches nothing on purpose. Returns nodes with refs + patterns.")]
    public Task<CallToolResult> Pilot_find_elements(
        string? name_regex = null, string? name_contains = null, string? automation_id = null,
        string? class_name = null,
        [Description("UIA control type e.g. Button, MenuItem, Edit, ToggleButton")] string? control_type = null,
        [Description("process name filter, e.g. notepad")] string? process = null,
        string? hwnd = null, string? @ref = null, int max = 50) =>
        Guard(async () => await engine.FindAsync(new FindQuery
        {
            NameRegex = name_regex, NameContains = name_contains, AutomationId = automation_id,
            ClassName = class_name, ControlType = control_type, Process = process, Hwnd = hwnd, Ref = @ref,
        }, max));

    [McpServerTool, Description("One element snapshot from a ref (liveness check + current props). Stale → stale_ref error with `was` fingerprint to re-find.")]
    public Task<CallToolResult> Pilot_get_element(string @ref) =>
        Guard(async () => await engine.GetElementAsync(@ref));

    [McpServerTool, Description("Full UIA property dict for one element (raw names, includes pattern support list).")]
    public Task<CallToolResult> Pilot_get_properties(string @ref) =>
        Guard(async () => await engine.GetPropertiesAsync(@ref));

    [McpServerTool, Description("Hit-test: which element is at physical (x,y)? Use to interpret a coordinate click before doing it.")]
    public Task<CallToolResult> Pilot_element_at(int x, int y) =>
        Guard(async () => await engine.ElementAtAsync(x, y));

    [McpServerTool, Description(
        "Screenshot. marks:true draws numbered boxes over actionable UIA elements and returns the mark→ref map — " +
        "click a MARK NUMBER via pilot_click(mark=n) (resolves to the ref, pattern-first; never blind pixels). " +
        "Empty marks = canvas UI (tree is a black box): fall back to coordinate clicks using source_origin+scale to " +
        "inverse-map image pixels to real coordinates. image_url is served at the returned path (HTTP-reachable host).")]
    public Task<CallToolResult> Pilot_screenshot(
        int monitor = 0, bool marks = true,
        [Description("longest edge px, default 1568 (vision-model sweet spot)")] int max_edge = 1568,
        [Description("jpeg quality 30-95")] int quality = 70,
        [Description("capture one window by hwnd instead of a monitor")] string? hwnd = null) =>
        Guard(async () => await engine.ScreenshotAsync(monitor, marks, max_edge, quality, hwnd));

    [McpServerTool, Description("Tail the activity ring (focus changes, windows, arm/panic, job transitions). since=last n you saw.")]
    public JsonElement Pilot_get_events(long since = 0, int max = 100) =>
        El(new { events = events.Since(since, max).Select(e => new { n = e.N, t = e.T, type = e.Type, detail = e.Detail }), n = events.SinceSeq });

    [McpServerTool, Description(
        "BLOCK server-side until a UI condition holds (element_appears/gone, window_opened/closed, window_foreground, " +
        "focused, text_matches, pixel_stable/action_settled). Returns matched:false on timeout (ok:true) so you branch " +
        "instead of throwing. Use this INSTEAD of screenshot-polling loops.")]
    public Task<CallToolResult> Pilot_wait_for(WaitCondition condition, int timeout_s = 30, int interval_ms = 250) =>
        Guard(async () => await engine.WaitForAsync(condition, timeout_s, interval_ms));

    [McpServerTool, Description("Tail the JSONL audit log (redacted args). For self-audit: 'what did I just do?'")]
    public JsonElement Pilot_get_audit(long since_seq = 0, string? tool = null, int limit = 50) =>
        El(new { items = audit.Tail(since_seq, tool, "mcp", limit).Items, n = audit.Seq });

    // ================================ act ================================
    // All act tools: require ARMED(act); audit every call; return the
    // verification bundle (before/after shots + tree_delta) unless verify:false.

    [McpServerTool, Description(
        "Click. PREFER ref (pattern-focus + center click) or mark=n (resolves mark→ref). x,y are PHYSICAL px — " +
        "inverse-map screenshot pixels with source_origin+scale first. Refs die when the UI rebuilds: a stale_ref " +
        "error carries `was` — re-find with its name/automation_id, then retry ONCE.")]
    public Task<CallToolResult> Pilot_click(string? @ref = null, int? x = null, int? y = null,
        [Description("left|right|middle")] string button = "left", int clicks = 1,
        [Description("mark number from a marks screenshot (takes precedence over ref/x,y)")] int? mark = null,
        bool verify = true)
    {
        return Guard(async () =>
        {
            string? r = @ref;
            if (mark is not null) r = ResolveMark(mark.Value);
            return await engine.ClickAsync(Cx(), r, x, y, button, clicks, verify);
        });
    }

    [McpServerTool, Description(
        "Drag (press, interpolate, release). The ONLY way to paint on canvas apps (mspaint etc). " +
        "Source = ref (its center) or x1,y1; target x2,y2. steps/duration_ms tune gesture speed.")]
    public Task<CallToolResult> Pilot_drag(string? @ref = null, int? x1 = null, int? y1 = null,
        int x2 = 0, int y2 = 0, int steps = 12, int duration_ms = 400, bool verify = true) =>
        Guard(async () => await engine.DragAsync(Cx(), @ref, x1, y1, x2, y2, steps, duration_ms, verify));

    [McpServerTool, Description("Wheel scroll at ref-center or coords. delta_y positive = scroll up (Windows convention).")]
    public Task<CallToolResult> Pilot_scroll(string? @ref = null, int? x = null, int? y = null,
        int delta_y = 0, int delta_x = 0) =>
        Guard(async () => await engine.ScrollAsync(Cx(), @ref, x, y, delta_y, delta_x, verify: false));

    [McpServerTool, Description(
        "Type text. With ref: uses ValuePattern when available (no focus steal), else focuses the element first. " +
        "Password elements are NEVER clipboard-pasted (auto-unicode) and audit-redacted. mode:auto picks " +
        "unicode/paste by length. For key SHORTCUTS use pilot_press_keys, not escape sequences here.")]
    public Task<CallToolResult> Pilot_type_text(string text, string? @ref = null,
        [Description("auto|keys|unicode|paste")] string mode = "auto", bool verify = true) =>
        Guard(async () => await engine.TypeTextAsync(Cx(), text, @ref, mode, verify));

    [McpServerTool, Description(
        "Key chords: single ('ctrl+alt+t') or comma sequence ('ctrl+s,ctrl+w'). Names: ctrl alt shift win enter tab " +
        "esc space delete arrows f1-f16 letters digits. This is the shortcut tool (win+r, alt+tab...).")]
    public Task<CallToolResult> Pilot_press_keys(string keys, int repeat = 1, bool verify = false) =>
        Guard(async () => await engine.KeysAsync(Cx(), keys, repeat, verify));

    [McpServerTool, Description("Set a ValuePattern element's text directly (search boxes, address bars) — fastest, no focus.")]
    public Task<CallToolResult> Pilot_set_value(string @ref, string value, bool verify = true) =>
        Guard(async () => await engine.SetValueAsync(Cx(), @ref, value, verify));

    [McpServerTool, Description("Invoke a button/menu-item (InvokePattern). Works on offscreen/minimized UI where pixel clicks can't reach.")]
    public Task<CallToolResult> Pilot_invoke(string @ref, bool verify = true) =>
        Guard(async () => await engine.InvokeAsync(Cx(), @ref, verify));

    [McpServerTool, Description("Toggle checkbox/toggle-button. Verify via get_element (ToggleState in properties), not pixels.")]
    public Task<CallToolResult> Pilot_toggle(string @ref, bool verify = true) =>
        Guard(async () => await engine.ToggleAsync(Cx(), @ref, verify));

    [McpServerTool, Description("Expand/collapse expander (menus, tree items). expand=true/false.")]
    public Task<CallToolResult> Pilot_expand(string @ref, bool expand = true, bool verify = true) =>
        Guard(async () => await engine.ExpandAsync(Cx(), @ref, expand, verify));

    [McpServerTool, Description("Select a selectable item (list item, tab). SelectionItemPattern.")]
    public Task<CallToolResult> Pilot_select(string @ref, bool verify = true) =>
        Guard(async () => await engine.SelectAsync(Cx(), @ref, verify));

    // ============================== lifecycle ==============================

    [McpServerTool, Description("Launch a process IN-SESSION (CreateProcess, falls back to ShellExecute). Waits up to wait_for_window_ms for its window to take foreground so you can tree-walk it immediately.")]
    public Task<CallToolResult> Pilot_launch_app(string cmd, string? args = null, string? workdir = null,
        int wait_for_window_ms = 8000) =>
        Guard(async () => await engine.LaunchAsync(Cx(), cmd, args, workdir, wait_for_window_ms, verify: true));

    [McpServerTool, Description("Terminate by pid or process name. Refuses when foreground app is on the denylist (UAC etc).")]
    public Task<CallToolResult> Pilot_kill_app(int? pid = null, string? process_name = null) =>
        Guard(async () => await engine.KillAppAsync(Cx(), pid, process_name));

    [McpServerTool, Description(
        "Arm injection. ONLY available when config governance.allow_mcp_arm (rig profiles). Normally a HUMAN action " +
        "via the web console. scope: act (inject+patterns) or drive (web-console mouse/keyboard).")]
    public CallToolResult Pilot_arm([Description("act|drive")] string scope = "act", int ttl_min = 15, string why = "")
    {
        if (!cfg.Governance.AllowMcpArm)
            return Err(PilotException.NotArmed("act"));   // structured, not sanitized prose
        var info = arm.Arm(Enum.Parse<ArmScope>(scope, true), ttl_min, why, allowMcpArm: true);
        audit.Write("mcp", "act", "pilot_arm", new { scope, ttl_min, why }, null,
            new { allow_mcp_arm = true }, new { ok = true });
        return Ok(info);
    }

    [McpServerTool, Description("PANIC: immediate disarm + drain queued input. Use when confused (wrong app open, wrong element targeted, UI misbehaving). Always allowed, never denied.")]
    public object Pilot_panic()
    {
        kill.Trip("mcp");
        arm.Disarm("panic(mcp)");
        return new { ok = true, trips = kill.Trips, dropped_inputs = kill.DroppedInputs };
    }

    // =============================== helpers ===============================

    string? ResolveMark(int mark)
    {
        // marks are rebuilt per screenshot; the last screenshot's map is held here
        var map = LastMarkMap.Current;
        if (map is not null && map.TryGetValue(mark, out var r)) return r;
        throw new PilotException("not_found", $"mark {mark} not in the last marks screenshot",
            "take a fresh pilot_screenshot(marks:true) — marks are rebuilt every shot");
    }

    static JsonElement El(object o) => JsonSerializer.SerializeToElement(o);

    /// <summary>SDK sanitizes thrown-exception messages to "An error occurred
    /// invoking..." — that destroys the error taxonomy. The documented way to
    /// keep control is returning CallToolResult directly: structured content +
    /// IsError, machine-branchable on `error` code.</summary>
    static CallToolResult Err(PilotException pe) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = El(pe.ToErr()).GetRawText() }],
        StructuredContent = El(pe.ToErr()),
    };

    static CallToolResult Ok(object o) =>
        new() { Content = [new TextContentBlock { Text = El(o).GetRawText() }] };

    static async Task<CallToolResult> Guard(Func<Task<object>> f)
    {
        try { return Ok(await f()); }
        catch (PilotException pe) { return Err(pe); }
    }
}
