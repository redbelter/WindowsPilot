using System.Drawing;
using WindowsPilot.Core;

namespace WindowsPilot.Engine.Vision;

/// <summary>
/// Set-of-marks renderer (design §7): draw indexed boxes over actionable
/// elements, return the mark→ref map. The boxes are a POINTER to refs, never
/// a coordinate source — click(mark) resolves to the ref, pattern-first.
/// Runs on the capture thread (pure System.Drawing on the already-downscaled
/// bitmap, so marks land in IMAGE space and the client's inverse transform
/// applies to both pixels and boxes alike).
/// </summary>
public static class Marks
{
    public const string ColorHex = "#f43f5e";

    public static List<MarkDto> Pick(NodeDto[] actionable, int maxMarks)
    {
        // dedupe: drop elements whose center sits inside a smaller actionable's rect
        var byArea = actionable
            .Where(n => n.Rect is not null && n.Rect.w >= 8 && n.Rect.h >= 8 && !n.Offscreen)
            .OrderBy(n => n.Rect!.w * n.Rect.h)
            .ToList();
        var picked = new List<MarkDto>();
        foreach (var n in byArea)
        {
            if (picked.Count >= maxMarks) break;
            var c = new Point(n.Rect!.x + n.Rect.w / 2, n.Rect.y + n.Rect.h / 2);
            if (picked.Any(p => InRect(c, p.Rect))) continue;   // covered by a deeper pick
            picked.Add(new MarkDto
            {
                N = picked.Count + 1, Ref = n.Ref, Rect = n.Rect,
                ControlType = n.ControlType, Name = Uia.UiaService.Trunc(n.Name, 40),
            });
        }
        return picked;
    }

    static bool InRect(Point p, RectDto r) => p.X >= r.x && p.X < r.x + r.w && p.Y >= r.y && p.Y < r.y + r.h;

    /// <summary>Draw boxes/labels onto the shot bitmap. Rect coords are PHYSICAL; caller passes the shot's transform so we can map px→image px.</summary>
    public static void Draw(Bitmap shot, IReadOnlyList<MarkDto> marks, RectDto sourceOrigin, double scale)
    {
        using var g = Graphics.FromImage(shot);
        using var pen = new Pen(Color.FromArgb(0xF4, 0x3F, 0x5E), 2);
        using var font = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var lblBg = new SolidBrush(Color.FromArgb(220, 0xF4, 0x3F, 0x5E));
        using var lblFg = new SolidBrush(Color.White);

        foreach (var m in marks)
        {
            int x = (int)((m.Rect.x - sourceOrigin.x) * scale);
            int y = (int)((m.Rect.y - sourceOrigin.y) * scale);
            int w = Math.Max(4, (int)(m.Rect.w * scale));
            int h = Math.Max(4, (int)(m.Rect.h * scale));
            if (x + w < 0 || y + h < 0 || x > shot.Width || y > shot.Height) continue;   // off the shot
            g.DrawRectangle(pen, x, y, w, h);

            var label = m.N.ToString();
            var sz = g.MeasureString(label, font);
            // alternating label offset avoids collisions between adjacent boxes' corners
            int lx = m.N % 2 == 0 ? x : Math.Max(0, x - (int)sz.Width - 2);
            int ty = Math.Max(0, y - (int)sz.Height);
            g.FillRectangle(lblBg, lx, ty, (int)sz.Width + 2, (int)sz.Height);
            g.DrawString(label, font, lblFg, lx + 1, ty);
        }
    }
}
