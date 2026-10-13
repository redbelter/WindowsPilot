using System.Threading.Channels;
using WindowsPilot.Engine;
using WindowsPilot.Engine.Capture;
using WindowsPilot.Engine.Governance;
using WindowsPilot.Engine.Input;

namespace WindowsPilot.Api;

/// <summary>
/// Latest-frame slot: capture thread produces, WS consumers get the newest
/// (drop-oldest — a stale frame is worse than none, design §4.3).
/// </summary>
public sealed class FrameHub
{
    readonly List<Channel<Frame>> _subs = [];
    readonly object _gate = new();
    public long Seq { get; private set; }
    public Frame? Latest { get; private set; }   // new subscribers get this instantly

    public void Publish(Frame f)
    {
        lock (_gate)
        {
            Seq++;
            Latest = f;
            foreach (var s in _subs) s.Writer.TryWrite(f);
        }
    }

    public Channel<Frame> Subscribe()
    {
        var ch = Channel.CreateBounded<Frame>(new BoundedChannelOptions(2)
        { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate) _subs.Add(ch);
        return ch;
    }

    public void Unsubscribe(Channel<Frame> ch)
    {
        lock (_gate) _subs.Remove(ch);
        ch.Writer.TryComplete();
    }
}

/// <summary>
/// Capture loop (hosted): idle_fps normally, active_fps while an action is
/// in flight or drive is held (design §4.3). Stillness via 8×8 phash skips
/// re-encode churn when nothing moved (idle CPU budget ≤5 %, T-14).
/// </summary>
public sealed class FrameStreamService(CaptureService capture, PilotEngine engine,
    FrameHub hub, ArmState arm, InputInjector input, PilotSettings cfg,
    ILogger<FrameStreamService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        System.Drawing.Image? last = null;
        while (!ct.IsCancellationRequested)
        {
            var fps = arm.IsDriveArmed || input.Holder.Length > 0 ? cfg.Capture.ActiveFps : cfg.Capture.IdleFps;
            try
            {
                var frame = capture.CaptureScreen(0);
                using var ms = new MemoryStream(frame.Jpeg);
                using var img = System.Drawing.Image.FromStream(ms);
                if (last is null || CaptureService.LooksChanged(last, img))
                    hub.Publish(frame);
                last?.Dispose();
                last = (System.Drawing.Image)img.Clone();
            }
            catch (PilotException pe) when (pe.Code == "capture_failed")
            {
                log.LogWarning("frame: {Msg}", pe.Message);   // locked workstation etc.
            }
            catch (Exception e) { log.LogWarning("frame loop: {Err}", e.Message); }
            try { await Task.Delay(1000 / Math.Clamp(fps, 1, 30), ct); }
            catch (OperationCanceledException) { break; }
        }
        last?.Dispose();
    }
}
