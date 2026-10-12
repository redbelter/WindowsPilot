using System.Text.Json;

namespace WindowsPilot.Engine.Governance;

/// <summary>
/// Two guardrails: (1) denylist of foreground processes where ALL mutation
/// tools refuse (UAC/credential UI first-class; user globs configurable) —
/// reads are never denied; (2) arg redaction rules for the audit log.
/// </summary>
public sealed class Guard(PilotSettings cfg)
{
    static readonly string[] DefaultDeny =
    [
        "consent", "useraccountbroker", "credentialdialoghost",
        "userconsentverifier", "logonui", "taskmgr-elevated",
    ];

    public string? DenyCheck(string foregroundProcess)
    {
        if (string.IsNullOrEmpty(foregroundProcess)) return null;
        var name = Path.GetFileNameWithoutExtension(foregroundProcess).ToLowerInvariant();
        foreach (var d in DefaultDeny)
            if (name.Contains(d, StringComparison.OrdinalIgnoreCase)) return d;
        foreach (var g in cfg.Governance.DenylistExtra)
            if (Match(name, g.ToLowerInvariant())) return g;
        return null;
    }

    static bool Match(string name, string glob)
    {
        if (!glob.Contains('*')) return name == glob || name.Contains(glob);
        var parts = glob.Split('*');
        int pos = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            var idx = name.IndexOf(parts[i], pos, StringComparison.Ordinal);
            if (idx < 0) return false;
            pos = idx + parts[i].Length;
        }
        if (!glob.EndsWith('*') && !name.EndsWith(parts[^1], StringComparison.Ordinal)) return false;
        return true;
    }

    public void CheckMutation(ArmState arm, ArmScope scope, string? foregroundProcess)
    {
        arm.Check(scope);
        if (foregroundProcess is not null && DenyCheck(foregroundProcess) is { } hit)
            throw PilotException.DeniedApp(foregroundProcess);
    }
}

/// <summary>Audit args → safe view: length-only for text payloads.</summary>
public static class Redactor
{
    static readonly string[] SensitiveKeys = ["text", "value", "keys", "command", "password", "secret", "token"];

    public static object Redact(object? args, bool redactText)
    {
        if (args is null) return null!;
        if (!redactText) return args;
        try
        {
            var el = JsonSerializer.SerializeToElement(args);
            return Walk(el, redactText);
        }
        catch { return "[unserializable]"; }
    }

    static object Walk(JsonElement el, bool redact)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var d = new Dictionary<string, object>();
                foreach (var p in el.EnumerateObject())
                {
                    bool sensitive = redact && Array.Exists(SensitiveKeys,
                        k => p.Name.Contains(k, StringComparison.OrdinalIgnoreCase));
                    d[p.Name] = sensitive && p.Value.ValueKind == JsonValueKind.String
                        ? $"[REDACTED len {p.Value.GetString()?.Length ?? 0}]"
                        : Walk(p.Value, redact);
                }
                return d;
            case JsonValueKind.Array:
                return el.EnumerateArray().Select(x => Walk(x, redact)).ToList();
            default:
                return el.ToString();
        }
    }
}
