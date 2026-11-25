using WindowsPilot.Core;
using Windows.Graphics.Imaging;

namespace WindowsPilot.Engine.Vision;

/// <summary>One recognized word; box in IMAGE-space px of the decoded shot
/// (same space as marks) so click(shot:true) consumes boxes directly.</summary>
public sealed record OcrWord(
    [property: System.Text.Json.Serialization.JsonPropertyName("text")] string Text,
    [property: System.Text.Json.Serialization.JsonPropertyName("x")] int x,
    [property: System.Text.Json.Serialization.JsonPropertyName("y")] int y,
    [property: System.Text.Json.Serialization.JsonPropertyName("w")] int w,
    [property: System.Text.Json.Serialization.JsonPropertyName("h")] int h);

/// <summary>
/// Side-car OCR for screenshots (design §7 companion). Canvas apps that draw
/// their own UI expose almost nothing over UIA, so every read-back turn costs a
/// screenshot round-trip through an external OCR. The pilot runs the SAME
/// offline engine locally: Windows.Media.Ocr — deterministic for a given frame,
/// ~100 ms, no network, no model files. Word boxes come back in IMAGE-space px
/// (same space as marks) so click(shot:true) consumes them directly.
/// Any failure degrades to an empty list — the shot itself always succeeds.
/// </summary>
public static class OcrService
{
    public static List<OcrWord> RecognizeWords(byte[] jpeg)
    {
        var words = new List<OcrWord>();
        try
        {
            using var ms = new MemoryStream(jpeg);
            var decoder = BitmapDecoder.CreateAsync(ms.AsRandomAccessStream())
                .AsTask().GetAwaiter().GetResult();
            var sb = decoder.GetSoftwareBitmapAsync()
                .AsTask().GetAwaiter().GetResult();

            var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null) return words;

            var result = engine.RecognizeAsync(sb).AsTask().GetAwaiter().GetResult();
            foreach (var line in result.Lines)
                foreach (var w in line.Words)
                {
                    var r = w.BoundingRect;
                    words.Add(new OcrWord(w.Text,
                        (int)Math.Round(r.X), (int)Math.Round(r.Y),
                        (int)Math.Round(r.Width), (int)Math.Round(r.Height)));
                    if (words.Count >= 2000) return words;   // bounded payload
                }
        }
        catch { /* OCR is additive — never fail the screenshot for it */ }
        return words;
    }
}
