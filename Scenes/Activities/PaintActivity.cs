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
    private const int FrameInset = 3;
    private const int ToolboxW   = 56;
    private const int PaletteH   = 48;

    // ── Canvas (the actual bitmap users paint on) ───────────────────────
    private RenderTexture2D _canvas;
    private int _canvasW = 640;
    private int _canvasH = 400;
    private bool _canvasReady;

    // Where the canvas's top-left lives in panel-local coords. Updated each
    // frame in Draw, then re-used by Update via _canvasOriginCached.
    private Vector2 _canvasOriginCached;

    // Title text (filename + dirty marker, MS-Paint style).
    private string _docName = "untitled";
    private bool _dirty;

    // ── Drawing state ───────────────────────────────────────────────────
    private Color _primary   = new(0, 0, 0, 255);    // left-button color
    private Color _secondary = new(255, 255, 255, 255); // right-button color
    private int _brushSize = 1;

    // MS Paint's classic 28-color palette (jspaint default_palette order: top
    // row of 14 darks, bottom row of 14 lights).
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

    // Stroke tracking — we interpolate between mouse samples so fast drags
    // don't leave gaps. -1,-1 means "no previous point".
    private Vector2 _lastDraw = new(-1, -1);
    private bool _drawingLeft;
    private bool _drawingRight;

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

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;

        // Title bar close button
        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        {
            IsFinished = true;
            return;
        }

        // Palette click handling. We do this before canvas input so a press
        // on a swatch doesn't begin a paint stroke.
        if (HandlePaletteInput(local, leftPressed, rightPressed)) return;

        HandleCanvasInput(local, leftPressed, leftReleased, rightPressed);
    }

    // ── Palette geometry & input ────────────────────────────────────────
    // The palette strip lives at the bottom of the body. The left ~46px is
    // the "current colors" indicator (overlapping primary/secondary swatches),
    // the rest is a 14×2 grid of color swatches.
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
        int col = index / 2;          // top row = even, bottom row = odd
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
        // Click was inside the palette strip but not on a swatch — still
        // consume so it doesn't fall through to canvas drawing.
        return true;
    }

    // ── Canvas painting ─────────────────────────────────────────────────
    private void HandleCanvasInput(Vector2 local, bool leftPressed, bool leftReleased, bool rightPressed)
    {
        if (!_canvasReady) return;

        // Position of mouse relative to the canvas's top-left, in canvas pixels.
        Vector2 cp = local - _canvasOriginCached;
        bool overCanvas = cp.X >= 0 && cp.Y >= 0 && cp.X < _canvasW && cp.Y < _canvasH;

        bool leftDown  = Raylib.IsMouseButtonDown(MouseButton.Left);
        bool rightDown = Raylib.IsMouseButtonDown(MouseButton.Right);
        bool rightReleased = Raylib.IsMouseButtonReleased(MouseButton.Right);

        // Begin a stroke only if the press happened over the canvas. Buttons
        // released anywhere finish the stroke, so off-canvas drags don't get
        // stuck in drawing mode.
        if (leftPressed && overCanvas)  { _drawingLeft  = true;  _lastDraw = new(-1, -1); }
        if (rightPressed && overCanvas) { _drawingRight = true;  _lastDraw = new(-1, -1); }

        if (_drawingLeft || _drawingRight)
        {
            if (overCanvas)
            {
                Color c = _drawingLeft ? _primary : _secondary;
                StrokePencil(cp, c);
                _lastDraw = cp;
                _dirty = true;
            }
            else
            {
                // Lift the pen when the cursor leaves the canvas — re-entering
                // starts a fresh segment instead of teleporting a long line.
                _lastDraw = new(-1, -1);
            }
        }

        if (leftReleased)  _drawingLeft  = false;
        if (rightReleased) _drawingRight = false;
    }

    // Pencil tool: 1px (or _brushSize) hard line from _lastDraw → cp using
    // Raylib's DrawLineEx inside a texture-mode pass. For 1px we draw
    // pixel-by-pixel via Bresenham so the result is genuinely 1px hard
    // (DrawLineEx with thickness=1 antialiases at corners).
    private void StrokePencil(Vector2 cp, Color color)
    {
        Raylib.BeginTextureMode(_canvas);
        try
        {
            int x1 = (int)cp.X, y1 = (int)cp.Y;
            if (_lastDraw.X < 0)
            {
                StampPencil(x1, y1, color);
            }
            else
            {
                int x0 = (int)_lastDraw.X, y0 = (int)_lastDraw.Y;
                BresenhamLine(x0, y0, x1, y1, (px, py) => StampPencil(px, py, color));
            }
        }
        finally
        {
            Raylib.EndTextureMode();
        }
    }

    private void StampPencil(int x, int y, Color color)
    {
        if (_brushSize <= 1)
        {
            Raylib.DrawPixel(x, y, color);
        }
        else
        {
            // MS-Paint's pencil is always 1px. This branch is here for when
            // Phase 4 wires up a pencil-size override or other size-using tool.
            int half = _brushSize / 2;
            Raylib.DrawRectangle(x - half, y - half, _brushSize, _brushSize, color);
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

    // ── Palette drawing ─────────────────────────────────────────────────
    private void DrawPalette(Rectangle paletteRect, Vector2 panelOffset)
    {
        // "Current colors" indicator — secondary peeks out from behind primary.
        int ix = (int)paletteRect.X + 6;
        int iy = (int)(paletteRect.Y + (paletteRect.Height - 30) / 2);
        var secRect = new Rectangle(ix + 12, iy + 10, 22, 22);
        var priRect = new Rectangle(ix, iy, 22, 22);

        Raylib.DrawRectangleRec(secRect, _secondary);
        RetroSkin.DrawSunken(secRect, _secondary);
        Raylib.DrawRectangleRec(priRect, _primary);
        RetroSkin.DrawSunken(priRect, _primary);

        // Swatch grid
        for (int i = 0; i < DefaultPalette.Length; i++)
        {
            var local = SwatchRectLocal(i);
            var screen = new Rectangle(local.X + panelOffset.X, local.Y + panelOffset.Y,
                local.Width, local.Height);
            RetroSkin.DrawSunken(screen, DefaultPalette[i]);
        }
    }

    // ── Drawing ─────────────────────────────────────────────────────────
    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        // Title bar
        var titleBar = new Rectangle(panel.X + FrameInset, panel.Y + FrameInset,
            panel.Width - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        string title = (_dirty ? "*" : "") + _docName + " - Paint";
        RetroWidgets.DrawTitleBarVisual(titleBar, title, true);

        // Menu bar (visual only this phase)
        var menuBar = new Rectangle(titleBar.X, titleBar.Y + titleBar.Height,
            titleBar.Width, RetroWidgets.MenuBarHeight);
        var menuItems = new[] { "File", "Edit", "View", "Image", "Colors", "Help" };
        RetroWidgets.MenuBarVisual(menuBar, menuItems, -1);

        // Body bounds
        float bodyY = menuBar.Y + menuBar.Height;
        float bodyH = panel.Height - (bodyY - panel.Y) - RetroWidgets.StatusBarHeight - FrameInset;
        var bodyRect = new Rectangle(panel.X + FrameInset, bodyY,
            panel.Width - 2 * FrameInset, bodyH);

        // Toolbox
        var toolboxRect = new Rectangle(bodyRect.X, bodyRect.Y, ToolboxW, bodyRect.Height - PaletteH);
        RetroSkin.DrawRaised(toolboxRect);

        // Canvas surround (sunken inset)
        var canvasArea = new Rectangle(
            toolboxRect.X + toolboxRect.Width + 2,
            bodyRect.Y,
            bodyRect.Width - toolboxRect.Width - 2,
            bodyRect.Height - PaletteH);
        RetroSkin.DrawSunken(canvasArea, RetroSkin.SunkenBg);

        // Canvas itself
        if (_canvasReady)
        {
            int cx = (int)(canvasArea.X + 4);
            int cy = (int)(canvasArea.Y + 4);
            // Cache canvas origin in panel-local coords for the next Update.
            _canvasOriginCached = new Vector2(cx - panelOffset.X, cy - panelOffset.Y);

            var src = new Rectangle(0, 0, _canvasW, -_canvasH);
            var dst = new Rectangle(cx, cy, _canvasW, _canvasH);
            Raylib.DrawTexturePro(_canvas.Texture, src, dst, Vector2.Zero, 0f, Color.White);
            Raylib.DrawRectangleLines(cx - 1, cy - 1, _canvasW + 2, _canvasH + 2, RetroSkin.DarkShadow);
        }

        // Color palette strip
        var paletteRect = new Rectangle(bodyRect.X, bodyRect.Y + bodyRect.Height - PaletteH,
            bodyRect.Width, PaletteH);
        RetroSkin.DrawRaised(paletteRect);
        DrawPalette(paletteRect, panelOffset);

        // Status bar
        var statusBar = new Rectangle(panel.X + FrameInset,
            panel.Y + panel.Height - RetroWidgets.StatusBarHeight - FrameInset,
            panel.Width - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(statusBar,
            "For Help, click Help Topics on the Help Menu.",
            $"{_canvasW} x {_canvasH}");
    }
}
