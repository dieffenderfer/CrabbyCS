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
    // Horizontal exclusion zone covering both the minimize button and
    // the close-X at the right edge of the title bar — clicks in this
    // strip belong to the activity's close handler or the host's
    // minimize interception (which iconifies via Raylib.MinimizeWindow
    // before forwarding to the activity), not the window-drag handler.
    private const int CloseBtnZone = 44;
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

        // Load retro theme prefs BEFORE building the activity so its first
        // draw uses the user's saved palette, not the RetroSkin default.
        var prefs = SaveManager.LoadOrDefault<GolfPrefs>(GolfPrefs.Filename);
        if (!string.IsNullOrEmpty(prefs.RetroThemeName))
            RetroSkin.SetTheme(prefs.RetroThemeName);

        var golf = new WorldTeeClassicActivity();
        // Title-bar right-click theme picker: persist the choice locally
        // so the game boots into the same theme next launch, and broadcast
        // via ThemeSync in case anything else under this save folder is
        // listening.
        golf.ThemeCommitted = name =>
        {
            SaveManager.Save(GolfPrefs.Filename, new GolfPrefs { RetroThemeName = name });
            MouseHouse.Core.ThemeSync.Write(name);
        };
        IActivity activity = golf;

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

            // Minimize → iconify to the OS dock. Intercept before
            // the activity sees the click so the underscore button
            // does its job instead of getting eaten by drag detection.
            var activityTitleBar = new Rectangle(FrameInset, FrameInset,
                activity.PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
            if (leftPressed
                && RetroWidgets.MinimizeHitTest(activityTitleBar, rawMouse, true))
            {
                Raylib.MinimizeWindow();
                leftPressed = false;
            }

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
        // Save the (possibly-updated) theme on the way out. The
        // title-bar picker saves on every commit, but this catches the
        // edge case where SetTheme was driven by something else (e.g. a
        // future migration writing the file directly).
        SaveManager.Save(GolfPrefs.Filename, new GolfPrefs
        {
            RetroThemeName = RetroSkin.Current.Name,
        });
        Raylib.CloseAudioDevice();
        Raylib.CloseWindow();
        return 0;
    }

    /// <summary>
    /// Tiny persisted-state record for the standalone build. The activity
    /// has its own <c>world_tee_classic.json</c> for in-game prefs
    /// (palette, beaten regions, etc.) — this file is just for shell-level
    /// state owned by the host, not the activity. Kept separate so the
    /// activity's JSON schema doesn't need to know about the standalone.
    /// </summary>
    private class GolfPrefs
    {
        public const string Filename = "standalone.json";
        public string RetroThemeName { get; set; } = "";
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
