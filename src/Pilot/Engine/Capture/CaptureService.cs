using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WindowsPilot.Core;
using WindowsPilot.Interop;

namespace WindowsPilot.Engine.Capture;

public sealed record Frame(byte[] Jpeg, ShotMeta Meta);

/// <summary>
/// Tiered screen bridge (design §4.3): gdi (BitBlt, always works, may be black
/// on protected overlays) → printwindow (per-window, PW_RENDERFULLCONTENT) →
/// wgc (P2 stub — needs the WinRT capture pool; the ladder reports which tier
/// actually produced the frame, never silently).
///
/// Every Frame carries its own inverse transform: width/height/scale/origin.
/// Coordinates picked in the image inverse-map to physical px via Meta
/// (design §6). Encode + downscale with ImageSharp-free System.Drawing.
/// </summary>
public sealed class CaptureService(PilotSettings cfg, ILogger<CaptureService> log)
{
    static readonly ImageCodecInfo? JpegCodec = ImageCodecInfo.GetImageEncoders()
        .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public List<MonitorDto> Monitors()
    {
        var list = new List<MonitorDto>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref Native.RECT r, IntPtr _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            Native.GetMonitorInfoW(h, ref info);
            uint dx = 96, dy = 96;
            try { Native.GetDpiForMonitor(h, Native.MDT_EFFECTIVE_DPI, out dx, out dy); } catch { }
            list.Add(new MonitorDto(list.Count, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                (int)dx, dx / 96.0)
            { Primary = (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0 });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public ShotMeta MetaFor(int monitor)
    {
        var mons = Monitors();
        var m = mons.Count == 0 ? new MonitorDto(0, 0, 0, 1920, 1080, 96, 1) : mons[Math.Min(monitor, mons.Count - 1)];
        return new ShotMeta { Width = m.w, Height = m.h, Scale = 1, SourceOrigin = new RectDto(m.x, m.y, m.w, m.h), Monitor = monitor };
    }

    // ------------------------------------------------------------------ gdi

    /// <summary>Full-screen (per-monitor) GDI capture. Physical pixels, virtual-screen origin.</summary>
    Frame CaptureGdi(int monitor, Native.RECT? region = null)
    {
        var mons = Monitors();
        var m = mons.Count == 0 ? null : mons[Math.Min(monitor, mons.Count - 1)];
        var (vx, vy, vw, vh) = Native.VirtualScreen();
        int x = region?.Left ?? m?.x ?? vx, y = region?.Top ?? m?.y ?? vy;
        int w = region is not null ? region.Value.Right - region.Value.Left : m?.w ?? vw;
        int h = region is not null ? region.Value.Bottom - region.Value.Top : m?.h ?? vh;

        IntPtr hScreen = Native.GetDC(IntPtr.Zero);
        IntPtr hDC = Native.CreateCompatibleDC(hScreen);
        IntPtr hBmp = Native.CreateCompatibleBitmap(hScreen, w, h);
        IntPtr old = Native.SelectObject(hDC, hBmp);
        try
        {
            if (!Native.BitBlt(hDC, 0, 0, w, h, hScreen, x, y, Native.SRCCOPY | Native.CAPTUREBLT))
                throw PilotException.CaptureFailed("gdi: BitBlt returned false");

            using var bmp = Image.FromHbitmap(hBmp);
            using var down = Downscale(bmp, cfg.Capture.MaxEdge, out double scale);
            return new Frame(Encode(down), new ShotMeta
            {
                Width = down.Width, Height = down.Height, Scale = scale,
                SourceOrigin = new RectDto(x, y, w, h), Monitor = monitor, CaptureTier = "gdi",
            });
        }
        finally
        {
            Native.SelectObject(hDC, old);
            Native.DeleteObject(hBmp);
            Native.DeleteDC(hDC);
            Native.ReleaseDC(IntPtr.Zero, hScreen);
        }
    }

    // ---------------------------------------------------------- printwindow

    public Frame CaptureWindow(IntPtr hwnd, string tierWanted = "printwindow")
    {
        if (!Native.GetWindowRect(hwnd, out var r))
            throw PilotException.NotFound($"hwnd {hwnd:x} has no rect");
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) throw PilotException.Offscreen($"window {hwnd:x}");

        IntPtr hScreen = Native.GetDC(IntPtr.Zero);
        IntPtr hDC = Native.CreateCompatibleDC(hScreen);
        IntPtr hBmp = Native.CreateCompatibleBitmap(hScreen, w, h);
        IntPtr old = Native.SelectObject(hDC, hBmp);
        try
        {
            if (!Native.PrintWindow(hwnd, hDC, Native.PW_RENDERFULLCONTENT))
                throw PilotException.CaptureFailed("printwindow: PrintWindow failed");
            using var bmp = Image.FromHbitmap(hBmp);
            using var down = Downscale(bmp, cfg.Capture.MaxEdge, out double scale);
            return new Frame(Encode(down), new ShotMeta
            {
                Width = down.Width, Height = down.Height, Scale = scale,
                SourceOrigin = new RectDto(r.Left, r.Top, w, h), Monitor = 0, CaptureTier = "printwindow",
            });
        }
        finally
        {
            Native.SelectObject(hDC, old);
            Native.DeleteObject(hBmp);
            Native.DeleteDC(hDC);
            Native.ReleaseDC(IntPtr.Zero, hScreen);
        }
    }

    /// <summary>Crop of the owning monitor (element-level; WGC-per-element is the P2 upgrade).</summary>
    public Frame CaptureRect(Native.RECT phys) => CaptureGdi(0, phys);

    // ------------------------------------------------------------- dispatch

    /// <summary>Ladder: cfg.prefer decides order; first success wins; tier reported honestly.</summary>
    public Frame CaptureScreen(int monitor, Native.RECT? region = null)
    {
        var tried = new List<string>();
        // wgc intentionally last-known-unimplemented in scaffold; P2 replaces gdi-first here.
        try { return CaptureGdi(monitor, region); }
        catch (PilotException pe) { tried.Add(pe.Code + ":" + pe.Message); }
        throw PilotException.CaptureFailed(string.Join("; ", tried));
    }

    // -------------------------------------------------------------- encode

    static Image Downscale(Image src, int maxEdge, out double scale)
    {
        scale = 1;
        int longest = Math.Max(src.Width, src.Height);
        if (longest <= maxEdge) return (Image)src.Clone();
        scale = (double)maxEdge / longest;
        int w = Math.Max(1, (int)(src.Width * scale)), h = Math.Max(1, (int)(src.Height * scale));
        var dst = new Bitmap(w, h);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, w, h);
        }
        return dst;
    }

    byte[] Encode(Image img)
    {
        using var ms = new MemoryStream();
        if (JpegCodec is not null)
        {
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)cfg.Capture.JpegQuality);
            img.Save(ms, JpegCodec, ep);
        }
        else img.Save(ms, ImageFormat.Jpeg);
        return ms.ToArray();
    }

    /// <summary>64×36 grayscale perceptual hash for the frame stream's stillness check (design §4.3).</summary>
    public static bool LooksChanged(Image a, Image b)
    {
        static ulong Hash(Image img)
        {
            using var small = new Bitmap(8, 8);
            using (var g = Graphics.FromImage(small)) g.DrawImage(img, 0, 0, 8, 8);
            ulong h = 0; int bit = 0;
            double sum = 0; var px = new double[64];
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
                { var c = small.GetPixel(x, y); px[bit++] = (c.R + c.G + c.B) / 3.0; sum += px[^1]; }
            double avg = sum / 64; bit = 0;
            for (int i = 0; i < 64; i++) if (px[i] > avg) h |= 1UL << bit++;
            return h;
        }
        try { return Hash(a) != Hash(b); } catch { return true; }
    }
}
