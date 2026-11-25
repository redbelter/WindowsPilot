using WindowsPilot.Core;

namespace WindowsPilot.Engine.Vision;

/// <summary>
/// Transform of the LAST screenshot (design §6 companion to LastMarkMap):
/// clients pick coordinates in IMAGE space; click x,y are physical px. To spare
/// every client the multiply-by-1/scale dance, pilot_click accepts shot:true
/// and maps image-space px through this meta server-side. Rebuilt every shot.
/// </summary>
public static class LastShotMap
{
    static volatile ShotMeta? _current;

    public static ShotMeta? Current => _current;

    public static void Update(ShotMeta meta) => _current = meta;

    /// <summary>Image-space px → physical px using the last shot's transform.</summary>
    public static (int x, int y) ToPhysical(int x, int y)
    {
        var m = _current;
        if (m is null || m.Scale <= 0) return (x, y);
        return ((int)Math.Round(m.SourceOrigin.x + x / m.Scale),
                (int)Math.Round(m.SourceOrigin.y + y / m.Scale));
    }
}
