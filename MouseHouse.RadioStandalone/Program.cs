using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;
using MouseHouse.UI;

namespace MouseHouse.RadioStandalone;

/// <summary>
/// Entry point for the standalone "Crabby Radio" build distributed
/// independently from the desktop pet (e.g. on itch.io). Hosts a single
/// <see cref="RadioWidget"/> in a normal decorated OS window — no
/// transparent overlay, no topmost flag, no pet code at all. Source is
/// shared with the main pet via the .csproj's Compile globs, so radio
/// improvements made for the pet flow into this binary on next build
/// and vice versa.
/// </summary>
internal static class Program
{
    private const string WindowTitle = "Crabby Radio";

    // The widget itself is 256×244 with the editor closed; when the user
    // opens the station library editor the widget reports a 400×450 panel
    // so the modal fits. We size the window for the larger case once at
    // boot and never resize — simpler than tracking PanelSize and
    // calling SetWindowSize every frame (which on macOS shifts the
    // viewport mid-frame and throws mouse coords off).
    private const int WinW = 400;
    private const int WinH = 450;

    public static int Main(string[] _)
    {
        // Critical: switch the persistence root BEFORE any save/load so
        // the standalone gets its own %APPDATA%\CrabbyRadio (etc.) and
        // doesn't share stations.json / radio.json with a CrabbyCS
        // install on the same machine.
        SaveManager.AppFolderName = "CrabbyRadio";

        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow);
        Raylib.InitWindow(WinW, WinH, WindowTitle);
        Raylib.InitAudioDevice();
        Raylib.SetTargetFPS(60);
        Raylib.SetExitKey(KeyboardKey.Null);  // Esc shouldn't quit the app

        var player = new RadioPlayer();
        var widget = new RadioWidget(player) { Visible = true };
        // Position is the widget's top-left in window coords; pin to (0,0)
        // so the widget's drawn title bar lines up with the OS frame.
        widget.Position = Vector2.Zero;

        // Load persisted station/volume/viz from the standalone's own
        // radio.json (lives under %APPDATA%\CrabbyRadio because of the
        // AppFolderName set above).
        var prefs = SaveManager.LoadOrDefault<RadioPrefs>(RadioPrefs.Filename);
        widget.Restore(prefs.StationIdx, prefs.Volume, prefs.VizMode);
        // Persist on every state change (station next/prev, volume drag,
        // viz cycle) so a force-quit doesn't lose the latest settings.
        widget.StateChanged = () => SavePrefs(widget);

        while (!Raylib.WindowShouldClose() && widget.Visible)
        {
            // Audio pump must run every frame so playback never starves.
            player.Pump();

            float delta = Raylib.GetFrameTime();
            var mouse = Raylib.GetMousePosition();
            // Centre the widget in the window so it works at any window
            // size the user resizes to. The widget reports its desired
            // panel size via PanelSize-style consts (W, H + editor case).
            int panelW = widget.IsEditorOpen ? 400 : RadioWidget.W;
            int panelH = widget.IsEditorOpen ? 450 : RadioWidget.H;
            int winW = Raylib.GetRenderWidth();
            int winH = Raylib.GetRenderHeight();
            widget.Position = new Vector2(
                MathF.Max(0, (winW - panelW) / 2f),
                MathF.Max(0, (winH - panelH) / 2f));

            bool leftPressed   = Raylib.IsMouseButtonPressed(MouseButton.Left);
            bool leftReleased  = Raylib.IsMouseButtonReleased(MouseButton.Left);
            bool rightPressed  = Raylib.IsMouseButtonPressed(MouseButton.Right);
            widget.Update(delta, mouse, leftPressed, leftReleased, rightPressed);

            Raylib.BeginDrawing();
            // Solid desktop fill behind the widget so the standalone
            // looks like a normal app rather than a floating panel —
            // matches the colour the retro skin uses for its desktop.
            Raylib.ClearBackground(RetroSkin.Desktop);
            widget.Draw();
            Raylib.EndDrawing();
        }

        SavePrefs(widget);
        player.Stop();
        Raylib.CloseAudioDevice();
        Raylib.CloseWindow();
        return 0;
    }

    private static void SavePrefs(RadioWidget widget)
    {
        SaveManager.Save(RadioPrefs.Filename, new RadioPrefs
        {
            StationIdx = widget.StationIndex,
            Volume = widget.Volume,
            VizMode = widget.VizMode,
        });
    }

    private class RadioPrefs
    {
        public const string Filename = "radio.json";
        public int StationIdx { get; set; }
        public float Volume { get; set; } = 0.6f;
        public int VizMode { get; set; }
    }
}
