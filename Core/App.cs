using System.Numerics;
using Raylib_cs;

namespace Crabby.Core;

/// <summary>
/// Main application class. Manages the fullscreen transparent overlay window
/// and the core game loop.
/// </summary>
public class App
{
    private const int TARGET_FPS = 60;
    private const string WINDOW_TITLE = "Crabby";

    public int ScreenWidth { get; private set; }
    public int ScreenHeight { get; private set; }

    public void Run()
    {
        // Configure window flags BEFORE InitWindow
        Raylib.SetConfigFlags(
            ConfigFlags.UndecoratedWindow |
            ConfigFlags.TransparentWindow |
            ConfigFlags.TopmostWindow |
            ConfigFlags.MousePassthroughWindow |
            ConfigFlags.AlwaysRunWindow
        );

        // Get monitor size for fullscreen overlay
        // We need to init a small window first to query monitor info on some platforms
        Raylib.InitWindow(1, 1, WINDOW_TITLE);

        int monitor = Raylib.GetCurrentMonitor();
        ScreenWidth = Raylib.GetMonitorWidth(monitor);
        ScreenHeight = Raylib.GetMonitorHeight(monitor);

        // Resize to cover the full screen
        Raylib.SetWindowSize(ScreenWidth, ScreenHeight);
        Raylib.SetWindowPosition(0, 0);

        Raylib.SetTargetFPS(TARGET_FPS);

        // Platform-specific setup (click-through, etc.)
        WindowHelper.Setup();

        // Demo: draw a red circle that follows the mouse to prove transparency works
        while (!Raylib.WindowShouldClose())
        {
            Update();
            Draw();
        }

        Raylib.CloseWindow();
    }

    private void Update()
    {
        // ESC to quit (temporary, for development)
    }

    private void Draw()
    {
        Raylib.BeginDrawing();

        // Transparent background - this is the key to the overlay
        Raylib.ClearBackground(Color.Blank);

        // Demo: draw a small crab-colored square at a fixed position
        // to verify transparency and rendering work
        Raylib.DrawRectangle(100, 100, 64, 64, Color.Orange);
        Raylib.DrawText("Crabby C#", 100, 170, 20, Color.White);

        // Draw a circle at mouse position to verify input works
        Vector2 mouse = Raylib.GetMousePosition();
        Raylib.DrawCircleV(mouse, 8, new Color(255, 100, 50, 180));

        Raylib.EndDrawing();
    }
}
