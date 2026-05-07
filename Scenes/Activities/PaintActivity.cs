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
    // PanelSize scales with the screen so Paint actually fills enough of the
    // user's monitor to be useful. Cached on first read so it's stable for
    // the lifetime of the activity (DesktopPetScene reads it many times per
    // frame for input/draw).
    public Vector2 PanelSize => _panelSizeCached ??= ComputePanelSize();
    private Vector2? _panelSizeCached;

    private static Vector2 ComputePanelSize()
    {
        int sw = Raylib.GetScreenWidth();
        int sh = Raylib.GetScreenHeight();
        // Take ~90% of the screen, but clamp so the chrome/text doesn't look
        // ridiculous on huge monitors and the layout still works on small ones.
        int w = Math.Clamp((int)(sw * 0.9f), 900, 1800);
        int h = Math.Clamp((int)(sh * 0.9f), 640, 1100);
        return new Vector2(w, h);
    }

    public bool IsFinished { get; private set; }

    private readonly AssetCache _assets;
    private readonly AudioManager _audio;

    // ── Layout constants ────────────────────────────────────────────────
    private const int FrameInset    = 3;
    private const int ToolboxW      = 56;
    private const int ToolOptionsH  = 70;
    private const int PaletteH      = 48;

    // ── Canvas (the actual bitmap users paint on) ───────────────────────
    // Default canvas is sized to fill most of the available drawing area in
    // the (now larger) panel. Users can still resize via Image → Attributes.
    private RenderTexture2D _canvas;
    private int _canvasW = 1024;
    private int _canvasH = 640;
    private bool _canvasReady;

    // Where the canvas's top-left lives in panel-local coords. Updated each
    // frame in Draw, then re-used by Update.
    private Vector2 _canvasOriginCached;
    private int _viewScale = 1; // 1, 2, 4, 6, 8 (Magnifier)

    private string _docName = "untitled";
    private bool _dirty;
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

    // ── Selection (Rectangular & Free-Form) ─────────────────────────────
    // Lifecycle:
    //   1) User drags Select/FreeFormSelect → marquee active, _selecting=true.
    //   2) On release with non-trivial area → marquee fixed; _hasSelection=true.
    //      The selected region remains visually part of the canvas (NOT lifted).
    //   3) If user clicks inside the marquee with the selection tool → start
    //      dragging. On first drag, "lift": copy selected region into
    //      _floatingSel and clear the original area to secondary color.
    //   4) Cursor outside marquee click, tool change, or commit shortcut →
    //      commit the floating tex back to canvas and clear selection.
    //   5) Ctrl+X cut / Ctrl+C copy / Ctrl+V paste / Del delete.
    private bool _selecting;     // mouse-drag in progress, before fix
    private bool _hasSelection;  // marquee placed
    private bool _selFreeForm;   // which tool drew it
    private Vector2 _selStart;   // canvas-pixel anchor of in-progress marquee
    private Rectangle _selRect;  // canvas-pixel selection bounds (normalized)
    private List<Vector2> _freeFormPath = new();
    private Image _freeFormMask;        // 0/255 alpha mask, exact size of _selRect; white inside the lasso
    private bool _freeFormMaskReady;

    private bool _selLifted;            // selection contents have been removed from canvas
    private Texture2D _floatingSel;     // contents while floating
    private bool _floatingSelReady;
    private Vector2 _floatingPos;       // canvas-pixel pos of floating top-left
    private bool _draggingSelection;
    private Vector2 _dragGrabOffset;    // mouse - floatingPos at drag start

    // Clipboard (separate from in-progress floating selection).
    private Image _clipboardImg;
    private bool _clipboardReady;

    // Marching ants animation
    private float _antsTime;

    // ── Menu bar / dropdown state ───────────────────────────────────────
    private record MenuEntry(string Label, Action? Action, bool Separator = false, bool Disabled = false);

    // ── Undo / Redo (snapshot-based) ────────────────────────────────────
    // Each entry is the full canvas pixel buffer + dims. Bounded to keep
    // memory predictable on large canvases.
    private const int MaxHistory = 20;
    private record Snapshot(Image Image, int W, int H);
    private Stack<Snapshot> _undoStack = new();
    private Stack<Snapshot> _redoStack = new();

    private static readonly string[] MenuBarLabels = { "File", "Edit", "View", "Image", "Colors", "Help" };
    private int _openMenu = -1;
    private List<MenuEntry> _openMenuEntries = new();
    private Rectangle _openMenuRect;
    private string _toast = "";
    private float _toastTimer;

    // ── Text tool ───────────────────────────────────────────────────────
    private bool _textActive;            // a text box is currently open
    private Rectangle _textRect;         // canvas-pixel bounds (rubber-band defined)
    private string _textBuffer = "";
    private int _textFontSize = 16;
    private float _textCursorBlink;

    // ── Curve tool (MS-Paint 2-stage) ───────────────────────────────────
    private int _curveStage;             // 0 idle, 1 line drawn awaiting bend1, 2 awaiting bend2
    private Vector2 _curveA, _curveB, _curveC1, _curveC2;
    private Color _curveColor;

    // ── Polygon tool ────────────────────────────────────────────────────
    private List<Vector2> _polyPts = new();
    private float _lastClickTime = -1f;
    private Vector2 _lastClickPos;
    private const float DoubleClickTime = 0.4f;
    private const float DoubleClickDist = 6f;
    private float _now;

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
        // Pick a default canvas size that fills most of the available drawing
        // area in the panel. Users can still resize via Image → Attributes.
        var panel = PanelSize;
        float bodyY = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight;
        float bodyH = panel.Y - bodyY - RetroWidgets.StatusBarHeight - FrameInset;
        int availW = (int)(panel.X - 2 * FrameInset - ToolboxW - 2 - 8);
        int availH = (int)(bodyH - PaletteH - 8);
        _canvasW = Math.Max(320, availW);
        _canvasH = Math.Max(200, availH);

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
        if (_floatingSelReady)  { Raylib.UnloadTexture(_floatingSel);  _floatingSelReady = false; }
        if (_freeFormMaskReady) { Raylib.UnloadImage(_freeFormMask);   _freeFormMaskReady = false; }
        if (_clipboardReady)    { Raylib.UnloadImage(_clipboardImg);   _clipboardReady = false; }
        ClearHistory();
    }

    private void ClearHistory()
    {
        while (_undoStack.Count > 0) Raylib.UnloadImage(_undoStack.Pop().Image);
        while (_redoStack.Count > 0) Raylib.UnloadImage(_redoStack.Pop().Image);
    }

    // ── Frame entry ─────────────────────────────────────────────────────
    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        _antsTime += delta;
        _textCursorBlink += delta;
        _now += delta;
        var local = mousePos - panelOffset;

        // Active text box swallows keystrokes regardless of where the mouse is.
        if (_textActive) HandleTextEntryKeys();
        if (_toastTimer > 0) _toastTimer -= delta;

        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        {
            IsFinished = true;
            return;
        }

        // Keyboard shortcuts that affect selection regardless of tool.
        HandleSelectionShortcuts();

        // Menu bar takes priority over everything else.
        if (HandleMenuBarInput(local, leftPressed)) return;

        if (HandleToolboxInput(local, leftPressed)) return;
        if (HandleToolOptionsInput(local, leftPressed)) return;
        if (HandlePaletteInput(local, leftPressed, rightPressed)) return;

        HandleCanvasInput(local, leftPressed, leftReleased, rightPressed);
    }

    private string ToolHint() => _tool switch
    {
        Tool.Pencil           => "Draws a free-form line one pixel wide.",
        Tool.Brush            => "Draws using a brush of the selected size.",
        Tool.Eraser           => "Erases part of the picture, using the selected eraser size.",
        Tool.Fill             => "Fills an area with the current foreground color.",
        Tool.Airbrush         => "Draws using an airbrush of the selected size.",
        Tool.Line             => "Draws a straight line.",
        Tool.Curve            => "Draws a curved line. Click to bend.",
        Tool.Rectangle        => "Draws a rectangle with the selected fill style.",
        Tool.Ellipse          => "Draws an ellipse with the selected fill style.",
        Tool.RoundedRectangle => "Draws a rounded rectangle with the selected fill style.",
        Tool.Polygon          => "Click to add vertices. Double-click to finish.",
        Tool.Text             => "Drag a rectangle, then type. Click outside to commit.",
        Tool.Select           => "Selects a rectangular part of the picture to move, copy, or edit.",
        Tool.FreeFormSelect   => "Selects a free-form part of the picture to move, copy, or edit.",
        Tool.PickColor        => "Picks up a color from the picture for drawing.",
        Tool.Magnifier        => "Click to cycle zoom (1x → 2x → 4x → 6x → 8x).",
        _                     => "For Help, click Help Topics on the Help Menu.",
    };

    private bool CtrlDown() => Raylib.IsKeyDown(KeyboardKey.LeftControl)
                            || Raylib.IsKeyDown(KeyboardKey.RightControl)
                            || Raylib.IsKeyDown(KeyboardKey.LeftSuper)
                            || Raylib.IsKeyDown(KeyboardKey.RightSuper);

    private void HandleSelectionShortcuts()
    {
        if (CtrlDown())
        {
            // Don't intercept text-entry keystrokes when the text tool is live.
            if (_textActive) return;

            if (Raylib.IsKeyPressed(KeyboardKey.X)) CutSelection();
            else if (Raylib.IsKeyPressed(KeyboardKey.C)) CopySelection();
            else if (Raylib.IsKeyPressed(KeyboardKey.V)) PasteClipboard();
            else if (Raylib.IsKeyPressed(KeyboardKey.A)) SelectAll();
            else if (Raylib.IsKeyPressed(KeyboardKey.S)) CmdSave();
            else if (Raylib.IsKeyPressed(KeyboardKey.O)) CmdOpen();
            else if (Raylib.IsKeyPressed(KeyboardKey.N)) CmdNew();
            else if (Raylib.IsKeyPressed(KeyboardKey.Z)) CmdUndo();
            else if (Raylib.IsKeyPressed(KeyboardKey.Y)) CmdRedo();
        }
        if (!_textActive && (Raylib.IsKeyPressed(KeyboardKey.Delete) || Raylib.IsKeyPressed(KeyboardKey.Backspace)))
            DeleteSelection();
        if (Raylib.IsKeyPressed(KeyboardKey.Escape) && (_hasSelection || _selecting))
            CancelSelection();
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
                var newTool = ToolGrid[i];
                // Switching to a non-selection tool commits any floating selection
                // back to the canvas — matches MS Paint behavior.
                if (_hasSelection && !IsSelectionTool(newTool))
                    CommitSelection();
                if (_textActive)            CommitText();
                if (_curveStage > 0)        CommitCurve(); // commit whatever's drawn
                if (_polyPts.Count > 0)     CommitPolygon();
                _tool = newTool;
                _shapeInProgress = false;
                return true;
            }
        }
        return true; // consume clicks anywhere in the toolbox area
    }

    private static bool IsSelectionTool(Tool t) => t == Tool.Select || t == Tool.FreeFormSelect;

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

        // Selection tool dispatch — handled before everything else because
        // it overrides the meaning of left-press inside the canvas.
        if (IsSelectionTool(_tool))
        {
            HandleSelectionInput(cp, overCanvas, leftPressed, leftReleased);
            return;
        }

        if (_tool == Tool.Text)
        {
            HandleTextInput(cp, overCanvas, leftPressed, leftReleased);
            return;
        }
        if (_tool == Tool.Curve)
        {
            HandleCurveInput(cp, overCanvas, leftPressed, leftReleased);
            return;
        }
        if (_tool == Tool.Polygon)
        {
            HandlePolygonInput(cp, overCanvas, leftPressed, leftReleased);
            return;
        }

        // Tools that act on press only (one-shot)
        if (leftPressed && overCanvas)
        {
            switch (_tool)
            {
                case Tool.PickColor: _primary = SamplePixel((int)cp.X, (int)cp.Y); return;
                case Tool.Fill:      PushUndo(); FloodFill((int)cp.X, (int)cp.Y, _primary); _dirty = true; return;
                case Tool.Magnifier: CycleZoom(); return;
            }
        }
        if (rightPressed && overCanvas)
        {
            switch (_tool)
            {
                case Tool.PickColor: _secondary = SamplePixel((int)cp.X, (int)cp.Y); return;
                case Tool.Fill:      PushUndo(); FloodFill((int)cp.X, (int)cp.Y, _secondary); _dirty = true; return;
            }
        }

        // Stroke-based tools: Pencil, Brush, Eraser, Airbrush
        if (IsStrokeTool(_tool))
        {
            if (leftPressed && overCanvas)  { PushUndo(); _drawingLeft = true;  _lastDraw = new(-1, -1); }
            if (rightPressed && overCanvas) { PushUndo(); _drawingRight = true; _lastDraw = new(-1, -1); }

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
                PushUndo();
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

    // ── Selection input ─────────────────────────────────────────────────
    private void HandleSelectionInput(Vector2 cp, bool overCanvas,
                                      bool leftPressed, bool leftReleased)
    {
        // 1) If a selection exists and the user clicks inside it, start a drag.
        if (_hasSelection && leftPressed && overCanvas)
        {
            var floatRect = CurrentFloatingRect();
            if (RectContainsPoint(floatRect, cp))
            {
                if (!_selLifted) LiftSelection(); // first drag lifts
                _draggingSelection = true;
                _dragGrabOffset = cp - _floatingPos;
                return;
            }
            else
            {
                // Click outside selection commits & deselects.
                CommitSelection();
                // Fall through so this same press can begin a new marquee.
            }
        }

        // 2) Continue an in-progress selection drag
        if (_draggingSelection)
        {
            _floatingPos = cp - _dragGrabOffset;
            // Sync _selRect so the marquee follows the floating contents.
            _selRect = new Rectangle(_floatingPos.X, _floatingPos.Y, _selRect.Width, _selRect.Height);
            if (leftReleased) _draggingSelection = false;
            return;
        }

        // 3) Start / continue a new marquee
        if (leftPressed && overCanvas && !_hasSelection)
        {
            _selecting = true;
            _selStart = cp;
            _selRect = new Rectangle(cp.X, cp.Y, 0, 0);
            _selFreeForm = _tool == Tool.FreeFormSelect;
            if (_selFreeForm) { _freeFormPath.Clear(); _freeFormPath.Add(cp); }
        }

        if (_selecting)
        {
            // Clamp cursor to canvas
            float ex = Math.Clamp(cp.X, 0, _canvasW - 1);
            float ey = Math.Clamp(cp.Y, 0, _canvasH - 1);
            if (_selFreeForm)
            {
                if (_freeFormPath.Count == 0 ||
                    Vector2.Distance(_freeFormPath[^1], new Vector2(ex, ey)) > 1.5f)
                    _freeFormPath.Add(new Vector2(ex, ey));
                // Bounding box
                float minX = ex, minY = ey, maxX = ex, maxY = ey;
                foreach (var p in _freeFormPath)
                {
                    minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
                }
                _selRect = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
            }
            else
            {
                float x0 = Math.Min(_selStart.X, ex), y0 = Math.Min(_selStart.Y, ey);
                float w  = Math.Abs(ex - _selStart.X) + 1, h = Math.Abs(ey - _selStart.Y) + 1;
                _selRect = new Rectangle(x0, y0, w, h);
            }

            if (leftReleased)
            {
                _selecting = false;
                if (_selRect.Width >= 2 && _selRect.Height >= 2)
                {
                    _hasSelection = true;
                    _selLifted = false;
                    _floatingPos = new Vector2(_selRect.X, _selRect.Y);
                    if (_selFreeForm) BuildFreeFormMask();
                }
                else
                {
                    _hasSelection = false;
                }
            }
        }
    }

    private static bool RectContainsPoint(Rectangle r, Vector2 p)
        => p.X >= r.X && p.Y >= r.Y && p.X < r.X + r.Width && p.Y < r.Y + r.Height;

    private Rectangle CurrentFloatingRect() =>
        new(_floatingPos.X, _floatingPos.Y, _selRect.Width, _selRect.Height);

    // Build a binary mask Image the size of the selection bbox: white inside
    // the lasso polygon, transparent outside. Used to mask the lifted region.
    private void BuildFreeFormMask()
    {
        if (_freeFormMaskReady) { Raylib.UnloadImage(_freeFormMask); _freeFormMaskReady = false; }
        int w = (int)_selRect.Width, h = (int)_selRect.Height;
        if (w <= 0 || h <= 0) return;

        // Translate path to mask-local
        var pts = _freeFormPath
            .Select(p => new Vector2(p.X - _selRect.X, p.Y - _selRect.Y))
            .ToArray();

        _freeFormMask = Raylib.GenImageColor(w, h, new Color(0, 0, 0, 0));
        Color fg = new(255, 255, 255, 255);

        for (int y = 0; y < h; y++)
        {
            // Even-odd polygon fill scanline
            var crossings = new List<float>();
            for (int i = 0; i < pts.Length; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Length];
                if ((a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y))
                {
                    float t = (y - a.Y) / (b.Y - a.Y);
                    crossings.Add(a.X + t * (b.X - a.X));
                }
            }
            crossings.Sort();
            for (int i = 0; i + 1 < crossings.Count; i += 2)
            {
                int x0 = (int)Math.Max(0, Math.Floor(crossings[i]));
                int x1 = (int)Math.Min(w - 1, Math.Ceiling(crossings[i + 1]));
                for (int x = x0; x <= x1; x++)
                    Raylib.ImageDrawPixel(ref _freeFormMask, x, y, fg);
            }
        }
        _freeFormMaskReady = true;
    }

    // Move the selected region from canvas to a floating Texture2D, and clear
    // the original area (free-form: clear masked pixels only).
    private void LiftSelection()
    {
        if (!_hasSelection || _selLifted) return;
        PushUndo();
        var img = Raylib.LoadImageFromTexture(_canvas.Texture);
        Raylib.ImageFlipVertical(ref img);
        try
        {
            int sx = (int)_selRect.X, sy = (int)_selRect.Y;
            int sw = (int)_selRect.Width, sh = (int)_selRect.Height;
            sx = Math.Max(0, sx); sy = Math.Max(0, sy);
            sw = Math.Min(_canvasW - sx, sw);
            sh = Math.Min(_canvasH - sy, sh);
            if (sw <= 0 || sh <= 0) return;

            var sub = Raylib.ImageFromImage(img, new Rectangle(sx, sy, sw, sh));
            if (_selFreeForm && _freeFormMaskReady)
            {
                ApplyMask(ref sub, _freeFormMask);
            }

            if (_floatingSelReady) Raylib.UnloadTexture(_floatingSel);
            _floatingSel = Raylib.LoadTextureFromImage(sub);
            _floatingSelReady = true;
            Raylib.UnloadImage(sub);

            // Clear original area on canvas to secondary color
            Raylib.BeginTextureMode(_canvas);
            try
            {
                if (_selFreeForm && _freeFormMaskReady)
                {
                    // Clear by drawing a colored quad masked by lasso shape: easiest
                    // is per-pixel via DrawPixel.
                    for (int y = 0; y < sh; y++)
                        for (int x = 0; x < sw; x++)
                        {
                            var m = Raylib.GetImageColor(_freeFormMask, x, y);
                            if (m.A > 0) Raylib.DrawPixel(sx + x, sy + y, _secondary);
                        }
                }
                else
                {
                    Raylib.DrawRectangle(sx, sy, sw, sh, _secondary);
                }
            }
            finally { Raylib.EndTextureMode(); }

            _selLifted = true;
            _dirty = true;
        }
        finally { Raylib.UnloadImage(img); }
    }

    private static void ApplyMask(ref Image src, Image mask)
    {
        int w = src.Width, h = src.Height;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var m = Raylib.GetImageColor(mask, x, y);
                if (m.A == 0)
                    Raylib.ImageDrawPixel(ref src, x, y, new Color(0, 0, 0, 0));
            }
    }

    // Stamp the floating selection back onto the canvas at its current pos.
    private void CommitSelection()
    {
        if (!_hasSelection) { CancelSelection(); return; }
        if (_selLifted && _floatingSelReady)
        {
            Raylib.BeginTextureMode(_canvas);
            try
            {
                Raylib.DrawTexture(_floatingSel, (int)_floatingPos.X, (int)_floatingPos.Y, Color.White);
            }
            finally { Raylib.EndTextureMode(); }
            _dirty = true;
        }
        CancelSelection();
    }

    private void CancelSelection()
    {
        _hasSelection = false;
        _selecting = false;
        _selLifted = false;
        _draggingSelection = false;
        if (_floatingSelReady) { Raylib.UnloadTexture(_floatingSel); _floatingSelReady = false; }
        if (_freeFormMaskReady) { Raylib.UnloadImage(_freeFormMask); _freeFormMaskReady = false; }
        _freeFormPath.Clear();
    }

    // ── Selection commands ──────────────────────────────────────────────
    private void SelectAll()
    {
        if (_hasSelection) CommitSelection();
        _tool = Tool.Select;
        _selFreeForm = false;
        _selRect = new Rectangle(0, 0, _canvasW, _canvasH);
        _floatingPos = Vector2.Zero;
        _hasSelection = true;
        _selLifted = false;
    }

    private void CopySelection()
    {
        if (!_hasSelection) return;
        if (_clipboardReady) Raylib.UnloadImage(_clipboardImg);

        if (_selLifted && _floatingSelReady)
        {
            _clipboardImg = Raylib.LoadImageFromTexture(_floatingSel);
        }
        else
        {
            // Selection isn't lifted — pull region from canvas.
            var canvasImg = Raylib.LoadImageFromTexture(_canvas.Texture);
            Raylib.ImageFlipVertical(ref canvasImg);
            int sx = Math.Max(0, (int)_selRect.X), sy = Math.Max(0, (int)_selRect.Y);
            int sw = Math.Min(_canvasW - sx, (int)_selRect.Width);
            int sh = Math.Min(_canvasH - sy, (int)_selRect.Height);
            _clipboardImg = Raylib.ImageFromImage(canvasImg, new Rectangle(sx, sy, sw, sh));
            if (_selFreeForm && _freeFormMaskReady) ApplyMask(ref _clipboardImg, _freeFormMask);
            Raylib.UnloadImage(canvasImg);
        }
        _clipboardReady = true;
    }

    private void CutSelection()
    {
        if (!_hasSelection) return;
        CopySelection();
        DeleteSelection();
    }

    private void DeleteSelection()
    {
        if (!_hasSelection) return;
        if (!_selLifted) LiftSelection();
        // After lifting, the canvas region is already cleared. Drop the
        // floating contents instead of committing.
        CancelSelection();
    }

    private void PasteClipboard()
    {
        if (!_clipboardReady) return;
        if (_hasSelection) CommitSelection();

        var tex = Raylib.LoadTextureFromImage(_clipboardImg);
        if (_floatingSelReady) Raylib.UnloadTexture(_floatingSel);
        _floatingSel = tex;
        _floatingSelReady = true;

        _selRect = new Rectangle(0, 0, _clipboardImg.Width, _clipboardImg.Height);
        _floatingPos = Vector2.Zero;
        _hasSelection = true;
        _selLifted = true;
        _selFreeForm = false; // paste is always rectangular
        _tool = Tool.Select;
    }

    // ── Menu bar dropdown ───────────────────────────────────────────────
    private Rectangle MenuBarRectLocal()
    {
        return new Rectangle(FrameInset, FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
    }

    private bool HandleMenuBarInput(Vector2 local, bool leftPressed)
    {
        // Click in menu bar: open / switch / close menu.
        var bar = MenuBarRectLocal();
        if (leftPressed && RetroSkin.PointInRect(local, bar))
        {
            int idx = MenuBarHitIndex(bar, local);
            if (idx >= 0)
            {
                if (_openMenu == idx) CloseMenu();
                else OpenMenu(idx);
                return true;
            }
        }

        // Hover over menu bar with one open: switch dropdowns.
        if (_openMenu >= 0 && RetroSkin.PointInRect(local, bar))
        {
            int idx = MenuBarHitIndex(bar, local);
            if (idx >= 0 && idx != _openMenu) OpenMenu(idx);
        }

        // Click on a dropdown item.
        if (_openMenu >= 0)
        {
            if (leftPressed)
            {
                int hovered = DropdownHitIndex(local);
                if (hovered >= 0 && hovered < _openMenuEntries.Count)
                {
                    var e = _openMenuEntries[hovered];
                    if (!e.Separator && !e.Disabled)
                    {
                        var act = e.Action;
                        CloseMenu();
                        act?.Invoke();
                        return true;
                    }
                }
                else if (!RetroSkin.PointInRect(local, _openMenuRect)
                      && !RetroSkin.PointInRect(local, MenuBarRectLocal()))
                {
                    CloseMenu();
                }
                return true; // swallow clicks while menu is open
            }
            // Mouse moves while open are also swallowed for canvas tools.
            return RetroSkin.PointInRect(local, _openMenuRect);
        }
        return false;
    }

    private static int MenuBarHitIndex(Rectangle bar, Vector2 local)
    {
        int x = (int)bar.X + 4;
        for (int i = 0; i < MenuBarLabels.Length; i++)
        {
            int w = RetroSkin.MeasureText(MenuBarLabels[i]) + 12;
            var slot = new Rectangle(x, bar.Y + 2, w, bar.Height - 4);
            if (RetroSkin.PointInRect(local, slot)) return i;
            x += w;
        }
        return -1;
    }

    private void OpenMenu(int idx)
    {
        _openMenu = idx;
        _openMenuEntries = BuildMenuEntries(idx);

        // Calculate the dropdown's screen rect: anchored under the menu bar
        // item, sized to fit the longest label (+ shortcut + padding).
        var bar = MenuBarRectLocal();
        int x = (int)bar.X + 4;
        for (int i = 0; i < idx; i++)
            x += RetroSkin.MeasureText(MenuBarLabels[i]) + 12;
        int w = 0;
        foreach (var e in _openMenuEntries)
        {
            int eW = RetroSkin.MeasureText(e.Label) + 36;
            if (eW > w) w = eW;
        }
        w = Math.Max(w, 130);
        int rowH = 18;
        int h = _openMenuEntries.Count * rowH + 4;
        _openMenuRect = new Rectangle(x, bar.Y + bar.Height, w, h);
    }

    private void CloseMenu()
    {
        _openMenu = -1;
        _openMenuEntries.Clear();
    }

    private int DropdownHitIndex(Vector2 local)
    {
        if (!RetroSkin.PointInRect(local, _openMenuRect)) return -1;
        int rowH = 18;
        int rel = (int)(local.Y - _openMenuRect.Y - 2);
        return rel / rowH;
    }

    private List<MenuEntry> BuildMenuEntries(int barIdx)
    {
        switch (barIdx)
        {
            case 0: // File
                return new List<MenuEntry>
                {
                    new("New",            () => CmdNew()),
                    new("Open...",        () => CmdOpen()),
                    new("Save",           () => CmdSave()),
                    new("Save As...",     () => CmdSaveAs()),
                    new("",               null, Separator: true),
                    new("Exit",           () => IsFinished = true),
                };
            case 1: // Edit
                return new List<MenuEntry>
                {
                    new("Undo  Ctrl+Z",       () => CmdUndo(),            Disabled: !CanUndo()),
                    new("Redo  Ctrl+Y",       () => CmdRedo(),            Disabled: !CanRedo()),
                    new("",                   null, Separator: true),
                    new("Cut   Ctrl+X",       CutSelection,               Disabled: !_hasSelection),
                    new("Copy  Ctrl+C",       CopySelection,              Disabled: !_hasSelection),
                    new("Paste Ctrl+V",       PasteClipboard,             Disabled: !_clipboardReady),
                    new("Clear Selection Del", DeleteSelection,           Disabled: !_hasSelection),
                    new("Select All Ctrl+A",   SelectAll),
                };
            case 2: // View
                return new List<MenuEntry>
                {
                    new("Zoom 1x", () => _viewScale = 1, Disabled: _viewScale == 1),
                    new("Zoom 2x", () => _viewScale = 2, Disabled: _viewScale == 2),
                    new("Zoom 4x", () => _viewScale = 4, Disabled: _viewScale == 4),
                    new("Zoom 6x", () => _viewScale = 6, Disabled: _viewScale == 6),
                    new("Zoom 8x", () => _viewScale = 8, Disabled: _viewScale == 8),
                };
            case 3: // Image
                return new List<MenuEntry>
                {
                    new("Flip Horizontal",  () => ImgFlip(true)),
                    new("Flip Vertical",    () => ImgFlip(false)),
                    new("",                 null, Separator: true),
                    new("Rotate 90° CW",    () => ImgRotate(90)),
                    new("Rotate 90° CCW",   () => ImgRotate(-90)),
                    new("Rotate 180°",      () => ImgRotate(180)),
                    new("",                 null, Separator: true),
                    new("Stretch 50%",      () => ImgStretch(0.5f)),
                    new("Stretch 200%",     () => ImgStretch(2.0f)),
                    new("",                 null, Separator: true),
                    new("Invert Colors",    () => ImgInvert()),
                    new("Clear Image",      () => ImgClear()),
                    new("",                 null, Separator: true),
                    new("Attributes 320×240",  () => ResizeCanvas(320, 240)),
                    new("Attributes 640×400",  () => ResizeCanvas(640, 400)),
                    new("Attributes 800×600",  () => ResizeCanvas(800, 600)),
                    new("Attributes 1024×768", () => ResizeCanvas(1024, 768)),
                };
            case 4: // Colors
                return new List<MenuEntry>
                {
                    new("Swap Primary/Secondary",  () => (_primary, _secondary) = (_secondary, _primary)),
                    new("Reset Primary to Black",  () => _primary = new Color(0, 0, 0, 255)),
                    new("Reset Secondary to White",() => _secondary = new Color(255, 255, 255, 255)),
                };
            case 5: // Help
                return new List<MenuEntry>
                {
                    new("About MouseHouse Paint", () => Toast("MouseHouse Paint — built into MouseHouse. Saves go through your OS file dialog.")),
                };
        }
        return new();
    }

    private void Toast(string s, float seconds = 4f)
    {
        _toast = s;
        _toastTimer = seconds;
    }

    // ── Image-menu commands ─────────────────────────────────────────────
    private void ImgFlip(bool horizontal)
    {
        if (_hasSelection) CommitSelection();
        PushUndo();
        var img = Raylib.LoadImageFromTexture(_canvas.Texture);
        Raylib.ImageFlipVertical(ref img); // un-flip from RT convention
        if (horizontal) Raylib.ImageFlipHorizontal(ref img);
        else Raylib.ImageFlipVertical(ref img);
        UploadCanvas(ref img);
        _dirty = true;
    }

    private void ImgRotate(int degrees)
    {
        if (_hasSelection) CommitSelection();
        PushUndo();
        var img = Raylib.LoadImageFromTexture(_canvas.Texture);
        Raylib.ImageFlipVertical(ref img);
        if (degrees == 90)       Raylib.ImageRotateCW(ref img);
        else if (degrees == -90) Raylib.ImageRotateCCW(ref img);
        else if (degrees == 180) { Raylib.ImageRotateCW(ref img); Raylib.ImageRotateCW(ref img); }
        // Rotation may swap dims — resize the canvas RT to match.
        if (img.Width != _canvasW || img.Height != _canvasH)
        {
            _canvasW = img.Width; _canvasH = img.Height;
            ReallocCanvas();
        }
        UploadCanvas(ref img);
        _dirty = true;
    }

    private void ImgStretch(float factor)
    {
        if (_hasSelection) CommitSelection();
        PushUndo();
        int newW = Math.Max(1, (int)(_canvasW * factor));
        int newH = Math.Max(1, (int)(_canvasH * factor));
        var img = Raylib.LoadImageFromTexture(_canvas.Texture);
        Raylib.ImageFlipVertical(ref img);
        Raylib.ImageResize(ref img, newW, newH);
        _canvasW = newW; _canvasH = newH;
        ReallocCanvas();
        UploadCanvas(ref img);
        _dirty = true;
    }

    private void ImgInvert()
    {
        if (_hasSelection) CommitSelection();
        PushUndo();
        var img = Raylib.LoadImageFromTexture(_canvas.Texture);
        Raylib.ImageFlipVertical(ref img);
        Raylib.ImageColorInvert(ref img);
        UploadCanvas(ref img);
        _dirty = true;
    }

    private void ImgClear()
    {
        if (_hasSelection) CommitSelection();
        PushUndo();
        Raylib.BeginTextureMode(_canvas);
        Raylib.ClearBackground(_secondary);
        Raylib.EndTextureMode();
        _dirty = true;
    }

    private void ResizeCanvas(int w, int h)
    {
        if (_hasSelection) CommitSelection();
        PushUndo();
        var img = Raylib.LoadImageFromTexture(_canvas.Texture);
        Raylib.ImageFlipVertical(ref img);
        // Resize-canvas (not stretch): new canvas, secondary background, paste
        // existing content top-left.
        var fresh = Raylib.GenImageColor(w, h, _secondary);
        int copyW = Math.Min(w, _canvasW);
        int copyH = Math.Min(h, _canvasH);
        var src = new Rectangle(0, 0, copyW, copyH);
        var dst = new Rectangle(0, 0, copyW, copyH);
        Raylib.ImageDraw(ref fresh, img, src, dst, Color.White);
        _canvasW = w; _canvasH = h;
        ReallocCanvas();
        UploadCanvas(ref fresh);
        Raylib.UnloadImage(img);
        _dirty = true;
    }

    // Replace the canvas contents with the (un-flipped) image.
    private void UploadCanvas(ref Image img)
    {
        // The RT renders with Y flipped (we already render with src h negative),
        // so when we upload we want the image to match what the RT expected.
        // Easiest: draw into the RT directly via an intermediate texture.
        var tex = Raylib.LoadTextureFromImage(img);
        Raylib.BeginTextureMode(_canvas);
        Raylib.ClearBackground(Color.White);
        // The display path uses src=(0,0,w,-h) which already flips Y. So we
        // simply draw the texture upright here and the display will match.
        Raylib.DrawTexture(tex, 0, 0, Color.White);
        Raylib.EndTextureMode();
        Raylib.UnloadTexture(tex);
        Raylib.UnloadImage(img);
    }

    private void ReallocCanvas()
    {
        if (_canvasReady) Raylib.UnloadRenderTexture(_canvas);
        _canvas = Raylib.LoadRenderTexture(_canvasW, _canvasH);
        _canvasReady = true;
    }

    // ── File commands (Save/Open are stubs until Phase 8) ───────────────
    private void CmdNew()
    {
        if (_hasSelection) CommitSelection();
        ClearHistory();
        Raylib.BeginTextureMode(_canvas);
        Raylib.ClearBackground(Color.White);
        Raylib.EndTextureMode();
        _docName = "untitled";
        _currentSavePath = null;
        _dirty = false;
    }
    private string? _currentSavePath;
    private static readonly string[] FileExts = { "png", "bmp", "jpg", "jpeg" };

    private void CmdOpen()
    {
        var path = NativeFileDialog.Open("Open image", FileExts);
        if (string.IsNullOrEmpty(path)) return;
        if (!File.Exists(path)) { Toast($"File not found: {path}"); return; }

        var img = Raylib.LoadImage(path);
        if (img.Width == 0 || img.Height == 0)
        {
            Raylib.UnloadImage(img);
            Toast($"Couldn't load image: {path}");
            return;
        }

        if (_hasSelection) CommitSelection();
        ClearHistory();

        // Replace canvas with the loaded image, sized to fit.
        _canvasW = img.Width; _canvasH = img.Height;
        ReallocCanvas();
        // The display path uses src=(0,0,w,-h) so we draw upright into the RT.
        var tex = Raylib.LoadTextureFromImage(img);
        Raylib.BeginTextureMode(_canvas);
        Raylib.ClearBackground(Color.White);
        Raylib.DrawTexture(tex, 0, 0, Color.White);
        Raylib.EndTextureMode();
        Raylib.UnloadTexture(tex);
        Raylib.UnloadImage(img);

        _currentSavePath = path;
        _docName = Path.GetFileName(path);
        _dirty = false;
        Toast($"Opened: {path}");
    }

    private void CmdSave()
    {
        if (string.IsNullOrEmpty(_currentSavePath)) { CmdSaveAs(); return; }
        if (DoSaveTo(_currentSavePath))
        {
            _docName = Path.GetFileName(_currentSavePath);
            _dirty = false;
            Toast($"Saved to: {_currentSavePath}");
            Console.WriteLine($"[Paint] saved to {_currentSavePath}");
        }
    }

    private void CmdSaveAs()
    {
        if (_hasSelection) CommitSelection();
        string defaultName = string.IsNullOrEmpty(_currentSavePath)
            ? (_docName.Contains('.') ? _docName : _docName + ".png")
            : Path.GetFileName(_currentSavePath);
        var path = NativeFileDialog.Save("Save image as", defaultName, FileExts);
        if (string.IsNullOrEmpty(path)) return;

        // If they didn't include an extension, default to PNG.
        if (string.IsNullOrEmpty(Path.GetExtension(path))) path += ".png";

        if (DoSaveTo(path))
        {
            _currentSavePath = path;
            _docName = Path.GetFileName(path);
            _dirty = false;
            Toast($"Saved to: {path}");
            Console.WriteLine($"[Paint] saved to {path}");
        }
        else
        {
            Toast($"Save failed: {path}");
        }
    }

    private bool DoSaveTo(string path)
    {
        try
        {
            var img = Raylib.LoadImageFromTexture(_canvas.Texture);
            // RenderTexture is Y-flipped relative to file image conventions.
            Raylib.ImageFlipVertical(ref img);
            // Raylib picks format from extension. JPG isn't supported by stb_write
            // in some builds — fall back to PNG bytes if so.
            bool ok = Raylib.ExportImage(img, path);
            Raylib.UnloadImage(img);
            return ok;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Paint] save error: {ex.Message}");
            return false;
        }
    }

    private bool CanUndo() => _undoStack.Count > 0;
    private bool CanRedo() => _redoStack.Count > 0;

    private void PushUndo()
    {
        var img = Raylib.LoadImageFromTexture(_canvas.Texture);
        Raylib.ImageFlipVertical(ref img);
        _undoStack.Push(new Snapshot(img, _canvasW, _canvasH));
        // Bound the stack — drop oldest by reversing into list.
        if (_undoStack.Count > MaxHistory)
        {
            var arr = _undoStack.ToArray();
            // arr[0] = newest, arr[^1] = oldest. Drop oldest.
            Raylib.UnloadImage(arr[^1].Image);
            _undoStack = new Stack<Snapshot>(arr.Take(MaxHistory).Reverse());
        }
        // Any new action invalidates the redo trail.
        while (_redoStack.Count > 0)
        {
            var s = _redoStack.Pop();
            Raylib.UnloadImage(s.Image);
        }
    }

    private void CmdUndo()
    {
        if (!CanUndo()) return;
        // Push current state to redo, then restore from undo.
        var current = Raylib.LoadImageFromTexture(_canvas.Texture);
        Raylib.ImageFlipVertical(ref current);
        _redoStack.Push(new Snapshot(current, _canvasW, _canvasH));

        var s = _undoStack.Pop();
        RestoreSnapshot(s);
        _dirty = true;
    }

    private void CmdRedo()
    {
        if (!CanRedo()) return;
        var current = Raylib.LoadImageFromTexture(_canvas.Texture);
        Raylib.ImageFlipVertical(ref current);
        _undoStack.Push(new Snapshot(current, _canvasW, _canvasH));

        var s = _redoStack.Pop();
        RestoreSnapshot(s);
        _dirty = true;
    }

    private void RestoreSnapshot(Snapshot s)
    {
        // If dimensions differ, realloc the canvas RT.
        if (s.W != _canvasW || s.H != _canvasH)
        {
            _canvasW = s.W; _canvasH = s.H;
            ReallocCanvas();
        }
        var tex = Raylib.LoadTextureFromImage(s.Image);
        Raylib.BeginTextureMode(_canvas);
        Raylib.ClearBackground(Color.White);
        Raylib.DrawTexture(tex, 0, 0, Color.White);
        Raylib.EndTextureMode();
        Raylib.UnloadTexture(tex);
        Raylib.UnloadImage(s.Image);
    }

    // ── Text tool ───────────────────────────────────────────────────────
    private void HandleTextInput(Vector2 cp, bool overCanvas,
                                 bool leftPressed, bool leftReleased)
    {
        // If a text box is open and the user clicks outside it, commit.
        if (_textActive && leftPressed)
        {
            var tr = _textRect;
            if (cp.X < tr.X || cp.Y < tr.Y || cp.X > tr.X + tr.Width || cp.Y > tr.Y + tr.Height)
            {
                CommitText();
                // Fall through so the press can begin a fresh rubber-band.
            }
        }

        // Rubber-band a new text-box rect with left-drag.
        if (!_textActive)
        {
            if (leftPressed && overCanvas)
            {
                _shapeInProgress = true;
                _shapeStart = cp;
                _shapeEnd = cp;
                _shapeUsesPrimary = true;
            }
            if (_shapeInProgress)
            {
                _shapeEnd = cp;
                if (leftReleased)
                {
                    _shapeInProgress = false;
                    var (x, y, w, h) = NormRect((int)_shapeStart.X, (int)_shapeStart.Y,
                                                (int)_shapeEnd.X,   (int)_shapeEnd.Y);
                    if (w >= 8 && h >= _textFontSize)
                    {
                        _textActive = true;
                        _textRect = new Rectangle(x, y, w, h);
                        _textBuffer = "";
                        _textCursorBlink = 0;
                    }
                }
            }
        }
    }

    private void HandleTextEntryKeys()
    {
        // Pull characters out of Raylib's Unicode queue (handles shift, etc).
        int ch;
        while ((ch = Raylib.GetCharPressed()) > 0)
        {
            if (ch >= 32 && ch < 127) _textBuffer += (char)ch;
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Backspace) && _textBuffer.Length > 0)
            _textBuffer = _textBuffer[..^1];
        if (Raylib.IsKeyPressed(KeyboardKey.Enter)) _textBuffer += "\n";
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { _textActive = false; _textBuffer = ""; }
    }

    private void CommitText()
    {
        if (!_textActive) return;
        if (_textBuffer.Length > 0)
        {
            PushUndo();
            Raylib.BeginTextureMode(_canvas);
            try
            {
                // Draw line by line, wrapping to rect.
                int lineH = _textFontSize + 2;
                int y = (int)_textRect.Y + 2;
                foreach (var line in _textBuffer.Split('\n'))
                {
                    RetroSkin.DrawText(line, (int)_textRect.X + 2, y, _primary, _textFontSize);
                    y += lineH;
                }
            }
            finally { Raylib.EndTextureMode(); }
            _dirty = true;
        }
        _textActive = false;
        _textBuffer = "";
    }

    // ── Curve tool ──────────────────────────────────────────────────────
    private void HandleCurveInput(Vector2 cp, bool overCanvas,
                                  bool leftPressed, bool leftReleased)
    {
        // Stage 0: drag to draw the base line. Stage 1+: each click defines a
        // bend control point. After two bends the curve commits.
        if (_curveStage == 0)
        {
            if (leftPressed && overCanvas)
            {
                _shapeInProgress = true;
                _curveA = cp;
                _curveB = cp;
                _curveColor = _primary;
            }
            if (_shapeInProgress)
            {
                _curveB = cp;
                if (leftReleased)
                {
                    _shapeInProgress = false;
                    if (Vector2.Distance(_curveA, _curveB) >= 2)
                    {
                        _curveStage = 1;
                        _curveC1 = (_curveA + _curveB) / 2;
                        _curveC2 = (_curveA + _curveB) / 2;
                    }
                }
            }
            return;
        }

        // While awaiting bends, track cursor as candidate control point.
        if (_curveStage == 1) _curveC1 = cp;
        if (_curveStage == 2) _curveC2 = cp;

        if (leftPressed && overCanvas)
        {
            if (_curveStage == 1) { _curveStage = 2; _curveC2 = cp; }
            else if (_curveStage == 2) CommitCurve();
        }
    }

    private void CommitCurve()
    {
        if (_curveStage == 0) return;
        PushUndo();
        Raylib.BeginTextureMode(_canvas);
        try
        {
            // Cubic Bezier sampling (a, c1, c2, b).
            int steps = (int)Math.Max(20, Vector2.Distance(_curveA, _curveB) * 1.5f);
            Vector2 prev = _curveA;
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                Vector2 p = CubicBezier(_curveA, _curveC1, _curveC2, _curveB, t);
                DrawThickLine((int)prev.X, (int)prev.Y, (int)p.X, (int)p.Y,
                    _lineThickness, _curveColor);
                prev = p;
            }
        }
        finally { Raylib.EndTextureMode(); }
        _dirty = true;
        _curveStage = 0;
    }

    private static Vector2 CubicBezier(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float t)
    {
        float u = 1 - t;
        return u * u * u * a
             + 3 * u * u * t * b
             + 3 * u * t * t * c
             + t * t * t * d;
    }

    // ── Polygon tool ────────────────────────────────────────────────────
    private void HandlePolygonInput(Vector2 cp, bool overCanvas,
                                    bool leftPressed, bool leftReleased)
    {
        if (leftReleased && overCanvas)
        {
            // Detect double-click to commit.
            bool isDouble = (_now - _lastClickTime) < DoubleClickTime
                         && Vector2.Distance(_lastClickPos, cp) <= DoubleClickDist;
            _lastClickTime = _now;
            _lastClickPos = cp;

            if (isDouble && _polyPts.Count >= 2)
            {
                CommitPolygon();
                return;
            }

            // Closing by clicking near the first vertex.
            if (_polyPts.Count >= 3 && Vector2.Distance(_polyPts[0], cp) <= 6)
            {
                CommitPolygon();
                return;
            }

            _polyPts.Add(cp);
        }
    }

    private void CommitPolygon()
    {
        if (_polyPts.Count < 2) { _polyPts.Clear(); return; }
        PushUndo();
        Raylib.BeginTextureMode(_canvas);
        try
        {
            Color stroke = _primary;
            Color fill = _secondary;

            if (_shapeStyle != 0 && _polyPts.Count >= 3)
            {
                // Triangle-fan fill from pts[0]
                for (int i = 1; i < _polyPts.Count - 1; i++)
                    Raylib.DrawTriangle(_polyPts[0], _polyPts[i], _polyPts[i + 1], fill);
            }
            if (_shapeStyle != 2)
            {
                for (int i = 0; i < _polyPts.Count; i++)
                {
                    var a = _polyPts[i];
                    var b = _polyPts[(i + 1) % _polyPts.Count];
                    DrawThickLine((int)a.X, (int)a.Y, (int)b.X, (int)b.Y,
                        _lineThickness, stroke);
                }
            }
        }
        finally { Raylib.EndTextureMode(); }
        _polyPts.Clear();
        _dirty = true;
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
        RetroWidgets.MenuBarVisual(menuBar, MenuBarLabels, _openMenu);

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

            // Text-tool previews: in-progress rubber-band rect, or active text box.
            if (_tool == Tool.Text && _shapeInProgress) DrawTextRectRubberBand(cx, cy);
            if (_textActive) DrawTextBox(cx, cy);

            // In-progress curve preview.
            if (_tool == Tool.Curve && _curveStage > 0) DrawCurvePreview(cx, cy);

            // In-progress polygon preview.
            if (_tool == Tool.Polygon && _polyPts.Count > 0) DrawPolygonPreview(cx, cy);

            // Floating selection contents render on top of canvas at their
            // current floating position.
            if (_hasSelection && _selLifted && _floatingSelReady)
            {
                int fx = cx + (int)(_floatingPos.X * _viewScale);
                int fy = cy + (int)(_floatingPos.Y * _viewScale);
                var fsrc = new Rectangle(0, 0, _floatingSel.Width, _floatingSel.Height);
                var fdst = new Rectangle(fx, fy,
                    _floatingSel.Width * _viewScale, _floatingSel.Height * _viewScale);
                Raylib.DrawTexturePro(_floatingSel, fsrc, fdst, Vector2.Zero, 0f, Color.White);
            }

            // Marching ants on the marquee (in-progress or fixed).
            if (_selecting || _hasSelection) DrawMarchingAnts(cx, cy);
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
        // First status panel: tool-specific hint (mutates as tool changes).
        string hint = ToolHint();
        RetroWidgets.StatusBar(statusBar,
            hint,
            _coordHint,
            $"{_canvasW} x {_canvasH}");

        // Overlay a primary-color swatch at the rightmost edge of the status
        // bar so the user can see exactly what color "left mouse paints" with.
        int swatchSize = (int)statusBar.Height - 8;
        var swatch = new Rectangle(
            statusBar.X + statusBar.Width - swatchSize - 6,
            statusBar.Y + 4, swatchSize, swatchSize);
        Raylib.DrawRectangleRec(swatch, _primary);
        Raylib.DrawRectangleLines((int)swatch.X, (int)swatch.Y,
            (int)swatch.Width, (int)swatch.Height, RetroSkin.DarkShadow);

        // Dropdown menu (drawn last so it appears on top of everything else).
        if (_openMenu >= 0) DrawDropdown(panelOffset);

        // Toast popup
        if (_toastTimer > 0 && _toast.Length > 0) DrawToast(panel);
    }

    private void DrawDropdown(Vector2 panelOffset)
    {
        var rScreen = new Rectangle(_openMenuRect.X + panelOffset.X, _openMenuRect.Y + panelOffset.Y,
            _openMenuRect.Width, _openMenuRect.Height);
        RetroSkin.DrawRaised(rScreen);
        Raylib.DrawRectangleLines((int)rScreen.X, (int)rScreen.Y,
            (int)rScreen.Width, (int)rScreen.Height, RetroSkin.DarkShadow);

        int rowH = 18;
        var mouse = Raylib.GetMousePosition();
        int hoveredIdx = -1;
        if (mouse.X >= rScreen.X && mouse.X < rScreen.X + rScreen.Width
         && mouse.Y >= rScreen.Y && mouse.Y < rScreen.Y + rScreen.Height)
        {
            hoveredIdx = (int)(mouse.Y - rScreen.Y - 2) / rowH;
        }

        for (int i = 0; i < _openMenuEntries.Count; i++)
        {
            var e = _openMenuEntries[i];
            int y = (int)rScreen.Y + 2 + i * rowH;
            if (e.Separator)
            {
                Raylib.DrawLine((int)rScreen.X + 4, y + rowH / 2,
                                (int)(rScreen.X + rScreen.Width - 4), y + rowH / 2, RetroSkin.Shadow);
                continue;
            }
            if (i == hoveredIdx && !e.Disabled)
            {
                Raylib.DrawRectangle((int)rScreen.X + 2, y,
                    (int)rScreen.Width - 4, rowH, RetroSkin.TitleActive);
                RetroSkin.DrawText(e.Label, (int)rScreen.X + 8, y + 2, RetroSkin.TitleText);
            }
            else
            {
                var color = e.Disabled ? RetroSkin.DisabledText : RetroSkin.BodyText;
                RetroSkin.DrawText(e.Label, (int)rScreen.X + 8, y + 2, color);
            }
        }
    }

    private void DrawToast(Rectangle panel)
    {
        int padX = 12, padY = 6;
        int textW = RetroSkin.MeasureText(_toast);
        int w = Math.Min((int)panel.Width - 40, textW + 2 * padX);
        int h = 30;
        int x = (int)(panel.X + (panel.Width - w) / 2);
        int y = (int)(panel.Y + panel.Height - h - RetroWidgets.StatusBarHeight - 12);
        var r = new Rectangle(x, y, w, h);
        RetroSkin.DrawRaised(r);
        var truncated = RetroWidgets.TruncateToWidth(_toast, w - 2 * padX, RetroSkin.BodyFontSize);
        RetroSkin.DrawText(truncated, x + padX, y + padY, RetroSkin.BodyText);
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

    // ── Marching ants ───────────────────────────────────────────────────
    private void DrawMarchingAnts(int canvasX, int canvasY)
    {
        // Origin in screen pixels for the marquee. If the selection has been
        // moved, _selRect is already updated to the floating position, so we
        // can just use _selRect for both lifted and fixed cases.
        int sx = canvasX + (int)(_selRect.X * _viewScale);
        int sy = canvasY + (int)(_selRect.Y * _viewScale);
        int sw = (int)(_selRect.Width  * _viewScale);
        int sh = (int)(_selRect.Height * _viewScale);
        int phase = (int)(_antsTime * 12) % 8;

        // Top + bottom edges
        DrawAntsHorizontal(sx, sy, sw, phase);
        DrawAntsHorizontal(sx, sy + sh - 1, sw, phase);
        // Left + right edges
        DrawAntsVertical(sx, sy, sh, phase);
        DrawAntsVertical(sx + sw - 1, sy, sh, phase);

        // For free-form, also outline the lasso path itself.
        if (_selFreeForm && _freeFormPath.Count > 1)
        {
            float dx = _floatingPos.X - _selRect.X; // 0 unless we sync below
            // _selRect.X is already _floatingPos.X for lifted, so dx = 0.
            for (int i = 0; i < _freeFormPath.Count; i++)
            {
                var p = _freeFormPath[i];
                // Lasso path is in original canvas coords. If the selection
                // has been moved (_selLifted with _floatingPos changed), shift
                // the path to the new origin.
                Vector2 origOrigin = new(0, 0); // freeFormPath was captured raw
                _ = origOrigin; // unused but explicit
                int px = canvasX + (int)((p.X + (_floatingPos.X - GetSelectionOriginalX())) * _viewScale);
                int py = canvasY + (int)((p.Y + (_floatingPos.Y - GetSelectionOriginalY())) * _viewScale);
                if (((px + py + phase) / 4) % 2 == 0)
                    Raylib.DrawPixel(px, py, Color.Black);
                else
                    Raylib.DrawPixel(px, py, Color.White);
            }
        }
    }

    // _selRect is normalized but its X/Y move with the floating selection. To
    // recover the original lasso anchor, we stash it once. For simplicity, use
    // the bbox of the captured path itself.
    private float GetSelectionOriginalX()
    {
        float min = float.MaxValue;
        foreach (var p in _freeFormPath) if (p.X < min) min = p.X;
        return min == float.MaxValue ? 0 : min;
    }
    private float GetSelectionOriginalY()
    {
        float min = float.MaxValue;
        foreach (var p in _freeFormPath) if (p.Y < min) min = p.Y;
        return min == float.MaxValue ? 0 : min;
    }

    private static void DrawAntsHorizontal(int x, int y, int w, int phase)
    {
        for (int i = 0; i < w; i++)
        {
            bool black = ((i + phase) / 4) % 2 == 0;
            Raylib.DrawPixel(x + i, y, black ? Color.Black : Color.White);
        }
    }
    private static void DrawAntsVertical(int x, int y, int h, int phase)
    {
        for (int i = 0; i < h; i++)
        {
            bool black = ((i + phase) / 4) % 2 == 0;
            Raylib.DrawPixel(x, y + i, black ? Color.Black : Color.White);
        }
    }

    // ── Text/Curve/Polygon previews ─────────────────────────────────────
    private void DrawTextRectRubberBand(int canvasX, int canvasY)
    {
        int x0 = canvasX + (int)(_shapeStart.X * _viewScale);
        int y0 = canvasY + (int)(_shapeStart.Y * _viewScale);
        int x1 = canvasX + (int)(_shapeEnd.X   * _viewScale);
        int y1 = canvasY + (int)(_shapeEnd.Y   * _viewScale);
        int x = Math.Min(x0, x1), y = Math.Min(y0, y1);
        int w = Math.Abs(x1 - x0), h = Math.Abs(y1 - y0);
        Raylib.DrawRectangleLines(x, y, w, h, Color.Black);
    }

    private void DrawTextBox(int canvasX, int canvasY)
    {
        int rx = canvasX + (int)(_textRect.X * _viewScale);
        int ry = canvasY + (int)(_textRect.Y * _viewScale);
        int rw = (int)(_textRect.Width  * _viewScale);
        int rh = (int)(_textRect.Height * _viewScale);

        // Live text — draw on top of canvas as preview, only commit on close.
        Raylib.DrawRectangleLines(rx - 1, ry - 1, rw + 2, rh + 2, new Color(0, 0, 255, 255));
        int lineH = (_textFontSize + 2) * _viewScale;
        int y = ry + 2;
        var lines = _textBuffer.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            RetroSkin.DrawText(lines[i], rx + 2, y, _primary, _textFontSize * _viewScale);
            y += lineH;
        }
        // Caret
        if (((int)(_textCursorBlink * 2)) % 2 == 0)
        {
            string lastLine = lines[^1];
            int caretX = rx + 2 + RetroSkin.MeasureText(lastLine, _textFontSize * _viewScale);
            int caretY = ry + 2 + (lines.Length - 1) * lineH;
            Raylib.DrawRectangle(caretX, caretY, 1, _textFontSize * _viewScale, _primary);
        }
    }

    private void DrawCurvePreview(int canvasX, int canvasY)
    {
        Vector2 ToScreen(Vector2 p) =>
            new(canvasX + p.X * _viewScale, canvasY + p.Y * _viewScale);

        if (_curveStage >= 1)
        {
            // Sample curve with current control points.
            int steps = 40;
            Vector2 prev = ToScreen(_curveA);
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                Vector2 p = ToScreen(CubicBezier(_curveA, _curveC1, _curveC2, _curveB, t));
                Raylib.DrawLineEx(prev, p,
                    Math.Max(1, _lineThickness * _viewScale), _curveColor);
                prev = p;
            }
        }
    }

    private void DrawPolygonPreview(int canvasX, int canvasY)
    {
        Vector2 ToScreen(Vector2 p) =>
            new(canvasX + p.X * _viewScale, canvasY + p.Y * _viewScale);

        // Connect existing points
        for (int i = 0; i + 1 < _polyPts.Count; i++)
            Raylib.DrawLineEx(ToScreen(_polyPts[i]), ToScreen(_polyPts[i + 1]),
                Math.Max(1, _lineThickness * _viewScale), _primary);
        // From last point to current cursor (panel-local mouse / viewScale)
        var mouseLocal = Raylib.GetMousePosition();
        // mouseLocal is screen-space already (ToScreen reverses canvas-pixel),
        // so convert it back through canvasX/canvasY:
        var live = new Vector2(mouseLocal.X, mouseLocal.Y);
        Raylib.DrawLineEx(ToScreen(_polyPts[^1]), live,
            Math.Max(1, _lineThickness * _viewScale), _primary);
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
