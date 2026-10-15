using System.Runtime.InteropServices;

namespace WindowsPilot.Interop;

/// <summary>
/// Win32 P/Invoke surface. Kept in one file, deliberately boring.
/// All coordinate-taking calls use PHYSICAL pixels in VIRTUAL-SCREEN space
/// (design §6): the DPI contract says SetProcessDpiAwarenessContext(PMV2)
/// MUST be called in Main before anything else in the process touches
/// windows/UIA/capture. That call lives in Program.cs, not here.
/// </summary>
public static class Native
{
    // ---------------------------------------------------------------- DPI

    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    // ------------------------------------------------------- process/session

    [DllImport("kernel32.dll")] public static extern int GetCurrentProcessId();
    [DllImport("kernel32.dll")] public static extern bool ProcessIdToSessionId(int pid, out int sessionId);
    [DllImport("kernel32.dll")] public static extern int WTSGetActiveConsoleSessionId();

    // -------------------------------------------------------- virtual screen

    public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    public const int SM_CMONITORS = 80;

    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

    public static (int x, int y, int w, int h) VirtualScreen()
        => (GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    // --------------------------------------------------------------- windows

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd); // GW_OWNER=4
    public const uint GW_OWNER = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);

    // -------------------------------------------------------- hotkey (panic)

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_CONTROL = 0x0002, MOD_ALT = 0x0001;
    public const int VK_PAUSE = 0x13;

    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // CreateWindowEx for the message-only window (panic hotkey host).
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr hInst, IntPtr lpParam);

    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);

    /// <summary>Register a trivial window class once (Win32 window-class dance).</summary>
    public static IntPtr RegisterWindowClass(string className)
    {
        var hInst = GetModuleHandleW(null);
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = WndProcThunk,
            hInstance = hInst,
            lpszClassName = className,
        };
        RegisterClassExW(ref wc); // ignore ERROR_CLASS_ALREADY_EXISTS
        return hInst;
    }

    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize; public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProcDelegate lpfnWndProc;
        public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? name);

    // keep the delegate alive — GC would yank a captured thunk otherwise
    public static WndProcDelegate? WndProcThunk;

    // --------------------------------------------------------- message pump

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG m, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG m);
    public const int WM_QUIT = 0x0012;
    [DllImport("user32.dll")] public static extern bool PostQuitMessage(int code);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr w, IntPtr l, uint flags, uint ms, out IntPtr res);
    public const int SMTO_ABORTIFHUNG = 0x0002;

    // ---------------------------------------------------------- SendInput

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
        public static int Size => Marshal.SizeOf<INPUT>();
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData;   // wheel delta or xbutton
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;

    // mouse flags
    public const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020,
        MOUSEEVENTF_MIDDLEUP = 0x0040, MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000,
        MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000,
        MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100;

    // keyboard flags
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002,
        KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_SCANCODE = 0x0008;

    [DllImport("user32.dll")]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public uint cbSize; public uint dwLastInputInfo; }
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);

    // ------------------------------------------------------------ keyboard map

    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] public static extern int VkKeyScanExW(int ch, IntPtr dwhkl);
    [DllImport("user32.dll")] public static extern uint MapVirtualKeyExW(uint vk, uint uMapType, IntPtr dwhkl);
    public const uint MAPVK_VK_TO_VSC = 0;
    public const int LOBYTE = 0xFF, HIBYTE_SHIFT = 8;

    // ------------------------------------------------------------- processes

    public const uint CREATE_NEW_CONSOLE = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb; public string? lpReserved;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr hStdInput, hStdOutput, hStdErr, lpReserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessW(string? app, string? cmd, IntPtr sa, IntPtr ta,
        bool inherit, uint flags, IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr ShellExecuteW(IntPtr hwnd, string verb, string file, string? params_, string? dir, int show);

    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] public static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
    public const uint WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 0x102;

    // ---------------------------------------------------------------- capture

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);       // hWnd=0 -> screen DC
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h,
        IntPtr hdcSrc, int x1, int y1, uint rop);
    public const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    public const int WHEEL_DELTA = 120;

    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    public const uint PW_CLIENTONLY = 1, PW_RENDERFULLCONTENT = 2;

    // monitors (for the view stream + per-monitor dpi report)
    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT r, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr hMon, ref MONITORINFOEX info);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hMon, int type, out uint dx, out uint dy);
    public const int MDT_EFFECTIVE_DPI = 0;
    public const uint MONITORINFOF_PRIMARY = 1;

    // ------------------------------------------------------------ clipboard

    [DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] public static extern bool EmptyClipboard();
    [DllImport("user32.dll")] public static extern bool CloseClipboard();
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint fmt);
    [DllImport("user32.dll")] public static extern IntPtr SetClipboardData(uint fmt, IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalFree(IntPtr h);
    public const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 0x0002;

    // misc
    [DllImport("user32.dll")] public static extern uint GetWindowLongW(IntPtr hWnd, int idx);
    public const int GWL_STYLE = -16, WS_VISIBLE = 0x10000000;
    [DllImport("kernel32.dll")] public static extern uint GetTickCount();
    [DllImport("kernel32.dll")] public static extern void Sleep(uint ms);

    // ------------------------------------------------------- input desktop
    // Wedged-input detection (design: fleet health). After certain foreground
    // churn a guest can end up with NO usable input desktop: SendInput returns
    // success but no app ever consumes it, GetForegroundWindow() == IntPtr.Zero
    // (the "Idle" process holds it), and OpenInputDesktop names something other
    // than "Default" (or the name read fails). Only a guest reboot cures it.
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseDesktop(IntPtr hDesktop);
    // NOTE byte[] not StringBuilder: the LPWStr copy-back into a StringBuilder
    // silently yields "" (observed live: returns true, need=12, buffer empty).
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetUserObjectInformationW(IntPtr hObj, int nIndex,
        byte[] pvInfo, uint nLength, out uint lpnLengthNeeded);
    public const uint DESKTOP_READOBJECTS = 0x0001;
    public const int UOI_NAME = 1;
}

/// <summary>Snapshot of the active-desktop info reported by /api/status.</summary>
public static class SessionInfo
{
    public static (int session, int activeConsole) Current()
    {
        Native.ProcessIdToSessionId(Native.GetCurrentProcessId(), out var mine);
        return (mine, Native.WTSGetActiveConsoleSessionId());
    }

    public static bool IsInteractive => Current().session == Current().activeConsole && Current().session != 0;

    /// <summary>
    /// Input-lane health (design §14 companion). Gates on the ONE reliably
    /// measured wedge signature: GetForegroundWindow()==IntPtr.Zero — the
    /// "Idle" process holds the foreground and no app receives input (seen
    /// live after long uptime + foreground churn; only a reboot cures it).
    /// input.ok=false => reboot the box; act results are lying, don't retry.
    ///
    /// NOT gated on these, all measured live and rejected:
    ///  * input-desktop NAME: in-session reads are privilege-blocked
    ///    (need=12, zero buffer, err 203) — always empty, useless.
    ///  * SendInput-ack probe (zero-motion move + GetLastInputInfo): Server
    ///    2025 never advances dwLastInputInfo for SYNTHETIC mouse input —
    ///    not even a real +2px absolute move — while apps consumed clicks
    ///    perfectly fine. False "wedged" on a healthy box. Worst of all.
    ///  * variant (b) of the wedge — real fg window, acts 'ok', NOTHING
    ///    consumed — is INVISIBLE to any cheap status check. Only
    ///    consumption-style tests catch it (smoke calculator re-presses
    ///    until the display changes); run the suite, don't trust 'ok'.
    /// last_input_s: seconds since the desktop's last INPUT event, for
    /// humans only (synthetic injection may not advance it on Server SKUs).
    /// </summary>
    public static (bool ok, string why, double last_input_s) InputHealth()
    {
        try
        {
            var lii = new Native.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.LASTINPUTINFO>() };
            double lastS = -1;
            if (Native.GetLastInputInfo(ref lii))
                lastS = (Native.GetTickCount() - lii.dwLastInputInfo) / 1000.0;   // uint wrap is fine for a human field
            if (Native.GetForegroundWindow() == IntPtr.Zero)
                return (false, "no_foreground_window", lastS);
            return (true, "", lastS);
        }
        catch (Exception e) { return (false, "probe_exception:" + e.GetType().Name, -1); }
    }
}
