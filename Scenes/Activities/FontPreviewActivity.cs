using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities;

public class FontPreviewActivity : IActivity
{
    public Vector2 PanelSize => new(800, 560);
    public bool IsFinished { get; private set; }

    private readonly AssetCache _assets;
    private readonly Action<string>? _onFontSelected;
    private readonly List<(string file, string name, Font font)> _fonts = new();
    private float _scroll;
    private int _hoveredRow = -1;
    private const int RowHeight = 44;
    private const int TitleBarH = 28;
    private const string Sample = "Mouse House! The quick brown fox jumps. 0123456789";

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
        ("Inconsolata.ttf", "Inconsolata"),
        ("RubikMonoOne.ttf", "Rubik Mono One"),
        ("SpecialElite.ttf", "Special Elite"),
        ("Coustard.ttf", "Coustard"),
        ("Righteous.ttf", "Righteous"),
        ("Lora.ttf", "Lora"),
        ("ConcertOne.ttf", "Concert One"),
        ("FredokaOne.ttf", "Fredoka"),
        ("PermanentMarker.ttf", "Permanent Marker"),
        ("Audiowide.ttf", "Audiowide"),
    };

    public FontPreviewActivity(AssetCache assets, Action<string>? onFontSelected = null)
    {
        _assets = assets;
        _onFontSelected = onFontSelected;
    }

    public void Load()
    {
        foreach (var (file, label) in FontFiles)
        {
            var path = Path.Combine(_assets.BasePath, "assets/fonts", file);
            if (!File.Exists(path)) continue;
            var font = Raylib.LoadFontEx(path, 20, null, 0);
            Raylib.SetTextureFilter(font.Texture, TextureFilter.Point);
            _fonts.Add((file, label, font));
        }
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
        bool leftPressed, bool leftReleased, bool rightPressed)
    {
        _scroll -= Raylib.GetMouseWheelMove() * 50f;
        if (Raylib.IsKeyPressed(KeyboardKey.Down)) _scroll += 50f;
        if (Raylib.IsKeyPressed(KeyboardKey.Up)) _scroll -= 50f;
        float totalRows = _fonts.Count + 1;
        float maxScroll = Math.Max(0, totalRows * RowHeight - (PanelSize.Y - TitleBarH - 20));
        _scroll = Math.Clamp(_scroll, 0, maxScroll);

        // Close button
        if (leftPressed)
        {
            var closeRect = new Rectangle(
                panelOffset.X + PanelSize.X - 40, panelOffset.Y, 40, TitleBarH);
            if (Raylib.CheckCollisionPointRec(mousePos, closeRect))
            {
                IsFinished = true;
                return;
            }
        }

        // Hit-test font rows
        float contentTop = panelOffset.Y + TitleBarH;
        float contentBot = panelOffset.Y + PanelSize.Y;
        _hoveredRow = -1;

        if (mousePos.X >= panelOffset.X && mousePos.X < panelOffset.X + PanelSize.X
            && mousePos.Y >= contentTop && mousePos.Y < contentBot)
        {
            float relY = mousePos.Y - contentTop + _scroll - 10;
            if (relY >= 0)
            {
                int row = (int)(relY / RowHeight);
                if (row >= 0 && row <= _fonts.Count)
                    _hoveredRow = row;
            }
        }

        // Click to select font
        if (leftPressed && _hoveredRow >= 0)
        {
            if (_hoveredRow == 0)
                _onFontSelected?.Invoke("");
            else
                _onFontSelected?.Invoke(_fonts[_hoveredRow - 1].file);
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
        Raylib.DrawText("Font Preview — click to select", px + 10, py + 7, 14, new Color(200, 200, 200, 255));
        Raylib.DrawText("[X]", px + pw - 36, py + 7, 14, new Color(200, 100, 100, 255));

        int contentY = py + TitleBarH;
        int contentH = ph - TitleBarH;
        Raylib.BeginScissorMode(px, contentY, pw, contentH);

        float y = contentY + 10 - _scroll;
        string currentFont = FontManager.CurrentFontFile;

        // Row 0: default font
        DrawRow(px, pw, y, 0, "Raylib Default", null, "", currentFont);
        y += RowHeight;

        // Font rows
        for (int i = 0; i < _fonts.Count; i++)
        {
            var (file, name, font) = _fonts[i];
            DrawRow(px, pw, y, i + 1, name, font, file, currentFont);
            y += RowHeight;
        }

        Raylib.EndScissorMode();
    }

    private void DrawRow(int px, int pw, float y, int rowIndex, string name,
        Font? font, string fontFile, string currentFont)
    {
        bool isSelected = fontFile == currentFont;
        bool isHovered = rowIndex == _hoveredRow;

        // Hover highlight
        if (isHovered)
            Raylib.DrawRectangle(px + 4, (int)y - 2, pw - 8, RowHeight, new Color(60, 60, 70, 180));

        // Selection indicator
        if (isSelected)
            Raylib.DrawRectangle(px + 4, (int)y - 2, 3, RowHeight, new Color(100, 180, 255, 255));

        // Label
        var labelColor = isSelected ? new Color(100, 180, 255, 255) : new Color(130, 160, 210, 255);
        string prefix = isSelected ? "● " : "  ";
        Raylib.DrawText(prefix + name, px + 12, (int)y, 12, labelColor);

        // Sample text
        float textY = y + 16;
        if (font == null)
            Raylib.DrawText(Sample, px + 12, (int)textY, 22, new Color(220, 220, 225, 255));
        else
            Raylib.DrawTextEx(font.Value, Sample, new Vector2(px + 12, textY), 22, 0,
                new Color(220, 220, 225, 255));

        // Divider
        float divY = y + RowHeight - 2;
        Raylib.DrawLineEx(new Vector2(px + 8, divY), new Vector2(px + pw - 8, divY),
            1, new Color(50, 50, 55, 200));
    }

    public void Close()
    {
        foreach (var (_, _, font) in _fonts)
            Raylib.UnloadFont(font);
        _fonts.Clear();
    }
}
