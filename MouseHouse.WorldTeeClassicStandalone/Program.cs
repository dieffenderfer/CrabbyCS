using System.Numerics;
using System.Runtime.InteropServices;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.WorldTeeClassicStandalone;

/// <summary>
/// Entry point for the standalone "Ohio Golf" / World Tee Classic build
/// distributed independently from the desktop pet (e.g. on itch.io).
/// Hosts a single <see cref="WorldTeeClassicActivity"/> as a transparent,
/// undecorated, always-on-top window so the game appears to float
/// directly on the user's desktop — the same on-the-desktop feel as the
/// CrabbyCS pet, without a regular OS frame around it. Source is shared
/// with the main pet via the .csproj's Compile globs, so improvements
/// made for the pet flow into this binary on next build and vice versa.
/// </summary>
internal static class Program
{
    // Approximate horizontal exclusion zone for the close-X glyph at the
    // far right of the activity's drawn title bar — clicks there must
    // reach the activity (which closes the window via IsFinished), not
    // the host's drag handler.
    private const int CloseBtnZone = 22;
    private const int FrameInset = 3;

    public static int Main(string[] _)
    {
        // Switch the persistence root BEFORE any save/load so the
        // standalone gets its own %APPDATA%\WorldTeeClassic\ etc. and
        // doesn't share world_tee_classic.json / courses/ / Moon-unlock
        // state with a CrabbyCS install on the same machine.
        SaveManager.AppFolderName = "WorldTeeClassic";

        // Same flag set the pet uses (App.cs). MousePassthroughWindow
        // is load-bearing on Windows for the GLFW → WS_EX_LAYERED setup
        // (per-pixel transparency); WindowHelper.Setup() clears it right
        // after init so the window still captures clicks.
        Raylib.SetConfigFlags(
            ConfigFlags.UndecoratedWindow |
            ConfigFlags.TransparentWindow |
            ConfigFlags.TopmostWindow |
            ConfigFlags.MousePassthroughWindow |
            ConfigFlags.AlwaysRunWindow);

        IActivity activity = new WorldTeeClassicActivity();

        // PanelSize is fixed for golf (718x428) — no editor-modal-style
        // dynamic resize like the radio has — so we can size the window
        // exactly once at boot and never call SetWindowSize again.
        var size = activity.PanelSize;
        int winW = (int)size.X;
        int winH = (int)size.Y;

        Raylib.InitWindow(winW, winH, WorldTeeClassicActivity.AppTitle);
        Raylib.InitAudioDevice();
        Raylib.SetTargetFPS(60);
        // Esc dismisses in-game dialogs (help overlay, popups) — keep
        // the OS-level "esc quits app" disabled.
        Raylib.SetExitKey(KeyboardKey.Null);

        // Platform-specific transparent-overlay setup: NSWindow level +
        // clearColor on macOS, clear-the-passthrough-but-keep-LAYERED on
        // Windows. After this returns the window is fully transparent
        // outside the activity's drawn pixels and floats above other
        // app windows, just like the pet.
        WindowHelper.Setup();

        // Boot the same 1 kHz click poller the pet uses, and dispense
        // its events through InputManager. Reading
        // Raylib.IsMouseButtonPressed/Released directly here would miss
        // fast click+release pairs on macOS — the long-standing "I have
        // to hold the mouse for clicks to register" complaint. The
        // sibling activity host (MouseHouse.Activities/Program.cs) hit
        // exactly this on chess puzzles; see commit 16ba1db for that fix.
        WindowHelper.StartClickPoller();
        var input = new InputManager();

        // Centre the window on the monitor we landed on.
        int monitor = Raylib.GetCurrentMonitor();
        int monW = Math.Max(800, Raylib.GetMonitorWidth(monitor));
        int monH = Math.Max(600, Raylib.GetMonitorHeight(monitor));
        Raylib.SetWindowPosition((monW - winW) / 2, (monH - winH) / 2);

        activity.Load();

        // Manual title-bar drag — the window is undecorated, so the OS
        // doesn't provide one. Drag zone is the activity's drawn title
        // bar minus the close-X glyph at the right edge. Mirrors
        // MouseHouse.Activities/Program.cs.
        bool dragging = false;
        Vector2 dragGrab = Vector2.Zero;
        int titleBarBottom = FrameInset + RetroWidgets.TitleBarHeight;

        while (!Raylib.WindowShouldClose() && !activity.IsFinished)
        {
            float delta = Raylib.GetFrameTime();
            // Drain the high-rate poller's counters into per-frame edge
            // bools — fires a press for every click that happened since
            // the last frame, even if Raylib's per-frame edge detection
            // would have collapsed a fast click+release into nothing.
            input.Update();
            var rawMouse = Raylib.GetMousePosition();

            // Cmd+Q (macOS) / Ctrl+Q (Win/Linux) → graceful quit. Without
            // a title bar X this is the standard "get me out of here"
            // hotkey alongside the activity's own X-button affordance.
            bool cmd = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? (Raylib.IsKeyDown(KeyboardKey.LeftSuper) || Raylib.IsKeyDown(KeyboardKey.RightSuper))
                : (Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl));
            if (cmd && Raylib.IsKeyPressed(KeyboardKey.Q)) break;

            // OS file drop → forward to the activity (golf uses this
            // for course imports / level edits).
            if (Raylib.IsFileDropped())
            {
                var paths = ReadDroppedFilePaths();
                if (paths.Length > 0) activity.OnFilesDropped(paths);
            }

            bool leftPressed   = input.LeftPressed;
            bool leftReleased  = input.LeftReleased;
            bool rightPressed  = input.RightPressed;

            bool inDragZone = rawMouse.Y >= FrameInset && rawMouse.Y < titleBarBottom
                && rawMouse.X >= FrameInset
                && rawMouse.X < activity.PanelSize.X - FrameInset - CloseBtnZone;

            if (!dragging && leftPressed && inDragZone)
            {
                dragging = true;
                dragGrab = rawMouse;
                // Suppress the press from reaching the activity so it
                // doesn't double-react to the drag-grab click.
                leftPressed = false;
            }
            if (dragging)
            {
                if (leftReleased) { dragging = false; }
                else
                {
                    var winPos = Raylib.GetWindowPosition();
                    var screenMouse = winPos + rawMouse;
                    var newPos = screenMouse - dragGrab;
                    Raylib.SetWindowPosition((int)newPos.X, (int)newPos.Y);
                }
                // Tick the activity even mid-drag so the planner /
                // ball-physics / wildlife animation keep advancing. Pass
                // cleared input edges so the activity doesn't react to
                // the click the host has already consumed.
                activity.Update(delta, rawMouse, Vector2.Zero,
                    leftPressed: false, leftReleased: false, rightPressed: false);
            }
            else
            {
                activity.Update(delta, rawMouse, Vector2.Zero,
                    leftPressed, leftReleased, rightPressed);
            }

            Raylib.BeginDrawing();
            // Blank clear — no solid background. The activity's chrome
            // and canvas draw over transparent pixels, so the panel
            // appears to float on the desktop just like the pet.
            Raylib.ClearBackground(Color.Blank);
            activity.Draw(Vector2.Zero);
            Raylib.EndDrawing();
        }

        activity.Close();
        Raylib.CloseAudioDevice();
        Raylib.CloseWindow();
        return 0;
    }

    private static unsafe string[] ReadDroppedFilePaths()
    {
        var list = Raylib.LoadDroppedFiles();
        var result = new string[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            result[i] = System.Runtime.InteropServices.Marshal
                .PtrToStringUTF8((IntPtr)list.Paths[i]) ?? "";
        }
        Raylib.UnloadDroppedFiles(list);
        return result;
    }
}
