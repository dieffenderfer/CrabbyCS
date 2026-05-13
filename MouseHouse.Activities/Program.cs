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
    // Horizontal exclusion zone covering both the close (X) and
    // minimize buttons at the right edge of the title bar — clicks
    // in this strip go to the activity / minimize handler, not to
    // window-drag. Two 15-px buttons + 2-px gap + a few px of
    // slack so the user doesn't accidentally start a drag when
    // aiming for the minimize underscore.
    private const int CloseBtnZone = 44;

    public static int Main(string[] args)
    {
        int id = 0;
        string theme = "";
        int bodyFontSize = 16;
        int titleFontSize = 16;
        int statusFontSize = 14;
        float uiScale = 1f;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--id=")) int.TryParse(arg[5..], out id);
            else if (arg.StartsWith("--theme=")) theme = arg[8..];
            else if (arg.StartsWith("--body=")) int.TryParse(arg[7..], out bodyFontSize);
            else if (arg.StartsWith("--title=")) int.TryParse(arg[8..], out titleFontSize);
            else if (arg.StartsWith("--status=")) int.TryParse(arg[9..], out statusFontSize);
            else if (arg.StartsWith("--uiscale=")) float.TryParse(arg[10..], out uiScale);
        }

        UIScaling.Factor = uiScale;

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

        // Title-bar right-click theme picker in activities that expose it
        // (currently World Tee Classic). Broadcast via ThemeSync so the
        // pet's main process and other siblings update in step. The pet
        // also polls ThemeSync now so the user's choice is honored when
        // it reads the active theme on next startup.
        if (activity is WorldTeeClassicActivity wt)
        {
            wt.ThemeCommitted = name => MouseHouse.Core.ThemeSync.Write(name);
        }

        // Query the activity's panel size BEFORE InitWindow so the window is
        // born at the right dimensions. Resizing post-init left Raylib's
        // viewport mapped to the original size on macOS, which threw mouse
        // hit-tests off by however much the size differed (clicks read
        // shifted up/down from where the cursor actually was).
        var size = activity.PanelSize;
        int winW = (int)(size.X * uiScale);
        int winH = (int)(size.Y * uiScale);
        // ResizableWindow lets us grow/shrink the OS window when activities
        // (e.g. Paint) report a new PanelSize via their internal resize grip.
        Raylib.SetConfigFlags(ConfigFlags.UndecoratedWindow | ConfigFlags.ResizableWindow);
        Raylib.InitWindow(winW, winH, "MouseHouse");
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
            (monitorW - winW) / 2,
            (monitorH - winH) / 2);

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
        // comment in InitWindow above). Seeded with the pre-Load `size`
        // (the value InitWindow was actually given), so if Load() mutates
        // PanelSize (e.g. chess puzzles' LoadWindowSize restores a
        // persisted larger size) the diff loop catches it on the first
        // iteration and the OS window gets resized to match.
        var lastPanelSize = size;

        while (!Raylib.WindowShouldClose() && !activity.IsFinished)
        {
            float delta = Raylib.GetFrameTime();

            var newTheme = MouseHouse.Core.ThemeSync.Poll(ref lastThemeMtime);
            if (newTheme != null) RetroSkin.SetTheme(newTheme);
            input.Update();
            var rawLocal = Raylib.GetMousePosition();
            var local = rawLocal / uiScale;
            bool leftPressed = input.LeftPressed;
            bool leftReleased = input.LeftReleased;
            bool rightPressed = input.RightPressed;

            // The previous commit also overrode `local` with the at-press
            // position from the global poller (converted to window-local).
            // The conversion was off for non-fullscreen sibling windows
            // and broke golf aiming (release-position landed wrong, so
            // pull-and-shoot computed a zero-direction vector). Reverted
            // — sibling activities use the live Raylib cursor for hit-
            // testing again. The high-rate poller still drives the press
            // /release counters via InputManager, so fast clicks still
            // register (which was the chess-puzzle fix). Click-position
            // drift on a moving cursor returns for the sibling, but
            // that's a smaller problem than a broken swing.

            // OS file drop (Finder drag, macOS screenshot preview, etc.) →
            // forward to the activity. Paint uses this to open the dropped
            // image directly into the canvas.
            if (Raylib.IsFileDropped())
            {
                var paths = ReadDroppedFilePaths();
                if (paths.Length > 0) activity.OnFilesDropped(paths);
            }

            // Minimize click → iconify to the OS dock/taskbar. Has to be
            // intercepted BEFORE the activity sees the click, otherwise
            // its own DrawTitleBarHitTest path wouldn't fire (the
            // shared helper distinguishes close-X from minimize) but
            // the click would still bleed into the activity's drag /
            // menu logic. The minimize rect is computed off the
            // activity's drawn title bar — same geometry the
            // shared helper draws.
            var activityTitleBar = new Rectangle(FrameInset, FrameInset,
                activity.PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
            if (leftPressed
                && RetroWidgets.MinimizeHitTest(activityTitleBar, local, true))
            {
                Raylib.MinimizeWindow();
                leftPressed = false;        // suppress for the activity
            }

            // Manual window drag — the window is undecorated, so no native
            // title bar to grab. Detect mousedown in the activity's drawn
            // title bar (excluding both the minimize and close glyph
            // zones) and slide the OS window with the cursor.
            bool inDragZone = local.Y >= FrameInset && local.Y < titleBarBottom
                && local.X >= FrameInset
                && local.X < activity.PanelSize.X - FrameInset - CloseBtnZone;

            if (!dragging && leftPressed && inDragZone)
            {
                dragging = true;
                dragGrab = rawLocal;
            }

            if (dragging)
            {
                if (leftReleased) dragging = false;
                else
                {
                    var winPos = Raylib.GetWindowPosition();
                    var screenMouse = winPos + rawLocal;
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
                Rlgl.PushMatrix();
                Rlgl.Scalef(uiScale, uiScale, 1);
                activity.Draw(Vector2.Zero);
                Rlgl.PopMatrix();
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
                Raylib.SetWindowSize((int)(wantSize.X * uiScale), (int)(wantSize.Y * uiScale));
                lastPanelSize = wantSize;
            }

            Raylib.BeginDrawing();
            Raylib.ClearBackground(Color.Blank);
            Rlgl.PushMatrix();
            Rlgl.Scalef(uiScale, uiScale, 1);
            activity.Draw(Vector2.Zero);
            Rlgl.PopMatrix();
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
        261 => new HeartsActivity(),
        270 => new FourTrackActivity(),
        _ => null
    };
}
