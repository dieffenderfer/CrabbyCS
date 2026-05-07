using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// In-app MS Paint clone. Replaces the old jspaint embed so saves go through
/// real OS file dialogs and the user always knows the resulting path.
///
/// Layout (panel-local):
///   [title bar][menu bar]
///   [toolbox | canvas    ]
///   [color palette        ]
///   [status bar           ]
/// </summary>
public class PaintActivity : IActivity
{
    public Vector2 PanelSize => new(900, 640);
    public bool IsFinished { get; private set; }

    private readonly AssetCache _assets;
    private readonly AudioManager _audio;

    // ── Layout constants ────────────────────────────────────────────────
    private const int FrameInset    = 3;
    private const int ToolboxW      = 56;
    private const int ToolOptionsH  = 70;
    private const int PaletteH      = 48;

    // ── Canvas (the actual bitmap users paint on) ───────────────────────
    private RenderTexture2D _canvas;
    private int _canvasW = 640;
    private int _canvasH = 400;
    private bool _canvasReady;

    // Where the canvas's top-left lives in panel-local coords. Updated each
    // frame in Draw, then re-used by Update.
    private Vector2 _canvasOriginCached;
    private int _viewScale = 1; // 1, 2, 4, 6, 8 (Magnifier)

    private string _docName = "untitled";
    private bool _dirty;
    private string _statusHint = "For Help, click Help Topics on the Help Menu.";
    private string _coordHint = "";

    // ── Tools ───────────────────────────────────────────────────────────
    public enum Tool
    {
        FreeFormSelect, Select,
        Eraser,         Fill,
        PickColor,      Magnifier,
        Pencil,         Brush,
        Airbrush,       Text,
        Line,           Curve,
        Rectangle,      Polygon,
        Ellipse,        RoundedRectangle,
    }

    // 8 rows × 2 cols, MS-Paint order.
    private static readonly Tool[] ToolGrid =
    {
        Tool.FreeFormSelect, Tool.Select,
        Tool.Eraser,         Tool.Fill,
        Tool.PickColor,      Tool.Magnifier,
        Tool.Pencil,         Tool.Brush,
        Tool.Airbrush,       Tool.Text,
        Tool.Line,           Tool.Curve,
        Tool.Rectangle,      Tool.Polygon,
        Tool.Ellipse,        Tool.RoundedRectangle,
    };

    private Tool _tool = Tool.Pencil;

    // ── Drawing state ───────────────────────────────────────────────────
    private Color _primary   = new(0, 0, 0, 255);
    private Color _secondary = new(255, 255, 255, 255);

    // Per-tool sizes the user has chosen. Brush stamp size, eraser size, etc.
    private int _brushSize    = 4;
    private int _eraserSize   = 8;
    private int _airbrushSize = 9;
    private int _lineThickness = 1;

    // Shape-fill style: 0 = outline only, 1 = outline + filled, 2 = filled only.
    private int _shapeStyle = 1;

    // Stroke tracking — true while a stroke is in progress.
    private Vector2 _lastDraw = new(-1, -1);
    private bool _drawingLeft;
    private bool _drawingRight;

    // Rubber-band shape tools (Line, Rectangle, Ellipse, RoundedRect): the
    // press point is fixed and we preview a shape from there to the cursor
    // until the button releases, at which point we commit.
    private bool _shapeInProgress;
    private Vector2 _shapeStart;
    private Vector2 _shapeEnd;
    private bool _shapeUsesPrimary; // true = left-drag (foreground), false = right-drag

    // ── Palette ─────────────────────────────────────────────────────────
    private static readonly Color[] DefaultPalette =
    {
        new(  0,   0,   0, 255), new(128, 128, 128, 255),
        new(128,   0,   0, 255), new(128, 128,   0, 255),
        new(  0, 128,   0, 255), new(  0, 128, 128, 255),
        new(  0,   0, 128, 255), new(128,   0, 128, 255),
        new(128, 128,  64, 255), new(  0,  64,  64, 255),
        new(  0, 128, 255, 255), new(  0,  64, 128, 255),
        new( 64,   0, 255, 255), new(128,  64,   0, 255),

        new(255, 255, 255, 255), new(192, 192, 192, 255),
        new(255,   0,   0, 255), new(255, 255,   0, 255),
        new(  0, 255,   0, 255), new(  0, 255, 255, 255),
        new(  0,   0, 255, 255), new(255,   0, 255, 255),
        new(255, 255, 128, 255), new(  0, 255, 128, 255),
        new(128, 255, 255, 255), new(128, 128, 255, 255),
        new(255,   0, 128, 255), new(255, 128,  64, 255),
    };

    private static readonly Random Rng = new();

    public PaintActivity(AssetCache assets, AudioManager audio)
    {
        _assets = assets;
        _audio = audio;
    }

    public void Load()
    {
        _canvas = Raylib.LoadRenderTexture(_canvasW, _canvasH);
        Raylib.BeginTextureMode(_canvas);
        Raylib.ClearBackground(Color.White);
        Raylib.EndTextureMode();
        _canvasReady = true;
    }

    public void Close()
    {
        if (_canvasReady)
        {
            Raylib.UnloadRenderTexture(_canvas);
            _canvasReady = false;
        }
    }

    // ── Frame entry ─────────────────────────────────────────────────────
    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;

        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        {
            IsFinished = true;
            return;
        }

        if (HandleToolboxInput(local, leftPressed)) return;
        if (HandleToolOptionsInput(local, leftPressed)) return;
        if (HandlePaletteInput(local, leftPressed, rightPressed)) return;

        HandleCanvasInput(local, leftPressed, leftReleased, rightPressed);
    }

    // ── Toolbox input/layout ────────────────────────────────────────────
    private const int ToolBtnW = 24;
    private const int ToolBtnH = 22;
    private const int ToolGridPadX = 3;
    private const int ToolGridPadY = 3;

    private Rectangle ToolboxRectLocal()
    {
        float bodyY = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight;
        float bodyH = PanelSize.Y - bodyY - RetroWidgets.StatusBarHeight - FrameInset;
        return new Rectangle(FrameInset, bodyY, ToolboxW, bodyH - PaletteH);
    }

    private Rectangle ToolBtnRectLocal(int index)
    {
        var t = ToolboxRectLocal();
        int row = index / 2, col = index % 2;
        int x = (int)t.X + ToolGridPadX + col * ToolBtnW;
        int y = (int)t.Y + ToolGridPadY + row * ToolBtnH;
        return new Rectangle(x, y, ToolBtnW, ToolBtnH);
    }

    private Rectangle ToolOptionsRectLocal()
    {
        var t = ToolboxRectLocal();
        // Options panel sits underneath the 8-row tool grid.
        int gridBottom = (int)t.Y + ToolGridPadY + 8 * ToolBtnH + 2;
        return new Rectangle(t.X + 2, gridBottom, t.Width - 4, ToolOptionsH);
    }

    private bool HandleToolboxInput(Vector2 local, bool leftPressed)
    {
        if (!leftPressed) return false;
        if (!RetroSkin.PointInRect(local, ToolboxRectLocal())) return false;

        for (int i = 0; i < ToolGrid.Length; i++)
        {
            if (RetroSkin.PointInRect(local, ToolBtnRectLocal(i)))
            {
                _tool = ToolGrid[i];
                _shapeInProgress = false; // cancel any rubber-band
                return true;
            }
        }
        return true; // consume clicks anywhere in the toolbox area
    }

    // ── Tool options input ──────────────────────────────────────────────
    private bool HandleToolOptionsInput(Vector2 local, bool leftPressed)
    {
        if (!leftPressed) return false;
        var r = ToolOptionsRectLocal();
        if (!RetroSkin.PointInRect(local, r)) return false;

        // Different tools surface different option chips. Each chip is a
        // 14×14 square laid out in a 4×3 grid inside the options panel.
        switch (_tool)
        {
            case Tool.Brush:
            case Tool.Eraser:
            case Tool.Airbrush:
            {
                int[] sizes = _tool == Tool.Airbrush
                    ? new[] { 9, 16, 24 }
                    : new[] { 1, 3, 5, 8, 12 };
                for (int i = 0; i < sizes.Length; i++)
                {
                    var chip = OptionChipRect(r, i);
                    if (RetroSkin.PointInRect(local, chip))
                    {
                        if (_tool == Tool.Brush)         _brushSize    = sizes[i];
                        else if (_tool == Tool.Eraser)   _eraserSize   = sizes[i];
                        else                             _airbrushSize = sizes[i];
                        return true;
                    }
                }
                return true;
            }
            case Tool.Line:
            case Tool.Curve:
            {
                int[] thicknesses = { 1, 2, 3, 4, 5 };
                for (int i = 0; i < thicknesses.Length; i++)
                {
                    var chip = OptionChipRect(r, i);
                    if (RetroSkin.PointInRect(local, chip)) { _lineThickness = thicknesses[i]; return true; }
                }
                return true;
            }
            case Tool.Rectangle:
            case Tool.Ellipse:
            case Tool.RoundedRectangle:
            case Tool.Polygon:
            {
                for (int i = 0; i < 3; i++)
                {
                    var chip = OptionChipRect(r, i);
                    if (RetroSkin.PointInRect(local, chip)) { _shapeStyle = i; return true; }
                }
                return true;
            }
            case Tool.Magnifier:
            {
                int[] zooms = { 1, 2, 4, 6, 8 };
                for (int i = 0; i < zooms.Length; i++)
                {
                    var chip = OptionChipRect(r, i);
                    if (RetroSkin.PointInRect(local, chip)) { _viewScale = zooms[i]; return true; }
                }
                return true;
            }
        }
        return true;
    }

    private static Rectangle OptionChipRect(Rectangle panel, int i)
    {
        int chipW = 14, chipH = 14, gap = 2;
        int cols = 4;
        int row = i / cols, col = i % cols;
        return new Rectangle(panel.X + 4 + col * (chipW + gap),
                             panel.Y + 4 + row * (chipH + gap),
                             chipW, chipH);
    }

    // ── Palette ─────────────────────────────────────────────────────────
    private const int PaletteSwatchSize = 16;
    private const int PaletteSwatchGap  = 2;
    private const int PaletteIndicatorW = 50;

    private Rectangle PaletteRectLocal()
    {
        float bodyY = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight;
        float bodyH = PanelSize.Y - bodyY - RetroWidgets.StatusBarHeight - FrameInset;
        return new Rectangle(FrameInset, bodyY + bodyH - PaletteH,
            PanelSize.X - 2 * FrameInset, PaletteH);
    }

    private Rectangle SwatchRectLocal(int index)
    {
        var p = PaletteRectLocal();
        int gridX = (int)p.X + PaletteIndicatorW + 6;
        int gridY = (int)p.Y + (PaletteH - (2 * PaletteSwatchSize + PaletteSwatchGap)) / 2;
        int col = index / 2;
        int row = index % 2;
        int x = gridX + col * (PaletteSwatchSize + PaletteSwatchGap);
        int y = gridY + row * (PaletteSwatchSize + PaletteSwatchGap);
        return new Rectangle(x, y, PaletteSwatchSize, PaletteSwatchSize);
    }

    private bool HandlePaletteInput(Vector2 local, bool leftPressed, bool rightPressed)
    {
        if (!(leftPressed || rightPressed)) return false;
        if (!RetroSkin.PointInRect(local, PaletteRectLocal())) return false;

        for (int i = 0; i < DefaultPalette.Length; i++)
        {
            if (RetroSkin.PointInRect(local, SwatchRectLocal(i)))
            {
                if (leftPressed)  _primary   = DefaultPalette[i];
                if (rightPressed) _secondary = DefaultPalette[i];
                return true;
            }
        }
        return true;
    }

    // ── Canvas input dispatch ───────────────────────────────────────────
    private void HandleCanvasInput(Vector2 local, bool leftPressed, bool leftReleased, bool rightPressed)
    {
        if (!_canvasReady) return;

        // Convert panel-local cursor to canvas pixel coords (account for view scale).
        Vector2 cp = (local - _canvasOriginCached) / _viewScale;
        bool overCanvas = cp.X >= 0 && cp.Y >= 0 && cp.X < _canvasW && cp.Y < _canvasH;

        _coordHint = overCanvas ? $"{(int)cp.X},{(int)cp.Y}" : "";

        bool leftDown      = Raylib.IsMouseButtonDown(MouseButton.Left);
        bool rightDown     = Raylib.IsMouseButtonDown(MouseButton.Right);
        bool rightReleased = Raylib.IsMouseButtonReleased(MouseButton.Right);

        // Tools that act on press only (one-shot)
        if (leftPressed && overCanvas)
        {
            switch (_tool)
            {
                case Tool.PickColor: _primary = SamplePixel((int)cp.X, (int)cp.Y); return;
                case Tool.Fill:      FloodFill((int)cp.X, (int)cp.Y, _primary); _dirty = true; return;
                case Tool.Magnifier: CycleZoom(); return;
            }
        }
        if (rightPressed && overCanvas)
        {
            switch (_tool)
            {
                case Tool.PickColor: _secondary = SamplePixel((int)cp.X, (int)cp.Y); return;
                case Tool.Fill:      FloodFill((int)cp.X, (int)cp.Y, _secondary); _dirty = true; return;
            }
        }

        // Stroke-based tools: Pencil, Brush, Eraser, Airbrush
        if (IsStrokeTool(_tool))
        {
            if (leftPressed && overCanvas)  { _drawingLeft = true;  _lastDraw = new(-1, -1); }
            if (rightPressed && overCanvas) { _drawingRight = true; _lastDraw = new(-1, -1); }

            if (_drawingLeft || _drawingRight)
            {
                if (overCanvas)
                {
                    Color c = _drawingLeft ? _primary : _secondary;
                    Color other = _drawingLeft ? _secondary : _primary;
                    StrokeAt(cp, c, other);
                    _lastDraw = cp;
                    _dirty = true;
                }
                else
                {
                    _lastDraw = new(-1, -1);
                }
            }

            if (leftReleased)  _drawingLeft = false;
            if (rightReleased) _drawingRight = false;
            return;
        }

        // Rubber-band shape tools: Line, Rectangle, Ellipse, RoundedRectangle
        if (IsShapeTool(_tool))
        {
            if ((leftPressed || rightPressed) && overCanvas)
            {
                _shapeInProgress = true;
                _shapeStart = cp;
                _shapeEnd = cp;
                _shapeUsesPrimary = leftPressed;
            }
            if (_shapeInProgress)
            {
                _shapeEnd = cp;
                if ((leftReleased && _shapeUsesPrimary) || (rightReleased && !_shapeUsesPrimary))
                {
                    CommitShape();
                    _shapeInProgress = false;
                    _dirty = true;
                }
            }
            return;
        }
    }

    private static bool IsStrokeTool(Tool t) =>
        t == Tool.Pencil || t == Tool.Brush || t == Tool.Eraser || t == Tool.Airbrush;

    private static bool IsShapeTool(Tool t) =>
        t == Tool.Line || t == Tool.Rectangle || t == Tool.Ellipse || t == Tool.RoundedRectangle;

    private void CycleZoom()
    {
        int[] zooms = { 1, 2, 4, 6, 8 };
        int idx = Array.IndexOf(zooms, _viewScale);
        _viewScale = zooms[(idx + 1) % zooms.Length];
    }

    // ── Stroke tools ────────────────────────────────────────────────────
    private void StrokeAt(Vector2 cp, Color color, Color otherColor)
    {
        Raylib.BeginTextureMode(_canvas);
        try
        {
            int x1 = (int)cp.X, y1 = (int)cp.Y;
            if (_lastDraw.X < 0) StampOne(x1, y1, color, otherColor);
            else
            {
                int x0 = (int)_lastDraw.X, y0 = (int)_lastDraw.Y;
                BresenhamLine(x0, y0, x1, y1, (px, py) => StampOne(px, py, color, otherColor));
            }
        }
        finally { Raylib.EndTextureMode(); }
    }

    private void StampOne(int x, int y, Color color, Color otherColor)
    {
        switch (_tool)
        {
            case Tool.Pencil:
                Raylib.DrawPixel(x, y, color);
                break;
            case Tool.Brush:
                Raylib.DrawCircle(x, y, _brushSize / 2f, color);
                break;
            case Tool.Eraser:
                // MS Paint's eraser paints with the *secondary* color regardless
                // of which mouse button is held. This makes "right-click eraser"
                // do color-replace later (color-eraser mode) — but for now we
                // always paint secondary, matching Paint's behavior.
                int s = _eraserSize;
                Raylib.DrawRectangle(x - s / 2, y - s / 2, s, s, _secondary);
                break;
            case Tool.Airbrush:
            {
                int radius = _airbrushSize;
                int dots = Math.Max(1, radius * 2);
                for (int i = 0; i < dots; i++)
                {
                    double angle = Rng.NextDouble() * Math.PI * 2;
                    double r = Math.Sqrt(Rng.NextDouble()) * radius;
                    int dx = (int)(Math.Cos(angle) * r);
                    int dy = (int)(Math.Sin(angle) * r);
                    Raylib.DrawPixel(x + dx, y + dy, color);
                }
                break;
            }
        }
    }

    private static void BresenhamLine(int x0, int y0, int x1, int y1, Action<int, int> plot)
    {
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            plot(x0, y0);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    // ── Shape commit (called on rubber-band release) ────────────────────
    private void CommitShape()
    {
        Color stroke = _shapeUsesPrimary ? _primary   : _secondary;
        Color fill   = _shapeUsesPrimary ? _secondary : _primary;
        int x0 = (int)_shapeStart.X, y0 = (int)_shapeStart.Y;
        int x1 = (int)_shapeEnd.X,   y1 = (int)_shapeEnd.Y;

        Raylib.BeginTextureMode(_canvas);
        try
        {
            switch (_tool)
            {
                case Tool.Line:
                    DrawThickLine(x0, y0, x1, y1, _lineThickness, stroke);
                    break;
                case Tool.Rectangle:
                    DrawShapeRect(x0, y0, x1, y1, stroke, fill);
                    break;
                case Tool.Ellipse:
                    DrawShapeEllipse(x0, y0, x1, y1, stroke, fill);
                    break;
                case Tool.RoundedRectangle:
                    DrawShapeRoundedRect(x0, y0, x1, y1, stroke, fill);
                    break;
            }
        }
        finally { Raylib.EndTextureMode(); }
    }

    private void DrawThickLine(int x0, int y0, int x1, int y1, int thickness, Color c)
    {
        if (thickness <= 1) BresenhamLine(x0, y0, x1, y1, (px, py) => Raylib.DrawPixel(px, py, c));
        else
            Raylib.DrawLineEx(new Vector2(x0, y0), new Vector2(x1, y1), thickness, c);
    }

    private static (int, int, int, int) NormRect(int x0, int y0, int x1, int y1)
        => (Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0) + 1, Math.Abs(y1 - y0) + 1);

    private void DrawShapeRect(int x0, int y0, int x1, int y1, Color stroke, Color fill)
    {
        var (x, y, w, h) = NormRect(x0, y0, x1, y1);
        if (_shapeStyle != 0) Raylib.DrawRectangle(x, y, w, h, fill);
        if (_shapeStyle != 2)
        {
            for (int t = 0; t < _lineThickness; t++)
                Raylib.DrawRectangleLines(x + t, y + t, w - 2 * t, h - 2 * t, stroke);
        }
    }

    private void DrawShapeRoundedRect(int x0, int y0, int x1, int y1, Color stroke, Color fill)
    {
        var (x, y, w, h) = NormRect(x0, y0, x1, y1);
        var rect = new Rectangle(x, y, w, h);
        float roundness = 0.25f;
        if (_shapeStyle != 0) Raylib.DrawRectangleRounded(rect, roundness, 8, fill);
        if (_shapeStyle != 2) Raylib.DrawRectangleRoundedLines(rect, roundness, 8, _lineThickness, stroke);
    }

    private void DrawShapeEllipse(int x0, int y0, int x1, int y1, Color stroke, Color fill)
    {
        var (x, y, w, h) = NormRect(x0, y0, x1, y1);
        int cx = x + w / 2, cy = y + h / 2;
        int rx = Math.Max(1, w / 2), ry = Math.Max(1, h / 2);
        if (_shapeStyle != 0) Raylib.DrawEllipse(cx, cy, rx, ry, fill);
        if (_shapeStyle != 2)
        {
            for (int t = 0; t < _lineThickness; t++)
                Raylib.DrawEllipseLines(cx, cy, rx - t, ry - t, stroke);
        }
    }

    // ── Pixel sampling + flood fill ─────────────────────────────────────
    private Color SamplePixel(int x, int y)
    {
        var img = Raylib.LoadImageFromTexture(_canvas.Texture);
        try
        {
            // RenderTexture is Y-flipped relative to images we read back.
            Raylib.ImageFlipVertical(ref img);
            return Raylib.GetImageColor(img, x, y);
        }
        finally { Raylib.UnloadImage(img); }
    }

    private void FloodFill(int sx, int sy, Color target)
    {
        var img = Raylib.LoadImageFromTexture(_canvas.Texture);
        try
        {
            Raylib.ImageFlipVertical(ref img);
            Color seed = Raylib.GetImageColor(img, sx, sy);
            if (ColorsEqual(seed, target)) return;

            // Scanline flood fill on the CPU side, then upload via ImageDrawPixel.
            var stack = new Stack<(int, int)>();
            stack.Push((sx, sy));
            while (stack.Count > 0)
            {
                var (x, y) = stack.Pop();
                if (x < 0 || x >= _canvasW || y < 0 || y >= _canvasH) continue;
                if (!ColorsEqual(Raylib.GetImageColor(img, x, y), seed)) continue;

                // Walk left/right to find span
                int lx = x;
                while (lx - 1 >= 0 && ColorsEqual(Raylib.GetImageColor(img, lx - 1, y), seed)) lx--;
                int rx = x;
                while (rx + 1 < _canvasW && ColorsEqual(Raylib.GetImageColor(img, rx + 1, y), seed)) rx++;

                for (int i = lx; i <= rx; i++)
                {
                    Raylib.ImageDrawPixel(ref img, i, y, target);
                    if (y - 1 >= 0 && ColorsEqual(Raylib.GetImageColor(img, i, y - 1), seed))
                        stack.Push((i, y - 1));
                    if (y + 1 < _canvasH && ColorsEqual(Raylib.GetImageColor(img, i, y + 1), seed))
                        stack.Push((i, y + 1));
                }
            }

            // Upload modified image back to the canvas RT.
            Raylib.ImageFlipVertical(ref img);
            var tex = Raylib.LoadTextureFromImage(img);
            Raylib.BeginTextureMode(_canvas);
            Raylib.ClearBackground(Color.Blank);
            Raylib.DrawTexture(tex, 0, 0, Color.White);
            Raylib.EndTextureMode();
            Raylib.UnloadTexture(tex);
        }
        finally { Raylib.UnloadImage(img); }
    }

    private static bool ColorsEqual(Color a, Color b)
        => a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;

    // ── Drawing ─────────────────────────────────────────────────────────
    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panel.X + FrameInset, panel.Y + FrameInset,
            panel.Width - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        string title = (_dirty ? "*" : "") + _docName + " - Paint";
        RetroWidgets.DrawTitleBarVisual(titleBar, title, true);

        var menuBar = new Rectangle(titleBar.X, titleBar.Y + titleBar.Height,
            titleBar.Width, RetroWidgets.MenuBarHeight);
        var menuItems = new[] { "File", "Edit", "View", "Image", "Colors", "Help" };
        RetroWidgets.MenuBarVisual(menuBar, menuItems, -1);

        // Body
        float bodyY = menuBar.Y + menuBar.Height;
        float bodyH = panel.Height - (bodyY - panel.Y) - RetroWidgets.StatusBarHeight - FrameInset;
        var bodyRect = new Rectangle(panel.X + FrameInset, bodyY,
            panel.Width - 2 * FrameInset, bodyH);

        // Toolbox
        var toolboxRect = new Rectangle(bodyRect.X, bodyRect.Y, ToolboxW, bodyRect.Height - PaletteH);
        RetroSkin.DrawRaised(toolboxRect);
        DrawToolGrid(panelOffset);
        DrawToolOptions(panelOffset);

        // Canvas
        var canvasArea = new Rectangle(
            toolboxRect.X + toolboxRect.Width + 2,
            bodyRect.Y,
            bodyRect.Width - toolboxRect.Width - 2,
            bodyRect.Height - PaletteH);
        RetroSkin.DrawSunken(canvasArea, RetroSkin.SunkenBg);

        if (_canvasReady)
        {
            int cx = (int)(canvasArea.X + 4);
            int cy = (int)(canvasArea.Y + 4);
            _canvasOriginCached = new Vector2(cx - panelOffset.X, cy - panelOffset.Y);

            int dispW = _canvasW * _viewScale;
            int dispH = _canvasH * _viewScale;
            var src = new Rectangle(0, 0, _canvasW, -_canvasH);
            var dst = new Rectangle(cx, cy, dispW, dispH);
            Raylib.DrawTexturePro(_canvas.Texture, src, dst, Vector2.Zero, 0f, Color.White);
            Raylib.DrawRectangleLines(cx - 1, cy - 1, dispW + 2, dispH + 2, RetroSkin.DarkShadow);

            // Rubber-band preview for shape tools
            if (_shapeInProgress) DrawShapePreview(cx, cy);
        }

        // Palette
        var paletteRect = new Rectangle(bodyRect.X, bodyRect.Y + bodyRect.Height - PaletteH,
            bodyRect.Width, PaletteH);
        RetroSkin.DrawRaised(paletteRect);
        DrawPalette(paletteRect, panelOffset);

        // Status bar
        var statusBar = new Rectangle(panel.X + FrameInset,
            panel.Y + panel.Height - RetroWidgets.StatusBarHeight - FrameInset,
            panel.Width - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(statusBar,
            _statusHint,
            _coordHint,
            $"{_canvasW} x {_canvasH}");
    }

    // ── Tool grid + tool icons ──────────────────────────────────────────
    private void DrawToolGrid(Vector2 panelOffset)
    {
        for (int i = 0; i < ToolGrid.Length; i++)
        {
            var local = ToolBtnRectLocal(i);
            var screen = new Rectangle(local.X + panelOffset.X, local.Y + panelOffset.Y,
                local.Width, local.Height);
            bool selected = ToolGrid[i] == _tool;
            if (selected) RetroSkin.DrawPressed(screen);
            else RetroSkin.DrawRaised(screen);
            DrawToolIcon(screen, ToolGrid[i], selected);
        }
    }

    private static void DrawToolIcon(Rectangle r, Tool tool, bool pressed)
    {
        int cx = (int)(r.X + r.Width / 2) + (pressed ? 1 : 0);
        int cy = (int)(r.Y + r.Height / 2) + (pressed ? 1 : 0);
        Color ink = RetroSkin.BodyText;

        switch (tool)
        {
            case Tool.FreeFormSelect:
                Raylib.DrawCircleLines(cx, cy, 6, ink);
                Raylib.DrawCircleLines(cx, cy, 4, ink);
                break;
            case Tool.Select:
                Raylib.DrawRectangleLines(cx - 7, cy - 5, 14, 10, ink);
                // dashed effect
                for (int x = cx - 7; x < cx + 7; x += 3) Raylib.DrawPixel(x, cy - 5, Color.White);
                break;
            case Tool.Eraser:
                Raylib.DrawRectangle(cx - 5, cy - 3, 10, 6, Color.White);
                Raylib.DrawRectangleLines(cx - 5, cy - 3, 10, 6, ink);
                break;
            case Tool.Fill:
                Raylib.DrawTriangle(new(cx - 5, cy + 2), new(cx + 5, cy + 2), new(cx, cy - 6), ink);
                Raylib.DrawCircle(cx + 5, cy + 5, 1.5f, new Color(0, 0, 255, 255));
                break;
            case Tool.PickColor:
                Raylib.DrawLine(cx - 5, cy + 5, cx + 3, cy - 3, ink);
                Raylib.DrawLine(cx - 5, cy + 6, cx + 3, cy - 2, ink);
                Raylib.DrawCircle(cx + 4, cy - 4, 2, new Color(255, 0, 0, 255));
                break;
            case Tool.Magnifier:
                Raylib.DrawCircleLines(cx - 1, cy - 1, 5, ink);
                Raylib.DrawLine(cx + 3, cy + 3, cx + 6, cy + 6, ink);
                Raylib.DrawLine(cx + 4, cy + 3, cx + 7, cy + 6, ink);
                break;
            case Tool.Pencil:
                Raylib.DrawLine(cx - 6, cy + 5, cx + 5, cy - 6, ink);
                Raylib.DrawLine(cx - 5, cy + 6, cx + 6, cy - 5, ink);
                Raylib.DrawCircle(cx - 6, cy + 6, 1, ink);
                break;
            case Tool.Brush:
                Raylib.DrawRectangle(cx - 1, cy - 6, 4, 6, ink);
                Raylib.DrawRectangle(cx - 2, cy, 5, 3, new Color(180, 100, 30, 255));
                Raylib.DrawCircle(cx - 4, cy + 4, 2, ink);
                break;
            case Tool.Airbrush:
                Raylib.DrawCircle(cx - 5, cy - 4, 1, ink);
                Raylib.DrawCircle(cx - 3, cy - 5, 1, ink);
                Raylib.DrawCircle(cx, cy - 3, 1, ink);
                Raylib.DrawCircle(cx + 2, cy - 1, 1, ink);
                for (int i = 0; i < 6; i++)
                {
                    int ax = cx + 1 + Rng.Next(-3, 3);
                    int ay = cy + 2 + Rng.Next(-3, 3);
                    Raylib.DrawPixel(ax, ay, ink);
                }
                break;
            case Tool.Text:
                RetroSkin.DrawText("A", cx - 4, cy - 7, ink, 14);
                break;
            case Tool.Line:
                Raylib.DrawLine(cx - 6, cy + 5, cx + 6, cy - 5, ink);
                break;
            case Tool.Curve:
                for (int x = -6; x <= 6; x++)
                {
                    int y = (int)(Math.Sin(x * 0.5) * 3);
                    Raylib.DrawPixel(cx + x, cy + y, ink);
                }
                break;
            case Tool.Rectangle:
                Raylib.DrawRectangleLines(cx - 6, cy - 4, 12, 8, ink);
                break;
            case Tool.Polygon:
                Raylib.DrawTriangleLines(new(cx, cy - 5), new(cx - 6, cy + 4), new(cx + 6, cy + 4), ink);
                break;
            case Tool.Ellipse:
                Raylib.DrawEllipseLines(cx, cy, 6, 4, ink);
                break;
            case Tool.RoundedRectangle:
                Raylib.DrawRectangleRoundedLines(new Rectangle(cx - 6, cy - 4, 12, 8), 0.5f, 6, 1f, ink);
                break;
        }
    }

    // ── Tool options ────────────────────────────────────────────────────
    private void DrawToolOptions(Vector2 panelOffset)
    {
        var rLocal = ToolOptionsRectLocal();
        var rScreen = new Rectangle(rLocal.X + panelOffset.X, rLocal.Y + panelOffset.Y,
            rLocal.Width, rLocal.Height);
        RetroSkin.DrawSunken(rScreen, RetroSkin.Face);

        int selected = -1;
        int count = 0;
        Action<Rectangle, int> drawChip = null!;

        switch (_tool)
        {
            case Tool.Brush:
            case Tool.Eraser:
            case Tool.Airbrush:
            {
                int[] sizes = _tool == Tool.Airbrush
                    ? new[] { 9, 16, 24 }
                    : new[] { 1, 3, 5, 8, 12 };
                count = sizes.Length;
                int cur = _tool == Tool.Brush ? _brushSize
                        : _tool == Tool.Eraser ? _eraserSize
                        : _airbrushSize;
                selected = Array.IndexOf(sizes, cur);
                drawChip = (chip, i) =>
                {
                    int dotR = Math.Max(1, sizes[i] / 4);
                    Raylib.DrawCircle((int)(chip.X + chip.Width / 2), (int)(chip.Y + chip.Height / 2),
                        dotR, RetroSkin.BodyText);
                };
                break;
            }
            case Tool.Line:
            case Tool.Curve:
            {
                int[] thick = { 1, 2, 3, 4, 5 };
                count = thick.Length;
                selected = Array.IndexOf(thick, _lineThickness);
                drawChip = (chip, i) =>
                {
                    int t = thick[i];
                    Raylib.DrawRectangle((int)chip.X + 2, (int)(chip.Y + chip.Height / 2 - t / 2f),
                        (int)chip.Width - 4, t, RetroSkin.BodyText);
                };
                break;
            }
            case Tool.Rectangle:
            case Tool.Ellipse:
            case Tool.RoundedRectangle:
            case Tool.Polygon:
            {
                count = 3;
                selected = _shapeStyle;
                drawChip = (chip, i) =>
                {
                    int x = (int)chip.X + 2, y = (int)chip.Y + 2,
                        w = (int)chip.Width - 4, h = (int)chip.Height - 4;
                    if (i == 0)
                    {
                        Raylib.DrawRectangleLines(x, y, w, h, RetroSkin.BodyText);
                    }
                    else if (i == 1)
                    {
                        Raylib.DrawRectangle(x, y, w, h, RetroSkin.BodyText);
                        Raylib.DrawRectangleLines(x, y, w, h, RetroSkin.DarkShadow);
                    }
                    else
                    {
                        Raylib.DrawRectangle(x, y, w, h, RetroSkin.BodyText);
                    }
                };
                break;
            }
            case Tool.Magnifier:
            {
                int[] zooms = { 1, 2, 4, 6, 8 };
                count = zooms.Length;
                selected = Array.IndexOf(zooms, _viewScale);
                drawChip = (chip, i) =>
                {
                    string label = zooms[i] + "x";
                    int tw = RetroSkin.MeasureText(label, 10);
                    RetroSkin.DrawText(label,
                        (int)(chip.X + (chip.Width - tw) / 2),
                        (int)(chip.Y + (chip.Height - 10) / 2),
                        RetroSkin.BodyText, 10);
                };
                break;
            }
        }

        for (int i = 0; i < count; i++)
        {
            var chipLocal = OptionChipRect(rLocal, i);
            var chipScreen = new Rectangle(chipLocal.X + panelOffset.X, chipLocal.Y + panelOffset.Y,
                chipLocal.Width, chipLocal.Height);
            if (i == selected) RetroSkin.DrawPressed(chipScreen);
            else RetroSkin.DrawRaised(chipScreen);
            drawChip?.Invoke(chipScreen, i);
        }
    }

    // ── Shape preview while rubber-banding ──────────────────────────────
    private void DrawShapePreview(int canvasX, int canvasY)
    {
        // Convert canvas-pixel start/end to screen pixels.
        int x0 = canvasX + (int)(_shapeStart.X * _viewScale);
        int y0 = canvasY + (int)(_shapeStart.Y * _viewScale);
        int x1 = canvasX + (int)(_shapeEnd.X   * _viewScale);
        int y1 = canvasY + (int)(_shapeEnd.Y   * _viewScale);
        Color stroke = _shapeUsesPrimary ? _primary : _secondary;

        switch (_tool)
        {
            case Tool.Line:
                Raylib.DrawLineEx(new Vector2(x0, y0), new Vector2(x1, y1),
                    Math.Max(1, _lineThickness * _viewScale), stroke);
                break;
            case Tool.Rectangle:
            {
                int x = Math.Min(x0, x1), y = Math.Min(y0, y1);
                int w = Math.Abs(x1 - x0) + _viewScale, h = Math.Abs(y1 - y0) + _viewScale;
                Raylib.DrawRectangleLines(x, y, w, h, stroke);
                break;
            }
            case Tool.Ellipse:
            {
                int x = Math.Min(x0, x1), y = Math.Min(y0, y1);
                int w = Math.Abs(x1 - x0), h = Math.Abs(y1 - y0);
                Raylib.DrawEllipseLines(x + w / 2, y + h / 2, Math.Max(1, w / 2), Math.Max(1, h / 2), stroke);
                break;
            }
            case Tool.RoundedRectangle:
            {
                int x = Math.Min(x0, x1), y = Math.Min(y0, y1);
                int w = Math.Abs(x1 - x0) + _viewScale, h = Math.Abs(y1 - y0) + _viewScale;
                Raylib.DrawRectangleRoundedLines(new Rectangle(x, y, w, h), 0.25f, 8, 1f, stroke);
                break;
            }
        }
    }

    // ── Palette drawing ─────────────────────────────────────────────────
    private void DrawPalette(Rectangle paletteRect, Vector2 panelOffset)
    {
        int ix = (int)paletteRect.X + 6;
        int iy = (int)(paletteRect.Y + (paletteRect.Height - 30) / 2);
        var secRect = new Rectangle(ix + 12, iy + 10, 22, 22);
        var priRect = new Rectangle(ix, iy, 22, 22);

        Raylib.DrawRectangleRec(secRect, _secondary);
        RetroSkin.DrawSunken(secRect, _secondary);
        Raylib.DrawRectangleRec(priRect, _primary);
        RetroSkin.DrawSunken(priRect, _primary);

        for (int i = 0; i < DefaultPalette.Length; i++)
        {
            var local = SwatchRectLocal(i);
            var screen = new Rectangle(local.X + panelOffset.X, local.Y + panelOffset.Y,
                local.Width, local.Height);
            RetroSkin.DrawSunken(screen, DefaultPalette[i]);
        }
    }
}
