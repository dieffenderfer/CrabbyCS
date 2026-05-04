using System.Numerics;
using Raylib_cs;

namespace MouseHouse.Scenes.Activities.Retro;

public class RetroTheme
{
    public string Name = "";
    public Color Face;
    public Color FaceLight;
    public Color Highlight;
    public Color Shadow;
    public Color DarkShadow;
    public Color SunkenBg;
    public Color TitleActive;
    public Color TitleGradEnd;
    public Color TitleText;
    public Color TitleInactive;
    public Color BodyText;
    public Color DisabledText;
    public Color Desktop;
}

/// <summary>
/// Palette + drawing primitives for the shared 90s-desktop chrome. The active
/// theme lives on <see cref="Current"/> — assigning a new theme repaints every
/// game/widget on the next frame, no recompile or restart needed.
/// </summary>
public static class RetroSkin
{
    public const int BodyFontSize = 16;
    public const int TitleFontSize = 16;

    public static readonly RetroTheme Win95Default = new()
    {
        Name         = "Windows Standard",
        Face         = new(192, 192, 192, 255),
        FaceLight    = new(223, 223, 223, 255),
        Highlight    = new(255, 255, 255, 255),
        Shadow       = new(128, 128, 128, 255),
        DarkShadow   = new(  0,   0,   0, 255),
        SunkenBg     = new(255, 255, 255, 255),
        TitleActive  = new(  0,   0, 128, 255),
        TitleGradEnd = new( 16, 132, 208, 255),
        TitleText    = new(255, 255, 255, 255),
        TitleInactive= new(128, 128, 128, 255),
        BodyText     = new(  0,   0,   0, 255),
        DisabledText = new(128, 128, 128, 255),
        Desktop      = new(  0, 128, 128, 255),
    };

    public static readonly RetroTheme HotDogStand = new()
    {
        Name         = "Hot Dog Stand",
        Face         = new(255, 255,   0, 255),
        FaceLight    = new(255, 255, 128, 255),
        Highlight    = new(255, 255, 255, 255),
        Shadow       = new(128,   0,   0, 255),
        DarkShadow   = new( 64,   0,   0, 255),
        SunkenBg     = new(255, 255, 255, 255),
        TitleActive  = new(255,   0,   0, 255),
        TitleGradEnd = new(255, 128,   0, 255),
        TitleText    = new(255, 255,   0, 255),
        TitleInactive= new(192, 128,   0, 255),
        BodyText     = new(  0,   0,   0, 255),
        DisabledText = new(160, 128,   0, 255),
        Desktop      = new(255, 128,   0, 255),
    };

    public static readonly RetroTheme RainyDay = new()
    {
        Name         = "Rainy Day",
        Face         = new(160, 176, 192, 255),
        FaceLight    = new(192, 208, 224, 255),
        Highlight    = new(224, 232, 240, 255),
        Shadow       = new( 96, 112, 128, 255),
        DarkShadow   = new( 32,  48,  64, 255),
        SunkenBg     = new(224, 232, 240, 255),
        TitleActive  = new( 32,  64, 128, 255),
        TitleGradEnd = new( 96, 144, 192, 255),
        TitleText    = new(255, 255, 255, 255),
        TitleInactive= new(112, 128, 144, 255),
        BodyText     = new(  0,   0,   0, 255),
        DisabledText = new(112, 128, 144, 255),
        Desktop      = new( 64,  96, 128, 255),
    };

    public static readonly RetroTheme Plum = new()
    {
        Name         = "Plum",
        Face         = new(192, 160, 192, 255),
        FaceLight    = new(216, 192, 216, 255),
        Highlight    = new(240, 224, 240, 255),
        Shadow       = new(112,  80, 112, 255),
        DarkShadow   = new( 48,  16,  48, 255),
        SunkenBg     = new(240, 224, 240, 255),
        TitleActive  = new( 96,  32,  96, 255),
        TitleGradEnd = new(160,  96, 160, 255),
        TitleText    = new(255, 255, 255, 255),
        TitleInactive= new(128, 112, 128, 255),
        BodyText     = new(  0,   0,   0, 255),
        DisabledText = new(128, 112, 128, 255),
        Desktop      = new( 96,  64,  96, 255),
    };

    public static RetroTheme Current = Win95Default;

    // Convenience accessors — let widget code stay readable.
    public static Color Face          => Current.Face;
    public static Color FaceLight     => Current.FaceLight;
    public static Color Highlight     => Current.Highlight;
    public static Color Shadow        => Current.Shadow;
    public static Color DarkShadow    => Current.DarkShadow;
    public static Color SunkenBg      => Current.SunkenBg;
    public static Color TitleActive   => Current.TitleActive;
    public static Color TitleGradEnd  => Current.TitleGradEnd;
    public static Color TitleText     => Current.TitleText;
    public static Color TitleInactive => Current.TitleInactive;
    public static Color BodyText      => Current.BodyText;
    public static Color DisabledText  => Current.DisabledText;
    public static Color Desktop       => Current.Desktop;

    // ── Font ─────────────────────────────────────────────────────────────
    // W95F.otf (W95FA, OFL 1.1 — bundled in assets/fonts/) is a pixel-accurate
    // open clone of the small system font used by 90s desktop chrome.
    private static Font? _font;
    private static bool _fontTried;
    private const int FontLoadSize = 32;

    public static Font GetFont()
    {
        if (_font.HasValue) return _font.Value;
        if (_fontTried) return Raylib.GetFontDefault();
        _fontTried = true;
        var path = Path.Combine(AppContext.BaseDirectory, "assets/fonts/W95F.otf");
        if (!File.Exists(path)) return Raylib.GetFontDefault();
        var f = Raylib.LoadFontEx(path, FontLoadSize, null, 0);
        Raylib.SetTextureFilter(f.Texture, TextureFilter.Point);
        _font = f;
        return f;
    }

    public static void DrawText(string text, int x, int y, Color color, int size = BodyFontSize)
    {
        var f = GetFont();
        Raylib.DrawTextEx(f, text, new Vector2(x, y), size, 0, color);
    }

    public static int MeasureText(string text, int size = BodyFontSize)
    {
        var f = GetFont();
        return (int)Raylib.MeasureTextEx(f, text, size, 0).X;
    }

    // ── Bevels ───────────────────────────────────────────────────────────
    public static void DrawRaised(Rectangle r, bool fill = true)
    {
        if (fill) Raylib.DrawRectangleRec(r, Face);
        int x = (int)r.X, y = (int)r.Y, w = (int)r.Width, h = (int)r.Height;
        Raylib.DrawRectangle(x, y, w, 1, Highlight);
        Raylib.DrawRectangle(x, y, 1, h, Highlight);
        Raylib.DrawRectangle(x, y + h - 1, w, 1, DarkShadow);
        Raylib.DrawRectangle(x + w - 1, y, 1, h, DarkShadow);
        Raylib.DrawRectangle(x + 1, y + 1, w - 2, 1, FaceLight);
        Raylib.DrawRectangle(x + 1, y + 1, 1, h - 2, FaceLight);
        Raylib.DrawRectangle(x + 1, y + h - 2, w - 2, 1, Shadow);
        Raylib.DrawRectangle(x + w - 2, y + 1, 1, h - 2, Shadow);
    }

    public static void DrawPressed(Rectangle r, bool fill = true)
    {
        if (fill) Raylib.DrawRectangleRec(r, Face);
        int x = (int)r.X, y = (int)r.Y, w = (int)r.Width, h = (int)r.Height;
        Raylib.DrawRectangle(x, y, w, 1, DarkShadow);
        Raylib.DrawRectangle(x, y, 1, h, DarkShadow);
        Raylib.DrawRectangle(x + 1, y + 1, w - 2, 1, Shadow);
        Raylib.DrawRectangle(x + 1, y + 1, 1, h - 2, Shadow);
    }

    public static void DrawSunken(Rectangle r, Color? fill = null)
    {
        Raylib.DrawRectangleRec(r, fill ?? SunkenBg);
        int x = (int)r.X, y = (int)r.Y, w = (int)r.Width, h = (int)r.Height;
        Raylib.DrawRectangle(x, y, w, 1, Shadow);
        Raylib.DrawRectangle(x, y, 1, h, Shadow);
        Raylib.DrawRectangle(x + 1, y, w - 1, 1, DarkShadow);
        Raylib.DrawRectangle(x, y + 1, 1, h - 1, DarkShadow);
        Raylib.DrawRectangle(x, y + h - 1, w, 1, Highlight);
        Raylib.DrawRectangle(x + w - 1, y, 1, h, Highlight);
        Raylib.DrawRectangle(x + 1, y + h - 2, w - 2, 1, FaceLight);
        Raylib.DrawRectangle(x + w - 2, y + 1, 1, h - 2, FaceLight);
    }

    public static bool PointInRect(Vector2 p, Rectangle r)
        => p.X >= r.X && p.Y >= r.Y && p.X < r.X + r.Width && p.Y < r.Y + r.Height;

    // ── Themes ───────────────────────────────────────────────────────────
    public static readonly RetroTheme[] AllThemes =
        { Win95Default, HotDogStand, RainyDay, Plum };

    public static void SetTheme(string name)
    {
        foreach (var t in AllThemes)
            if (t.Name == name) { Current = t; return; }
    }
}
