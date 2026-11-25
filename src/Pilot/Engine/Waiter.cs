using WindowsPilot.Core;
using WindowsPilot.Engine.Capture;
using WindowsPilot.Engine.Uia;

namespace WindowsPilot.Engine;

/// <summary>
/// Server-side wait conditions (design §17) — kills the LLM polling loop.
/// Polls on a timer against cheap UIA probes; timed out is ok:true,
/// matched:false (agents branch without exception noise).
/// </summary>
public sealed class WaitCondition
{
    public string Type { get; set; } = "";           // element_appears|element_gone|window_opened|window_closed|window_foreground|focused|text_matches|pixel_stable|action_settled|ocr_text
    public FindQuery? Query { get; set; }
    public string? TitleRegex { get; set; }
    public string? Process { get; set; }
    public string? Ref { get; set; }
    public string? Regex { get; set; }
    public string? Contains { get; set; }
    public int ForMs { get; set; } = 800;             // pixel_stable / action_settled window
}

public sealed class Waiter(UiaService uia, EventHub events, CaptureService capture)
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
                "ocr_text" => await ProbeOcrText(c),
                "pixel_stable" or "action_settled" => await ProbeSettled(c),
                _ => throw new PilotException("not_found", $"unknown wait condition '{c.Type}'",
                    "types: element_appears element_gone window_opened window_closed window_foreground focused text_matches ocr_text pixel_stable action_settled"),
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
                    using var proc = System.Diagnostics.Process.GetProcessById(t.Pid);
                    m = proc.ProcessName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
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

    async Task<NodeDto?> ProbeOcrText(WaitCondition c)
    {
        // canvas-UI read-back: OCR the live screen and match, collapses the
        // screenshot→OCR→compare dance into one bounded poll (design §17).
        if (c.Contains is null && c.Regex is null)
            throw new PilotException("not_found", "ocr_text needs contains or regex",
                "{\"type\":\"ocr_text\",\"contains\":\"...\")");
        var frame = await Task.Run(() => capture.CaptureScreen(0));
        var words = await Task.Run(() => Vision.OcrService.RecognizeWords(frame.Jpeg));
        var text = string.Join(" ", words.Select(w => w.Text));
        // word boxes split tokens; compare whitespace-insensitively so "PW 7185"
        // still satisfies contains("PW7185")
        var tight = string.Concat(words.Select(w => w.Text));
        bool Hit(string hay) => c.Regex is not null
            ? System.Text.RegularExpressions.Regex.IsMatch(hay, c.Regex, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(200))
            : hay.Contains(c.Contains!, StringComparison.OrdinalIgnoreCase);
        bool hit = Hit(text) || Hit(tight);
        return hit ? new NodeDto { Ref = "-", Name = "ocr", TextSnippet = text.Length > 200 ? text[..200] : text } : null;
    }

    async Task<NodeDto?> ProbeSettled(WaitCondition c)
    {
        // Pixel-truth stability: two captures a ForMs window apart that look the
        // same (8x8 grayscale phash, CaptureService.LooksChanged) ⇒ settled. This
        // is what render/animation waits actually want — canvas UIs push zero UIA
        // events, so the old event-quiet proxy matched instantly and lied.
        var first = await Task.Run(() => capture.CaptureScreen(0));
        await Task.Delay(c.ForMs);
        var second = await Task.Run(() => capture.CaptureScreen(0));
        using var a = new System.Drawing.Bitmap(new MemoryStream(first.Jpeg));
        using var b = new System.Drawing.Bitmap(new MemoryStream(second.Jpeg));
        if (CaptureService.LooksChanged(a, b)) return null;
        // belt+braces: also require the UIA lane to be quiet
        long seqA = events.SinceSeq;
        await Task.Delay(Math.Max(50, c.ForMs / 4));
        return events.SinceSeq == seqA ? new NodeDto { Ref = "-", Name = "settled" } : null;
    }
}
