using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Activities;

/// <summary>
/// Entry point for the activity sibling executable. Each retro game runs in
/// its own instance of this process: it boots a single Raylib window (NOT
/// always-on-top), hosts one IActivity, and exits when the activity closes.
/// This is what lets the pet's main window keep its NSStatusWindowLevel
/// "always above other apps" behavior while game windows behave like normal
/// app windows you can put behind Chrome / your editor / etc.
/// </summary>
internal static class Program
{
    private const int FrameInset = 3;
    // Approximate horizontal exclusion zone for the close-X glyph at the far
    // right of the title bar — clicks there go to the activity (close), not
    // to window-drag.
    private const int CloseBtnZone = 22;

    public static int Main(string[] args)
    {
        int id = 0;
        string theme = "";
        int bodyFontSize = 16;
        int titleFontSize = 16;
        int statusFontSize = 14;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--id=")) int.TryParse(arg[5..], out id);
            else if (arg.StartsWith("--theme=")) theme = arg[8..];
            else if (arg.StartsWith("--body=")) int.TryParse(arg[7..], out bodyFontSize);
            else if (arg.StartsWith("--title=")) int.TryParse(arg[8..], out titleFontSize);
            else if (arg.StartsWith("--status=")) int.TryParse(arg[9..], out statusFontSize);
        }

        if (!string.IsNullOrEmpty(theme)) RetroSkin.SetTheme(theme);
        RetroSkin.BodyFontSize = bodyFontSize;
        RetroSkin.TitleFontSize = titleFontSize;
        RetroWidgets.StatusFontSize = statusFontSize;

        var activity = CreateActivity(id);
        if (activity == null)
        {
            Console.Error.WriteLine($"[MouseHouse.Activities] unknown activity id: {id}");
            return 1;
        }

        // Query the activity's panel size BEFORE InitWindow so the window is
        // born at the right dimensions. Resizing post-init left Raylib's
        // viewport mapped to the original size on macOS, which threw mouse
        // hit-tests off by however much the size differed (clicks read
        // shifted up/down from where the cursor actually was).
        var size = activity.PanelSize;
        // ResizableWindow lets us grow/shrink the OS window when activities
        // (e.g. Paint) report a new PanelSize via their internal resize grip.
        Raylib.SetConfigFlags(ConfigFlags.UndecoratedWindow | ConfigFlags.ResizableWindow);
        Raylib.InitWindow((int)size.X, (int)size.Y, "MouseHouse");
        Raylib.InitAudioDevice();
        Raylib.SetTargetFPS(60);

        // Boot the same high-rate global click poller the main app uses,
        // and dispense its events through InputManager. Without this the
        // sibling fell back to Raylib.IsMouseButtonPressed/Released which
        // misses fast click+release pairs on macOS — the long-standing
        // "I have to hold the mouse for clicks to register in chess
        // puzzles" complaint, since chess puzzles runs in this sibling.
        WindowHelper.StartClickPoller();
        var input = new InputManager();

        activity.Load();

        // Center on whichever monitor the OS placed us on.
        int monitor = Raylib.GetCurrentMonitor();
        int monitorW = Math.Max(800, Raylib.GetMonitorWidth(monitor));
        int monitorH = Math.Max(600, Raylib.GetMonitorHeight(monitor));
        Raylib.SetWindowPosition(
            (monitorW - (int)size.X) / 2,
            (monitorH - (int)size.Y) / 2);

        // Raylib's default Esc-quits behavior: keep it; closing via title-bar
        // X is also wired (the activity sets IsFinished).
        bool dragging = false;
        Vector2 dragGrab = Vector2.Zero;
        int titleBarBottom = FrameInset + RetroWidgets.TitleBarHeight;

        // Poll the pet's theme broadcast each frame so theme hover-previews
        // (and committed theme changes) apply live in this sibling window.
        var lastThemeMtime = DateTime.MinValue;

        // Track activity.PanelSize so we only call SetWindowSize when the
        // activity actually changes its size — calling it every frame on
        // macOS can shift the viewport and throw mouse coords off (see the
        // comment in InitWindow above).
        var lastPanelSize = activity.PanelSize;

        while (!Raylib.WindowShouldClose() && !activity.IsFinished)
        {
            float delta = Raylib.GetFrameTime();

            var newTheme = MouseHouse.Core.ThemeSync.Poll(ref lastThemeMtime);
            if (newTheme != null) RetroSkin.SetTheme(newTheme);
            input.Update();
            var local = Raylib.GetMousePosition();
            bool leftPressed = input.LeftPressed;
            bool leftReleased = input.LeftReleased;
            bool rightPressed = input.RightPressed;

            // OS file drop (Finder drag, macOS screenshot preview, etc.) →
            // forward to the activity. Paint uses this to open the dropped
            // image directly into the canvas.
            if (Raylib.IsFileDropped())
            {
                var paths = ReadDroppedFilePaths();
                if (paths.Length > 0) activity.OnFilesDropped(paths);
            }

            // Manual window drag — the window is undecorated, so no native
            // title bar to grab. Detect mousedown in the activity's drawn
            // title bar (excluding the close glyph zone) and slide the OS
            // window with the cursor.
            bool inDragZone = local.Y >= FrameInset && local.Y < titleBarBottom
                && local.X >= FrameInset
                && local.X < activity.PanelSize.X - FrameInset - CloseBtnZone;

            if (!dragging && leftPressed && inDragZone)
            {
                dragging = true;
                dragGrab = local;
            }

            if (dragging)
            {
                if (leftReleased) dragging = false;
                else
                {
                    var winPos = Raylib.GetWindowPosition();
                    var screenMouse = winPos + Raylib.GetMousePosition();
                    var newPos = screenMouse - dragGrab;
                    Raylib.SetWindowPosition((int)newPos.X, (int)newPos.Y);
                }
                // Tick the activity even mid-drag so streaming audio
                // (radio in particular) doesn't starve while the user
                // moves the window. We pass leftPressed=false so the
                // activity doesn't double-react to the drag-grab click,
                // and clear the input edge bits the host already consumed.
                activity.Update(delta, local, Vector2.Zero,
                    leftPressed: false, leftReleased: false, rightPressed: false);
                Raylib.BeginDrawing();
                Raylib.ClearBackground(Color.Blank);
                activity.Draw(Vector2.Zero);
                Raylib.EndDrawing();
                continue;
            }

            activity.Update(delta, local, Vector2.Zero,
                leftPressed, leftReleased, rightPressed);

            // Mirror PanelSize → OS window only when the activity ACTUALLY
            // changed its size. Diffing against GetScreenWidth/Height each
            // frame would re-trigger SetWindowSize on Retina (logical-vs-
            // framebuffer mismatch) and shift mouse coords mid-frame.
            var wantSize = activity.PanelSize;
            if (wantSize != lastPanelSize)
            {
                Raylib.SetWindowSize((int)wantSize.X, (int)wantSize.Y);
                lastPanelSize = wantSize;
            }

            Raylib.BeginDrawing();
            Raylib.ClearBackground(Color.Blank);
            activity.Draw(Vector2.Zero);
            Raylib.EndDrawing();
        }

        activity.Close();
        Raylib.CloseAudioDevice();
        Raylib.CloseWindow();
        return 0;
    }

    // Marshal a Raylib FilePathList to a managed string[] of UTF-8 paths,
    // and unload the native list so we don't leak.
    private static unsafe string[] ReadDroppedFilePaths()
    {
        var list = Raylib.LoadDroppedFiles();
        var result = new string[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            // list.Paths is char** — each entry is a NUL-terminated UTF-8 path.
            result[i] = System.Runtime.InteropServices.Marshal
                .PtrToStringUTF8((IntPtr)list.Paths[i]) ?? "";
        }
        Raylib.UnloadDroppedFiles(list);
        return result;
    }

    private static IActivity? CreateActivity(int id) => id switch
    {
        7   => new PaintActivity(),
        8   => new ChessPuzzleActivity(),
        9   => new RadioActivity(),
        200 => new RetroDemoActivity(),
        201 => new MinesweeperActivity(),
        202 => new GolfActivity(),
        203 => new CruelActivity(),
        204 => new TaipeiActivity(),
        205 => new PeggedActivity(),
        206 => new TicTacticsActivity(),
        207 => new TetrisActivity(),
        208 => new IdleWildActivity(),
        210 => new FreeCellActivity(),
        211 => new TutsTombActivity(),
        212 => new StonesActivity(),
        213 => new JigsawedActivity(),
        214 => new RattlerRaceActivity(),
        215 => new PipeDreamActivity(),
        216 => new RodentsRevengeActivity(),
        240 => new TriPeaksActivity(),
        241 => new TetraVexActivity(),
        242 => new KlotskiActivity(),
        243 => new LifeGenesisActivity(),
        244 => new WordZapActivity(),
        245 => new SkiFreeActivity(),
        246 => new WorldTeeClassicActivity(),
        250 => new ChessActivity(),
        251 => new ChipsChallengeActivity(),
        252 => new DrBlackJackActivity(),
        253 => new GoFigureActivity(),
        254 => new JezzBallActivity(),
        255 => new MaxwellsManiacActivity(),
        256 => new TicTacDropActivity(),
        260 => new RetroChessPuzzlesActivity(),
        _ => null
    };
}
