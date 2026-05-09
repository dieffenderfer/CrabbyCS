using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities.Chess;

/// <summary>
/// Chess piece font picker — lets the user swap between bundled fonts that
/// carry the Unicode chess pieces (U+2654-265F) so they can find one that
/// looks right and that their machine's text renderer is happy with. The
/// selection persists via a small file in the save dir, same pattern as
/// <see cref="ChessBoardThemes"/>, so retro and non-retro chess UIs share it.
///
/// Of the ~25 fonts bundled in assets/core/fonts/, only DejaVuSans-fallback
/// (the GlyphFallback font) actually contains the chess codepoints. The
/// pre-subset copies of DejaVu Serif and DejaVu Sans Bold added here cover
/// just ASCII + U+2654-265F so they're a few KB each — tiny tax for a real
/// piece-style choice (sans / sans-bold / serif).
/// </summary>
public record ChessPieceFontDef(string Name, string FileName);

public static class ChessPieceFonts
{
    public static readonly ChessPieceFontDef[] All =
    {
        new("DejaVu Sans",       "DejaVuSans-fallback.ttf"),
        new("DejaVu Sans Bold",  "DejaVuSansBold-chess.ttf"),
        new("DejaVu Serif",      "DejaVuSerif-chess.ttf"),
    };

    private static int _idx;
    public static ChessPieceFontDef Current => All[_idx];
    public static int CurrentIdx => _idx;

    private static string PathStr
        => Path.Combine(SaveManager.SaveDirectory, "chess_piece_font.txt");
    private static DateTime _lastSeenUtc = DateTime.MinValue;

    private static readonly Dictionary<string, Font> _fontCache = new();

    public static void Load()
    {
        try
        {
            if (!File.Exists(PathStr)) return;
            var name = File.ReadAllText(PathStr).Trim();
            for (int i = 0; i < All.Length; i++)
                if (All[i].Name == name) { _idx = i; break; }
            _lastSeenUtc = File.GetLastWriteTimeUtc(PathStr);
        }
        catch { /* default */ }
    }

    public static ChessPieceFontDef Cycle()
    {
        _idx = (_idx + 1) % All.Length;
        Save();
        return Current;
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(SaveManager.SaveDirectory);
            File.WriteAllText(PathStr, Current.Name);
            _lastSeenUtc = File.GetLastWriteTimeUtc(PathStr);
        }
        catch { /* best-effort */ }
    }

    public static bool PollExternalChange()
    {
        try
        {
            if (!File.Exists(PathStr)) return false;
            var mtime = File.GetLastWriteTimeUtc(PathStr);
            if (mtime <= _lastSeenUtc) return false;
            _lastSeenUtc = mtime;
            var name = File.ReadAllText(PathStr).Trim();
            for (int i = 0; i < All.Length; i++)
                if (All[i].Name == name) { _idx = i; return true; }
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// Lazily-load and cache the active piece font. Loading the chess
    /// codepoints into the atlas ensures the glyphs are actually
    /// rasterised regardless of the font's default codepoint range.
    /// </summary>
    public static Font GetFont()
    {
        var fn = Current.FileName;
        if (_fontCache.TryGetValue(fn, out var f)) return f;

        var path = Path.Combine(AppContext.BaseDirectory, "assets/core/fonts", fn);
        if (!File.Exists(path))
        {
            // Missing file — fall back to whatever GlyphFallback's font is
            // (which is DejaVuSans-fallback by definition). At worst we get
            // the Sans look for an entry that wasn't deployed.
            var fallback = GlyphFallback.Get();
            return fallback ?? Raylib.GetFontDefault();
        }

        // Bake just the ASCII + chess range so each font's atlas is small.
        var codepoints = new List<int>();
        for (int cp = 0x20; cp <= 0x7E; cp++) codepoints.Add(cp);
        for (int cp = 0x2654; cp <= 0x265F; cp++) codepoints.Add(cp);
        var arr = codepoints.ToArray();

        var loaded = Raylib.LoadFontEx(path, 64, arr, arr.Length);
        // Point filter — bilinear AA plus the 8-direction outline stamp
        // in DrawPieceGlyph compounded into a fuzzy halo around white
        // pieces. Crisp pixel edges play nicer with the W95 retro chrome
        // around the rest of this activity, and they let a thinner
        // outline read as a hard 1-px line instead of a soft glow.
        Raylib.SetTextureFilter(loaded.Texture, TextureFilter.Point);
        _fontCache[fn] = loaded;
        return loaded;
    }
}
