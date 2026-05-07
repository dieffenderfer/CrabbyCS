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
///   [toolbox | canvas    ] [tool options strip below toolbox]
///   [        | (scrolls) ]
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
    private const int ToolOptionsH  = 80;
    private const int PaletteH      = 48;

    // ── Canvas (the actual bitmap users paint on) ───────────────────────
    private RenderTexture2D _canvas;
    private int _canvasW = 640;
    private int _canvasH = 400;
    private bool _canvasReady;

    // Title text (filename + dirty marker, MS-Paint style).
    private string _docName = "untitled";
    private bool _dirty;

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
    }

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

        // Body bounds (everything between menu bar and status bar)
        float bodyY  = menuBar.Y + menuBar.Height;
        float bodyH  = panel.Height - (bodyY - panel.Y) - RetroWidgets.StatusBarHeight - FrameInset;
        var bodyRect = new Rectangle(panel.X + FrameInset, bodyY,
            panel.Width - 2 * FrameInset, bodyH);

        // Toolbox + tool options panel (left column)
        var toolboxRect = new Rectangle(bodyRect.X, bodyRect.Y, ToolboxW, bodyRect.Height - PaletteH);
        RetroSkin.DrawRaised(toolboxRect);

        // Canvas surround (sunken inset showing the paint area + scrollable space)
        var canvasArea = new Rectangle(
            toolboxRect.X + toolboxRect.Width + 2,
            bodyRect.Y,
            bodyRect.Width - toolboxRect.Width - 2,
            bodyRect.Height - PaletteH);
        RetroSkin.DrawSunken(canvasArea, RetroSkin.SunkenBg);

        // Draw the canvas bitmap inside the sunken area at 1:1, top-left aligned.
        if (_canvasReady)
        {
            int cx = (int)(canvasArea.X + 4);
            int cy = (int)(canvasArea.Y + 4);
            // RenderTexture has flipped Y in Raylib — use negative source height.
            var src = new Rectangle(0, 0, _canvasW, -_canvasH);
            var dst = new Rectangle(cx, cy, _canvasW, _canvasH);
            Raylib.DrawTexturePro(_canvas.Texture, src, dst, Vector2.Zero, 0f, Color.White);
            // Thin border around the canvas itself
            Raylib.DrawRectangleLines(cx - 1, cy - 1, _canvasW + 2, _canvasH + 2, RetroSkin.DarkShadow);
        }

        // Color palette strip (bottom of body)
        var paletteRect = new Rectangle(bodyRect.X, bodyRect.Y + bodyRect.Height - PaletteH,
            bodyRect.Width, PaletteH);
        RetroSkin.DrawRaised(paletteRect);

        // Status bar
        var statusBar = new Rectangle(panel.X + FrameInset,
            panel.Y + panel.Height - RetroWidgets.StatusBarHeight - FrameInset,
            panel.Width - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(statusBar,
            "For Help, click Help Topics on the Help Menu.",
            $"{_canvasW} x {_canvasH}");
    }
}
