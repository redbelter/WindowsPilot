using System.Collections.Concurrent;
using WindowsPilot.Core;

namespace WindowsPilot.Engine.Uia;

/// <summary>
/// Element registry (design §5). Hosts (LLM/web) only ever see opaque refs.
/// Dead UIA elements never crash the engine: resolve throws StaleRef carrying
/// the Fingerprint so a re-find is ONE cheap call — and crucially we REFUSE
/// to silently auto-resolve (acting on the wrong element is the worst
/// available failure mode, worse than failing).
///
/// A process-wide `generation` bumps on engine restart so long-running agent
/// loops can detect "every ref you hold is dead" in one comparison.
/// </summary>
public sealed class ElementRegistry
{
    public sealed class Entry
    {
        public required string Id { get; init; }
        public FlaUI.Core.AutomationElements.AutomationElement? Element { get; set; } // strong, per-ref
        public required FingerprintDto Fingerprint { get; init; }
        public DateTime Created { get; init; } = DateTime.UtcNow;
        public DateTime LastHit { get; set; } = DateTime.UtcNow;
    }

    readonly ConcurrentDictionary<string, Entry> _byId = new();
    TimeSpan _ttl = TimeSpan.FromMinutes(10);

    public long Generation { get; } = DateTime.UtcNow.Ticks;   // one per process lifetime

    static readonly char[] Alpha = "abcdefghijklmnopqrstuvwxyz234567".ToCharArray();

    /// <summary>Register (or reuse via identity) and return the wire ref string.</summary>
    public string Register(FlaUI.Core.AutomationElements.AutomationElement el)
    {
        // reuse: UIA RuntimeId is a cheap identity for dedupe within this process
        var key = IdentityKey(el);
        foreach (var e in _byId.Values)
            if (e.Element is not null && IdentityKey(e.Element) == key)
            {
                e.LastHit = DateTime.UtcNow;
                e.Element = el;
                return e.Id;
            }

        string id;
        do { id = "e-" + NewId(); } while (!_byId.TryAdd(id, new Entry
        {
            Id = id, Element = el, Fingerprint = FingerprintOf(el),
        }));
        return id;
    }

    /// <summary>Resolve a ref → live element. Dead refs get ONE cheap auto-rebind
    /// via RebindHook (match by automation_id/name/class + rect proximity against
    /// the fresh tree); only if that misses do we throw stale_ref. A successful
    /// rebind keeps the OLD ref id working — clients never need to re-find.</summary>
    public Entry Resolve(string refId)
    {
        if (!_byId.TryGetValue(refId, out var e))
            throw PilotException.StaleRef(null); // never registered / swept
        if (e.Element is not null && Alive(e.Element))
        {
            e.LastHit = DateTime.UtcNow;
            return e;
        }
        FlaUI.Core.AutomationElements.AutomationElement? fresh = null;
        try { fresh = RebindHook?.Invoke(e.Fingerprint); } catch { }
        if (fresh is not null && Alive(fresh))
        {
            var reb = new Entry { Id = refId, Element = fresh, Fingerprint = FingerprintOf(fresh) };
            _byId[refId] = reb;               // old ref keeps working on the fresh element
            return reb;
        }
        _byId.TryRemove(refId, out _);
        throw PilotException.StaleRef(e.Fingerprint);
    }

    /// <summary>Set by UiaService: re-find a fingerprint's element in the current
    /// tree (runs on the UIA STA thread — the caller's thread is fine too, FlaUI
    /// proxies). Match order: automation_id > name > class, rect proximity tiebreak.</summary>
    public Func<FingerprintDto, FlaUI.Core.AutomationElements.AutomationElement?>? RebindHook { get; set; }

    /// <summary>Budgeted near-match search under one root (depth ≤ 6, ≤ 150 ms).
    /// Shared by the rebind pass; returns the candidate closest to the old rect.</summary>
    public static FlaUI.Core.AutomationElements.AutomationElement? MatchNear(
        FlaUI.Core.AutomationElements.AutomationElement root, FingerprintDto fp)
    {
        FlaUI.Core.AutomationElements.AutomationElement? best = null;
        double bestDist = double.MaxValue;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int seen = 0;
        void Rec(FlaUI.Core.AutomationElements.AutomationElement el, int d)
        {
            if (best is not null && bestDist <= 8) return;                 // good enough
            if (d > 6 || sw.ElapsedMilliseconds > 150 || ++seen > 2000) return;
            if (FpMatch(el, fp))
            {
                double dist = RectDistance(el, fp);
                if (dist < bestDist) { bestDist = dist; best = el; }
            }
            FlaUI.Core.AutomationElements.AutomationElement[] kids;
            try { kids = el.FindAllChildren(); } catch { return; }
            foreach (var k in kids) Rec(k, d + 1);
        }
        Rec(root, 0);
        return best;
    }

    static bool FpMatch(FlaUI.Core.AutomationElements.AutomationElement el, FingerprintDto fp)
    {
        try
        {
            if (fp.AutomationId is { Length: > 0 })
                return (el.AutomationId ?? "") == fp.AutomationId;
            if (fp.Name is { Length: > 0 } nm)
                return (el.Name ?? "").Contains(nm, StringComparison.OrdinalIgnoreCase);
            if (fp.ClassName is { Length: > 0 } cls)
                return (el.ClassName ?? "").Contains(cls, StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        return false;
    }

    static double RectDistance(FlaUI.Core.AutomationElements.AutomationElement el, FingerprintDto fp)
    {
        if (fp.Rect is not { Length: 4 } orr) return 1;   // no rect info: first match wins
        try
        {
            var r = el.BoundingRectangle;
            double dx = r.X + r.Width / 2 - (orr[0] + orr[2] / 2.0);
            double dy = r.Y + r.Height / 2 - (orr[1] + orr[3] / 2.0);
            return Math.Sqrt(dx * dx + dy * dy);
        }
        catch { return 2; }
    }

    public FingerprintDto? FingerprintOf(string refId) =>
        _byId.TryGetValue(refId, out var e) ? e.Fingerprint : null;

    public int Count => _byId.Count;

    public void Sweep()
    {
        var cutoff = DateTime.UtcNow - _ttl;
        foreach (var e in _byId.Values)
            if (e.LastHit < cutoff || e.Element is null)
                _byId.TryRemove(e.Id, out _);
    }

    /// <summary>TTL sweeper on a plain timer (registry metadata ops are dictionary-only).</summary>
    public Timer StartSweeper(CancellationToken life) =>
        new(_ => { try { Sweep(); } catch { } }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

    public void Clear() => _byId.Clear();

    // ---------------------------------------------------------------- utils

    static string NewId()
    {
        var n = Interlocked.Increment(ref _globalCounter);
        Span<char> buf = stackalloc char[6];
        for (int i = 0; i < 6; i++) { buf[i] = Alpha[(int)(n >> (i * 5) & 31)]; }
        return new string(buf);
    }
    static int _globalCounter = Random.Shared.Next(100000);

    static bool Alive(FlaUI.Core.AutomationElements.AutomationElement el)
    {
        try { _ = el.Name; return true; }   // any property read on a dead element throws
        catch { return false; }
    }

    static string IdentityKey(FlaUI.Core.AutomationElements.AutomationElement el)
    {
        // FlaUI 4: RuntimeId lives on Properties as AutomationProperty<int[]>;
        // fall back to hwnd+hashcode for provider quirks.
        try
        {
            var rid = el.Properties.RuntimeId.ValueOrDefault;
            if (rid is { Length: > 0 }) return string.Join(",", rid);
        }
        catch { }
        try { return el.Properties.NativeWindowHandle.ValueOrDefault.ToInt64().ToString() + ":" + el.GetHashCode(); }
        catch { return el.GetHashCode().ToString(); }
    }

    public static FingerprintDto FingerprintOf(FlaUI.Core.AutomationElements.AutomationElement el)
    {
        string? name = null, aid = null, cls = null, ctype = null, hwnd = null;
        int[]? rect = null;
        try { name = el.Name; } catch { }
        try { aid = el.AutomationId; } catch { }
        try { cls = el.ClassName; } catch { }
        try { ctype = el.ControlType.ToString(); } catch { }
        try { hwnd = "0x" + el.Properties.NativeWindowHandle.ValueOrDefault.ToInt64().ToString("x"); } catch { }
        try
        {
            var r = el.BoundingRectangle;
            if (r.Width > 0 && r.Height > 0) rect = [(int)r.X, (int)r.Y, (int)r.Width, (int)r.Height];
        }
        catch { }
        return new FingerprintDto
        {
            Name = name, AutomationId = aid, ClassName = cls, ControlType = ctype, Hwnd = hwnd,
            Path = PathOf(el), Rect = rect,
        };
    }

    /// <summary>≤6-level ancestor path with sibling-index disambiguation (design §5).</summary>
    static string[] PathOf(FlaUI.Core.AutomationElements.AutomationElement el)
    {
        var path = new List<string>();
        var cur = (FlaUI.Core.AutomationElements.AutomationElement?)el;
        for (int depth = 0; depth < 6 && cur is not null; depth++)
        {
            try
            {
                var parent = cur.Parent;
                int idx = 0;
                if (parent is not null)
                {
                    FlaUI.Core.AutomationElements.AutomationElement[] sibs;
                    try { sibs = parent.FindAllChildren(); } catch { sibs = []; }
                    foreach (var sib in sibs)
                    {
                        if (SameElement(sib, cur)) break;
                        idx++;
                    }
                }
                path.Add($"{cur.ControlType}#{idx}");
                cur = parent;
            }
            catch { break; }
        }
        path.Reverse();
        return [.. path];
    }

    /// <summary>FlaUI element wrappers are not reference-stable; RuntimeId is.</summary>
    static bool SameElement(FlaUI.Core.AutomationElements.AutomationElement a,
                            FlaUI.Core.AutomationElements.AutomationElement b)
    {
        try { return a.Equals(b); } catch { return ReferenceEquals(a, b); }
    }
}
