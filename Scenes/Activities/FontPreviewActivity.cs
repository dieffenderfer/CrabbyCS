using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities;

public class FontPreviewActivity : IActivity
{
    public Vector2 PanelSize => new(800, 560);
    public bool IsFinished { get; private set; }

    private readonly AssetCache _assets;
    private readonly List<(string name, Font font)> _fonts = new();
    private float _scroll;
    private const int RowHeight = 44;
    private const int TitleBarH = 28;

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
        ("Tiny5.ttf", "Tiny5"),
        ("Geo.ttf", "Geo"),
        ("Jacquard12.ttf", "Jacquard 12"),
        ("Matemasie.ttf", "Matemasie"),
        ("Orbitron.ttf", "Orbitron"),
    };

    public FontPreviewActivity(AssetCache assets) => _assets = assets;

    public void Load()
    {
        foreach (var (file, label) in FontFiles)
        {
            var path = Path.Combine(_assets.BasePath, "assets/fonts", file);
            if (!File.Exists(path)) continue;
            var font = Raylib.LoadFontEx(path, 64, null, 0);
            Raylib.SetTextureFilter(font.Texture, TextureFilter.Bilinear);
            _fonts.Add((label, font));
        }
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
        bool leftPressed, bool leftReleased, bool rightPressed)
    {
        _scroll -= Raylib.GetMouseWheelMove() * 50f;
        if (Raylib.IsKeyPressed(KeyboardKey.Down)) _scroll += 50f;
        if (Raylib.IsKeyPressed(KeyboardKey.Up)) _scroll -= 50f;
        float maxScroll = Math.Max(0, (_fonts.Count + 1) * RowHeight - (PanelSize.Y - TitleBarH - 20));
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
        int px = (int)offset.X, py = (int)offset.Y;
        int pw = (int)PanelSize.X, ph = (int)PanelSize.Y;

        // Panel bg
        Raylib.DrawRectangle(px, py, pw, ph, new Color(25, 25, 30, 255));
        Raylib.DrawRectangleLines(px, py, pw, ph, new Color(60, 60, 65, 255));

        // Title bar
        Raylib.DrawRectangle(px, py, pw, TitleBarH, new Color(45, 45, 50, 255));
        Raylib.DrawText("Font Preview", px + 10, py + 7, 14, new Color(200, 200, 200, 255));
        Raylib.DrawText("[X]", px + pw - 36, py + 7, 14, new Color(200, 100, 100, 255));

        // Content area — use panel bounds directly for scissor (never GetScreenWidth)
        int contentY = py + TitleBarH;
        int contentH = ph - TitleBarH;
        Raylib.BeginScissorMode(px, contentY, pw, contentH);

        float y = contentY + 10 - _scroll;
        const int fontSize = 22;
        const string sample = "Mouse House! The quick brown fox jumps. 0123456789";

        // Default font row
        y = DrawRow(px, pw, y, "Raylib Default (current)", null, fontSize, sample);

        foreach (var (name, font) in _fonts)
            y = DrawRow(px, pw, y, name, font, fontSize, sample);

        Raylib.EndScissorMode();
    }

    private float DrawRow(int px, int pw, float y, string name, Font? font, int fontSize, string sample)
    {
        // Label on the left
        Raylib.DrawText(name, px + 12, (int)y, 12, new Color(130, 160, 210, 255));

        // Sample text
        float textY = y + 16;
        if (font == null)
            Raylib.DrawText(sample, px + 12, (int)textY, fontSize, new Color(220, 220, 225, 255));
        else
            Raylib.DrawTextEx(font.Value, sample, new Vector2(px + 12, textY), fontSize, 0,
                new Color(220, 220, 225, 255));

        // Divider
        float divY = y + RowHeight - 2;
        Raylib.DrawLineEx(new Vector2(px + 8, divY), new Vector2(px + pw - 8, divY),
            1, new Color(50, 50, 55, 200));

        return y + RowHeight;
    }

    public void Close()
    {
        foreach (var (_, font) in _fonts)
            Raylib.UnloadFont(font);
        _fonts.Clear();
    }
}
