using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using WindowsPilot.Core;
using WindowsPilot.Engine;
using WindowsPilot.Engine.Governance;
using WindowsPilot.Engine.Uia;

namespace WindowsPilot.Api;

/// <summary>
/// Bearer-scope auth (design §13). Rules:
///   - static files (/console.html, app.js…) are open; the JS carries tokens per call
///   - /api/panic is ALWAYS auth-free (a stop button needs no credentials)
///   - rig profile (PILOT_RIG=1) allows no-token (localhost/dev); real installs deny
///   - GET-ish reads need scope ro; mutations need act; /ws/drive needs drive (checked there)
///   - /mcp requires act (configure the MCP client's Authorization header)
/// Tokens travel as Authorization: Bearer or ?token= (WS/EventSource can't set headers).
/// </summary>
public static class AuthMw
{
    public static IApplicationBuilder UsePilotAuth(this WebApplication app) => app.Use(async (ctx, next) =>
    {
        var path = ctx.Request.Path.Value ?? "";
        var tokens = ctx.RequestServices.GetRequiredService<TokenStore>();
        var cfg = ctx.RequestServices.GetRequiredService<PilotSettings>();

        bool open = path is "/" or "/index.html" or "/console.html" or "/app.js"
            || path.StartsWith("/api/shots/") && ctx.Request.Method == "GET"
            || (path == "/api/panic" && ctx.Request.Method == "POST")
            || !path.StartsWith("/api/") && !path.StartsWith("/mcp") && !path.StartsWith("/ws") && !path.StartsWith("/events");

        if (open) { await next(); return; }

        // WS/SSE can't send headers → token may arrive as ?token=.
        // NOTE: never ctx.Request.Query["token"] — ASP.NET Core reserves the
        // route value "token" (access_token) and it SHADOWS the query key.
        // Parse the raw query string instead.
        string? bearer = ctx.Request.Headers.Authorization.ToString();
        bearer = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer[7..].Trim() : null;
        if (string.IsNullOrEmpty(bearer) && !string.IsNullOrEmpty(ctx.Request.QueryString.Value))
        {
            var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(ctx.Request.QueryString.Value);
            if (q.TryGetValue("token", out var tv)) bearer = tv.FirstOrDefault();
        }

        if (string.IsNullOrEmpty(bearer))
        {
            if (cfg.Rig.Profile) { await next(); return; }   // rig: reach-the-port auth (hyperv precedent)
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsync("{\"ok\":false,\"error\":\"unauthorized\",\"hint\":\"Authorization: Bearer <token> from tokens.json\"}");
            return;
        }

        var scope = tokens.Validate(bearer);
        if (scope is null)
        {
            ctx.Response.StatusCode = 401;
            // rig-only debug: show EXACTLY what we extracted (quoted + hex) so
            // query-vs-header extraction bugs die in one round-trip instead of
            // ten theories. Never ship this to non-rig deployments.
            string dbg = cfg.Rig.Profile && bearer is not null
                ? $" [dbg {System.Text.Json.JsonSerializer.Serialize(bearer)} hex={Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(bearer))}]"
                : "";
            await ctx.Response.WriteAsync("{\"ok\":false,\"error\":\"bad_token\"}" + dbg);
            return;
        }

        // mutation routes require the act scope (panic already open above)
        bool mutation = ctx.Request.Method == "POST" || path == "/ws/drive";
        if (mutation && scope == TokenScope.ro)
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsync("{\"ok\":false,\"error\":\"not_armed\",\"hint\":\"GET a scope=act token\"}");
            return;
        }
        ctx.Items["scope"] = scope.Value;
        await next();
    });

public static CallContext Ctx(this HttpContext ctx) =>
    new(ctx.Request.Path.StartsWithSegments("/mcp") ? "mcp" : "web",
        ctx.Items["scope"] is TokenScope s ? s.ToString()
        : ctx.RequestServices!.GetRequiredService<PilotSettings>().Rig.Profile ? "act"   // rig reach-the-port: default scope
        : "ro");
}

/// <summary>
/// REST mirror of the MCP tools — byte-compatible job/log shapes with
/// HermesHyperV's dashboard contract ({jobs:[{id,name,status,result,error}]},
/// /api/log?since=N → {log:[{n,t,m,k}],n}) so the same UI instincts apply.
/// Same-origin: the console gets exactly the engine calls the MCP host gets.
/// </summary>
public static class PilotApi
{
    public static IEndpointRouteBuilder MapPilotApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        static async Task<IResult> Wrap(Func<Task<object>> f)
        {
            try { return Results.Json(new { ok = true, data = await f() }); }
            catch (PilotException pe) { return Results.Json(pe.ToErr(), statusCode: 400); }
            catch (Exception e) { return Results.Json(new Err { Error = "job_error", Msg = e.Message }, statusCode: 500); }
        }

        api.MapGet("/status", async (PilotEngine e) => await Wrap(() => e.StatusAsync()));

        api.MapGet("/windows", async (PilotEngine e, bool? minimized) =>
            await Wrap(() => e.GetWindowsAsync(minimized ?? false)));

        api.MapGet("/foreground", async (PilotEngine e) => await Wrap(() => e.GetForegroundAsync()));

        api.MapGet("/tree", async (PilotEngine e, string? hwnd, string? @ref, int? depth, int? max_nodes, int? budget_ms) =>
            await Wrap(() => e.GetTreeAsync(hwnd, @ref, depth ?? 1, max_nodes ?? 2000, budget_ms ?? 2000)));

        api.MapPost("/find", async (FindReq req, PilotEngine e) => await Wrap(() => e.FindAsync(req.ToQuery(), req.Max ?? 50)));

        api.MapGet("/element", async (PilotEngine e, string @ref) => await Wrap(() => e.GetElementAsync(@ref)));
        api.MapGet("/properties", async (PilotEngine e, string @ref) => await Wrap(() => e.GetPropertiesAsync(@ref)));
        api.MapGet("/element_at", async (PilotEngine e, int x, int y) => await Wrap(() => e.ElementAtAsync(x, y)));

        api.MapPost("/screenshot", async (ShotReq req, PilotEngine e) =>
            await Wrap(() => e.ScreenshotAsync(req.Monitor ?? 0, req.Marks ?? true, req.MaxEdge ?? 1568, req.Quality ?? 70, req.Hwnd)));

        api.MapGet("/events", (EventHub hub, long? since, int? max) =>
            Results.Json(new { log = hub.Since(since ?? 0, max ?? 200)
                .Select(e => new { n = e.N, t = e.T, m = e.Type + " " + JsonSerializer.Serialize(e.Detail), k = e.Type }), n = hub.SinceSeq }));

        // ---- mutations: single entry-point, hyperv job pattern ----
        api.MapPost("/act", (ActReq req, PilotEngine e, PilotJobStore jobs, HttpContext ctx) =>
        {
            var cx = ctx.Ctx();
            var jid = jobs.Spawn(req.Tool, async _ => req.Tool switch
            {
                "click" => await e.ClickAsync(cx, req.Req, req.X, req.Y, req.Button ?? "left", req.Clicks ?? 1, req.Verify ?? true),
                "drag" => await e.DragAsync(cx, req.Req, req.X1, req.Y1, req.X2 ?? 0, req.Y2 ?? 0, req.Steps ?? 12, req.DurationMs ?? 400, req.Verify ?? true),
                "scroll" => await e.ScrollAsync(cx, req.Req, req.X, req.Y, req.DeltaY ?? 0, req.DeltaX ?? 0, false),
                "type" => await e.TypeTextAsync(cx, req.Text ?? "", req.Req, req.Mode ?? "auto", req.Verify ?? true),
                "keys" => await e.KeysAsync(cx, req.Keys ?? "", req.Repeat ?? 1, req.Verify ?? false),
                "value" => await e.SetValueAsync(cx, req.Req ?? "", req.Text ?? "", req.Verify ?? true),
                "invoke" => await e.InvokeAsync(cx, req.Req ?? "", req.Verify ?? true),
                "toggle" => await e.ToggleAsync(cx, req.Req ?? "", req.Verify ?? true),
                "expand" => await e.ExpandAsync(cx, req.Req ?? "", req.Expand ?? true, req.Verify ?? true),
                "select" => await e.SelectAsync(cx, req.Req ?? "", req.Verify ?? true),
                "launch" => await e.LaunchAsync(cx, req.Cmd ?? "", req.Args, req.Workdir, req.WaitWindowMs ?? 8000, true),
                "kill" => await e.KillAppAsync(cx, req.Pid, req.Process),
                _ => throw new PilotException("not_found", $"unknown act tool '{req.Tool}'",
                    "click drag scroll type keys value invoke toggle expand select launch kill"),
            });
            return Results.Json(new { job = jid });
        });

        api.MapPost("/arm", (ArmReq req, ArmState armState, AuditLog audit, HttpContext ctx) =>
        {
            if (req.Scope == "read") return Results.Json(armState.Disarm("web"));
            var info = armState.Arm(Enum.Parse<ArmScope>(req.Scope, true), req.TtlMin, req.Why ?? "web-console", allowMcpArm: false);
            audit.Write(ctx.Ctx().Host, "act", "arm", new { req.Scope, req.TtlMin }, null, new { }, new { ok = true });
            return Results.Json(info);
        });

        api.MapPost("/panic", (KillSwitch kill, ArmState armState, AuditLog audit) =>
        {
            kill.Trip("web-stop");
            armState.Disarm("panic(web)");
            audit.Write("web", "-", "panic", null, null, new { }, new { ok = true, trips = kill.Trips });
            return Results.Json(new { ok = true, trips = kill.Trips, dropped = kill.DroppedInputs });
        });

        api.MapGet("/jobs", (PilotJobStore jobs) => Results.Json(new { jobs = jobs.Jobs() }));
        api.MapGet("/job", (PilotJobStore jobs, string id) =>
            jobs.Job(id) is { } j ? Results.Json(j) : Results.NotFound());

        api.MapGet("/audit", (AuditLog a, long? since_seq, string? tool, string? host, int? limit) =>
        {
            var (items, n) = a.Tail(since_seq ?? 0, tool, host, limit ?? 50);
            return Results.Json(new { items, n });
        });

        api.MapGet("/shots/{id}", (string id, PilotSettings cfg) =>
        {
            var f = Path.Combine(cfg.ShotsDir, Path.GetFileName(id) + ".jpg");   // traversal-safe leaf only
            return File.Exists(f) ? Results.File(f, "image/jpeg") : Results.NotFound();
        });

        api.MapGet("/config", (PilotEngine e) => Results.Json(new { ok = true, data = e.RedactedConfig() }));

        api.MapGet("/log", (EventHub hub, long? since) =>
        {
            var items = hub.Since(since ?? 0, 300)
                .Select(e => new { n = e.N, t = e.T, m = e.Type, k = e.Type }).ToList();
            return Results.Json(new { log = items, n = hub.SinceSeq });
        });

        return app;
    }

    public sealed record FindReq
    {
        [JsonPropertyName("name_regex")] public string? NameRegex { get; init; } [JsonPropertyName("name_contains")] public string? NameContains { get; init; }
        [JsonPropertyName("automation_id")] public string? AutomationId { get; init; } [JsonPropertyName("class_name")] public string? ClassName { get; init; }
        [JsonPropertyName("control_type")] public string? ControlType { get; init; } [JsonPropertyName("process")] public string? Process { get; init; }
        [JsonPropertyName("hwnd")] public string? Hwnd { get; init; } [JsonPropertyName("ref")] public string? Ref { get; init; }
        [JsonPropertyName("max")] public int? Max { get; init; }
        public FindQuery ToQuery() => new()
        {
            NameRegex = NameRegex, NameContains = NameContains, AutomationId = AutomationId,
            ClassName = ClassName, ControlType = ControlType, Process = Process, Hwnd = Hwnd, Ref = Ref,
        };
    }

    public sealed record ShotReq
    {
        [JsonPropertyName("monitor")] public int? Monitor { get; init; } [JsonPropertyName("marks")] public bool? Marks { get; init; }
        [JsonPropertyName("max_edge")] public int? MaxEdge { get; init; } [JsonPropertyName("quality")] public int? Quality { get; init; } [JsonPropertyName("hwnd")] public string? Hwnd { get; init; }
    }

    public sealed record ActReq
    {
        // Wire contract = what wwwroot/app.js sends (snake_case; 'req' aliases ref).
        [JsonPropertyName("tool")] public string Tool { get; init; } = "";
        [JsonPropertyName("req")] public string? Req { get; init; }            // element ref
        [JsonPropertyName("x")] public int? X { get; init; } [JsonPropertyName("y")] public int? Y { get; init; }
        [JsonPropertyName("x1")] public int? X1 { get; init; } [JsonPropertyName("y1")] public int? Y1 { get; init; }
        [JsonPropertyName("x2")] public int? X2 { get; init; } [JsonPropertyName("y2")] public int? Y2 { get; init; }
        [JsonPropertyName("button")] public string? Button { get; init; } [JsonPropertyName("clicks")] public int? Clicks { get; init; }
        [JsonPropertyName("steps")] public int? Steps { get; init; } [JsonPropertyName("duration_ms")] public int? DurationMs { get; init; }
        [JsonPropertyName("delta_y")] public int? DeltaY { get; init; } [JsonPropertyName("delta_x")] public int? DeltaX { get; init; }
        [JsonPropertyName("text")] public string? Text { get; init; } [JsonPropertyName("mode")] public string? Mode { get; init; }
        [JsonPropertyName("keys")] public string? Keys { get; init; } [JsonPropertyName("repeat")] public int? Repeat { get; init; }
        [JsonPropertyName("expand")] public bool? Expand { get; init; }
        [JsonPropertyName("cmd")] public string? Cmd { get; init; } [JsonPropertyName("args")] public string? Args { get; init; } [JsonPropertyName("workdir")] public string? Workdir { get; init; }
        [JsonPropertyName("wait_window_ms")] public int? WaitWindowMs { get; init; }
        [JsonPropertyName("pid")] public int? Pid { get; init; } [JsonPropertyName("process")] public string? Process { get; init; }
        [JsonPropertyName("verify")] public bool? Verify { get; init; }
    }

    public sealed record ArmReq
    {
        [JsonPropertyName("scope")] public string Scope { get; init; } = "";
        [JsonPropertyName("ttl_min")] public int? TtlMin { get; init; }
        [JsonPropertyName("why")] public string? Why { get; init; }
    }
}
