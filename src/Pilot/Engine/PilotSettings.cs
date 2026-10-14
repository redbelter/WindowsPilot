using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsPilot.Engine;

/// <summary>
/// Effective config. Load order: defaults &lt; pilot.json (%LOCALAPPDATA%\WindowsPilot
/// or PILOT_CONFIG) &lt; env (PILOT_*). Env wins so the rig can flip flags without
/// editing files inside fresh VMs. Mirrors hyperv's env-first habits
/// (HYPERV_SVC_PORT / HYPERV_ROOT).
/// </summary>
public sealed class PilotSettings
{
    // pilot.json is snake_case (matches config/pilot.example.json — the contract).
    // Case-insensitive binding does NOT map "auto_arm"→AutoArm, so every
    // multi-word property needs an explicit [JsonPropertyName]. (Found live in
    // the P0 rig: snake keys silently fell back to defaults otherwise.)
    [JsonPropertyName("bind")] public string Bind { get; set; } = "0.0.0.0";   // rig needs host→guest reach; real desktops set loopback
    [JsonPropertyName("port")] public int Port { get; set; } = 8791;
    [JsonPropertyName("data_dir")] public string DataDir { get; set; } = Default();

    [JsonPropertyName("governance")] public GovernanceCfg Governance { get; set; } = new();
    [JsonPropertyName("uia")] public UiaCfg Uia { get; set; } = new();
    [JsonPropertyName("capture")] public CaptureCfg Capture { get; set; } = new();
    [JsonPropertyName("audit")] public AuditCfg Audit { get; set; } = new();
    [JsonPropertyName("vision")] public VisionCfg Vision { get; set; } = new();
    [JsonPropertyName("rig")] public RigCfg Rig { get; set; } = new();

    public sealed class GovernanceCfg
    {
        [JsonPropertyName("allow_mcp_arm")] public bool AllowMcpArm { get; set; } = false;
        [JsonConverter(typeof(JsonStringEnumConverter<AutoArm>))]
        [JsonPropertyName("auto_arm")] public AutoArm AutoArm { get; set; } = AutoArm.never;
        [JsonPropertyName("supervised")] public bool Supervised { get; set; } = false;
        [JsonPropertyName("rate_per_min")] public int RatePerMin { get; set; } = 60;
        [JsonPropertyName("rate_burst")] public int RateBurst { get; set; } = 20;
        [JsonPropertyName("arm_ttl_min")] public int ArmTtlMin { get; set; } = 15;
        [JsonPropertyName("denylist_extra")] public string[] DenylistExtra { get; set; } = [];
    }

    public enum AutoArm { never, startup, always }

    public sealed class UiaCfg
    {
        [JsonPropertyName("per_op_timeout_ms")] public int PerOpTimeoutMs { get; set; } = 5000;
        [JsonPropertyName("max_nodes")] public int MaxNodes { get; set; } = 2000;
        [JsonPropertyName("budget_ms")] public int BudgetMs { get; set; } = 2000;
        [JsonPropertyName("max_depth")] public int MaxDepth { get; set; } = 25;
    }

    public sealed class CaptureCfg
    {
        [JsonPropertyName("prefer")] public string Prefer { get; set; } = "wgc";     // wgc | printwindow | gdi
        [JsonPropertyName("idle_fps")] public int IdleFps { get; set; } = 2;
        [JsonPropertyName("active_fps")] public int ActiveFps { get; set; } = 6;
        [JsonPropertyName("jpeg_quality")] public int JpegQuality { get; set; } = 70;
        [JsonPropertyName("max_edge")] public int MaxEdge { get; set; } = 1568;
    }

    public sealed class AuditCfg
    {
        [JsonPropertyName("retain_days")] public int RetainDays { get; set; } = 30;
        [JsonPropertyName("redact_text")] public bool RedactText { get; set; } = true;
        [JsonPropertyName("save_shots")] public bool SaveShots { get; set; } = true;
    }

    public sealed class VisionCfg
    {
        [JsonPropertyName("marks")] public bool Marks { get; set; } = true;
        [JsonPropertyName("max_marks")] public int MaxMarks { get; set; } = 150;
    }

    public sealed class RigCfg
    {
        /// <summary>rig profile: allow_mcp_arm + LOUD LAN banner + verbose logs. Only for VM rigs.</summary>
        [JsonPropertyName("profile")] public bool Profile { get; set; } = false;
    }

    static string Default() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsPilot");

    public string LogFile => Path.Combine(DataDir, "pilot.log");
    public string AuditDir => Path.Combine(DataDir, "audit");
    public string ShotsDir => Path.Combine(DataDir, "shots");
    public string TokensFile => Path.Combine(DataDir, "tokens.json");

    public static PilotSettings Load(ILogger log)
    {
        var s = new PilotSettings();

        var path = Environment.GetEnvironmentVariable("PILOT_CONFIG")
                   ?? Path.Combine(Default(), "pilot.json");
        if (File.Exists(path))
        {
            try
            {
                var o = JsonSerializer.Deserialize<PilotSettings>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (o is not null) s = o;
                log.LogInformation("config loaded from {Path}", path);
            }
            catch (Exception e) { log.LogWarning("config {Path} unreadable, defaults: {Err}", path, e.Message); }
        }

        // env overrides (rig + quick local flips)
        s.Bind = Environment.GetEnvironmentVariable("PILOT_BIND") ?? s.Bind;
        if (int.TryParse(Environment.GetEnvironmentVariable("PILOT_PORT"), out var p)) s.Port = p;
        s.DataDir = Environment.GetEnvironmentVariable("PILOT_DATA_DIR") ?? s.DataDir;
        if (bool.TryParse(Environment.GetEnvironmentVariable("PILOT_ALLOW_MCP_ARM"), out var am)) s.Governance.AllowMcpArm = am;
        if (bool.TryParse(Environment.GetEnvironmentVariable("PILOT_SUPERVISED"), out var sv)) s.Governance.Supervised = sv;
        if (bool.TryParse(Environment.GetEnvironmentVariable("PILOT_RIG"), out var rg)) s.Rig.Profile = rg;
        if (Enum.TryParse<AutoArm>(Environment.GetEnvironmentVariable("PILOT_AUTO_ARM"), true, out var aa)) s.Governance.AutoArm = aa;

        if (s.Rig.Profile) s.Governance.AllowMcpArm = true;   // rig implies mcp-arm
        Directory.CreateDirectory(s.DataDir);
        Directory.CreateDirectory(s.AuditDir);
        Directory.CreateDirectory(s.ShotsDir);
        return s;
    }

    /// <summary>Redacted view for /api/config — never tokens/paths beyond DataDir.</summary>
    public object RedactedView() => new
    {
        bind = Bind, port = Port, data_dir = DataDir,
        governance = new
        {
            allow_mcp_arm = Governance.AllowMcpArm, auto_arm = Governance.AutoArm.ToString(),
            supervised = Governance.Supervised, rate_per_min = Governance.RatePerMin,
            denylist_extra = Governance.DenylistExtra,
        },
        uia = Uia, capture = Capture, vision = Vision,
        audit = new { retain_days = Audit.RetainDays, redact_text = Audit.RedactText, save_shots = Audit.SaveShots },
        rig_profile = Rig.Profile,
    };
}
