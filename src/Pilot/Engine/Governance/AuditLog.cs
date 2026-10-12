using System.Text.Json;

namespace WindowsPilot.Engine.Governance;

/// <summary>
/// Append-only JSONL audit (design §8.3): one line per tool call, both hosts,
/// with guard verdicts and the action's before/after shot ids. Redaction is
/// default-ON for text-bearing args; password elements hard-refuse paste and
/// get length-only audit regardless of config. Rotation: daily file, retain N.
/// </summary>
public sealed class AuditLog(PilotSettings cfg)
{
    readonly object _gate = new();
    long _seq;
    StreamWriter? _w;
    string _fileDay = "";

    public long Seq { get { lock (_gate) return _seq; } }

    string TodayPath() => Path.Combine(cfg.AuditDir, $"audit-{DateTime.Now:yyyyMMdd}.jsonl");

    StreamWriter Writer()
    {
        // caller holds _gate
        var day = DateTime.Now.ToString("yyyyMMdd");
        if (_w is not null && _fileDay == day) return _w;
        _w?.Dispose();
        _fileDay = day;
        RotateOld();
        _w = new StreamWriter(TodayPath(), append: true) { AutoFlush = true };
        if (_seq == 0 && File.Exists(TodayPath()))
            _seq = File.ReadLines(TodayPath()).Count(x => x.Length > 0) * 1; // resume seq after restart
        return _w;
    }

    void RotateOld()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-cfg.Audit.RetainDays);
            foreach (var f in Directory.GetFiles(cfg.AuditDir, "audit-*.jsonl"))
                if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f)[6..], "yyyyMMdd", null, default, out var d) && d < cutoff)
                    File.Delete(f);
        }
        catch { }
    }

    public long Write(string host, string tokenScope, string tool, object? args,
        object? target, object guard, object? res)
    {
        lock (_gate)
        {
            _seq++;
            var line = JsonSerializer.Serialize(new
            {
                ts = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz"),
                seq = _seq,
                host, tok = tokenScope, tool,
                args_red = Redactor.Redact(args, cfg.Audit.RedactText),
                target, guard, res,
            });
            Writer().WriteLine(line);
            return _seq;
        }
    }

    /// <summary>Tail for /api/audit + pilot_get_audit. newest-first, cheap scan.</summary>
    public (List<JsonElement> Items, long N) Tail(long sinceSeq, string? tool, string? host, int limit)
    {
        var outp = new List<JsonElement>();
        lock (_gate)
        {
            var files = Directory.GetFiles(cfg.AuditDir, "audit-*.jsonl").OrderByDescending(f => f).Take(3);
            foreach (var f in files)
            {
                string[] lines;
                try
                {
                    // this same process holds an append-writer on today's file with
                    // FileShare.Read; ReadAllLines demands FileShare.Write too and
                    // throws IOException — must open with ReadWrite explicitly.
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs);
                    lines = sr.ReadToEnd().Split('\n');
                }
                catch { continue; }
                for (int i = lines.Length - 1; i >= 0 && outp.Count < limit; i--)
                {
                    if (lines[i].Length == 0) continue;
                    JsonElement el;
                    try { el = JsonDocument.Parse(lines[i]).RootElement.Clone(); } catch { continue; }
                    var seq = el.TryGetProperty("seq", out var s) ? s.GetInt64() : 0;
                    if (sinceSeq > 0 && seq <= sinceSeq) return (outp, Seq);
                    if (tool is not null && !(el.TryGetProperty("tool", out var t) && t.GetString() == tool)) continue;
                    if (host is not null && !(el.TryGetProperty("host", out var h) && h.GetString() == host)) continue;
                    outp.Add(el);
                }
                if (outp.Count >= limit) break;
            }
        }
        return (outp, Seq);
    }
}
