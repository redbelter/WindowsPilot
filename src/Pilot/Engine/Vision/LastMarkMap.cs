using WindowsPilot.Core;

namespace WindowsPilot.Engine.Vision;

/// <summary>
/// The mark→ref map of the LAST marks screenshot (design §7): marks are
/// rebuilt every shot, so only the latest map is valid — pilot_click(mark=n)
/// resolves against this. Thread-safe swap; stale-mark clicks fail loudly
/// (not_found) instead of guessing.
/// </summary>
public static class LastMarkMap
{
    static volatile Dictionary<int, string>? _current;

    public static IReadOnlyDictionary<int, string>? Current => _current;

    public static void Update(IReadOnlyList<MarkDto> marks) =>
        _current = marks.ToDictionary(m => m.N, m => m.Ref);
}
