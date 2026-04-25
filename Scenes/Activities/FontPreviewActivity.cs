using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities;

public class FontPreviewActivity : IActivity
{
    public Vector2 PanelSize => new(700, 520);
    public bool IsFinished { get; private set; }

    private readonly AssetCache _assets;
    private readonly List<FontEntry> _fonts = new();
    private float _scroll;
    private const float ScrollSpeed = 40f;
    private const int TitleBarH = 28;
    private const string SampleLine = "Mouse House! The quick brown fox. 0123456789";

    private static readonly int[] PreviewSizes = { 16, 20, 28 };

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

            var entry = new FontEntry { Name = label };
            foreach (int size in PreviewSizes)
            {
                var font = Raylib.LoadFontEx(path, size, null, 0);
                Raylib.SetTextureFilter(font.Texture, TextureFilter.Point);
                entry.Sizes.Add((size, font));
            }
            _fonts.Add(entry);
        }
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset, bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var wheel = Raylib.GetMouseWheelMove();
        _scroll -= wheel * ScrollSpeed;

        if (Raylib.IsKeyPressed(KeyboardKey.Down)) _scroll += ScrollSpeed;
        if (Raylib.IsKeyPressed(KeyboardKey.Up)) _scroll -= ScrollSpeed;

        float maxScroll = Math.Max(0, GetContentHeight() - (PanelSize.Y - TitleBarH - 16));
        _scroll = Math.Clamp(_scroll, 0, maxScroll);

        if (leftPressed)
        {
            var closeRect = new Rectangle(
                panelOffset.X + PanelSize.X - 40, panelOffset.Y, 40, TitleBarH);
            if (Raylib.CheckCollisionPointRec(mousePos, closeRect))
                IsFinished = true;
        }

        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
            IsFinished = true;
    }

    public void Draw(Vector2 offset)
    {
        int px = (int)offset.X;
        int py = (int)offset.Y;
        int pw = (int)PanelSize.X;
        int ph = (int)PanelSize.Y;

        // Panel background
        Raylib.DrawRectangle(px, py, pw, ph, new Color(30, 30, 35, 255));
        Raylib.DrawRectangleLines(px, py, pw, ph, new Color(60, 60, 65, 255));

        // Title bar
        Raylib.DrawRectangle(px, py, pw, TitleBarH, new Color(45, 45, 50, 255));
        Raylib.DrawText("Font Preview  (scroll to see more)", px + 10, py + 7, 14, new Color(200, 200, 200, 255));
        Raylib.DrawText("X", px + pw - 24, py + 7, 14, new Color(200, 100, 100, 255));

        // Scissor to content area — no screen clamping (GetScreenWidth returns 1 on transparent windows)
        Raylib.BeginScissorMode(px + 1, py + TitleBarH, pw - 2, ph - TitleBarH - 1);

        float y = py + TitleBarH + 8 - _scroll;

        // Default font
        y = DrawSection(y, px + 8, pw - 16, "Raylib Default (current)", null);

        foreach (var entry in _fonts)
            y = DrawSection(y, px + 8, pw - 16, entry.Name, entry);

        Raylib.EndScissorMode();

        // Scrollbar
        float viewH = ph - TitleBarH - 8;
        float totalH = GetContentHeight();
        if (totalH > viewH)
        {
            float barH = Math.Max(20, viewH * viewH / totalH);
            float maxScroll = totalH - viewH;
            float barY = py + TitleBarH + 4 + (_scroll / maxScroll) * (viewH - barH);
            Raylib.DrawRectangleRounded(
                new Rectangle(px + pw - 8, barY, 5, barH),
                0.5f, 4, new Color(100, 100, 110, 150));
        }
    }

    private float DrawSection(float y, float x, float w, string name, FontEntry? entry)
    {
        Raylib.DrawRectangle((int)x, (int)y, (int)w, 22, new Color(50, 90, 140, 200));
        Raylib.DrawText(name, (int)x + 8, (int)y + 4, 14, new Color(255, 255, 255, 255));
        y += 28;

        if (entry == null)
        {
            foreach (int size in PreviewSizes)
            {
                Raylib.DrawText($"{size}px", (int)x + 4, (int)y + 2, 10, new Color(110, 110, 120, 255));
                Raylib.DrawText(SampleLine, (int)x + 36, (int)y, size, new Color(220, 220, 225, 255));
                y += size + 8;
            }
        }
        else
        {
            foreach (var (size, font) in entry.Sizes)
            {
                Raylib.DrawText($"{size}px", (int)x + 4, (int)y + 2, 10, new Color(110, 110, 120, 255));
                Raylib.DrawTextEx(font, SampleLine, new Vector2(x + 36, y), size, 0, new Color(220, 220, 225, 255));
                y += size + 8;
            }
        }

        y += 4;
        Raylib.DrawLineEx(new Vector2(x + 4, y), new Vector2(x + w - 4, y), 1, new Color(55, 55, 65, 200));
        y += 10;

        return y;
    }

    private float GetContentHeight()
    {
        float h = 8;
        int sections = 1 + _fonts.Count;
        for (int s = 0; s < sections; s++)
        {
            h += 28;
            foreach (int size in PreviewSizes)
                h += size + 8;
            h += 14;
        }
        return h;
    }

    public void Close()
    {
        foreach (var entry in _fonts)
            foreach (var (_, font) in entry.Sizes)
                Raylib.UnloadFont(font);
        _fonts.Clear();
    }

    private class FontEntry
    {
        public string Name = "";
        public List<(int size, Font font)> Sizes = new();
    }
}
