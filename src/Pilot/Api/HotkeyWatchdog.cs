using System.Runtime.InteropServices;
using WindowsPilot.Engine;
using WindowsPilot.Engine.Governance;
using WindowsPilot.Interop;

namespace WindowsPilot.Api;

/// <summary>
/// Kill-switch path #3 (design §8.2): RegisterHotKey Ctrl+Alt+Pause on a
/// message-only window, pumped on its own thread. Works with no browser,
/// no MCP client, no token — the last line of defense. Also hosts the
/// engine watchdog notice: logs a LOUD line if the interactive session
/// changes (agent becomes useless then; restart in-session, design §14).
/// </summary>
public sealed class HotkeyWatchdog(KillSwitch kill, ArmState arm, EventHub events,
    ILogger<HotkeyWatchdog> log) : BackgroundService
{
    const int HOTKEY_PANIC = 0xB001;
    const int WM_SESSIONCHANGE = 0x0219;

    IntPtr _hwnd;
    Native.WndProcDelegate? _proc;   // keep alive for GC (interop delegate root)
    Thread? _thread;

    protected override Task ExecuteAsync(CancellationToken st)
    {
        _thread = new Thread(Run) { Name = "pilot-hotkey", IsBackground = true };
        _thread.Start();
        return Task.CompletedTask;
    }

    void Run()
    {
        // order matters: the class registration captures the wndproc pointer,
        // so the delegate must be rooted BEFORE RegisterWindowClass runs.
        Native.WndProcThunk = _proc = WndProc;
        Native.RegisterWindowClass("PilotHotkey");
        _hwnd = Native.CreateWindowExW(0, "PilotHotkey", "pilot", 0, 0, 0, 0, 0,
            Native.HWND_MESSAGE, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            log.LogWarning("hotkey window failed — Ctrl+Alt+Pause unavailable");
            return;
        }
        bool ok = Native.RegisterHotKey(_hwnd, HOTKEY_PANIC, Native.MOD_CONTROL | Native.MOD_ALT, Native.VK_PAUSE);
        log.LogInformation("panic hotkey Ctrl+Alt+Pause {State}", ok ? "armed" : "FAILED (already taken?)");

        while (Native.GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0)
        {
            if (m.message == Native.WM_QUIT) break;
            Native.TranslateMessage(ref m);
            Native.DispatchMessageW(ref m);
        }
    }

    IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == Native.WM_HOTKEY && w.ToInt32() == HOTKEY_PANIC)
        {
            kill.Trip("hotkey");
            arm.Disarm("panic(hotkey)");
            log.LogWarning("PANIC via Ctrl+Alt+Pause");
            return IntPtr.Zero;
        }
        if (msg == WM_SESSIONCHANGE)
            events.Push("session_change", new { code = l.ToInt64() });
        return Native.DefWindowProcW(h, msg, w, l);
    }

    public override Task StopAsync(CancellationToken ct)
    {
        if (_hwnd != IntPtr.Zero) Native.PostMessageW(_hwnd, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        return base.StopAsync(ct);
    }
}
