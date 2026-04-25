using System.Numerics;
using Raylib_cs;

namespace MouseHouse.Core;

public static class FontManager
{
    private static Font? _font;
    private static string _fontFile = "";
    private static string _basePath = "";

    public const string DefaultFontFile = "Lora.ttf";
    private const int PixelLoadSize = 14;

    public static string CurrentFontFile => _fontFile;

    public static void Init(string basePath)
    {
        _basePath = basePath;
    }

    public static void SetFont(string fontFile)
    {
        if (_font.HasValue)
            Raylib.UnloadFont(_font.Value);

        _fontFile = fontFile;

        if (string.IsNullOrEmpty(fontFile))
        {
            _font = null;
            return;
        }

        var path = Path.Combine(_basePath, "assets/fonts", fontFile);
        if (!File.Exists(path))
        {
            _font = null;
            _fontFile = "";
            return;
        }

        var font = Raylib.LoadFontEx(path, PixelLoadSize, null, 0);
        Raylib.SetTextureFilter(font.Texture, TextureFilter.Point);
        _font = font;
    }

    public static void DrawText(string text, int x, int y, int fontSize, Color color)
    {
        if (_font.HasValue)
            Raylib.DrawTextEx(_font.Value, text, new Vector2(x, y), fontSize, 0, color);
        else
            Raylib.DrawText(text, x, y, fontSize, color);
    }

    public static int MeasureText(string text, int fontSize)
    {
        if (_font.HasValue)
            return (int)Raylib.MeasureTextEx(_font.Value, text, fontSize, 0).X;
        else
            return Raylib.MeasureText(text, fontSize);
    }
}
