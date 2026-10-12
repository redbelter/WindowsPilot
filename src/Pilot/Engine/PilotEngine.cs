using System.Diagnostics;
using System.Text.Json;
using WindowsPilot.Core;
using WindowsPilot.Engine.Capture;
using WindowsPilot.Engine.Governance;
using WindowsPilot.Engine.Input;
using WindowsPilot.Engine.Uia;
using WindowsPilot.Engine.Vision;
using WindowsPilot.Interop;

namespace WindowsPilot.Engine;

public sealed record CallContext(string Host, string TokenScope);

/// <summary>
/// THE SEAM (design §3). MCP tools and REST routes are adapters that parse
/// arguments and call methods here — nothing else implements behavior. All
/// governance (arm/deny/rate/audit/panic) is enforced inside this class, so
/// no host can bypass it. Thread model per design §9: UIA ops hop to the STA
/// thread via UiaService; injections hop via InputInjector's mutex; capture
/// runs on the caller (Kestrel) thread — GDI/PrintWindow are thread-safe.
/// </summary>
public sealed class PilotEngine(
    PilotSettings cfg, UiaService uia, InputInjector input, CaptureService capture,
    ArmState arm, KillSwitch kill, AuditLog audit, Guard guard, EventHub events,
    TokenStore tokens, ILogger<PilotEngine> log)
{
    readonly DateTime _started = DateTime.UtcNow;
    long _rateTokens; long _rateLast;
    readonly object _rateGate = new();

    public ElementRegistry Registry => uia.Registry;

    // ============================ status ============================

    public async Task<object> StatusAsync()
    {
        var (session, active) = SessionInfo.Current();
        if (session != active) throw PilotException.SessionLost();   // T-01 asserts this path
        var mons = capture.Monitors();
        string fgTitle = "";
        try
        {
            var fgEl = JsonSerializer.SerializeToElement(await GetForegroundAsync());
            if (fgEl.TryGetProperty("title", out var ft)) fgTitle = ft.GetString() ?? "";
        }
        catch (PilotException) { }
        return new
        {
            ok = true, version = typeof(PilotEngine).Assembly.GetName().Version?.ToString(),
            uptime_s = (long)(DateTime.UtcNow - _started).TotalSeconds,
            generation = Registry.Generation,
            armed = arm.Info.Armed, arm_expires = arm.Info.Expires, arm_why = arm.Info.Why,
            kill_trips = kill.Trips, dropped_inputs = kill.DroppedInputs,
            refs = Registry.Count,
            session = new { id = session, active_console = active, interactive = session == active },
            monitors = mons,
            capture_tier = cfg.Capture.Prefer,
            foreground = fgTitle,
            enabled_tools = ToolSurface.Names,
        };
    }

    // ============================ reads ============================

    public Task<object> GetWindowsAsync(bool minimized) => uia.Run(() =>
    {
        var fg = Native.GetForegroundWindow();
        var rows = new List<WindowDto>();
        foreach (var t in uia.NativeTopWindowsSync())
        {
            try
            {
                var hwnd = t.Hwnd;
                if (Native.IsIconic(hwnd) && !minimized) continue;
                RectDto? rect = null;
                if (Native.GetWindowRect(hwnd, out var r))
                    rect = new RectDto(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                string proc;
                try { proc = Process.GetProcessById(t.Pid).ProcessName; } catch { proc = "?"; }
                rows.Add(new WindowDto
                {
                    Ref = uia.RegisterHwndRefSync(hwnd),   // windows are elements — one ref namespace (design §5)
                    Hwnd = "0x" + hwnd.ToInt64().ToString("x"),
                    Title = t.Title.Length > 200 ? t.Title[..200] : t.Title,
                    Process = proc, Pid = t.Pid,
                    Fg = hwnd == fg,
                    Minimized = Native.IsIconic(hwnd),
                    Rect = rect,
                });
            }
            catch { }
        }
        return (object)new { windows = rows };
    }, "GetWindows");

    public async Task<object> GetForegroundAsync()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return new { title = "", @ref = (string?)null, focused = (string?)null };
        return await uia.Run(() =>
        {
            var el = uia.TryFromHwnd(hwnd);
            if (el is null) return (object)new { title = "", @ref = (string?)null, focused = (string?)null };
            string title = UiaService.TitleOfNative(hwnd);   // Win32 truth (design §6)
            string? focusedRef = uia.FocusedElementRefSync();
            return (object)new { title, @ref = (string?)uia.Registry.Register(el), focused = focusedRef };
        }, "GetForeground");
    }

    public Task<object> GetTreeAsync(string? hwndHex, string? rootRef, int depth, int maxNodes, int budgetMs)
        => uia.Run(() =>
        {
            string root;
            if (rootRef is not null) root = rootRef;
            else if (hwndHex is not null) root = uia.RegisterHwndRefSync(UiaService.ParseH(hwndHex));
            else
            {
                var fg = Native.GetForegroundWindow();
                root = uia.RegisterHwndRefSync(fg);
            }
            var (nodes, truncated) = uia.WalkTreeSync(root, Math.Clamp(depth, 0, 12),
                Math.Clamp(maxNodes, 1, 20000), Math.Clamp(budgetMs, 100, 20000));
            return (object)new { root, nodes, truncated };
        }, "GetTree");

    public async Task<object> FindAsync(FindQuery q, int max) =>
        new { elements = await uia.Find(q, Math.Clamp(max, 1, 500)) };

    public async Task<object> GetElementAsync(string r) => await uia.NodeOfRef(r);
    public async Task<object> GetPropertiesAsync(string r) => await uia.RawProperties(r);

    public async Task<object> ElementAtAsync(int x, int y)
    {
        var el = await uia.ElementFromPoint(x, y) ?? throw PilotException.NotFound($"no element at {x},{y}");
        return await uia.Run(() => (object)uia.NodeOfOnThread(el, 0), "ElementAt");
    }

    // ========================= screenshot + marks =========================

    public async Task<object> ScreenshotAsync(int monitor, bool marks, int maxEdge, int quality, string? hwndHex)
    {
        maxEdge = Math.Clamp(maxEdge, 320, 4096);
        quality = Math.Clamp(quality, 30, 95);

        Frame frame;
        if (hwndHex is not null) frame = capture.CaptureWindow(UiaService.ParseH(hwndHex));
        else frame = capture.CaptureScreen(monitor);

        List<MarkDto>? picked = null;
        byte[] jpeg = frame.Jpeg;

        if (marks && cfg.Vision.Marks)
        {
            var actionable = await CollectActionableAsync(hwndHex);
            picked = Marks.Pick(actionable, cfg.Vision.MaxMarks);
            LastMarkMap.Update(picked);   // pilot_click(mark=n) resolves against THIS shot
            if (picked.Count > 0)
                jpeg = RedrawWithMarks(frame, picked);
        }

        // verification shots save to disk for audit/Operator tab (design §8.3)
        string shotId = SaveShot(jpeg);
        return new
        {
            image_url = $"/api/shots/{shotId}.jpg",
            width = frame.Meta.Width, height = frame.Meta.Height,
            scale = frame.Meta.Scale, source_origin = frame.Meta.SourceOrigin,
            monitor = frame.Meta.Monitor, capture_tier = frame.Meta.CaptureTier,
            marks = picked ?? [],
            empty_marks_hint = picked is { Count: 0 } && marks
                ? "no_uia_marks, use coordinate clicks (canvas UI)" : null,
        };
    }

    async Task<NodeDto[]> CollectActionableAsync(string? hwndHex)
    {
        try
        {
            var (nodes, _) = await uia.WalkTree(
                hwndHex is not null ? await uia.RegisterHwndRef(UiaService.ParseH(hwndHex))
                                    : await RegisterForegroundRef(),
                depth: 4, maxNodes: 3000, budgetMs: 1200);
            return nodes.Where(n => n.Actionable && n.Patterns.Length > 0).ToArray();
        }
        catch (PilotException) { return []; }   // canvas UI: marks empty, coordinate path continues
    }

    async Task<string> RegisterForegroundRef() =>
        await uia.RegisterHwndRef(Native.GetForegroundWindow());

    static byte[] RedrawWithMarks(Frame frame, List<MarkDto> marks)
    {
        using var ms = new MemoryStream(frame.Jpeg);
        using var img = System.Drawing.Image.FromStream(ms);
        using var bmp = new System.Drawing.Bitmap(img);
        Marks.Draw(bmp, marks, frame.Meta.SourceOrigin, frame.Meta.Scale);
        using var outMs = new MemoryStream();
        bmp.Save(outMs, System.Drawing.Imaging.ImageFormat.Jpeg);
        return outMs.ToArray();
    }

    string SaveShot(byte[] jpeg)
    {
        if (!cfg.Audit.SaveShots) return "off";
        var id = Guid.NewGuid().ToString("N")[..12];
        try { File.WriteAllBytes(Path.Combine(cfg.ShotsDir, id + ".jpg"), jpeg); }
        catch (Exception e) { log.LogWarning("shot save failed: {Err}", e.Message); return "err"; }
        return id;
    }

    // ============================ mutations ============================

    /// <summary>Every mutation: arm+deny+rate check → optional before-shot → work → optional after-shot + tree delta → audit line.</summary>
    async Task<VerifyBundle> Mutate(CallContext cx, string tool, object args, bool verify,
        Func<Task> work)
    {
        var (fgProc, denyHit) = ForegroundProcess();
        guard.CheckMutation(arm, ArmScope.act, fgProc);
        RateCheck();

        string? before = null;
        NodeDto[]? beforeSet = null;
        if (verify)
        {
            try { before = SaveShot((await ScreenshotAsyncRaw()).Jpeg); beforeSet = await SnapshotActionable(); }
            catch (PilotException) { }
        }

        var sw = Stopwatch.StartNew();
        Exception? failure = null;
        try { await work(); }
        catch (Exception e) { failure = e; }
        sw.Stop();

        string? after = null;
        DeltaOp[]? delta = null;
        if (verify)
        {
            try
            {
                after = SaveShot((await ScreenshotAsyncRaw()).Jpeg);
                var afterSet = await SnapshotActionable();
                delta = TreeDelta(beforeSet, afterSet);
            }
            catch (PilotException) { }
        }

        long seq = audit.Write(cx.Host, cx.TokenScope, tool, args,
            target: new { proc = fgProc, deny = denyHit },
            guard: new { armed = arm.Info.Armed, rate = $"{_rateTokens}/{cfg.Governance.RatePerMin}" },
            res: new { ok = failure is null, code = (failure as PilotException)?.Code, ms = sw.ElapsedMilliseconds });

        if (failure is not null) throw failure;

        return new VerifyBundle
        {
            Ms = sw.ElapsedMilliseconds, BeforeShot = Before(before), AfterShot = After(after),
            TreeDelta = delta, AuditSeq = seq, DroppedInputs = kill.DroppedInputs,
        };
    }

    async Task<Frame> ScreenshotAsyncRaw() => await Task.FromResult(capture.CaptureScreen(0));

    async Task<NodeDto[]> SnapshotActionable()
    {
        try
        {
            var root = await RegisterForegroundRef();
            var (nodes, _) = await uia.WalkTree(root, 2, 600, 800);
            return nodes.Where(n => n.Actionable).ToArray();
        }
        catch (PilotException) { return []; }
    }

    static DeltaOp[]? TreeDelta(NodeDto[]? before, NodeDto[] after)
    {
        if (before is null) return null;
        var ops = new List<DeltaOp>();
        var bRefs = before.Select(n => (n.Ref, n.Name)).ToHashSet();
        var aRefs = after.Select(n => n.Ref).ToHashSet();
        foreach (var n in after.Where(n => !bRefs.Any(b => b.Ref == n.Ref)).Take(15))
            ops.Add(new DeltaOp { Op = "+", El = n });
        foreach (var b in bRefs.Where(b => !aRefs.Contains(b.Ref)).Take(15))
            ops.Add(new DeltaOp { Op = "-", El = new NodeDto { Ref = b.Ref, Name = b.Name } });
        foreach (var pair in after.Select(a => (a, before.FirstOrDefault(b => b.Ref == a.Ref)))
                 .Where(p => p.Item2 is not null && p.Item2.Name != p.a.Name).Take(10))
            ops.Add(new DeltaOp { Op = "~", Ref = pair.a.Ref, Changed = ["name"] });
        return ops.Count > 0 ? [.. ops.Take(30)] : [];
    }

    static string? Before(string? s) => s is null or "off" or "err" ? null : $"/api/shots/{s}.jpg";
    static string? After(string? s) => Before(s);

    (string? proc, string? deny) ForegroundProcess()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return (null, null);
        try
        {
            var pid = (int)Native.GetWindowThreadProcessId(hwnd, out int p); _ = pid;
            var proc = Process.GetProcessById(p);
            return (proc.ProcessName, guard.DenyCheck(proc.ProcessName));
        }
        catch { return (null, null); }
    }

    void RateCheck()
    {
        lock (_rateGate)
        {
            var now = Environment.TickCount64;
            var mins = (now - _rateLast) / 60000.0;
            if (mins >= 1) { _rateLast = now; _rateTokens = 0; }
            if (_rateTokens >= cfg.Governance.RatePerMin)
                throw PilotException.RateLimited(Math.Max(1, (int)Math.Ceiling(60 - (now - _rateLast) / 1000.0)));
            _rateTokens++;
        }
    }

    // ======================= mutation entry points =======================

    public Task<VerifyBundle> ClickAsync(CallContext cx, string? r, int? x, int? y, string button, int clicks, bool verify)
        => Mutate(cx, "pilot_click", new { @ref = r, x, y, button, clicks }, verify, async () =>
        {
            if (r is not null) { await uia.RunPattern(r, "focus", null); var pt = await CenterOf(r); await input.Click(pt.x, pt.y, button, clicks); }
            else if (x is not null && y is not null) await input.Click(x.Value, y.Value, button, clicks);
            else throw new PilotException("not_found", "click needs a ref or x,y", "pass ref (preferred) or coordinates");
        });

    public Task<VerifyBundle> DragAsync(CallContext cx, string? r, int? x1, int? y1, int x2, int y2, int steps, int durationMs, bool verify)
        => Mutate(cx, "pilot_drag", new { @ref = r, x1, y1, x2, y2, steps, duration_ms = durationMs }, verify, async () =>
        {
            if (r is not null)
            {
                var c = await CenterOf(r);
                await input.Drag(c.x, c.y, x2, y2, steps, durationMs: durationMs);
            }
            else
            {
                if (x1 is null || y1 is null)
                    throw new PilotException("not_found", "drag needs a source ref or x1,y1", "");
                await input.Drag(x1.Value, y1.Value, x2, y2, steps, durationMs: durationMs);
            }
        });

    public Task<VerifyBundle> ScrollAsync(CallContext cx, string? r, int? x, int? y, int dy, int dx, bool verify)
        => Mutate(cx, "pilot_scroll", new { @ref = r, x, y, delta_y = dy, delta_x = dx }, verify, async () =>
        {
            (int x, int y) pt = r is not null ? await CenterOf(r) : (x ?? 0, y ?? 0);
            await input.Wheel(pt.x, pt.y, dy, dx);
        });

    public Task<VerifyBundle> TypeTextAsync(CallContext cx, string text, string? r, string mode, bool verify)
        => Mutate(cx, "pilot_type_text", new { text, @ref = r, mode }, verify, async () =>
        {
            bool isPw = false;
            if (r is not null)
            {
                var node = await uia.NodeOfRef(r);
                isPw = node.IsPassword;
                if (node.Patterns.Contains("value") && mode == "auto" && !isPw)
                {
                    await uia.RunPattern(r, "value", text);   // pattern-first: no focus steal
                    return;
                }
                await uia.SetFocus(r);
                await Task.Delay(30);
            }
            await input.TypeText(text, Enum.Parse<InputInjector.TypeMode>(mode, true), isPw);
        });

    public Task<VerifyBundle> KeysAsync(CallContext cx, string keys, int repeat, bool verify)
        => Mutate(cx, "pilot_press_keys", new { keys, repeat }, verify,
            () => input.PressChords(keys, repeat));

    public Task<VerifyBundle> SetValueAsync(CallContext cx, string r, string value, bool verify)
        => Mutate(cx, "pilot_set_value", new { @ref = r, value }, verify,
            () => uia.RunPattern(r, "value", value));

    public Task<VerifyBundle> InvokeAsync(CallContext cx, string r, bool verify)
        => Mutate(cx, "pilot_invoke", new { @ref = r }, verify, () => uia.RunPattern(r, "invoke", null));

    public Task<VerifyBundle> ToggleAsync(CallContext cx, string r, bool verify)
        => Mutate(cx, "pilot_toggle", new { @ref = r }, verify, () => uia.RunPattern(r, "toggle", null));

    public Task<VerifyBundle> ExpandAsync(CallContext cx, string r, bool expand, bool verify)
        => Mutate(cx, "pilot_expand", new { @ref = r, expand }, verify, () => uia.RunPattern(r, "expand", expand));

    public Task<VerifyBundle> SelectAsync(CallContext cx, string r, bool verify)
        => Mutate(cx, "pilot_select", new { @ref = r }, verify, () => uia.RunPattern(r, "select", null));

    async Task<(int x, int y)> CenterOf(string r)
    {
        var node = await uia.NodeOfRef(r);
        if (node.Rect is null || node.Offscreen) throw PilotException.Offscreen(r);
        return (node.Rect.x + node.Rect.w / 2, node.Rect.y + node.Rect.h / 2);
    }

    // ========================== lifecycle ==========================

    public Task<VerifyBundle> LaunchAsync(CallContext cx, string cmd, string? args, string? workdir, int waitMs, bool verify)
        => Mutate(cx, "pilot_launch_app", new { cmd, args, workdir }, verify, async () =>
        {
            var si = new Native.STARTUPINFO { cb = System.Runtime.InteropServices.Marshal.SizeOf<Native.STARTUPINFO>() };
            if (!Native.CreateProcessW(cmd, args, IntPtr.Zero, IntPtr.Zero, false,
                    Native.CREATE_NEW_CONSOLE, IntPtr.Zero, workdir, ref si, out var pi))
            {
                // fall back to shell (verb resolution, .doc-style targets)
                var h = Native.ShellExecuteW(IntPtr.Zero, "open", cmd, args, workdir, 1);
                if ((long)h <= 32) throw new PilotException("not_found", $"launch failed: {cmd}", "use an absolute path or a shell-known command");
                return;
            }
            Native.CloseHandle(pi.hThread);
            events.Push("process_started", new { pid = pi.dwProcessId, cmd });
            Native.CloseHandle(pi.hProcess);
            // foreground-wait: the launched window (design §10 — built in so agents don't poll)
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Clamp(waitMs, 0, 60000));
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(150);
                var fg = Native.GetForegroundWindow();
                if (fg != IntPtr.Zero && Native.GetWindowThreadProcessId(fg, out var fgPid) != 0 && fgPid == pi.dwProcessId) break;
            }
        });

    public Task<VerifyBundle> KillAppAsync(CallContext cx, int? pid, string? processName)
        => Mutate(cx, "pilot_kill_app", new { pid, process = processName }, false, async () =>
        {
            var (fgProc, _) = ForegroundProcess();
            guard.CheckMutation(arm, ArmScope.act, fgProc);
            await Task.Run(() =>
            {
                var targets = pid is not null
                    ? new[] { Process.GetProcessById(pid.Value) }
                    : Process.GetProcessesByName(processName!);
                foreach (var p in targets)
                    try { Native.TerminateProcess(p.Handle, 0); events.Push("process_exited", new { pid = p.Id }); }
                    catch (Exception e) { log.LogWarning("kill {Pid}: {Err}", p.Id, e.Message); }
            });
        });

    // ========================== wait_for ==========================

    public Task<WaitForResult> WaitForAsync(WaitCondition cond, int timeoutS, int intervalMs)
        => new Waiter(uia, events, log).WaitAsync(cond, Math.Clamp(timeoutS, 1, 600), Math.Max(50, intervalMs));

    // ========================== lifecycle bits ==========================

    public object RedactedConfig() => cfg.RedactedView();
    public EventHub Events => events;
    public ArmState Arm => arm;
    public KillSwitch Kill => kill;
    public AuditLog Audit => audit;
    public InputInjector Input => input;
    public CaptureService Capture => capture;
}

public static class ToolSurface
{
    public static readonly string[] Names =
    [
        "pilot_status", "pilot_get_windows", "pilot_get_foreground", "pilot_get_tree",
        "pilot_find_elements", "pilot_get_element", "pilot_get_properties",
        "pilot_element_at", "pilot_screenshot", "pilot_get_events", "pilot_wait_for",
        "pilot_get_audit",
        "pilot_click", "pilot_drag", "pilot_scroll", "pilot_type_text", "pilot_press_keys",
        "pilot_set_value", "pilot_invoke", "pilot_toggle", "pilot_expand", "pilot_select",
        "pilot_launch_app", "pilot_kill_app", "pilot_arm", "pilot_panic",
    ];
}
