using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;
using MouseHouse.UI;

namespace MouseHouse.RadioStationEditor;

/// <summary>
/// Entry point for the dedicated station-library editor process.
/// Spawned by the radio (standalone build OR the pet's radio companion)
/// via <see cref="MouseHouse.Data.RadioStations.OpenInExternalEditor"/>.
/// Hosts the existing <see cref="RadioStationEditor"/> UI in a normal
/// decorated OS window — solid background, native title-bar drag,
/// native close button — so it never has to grow the radio's
/// transparent-overlay window the way the old inline modal did.
///
/// CLI: <c>--app-folder=&lt;name&gt;</c> tells SaveManager which
/// %APPDATA%\&lt;name&gt;\stations.json to read/write. The launching
/// radio passes its own AppFolderName so both processes edit the same
/// file. Defaults to "MouseHouse" if the arg is missing (the pet's
/// folder), which is the right fallback for someone who runs this
/// exe directly without going through the radio.
/// </summary>
internal static class Program
{
    private const int WinW = 400;
    private const int WinH = 450;

    public static int Main(string[] args)
    {
        string appFolder = "MouseHouse";
        foreach (var arg in args)
        {
            if (arg.StartsWith("--app-folder=")) appFolder = arg["--app-folder=".Length..];
        }
        // CRITICAL: must run before any RadioStations access so the
        // first EnsureLoaded() reads from the right file.
        SaveManager.AppFolderName = appFolder;

        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow);
        Raylib.InitWindow(WinW, WinH, $"Stations — {appFolder}");
        Raylib.SetTargetFPS(60);
        // Esc closes the editor (handled inside the editor's Update —
        // it sets IsOpen=false, which our loop sees and exits). Keep
        // the OS-level "esc quits" disabled so a stray Esc inside a
        // text field doesn't quit unexpectedly.
        Raylib.SetExitKey(KeyboardKey.Null);

        // 1 kHz click poller + InputManager — same plumbing the radio
        // and golf standalones use to dodge Raylib's per-frame edge
        // detection missing fast click+release pairs on macOS. Without
        // this, quick clicks on tiny editor buttons require a hold.
        WindowHelper.StartClickPoller();
        var input = new InputManager();

        var editor = new UI.RadioStationEditor();
        editor.Open();

        // Pick up the active retro theme broadcast by the radio process.
        // ThemeSync writes a tiny theme_sync.txt into the same SaveManager
        // folder we share — read it once on boot so the editor opens in
        // the right palette, then poll per-frame so live theme changes
        // (from the radio's right-click title-bar menu) apply instantly.
        var lastThemeMtime = DateTime.MinValue;
        var bootTheme = MouseHouse.Core.ThemeSync.Poll(ref lastThemeMtime);
        if (bootTheme != null) RetroSkin.SetTheme(bootTheme);

        while (!Raylib.WindowShouldClose() && editor.IsOpen)
        {
            float delta = Raylib.GetFrameTime();
            input.Update();
            var newTheme = MouseHouse.Core.ThemeSync.Poll(ref lastThemeMtime);
            if (newTheme != null) RetroSkin.SetTheme(newTheme);
            var mouse = Raylib.GetMousePosition();
            int sw = Raylib.GetRenderWidth();
            int sh = Raylib.GetRenderHeight();
            editor.Update(delta, mouse,
                input.LeftPressed, input.LeftReleased, input.RightPressed,
                sw, sh);

            Raylib.BeginDrawing();
            // Solid retro-desktop fill behind the editor panel — we're
            // a normal decorated window here, not a transparent overlay,
            // so the background is meant to be opaque.
            Raylib.ClearBackground(RetroSkin.Desktop);
            editor.Draw(sw, sh);
            Raylib.EndDrawing();
        }

        Raylib.CloseWindow();
        return 0;
    }
}
