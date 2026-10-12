using WindowsPilot;
using WindowsPilot.Api;
using WindowsPilot.Engine;
using WindowsPilot.Engine.Capture;
using WindowsPilot.Engine.Governance;
using WindowsPilot.Engine.Input;
using WindowsPilot.Engine.Uia;
using WindowsPilot.Interop;

// ── THE DPI CALL: first line of real work, before ANY window/UIA/capture ──
// (design §6.1 / G10 — getting this wrong makes every bbox a lie).
Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

var builder = WebApplication.CreateBuilder(args);

// WindowsPilot runs as a LOGON SCHEDULED TASK in the user's interactive
// session — deliberately NOT UseWindowsService (Session 0 = UIA-blind;
// design §14, the single most important install rule).
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

PilotSettings cfg;
using (var bootFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddConsole()))
    cfg = PilotSettings.Load(bootFactory.CreateLogger("pilot.config"));

builder.Services.AddSingleton(cfg);
builder.WebHost.UseUrls($"{(cfg.Bind == "loopback" ? "http://127.0.0.1" : "http://0.0.0.0")}:{cfg.Port}");

builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<ArmState>();
builder.Services.AddSingleton<KillSwitch>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<Guard>();
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddSingleton<UiaService>();
builder.Services.AddSingleton<InputInjector>();
builder.Services.AddSingleton<CaptureService>();
builder.Services.AddSingleton<PilotEngine>();
builder.Services.AddSingleton<PilotJobStore>();
builder.Services.AddSingleton<FrameHub>();
builder.Services.AddHostedService<FrameStreamService>();
builder.Services.AddHostedService<HotkeyWatchdog>();   // Ctrl+Alt+Pause panic host (message-only window)

builder.Services.AddMcpServer().WithHttpTransport().WithTools<PilotTools>();

var app = builder.Build();

app.Services.GetRequiredService<TokenStore>().LoadOrGenerate();
app.Services.GetRequiredService<ArmState>().MaybeAutoArm();   // dev/rig auto_arm=startup (design §8.1)

// LOUD banner: LAN bind + no TLS is rig/dev territory (design §13)
if (cfg.Bind != "loopback")
    app.Logger.LogWarning("!!! WindowsPilot bound to {Bind}:{Port} WITHOUT TLS — LAN-trust mode, tokens only. " +
                          "Real desktops: pilot bind loopback.", cfg.Bind, cfg.Port);

app.UseStaticFiles();
app.UsePilotAuth();                    // bearer scopes; /api/panic exempt (auth-free stop)
app.MapGet("/", () => Results.Redirect("/console.html"));
app.MapPilotApi();                     // /api/* REST mirror
app.MapViewWs();                       // /ws/view frames
app.MapDriveWs();                      // /ws/drive human input channel
app.MapEventsSse();                    // /events SSE
app.MapMcp("/mcp");

app.Logger.LogInformation(
    "WindowsPilot {Ver} starting — mcp=/mcp  ui=/console.html  api=/api/*  (session {Sid})",
    typeof(Program).Assembly.GetName().Version, SessionInfo.Current().session);

app.Run();

/// <summary>partial anchor so WebApplicationFactory&lt;Program&gt; rig tests can boot this file.</summary>
public partial class Program { }
