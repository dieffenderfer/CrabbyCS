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
    public static readonly RetroTheme Parchment = new()
    {
        Name         = "Parchment",
        Face         = new(213, 196, 156, 255),
        FaceLight    = new(232, 218, 184, 255),
        Highlight    = new(248, 238, 212, 255),
        Shadow       = new(140, 116,  78, 255),
        DarkShadow   = new( 64,  46,  24, 255),
        SunkenBg     = new(245, 232, 200, 255),
        TitleActive  = new( 96,  56,  24, 255),
        TitleGradEnd = new(160, 108,  56, 255),
        TitleText    = new(248, 238, 212, 255),
        TitleInactive= new(160, 138, 102, 255),
        BodyText     = new( 32,  20,  12, 255),
        DisabledText = new(140, 116,  78, 255),
        Desktop      = new(115,  82,  46, 255),
    };

    public static readonly RetroTheme CreamyBeige = new()
    {
        Name         = "Creamy Beige",
        Face         = new(238, 228, 206, 255),
        FaceLight    = new(248, 240, 224, 255),
        Highlight    = new(255, 250, 240, 255),
        Shadow       = new(176, 160, 130, 255),
        DarkShadow   = new( 96,  84,  64, 255),
        SunkenBg     = new(252, 246, 230, 255),
        TitleActive  = new(132,  92,  56, 255),
        TitleGradEnd = new(196, 148,  96, 255),
        TitleText    = new(255, 250, 240, 255),
        TitleInactive= new(192, 176, 144, 255),
        BodyText     = new( 56,  40,  24, 255),
        DisabledText = new(168, 152, 124, 255),
        Desktop      = new(160, 132,  96, 255),
    };

    public static readonly RetroTheme Midnight = new()
    {
        Name         = "Midnight",
        Face         = new( 36,  40,  56, 255),
        FaceLight    = new( 56,  60,  80, 255),
        Highlight    = new( 88,  96, 120, 255),
        Shadow       = new( 16,  20,  32, 255),
        DarkShadow   = new(  0,   0,   8, 255),
        SunkenBg     = new( 16,  20,  32, 255),
        TitleActive  = new( 16,  24,  72, 255),
        TitleGradEnd = new( 56,  88, 168, 255),
        TitleText    = new(232, 232, 248, 255),
        TitleInactive= new( 48,  56,  80, 255),
        BodyText     = new(220, 220, 240, 255),
        DisabledText = new(112, 120, 144, 255),
        Desktop      = new(  8,  12,  24, 255),
    };

    public static readonly RetroTheme Twilight = new()
    {
        Name         = "Twilight",
        Face         = new( 64,  56,  88, 255),
        FaceLight    = new( 96,  84, 128, 255),
        Highlight    = new(144, 128, 184, 255),
        Shadow       = new( 40,  32,  56, 255),
        DarkShadow   = new( 16,  12,  24, 255),
        SunkenBg     = new( 32,  24,  48, 255),
        TitleActive  = new( 80,  32, 112, 255),
        TitleGradEnd = new(192,  96, 184, 255),
        TitleText    = new(248, 232, 240, 255),
        TitleInactive= new( 80,  72, 104, 255),
        BodyText     = new(232, 224, 248, 255),
        DisabledText = new(128, 116, 152, 255),
        Desktop      = new( 24,  16,  40, 255),
    };

    public static readonly RetroTheme Slate = new()
    {
        Name         = "Slate",
        Face         = new( 72,  76,  84, 255),
        FaceLight    = new(104, 112, 124, 255),
        Highlight    = new(160, 168, 184, 255),
        Shadow       = new( 40,  44,  52, 255),
        DarkShadow   = new( 12,  16,  24, 255),
        SunkenBg     = new( 32,  36,  44, 255),
        TitleActive  = new( 56,  72,  96, 255),
        TitleGradEnd = new(112, 144, 176, 255),
        TitleText    = new(232, 240, 248, 255),
        TitleInactive= new( 80,  88, 104, 255),
        BodyText     = new(224, 232, 240, 255),
        DisabledText = new(128, 136, 152, 255),
        Desktop      = new( 24,  28,  36, 255),
    };

    public static readonly RetroTheme Forest = new()
    {
        Name         = "Forest",
        Face         = new(124, 144, 108, 255),
        FaceLight    = new(160, 184, 144, 255),
        Highlight    = new(208, 224, 184, 255),
        Shadow       = new( 72,  88,  60, 255),
        DarkShadow   = new( 24,  40,  16, 255),
        SunkenBg     = new(232, 240, 216, 255),
        TitleActive  = new( 32,  72,  32, 255),
        TitleGradEnd = new( 96, 144,  72, 255),
        TitleText    = new(232, 240, 200, 255),
        TitleInactive= new(120, 136, 104, 255),
        BodyText     = new( 16,  32,   8, 255),
        DisabledText = new(112, 128,  96, 255),
        Desktop      = new( 40,  72,  32, 255),
    };

    public static readonly RetroTheme Terminal = new()
    {
        Name         = "Terminal",
        Face         = new( 16,  16,  16, 255),
        FaceLight    = new( 32,  40,  32, 255),
        Highlight    = new( 80, 200,  80, 255),
        Shadow       = new(  8,  24,   8, 255),
        DarkShadow   = new(  0,   0,   0, 255),
        SunkenBg     = new(  0,   8,   0, 255),
        TitleActive  = new( 16,  64,  16, 255),
        TitleGradEnd = new( 48, 128,  48, 255),
        TitleText    = new( 80, 240,  80, 255),
        TitleInactive= new( 32,  64,  32, 255),
        BodyText     = new( 48, 220,  48, 255),
        DisabledText = new( 48, 100,  48, 255),
        Desktop      = new(  0,   0,   0, 255),
    };

    public static readonly RetroTheme RoseQuartz = new()
    {
        Name         = "Rose Quartz",
        Face         = new(248, 216, 216, 255),
        FaceLight    = new(252, 232, 232, 255),
        Highlight    = new(255, 248, 248, 255),
        Shadow       = new(184, 144, 152, 255),
        DarkShadow   = new( 96,  56,  64, 255),
        SunkenBg     = new(255, 244, 244, 255),
        TitleActive  = new(176,  72, 104, 255),
        TitleGradEnd = new(232, 144, 168, 255),
        TitleText    = new(255, 248, 248, 255),
        TitleInactive= new(200, 168, 176, 255),
        BodyText     = new( 64,  24,  40, 255),
        DisabledText = new(176, 144, 152, 255),
        Desktop      = new(192, 128, 144, 255),
    };

    public static readonly RetroTheme[] AllThemes =
    {
        Win95Default, CreamyBeige, Parchment, RoseQuartz, Forest,
        RainyDay, Plum, HotDogStand,
        Slate, Twilight, Midnight, Terminal,
    };

    public static void SetTheme(string name)
    {
        foreach (var t in AllThemes)
            if (t.Name == name) { Current = t; return; }
    }
}
