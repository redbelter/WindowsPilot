using System.Collections.Concurrent;
using FlaUI.UIA3;
using FlaUI.Core.AutomationElements;
using WindowsPilot.Core;
using WindowsPilot.Interop;

namespace WindowsPilot.Engine.Uia;

/// <summary>
/// All FlaUI/UIA3 work runs on ONE dedicated STA thread with per-op timeouts
/// (design §9; lesson G10: STA discipline is non-negotiable and a hung UIA
/// provider must time out, never hang the engine). Callers submit closures;
/// we never block *on* the UIA thread itself (self-deadlock).
///
/// FlaUI 4.0.0 surface (resolved against the shipped assemblies by reflection):
///  - UIA3Automation lives in FlaUI.UIA3 (package FlaUI.UIA3)
///  - focused/point/handle: AutomationBase.FocusedElement()/FromPoint()/FromHandle()
///  - pattern access: el.Patterns.Invoke.TryGetPattern(out var p) etc.
///  - GetSupportedPatterns() returns FlaUI.Core.Identifiers.PatternId[] (.Name)
///  - deep props (pid/hwnd/focus/password) hang off el.Properties.*, each an
///    AutomationProperty&lt;T&gt; with ValueOrDefault — NEVER read raw.
///  - window title: Win32 GetWindowText is the truth (Properties.Name not guaranteed).
/// </summary>
public sealed class UiaService : IAsyncDisposable
{
    readonly BlockingCollection<Func<object?>> _q = new(400);
    readonly Thread _sta;
    readonly PilotSettings _cfg;
    readonly ILogger _log;
    readonly CancellationTokenSource _life = new();
    UIA3Automation? _automation;   // FlaUI.UIA3
    Timer? _sweeper;

    public ElementRegistry Registry { get; }
    public EventHub Events { get; }

    public UiaService(PilotSettings cfg, EventHub events, ILoggerFactory logs)
    {
        _cfg = cfg; Events = events; _log = logs.CreateLogger<UiaService>();
        Registry = new ElementRegistry(logs.CreateLogger<ElementRegistry>());
        _sta = new Thread(Run) { Name = "pilot-uia", IsBackground = true };
        _sta.SetApartmentState(ApartmentState.STA);
        _sta.Start();
    }

    void Run()
    {
        try
        {
            _automation = new UIA3Automation();
            _log.LogInformation("UIA3 automation initialized on STA thread");
        }
        catch (Exception e)
        {
            _log.LogError(e, "UIA init failed — is this an interactive session (Session 1)?");
        }

        _sweeper = Registry.StartSweeper(_life.Token);
        try
        {
            foreach (var f in _q.GetConsumingEnumerable(_life.Token))
            {
                try { f(); } catch { /* each op reports via its own TCS */ }
            }
        }
        catch (OperationCanceledException) { }
        _sweeper?.Dispose();
    }

    /// <summary>Submit a UIA op; runs on the STA thread; Timeout if the provider hangs.</summary>
    public Task<T> Run<T>(Func<T> op, string what, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_life.IsCancellationRequested || !_q.TryAdd(() =>
            {
                try { tcs.TrySetResult(op()); } catch (Exception e) { tcs.TrySetException(e); }
                return null;
            }))
            throw PilotException.SessionLost();

        return Wrap<T>(tcs.Task, what, ct);
    }

    async Task<T> Wrap<T>(Task<object?> inner, string what, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_cfg.Uia.PerOpTimeoutMs);
        try
        {
            var done = await Task.WhenAny(inner, Task.Delay(Timeout.Infinite, timeout.Token));
            if (done != inner) throw PilotException.Timeout(what);
            return (T)(await inner)!;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw PilotException.Timeout(what);
        }
    }

    UIA3Automation Aut => _automation ?? throw PilotException.SessionLost();

    // ================================ reads ================================

    /// <summary>Taskbar-style top-level window row (pure Win32 — thread-safe, no UIA hop needed).</summary>
    public record struct HwndTop(IntPtr Hwnd, int Pid, string Title);

    /// <summary>Static so cheap poll loops (Waiter) never touch the UIA thread.</summary>
    public static HwndTop[] NativeTopWindows()
    {
        // Top-level windows enumerated natively (taskbar semantics). FlaUI 4's
        // desktop.FindAllChildren() is unreliable as an alt-tab source (worker-w
        // /program-man noise), so we go straight to Win32 and wrap on demand.
        var list = new List<HwndTop>();
        Native.EnumWindows((h, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(h)) return true;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true; // owned → not taskbar-level
                var title = TitleOfNative(h);
                if (title.Length == 0) return true;
                Native.GetWindowThreadProcessId(h, out var pid);
                list.Add(new HwndTop(h, pid, title));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }

    public HwndTop[] NativeTopWindowsSync() => NativeTopWindows();

    /// <summary>Window title, Win32 truth. Call from any thread.</summary>
    public static string TitleOfNative(IntPtr hwnd)
    {
        try
        {
            int len = Native.GetWindowTextLength(hwnd);
            if (len == 0) return "";
            var sb = new System.Text.StringBuilder(len + 1);
            Native.GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return ""; }
    }

    public Task<AutomationElement?> FocusedElement() => Run(() =>
    {
        try { return Aut.FocusedElement(); } catch { return null; }
    }, "FocusedElement");

    public Task<AutomationElement?> ElementFromPoint(int x, int y) => Run(() =>
    {
        try { return Aut.FromPoint(new System.Drawing.Point(x, y)); } catch { return null; }
    }, "ElementFromPoint");

    public Task<AutomationElement?> FromHwnd(IntPtr hwnd) => Run(() => TryFromHwnd(hwnd), "FromHwnd");

    public Task<string> RegisterHwndRef(IntPtr hwnd) => Run(() =>
    {
        var el = TryFromHwnd(hwnd) ?? throw PilotException.NotFound($"hwnd {hwnd:x}");
        return Registry.Register(el);
    }, "RegisterHwndRef");

    /// <summary>Sync variants — ONLY legal when already executing on the UIA thread (inside Run).</summary>
    public string RegisterHwndRefSync(IntPtr hwnd)
    {
        var el = TryFromHwnd(hwnd) ?? throw PilotException.NotFound($"hwnd {hwnd:x}");
        return Registry.Register(el);
    }

    public (List<NodeDto> Nodes, bool Truncated) WalkTreeSync(string rootRef, int depth, int maxNodes, int budgetMs)
    {
        var rootEl = Registry.Resolve(rootRef).Element!;
        var nodes = new List<NodeDto>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool truncated = false;
        void Rec(AutomationElement el, int d)
        {
            if (nodes.Count >= maxNodes || sw.ElapsedMilliseconds > budgetMs || d > _cfg.Uia.MaxDepth)
            { truncated = true; return; }
            nodes.Add(NodeOf(el, d));
            if (d == depth) return;   // lazy — caller expands deeper explicitly
            AutomationElement[] kids;
            try { kids = el.FindAllChildren(); } catch { truncated = true; return; }
            foreach (var k in kids) Rec(k, d + 1);
        }
        Rec(rootEl, 0);
        return (nodes, truncated);
    }

    /// <summary>Sync node-map for an element already in hand (UIA thread only).</summary>
    public NodeDto NodeOfOnThread(AutomationElement el, int depth) => NodeOf(el, depth);

    /// <summary>Register focused element while on the UIA thread (engine closures only).</summary>
    public string? FocusedElementRefSync()
    {
        try { var fe = Aut.FocusedElement(); return fe is null ? null : Registry.Register(fe); }
        catch { return null; }
    }

    /// <summary>Must be called ON the UIA thread (via Run).</summary>
    public AutomationElement? TryFromHwnd(IntPtr hwnd)
    {
        try { return hwnd == IntPtr.Zero ? null : Aut.FromHandle(hwnd); } catch { return null; }
    }

    /// <summary>Budgeted tree walk (design §4.1): depth / max_nodes / ms all capped; returns partial + truncated.</summary>
    public Task<(List<NodeDto> Nodes, bool Truncated)> WalkTree(string rootRef, int depth, int maxNodes, int budgetMs)
        => Run(() => WalkTreeSync(rootRef, depth, maxNodes, budgetMs), "WalkTree");

    public Task<List<NodeDto>> Find(FindQuery q, int max) => Run(() =>
    {
        var outp = new List<NodeDto>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var r in Roots(q))
        {
            if (outp.Count >= max || sw.ElapsedMilliseconds > _cfg.Uia.BudgetMs) break;
            Collect(r, 0, q, outp, max, sw);
        }
        return outp;
    }, "Find");

    IEnumerable<AutomationElement> Roots(FindQuery q)
    {
        if (q.Ref is not null) { yield return Registry.Resolve(q.Ref).Element!; yield break; }
        if (q.Hwnd is not null)
        {
            var el = TryFromHwnd(ParseH(q.Hwnd));
            if (el is not null) { yield return el; yield break; }
            yield break;
        }
        if (q.Process is not null)
        {
            var found = new List<AutomationElement>();
            foreach (var t in NativeTopWindows())
            {
                try
                {
                    var proc = System.Diagnostics.Process.GetProcessById(t.Pid);
                    if (proc.ProcessName.Equals(q.Process, StringComparison.OrdinalIgnoreCase))
                    {
                        var el = TryFromHwnd(t.Hwnd);
                        if (el is not null) found.Add(el);
                    }
                }
                catch { }
            }
            foreach (var el in found) yield return el;   // yields not allowed inside try/catch — collect first
            yield break;
        }
        yield return Aut.GetDesktop();   // whole desktop — budgeted, so this stays bounded
    }

    void Collect(AutomationElement el, int d, FindQuery q, List<NodeDto> outp, int max, System.Diagnostics.Stopwatch sw)
    {
        if (outp.Count >= max || d > _cfg.Uia.MaxDepth || sw.ElapsedMilliseconds > _cfg.Uia.BudgetMs) return;
        if (Matches(el, q)) outp.Add(NodeOf(el, d));
        try { foreach (var k in el.FindAllChildren()) Collect(k, d + 1, q, outp, max, sw); } catch { }
    }

    static bool Matches(AutomationElement el, FindQuery q)
    {
        try
        {
            if (q.ControlType is not null && !SafeControlType(el).Equals(q.ControlType, StringComparison.OrdinalIgnoreCase)) return false;
            if (q.ClassName is not null && (el.ClassName ?? "").IndexOf(q.ClassName, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (q.AutomationId is not null && (el.AutomationId ?? "") != q.AutomationId) return false;
            var name = SafeName(el);
            if (q.NameContains is not null && name.IndexOf(q.NameContains, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (q.NameRegex is not null && !System.Text.RegularExpressions.Regex.IsMatch(
                    name, q.NameRegex, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100))) return false;
            // an empty query matches nothing — force callers to ask for something
            return q.NameContains is not null || q.NameRegex is not null || q.AutomationId is not null
                || q.ClassName is not null || q.ControlType is not null;
        }
        catch { return false; }
    }

    // =============================== actions ===============================

    /// <summary>
    /// Pattern-first action. The engine layer already ran guard+arm+deny checks.
    /// We probe supported patterns first so unsupported ones fail with a clean
    /// pattern_unsupported instead of a FlaUI exception.
    /// </summary>
    public Task RunPattern(string refId, string pattern, object? arg) => Run(() =>
    {
        var el = Registry.Resolve(refId).Element!;
        if (pattern == "focus") { el.Focus(); return true; }   // focus is a method, not a pattern

        if (!KnownPatterns.Contains(pattern))
            throw new PilotException("pattern_unsupported",
                $"unknown pattern '{pattern}'", "supported: invoke value toggle expand select scroll rangevalue transform text focus");
        if (!SupportsWire(el, pattern))
            throw PilotException.PatternUnsupported(refId, pattern);

        switch (pattern)
        {
            case "invoke":
                if (el.Patterns.Invoke.TryGetPattern(out var inv)) inv.Invoke();
                break;
            case "toggle":
                if (el.Patterns.Toggle.TryGetPattern(out var tog)) tog.Toggle();
                break;
            case "value":
                if (el.Patterns.Value.TryGetPattern(out var val)) val.SetValue((string)arg!);
                break;
            case "expand":
                if (el.Patterns.ExpandCollapse.TryGetPattern(out var ec))
                {
                    if (Convert.ToBoolean(arg!)) ec.Expand(); else ec.Collapse();
                }
                break;
            case "select":
                if (el.Patterns.SelectionItem.TryGetPattern(out var sel)) sel.Select();
                break;
            default:
                throw PilotException.PatternUnsupported(refId, pattern);
        }
        return true;
    }, $"{pattern}({refId})");

    public Task SetFocus(string refId) => RunPattern(refId, "focus", null);

    public Task<NodeDto> NodeOfRef(string refId) => Run(() => NodeOf(Registry.Resolve(refId).Element!, 0), "NodeOf");

    public Task<Dictionary<string, string>> RawProperties(string refId) => Run(() =>
    {
        var el = Registry.Resolve(refId).Element!;
        var d = new Dictionary<string, string>();
        void Try(string k, Func<string> f) { try { d[k] = f(); } catch { d[k] = "<error>"; } }
        Try("name", () => el.Name ?? ""); Try("automation_id", () => el.AutomationId ?? "");
        Try("class_name", () => el.ClassName ?? ""); Try("control_type", () => el.ControlType.ToString());
        Try("enabled", () => el.IsEnabled.ToString()); Try("offscreen", () => el.IsOffscreen.ToString());
        Try("focused", () => PBool(() => el.Properties.HasKeyboardFocus).ToString());
        Try("rect", () => el.BoundingRectangle.ToString());
        Try("is_password", () => PBool(() => el.Properties.IsPassword).ToString());
        Try("hwnd", () => "0x" + PtrProp(() => el.Properties.NativeWindowHandle).ToString("x"));
        Try("process_id", () => PInt(() => el.Properties.ProcessId).ToString());
        Try("framework_id", () => PStr(() => el.Properties.FrameworkId));
        Try("help_text", () => el.HelpText ?? "");
        Try("itemstatus", () => el.ItemStatus ?? "");
        Try("itemtype", () => PStr(() => el.Properties.ItemType));
        d["patterns"] = string.Join(",", SupportedWirePatterns(el));
        return d;
    }, "RawProperties");

    // ================================ mapping ================================

    public NodeDto NodeOf(AutomationElement el, int depth)
    {
        string? name = null, aid = null, cls = null; bool enabled = true, offscreen = false, pw = false;
        RectDto? rect = null;
        try { name = el.Name; } catch { }
        try { aid = el.AutomationId; } catch { }
        try { cls = el.ClassName; } catch { }
        try { enabled = el.IsEnabled; } catch { }
        try { offscreen = el.IsOffscreen; } catch { }
        try { pw = PBool(() => el.Properties.IsPassword); } catch { }
        try
        {
            var r = el.BoundingRectangle;
            if (r.Width > 0 && r.Height > 0)
                rect = new RectDto((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
        }
        catch { }

        var pats = SupportedWirePatterns(el);
        return new NodeDto
        {
            Ref = Registry.Register(el),
            Name = Trunc(name, 200), AutomationId = aid, ClassName = cls,
            ControlType = SafeControlType(el), Enabled = enabled, Offscreen = offscreen, IsPassword = pw,
            Rect = rect, Patterns = pats, Actionable = enabled && !offscreen && pats.Length > 0,
            TextSnippet = Trunc(FirstText(el, pats), 80), Depth = depth, Children = -1,
        };
    }

    static readonly string[] KnownPatterns = ["invoke", "value", "toggle", "expand", "select", "scroll", "rangevalue", "transform", "text"];

    public static string[] SupportedWirePatterns(AutomationElement el)
    {
        try
        {
            return el.GetSupportedPatterns()
                .Select(pid => WireOfPatternName(pid.Name))
                .Where(w => w is not null)
                .Distinct().ToArray()!;
        }
        catch { return []; }
    }

    /// <summary>FlaUI PatternId.Name → wire name. Name casing/suffix varies by
    /// FlaUI build ("InvokePattern" vs "Invoke"); normalize the suffix instead
    /// of exact-matching, else every pattern silently drops and nothing is
    /// ever actionable.</summary>
    static string? WireOfPatternName(string pidName)
    {
        var n = (pidName ?? "").Trim();
        if (n.EndsWith("Pattern", StringComparison.OrdinalIgnoreCase))
            n = n[..^"Pattern".Length];
        return n.ToLowerInvariant() switch
        {
            "invoke" => "invoke",
            "value" => "value",
            "toggle" => "toggle",
            "expandcollapse" or "expand" => "expand",
            "selectionitem" or "select" => "select",
            "scroll" => "scroll",
            "rangevalue" => "rangevalue",
            "transform" or "transform2" => "transform",
            "text" or "text2" => "text",
            _ => null,
        };
    }

    internal static bool SupportsWire(AutomationElement el, string wire)
    {
        try { return SupportedWirePatterns(el).Contains(wire); } catch { return false; }
    }

    static string FirstText(AutomationElement el, string[] pats)
    {
        try
        {
            if (pats.Contains("value") && el.Patterns.Value.TryGetPattern(out var v))
                return v.Value.ValueOrDefault ?? "";
            if (pats.Contains("text") && el.Patterns.Text.TryGetPattern(out var t))
            {
                try { return t.DocumentRange.GetText(-1) ?? ""; } catch { }
            }
        }
        catch { }
        return "";
    }

    // ---- property helpers: FlaUI 4 deep props live on Properties.* and are
    // ---- AutomationProperty&lt;T&gt;; always read ValueOrDefault, never raw. ----

    static string PStr(Func<FlaUI.Core.AutomationProperty<string>> f) { try { return f().ValueOrDefault ?? ""; } catch { return ""; } }
    static bool PBool(Func<FlaUI.Core.AutomationProperty<bool>> f) { try { return f().ValueOrDefault; } catch { return false; } }
    static int PInt(Func<FlaUI.Core.AutomationProperty<int>> f) { try { return f().ValueOrDefault; } catch { return 0; } }
    static long PtrProp(Func<FlaUI.Core.AutomationProperty<IntPtr>> f) { try { return f().ValueOrDefault.ToInt64(); } catch { return 0; } }

    static string SafeName(AutomationElement el) { try { return el.Name ?? ""; } catch { return ""; } }
    static string SafeControlType(AutomationElement el) { try { return el.ControlType.ToString(); } catch { return "Custom"; } }

    public static string Trunc(string? s, int n) => s is null ? "" : (s.Length <= n ? s : s[..n]);

    public static IntPtr ParseH(string hex) =>
        new(Convert.ToInt64(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex, 16));

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        try { _q.CompleteAdding(); } catch { }
        try { _automation?.Dispose(); } catch { }
        await Task.CompletedTask;
    }
}

public sealed class FindQuery
{
    public string? Ref { get; set; }
    public string? Hwnd { get; set; }
    public string? Process { get; set; }
    public string? NameRegex { get; set; }
    public string? NameContains { get; set; }
    public string? AutomationId { get; set; }
    public string? ClassName { get; set; }
    public string? ControlType { get; set; }
}
