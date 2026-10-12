using WindowsPilot.Core;
using WindowsPilot.Engine.Uia;

namespace WindowsPilot.Engine;

/// <summary>
/// Server-side wait conditions (design §17) — kills the LLM polling loop.
/// Polls on a timer against cheap UIA probes; timed out is ok:true,
/// matched:false (agents branch without exception noise).
/// </summary>
public sealed class WaitCondition
{
    public string Type { get; set; } = "";           // element_appears|element_gone|window_opened|window_closed|window_foreground|focused|text_matches|pixel_stable|action_settled
    public FindQuery? Query { get; set; }
    public string? TitleRegex { get; set; }
    public string? Process { get; set; }
    public string? Ref { get; set; }
    public string? Regex { get; set; }
    public string? Contains { get; set; }
    public int ForMs { get; set; } = 800;             // pixel_stable / action_settled window
}

public sealed class Waiter(UiaService uia, EventHub events, ILogger log)
{
    public async Task<WaitForResult> WaitAsync(WaitCondition c, int timeoutS, int intervalMs)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutS);
        var started = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            NodeDto? hit = c.Type switch
            {
                "element_appears" => await ProbeElement(c, expect: true),
                "element_gone" => await ProbeElement(c, expect: false),
                "window_opened" => ProbeWindow(c.TitleRegex ?? c.Process, expect: true),
                "window_closed" => ProbeWindow(c.TitleRegex ?? c.Process, expect: false),
                "window_foreground" => await ProbeForeground(c),
                "focused" => await ProbeElement(c, expect: true, focusedOnly: true),
                "text_matches" => await ProbeText(c),
                "pixel_stable" or "action_settled" => await ProbeSettled(c),
                _ => throw new PilotException("not_found", $"unknown wait condition '{c.Type}'",
                    "types: element_appears element_gone window_opened window_closed window_foreground focused text_matches pixel_stable action_settled"),
            };
            if (hit is not null)
                return new WaitForResult { Matched = true, WaitedMs = (long)(DateTime.UtcNow - started).TotalMilliseconds, Detail = hit };
            await Task.Delay(intervalMs);
        }
        return new WaitForResult { Matched = false, TimedOut = true, WaitedMs = (long)(DateTime.UtcNow - started).TotalMilliseconds };
    }

    async Task<NodeDto?> ProbeElement(WaitCondition c, bool expect, bool focusedOnly = false)
    {
        if (c.Query is null && c.Ref is null)
            throw new PilotException("not_found", "element wait needs a query",
                "pass query {name_contains|automation_id|control_type|...}");
        var found = await uia.Find(c.Query ?? new FindQuery { Ref = c.Ref }, 5);
        var first = found.FirstOrDefault();
        if (focusedOnly && first is not null)
        {
            var fe = await uia.FocusedElement();
            if (fe is null) return null;
            // cheap: name+control_type equality against focused
            try
            {
                if (fe.Name == first.Name && fe.ControlType.ToString() == first.ControlType) return first;
            }
            catch { }
            return null;
        }
        return expect ? first : (first is null ? new NodeDto { Ref = "-" } : null);
    }

    static NodeDto? ProbeWindow(string? needle, bool expect)
    {
        // pure Win32 enumeration — no UIA hop, cheap to poll (design §17)
        foreach (var t in UiaService.NativeTopWindows())
        {
            try
            {
                if (needle is null) continue;
                bool m = t.Title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!m)
                {
                    var proc = System.Diagnostics.Process.GetProcessById(t.Pid).ProcessName;
                    m = proc.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                }
                if (m) return expect ? new NodeDto { Ref = "-", Name = t.Title } : null;
            }
            catch { }
        }
        return expect ? null : new NodeDto { Ref = "-" };
    }

    async Task<NodeDto?> ProbeForeground(WaitCondition c)
    {
        var fe = await uia.Run(() =>
        {
            var hwnd = Interop.Native.GetForegroundWindow();
            var el = uia.TryFromHwnd(hwnd);
            return el is null ? null : uia.NodeOfOnThread(el, 0);
        }, "ProbeForeground");
        if (fe is null) return null;
        if (c.TitleRegex is not null && !System.Text.RegularExpressions.Regex.IsMatch(fe.Name ?? "", c.TitleRegex,
                System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100))) return null;
        return fe;
    }

    async Task<NodeDto?> ProbeText(WaitCondition c)
    {
        if (c.Ref is null) throw new PilotException("not_found", "text_matches needs a ref", "find the element first");
        var node = await uia.NodeOfRef(c.Ref);
        var text = node.TextSnippet ?? "";
        bool hit = c.Regex is not null
            ? System.Text.RegularExpressions.Regex.IsMatch(text, c.Regex, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100))
            : text.Contains(c.Contains ?? "", StringComparison.OrdinalIgnoreCase);
        return hit ? node with { TextSnippet = text } : null;
    }

    async Task<NodeDto?> ProbeSettled(WaitCondition c)
    {
        // UIA-quiet proxy: no new hub events across a full ForMs window ⇒ settled.
        // (Shot-diff based pixel_stable is the P2 upgrade; this keeps agents from
        // hammering screenshots while an animation/dialog settles.)
        long before = events.SinceSeq;
        await Task.Delay(c.ForMs);
        return events.SinceSeq == before ? new NodeDto { Ref = "-", Name = "settled" } : null;
    }
}
