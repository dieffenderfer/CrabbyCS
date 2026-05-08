using System.Numerics;
using Raylib_cs;

namespace MouseHouse.Core;

/// <summary>
/// A bundled wider-Unicode font that fills in glyphs missing from W95F.otf —
/// chess pieces (U+2654-265F), arrows (U+2190-21FF, U+25B0-25FF), check/cross
/// (U+2713/2717), bullets, em-dashes, ellipsis, etc. W95F is a pixel-accurate
/// Win95 system clone with only ~167 glyphs (basic ASCII + a sliver of Latin-1),
/// so anything beyond that fell back to Raylib's "missing glyph" rectangle and
/// rendered as "?". Both <see cref="FontManager"/> and the retro skin now route
/// non-ASCII codepoints through this font and keep the W95F look for ASCII.
/// </summary>
public static class GlyphFallback
{
    private static Font? _font;
    private static bool _tried;
    private const string FontFile = "DejaVuSans-fallback.ttf";

    // Codepoints baked into the fallback atlas. Loading lazily-on-demand would
    // mean re-creating the atlas on first use of every new glyph — slower and
    // causes a frame hitch. Pre-baking the ranges we actually use is cheap.
    private static readonly int[] Codepoints = BuildCodepoints();

    private static int[] BuildCodepoints()
    {
        var list = new List<int>();
        // Latin-1 supplement (accented letters, ¬±× etc.)
        for (int cp = 0x00A0; cp <= 0x00FF; cp++) list.Add(cp);
        // General punctuation (en/em dash, bullets, ellipsis, etc.)
        for (int cp = 0x2010; cp <= 0x205E; cp++) list.Add(cp);
        // Superscripts / subscripts
        for (int cp = 0x2070; cp <= 0x209F; cp++) list.Add(cp);
        // Arrows
        for (int cp = 0x2190; cp <= 0x21FF; cp++) list.Add(cp);
        // Mathematical operators (≈ ≠ ≤ ≥ ∞ etc.)
        for (int cp = 0x2200; cp <= 0x22FF; cp++) list.Add(cp);
        // Box drawing (used by ASCII art borders sometimes)
        for (int cp = 0x2500; cp <= 0x257F; cp++) list.Add(cp);
        // Block elements
        for (int cp = 0x2580; cp <= 0x259F; cp++) list.Add(cp);
        // Geometric shapes (▶ ▸ ► ◄ ● ○ ★ etc.)
        for (int cp = 0x25A0; cp <= 0x25FF; cp++) list.Add(cp);
        // Miscellaneous symbols (chess U+2654-265F, ♠♥♦♣, ☼ ☀ etc.)
        for (int cp = 0x2600; cp <= 0x26FF; cp++) list.Add(cp);
        // Dingbats (✓ U+2713, ✗ U+2717, ✶ ✦ etc.)
        for (int cp = 0x2700; cp <= 0x27BF; cp++) list.Add(cp);
        return list.ToArray();
    }

    /// <summary>Codepoints below this go to the primary font; at or above, fallback.</summary>
    public const int FallbackThreshold = 0x80;

    public static bool ShouldUseFallback(int codepoint) => codepoint >= FallbackThreshold;

    public static Font? Get()
    {
        if (_font.HasValue) return _font;
        if (_tried) return null;
        _tried = true;

        var path = Path.Combine(AppContext.BaseDirectory, "assets/core/fonts", FontFile);
        if (!File.Exists(path)) return null;

        var f = Raylib.LoadFontEx(path, 64, Codepoints, Codepoints.Length);
        Raylib.SetTextureFilter(f.Texture, TextureFilter.Bilinear);
        _font = f;
        return f;
    }

    /// <summary>
    /// Iterate Unicode codepoints (surrogate-aware) grouped into runs of the
    /// same font choice, so each run can be drawn/measured in one call.
    /// </summary>
    public static IEnumerable<(string run, bool useFallback)> SplitRuns(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        int i = 0;
        bool curUseFb = ShouldUseFallback(char.ConvertToUtf32(text, 0));
        int runStart = 0;
        while (i < text.Length)
        {
            int cp = char.ConvertToUtf32(text, i);
            int step = char.IsSurrogatePair(text, i) ? 2 : 1;
            bool useFb = ShouldUseFallback(cp);
            if (useFb != curUseFb)
            {
                yield return (text.Substring(runStart, i - runStart), curUseFb);
                runStart = i;
                curUseFb = useFb;
            }
            i += step;
        }
        if (runStart < text.Length)
            yield return (text.Substring(runStart), curUseFb);
    }

    /// <summary>
    /// Draw <paramref name="text"/> using <paramref name="primary"/> for ASCII
    /// codepoints and the bundled wide-Unicode fallback for everything else.
    /// Falls back to plain primary rendering if the fallback font is missing.
    /// </summary>
    public static void DrawText(Font primary, string text, Vector2 pos, float fontSize,
                                float spacing, Color color)
    {
        var fallback = Get();
        if (!fallback.HasValue || !ContainsNonAscii(text))
        {
            Raylib.DrawTextEx(primary, text, pos, fontSize, spacing, color);
            return;
        }

        float fx = pos.X;
        foreach (var (run, useFb) in SplitRuns(text))
        {
            var font = useFb ? fallback.Value : primary;
            Raylib.DrawTextEx(font, run, new Vector2(fx, pos.Y), fontSize, spacing, color);
            fx += Raylib.MeasureTextEx(font, run, fontSize, spacing).X;
        }
    }

    public static Vector2 MeasureText(Font primary, string text, float fontSize, float spacing)
    {
        var fallback = Get();
        if (!fallback.HasValue || !ContainsNonAscii(text))
            return Raylib.MeasureTextEx(primary, text, fontSize, spacing);

        float w = 0, h = 0;
        foreach (var (run, useFb) in SplitRuns(text))
        {
            var font = useFb ? fallback.Value : primary;
            var sz = Raylib.MeasureTextEx(font, run, fontSize, spacing);
            w += sz.X;
            if (sz.Y > h) h = sz.Y;
        }
        return new Vector2(w, h);
    }

    private static bool ContainsNonAscii(string s)
    {
        for (int i = 0; i < s.Length; i++)
            if (s[i] >= FallbackThreshold) return true;
        return false;
    }
}
