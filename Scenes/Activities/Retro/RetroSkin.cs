using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities.Retro;

/// <summary>
/// Palette and primitive drawing helpers for the shared 90s-desktop chrome
/// used by every Entertainment Pack game. Colors and bevel patterns mirror
/// the standard 3D widget look from that era.
/// </summary>
public static class RetroSkin
{
    public static readonly Color Face         = new(192, 192, 192, 255);
    public static readonly Color FaceLight    = new(223, 223, 223, 255);
    public static readonly Color Highlight    = new(255, 255, 255, 255);
    public static readonly Color Shadow       = new(128, 128, 128, 255);
    public static readonly Color DarkShadow   = new(  0,   0,   0, 255);
    public static readonly Color WindowBg     = new(192, 192, 192, 255);
    public static readonly Color SunkenBg     = new(255, 255, 255, 255);
    public static readonly Color TitleActive  = new(  0,   0, 128, 255);
    public static readonly Color TitleGradEnd = new( 16, 132, 208, 255);
    public static readonly Color TitleText    = new(255, 255, 255, 255);
    public static readonly Color TitleInactive = new(128, 128, 128, 255);
    public static readonly Color BodyText     = new(  0,   0,   0, 255);
    public static readonly Color DisabledText = new(128, 128, 128, 255);
    public static readonly Color Desktop      = new(  0, 128, 128, 255);

    public const int BodyFontSize = 14;
    public const int TitleFontSize = 14;

    /// <summary>Outer raised bevel: white top/left, black bottom/right, then inner light/shadow.</summary>
    public static void DrawRaised(Rectangle r, bool fill = true)
    {
        if (fill) Raylib.DrawRectangleRec(r, Face);
        int x = (int)r.X, y = (int)r.Y, w = (int)r.Width, h = (int)r.Height;
        // outer
        Raylib.DrawRectangle(x, y, w, 1, Highlight);
        Raylib.DrawRectangle(x, y, 1, h, Highlight);
        Raylib.DrawRectangle(x, y + h - 1, w, 1, DarkShadow);
        Raylib.DrawRectangle(x + w - 1, y, 1, h, DarkShadow);
        // inner
        Raylib.DrawRectangle(x + 1, y + 1, w - 2, 1, FaceLight);
        Raylib.DrawRectangle(x + 1, y + 1, 1, h - 2, FaceLight);
        Raylib.DrawRectangle(x + 1, y + h - 2, w - 2, 1, Shadow);
        Raylib.DrawRectangle(x + w - 2, y + 1, 1, h - 2, Shadow);
    }

    /// <summary>Pressed-button look: swap highlight/shadow.</summary>
    public static void DrawPressed(Rectangle r, bool fill = true)
    {
        if (fill) Raylib.DrawRectangleRec(r, Face);
        int x = (int)r.X, y = (int)r.Y, w = (int)r.Width, h = (int)r.Height;
        Raylib.DrawRectangle(x, y, w, 1, DarkShadow);
        Raylib.DrawRectangle(x, y, 1, h, DarkShadow);
        Raylib.DrawRectangle(x + 1, y + 1, w - 2, 1, Shadow);
        Raylib.DrawRectangle(x + 1, y + 1, 1, h - 2, Shadow);
    }

    /// <summary>Sunken inset (text fields, status bar panels).</summary>
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

    public static void DrawText(string text, int x, int y, Color color, int size = BodyFontSize)
        => FontManager.DrawText(text, x, y, size, color);

    public static int MeasureText(string text, int size = BodyFontSize)
        => FontManager.MeasureText(text, size);

    public static bool PointInRect(Vector2 p, Rectangle r)
        => p.X >= r.X && p.Y >= r.Y && p.X < r.X + r.Width && p.Y < r.Y + r.Height;
}
