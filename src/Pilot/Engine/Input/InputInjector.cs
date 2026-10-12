using System.Runtime.InteropServices;
using WindowsPilot.Engine.Governance;
using WindowsPilot.Interop;

namespace WindowsPilot.Engine.Input;

/// <summary>
/// SendInput-only injector (design §4.2). One global async mutex serializes
/// every injection source (agent, drive channel, human pattern clicks) —
/// SendInput is process-global and interleaved injections scramble gestures.
/// The kill switch is checked between EVERY individual SendInput event.
/// </summary>
public sealed class InputInjector(KillSwitch kill, EventHub events, ILogger<InputInjector> log)
{
    readonly SemaphoreSlim _mutex = new(1, 1);
    public string Holder { get; private set; } = "";

    // ------------------------------------------------------------- primitives

    public Task Move(int x, int y, string holder = "drive") => Inject(holder, async () =>
    {
        await SendMouse(Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK, x, y);
    });

    public Task Click(int x, int y, string button = "left", int clicks = 1, string holder = "agent") => Inject(holder, async () =>
    {
        var (down, up) = button switch
        {
            "right" => (Native.MOUSEEVENTF_RIGHTDOWN, Native.MOUSEEVENTF_RIGHTUP),
            "middle" => (Native.MOUSEEVENTF_MIDDLEDOWN, Native.MOUSEEVENTF_MIDDLEUP),
            _ => (Native.MOUSEEVENTF_LEFTDOWN, Native.MOUSEEVENTF_LEFTUP),
        };
        for (int i = 0; i < clicks && Alive(); i++)
        {
            await SendMouse(Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK, x, y);
            await SendMouse(down, x, y);
            await Task.Delay(15);
            await SendMouse(up, x, y);
            if (clicks > 1) await Task.Delay(40);   // double-click gap under typical double-click time
        }
    });

    public Task Wheel(int x, int y, int deltaY, int deltaX = 0, string holder = "agent") => Inject(holder, async () =>
    {
        await SendMouse(Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK, x, y);
        if (deltaY != 0) await SendMouse(Native.MOUSEEVENTF_WHEEL | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK, x, y, (uint)(deltaY * Native.WHEEL_DELTA));
        if (deltaX != 0) await SendMouse(Native.MOUSEEVENTF_HWHEEL | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK, x, y, (uint)(deltaX * Native.WHEEL_DELTA));
    });

    // raw button verbs for the drive channel (browser holds the button across
    // drags — we mirror down/up 1:1 instead of synthesizing a gesture)
    public Task MouseDown(int x, int y, string button = "left", string holder = "drive") => Inject(holder, async () =>
    {
        await SendMouse(Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK, x, y);
        await SendMouse(DownFlag(button) | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK, x, y);
    });

    public Task MouseUp(int x, int y, string button = "left", string holder = "drive") => Inject(holder, async () =>
    {
        await SendMouse(UpFlag(button) | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK, x, y);
    });

    static uint DownFlag(string b) => b switch
    {
        "right" => Native.MOUSEEVENTF_RIGHTDOWN,
        "middle" => Native.MOUSEEVENTF_MIDDLEDOWN,
        _ => Native.MOUSEEVENTF_LEFTDOWN,
    };
    static uint UpFlag(string b) => b switch
    {
        "right" => Native.MOUSEEVENTF_RIGHTUP,
        "middle" => Native.MOUSEEVENTF_MIDDLEUP,
        _ => Native.MOUSEEVENTF_LEFTUP,
    };

    /// <summary>Drag = move→down→(steps)→up, panic-checkable at EVERY step (design §8.2).</summary>
    public Task Drag(int x1, int y1, int x2, int y2, int steps = 12, int holdMs = 120, int durationMs = 400)
        => Inject("agent", async () =>
        {
            steps = Math.Clamp(steps, 2, 60);
            int stepDelay = Math.Max(5, durationMs / steps);
            await SendMouse(Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK, x1, y1);
            await SendMouse(Native.MOUSEEVENTF_LEFTDOWN | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK, x1, y1);
            await Task.Delay(holdMs);
            for (int i = 1; i <= steps; i++)
            {
                if (!Alive()) { kill.DropInput(); break; }
                double t = (double)i / steps;
                int x = (int)Math.Round(x1 + (x2 - x1) * t);
                int y = (int)Math.Round(y1 + (y2 - y1) * t);
                await SendMouse(Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK, x, y);
                await Task.Delay(stepDelay);
            }
            await SendMouse(Native.MOUSEEVENTF_LEFTUP | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK, x2, y2);
        });

    /// <summary>Key chord(s): "ctrl+alt+t" or sequence "ctrl+s,ctrl+w".</summary>
    public Task PressChords(string chords, int repeat = 1, string holder = "agent") => Inject(holder, async () =>
    {
        for (int r = 0; r < Math.Max(1, repeat); r++)
            foreach (var chord in chords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Alive()) { kill.DropInput(); return; }
                await PressChord(chord);
                await Task.Delay(25);
            }
    });

    async Task PressChord(string chord)
    {
        var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return;
        var mods = new List<ushort>();
        foreach (var m in parts[..^1])
            if (ChordVk(m) is { } vk) mods.Add(vk);
        var final = ChordVk(parts[^1]) ?? throw new PilotException("not_found", $"unknown key '{parts[^1]}'", "use a VK name (f5, win, enter, a, 1, ...)");

        var events = new List<Native.INPUT>();
        foreach (var m in mods) events.Add(Key(m, false));
        events.Add(Key(final, false));
        events.Add(Key(final, true));
        foreach (var m in mods.AsEnumerable().Reverse()) events.Add(Key(m, true));

        foreach (var e in events)
        {
            if (!kill.ShouldContinue()) { kill.DropInput(); throw Dropped(); }   // T-09: panic stops mid-chord
            Send([e]);
            await Task.Delay(8);
        }
    }

    static ushort? ChordVk(string k) => k.ToLowerInvariant() switch
    {
        "ctrl" or "control" => 0x11, "alt" or "menu" => 0x12, "shift" => 0x10,
        "win" or "meta" or "super" => 0x5B,
        "enter" => 0x0D, "return" => 0x0D, "tab" => 0x09, "esc" or "escape" => 0x1B,
        "space" => 0x20, "backspace" => 0x08, "delete" or "del" => 0x2E, "insert" or "ins" => 0x2D,
        "home" => 0x24, "end" => 0x23, "pageup" or "pgup" => 0x21, "pagedown" or "pgdn" => 0x22,
        "up" => 0x26, "down" => 0x28, "left" => 0x25, "right" => 0x27,
        "pause" => 0x13, "printscreen" => 0x2C, "capslock" => 0x14,
        "f1" => 0x70, "f2" => 0x71, "f3" => 0x72, "f4" => 0x73, "f5" => 0x74, "f6" => 0x75,
        "f7" => 0x76, "f8" => 0x77, "f9" => 0x78, "f10" => 0x79, "f11" => 0x7A, "f12" => 0x7B,
        "f13" => 0x7C, "f14" => 0x7D, "f15" => 0x7E, "f16" => 0x7F,
        var s when s.Length == 1 && char.IsAsciiLetterOrDigit(s[0]) => CharUpper(s[0]),
        _ => null,
    };

    static ushort CharUpper(char c) => char.ToUpperInvariant(c);

    static Native.INPUT Key(ushort vk, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.InputUnion
        {
            ki = new Native.KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = Native.KEYEVENTF_KEYUP * (up ? 1u : 0u) | Extended(vk),
                time = 0, dwExtraInfo = IntPtr.Zero,
            }
        }
    };

    static uint Extended(ushort vk) => vk switch
    {
        >= 0x21 and <= 0x28 => Native.KEYEVENTF_EXTENDEDKEY,   // arrows/pg/home/end
        0x2D or 0x2E or 0x2A or 0x5B or 0x5C => Native.KEYEVENTF_EXTENDEDKEY,
        _ => 0,
    };

    // ------------------------------------------------------------- type_text

    public enum TypeMode { auto, keys, unicode, paste }

    public Task TypeText(string text, TypeMode mode, bool isPassword) => Inject("agent", async () =>
    {
        if (string.IsNullOrEmpty(text)) return;
        mode = mode switch
        {
            TypeMode.auto => text.Length > 200 ? TypeMode.paste : TypeMode.unicode,
            _ => mode,
        };
        if (isPassword && mode == TypeMode.paste)
            mode = TypeMode.unicode;   // password box: NEVER touch the clipboard (design §4.2)

        switch (mode)
        {
            case TypeMode.unicode: await TypeUnicode(text); break;
            case TypeMode.keys: await TypeKeys(text); break;
            case TypeMode.paste: await Paste(text); break;
        }
    });

    async Task TypeUnicode(string text)
    {
        foreach (var ch in text)
        {
            if (!kill.ShouldContinue()) { kill.DropInput(); throw Dropped(); }
            var inputs = new[]
            {
                new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = Native.KEYEVENTF_UNICODE } } },
                new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP } } },
            };
            Send(inputs);
            await Task.Delay(6);
        }
    }

    async Task TypeKeys(string text)
    {
        var hkl = Native.GetKeyboardLayout(Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _));
        foreach (var ch in text)
        {
            if (!kill.ShouldContinue()) { kill.DropInput(); throw Dropped(); }
            int scan = Native.VkKeyScanExW(ch, hkl);
            if (scan == -1)
            {
                await TypeUnicode(ch.ToString());   // unmappable in this layout → unicode fallback
                continue;
            }
            ushort vk = (ushort)(scan & 0xFF);
            bool shift = (scan & 0x100) != 0, ctrl = (scan & 0x200) != 0, alt = (scan & 0x400) != 0;
            var events = new List<Native.INPUT>();
            if (ctrl) events.Add(Key(0x11, false));
            if (alt) events.Add(Key(0x12, false));
            if (shift) events.Add(Key(0x10, false));
            events.Add(Key(vk, false));
            events.Add(Key(vk, true));
            if (shift) events.Add(Key(0x10, true));
            if (alt) events.Add(Key(0x12, true));
            if (ctrl) events.Add(Key(0x11, true));
            foreach (var e in events)
            {
                if (!kill.ShouldContinue()) { kill.DropInput(); throw Dropped(); }
                Send([e]);
                await Task.Delay(4);
            }
        }
    }

    async Task Paste(string text)
    {
        var saved = Clipboard.GetText();
        try
        {
            Clipboard.SetText(text);
            await Task.Delay(10);
            await PressChord("ctrl+v");
        }
        finally
        {
            try { if (saved is not null) Clipboard.SetText(saved); } catch { }   // restore-after (design §13)
        }
    }

    // ------------------------------------------------------------- plumbing

    bool Alive() => kill.ShouldContinue();
    static PilotException Dropped() => new("input_dropped", "input dropped by kill switch mid-operation",
        "the agent was panicked mid-action; re-arm (web console or pilot_arm) before retrying");

    async Task Inject(string holder, Func<Task> work)
    {
        if (!kill.ShouldContinue()) { kill.DropInput(); throw PilotException.NotArmed("act"); }
        await _mutex.WaitAsync(kill.Token);
        try
        {
            Holder = holder;
            await work();
        }
        catch (OperationCanceledException) { kill.DropInput(); }
        finally { Holder = ""; _mutex.Release(); }
    }

    async Task SendMouse(uint flags, int x = 0, int y = 0, uint data = 0)
    {
        if (!kill.ShouldContinue()) { kill.DropInput(); throw PilotException.NotArmed("act"); }
        var (nx, ny) = ToNormalized(x, y);
        var input = new Native.INPUT
        {
            type = Native.INPUT_MOUSE,
            u = new Native.InputUnion
            {
                mi = new Native.MOUSEINPUT { dx = nx, dy = ny, mouseData = data, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero }
            }
        };
        Send([input]);
        await Task.CompletedTask;
    }

    /// <summary>Physical px (virtual-screen space) → 0..65535 normalized, recomputed per call (design §6).</summary>
    static (int, int) ToNormalized(int x, int y)
    {
        var (vx, vy, vw, vh) = Native.VirtualScreen();
        if (vw <= 0 || vh <= 0) return (0, 0);
        int nx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        int ny = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        return (Math.Clamp(nx, 0, 65535), Math.Clamp(ny, 0, 65535));
    }

    static void Send(Native.INPUT[] inputs)
    {
        var n = Native.SendInput((uint)inputs.Length, inputs, Native.INPUT.Size);
        if (n != inputs.Length)
            throw new PilotException("capture_failed", $"SendInput accepted {n}/{inputs.Length}",
                "an app may be blocking injected input (UIPI / anti-injection); the fallback ladder is pattern actions");
    }
}

/// <summary>Minimal clipboard via Win32 (no WPF/WinForms dependency).</summary>
public static class Clipboard
{
    public static string? GetText()
    {
        if (!Native.OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var h = Native.GetClipboardData(Native.CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            var p = Native.GlobalLock(h);
            try { return p == IntPtr.Zero ? null : Marshal.PtrToStringUni(p); }
            finally { Native.GlobalUnlock(h); }
        }
        finally { Native.CloseClipboard(); }
    }

    public static void SetText(string text)
    {
        if (!Native.OpenClipboard(IntPtr.Zero)) return;
        try
        {
            Native.EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var h = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)bytes);
            var p = Native.GlobalLock(h);
            Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
            // null terminator
            System.Runtime.InteropServices.Marshal.WriteInt16(p, text.Length * 2, 0);
            Native.GlobalUnlock(h);
            Native.SetClipboardData(Native.CF_UNICODETEXT, h);  // ownership transfers to clipboard
        }
        finally { Native.CloseClipboard(); }
    }
}
