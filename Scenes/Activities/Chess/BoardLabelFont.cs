using System.Numerics;
using Raylib_cs;

namespace MouseHouse.Scenes.Activities.Chess;

/// <summary>
/// Lazy loader for the Jacquard 12 pixel-art font, used only for the rank
/// (1-8) and file (a-h) labels around the chess board. Bundling Jacquard
/// just for these tiny labels gives the board an old-fashioned tournament-
/// scoresheet feel without affecting the rest of the UI's typography.
/// Atlas is baked at 32 px so we can scale down to ~12-14 px display
/// cleanly with a bilinear filter.
/// </summary>
public static class BoardLabelFont
{
    private const string FontFile = "Jacquard12.ttf";
    private const int LoadSize = 32;
    private static Font? _font;
    private static bool _tried;

    public static Font Get()
    {
        if (_font.HasValue) return _font.Value;
        if (_tried) return Raylib.GetFontDefault();
        _tried = true;

        var path = Path.Combine(AppContext.BaseDirectory, "assets/core/fonts", FontFile);
        if (!File.Exists(path)) return Raylib.GetFontDefault();

        var f = Raylib.LoadFontEx(path, LoadSize, null, 0);
        Raylib.SetTextureFilter(f.Texture, TextureFilter.Bilinear);
        _font = f;
        return f;
    }

    public static void DrawText(string text, int x, int y, int fontSize, Color color)
    {
        Raylib.DrawTextEx(Get(), text, new Vector2(x, y), fontSize, 0, color);
    }

    public static int MeasureText(string text, int fontSize)
        => (int)Raylib.MeasureTextEx(Get(), text, fontSize, 0).X;
}
