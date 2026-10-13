using System.Net.WebSockets;
using System.Text.Json;
using WindowsPilot.Engine;
using WindowsPilot.Engine.Capture;
using WindowsPilot.Engine.Governance;
using WindowsPilot.Engine.Input;

namespace WindowsPilot.Api;

/// <summary>
/// /ws/view — live JPEG frame stream (design §11): binary frames + JSON
/// control messages ({type:"hdr"} with scale/origin/seq so the browser's
/// overlay can use the SAME inverse transform, design §6.6). One send at a
/// time (the TerminalWs lesson). Drop-oldest comes from FrameHub's channel.
/// </summary>
public static class ViewWs
{
    public static void MapViewWs(this WebApplication app)
    {
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        app.Map("/ws/view", async (HttpContext ctx, FrameHub hub, CaptureService capture, ArmState arm, EventHub events) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var sendLock = new SemaphoreSlim(1, 1);
            var ch = hub.Subscribe();
            var cts = new CancellationTokenSource();

            async Task Send(byte[] buf, WebSocketMessageType type)
            {
                await sendLock.WaitAsync();
                try
                {
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await socket.SendAsync(new ArraySegment<byte>(buf), type, true, CancellationToken.None);
                }
                catch { cts.Cancel(); }
                finally { sendLock.Release(); }
            }

            // hdr first: client needs the transform before the first frame
            var meta = capture.MetaFor(0);
            await Send(JsonSerializer.SerializeToUtf8Bytes(new { type = "hdr", scale = meta.Scale,
                origin = meta.SourceOrigin, capture_tier = meta.CaptureTier, armed = arm.Info.Armed }),
                WebSocketMessageType.Text);

            // instant paint: latest captured frame even if the screen is still
            var seed = hub.Latest;
            if (seed is not null)
            {
                await Send(seed.Jpeg, WebSocketMessageType.Binary);
                await Send(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "meta", scale = seed.Meta.Scale, origin = seed.Meta.SourceOrigin,
                    tier = seed.Meta.CaptureTier, seq = hub.Seq, armed = arm.Info.Armed,
                }), WebSocketMessageType.Text);
            }

            var pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var frame in ch.Reader.ReadAllAsync(cts.Token))
                    {
                        await Send(frame.Jpeg, WebSocketMessageType.Binary);
                        await Send(JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            type = "meta", scale = frame.Meta.Scale, origin = frame.Meta.SourceOrigin,
                            tier = frame.Meta.CaptureTier, seq = hub.Seq, armed = arm.Info.Armed,
                        }), WebSocketMessageType.Text);
                    }
                }
                catch { }
            });

            try
            {
                var buf = new byte[4096];
                while (socket.State == WebSocketState.Open && !cts.IsCancellationRequested)
                {
                    var res = await socket.ReceiveAsync(buf, cts.Token);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    // control in: {cmd:"pause"|"resume"|"rehdr"} — pause just stops forwarding
                }
            }
            catch { }
            finally
            {
                cts.Cancel();
                hub.Unsubscribe(ch);
                try { await Task.WhenAny(pump, Task.Delay(1000)); } catch { }
                try
                {
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "end", CancellationToken.None);
                }
                catch { }
                sendLock.Dispose();
            }
        });
    }
}

/// <summary>
/// /ws/drive — human input channel (design §11). Grants ONLY while
/// ARMED(drive); holds the input mutex per event (holder "drive") so an
/// agent cannot interleave. Uplink JSON: {k:"move"|"down"|"up"|"wheel"|"key", ...}
/// already in PHYSICAL px — the browser applies devicePixelRatio + canvas
/// letterbox math BEFORE sending (marks.js header documents this, design §6.6);
/// the engine double-checks nothing (it cannot know browser layout) — hence
/// T-13 asserts the pixel outcome end to end.
/// </summary>
public static class DriveWs
{
    public static void MapDriveWs(this WebApplication app)
    {
        app.Map("/ws/drive", async (HttpContext ctx, ArmState arm, InputInjector input, EventHub events) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }

            if (!arm.IsDriveArmed)
            {
                ctx.Response.StatusCode = 403;   // drive_locked semantics without a frame dance
                await ctx.Response.WriteAsync("{\"ok\":false,\"error\":\"drive_locked\",\"hint\":\"enable DRIVE from the console ARM menu first\"}");
                return;
            }
            events.Push("drive_started", new { ua = ctx.Request.Headers.UserAgent.ToString()[..Math.Min(60, ctx.Request.Headers.UserAgent.ToString().Length)] });

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var buf = new byte[8192];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var res = await socket.ReceiveAsync(buf, CancellationToken.None);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    if (res.Count == 0) continue;
                    JsonElement msg;
                    try { msg = JsonDocument.Parse(buf.AsMemory(0, res.Count)).RootElement; } catch { continue; }
                    var k = msg.TryGetProperty("k", out var kk) ? kk.GetString() : null;
                    // re-check every event: an agent-arm race can disarm mid-stream
                    if (!arm.IsDriveArmed) break;
                    try
                    {
                        switch (k)
                        {
                            case "move": await input.Move(I(msg, "x"), I(msg, "y"), "drive"); break;
                            case "down": await input.MouseDown(I(msg, "x"), I(msg, "y"), Btn(msg)); break;
                            case "up": await input.MouseUp(I(msg, "x"), I(msg, "y"), Btn(msg)); break;
                            case "wheel": await input.Wheel(I(msg, "x"), I(msg, "y"), I(msg, "dy"), I(msg, "dx"), "drive"); break;
                            case "key": await input.PressChords(msg.GetProperty("keys").GetString() ?? "", 1, "drive"); break;
                        }
                    }
                    catch (PilotException) { /* drive drops are logged by injector; keep the socket alive */ }
                }
            }
            catch { }
            finally { events.Push("drive_stopped", new { }); }
        });
    }

    static int I(JsonElement m, string p) => m.TryGetProperty(p, out var v) && v.TryGetInt32(out var n) ? n : 0;
    static string Btn(JsonElement m) => m.TryGetProperty("b", out var v) ? v.GetString() ?? "left" : "left";
}

/// <summary>/events SSE fallback for the activity strip (same seq space as /api/log).</summary>
public static class EventsSse
{
    public static void MapEventsSse(this WebApplication app)
    {
        app.MapGet("/events", async (HttpContext ctx, EventHub hub, long? since) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            var ch = hub.Subscribe();
            try
            {
                await foreach (var (n, t, type, detail) in ch.Reader.ReadAllAsync(ctx.RequestAborted))
                {
                    await ctx.Response.WriteAsync($"id: {n}\ndata: {JsonSerializer.Serialize(new { n, t, type, detail })}\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch { }
            finally { hub.Unsubscribe(ch); }
        });
    }
}
