using WindowsPilot.Core;

namespace WindowsPilot.Engine;

/// <summary>
/// Error taxonomy (design §16). Every failure is a PilotException carrying a
/// machine-branchable code + hint + retry flag. The MCP adapters turn these
/// into ToolError (failed calls RAISE — clients must see failure states);
/// REST turns them into the {ok:false,...} envelope.
/// </summary>
public sealed class PilotException : Exception
{
    public string Code { get; }
    public string Hint { get; }
    public bool Retry { get; }
    public int? RetryAfterS { get; }
    public FingerprintDto? Was { get; }

    public PilotException(string code, string msg, string hint = "", bool retry = false,
        int? retryAfterS = null, FingerprintDto? was = null) : base(msg)
    { Code = code; Hint = hint; Retry = retry; RetryAfterS = retryAfterS; Was = was; }

    public Err ToErr() => new()
    {
        Error = Code, Msg = Message, Hint = Hint, Retry = Retry,
        RetryAfterS = RetryAfterS, Was = Was,
    };

    // ---- factories (keep codes + hints consistent everywhere) ----

    public static PilotException StaleRef(FingerprintDto? was) => new("stale_ref",
        "element reference is no longer valid",
        "re-run pilot_find_elements using the returned `was` fingerprint (name/automation_id), then retry once",
        retry: true, was: was);

    public static PilotException NotFound(string what) => new("not_found", $"{what} matched no elements",
        "broaden the query or call pilot_get_tree on the window first", retry: true);

    public static PilotException PatternUnsupported(string ref_, string pattern) => new("pattern_unsupported",
        $"{ref_} does not support {pattern}Pattern",
        "try pilot_invoke, or a coordinate click at the element rect center");

    public static PilotException NotArmed(string scope) => new("not_armed",
        $"this action requires armed scope '{scope}'",
        "arm from the web console (ARM button) — or pilot_arm if allow_mcp_arm is enabled");

    public static PilotException DriveLocked() => new("drive_locked",
        "a human holds the input (drive mode is on in the web console)",
        "release drive in the console first; do not retry while it is held");

    public static PilotException DeniedApp(string proc) => new("denied_app",
        $"foreground process '{proc}' is on the denylist — agent actions are refused here",
        "this is a human-only surface (e.g. UAC/credential UI). STOP looping; surface it to the operator",
        retry: false);

    public static PilotException RateLimited(int afterS) => new("rate_limited",
        "mutation rate limit reached", $"retry after {afterS}s", retry: true, retryAfterS: afterS);

    public static PilotException Offscreen(string ref_) => new("element_offscreen",
        $"{ref_} has no on-screen rect", "scroll its container (pilot_scroll) or re-find after scrolling");

    public static PilotException CaptureFailed(string tiers) => new("capture_failed",
        $"all capture tiers failed: {tiers}", "check the session/desktop state in pilot_status");

    public static PilotException Timeout(string what) => new("timeout",
        $"UIA provider too slow during: {what}",
        "the owning app may be hung; retry once, then treat the app as sluggish and pick another window");

    public static PilotException SessionLost() => new("session_lost",
        "agent is no longer on the interactive desktop station",
        "restart the agent INSIDE an interactive session (it must run as the logged-on user, never a Session-0 service)");

    public static PilotException UacPending() => new("uac_pending",
        "a consent prompt is on screen", "human action required (drive mode); stop the agent loop");
}
