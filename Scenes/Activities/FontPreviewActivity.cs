using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities;

public class FontPreviewActivity : IActivity
{
    public Vector2 PanelSize => new(700, 520);
    public bool IsFinished { get; private set; }

    private readonly AssetCache _assets;
    private readonly List<(string name, Font font)> _fonts = new();
    private float _scroll;
    private const float ScrollSpeed = 30f;

    private static readonly string[] SampleTexts =
    {
        "Mouse House Desktop Pet",
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
        "abcdefghijklmnopqrstuvwxyz 0123456789",
    };

    private static readonly int[] PreviewSizes = { 12, 16, 20 };

    private static readonly (string file, string label)[] FontFiles =
    {
        ("PressStart2P.ttf", "Press Start 2P"),
        ("Silkscreen.ttf", "Silkscreen"),
        ("SilkscreenBold.ttf", "Silkscreen Bold"),
        ("VT323.ttf", "VT323"),
        ("DotGothic16.ttf", "DotGothic16"),
        ("PixelifySans.ttf", "Pixelify Sans"),
        ("Jersey10.ttf", "Jersey 10"),
        ("Jersey15.ttf", "Jersey 15"),
        ("ShareTechMono.ttf", "Share Tech Mono"),
        ("Bungee.ttf", "Bungee Shade"),
    };

    public FontPreviewActivity(AssetCache assets)
    {
        _assets = assets;
    }

    public void Load()
    {
        foreach (var (file, label) in FontFiles)
        {
            var path = Path.Combine(_assets.BasePath, "assets/fonts", file);
            if (!File.Exists(path)) continue;
            var font = Raylib.LoadFontEx(path, 48, null, 0);
            Raylib.SetTextureFilter(font.Texture, TextureFilter.Point);
            _fonts.Add((label, font));
        }
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset, bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var wheel = Raylib.GetMouseWheelMove();
        _scroll -= wheel * ScrollSpeed;

        float contentHeight = GetContentHeight();
        float viewHeight = PanelSize.Y - 40;
        float maxScroll = Math.Max(0, contentHeight - viewHeight);
        _scroll = Math.Clamp(_scroll, 0, maxScroll);

        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
            IsFinished = true;
    }

    public void Draw(Vector2 offset)
    {
        int px = (int)offset.X;
        int py = (int)offset.Y;
        int pw = (int)PanelSize.X;
        int ph = (int)PanelSize.Y;

        Raylib.DrawRectangle(px, py, pw, ph, new Color(30, 30, 35, 255));
        Raylib.DrawRectangleLines(px, py, pw, ph, new Color(60, 60, 65, 255));

        // Title bar
        Raylib.DrawRectangle(px, py, pw, 28, new Color(45, 45, 50, 255));
        Raylib.DrawText("Font Preview", px + 10, py + 6, 16, new Color(200, 200, 200, 255));
        Raylib.DrawText("X", px + pw - 28, py + 6, 16, new Color(200, 100, 100, 255));

        // Clip region
        Raylib.BeginScissorMode(px + 1, py + 29, pw - 2, ph - 30);

        float y = py + 36 - _scroll;

        // Default font section
        y = DrawFontSection(px, y, pw, "Raylib Default (current)", Raylib.GetFontDefault(), true);

        // Each loaded font
        foreach (var (name, font) in _fonts)
        {
            y = DrawFontSection(px, y, pw, name, font, false);
        }

        Raylib.EndScissorMode();

        // Scroll indicator
        float contentHeight = GetContentHeight();
        float viewHeight = ph - 40;
        if (contentHeight > viewHeight)
        {
            float barHeight = Math.Max(20, viewHeight * viewHeight / contentHeight);
            float barY = py + 30 + (_scroll / (contentHeight - viewHeight)) * (viewHeight - barHeight);
            Raylib.DrawRectangle(px + pw - 6, (int)barY, 4, (int)barHeight, new Color(100, 100, 110, 150));
        }
    }

    private float DrawFontSection(int px, float y, int pw, string name, Font font, bool isDefault)
    {
        float startY = y;

        // Font name header
        Raylib.DrawRectangle(px + 10, (int)y, pw - 20, 24, new Color(50, 90, 140, 200));
        Raylib.DrawText(name, px + 16, (int)y + 4, 16, new Color(255, 255, 255, 255));
        y += 30;

        foreach (int size in PreviewSizes)
        {
            // Size label
            Raylib.DrawText($"{size}px:", px + 16, (int)y + 2, 12, new Color(140, 140, 150, 255));

            foreach (var sample in SampleTexts)
            {
                if (isDefault)
                {
                    Raylib.DrawText(sample, px + 52, (int)y + 2, size, new Color(220, 220, 225, 255));
                }
                else
                {
                    Raylib.DrawTextEx(font, sample, new Vector2(px + 52, y + 2), size, 1, new Color(220, 220, 225, 255));
                }
                y += size + 6;
            }
            y += 4;
        }

        // Separator
        Raylib.DrawLineEx(
            new Vector2(px + 16, y + 4),
            new Vector2(px + pw - 16, y + 4),
            1, new Color(60, 60, 70, 200));
        y += 12;

        return y;
    }

    private float GetContentHeight()
    {
        float height = 8;
        int sections = 1 + _fonts.Count;
        for (int s = 0; s < sections; s++)
        {
            height += 30; // header
            foreach (int size in PreviewSizes)
            {
                height += (size + 6) * SampleTexts.Length;
                height += 4;
            }
            height += 12; // separator
        }
        return height;
    }

    public void Close()
    {
        foreach (var (_, font) in _fonts)
            Raylib.UnloadFont(font);
        _fonts.Clear();
    }
}
