using System.Text.Json.Serialization;

namespace WindowsPilot.Core;

// ---------- DTOs on the wire (MCP + REST share these byte-for-byte) ----------

public sealed record RectDto(int x, int y, int w, int h);

public sealed record NodeDto
{
    [JsonPropertyName("ref")] public string Ref { get; init; } = "";
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("automation_id")] public string? AutomationId { get; init; }
    [JsonPropertyName("class_name")] public string? ClassName { get; init; }
    [JsonPropertyName("control_type")] public string? ControlType { get; init; }
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("offscreen")] public bool Offscreen { get; init; }
    [JsonPropertyName("is_password")] public bool IsPassword { get; init; }
    [JsonPropertyName("rect")] public RectDto? Rect { get; init; }
    [JsonPropertyName("patterns")] public string[] Patterns { get; init; } = [];
    [JsonPropertyName("actionable")] public bool Actionable { get; init; }
    [JsonPropertyName("text_snippet")] public string? TextSnippet { get; init; }
    [JsonPropertyName("depth")] public int Depth { get; init; }
    [JsonPropertyName("children")] public int? Children { get; init; } // -1 unknown / count
}

public sealed record WindowDto
{
    [JsonPropertyName("ref")] public string Ref { get; init; } = "";
    [JsonPropertyName("hwnd")] public string Hwnd { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("process")] public string Process { get; init; } = "";
    [JsonPropertyName("pid")] public int Pid { get; init; }
    [JsonPropertyName("fg")] public bool Fg { get; init; }
    [JsonPropertyName("minimized")] public bool Minimized { get; init; }
    [JsonPropertyName("rect")] public RectDto? Rect { get; init; }
    [JsonPropertyName("focused")] public string? Focused { get; init; }
}

/// <summary>Every image on the wire carries its own inverse transform (design §6).</summary>
public sealed record ShotMeta
{
    [JsonPropertyName("width")] public int Width { get; init; }
    [JsonPropertyName("height")] public int Height { get; init; }
    [JsonPropertyName("scale")] public double Scale { get; init; }
    [JsonPropertyName("source_origin")] public RectDto SourceOrigin { get; init; } = new(0, 0, 0, 0);
    [JsonPropertyName("monitor")] public int Monitor { get; init; }
    [JsonPropertyName("capture_tier")] public string CaptureTier { get; init; } = "gdi";
}

public sealed record MarkDto
{
    [JsonPropertyName("n")] public int N { get; init; }
    [JsonPropertyName("ref")] public string Ref { get; init; } = "";
    [JsonPropertyName("rect")] public RectDto Rect { get; init; } = new(0, 0, 0, 0);
    [JsonPropertyName("control_type")] public string? ControlType { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
}

/// <summary>Uniform success/failure envelope (design §16).</summary>
public sealed record Ok
{
    [JsonPropertyName("ok")] public bool Ok_ { get; init; } = true;
}

public sealed record Err
{
    [JsonPropertyName("ok")] public bool Ok_ { get; init; } = false;
    [JsonPropertyName("error")] public string Error { get; init; } = "";
    [JsonPropertyName("msg")] public string Msg { get; init; } = "";
    [JsonPropertyName("hint")] public string Hint { get; init; } = "";
    [JsonPropertyName("retry")] public bool Retry { get; init; }
    [JsonPropertyName("retry_after_s")] public int? RetryAfterS { get; init; }
    [JsonPropertyName("was")] public FingerprintDto? Was { get; init; }
}

public sealed record FingerprintDto
{
    [JsonPropertyName("hwnd")] public string? Hwnd { get; init; }
    [JsonPropertyName("control_type")] public string? ControlType { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("automation_id")] public string? AutomationId { get; init; }
    [JsonPropertyName("class_name")] public string? ClassName { get; init; }
    [JsonPropertyName("path")] public string[]? Path { get; init; }
}

/// <summary>Returned by every mutating tool unless verify:false (design §10).</summary>
public sealed record VerifyBundle
{
    [JsonPropertyName("ok")] public bool Ok_ { get; init; } = true;
    [JsonPropertyName("ms")] public long Ms { get; init; }
    [JsonPropertyName("before_shot")] public string? BeforeShot { get; init; }   // /api/shots/<id>.jpg
    [JsonPropertyName("after_shot")] public string? AfterShot { get; init; }
    [JsonPropertyName("shot_scale")] public double ShotScale { get; init; } = 0.5;
    [JsonPropertyName("tree_delta")] public DeltaOp[]? TreeDelta { get; init; }
    [JsonPropertyName("audit_seq")] public long AuditSeq { get; init; }
    [JsonPropertyName("dropped_inputs")] public int DroppedInputs { get; init; }
}

public sealed record DeltaOp
{
    [JsonPropertyName("op")] public string Op { get; init; } = "+"; // + | ~ | -
    [JsonPropertyName("el")] public NodeDto? El { get; init; }
    [JsonPropertyName("ref")] public string? Ref { get; init; }
    [JsonPropertyName("changed")] public string[]? Changed { get; init; }
}

/// <summary>Arm-state projection for status bars (design §8.1).</summary>
public sealed record ArmInfo
{
    [JsonPropertyName("armed")] public string Armed { get; init; } = "read";
    [JsonPropertyName("expires")] public long? Expires { get; init; }
    [JsonPropertyName("why")] public string? Why { get; init; }
    [JsonPropertyName("scope_required_for_actions")] public string Required { get; init; } = "act";
}

public sealed class WaitForResult
{
    [JsonPropertyName("ok")] public bool Ok_ { get; init; } = true;
    [JsonPropertyName("matched")] public bool Matched { get; init; }
    [JsonPropertyName("waited_ms")] public long WaitedMs { get; init; }
    [JsonPropertyName("detail")] public NodeDto? Detail { get; init; }
    [JsonPropertyName("timed_out")] public bool TimedOut { get; init; }
}

public sealed record MonitorDto(int index, int x, int y, int w, int h, int dpi, double scale)
{
    [JsonPropertyName("primary")] public bool Primary { get; init; }
}
