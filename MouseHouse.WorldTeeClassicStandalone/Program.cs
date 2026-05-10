using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities;

namespace MouseHouse.WorldTeeClassicStandalone;

/// <summary>
/// Entry point for the standalone "Ohio Golf" / World Tee Classic build
/// distributed independently from the desktop pet (e.g. on itch.io).
/// Hosts a single <see cref="WorldTeeClassicActivity"/> in a normal
/// decorated OS window — no transparent overlay, no topmost flag, no
/// pet code at all. Source is shared with the main pet via the
/// .csproj's Compile globs, so improvements made for the pet flow into
/// this binary on next build and vice versa.
/// </summary>
internal static class Program
{
    public static int Main(string[] _)
    {
        // Switch the persistence root BEFORE any save/load so the
        // standalone gets its own %APPDATA%\WorldTeeClassic\ etc. and
        // doesn't share world_tee_classic.json / courses/ / Moon-unlock
        // state with a CrabbyCS install on the same machine.
        SaveManager.AppFolderName = "WorldTeeClassic";

        // Typed as IActivity so default-interface methods like
        // OnFilesDropped resolve through the interface table — calling
        // them on the concrete type would skip the default body.
        IActivity activity = new WorldTeeClassicActivity();

        // Query PanelSize BEFORE InitWindow so the OS window is born at
        // the exact dimensions the activity expects. Resizing post-init
        // on macOS leaves Raylib's viewport mapped to the original size,
        // which throws mouse hit-tests off — the same gotcha the
        // companion-process host (MouseHouse.Activities/Program.cs) calls
        // out for the same reason.
        var size = activity.PanelSize;
        int winW = (int)size.X;
        int winH = (int)size.Y;

        // Decorated, non-topmost. The OS provides a real title bar +
        // close button, so dragging is native and the OS X triggers
        // Raylib.WindowShouldClose() → graceful quit. The activity's
        // own drawn title-bar X also still works (sets IsFinished).
        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow);
        Raylib.InitWindow(winW, winH, WorldTeeClassicActivity.AppTitle);
        Raylib.InitAudioDevice();
        Raylib.SetTargetFPS(60);
        // Esc shouldn't quit the app — the activity uses it for in-game
        // dialog dismissal (help overlay, popups).
        Raylib.SetExitKey(KeyboardKey.Null);

        activity.Load();

        while (!Raylib.WindowShouldClose() && !activity.IsFinished)
        {
            float delta = Raylib.GetFrameTime();
            var mouse = Raylib.GetMousePosition();

            // OS file drop (Finder drag, etc.) → forward to the activity.
            // Golf uses this for course imports / level edits.
            if (Raylib.IsFileDropped())
            {
                var paths = ReadDroppedFilePaths();
                if (paths.Length > 0) activity.OnFilesDropped(paths);
            }

            bool leftPressed   = Raylib.IsMouseButtonPressed(MouseButton.Left);
            bool leftReleased  = Raylib.IsMouseButtonReleased(MouseButton.Left);
            bool rightPressed  = Raylib.IsMouseButtonPressed(MouseButton.Right);

            // Render the activity flush against the window origin —
            // panelOffset = (0,0) — so panel-local input coords match
            // raw window-local mouse coords without remapping.
            activity.Update(delta, mouse, Vector2.Zero,
                leftPressed, leftReleased, rightPressed);

            Raylib.BeginDrawing();
            // Solid desktop fill behind the activity panel matches the
            // 90s desktop look the retro chrome targets.
            Raylib.ClearBackground(MouseHouse.Scenes.Activities.Retro.RetroSkin.Desktop);
            activity.Draw(Vector2.Zero);
            Raylib.EndDrawing();
        }

        activity.Close();
        Raylib.CloseAudioDevice();
        Raylib.CloseWindow();
        return 0;
    }

    // Marshal a Raylib FilePathList → managed string[] of UTF-8 paths
    // and unload the native list so we don't leak. Mirrors the helper
    // in MouseHouse.Activities/Program.cs.
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
