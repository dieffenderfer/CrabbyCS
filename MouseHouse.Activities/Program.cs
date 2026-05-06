using System.Numerics;
using Raylib_cs;
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
        Raylib.SetConfigFlags(ConfigFlags.UndecoratedWindow);
        Raylib.InitWindow((int)size.X, (int)size.Y, "MouseHouse");
        Raylib.SetTargetFPS(60);

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

        while (!Raylib.WindowShouldClose() && !activity.IsFinished)
        {
            float delta = Raylib.GetFrameTime();
            var local = Raylib.GetMousePosition();
            bool leftPressed = Raylib.IsMouseButtonPressed(MouseButton.Left);
            bool leftReleased = Raylib.IsMouseButtonReleased(MouseButton.Left);
            bool rightPressed = Raylib.IsMouseButtonPressed(MouseButton.Right);

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
                Raylib.BeginDrawing();
                Raylib.ClearBackground(Color.Blank);
                activity.Draw(Vector2.Zero);
                Raylib.EndDrawing();
                continue;
            }

            activity.Update(delta, local, Vector2.Zero,
                leftPressed, leftReleased, rightPressed);

            Raylib.BeginDrawing();
            Raylib.ClearBackground(Color.Blank);
            activity.Draw(Vector2.Zero);
            Raylib.EndDrawing();
        }

        activity.Close();
        Raylib.CloseWindow();
        return 0;
    }

    private static IActivity? CreateActivity(int id) => id switch
    {
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
        246 => new FujiGolfActivity(),
        250 => new ChessActivity(),
        251 => new ChipsChallengeActivity(),
        252 => new DrBlackJackActivity(),
        253 => new GoFigureActivity(),
        254 => new JezzBallActivity(),
        255 => new MaxwellsManiacActivity(),
        256 => new TicTacDropActivity(),
        260 => new RetroChessPuzzlesActivity(),
        270 => new DesktopDestroyerActivity(),
        _ => null
    };
}
