using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities;

public class PaintActivity : IActivity
{
    public Vector2 PanelSize => new(800, 600);
    public bool IsFinished { get; private set; }

    private readonly AssetCache _assets;

    // Canvas
    private const int CanvasW = 160, CanvasH = 120;
    private const int PixelScale = 4;
    private const int CanvasOffsetX = 100, CanvasOffsetY = 50;
    private Color[,] _pixels = new Color[CanvasW, CanvasH];

    // Tools
    private enum Tool { Pencil, Brush, Eraser, Fill }
    private Tool _currentTool = Tool.Pencil;

    // Colors
    private static readonly Color[] Palette =
    {
        Color.Black, Color.White,
        new((byte)128,(byte)128,(byte)128,(byte)255), // gray
        new((byte)128,(byte)0,(byte)0,(byte)255),     // dark red
        Color.Red,
        new((byte)255,(byte)128,(byte)0,(byte)255),   // orange
        Color.Yellow,
        new((byte)128,(byte)255,(byte)0,(byte)255),   // lime
        Color.Green,
        new((byte)0,(byte)128,(byte)0,(byte)255),     // dark green
        new((byte)0,(byte)255,(byte)255,(byte)255),   // cyan
        new((byte)0,(byte)128,(byte)255,(byte)255),   // light blue
        Color.Blue,
        new((byte)0,(byte)0,(byte)128,(byte)255),     // dark blue
        new((byte)128,(byte)0,(byte)255,(byte)255),   // purple
        Color.Magenta,
        new((byte)255,(byte)128,(byte)128,(byte)255), // light red
        new((byte)255,(byte)200,(byte)128,(byte)255), // peach
        new((byte)255,(byte)255,(byte)128,(byte)255), // light yellow
        new((byte)128,(byte)255,(byte)128,(byte)255), // light green
        new((byte)128,(byte)255,(byte)255,(byte)255), // light cyan
        new((byte)128,(byte)128,(byte)255,(byte)255), // light blue
        new((byte)200,(byte)150,(byte)100,(byte)255), // brown
        new((byte)100,(byte)70,(byte)40,(byte)255),   // dark brown
    };

    private Color _fgColor = Color.Black;
    private Color _bgColor = Color.White;
    private bool _drawing;
    private Vector2 _lastPixel = new(-1, -1);

    // Undo
    private readonly List<Color[,]> _undoStack = new();

    public PaintActivity(AssetCache assets)
    {
        _assets = assets;
    }

    public void Load()
    {
        // Clear canvas to white
        for (int x = 0; x < CanvasW; x++)
            for (int y = 0; y < CanvasH; y++)
                _pixels[x, y] = Color.White;
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;

        // Undo: Ctrl+Z
        if (Raylib.IsKeyDown(KeyboardKey.LeftControl) && Raylib.IsKeyPressed(KeyboardKey.Z))
        {
            if (_undoStack.Count > 0)
            {
                _pixels = _undoStack[^1];
                _undoStack.RemoveAt(_undoStack.Count - 1);
            }
        }

        // Tool selection via keys
        if (Raylib.IsKeyPressed(KeyboardKey.One)) _currentTool = Tool.Pencil;
        if (Raylib.IsKeyPressed(KeyboardKey.Two)) _currentTool = Tool.Brush;
        if (Raylib.IsKeyPressed(KeyboardKey.Three)) _currentTool = Tool.Eraser;
        if (Raylib.IsKeyPressed(KeyboardKey.Four)) _currentTool = Tool.Fill;

        // Palette click
        if (leftPressed || rightPressed)
        {
            for (int i = 0; i < Palette.Length; i++)
            {
                int px = 100 + (i % 12) * 24;
                int py = 545 + (i / 12) * 24;
                if (local.X >= px && local.X < px + 22 && local.Y >= py && local.Y < py + 22)
                {
                    if (leftPressed) _fgColor = Palette[i];
                    if (rightPressed) _bgColor = Palette[i];
                    return;
                }
            }
        }

        // Tool buttons click
        if (leftPressed)
        {
            string[] toolNames = { "Pencil", "Brush", "Eraser", "Fill" };
            for (int i = 0; i < toolNames.Length; i++)
            {
                int bx = 10, by = 60 + i * 35;
                if (local.X >= bx && local.X < bx + 80 && local.Y >= by && local.Y < by + 30)
                {
                    _currentTool = (Tool)i;
                    return;
                }
            }

            // Clear button
            if (local.X >= 10 && local.X < 90 && local.Y >= 210 && local.Y < 240)
            {
                SaveUndo();
                for (int x = 0; x < CanvasW; x++)
                    for (int y = 0; y < CanvasH; y++)
                        _pixels[x, y] = _bgColor;
                return;
            }
        }

        // Canvas drawing
        var canvasRect = new Rectangle(CanvasOffsetX, CanvasOffsetY, CanvasW * PixelScale, CanvasH * PixelScale);
        bool inCanvas = Raylib.CheckCollisionPointRec(local, canvasRect);

        if (inCanvas)
        {
            int cx = (int)((local.X - CanvasOffsetX) / PixelScale);
            int cy = (int)((local.Y - CanvasOffsetY) / PixelScale);
            cx = Math.Clamp(cx, 0, CanvasW - 1);
            cy = Math.Clamp(cy, 0, CanvasH - 1);

            if (leftPressed)
            {
                SaveUndo();
                _drawing = true;
                _lastPixel = new Vector2(-1, -1);

                if (_currentTool == Tool.Fill)
                {
                    FloodFill(cx, cy, _fgColor);
                    _drawing = false;
                    return;
                }
            }

            if (_drawing && Raylib.IsMouseButtonDown(MouseButton.Left))
            {
                var color = _currentTool == Tool.Eraser ? _bgColor : _fgColor;
                int brushSize = _currentTool == Tool.Brush ? 2 : 1;

                // Interpolate between last and current for smooth lines
                if (_lastPixel.X >= 0)
                {
                    int steps = (int)Math.Max(Math.Abs(cx - _lastPixel.X), Math.Abs(cy - _lastPixel.Y)) + 1;
                    for (int s = 0; s <= steps; s++)
                    {
                        float t = steps == 0 ? 0 : (float)s / steps;
                        int ix = (int)(_lastPixel.X + (cx - _lastPixel.X) * t);
                        int iy = (int)(_lastPixel.Y + (cy - _lastPixel.Y) * t);
                        PaintPixel(ix, iy, color, brushSize);
                    }
                }
                else
                {
                    PaintPixel(cx, cy, color, brushSize);
                }
                _lastPixel = new Vector2(cx, cy);
            }
        }

        if (leftReleased)
            _drawing = false;
    }

    private void PaintPixel(int x, int y, Color color, int size)
    {
        for (int dx = 0; dx < size; dx++)
            for (int dy = 0; dy < size; dy++)
            {
                int px = x + dx, py = y + dy;
                if (px >= 0 && px < CanvasW && py >= 0 && py < CanvasH)
                    _pixels[px, py] = color;
            }
    }

    private void FloodFill(int startX, int startY, Color newColor)
    {
        var targetColor = _pixels[startX, startY];
        if (ColorsEqual(targetColor, newColor)) return;

        var stack = new Stack<(int x, int y)>();
        stack.Push((startX, startY));

        while (stack.Count > 0 && stack.Count < 50000)
        {
            var (x, y) = stack.Pop();
            if (x < 0 || x >= CanvasW || y < 0 || y >= CanvasH) continue;
            if (!ColorsEqual(_pixels[x, y], targetColor)) continue;
            _pixels[x, y] = newColor;
            stack.Push((x + 1, y));
            stack.Push((x - 1, y));
            stack.Push((x, y + 1));
            stack.Push((x, y - 1));
        }
    }

    private static bool ColorsEqual(Color a, Color b)
        => a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;

    private void SaveUndo()
    {
        var copy = new Color[CanvasW, CanvasH];
        Array.Copy(_pixels, copy, _pixels.Length);
        _undoStack.Add(copy);
        if (_undoStack.Count > 10)
            _undoStack.RemoveAt(0);
    }

    public void Draw(Vector2 offset)
    {
        // Background
        Raylib.DrawRectangle((int)offset.X, (int)offset.Y, 800, 600,
            new Color((byte)200, (byte)200, (byte)200, (byte)255));

        // Canvas
        for (int x = 0; x < CanvasW; x++)
            for (int y = 0; y < CanvasH; y++)
                Raylib.DrawRectangle(
                    (int)offset.X + CanvasOffsetX + x * PixelScale,
                    (int)offset.Y + CanvasOffsetY + y * PixelScale,
                    PixelScale, PixelScale, _pixels[x, y]);

        // Canvas border
        Raylib.DrawRectangleLines(
            (int)offset.X + CanvasOffsetX - 1, (int)offset.Y + CanvasOffsetY - 1,
            CanvasW * PixelScale + 2, CanvasH * PixelScale + 2, Color.DarkGray);

        // Top bar
        Raylib.DrawRectangle((int)offset.X, (int)offset.Y, 800, 44,
            new Color((byte)50, (byte)50, (byte)55, (byte)255));
        Raylib.DrawText("Paint", (int)offset.X + 10, (int)offset.Y + 12, 20, Color.White);
        Raylib.DrawText("[ESC] Exit   [Ctrl+Z] Undo", (int)offset.X + 550, (int)offset.Y + 14, 14, Color.LightGray);

        // Tool buttons
        string[] toolNames = { "Pencil", "Brush", "Eraser", "Fill" };
        for (int i = 0; i < toolNames.Length; i++)
        {
            int bx = (int)offset.X + 10, by = (int)offset.Y + 60 + i * 35;
            bool selected = (int)_currentTool == i;
            Raylib.DrawRectangle(bx, by, 80, 30,
                selected ? new Color((byte)100, (byte)150, (byte)255, (byte)255) : new Color((byte)180, (byte)180, (byte)180, (byte)255));
            Raylib.DrawRectangleLines(bx, by, 80, 30, Color.DarkGray);
            Raylib.DrawText(toolNames[i], bx + 5, by + 7, 14,
                selected ? Color.White : Color.Black);
        }

        // Clear button
        int clx = (int)offset.X + 10, cly = (int)offset.Y + 210;
        Raylib.DrawRectangle(clx, cly, 80, 28, new Color((byte)220, (byte)100, (byte)100, (byte)255));
        Raylib.DrawText("Clear", clx + 15, cly + 6, 14, Color.White);

        // Key hints
        Raylib.DrawText("[1-4] Tools", (int)offset.X + 10, (int)offset.Y + 250, 12, Color.DarkGray);

        // Color palette
        for (int i = 0; i < Palette.Length; i++)
        {
            int px = (int)offset.X + 100 + (i % 12) * 24;
            int py = (int)offset.Y + 545 + (i / 12) * 24;
            Raylib.DrawRectangle(px, py, 22, 22, Palette[i]);
            Raylib.DrawRectangleLines(px, py, 22, 22, Color.DarkGray);
        }

        // FG/BG color preview
        Raylib.DrawRectangle((int)offset.X + 30, (int)offset.Y + 555, 30, 30, _bgColor);
        Raylib.DrawRectangle((int)offset.X + 20, (int)offset.Y + 545, 30, 30, _fgColor);
        Raylib.DrawRectangleLines((int)offset.X + 20, (int)offset.Y + 545, 30, 30, Color.Black);
        Raylib.DrawRectangleLines((int)offset.X + 30, (int)offset.Y + 555, 30, 30, Color.Black);
        Raylib.DrawText("FG", (int)offset.X + 25, (int)offset.Y + 530, 10, Color.DarkGray);
    }

    public void Close() => IsFinished = true;
}
