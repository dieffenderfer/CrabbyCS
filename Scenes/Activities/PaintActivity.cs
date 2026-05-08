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
    // Resizable window: drag the bottom-right grip to grow / shrink. Cached
    // backing field so PanelSize is O(1).
    private Vector2 _panelSize = new(900, 640);
    public Vector2 PanelSize => _panelSize;

    private bool _resizing;
    private Vector2 _resizeStartMouse;
    private Vector2 _resizeStartSize;
    private const int ResizeGrip = 14;
    private static readonly Vector2 PanelMin = new(560, 420);
    private static readonly Vector2 PanelMax = new(2000, 1400);

    public bool IsFinished { get; private set; }

    // (No asset/audio dependencies — Paint draws everything from code.)

    // ── Layout constants ────────────────────────────────────────────────
    private const int FrameInset    = 3;
    private const int ToolboxW      = 56;
    private const int ToolOptionsH  = 70;
    private const int PaletteH      = 48;

    // ── Canvas (the actual bitmap users paint on) ───────────────────────
    // Stored as a CPU-side Image that we mutate with Raylib.ImageDraw* and a
    // matching Texture2D used for display. RenderTexture would be simpler but
    // BeginTextureMode/EndTextureMode triggers a known Raylib/macOS-Retina
    // viewport bug that scales the whole activity panel down ~0.5x — see
    // RadioWidget.cs which avoids RenderTexture for the same reason.
    private Image _canvasImg;
    private Texture2D _canvasTex;
    // jspaint's MIT-licensed 16×16 tool spritesheet (16 tiles in a row, in
    // the same order as the Tool enum). Loaded in Load(), used by the tool-
    // grid buttons in DrawToolIcon.
    private Texture2D _toolsTex;
    private bool _toolsTexReady;
    private int _canvasW = 1024;
    private int _canvasH = 640;
    private bool _canvasReady;
    private bool _texDirty;

    // Where the canvas's top-left lives in panel-local coords. Updated each
    // frame in Draw, then re-used by Update.
    private Vector2 _canvasOriginCached;
    private int _viewScale = 1; // 1, 2, 4, 6, 8 (Magnifier)

    // Scroll offset (canvas-display pixels, i.e. already viewScale-scaled).
    // The visible canvas area shows _canvas at -_scrollX, -_scrollY relative
    // to the area's top-left, clipped to the area rect.
    private int _scrollX, _scrollY;
    private bool _draggingHScroll;
    private bool _draggingVScroll;
    private int _scrollDragGrabPx;          // pixel offset within the thumb when grabbed
    private const int ScrollbarThickness = 14;

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
    // Updated each frame in Update — null when not hovering any tool button.
    // Drives the status-bar hint text and a small tooltip near the cursor.
    private Tool? _hoveredTool;
    private float _hoveredToolTimer; // counts up while hovering, gates tooltip popup

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

    public PaintActivity() { }

    public void Load()
    {
        _canvasW = 432;
        _canvasH = 324;
        AllocCanvas();

        // jspaint's tool spritesheet (MIT-licensed). Lives next to the binary
        // because the activities companion runs from its own subdirectory.
        var p = Path.Combine(AppContext.BaseDirectory, "assets", "paint", "tools.png");
        if (File.Exists(p))
        {
            _toolsTex = Raylib.LoadTexture(p);
            _toolsTexReady = _toolsTex.Id != 0;
        }
    }

    // Allocate (or reallocate) _canvasImg + _canvasTex sized to _canvasW x _canvasH.
    // Replaces ReallocCanvas() in the old RT pipeline.
    private void AllocCanvas()
    {
        if (_canvasReady)
        {
            Raylib.UnloadImage(_canvasImg);
            Raylib.UnloadTexture(_canvasTex);
        }
        _canvasImg = Raylib.GenImageColor(_canvasW, _canvasH, Color.White);
        _canvasTex = Raylib.LoadTextureFromImage(_canvasImg);
        _canvasReady = true;
        _texDirty = false;
    }

    // Push CPU image bytes into the GPU texture if a mutation has happened.
    // Called once per frame from Draw before sampling _canvasTex.
    private unsafe void SyncCanvasTexture()
    {
        if (!_canvasReady || !_texDirty) return;
        Raylib.UpdateTexture(_canvasTex, _canvasImg.Data);
        _texDirty = false;
    }

    // Replace the canvas with a (presumably already correctly-sized) image.
    // The new image becomes the owned _canvasImg; pass an image you no longer
    // intend to use elsewhere — this method takes ownership.
    private unsafe void AdoptCanvasImage(Image img)
    {
        // Order matters: if `img` is the same struct as _canvasImg (in-place
        // mutation case), unloading first would free the pixels we need.
        if (_canvasReady && img.Data != _canvasImg.Data)
        {
            Raylib.UnloadImage(_canvasImg);
        }
        _canvasImg = img;
        _canvasW = img.Width;
        _canvasH = img.Height;
        if (_canvasReady) Raylib.UnloadTexture(_canvasTex);
        _canvasTex = Raylib.LoadTextureFromImage(_canvasImg);
        _canvasReady = true;
        _texDirty = false;
    }

    public void Close()
    {
        if (_hideOsCursor) { Raylib.ShowCursor(); _hideOsCursor = false; }
        if (_osCursor != MouseCursor.Default)
        {
            Raylib.SetMouseCursor(MouseCursor.Default);
            _osCursor = MouseCursor.Default;
        }
        if (_canvasReady)
        {
            Raylib.UnloadImage(_canvasImg);
            Raylib.UnloadTexture(_canvasTex);
            _canvasReady = false;
        }
        if (_floatingSelReady)  { Raylib.UnloadTexture(_floatingSel);  _floatingSelReady = false; }
        if (_freeFormMaskReady) { Raylib.UnloadImage(_freeFormMask);   _freeFormMaskReady = false; }
        if (_clipboardReady)    { Raylib.UnloadImage(_clipboardImg);   _clipboardReady = false; }
        if (_toolsTexReady)     { Raylib.UnloadTexture(_toolsTex);     _toolsTexReady = false; }
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
        // ── Stuck-flag safety net ─────────────────────────────────────
        // Several drag/draw flags below latch on a leftPressed and reset on
        // leftReleased. If the OS ever drops a release event (mouse-up off
        // the window, focus loss, etc.) the flags would stay stuck and
        // every subsequent click would be eaten by the "still dragging"
        // handler. Whenever the left button is genuinely NOT down, force-
        // clear them so input recovers next frame.
        bool leftDown  = Raylib.IsMouseButtonDown(MouseButton.Left);
        bool rightDown = Raylib.IsMouseButtonDown(MouseButton.Right);
        if (!leftDown)
        {
            _resizing = false;
            _draggingHScroll = false;
            _draggingVScroll = false;
            _draggingSelection = false;
            _drawingLeft = false;
            // Don't auto-cancel _shapeInProgress here — shape tools commit
            // ON release, and if a release was missed the user can still
            // press again to start a new shape. Auto-canceling would lose
            // their in-progress preview if they momentarily lifted off.
        }
        if (!rightDown)
        {
            _drawingRight = false;
        }

        _antsTime += delta;
        _textCursorBlink += delta;
        _now += delta;
        var local = mousePos - panelOffset;

        // Tool-button hover detection (does not depend on click state, runs
        // every frame so the status-bar hint follows the cursor).
        UpdateToolHover(local, delta);

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

        // Resize grip + scrollbars (panel-level interactions before any of
        // the tool/canvas handling).
        if (HandleResizeGrip(local, leftPressed, leftReleased)) return;
        if (HandleScrollbars(local, leftPressed, leftReleased)) return;

        // Menu bar takes priority over everything else.
        if (HandleMenuBarInput(local, leftPressed)) return;

        if (HandleToolboxInput(local, leftPressed)) return;
        if (HandleToolOptionsInput(local, leftPressed)) return;
        if (HandlePaletteInput(local, leftPressed, rightPressed)) return;

        HandleCanvasInput(local, leftPressed, leftReleased, rightPressed);
    }

    // Tool descriptions sourced from MS Paint / jspaint canonical strings so
    // the status-bar hover help reads the same as users remember.
    private static string ToolHintFor(Tool t) => t switch
    {
        Tool.Pencil           => "Draws a free-form line one pixel wide.",
        Tool.Brush            => "Draws using a brush of the selected shape and size.",
        Tool.Eraser           => "Erases part of the picture, using the selected eraser shape.",
        Tool.Fill             => "Fills an area with the current foreground color.",
        Tool.Airbrush         => "Draws using an airbrush of the selected size.",
        Tool.Line             => "Draws a straight line with the selected line width.",
        Tool.Curve            => "Draws a curved line with the selected line width.",
        Tool.Rectangle        => "Draws a rectangle with the selected fill style.",
        Tool.Ellipse          => "Draws an ellipse with the selected fill style.",
        Tool.RoundedRectangle => "Draws a rounded rectangle with the selected fill style.",
        Tool.Polygon          => "Draws a polygon. Double-click to finish.",
        Tool.Text             => "Inserts text into the picture.",
        Tool.Select           => "Selects a rectangular part of the picture to move, copy, or edit.",
        Tool.FreeFormSelect   => "Selects a free-form part of the picture to move, copy, or edit.",
        Tool.PickColor        => "Picks up a color from the picture for drawing.",
        Tool.Magnifier        => "Changes the magnification.",
        _                     => "For Help, click Help Topics on the Help Menu.",
    };

    // Short canonical name for each tool (matches MS Paint's tooltip / a11y
    // labels) — shown above the longer description on the status bar.
    private static string ToolNameFor(Tool t) => t switch
    {
        Tool.Pencil           => "Pencil",
        Tool.Brush            => "Brush",
        Tool.Eraser           => "Eraser/Color Eraser",
        Tool.Fill             => "Fill With Color",
        Tool.Airbrush         => "Airbrush",
        Tool.Line             => "Line",
        Tool.Curve            => "Curve",
        Tool.Rectangle        => "Rectangle",
        Tool.Ellipse          => "Ellipse",
        Tool.RoundedRectangle => "Rounded Rectangle",
        Tool.Polygon          => "Polygon",
        Tool.Text             => "Text",
        Tool.Select           => "Select",
        Tool.FreeFormSelect   => "Free-Form Select",
        Tool.PickColor        => "Pick Color",
        Tool.Magnifier        => "Magnifier",
        _                     => "",
    };

    // Status-bar hint = hovered tool's description if hovering one, else the
    // selected tool's description.
    private string ToolHint() => ToolHintFor(_hoveredTool ?? _tool);

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

    // ── Window resize grip (bottom-right corner) ────────────────────────
    private Rectangle ResizeGripRectLocal()
    {
        return new Rectangle(PanelSize.X - ResizeGrip - FrameInset,
                             PanelSize.Y - ResizeGrip - FrameInset,
                             ResizeGrip, ResizeGrip);
    }

    private bool HandleResizeGrip(Vector2 local, bool leftPressed, bool leftReleased)
    {
        var grip = ResizeGripRectLocal();
        if (!_resizing && leftPressed && RetroSkin.PointInRect(local, grip))
        {
            _resizing = true;
            _resizeStartMouse = local;
            _resizeStartSize = PanelSize;
            return true;
        }
        if (_resizing)
        {
            var delta = local - _resizeStartMouse;
            float w = Math.Clamp(_resizeStartSize.X + delta.X, PanelMin.X, PanelMax.X);
            float h = Math.Clamp(_resizeStartSize.Y + delta.Y, PanelMin.Y, PanelMax.Y);
            _panelSize = new Vector2((int)w, (int)h);
            // After resizing, the previously cached scroll offsets may
            // exceed the new bounds — clamp.
            ClampScroll();
            if (leftReleased) _resizing = false;
            return true;
        }
        return false;
    }

    // ── Canvas display area + scroll geometry ───────────────────────────
    // CanvasAreaLocal is the sunken viewport that the canvas image is
    // displayed inside. Scrollbars (when needed) eat ScrollbarThickness from
    // the right / bottom edges of this rect.
    private Rectangle CanvasAreaLocalFull()
    {
        float bodyY = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight;
        float bodyH = PanelSize.Y - bodyY - RetroWidgets.StatusBarHeight - FrameInset;
        return new Rectangle(FrameInset + ToolboxW + 2, bodyY,
            PanelSize.X - 2 * FrameInset - ToolboxW - 2,
            bodyH - PaletteH);
    }

    private (Rectangle area, bool needH, bool needV) CanvasViewport()
    {
        var full = CanvasAreaLocalFull();
        int dispW = _canvasW * _viewScale;
        int dispH = _canvasH * _viewScale;
        bool needH = dispW > full.Width  - 8;
        bool needV = dispH > full.Height - 8;
        // Reserving a scrollbar may itself force the perpendicular one.
        if (needV && dispW > full.Width  - 8 - ScrollbarThickness) needH = true;
        if (needH && dispH > full.Height - 8 - ScrollbarThickness) needV = true;
        var area = new Rectangle(full.X, full.Y,
            full.Width  - (needV ? ScrollbarThickness : 0),
            full.Height - (needH ? ScrollbarThickness : 0));
        return (area, needH, needV);
    }

    private void ClampScroll()
    {
        var (area, _, _) = CanvasViewport();
        int dispW = _canvasW * _viewScale;
        int dispH = _canvasH * _viewScale;
        int viewW = (int)area.Width  - 8;
        int viewH = (int)area.Height - 8;
        _scrollX = Math.Clamp(_scrollX, 0, Math.Max(0, dispW - viewW));
        _scrollY = Math.Clamp(_scrollY, 0, Math.Max(0, dispH - viewH));
        // Snap scroll to canvas-pixel boundaries so source-rect clipping in
        // the draw path lines up exactly with the viewport edge.
        if (_viewScale > 1)
        {
            _scrollX -= _scrollX % _viewScale;
            _scrollY -= _scrollY % _viewScale;
        }
    }

    // Returns rects for the H/V scrollbar tracks (in panel-local coords).
    private (Rectangle hTrack, Rectangle vTrack, Rectangle hThumb, Rectangle vThumb)
        ScrollbarRects()
    {
        var full = CanvasAreaLocalFull();
        var (area, needH, needV) = CanvasViewport();

        Rectangle hTrack = needH
            ? new Rectangle(full.X, area.Y + area.Height, area.Width, ScrollbarThickness)
            : default;
        Rectangle vTrack = needV
            ? new Rectangle(area.X + area.Width, full.Y, ScrollbarThickness, area.Height)
            : default;

        int dispW = _canvasW * _viewScale;
        int dispH = _canvasH * _viewScale;
        int viewW = (int)area.Width  - 8;
        int viewH = (int)area.Height - 8;

        Rectangle hThumb = default, vThumb = default;
        if (needH)
        {
            float ratio = viewW / (float)dispW;
            float thumbW = Math.Max(20, hTrack.Width * ratio);
            float maxScroll = Math.Max(1, dispW - viewW);
            float frac = _scrollX / maxScroll;
            hThumb = new Rectangle(hTrack.X + frac * (hTrack.Width - thumbW),
                                   hTrack.Y + 2, thumbW, hTrack.Height - 4);
        }
        if (needV)
        {
            float ratio = viewH / (float)dispH;
            float thumbH = Math.Max(20, vTrack.Height * ratio);
            float maxScroll = Math.Max(1, dispH - viewH);
            float frac = _scrollY / maxScroll;
            vThumb = new Rectangle(vTrack.X + 2, vTrack.Y + frac * (vTrack.Height - thumbH),
                                   vTrack.Width - 4, thumbH);
        }
        return (hTrack, vTrack, hThumb, vThumb);
    }

    private bool HandleScrollbars(Vector2 local, bool leftPressed, bool leftReleased)
    {
        var (hTrack, vTrack, hThumb, vThumb) = ScrollbarRects();
        var (area, needH, needV) = CanvasViewport();
        int dispW = _canvasW * _viewScale;
        int dispH = _canvasH * _viewScale;
        int viewW = (int)area.Width  - 8;
        int viewH = (int)area.Height - 8;

        if (_draggingHScroll && needH)
        {
            float maxScroll = Math.Max(1, dispW - viewW);
            float trackUseable = hTrack.Width - hThumb.Width;
            float thumbX = Math.Clamp(local.X - hTrack.X - _scrollDragGrabPx, 0, trackUseable);
            _scrollX = (int)(thumbX / Math.Max(1, trackUseable) * maxScroll);
            ClampScroll();
            if (leftReleased) _draggingHScroll = false;
            return true;
        }
        if (_draggingVScroll && needV)
        {
            float maxScroll = Math.Max(1, dispH - viewH);
            float trackUseable = vTrack.Height - vThumb.Height;
            float thumbY = Math.Clamp(local.Y - vTrack.Y - _scrollDragGrabPx, 0, trackUseable);
            _scrollY = (int)(thumbY / Math.Max(1, trackUseable) * maxScroll);
            ClampScroll();
            if (leftReleased) _draggingVScroll = false;
            return true;
        }

        if (leftPressed && needH && RetroSkin.PointInRect(local, hTrack))
        {
            if (RetroSkin.PointInRect(local, hThumb))
            {
                _draggingHScroll = true;
                _scrollDragGrabPx = (int)(local.X - hThumb.X);
            }
            else
            {
                // Page step
                _scrollX += (local.X < hThumb.X ? -1 : 1) * viewW;
                ClampScroll();
            }
            return true;
        }
        if (leftPressed && needV && RetroSkin.PointInRect(local, vTrack))
        {
            if (RetroSkin.PointInRect(local, vThumb))
            {
                _draggingVScroll = true;
                _scrollDragGrabPx = (int)(local.Y - vThumb.Y);
            }
            else
            {
                _scrollY += (local.Y < vThumb.Y ? -1 : 1) * viewH;
                ClampScroll();
            }
            return true;
        }

        // Mouse-wheel pans the canvas.
        if (RetroSkin.PointInRect(local, area))
        {
            float wheel = Raylib.GetMouseWheelMove();
            if (wheel != 0)
            {
                if (Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift))
                    _scrollX -= (int)(wheel * 30);
                else
                    _scrollY -= (int)(wheel * 30);
                ClampScroll();
            }
        }
        return false;
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

    private void UpdateToolHover(Vector2 local, float delta)
    {
        Tool? hover = null;
        if (RetroSkin.PointInRect(local, ToolboxRectLocal()))
        {
            for (int i = 0; i < ToolGrid.Length; i++)
            {
                if (RetroSkin.PointInRect(local, ToolBtnRectLocal(i)))
                {
                    hover = ToolGrid[i];
                    break;
                }
            }
        }
        if (hover != _hoveredTool) _hoveredToolTimer = 0;
        else if (hover != null) _hoveredToolTimer += delta;
        _hoveredTool = hover;

        // While ANY mouse button is down, suppress the tooltip popup. Stops
        // it from flashing under the cursor mid-click and feeling like it's
        // eating clicks. Status-bar hint still updates.
        if (Raylib.IsMouseButtonDown(MouseButton.Left)
         || Raylib.IsMouseButtonDown(MouseButton.Right))
            _hoveredToolTimer = 0;
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
        // Toolbox is 56 wide, options panel is ~52 wide after inset, so 3
        // columns of 13×13 chips fit cleanly: 3*13 + 2*2 + 4 = 47.
        int chipW = 13, chipH = 13, gap = 2;
        int cols = 3;
        int row = i / cols, col = i % cols;
        return new Rectangle(panel.X + 3 + col * (chipW + gap),
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
        // 14 columns × 2 rows, row-major. The palette array is stored in
        // jspaint order: indices 0..13 are the dark/saturated row (top) and
        // 14..27 are the light/pastel row (bottom), so each column has its
        // canonical "dark on top, light below" pairing (black/white,
        // dark-gray/light-gray, dark-red/red, etc.).
        const int Cols = 14;
        var p = PaletteRectLocal();
        int gridX = (int)p.X + PaletteIndicatorW + 6;
        int gridY = (int)p.Y + (PaletteH - (2 * PaletteSwatchSize + PaletteSwatchGap)) / 2;
        int col = index % Cols;
        int row = index / Cols;
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

        // Convert panel-local cursor to canvas pixel coords. _canvasOriginCached
        // already reflects the scroll offset, so the math is uniform regardless
        // of zoom / pan.
        Vector2 cp = (local - _canvasOriginCached) / _viewScale;
        bool overCanvas = cp.X >= 0 && cp.Y >= 0 && cp.X < _canvasW && cp.Y < _canvasH;
        // Also gate input by whether the cursor is inside the visible viewport
        // — clicking on a scrollbar shouldn't draw on hidden canvas underneath.
        var (viewportRect, _, _) = CanvasViewport();
        if (!RetroSkin.PointInRect(local, viewportRect)) overCanvas = false;

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

    // ── Image-draw primitives (canvas-side, CPU) ────────────────────────
    // These wrap Raylib.ImageDraw* and write to _canvasImg, automatically
    // marking the GPU texture as dirty so the next Draw uploads it.
    private void Img_Pixel(int x, int y, Color c)
    {
        if ((uint)x >= (uint)_canvasW || (uint)y >= (uint)_canvasH) return;
        Raylib.ImageDrawPixel(ref _canvasImg, x, y, c);
        _texDirty = true;
    }

    private void Img_FillRect(int x, int y, int w, int h, Color c)
    {
        Raylib.ImageDrawRectangle(ref _canvasImg, x, y, w, h, c);
        _texDirty = true;
    }

    private void Img_RectLines(int x, int y, int w, int h, int thick, Color c)
    {
        for (int t = 0; t < thick; t++)
        {
            Raylib.ImageDrawRectangle(ref _canvasImg, x + t,             y + t,             w - 2 * t, 1,             c); // top
            Raylib.ImageDrawRectangle(ref _canvasImg, x + t,             y + h - 1 - t,     w - 2 * t, 1,             c); // bottom
            Raylib.ImageDrawRectangle(ref _canvasImg, x + t,             y + t,             1,         h - 2 * t,     c); // left
            Raylib.ImageDrawRectangle(ref _canvasImg, x + w - 1 - t,     y + t,             1,         h - 2 * t,     c); // right
        }
        _texDirty = true;
    }

    private void Img_FillCircle(int cx, int cy, int r, Color c)
    {
        Raylib.ImageDrawCircle(ref _canvasImg, cx, cy, r, c);
        _texDirty = true;
    }

    private void Img_FillEllipse(int cx, int cy, int rx, int ry, Color c)
    {
        if (rx <= 0 || ry <= 0) return;
        // Scanline ellipse fill: for each y, compute x-extent from ellipse eq.
        for (int dy = -ry; dy <= ry; dy++)
        {
            float t = dy / (float)ry;
            int dx = (int)(rx * Math.Sqrt(Math.Max(0, 1 - t * t)));
            Raylib.ImageDrawRectangle(ref _canvasImg, cx - dx, cy + dy, 2 * dx + 1, 1, c);
        }
        _texDirty = true;
    }

    private void Img_EllipseLines(int cx, int cy, int rx, int ry, int thick, Color c)
    {
        if (rx <= 0 || ry <= 0) return;
        // Plot the outline by sampling the parametric ellipse densely. For
        // thickness > 1 we draw multiple concentric outlines stepping inward.
        for (int t = 0; t < thick; t++)
        {
            int trx = Math.Max(1, rx - t), try_ = Math.Max(1, ry - t);
            int steps = (int)(Math.PI * (trx + try_));
            steps = Math.Max(steps, 32);
            for (int i = 0; i < steps; i++)
            {
                double a = i * Math.PI * 2 / steps;
                int px = cx + (int)Math.Round(trx * Math.Cos(a));
                int py = cy + (int)Math.Round(try_ * Math.Sin(a));
                if ((uint)px < (uint)_canvasW && (uint)py < (uint)_canvasH)
                    Raylib.ImageDrawPixel(ref _canvasImg, px, py, c);
            }
        }
        _texDirty = true;
    }

    // Filled triangle via barycentric scanline. Fine for the polygon-fan use
    // case where triangles aren't huge.
    private void Img_FillTriangle(Vector2 a, Vector2 b, Vector2 cp, Color c)
    {
        // Sort vertices by Y
        Vector2[] pts = { a, b, cp };
        Array.Sort(pts, (p, q) => p.Y.CompareTo(q.Y));
        Vector2 p0 = pts[0], p1 = pts[1], p2 = pts[2];

        void FillFlatBottom(Vector2 v0, Vector2 v1, Vector2 v2)
        {
            float invslope1 = (v1.X - v0.X) / (v1.Y - v0.Y);
            float invslope2 = (v2.X - v0.X) / (v2.Y - v0.Y);
            float curx1 = v0.X, curx2 = v0.X;
            for (int sy = (int)v0.Y; sy <= (int)v1.Y; sy++)
            {
                int xa = (int)Math.Min(curx1, curx2), xb = (int)Math.Max(curx1, curx2);
                Raylib.ImageDrawRectangle(ref _canvasImg, xa, sy, xb - xa + 1, 1, c);
                curx1 += invslope1; curx2 += invslope2;
            }
        }
        void FillFlatTop(Vector2 v0, Vector2 v1, Vector2 v2)
        {
            float invslope1 = (v2.X - v0.X) / (v2.Y - v0.Y);
            float invslope2 = (v2.X - v1.X) / (v2.Y - v1.Y);
            float curx1 = v2.X, curx2 = v2.X;
            for (int sy = (int)v2.Y; sy > (int)v0.Y; sy--)
            {
                int xa = (int)Math.Min(curx1, curx2), xb = (int)Math.Max(curx1, curx2);
                Raylib.ImageDrawRectangle(ref _canvasImg, xa, sy, xb - xa + 1, 1, c);
                curx1 -= invslope1; curx2 -= invslope2;
            }
        }

        if (p1.Y == p2.Y) FillFlatBottom(p0, p1, p2);
        else if (p0.Y == p1.Y) FillFlatTop(p0, p1, p2);
        else
        {
            Vector2 p3 = new(p0.X + (p1.Y - p0.Y) / (p2.Y - p0.Y) * (p2.X - p0.X), p1.Y);
            FillFlatBottom(p0, p1, p3);
            FillFlatTop(p1, p3, p2);
        }
        _texDirty = true;
    }

    // Thick line via Bresenham + a stamp circle of radius (thickness/2).
    private void Img_ThickLine(int x0, int y0, int x1, int y1, int thick, Color c)
    {
        if (thick <= 1)
        {
            BresenhamLine(x0, y0, x1, y1, (px, py) => Img_Pixel(px, py, c));
            return;
        }
        int r = thick / 2;
        BresenhamLine(x0, y0, x1, y1, (px, py) => Raylib.ImageDrawCircle(ref _canvasImg, px, py, r, c));
        _texDirty = true;
    }

    private void Img_RoundedRectFill(int x, int y, int w, int h, int r, Color c)
    {
        r = Math.Max(0, Math.Min(r, Math.Min(w / 2, h / 2)));
        if (r == 0) { Img_FillRect(x, y, w, h, c); return; }
        // Body + side strips + 4 corner quarter-disks.
        Raylib.ImageDrawRectangle(ref _canvasImg, x + r, y,         w - 2 * r, h,         c); // center+top+bot
        Raylib.ImageDrawRectangle(ref _canvasImg, x,     y + r,     r,         h - 2 * r, c); // left strip
        Raylib.ImageDrawRectangle(ref _canvasImg, x + w - r, y + r, r,         h - 2 * r, c); // right strip
        Raylib.ImageDrawCircle(ref _canvasImg, x + r,         y + r,         r, c);
        Raylib.ImageDrawCircle(ref _canvasImg, x + w - r - 1, y + r,         r, c);
        Raylib.ImageDrawCircle(ref _canvasImg, x + r,         y + h - r - 1, r, c);
        Raylib.ImageDrawCircle(ref _canvasImg, x + w - r - 1, y + h - r - 1, r, c);
        _texDirty = true;
    }

    private void Img_RoundedRectLines(int x, int y, int w, int h, int r, int thick, Color c)
    {
        r = Math.Max(0, Math.Min(r, Math.Min(w / 2, h / 2)));
        if (r == 0) { Img_RectLines(x, y, w, h, thick, c); return; }
        // Straight edges
        for (int t = 0; t < thick; t++)
        {
            Raylib.ImageDrawRectangle(ref _canvasImg, x + r, y + t,             w - 2 * r, 1, c);
            Raylib.ImageDrawRectangle(ref _canvasImg, x + r, y + h - 1 - t,     w - 2 * r, 1, c);
            Raylib.ImageDrawRectangle(ref _canvasImg, x + t,         y + r, 1, h - 2 * r, c);
            Raylib.ImageDrawRectangle(ref _canvasImg, x + w - 1 - t, y + r, 1, h - 2 * r, c);
        }
        // Corners as arc samples
        int steps = Math.Max(8, r * 2);
        for (int corner = 0; corner < 4; corner++)
        {
            double startAngle = corner * Math.PI / 2 + Math.PI; // upper-left starts at PI
            int ccx = corner switch
            {
                0 => x + r,         // upper-left
                1 => x + w - r - 1, // upper-right
                2 => x + w - r - 1, // lower-right
                _ => x + r,         // lower-left
            };
            int ccy = corner switch
            {
                0 or 1 => y + r,
                _      => y + h - r - 1,
            };
            for (int i = 0; i <= steps; i++)
            {
                double a = startAngle + (corner == 0 ? Math.PI / 2 * i / steps
                                       : corner == 1 ? Math.PI / 2 * i / steps - Math.PI
                                       : corner == 2 ? Math.PI / 2 * i / steps - Math.PI / 2
                                       :                Math.PI / 2 * i / steps - 3 * Math.PI / 2);
                for (int t = 0; t < thick; t++)
                {
                    int rt = r - t;
                    if (rt <= 0) continue;
                    int px = ccx + (int)Math.Round(rt * Math.Cos(a));
                    int py = ccy + (int)Math.Round(rt * Math.Sin(a));
                    if ((uint)px < (uint)_canvasW && (uint)py < (uint)_canvasH)
                        Raylib.ImageDrawPixel(ref _canvasImg, px, py, c);
                }
            }
        }
        _texDirty = true;
    }

    // ── Stroke tools ────────────────────────────────────────────────────
    private void StrokeAt(Vector2 cp, Color color, Color otherColor)
    {
        int x1 = (int)cp.X, y1 = (int)cp.Y;
        if (_lastDraw.X < 0) StampOne(x1, y1, color, otherColor);
        else
        {
            int x0 = (int)_lastDraw.X, y0 = (int)_lastDraw.Y;
            BresenhamLine(x0, y0, x1, y1, (px, py) => StampOne(px, py, color, otherColor));
        }
    }

    private void StampOne(int x, int y, Color color, Color otherColor)
    {
        switch (_tool)
        {
            case Tool.Pencil:
                Img_Pixel(x, y, color);
                break;
            case Tool.Brush:
                Img_FillCircle(x, y, Math.Max(1, _brushSize / 2), color);
                break;
            case Tool.Eraser:
                int s = _eraserSize;
                Img_FillRect(x - s / 2, y - s / 2, s, s, _secondary);
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
                    Img_Pixel(x + dx, y + dy, color);
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

        switch (_tool)
        {
            case Tool.Line:
                Img_ThickLine(x0, y0, x1, y1, _lineThickness, stroke);
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

    private static (int, int, int, int) NormRect(int x0, int y0, int x1, int y1)
        => (Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0) + 1, Math.Abs(y1 - y0) + 1);

    private void DrawShapeRect(int x0, int y0, int x1, int y1, Color stroke, Color fill)
    {
        var (x, y, w, h) = NormRect(x0, y0, x1, y1);
        if (_shapeStyle != 0) Img_FillRect(x, y, w, h, fill);
        if (_shapeStyle != 2) Img_RectLines(x, y, w, h, _lineThickness, stroke);
    }

    private void DrawShapeRoundedRect(int x0, int y0, int x1, int y1, Color stroke, Color fill)
    {
        var (x, y, w, h) = NormRect(x0, y0, x1, y1);
        int r = (int)(0.25f * Math.Min(w, h));
        if (_shapeStyle != 0) Img_RoundedRectFill(x, y, w, h, r, fill);
        if (_shapeStyle != 2) Img_RoundedRectLines(x, y, w, h, r, _lineThickness, stroke);
    }

    private void DrawShapeEllipse(int x0, int y0, int x1, int y1, Color stroke, Color fill)
    {
        var (x, y, w, h) = NormRect(x0, y0, x1, y1);
        int cx = x + w / 2, cy = y + h / 2;
        int rx = Math.Max(1, w / 2), ry = Math.Max(1, h / 2);
        if (_shapeStyle != 0) Img_FillEllipse(cx, cy, rx, ry, fill);
        if (_shapeStyle != 2) Img_EllipseLines(cx, cy, rx, ry, _lineThickness, stroke);
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
        int sx = (int)_selRect.X, sy = (int)_selRect.Y;
        int sw = (int)_selRect.Width, sh = (int)_selRect.Height;
        sx = Math.Max(0, sx); sy = Math.Max(0, sy);
        sw = Math.Min(_canvasW - sx, sw);
        sh = Math.Min(_canvasH - sy, sh);
        if (sw <= 0 || sh <= 0) return;

        var sub = Raylib.ImageFromImage(_canvasImg, new Rectangle(sx, sy, sw, sh));
        if (_selFreeForm && _freeFormMaskReady) ApplyMask(ref sub, _freeFormMask);

        if (_floatingSelReady) Raylib.UnloadTexture(_floatingSel);
        _floatingSel = Raylib.LoadTextureFromImage(sub);
        _floatingSelReady = true;
        Raylib.UnloadImage(sub);

        // Clear original area in the canvas image
        if (_selFreeForm && _freeFormMaskReady)
        {
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                {
                    var m = Raylib.GetImageColor(_freeFormMask, x, y);
                    if (m.A > 0) Raylib.ImageDrawPixel(ref _canvasImg, sx + x, sy + y, _secondary);
                }
        }
        else
        {
            Raylib.ImageDrawRectangle(ref _canvasImg, sx, sy, sw, sh, _secondary);
        }
        _texDirty = true;

        _selLifted = true;
        _dirty = true;
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
            // Pull the floating contents back to a CPU image and ImageDraw it.
            var floatImg = Raylib.LoadImageFromTexture(_floatingSel);
            try
            {
                int dx = (int)_floatingPos.X, dy = (int)_floatingPos.Y;
                Raylib.ImageDraw(ref _canvasImg, floatImg,
                    new Rectangle(0, 0, floatImg.Width, floatImg.Height),
                    new Rectangle(dx, dy, floatImg.Width, floatImg.Height),
                    Color.White);
                _texDirty = true;
                _dirty = true;
            }
            finally { Raylib.UnloadImage(floatImg); }
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
            // Selection isn't lifted — pull region from canvas image directly.
            int sx = Math.Max(0, (int)_selRect.X), sy = Math.Max(0, (int)_selRect.Y);
            int sw = Math.Min(_canvasW - sx, (int)_selRect.Width);
            int sh = Math.Min(_canvasH - sy, (int)_selRect.Height);
            _clipboardImg = Raylib.ImageFromImage(_canvasImg, new Rectangle(sx, sy, sw, sh));
            if (_selFreeForm && _freeFormMaskReady) ApplyMask(ref _clipboardImg, _freeFormMask);
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
        // Prefer the system clipboard so screenshots / images copied from
        // other apps land here. Fall back to our own internal clipboard.
        Image? pasteImg = TryLoadSystemClipboardImage();
        if (pasteImg == null && _clipboardReady)
            pasteImg = Raylib.ImageCopy(_clipboardImg);

        if (pasteImg == null)
        {
            Toast("Nothing to paste — clipboard is empty.");
            return;
        }

        if (_hasSelection) CommitSelection();

        var img = pasteImg.Value;
        // Normalize format so blits/uploads behave predictably.
        Raylib.ImageFormat(ref img, PixelFormat.UncompressedR8G8B8A8);

        var tex = Raylib.LoadTextureFromImage(img);
        if (_floatingSelReady) Raylib.UnloadTexture(_floatingSel);
        _floatingSel = tex;
        _floatingSelReady = true;

        _selRect = new Rectangle(0, 0, img.Width, img.Height);
        _floatingPos = Vector2.Zero;
        _hasSelection = true;
        _selLifted = true;
        _selFreeForm = false;
        _tool = Tool.Select;

        Raylib.UnloadImage(img);

        // If pasted content is bigger than canvas, expand the canvas so the
        // paste actually fits. MS Paint prompts here; we just auto-expand.
        if (_selRect.Width > _canvasW || _selRect.Height > _canvasH)
        {
            int newW = Math.Max(_canvasW, (int)_selRect.Width);
            int newH = Math.Max(_canvasH, (int)_selRect.Height);
            ExpandCanvasTo(newW, newH);
        }
    }

    // Attempt to load an image from the OS clipboard. Returns null on miss.
    private static Image? TryLoadSystemClipboardImage()
    {
        var path = SystemClipboard.TryGetImageToTempPng();
        if (path == null) return null;
        try
        {
            var img = Raylib.LoadImage(path);
            if (img.Width == 0 || img.Height == 0)
            {
                Raylib.UnloadImage(img);
                return null;
            }
            return img;
        }
        finally { try { File.Delete(path); } catch { /* ignore */ } }
    }

    // Resize canvas to (w, h), preserving existing pixels in the top-left.
    private void ExpandCanvasTo(int w, int h)
    {
        if (w <= _canvasW && h <= _canvasH) return;
        var fresh = Raylib.GenImageColor(w, h, _secondary);
        Raylib.ImageDraw(ref fresh, _canvasImg,
            new Rectangle(0, 0, _canvasW, _canvasH),
            new Rectangle(0, 0, _canvasW, _canvasH),
            Color.White);
        AdoptCanvasImage(fresh);
        _dirty = true;
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
                    new("New Window",     () => CmdNewWindow()),
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
                    // Don't grey out — system clipboard might still have an
                    // image even when our internal clipboard is empty.
                    new("Paste Ctrl+V",       PasteClipboard),
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
        if (horizontal) Raylib.ImageFlipHorizontal(ref _canvasImg);
        else            Raylib.ImageFlipVertical(ref _canvasImg);
        AdoptCanvasImage(_canvasImg); // refresh GPU tex with same image
        _dirty = true;
    }

    private void ImgRotate(int degrees)
    {
        if (_hasSelection) CommitSelection();
        PushUndo();
        if (degrees == 90)       Raylib.ImageRotateCW(ref _canvasImg);
        else if (degrees == -90) Raylib.ImageRotateCCW(ref _canvasImg);
        else if (degrees == 180) { Raylib.ImageRotateCW(ref _canvasImg); Raylib.ImageRotateCW(ref _canvasImg); }
        // Rotation may swap dims — re-adopt to refresh GPU tex.
        var img = _canvasImg; AdoptCanvasImage(img);
        _dirty = true;
    }

    private void ImgStretch(float factor)
    {
        if (_hasSelection) CommitSelection();
        PushUndo();
        int newW = Math.Max(1, (int)(_canvasW * factor));
        int newH = Math.Max(1, (int)(_canvasH * factor));
        Raylib.ImageResize(ref _canvasImg, newW, newH);
        var img = _canvasImg; AdoptCanvasImage(img);
        _dirty = true;
    }

    private void ImgInvert()
    {
        if (_hasSelection) CommitSelection();
        PushUndo();
        Raylib.ImageColorInvert(ref _canvasImg);
        var img = _canvasImg; AdoptCanvasImage(img);
        _dirty = true;
    }

    private void ImgClear()
    {
        if (_hasSelection) CommitSelection();
        PushUndo();
        Raylib.ImageClearBackground(ref _canvasImg, _secondary);
        _texDirty = true;
        _dirty = true;
    }

    private void ResizeCanvas(int w, int h)
    {
        if (_hasSelection) CommitSelection();
        PushUndo();
        var fresh = Raylib.GenImageColor(w, h, _secondary);
        int copyW = Math.Min(w, _canvasW);
        int copyH = Math.Min(h, _canvasH);
        Raylib.ImageDraw(ref fresh, _canvasImg,
            new Rectangle(0, 0, copyW, copyH),
            new Rectangle(0, 0, copyW, copyH),
            Color.White);
        AdoptCanvasImage(fresh);
        _dirty = true;
    }

    // ── File commands (Save/Open are stubs until Phase 8) ───────────────
    private void CmdNew()
    {
        if (_hasSelection) CommitSelection();
        ClearHistory();
        Raylib.ImageClearBackground(ref _canvasImg, Color.White);
        _texDirty = true;
        _docName = "untitled";
        _currentSavePath = null;
        _dirty = false;
    }

    // Spawns another Paint window in a separate companion process. The two
    // windows share the system clipboard, so Cut/Copy in one + Paste in the
    // other Just Works.
    private void CmdNewWindow()
    {
        var p = ActivityLauncher.Launch(
            7,
            theme: RetroSkin.Current.Name,
            bodyFontSize: RetroSkin.BodyFontSize,
            titleFontSize: RetroSkin.TitleFontSize,
            statusFontSize: RetroWidgets.StatusFontSize);
        if (p == null) Toast("Couldn't spawn a new Paint window.");
    }
    private string? _currentSavePath;
    private static readonly string[] FileExts = { "png", "bmp", "jpg", "jpeg" };

    private void CmdOpen()
    {
        var path = NativeFileDialog.Open("Open image", FileExts);
        if (string.IsNullOrEmpty(path)) return;
        OpenImageFile(path);
    }

    // Shared by File→Open and OnFilesDropped. Loads `path` and adopts it as
    // the canvas. Returns true on success.
    private bool OpenImageFile(string path)
    {
        if (!File.Exists(path)) { Toast($"File not found: {path}"); return false; }

        var img = Raylib.LoadImage(path);
        if (img.Width == 0 || img.Height == 0)
        {
            Raylib.UnloadImage(img);
            Toast($"Couldn't load image: {path}");
            return false;
        }

        if (_hasSelection) CommitSelection();
        ClearHistory();
        Raylib.ImageFormat(ref img, PixelFormat.UncompressedR8G8B8A8);
        AdoptCanvasImage(img);

        _currentSavePath = path;
        _docName = Path.GetFileName(path);
        _dirty = false;
        Toast($"Opened: {path}");
        return true;
    }

    // Invoked by the host process when the user drags a file onto the Paint
    // window (Finder drag, macOS screenshot preview, etc).
    public void OnFilesDropped(string[] paths)
    {
        if (paths == null || paths.Length == 0) return;
        // Try paths in order; first one that loads wins.
        foreach (var p in paths)
        {
            var ext = Path.GetExtension(p).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif"
                    or ".tga" or ".tiff" or ".tif")
            {
                if (OpenImageFile(p)) return;
            }
        }
        Toast("Drop a PNG / JPG / BMP / GIF — that file isn't a supported image.");
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
            // Image is already in normal (top-down) orientation — write directly.
            return Raylib.ExportImage(_canvasImg, path);
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
        // Snapshot is a deep copy of the current canvas image.
        var copy = Raylib.ImageCopy(_canvasImg);
        _undoStack.Push(new Snapshot(copy, _canvasW, _canvasH));
        if (_undoStack.Count > MaxHistory)
        {
            var arr = _undoStack.ToArray();
            Raylib.UnloadImage(arr[^1].Image);
            _undoStack = new Stack<Snapshot>(arr.Take(MaxHistory).Reverse());
        }
        while (_redoStack.Count > 0)
        {
            var s = _redoStack.Pop();
            Raylib.UnloadImage(s.Image);
        }
    }

    private void CmdUndo()
    {
        if (!CanUndo()) return;
        // If a selection is currently floating, dropping the float without
        // committing is part of "undo" — otherwise the floating preview keeps
        // showing on top of the restored canvas, looking like a duplicate.
        CancelSelection();
        // Also cancel any in-progress shape / text / curve / polygon so the
        // restored state isn't overlaid by a half-built primitive.
        _shapeInProgress = false;
        _curveStage = 0;
        _polyPts.Clear();
        _textActive = false;
        _textBuffer = "";

        var current = Raylib.ImageCopy(_canvasImg);
        _redoStack.Push(new Snapshot(current, _canvasW, _canvasH));
        var s = _undoStack.Pop();
        RestoreSnapshot(s);
        _dirty = true;
    }

    private void CmdRedo()
    {
        if (!CanRedo()) return;
        CancelSelection();
        _shapeInProgress = false;
        _curveStage = 0;
        _polyPts.Clear();
        _textActive = false;
        _textBuffer = "";

        var current = Raylib.ImageCopy(_canvasImg);
        _undoStack.Push(new Snapshot(current, _canvasW, _canvasH));
        var s = _redoStack.Pop();
        RestoreSnapshot(s);
        _dirty = true;
    }

    private void RestoreSnapshot(Snapshot s)
    {
        // The snapshot's image is a fresh standalone Image; AdoptCanvasImage
        // takes ownership and refreshes the GPU texture.
        AdoptCanvasImage(s.Image);
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
            int lineH = _textFontSize + 2;
            int y = (int)_textRect.Y + 2;
            foreach (var line in _textBuffer.Split('\n'))
            {
                Raylib.ImageDrawText(ref _canvasImg, line,
                    (int)_textRect.X + 2, y, _textFontSize, _primary);
                y += lineH;
            }
            _texDirty = true;
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
        int steps = (int)Math.Max(20, Vector2.Distance(_curveA, _curveB) * 1.5f);
        Vector2 prev = _curveA;
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            Vector2 p = CubicBezier(_curveA, _curveC1, _curveC2, _curveB, t);
            Img_ThickLine((int)prev.X, (int)prev.Y, (int)p.X, (int)p.Y,
                _lineThickness, _curveColor);
            prev = p;
        }
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
        Color stroke = _primary;
        Color fill = _secondary;

        if (_shapeStyle != 0 && _polyPts.Count >= 3)
        {
            for (int i = 1; i < _polyPts.Count - 1; i++)
                Img_FillTriangle(_polyPts[0], _polyPts[i], _polyPts[i + 1], fill);
        }
        if (_shapeStyle != 2)
        {
            for (int i = 0; i < _polyPts.Count; i++)
            {
                var a = _polyPts[i];
                var b = _polyPts[(i + 1) % _polyPts.Count];
                Img_ThickLine((int)a.X, (int)a.Y, (int)b.X, (int)b.Y,
                    _lineThickness, stroke);
            }
        }
        _polyPts.Clear();
        _dirty = true;
    }

    // ── Pixel sampling + flood fill ─────────────────────────────────────
    private Color SamplePixel(int x, int y)
    {
        if ((uint)x >= (uint)_canvasW || (uint)y >= (uint)_canvasH) return _primary;
        return Raylib.GetImageColor(_canvasImg, x, y);
    }

    private void FloodFill(int sx, int sy, Color target)
    {
        Color seed = Raylib.GetImageColor(_canvasImg, sx, sy);
        if (ColorsEqual(seed, target)) return;

        var stack = new Stack<(int, int)>();
        stack.Push((sx, sy));
        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            if (x < 0 || x >= _canvasW || y < 0 || y >= _canvasH) continue;
            if (!ColorsEqual(Raylib.GetImageColor(_canvasImg, x, y), seed)) continue;

            int lx = x;
            while (lx - 1 >= 0 && ColorsEqual(Raylib.GetImageColor(_canvasImg, lx - 1, y), seed)) lx--;
            int rx = x;
            while (rx + 1 < _canvasW && ColorsEqual(Raylib.GetImageColor(_canvasImg, rx + 1, y), seed)) rx++;

            for (int i = lx; i <= rx; i++)
            {
                Raylib.ImageDrawPixel(ref _canvasImg, i, y, target);
                if (y - 1 >= 0 && ColorsEqual(Raylib.GetImageColor(_canvasImg, i, y - 1), seed))
                    stack.Push((i, y - 1));
                if (y + 1 < _canvasH && ColorsEqual(Raylib.GetImageColor(_canvasImg, i, y + 1), seed))
                    stack.Push((i, y + 1));
            }
        }
        _texDirty = true;
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

        // Canvas viewport (full = sunken inset; area = viewport minus
        // scrollbars). The canvas display is always clipped to `area` via
        // BeginScissorMode so the zoomed image can't escape the panel.
        var fullCanvasArea = new Rectangle(panel.X + CanvasAreaLocalFull().X,
                                           panel.Y + CanvasAreaLocalFull().Y,
                                           CanvasAreaLocalFull().Width,
                                           CanvasAreaLocalFull().Height);
        RetroSkin.DrawSunken(fullCanvasArea, RetroSkin.SunkenBg);

        if (_canvasReady)
        {
            // Make sure the GPU texture reflects the latest CPU edits.
            SyncCanvasTexture();

            ClampScroll();
            var (areaLocal, needH, needV) = CanvasViewport();
            var areaScreen = new Rectangle(panel.X + areaLocal.X, panel.Y + areaLocal.Y,
                                           areaLocal.Width, areaLocal.Height);

            // Canvas display origin in screen coords, after scroll. The 4-pixel
            // inset is absorbed by the scroll system: when scrollX=0 we offset
            // by +4 just to give the canvas some breathing room from the area
            // edge (matches MS Paint).
            int cx = (int)(areaScreen.X + 4 - _scrollX);
            int cy = (int)(areaScreen.Y + 4 - _scrollY);
            _canvasOriginCached = new Vector2(cx - panelOffset.X, cy - panelOffset.Y);

            int dispW = _canvasW * _viewScale;
            int dispH = _canvasH * _viewScale;

            // BeginScissorMode is broken on macOS Retina (it interprets
            // coords as framebuffer pixels, not logical pixels — same bug
            // that RetroWidgets.cs avoids). So we don't use it. Instead we:
            //   • src-rect-clip the main canvas texture so it never draws
            //     outside the viewport,
            //   • draw overlays (ants, previews, floating selection) using
            //     scaled clip math that constrains them to the viewport.
            int viewW = (int)areaScreen.Width  - 8;
            int viewH = (int)areaScreen.Height - 8;
            int srcX0 = _scrollX / _viewScale;
            int srcY0 = _scrollY / _viewScale;
            int srcW  = Math.Min(_canvasW - srcX0, (viewW + _viewScale - 1) / _viewScale + 1);
            int srcH  = Math.Min(_canvasH - srcY0, (viewH + _viewScale - 1) / _viewScale + 1);
            srcW = Math.Max(0, srcW);
            srcH = Math.Max(0, srcH);
            if (srcW > 0 && srcH > 0)
            {
                var src = new Rectangle(srcX0, srcY0, srcW, srcH);
                var dst = new Rectangle((int)areaScreen.X + 4, (int)areaScreen.Y + 4,
                                        srcW * _viewScale, srcH * _viewScale);
                Raylib.DrawTexturePro(_canvasTex, src, dst, Vector2.Zero, 0f, Color.White);
            }

            // Canvas-edge border. Only draw the parts that intersect the
            // viewport; otherwise on Retina (no scissor) this would draw
            // straight across the panel.
            DrawCanvasEdgeBorder(cx, cy, dispW, dispH, areaScreen);

            if (_shapeInProgress) DrawShapePreview(cx, cy);
            if (_tool == Tool.Text && _shapeInProgress) DrawTextRectRubberBand(cx, cy);
            if (_textActive) DrawTextBox(cx, cy);
            if (_tool == Tool.Curve && _curveStage > 0) DrawCurvePreview(cx, cy);
            if (_tool == Tool.Polygon && _polyPts.Count > 0) DrawPolygonPreview(cx, cy);

            if (_hasSelection && _selLifted && _floatingSelReady)
            {
                DrawFloatingSelectionClipped(cx, cy, areaScreen);
            }
            if (_selecting || _hasSelection) DrawMarchingAnts(cx, cy);

            DrawScrollbars(panelOffset, needH, needV);
        }

        // Palette
        var paletteRect = new Rectangle(bodyRect.X, bodyRect.Y + bodyRect.Height - PaletteH,
            bodyRect.Width, PaletteH);
        RetroSkin.DrawRaised(paletteRect);
        DrawPalette(paletteRect, panelOffset);

        // Bottom-right resize grip (diagonal hatch lines).
        DrawResizeGrip(panelOffset);

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

        // Win9x-style hover tooltip: appears ~0.5s after the cursor settles
        // on a tool button, drawn just below+right of the cursor.
        if (_hoveredTool is { } ht && _hoveredToolTimer > 0.8f)
        {
            DrawToolTooltip(ToolNameFor(ht));
        }

        // Custom-cursor sprite for tools that don't map to a stock OS cursor
        // (Magnifier, Pick Color, Airbrush, Bucket Fill).
        UpdateAndDrawCursor(panelOffset);
    }

    // Currently-active OS cursor; reset to Default whenever leaving the panel
    // or finishing the activity so we don't strand the user with a non-arrow.
    private MouseCursor _osCursor = MouseCursor.Default;
    private bool _hideOsCursor;

    private void UpdateAndDrawCursor(Vector2 panelOffset)
    {
        var mouse = Raylib.GetMousePosition();
        var local = mouse - panelOffset;

        bool inPanel = local.X >= 0 && local.Y >= 0
                    && local.X <= PanelSize.X && local.Y <= PanelSize.Y;
        var (areaLocal, _, _) = CanvasViewport();
        bool overCanvas = inPanel && RetroSkin.PointInRect(local, areaLocal);

        // Decide on the cursor for this frame.
        MouseCursor want = MouseCursor.Default;
        bool wantHidden = false;

        if (_resizing || RetroSkin.PointInRect(local, ResizeGripRectLocal()))
        {
            want = MouseCursor.ResizeNwse;
        }
        else if (overCanvas)
        {
            switch (_tool)
            {
                case Tool.Pencil:
                case Tool.Brush:
                case Tool.Eraser:
                case Tool.Line:
                case Tool.Curve:
                case Tool.Rectangle:
                case Tool.Polygon:
                case Tool.Ellipse:
                case Tool.RoundedRectangle:
                case Tool.Select:
                case Tool.FreeFormSelect:
                    want = MouseCursor.Crosshair;
                    break;
                case Tool.Text:
                    want = MouseCursor.IBeam;
                    break;
                case Tool.PickColor:
                case Tool.Fill:
                case Tool.Airbrush:
                case Tool.Magnifier:
                    // Custom sprite — hide the system cursor.
                    wantHidden = true;
                    break;
            }
        }

        if (wantHidden && !_hideOsCursor) { Raylib.HideCursor(); _hideOsCursor = true; }
        else if (!wantHidden && _hideOsCursor) { Raylib.ShowCursor(); _hideOsCursor = false; }
        if (!wantHidden && want != _osCursor) { Raylib.SetMouseCursor(want); _osCursor = want; }

        if (wantHidden && overCanvas)
        {
            DrawCustomCursorAt((int)mouse.X, (int)mouse.Y, _tool);
        }
    }

    private void DrawCustomCursorAt(int mx, int my, Tool tool)
    {
        if (_toolsTexReady)
        {
            int hotX = tool switch
            {
                Tool.Magnifier => 6,
                Tool.PickColor => 2,
                Tool.Fill      => 2,
                Tool.Airbrush  => 8,
                _              => 8,
            };
            int hotY = tool switch
            {
                Tool.Magnifier => 6,
                Tool.PickColor => 14,
                Tool.Fill      => 12,
                Tool.Airbrush  => 8,
                _              => 8,
            };
            int idx = (int)tool;
            var src = new Rectangle(idx * 16, 0, 16, 16);
            // Halo: draw the sprite tinted white at 8 surrounding 1-px offsets
            // so the icon stays visible against any canvas color.
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var dst = new Rectangle(mx - hotX + dx, my - hotY + dy, 16, 16);
                    Raylib.DrawTexturePro(_toolsTex, src, dst, Vector2.Zero, 0f, Color.White);
                }
            // Then the actual icon on top in normal color.
            var dstTop = new Rectangle(mx - hotX, my - hotY, 16, 16);
            Raylib.DrawTexturePro(_toolsTex, src, dstTop, Vector2.Zero, 0f, Color.White);
            return;
        }
        DrawCustomCursor(mx, my, tool);
    }

    private static void DrawCustomCursor(int mx, int my, Tool tool)
    {
        // Reuse the toolbox's bitmap for visual continuity. Position depends
        // on the tool's "hot spot": Magnifier hot-spot is the center of the
        // glass, Pick Color is the dropper tip, etc.
        var bmp = ToolIconBitmap(tool);
        int hotX = tool switch
        {
            Tool.Magnifier => 6,    // center of the glass
            Tool.PickColor => 2,    // tip is bottom-left
            Tool.Fill      => 2,    // bucket spout
            Tool.Airbrush  => 8,    // center of spray
            _              => 8,
        };
        int hotY = tool switch
        {
            Tool.Magnifier => 6,
            Tool.PickColor => 14,
            Tool.Fill      => 12,
            Tool.Airbrush  => 8,
            _              => 8,
        };
        int x0 = mx - hotX, y0 = my - hotY;
        Color ink = RetroSkin.BodyText;
        Color outline = Color.White; // 1px white halo for visibility
        // Two-pass: first draw white halo (offset pixels), then ink, so the
        // cursor stays visible against any canvas color.
        for (int y = 0; y < bmp.Length && y < 16; y++)
        {
            var row = bmp[y];
            for (int x = 0; x < row.Length && x < 16; x++)
            {
                if (row[x] == '.') continue;
                Raylib.DrawRectangle(x0 + x - 1, y0 + y - 1, 3, 3, outline);
            }
        }
        for (int y = 0; y < bmp.Length && y < 16; y++)
        {
            var row = bmp[y];
            for (int x = 0; x < row.Length && x < 16; x++)
            {
                Color? c = row[x] switch
                {
                    '#' => ink,
                    'B' => new Color(0,   0,   192, 255),
                    'R' => new Color(192, 0,   0,   255),
                    'Y' => new Color(220, 180, 70,  255),
                    'T' => new Color(180, 130, 70,  255),
                    'W' => Color.White,
                    _   => null,
                };
                if (c is { } col) Raylib.DrawPixel(x0 + x, y0 + y, col);
            }
        }
    }

    // Draw only the parts of a 1px rectangle border that are inside the
    // viewport rect. Used because we can't rely on BeginScissorMode on macOS.
    private static void DrawCanvasEdgeBorder(int cx, int cy, int dispW, int dispH, Rectangle viewport)
    {
        int x0 = cx - 1, y0 = cy - 1;
        int x1 = cx + dispW, y1 = cy + dispH;
        int vL = (int)viewport.X, vT = (int)viewport.Y;
        int vR = (int)(viewport.X + viewport.Width);
        int vB = (int)(viewport.Y + viewport.Height);
        Color c = RetroSkin.DarkShadow;

        void Hline(int x, int y, int len)
        {
            int a = Math.Max(x, vL), b = Math.Min(x + len, vR);
            if (a < b && y >= vT && y < vB) Raylib.DrawRectangle(a, y, b - a, 1, c);
        }
        void Vline(int x, int y, int len)
        {
            int a = Math.Max(y, vT), b = Math.Min(y + len, vB);
            if (a < b && x >= vL && x < vR) Raylib.DrawRectangle(x, a, 1, b - a, c);
        }
        Hline(x0, y0, dispW + 2);
        Hline(x0, y1, dispW + 2);
        Vline(x0, y0, dispH + 2);
        Vline(x1, y0, dispH + 2);
    }

    // Floating selection texture, drawn only inside the viewport. We compute
    // a sub-source rect of _floatingSel and a clipped dst rect, mirroring the
    // canvas src-clip approach.
    private void DrawFloatingSelectionClipped(int cx, int cy, Rectangle viewport)
    {
        int fxScreen = cx + (int)(_floatingPos.X * _viewScale);
        int fyScreen = cy + (int)(_floatingPos.Y * _viewScale);
        int fwScreen = _floatingSel.Width  * _viewScale;
        int fhScreen = _floatingSel.Height * _viewScale;

        int vL = (int)viewport.X, vT = (int)viewport.Y;
        int vR = (int)(viewport.X + viewport.Width);
        int vB = (int)(viewport.Y + viewport.Height);

        int dstL = Math.Max(fxScreen, vL);
        int dstT = Math.Max(fyScreen, vT);
        int dstR = Math.Min(fxScreen + fwScreen, vR);
        int dstB = Math.Min(fyScreen + fhScreen, vB);
        if (dstR <= dstL || dstB <= dstT) return;

        int srcL = (dstL - fxScreen) / _viewScale;
        int srcT = (dstT - fyScreen) / _viewScale;
        int srcW = (dstR - dstL) / _viewScale;
        int srcH = (dstB - dstT) / _viewScale;
        if (srcW <= 0 || srcH <= 0) return;

        var src = new Rectangle(srcL, srcT, srcW, srcH);
        var dst = new Rectangle(dstL, dstT, srcW * _viewScale, srcH * _viewScale);
        Raylib.DrawTexturePro(_floatingSel, src, dst, Vector2.Zero, 0f, Color.White);
    }

    private void DrawScrollbars(Vector2 panelOffset, bool needH, bool needV)
    {
        if (!needH && !needV) return;
        var (hTrack, vTrack, hThumb, vThumb) = ScrollbarRects();

        Rectangle ToScreen(Rectangle r) =>
            new(r.X + panelOffset.X, r.Y + panelOffset.Y, r.Width, r.Height);

        if (needH)
        {
            var t = ToScreen(hTrack);
            Raylib.DrawRectangleRec(t, RetroSkin.SunkenBg);
            RetroSkin.DrawRaised(ToScreen(hThumb));
        }
        if (needV)
        {
            var t = ToScreen(vTrack);
            Raylib.DrawRectangleRec(t, RetroSkin.SunkenBg);
            RetroSkin.DrawRaised(ToScreen(vThumb));
        }
        if (needH && needV)
        {
            // Corner box where the two scrollbars meet
            var corner = new Rectangle(hTrack.X + hTrack.Width + panelOffset.X,
                                       hTrack.Y + panelOffset.Y,
                                       ScrollbarThickness, ScrollbarThickness);
            Raylib.DrawRectangleRec(corner, RetroSkin.Face);
        }
    }

    private void DrawResizeGrip(Vector2 panelOffset)
    {
        var grip = ResizeGripRectLocal();
        int gx = (int)(grip.X + panelOffset.X);
        int gy = (int)(grip.Y + panelOffset.Y);
        // Three diagonal hatch lines from upper-right to lower-left of the
        // grip rect — classic Win9x sizing handle.
        for (int d = 2; d < ResizeGrip; d += 4)
        {
            for (int t = 0; t < 2; t++)
            {
                Raylib.DrawLine(gx + ResizeGrip - d - t, gy + ResizeGrip - 2,
                                gx + ResizeGrip - 2,    gy + ResizeGrip - d - t,
                                t == 0 ? RetroSkin.DarkShadow : RetroSkin.Highlight);
            }
        }
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

    private static void DrawToolTooltip(string label)
    {
        if (string.IsNullOrEmpty(label)) return;
        var mouse = Raylib.GetMousePosition();
        int padX = 4, padY = 2;
        int fontSize = 12;
        int tw = RetroSkin.MeasureText(label, fontSize);
        int w = tw + 2 * padX;
        int h = fontSize + 2 * padY;
        int x = (int)mouse.X + 14;
        int y = (int)mouse.Y + 18;
        // Classic Win9x tooltip — pale yellow with a thin black outline.
        var bg = new Color(255, 255, 220, 255);
        Raylib.DrawRectangle(x, y, w, h, bg);
        Raylib.DrawRectangleLines(x, y, w, h, RetroSkin.BodyText);
        RetroSkin.DrawText(label, x + padX, y + padY, RetroSkin.BodyText, fontSize);
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
            DrawToolIconFromSheet(screen, ToolGrid[i], selected);
        }
    }

    // Wraps the static fallback so the toolbox can use the loaded spritesheet
    // when available and the pixel-art icons otherwise.
    private void DrawToolIconFromSheet(Rectangle r, Tool tool, bool pressed)
    {
        if (_toolsTexReady)
        {
            int x0 = (int)r.X + (int)(r.Width  - 16) / 2 + (pressed ? 1 : 0);
            int y0 = (int)r.Y + (int)(r.Height - 16) / 2 + (pressed ? 1 : 0);
            // Tools enum order matches the spritesheet's left-to-right tile
            // order, so (int)tool is the tile index directly.
            int idx = (int)tool;
            var src = new Rectangle(idx * 16, 0, 16, 16);
            var dst = new Rectangle(x0, y0, 16, 16);
            Raylib.DrawTexturePro(_toolsTex, src, dst, Vector2.Zero, 0f, Color.White);
            return;
        }
        DrawToolIcon(r, tool, pressed);
    }

    private static void DrawToolIcon(Rectangle r, Tool tool, bool pressed)
    {
        // Center a 16×16 bitmap inside the 22×22 button. Pixel chars:
        //   '#' → ink (RetroSkin.BodyText)
        //   'B' → cool blue (paint splash)
        //   'R' → red bulb (eyedropper)
        //   'Y' → yellow ferrule (brush)
        //   'W' → highlight white
        //   '.' → transparent
        var icon = ToolIconBitmap(tool);
        int x0 = (int)r.X + (int)(r.Width  - 16) / 2 + (pressed ? 1 : 0);
        int y0 = (int)r.Y + (int)(r.Height - 16) / 2 + (pressed ? 1 : 0);
        Color ink   = RetroSkin.BodyText;
        Color blue  = new(0,   0,   192, 255);
        Color red   = new(192, 0,   0,   255);
        Color yel   = new(220, 180, 70,  255);
        Color tan   = new(180, 130, 70,  255);
        for (int y = 0; y < icon.Length && y < 16; y++)
        {
            var row = icon[y];
            for (int x = 0; x < row.Length && x < 16; x++)
            {
                Color? c = row[x] switch
                {
                    '#' => ink,
                    'B' => blue,
                    'R' => red,
                    'Y' => yel,
                    'T' => tan,
                    'W' => Color.White,
                    _   => null,
                };
                if (c is { } col) Raylib.DrawPixel(x0 + x, y0 + y, col);
            }
        }
    }

    private static string[] ToolIconBitmap(Tool t) => t switch
    {
        Tool.FreeFormSelect => new[]
        {
            "................",
            "................",
            ".......#.#......",
            ".....#.....#....",
            "....#.......#...",
            "...#.........#..",
            "..#...........#.",
            "..#............#",
            ".#.............#",
            "..#............#",
            "..#...........#.",
            "...#.........#..",
            "....#......#....",
            ".....##..#.#....",
            "................",
            "................",
        },
        Tool.Select => new[]
        {
            "................",
            "................",
            "...# # # # # ##.",
            "...#..........#.",
            "...............",
            "...#..........#.",
            "...............",
            "...#..........#.",
            "...............",
            "...#..........#.",
            "...............",
            "...# # # # # ##.",
            "................",
            "................",
            "................",
            "................",
        },
        Tool.Eraser => new[]
        {
            "................",
            "................",
            "................",
            "..........####..",
            ".........#####..",
            "........######..",
            ".......#####....",
            "......#####.....",
            ".....#####......",
            "....#####.......",
            "...#####........",
            "...####.........",
            "................",
            "................",
            "................",
            "................",
        },
        Tool.Fill => new[]
        {
            "................",
            ".....#..........",
            "....##..........",
            "...#.#..........",
            "..#..#..........",
            ".##.##..........",
            "#####...........",
            "#####...........",
            "#####...........",
            ".####...........",
            "..##....B.......",
            "........BB......",
            ".......BB.B.....",
            "........BB......",
            ".........B......",
            "................",
        },
        Tool.PickColor => new[]
        {
            "................",
            ".............R..",
            "............RRR.",
            "...........RRR..",
            "..........###...",
            ".........###....",
            "........###.....",
            ".......###......",
            "......###.......",
            ".....###........",
            "....###.........",
            "...###..........",
            "..###...........",
            "..##............",
            "..#.............",
            "................",
        },
        Tool.Magnifier => new[]
        {
            "................",
            "....######......",
            "...##....##.....",
            "..##......##....",
            "..#........#....",
            "..#........#....",
            "..#........#....",
            "..##......##....",
            "...##....###....",
            "....######.##...",
            "..........###...",
            "...........###..",
            "............###.",
            ".............##.",
            "................",
            "................",
        },
        Tool.Pencil => new[]
        {
            "................",
            ".............##.",
            "............####",
            "...........####.",
            "..........####..",
            ".........####...",
            "........####....",
            ".......####.....",
            "......####......",
            ".....####.......",
            "....####........",
            "...####.........",
            "..####..........",
            ".###............",
            "..#.............",
            "................",
        },
        Tool.Brush => new[]
        {
            "................",
            ".......##.......",
            "......##........",
            ".....##.........",
            "....##.YY.......",
            "...##.YYYY......",
            "..##.YYYYY......",
            "..#.YYYYY.......",
            ".#.YYYY.........",
            ".TTTTT..........",
            ".TTTT...........",
            "..TT............",
            "...T............",
            "................",
            "................",
            "................",
        },
        Tool.Airbrush => new[]
        {
            "................",
            "...####.........",
            "..#....#........",
            "..#....#........",
            "..#....#........",
            "..######........",
            ".#......#.......",
            ".#......#.......",
            ".#......#.......",
            ".########...#...",
            ".#......#..###..",
            ".########...#...",
            "................",
            "................",
            "................",
            "................",
        },
        Tool.Text => new[]
        {
            "................",
            "................",
            ".......##.......",
            ".......##.......",
            "......####......",
            "......####......",
            ".....##..##.....",
            ".....##..##.....",
            "....########....",
            "....##....##....",
            "...##......##...",
            "...##......##...",
            "..##........##..",
            "................",
            "................",
            "................",
        },
        Tool.Line => new[]
        {
            "................",
            "................",
            ".............##.",
            "............##..",
            "...........##...",
            "..........##....",
            ".........##.....",
            "........##......",
            ".......##.......",
            "......##........",
            ".....##.........",
            "....##..........",
            "...##...........",
            "..##............",
            "................",
            "................",
        },
        Tool.Curve => new[]
        {
            "................",
            "................",
            ".....####.......",
            "....##..##......",
            "...##....#......",
            "...#......#.....",
            "..#.......##....",
            "..#........##...",
            "..#.........##..",
            "...#.........##.",
            "................",
            "................",
            "................",
            "................",
            "................",
            "................",
        },
        Tool.Rectangle => new[]
        {
            "................",
            "................",
            "..############..",
            "..#..........#..",
            "..#..........#..",
            "..#..........#..",
            "..#..........#..",
            "..#..........#..",
            "..#..........#..",
            "..#..........#..",
            "..#..........#..",
            "..############..",
            "................",
            "................",
            "................",
            "................",
        },
        Tool.Polygon => new[]
        {
            "................",
            "................",
            ".......##.......",
            "......####......",
            ".....######.....",
            "....########....",
            "...##########...",
            "..############..",
            ".##############.",
            "################",
            "................",
            "................",
            "................",
            "................",
            "................",
            "................",
        },
        Tool.Ellipse => new[]
        {
            "................",
            "................",
            "................",
            "......####......",
            "....##....##....",
            "...#........#...",
            "..#..........#..",
            "..#..........#..",
            "..#..........#..",
            "...#........#...",
            "....##....##....",
            "......####......",
            "................",
            "................",
            "................",
            "................",
        },
        Tool.RoundedRectangle => new[]
        {
            "................",
            "................",
            "....########....",
            "...#........#...",
            "..#..........#..",
            "..#..........#..",
            "..#..........#..",
            "..#..........#..",
            "..#..........#..",
            "..#..........#..",
            "...#........#...",
            "....########....",
            "................",
            "................",
            "................",
            "................",
        },
        _ => new[] { "................" },
    };

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
                    // Tiny font so "8x" fits inside the 13-wide chip without
                    // bleeding past its right edge.
                    string label = zooms[i] + "x";
                    int fs = 9;
                    int tw = RetroSkin.MeasureText(label, fs);
                    RetroSkin.DrawText(label,
                        (int)(chip.X + (chip.Width - tw) / 2),
                        (int)(chip.Y + (chip.Height - fs) / 2),
                        RetroSkin.BodyText, fs);
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
