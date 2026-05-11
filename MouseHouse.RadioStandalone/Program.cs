using System.Numerics;
using System.Runtime.InteropServices;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;
using MouseHouse.UI;

namespace MouseHouse.RadioStandalone;

/// <summary>
/// Entry point for the standalone "Crabby Radio" build distributed
/// independently from the desktop pet (e.g. on itch.io). Hosts a single
/// <see cref="RadioWidget"/> as a transparent, undecorated, always-on-top
/// window so the radio appears to float directly on the user's desktop —
/// the same on-the-desktop feel as the CrabbyCS pet, without a regular
/// OS frame around it. Source is shared with the main pet via the
/// .csproj's Compile globs, so radio improvements made for the pet flow
/// into this binary on next build and vice versa.
/// </summary>
internal static class Program
{
    private const string WindowTitle = "Crabby Radio";

    public static int Main(string[] _)
    {
        // Switch the persistence root BEFORE any save/load so the
        // standalone gets its own %APPDATA%\CrabbyRadio (etc.) and
        // doesn't share stations.json / radio.json with a CrabbyCS
        // install on the same machine.
        SaveManager.AppFolderName = "CrabbyRadio";

        // Same flag set the pet uses (App.cs). MousePassthroughWindow
        // looks contradictory — we want to capture clicks, not pass them
        // through — but on Windows it's load-bearing: GLFW only attaches
        // WS_EX_LAYERED (the layered-window style required for per-pixel
        // transparency) when MousePassthroughWindow is in the initial
        // config flags. WindowHelper.Setup() clears the passthrough state
        // a few lines down once the layered attributes are in place.
        Raylib.SetConfigFlags(
            ConfigFlags.UndecoratedWindow |
            ConfigFlags.TransparentWindow |
            ConfigFlags.TopmostWindow |
            ConfigFlags.MousePassthroughWindow |
            ConfigFlags.AlwaysRunWindow);

        // Boot at the smaller "no editor open" panel size; resize when
        // the user opens the station editor. Hugging the actual content
        // matters more here than for a normal-windowed app — the empty
        // space around the widget would otherwise be transparent dead
        // zone the user can't click through.
        int winW = RadioWidget.W;
        int winH = RadioWidget.H;
        Raylib.InitWindow(winW, winH, WindowTitle);
        Raylib.InitAudioDevice();
        Raylib.SetTargetFPS(60);
        // Esc dismisses the station editor modal — don't let it quit
        // the app out from under the user.
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

        var player = new RadioPlayer();
        var widget = new RadioWidget(player) { Visible = true };
        // Pin the widget flush against the OS window's origin so its
        // drawn title bar lines up with the (transparent) top of the
        // window — the manual-drag hit-test below assumes this.
        widget.Position = Vector2.Zero;

        var prefs = SaveManager.LoadOrDefault<RadioPrefs>(RadioPrefs.Filename);
        widget.Restore(prefs.StationIdx, prefs.Volume, prefs.VizMode);
        // Apply saved retro theme before the first draw so the title bar /
        // chrome boot in the user's chosen palette, not the default.
        if (!string.IsNullOrEmpty(prefs.RetroThemeName))
            RetroSkin.SetTheme(prefs.RetroThemeName);
        widget.StateChanged = () => SavePrefs(widget, RetroSkin.Current.Name);
        // Title-bar right-click theme picker: persist the chosen theme
        // and broadcast it to the radio station editor sibling (which
        // shares this SaveManager folder, so ThemeSync.Write lands in
        // a file the editor process polls).
        widget.ThemeCommitted = name =>
        {
            SavePrefs(widget, name);
            MouseHouse.Core.ThemeSync.Write(name);
        };
        // The radio panel is only 244 px tall — not enough room for the
        // 12-row theme popup. Grow the OS window while the dropdown is
        // open and snap back when it closes, so the menu has space to
        // extend below the title bar.
        bool lastThemeOpen = false;

        // Manual title-bar drag — the window is undecorated, so the OS
        // doesn't provide one. Mirrors MouseHouse.Activities/Program.cs:
        // detect mousedown in the drawn title-bar zone (excluding the
        // close-X glyph), then slide the OS window with the cursor.
        bool dragging = false;
        Vector2 dragGrab = Vector2.Zero;
        const int CloseBtnZone = 22;

        while (!Raylib.WindowShouldClose() && widget.Visible)
        {
            player.Pump();

            // Cmd+Q (macOS) / Ctrl+Q (Win/Linux) → graceful quit. Without
            // a title bar X this is the standard "get me out of here"
            // hotkey alongside the widget's own X-button affordance.
            bool cmd = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? (Raylib.IsKeyDown(KeyboardKey.LeftSuper) || Raylib.IsKeyDown(KeyboardKey.RightSuper))
                : (Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl));
            if (cmd && Raylib.IsKeyPressed(KeyboardKey.Q)) widget.Visible = false;

            float delta = Raylib.GetFrameTime();
            // Drain the high-rate poller's counters into per-frame edge
            // bools — fires a press for every click that happened since
            // the last frame, even if Raylib's per-frame edge detection
            // would have collapsed a fast click+release into nothing.
            input.Update();
            var rawMouse = Raylib.GetMousePosition();
            bool leftPressed   = input.LeftPressed;
            bool leftReleased  = input.LeftReleased;
            bool rightPressed  = input.RightPressed;

            // Drag zone: title-bar strip across the top of the widget,
            // minus the close-X glyph at the right edge so clicks on it
            // still close the window.
            bool inDragZone = rawMouse.Y >= 0
                && rawMouse.Y < RetroWidgets.TitleBarHeight
                && rawMouse.X >= 0
                && rawMouse.X < RadioWidget.W - CloseBtnZone;

            if (!dragging && leftPressed && inDragZone)
            {
                dragging = true;
                dragGrab = rawMouse;
                // Suppress the press from reaching the widget so it
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
                // Mid-drag, tick widget with cleared input edges so audio
                // keeps streaming but the widget doesn't react to clicks.
                widget.Update(delta, rawMouse,
                    leftPressed: false, leftReleased: false, rightPressed: false);
            }
            else
            {
                widget.Update(delta, rawMouse, leftPressed, leftReleased, rightPressed);
            }

            // Resize the OS window to make room for the theme dropdown. The
            // 244 px default is too short — the menu would clip below the
            // window frame. The legacy editor-resize was flaky, but a small
            // height-only grow with no width change is much safer.
            if (widget.IsThemeMenuOpen != lastThemeOpen)
            {
                lastThemeOpen = widget.IsThemeMenuOpen;
                int targetH = lastThemeOpen ? RadioWidget.H + 220 : RadioWidget.H;
                Raylib.SetWindowSize(RadioWidget.W, targetH);
            }

            // (No window resize: the panel is now fixed at RadioWidget.W
            // × RadioWidget.H. The legacy inline station editor used to
            // grow the panel to 400×450 on open, but that's been retired
            // in favour of launching stations.json in the user's text
            // editor on Shift+Right-Click — sidesteps the dynamic resize
            // entirely, which was the source of Windows positioning bugs
            // and flaky open behaviour in the transparent overlay.)

            Raylib.BeginDrawing();
            // Blank clear — no solid background. The widget's chrome
            // draws over transparent pixels, so it appears to float on
            // the desktop just like the pet.
            Raylib.ClearBackground(Color.Blank);
            widget.Draw();
            Raylib.EndDrawing();
        }

        SavePrefs(widget, RetroSkin.Current.Name);
        player.Stop();
        Raylib.CloseAudioDevice();
        Raylib.CloseWindow();
        return 0;
    }

    private static void SavePrefs(RadioWidget widget, string themeName)
    {
        SaveManager.Save(RadioPrefs.Filename, new RadioPrefs
        {
            StationIdx = widget.StationIndex,
            Volume = widget.Volume,
            VizMode = widget.VizMode,
            RetroThemeName = themeName,
        });
    }

    private class RadioPrefs
    {
        public const string Filename = "radio.json";
        public int StationIdx { get; set; }
        public float Volume { get; set; } = 0.6f;
        public int VizMode { get; set; }
        /// <summary>Persisted retro theme name (matches RetroTheme.Name).
        /// Empty string means "use the RetroSkin default".</summary>
        public string RetroThemeName { get; set; } = "";
    }
}
