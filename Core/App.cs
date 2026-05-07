using System.Numerics;
using Raylib_cs;
using MouseHouse.Net;
using MouseHouse.Scenes.DesktopPet;
using MouseHouse.Scenes.DesktopPet.Cheese;

namespace MouseHouse.Core;

/// <summary>
/// Main application class. Manages the fullscreen transparent overlay window
/// and the core game loop.
/// </summary>
public class App
{
    private const int TARGET_FPS = 60;
    private const string WINDOW_TITLE = "Mouse House";

    public int ScreenWidth { get; private set; }
    public int ScreenHeight { get; private set; }

    private AssetCache _assets = null!;
    private InputManager _input = null!;
    private AudioManager _audio = null!;
    private MultiplayerManager _multiplayer = null!;
    private DesktopPetScene _petScene = null!;

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

        // Init a small window first to query monitor info
        Raylib.InitWindow(1, 1, WINDOW_TITLE);
        Raylib.InitAudioDevice();

        // Disable Raylib's default "exit on Escape" behavior — the pet scene uses
        // Escape to close open activities (Paint, Solitaire, etc.), and we don't
        // want the whole app to quit when closing a mini-app.
        Raylib.SetExitKey(KeyboardKey.Null);

        int monitor = Raylib.GetCurrentMonitor();
        int monW = Raylib.GetMonitorWidth(monitor);
        int monH = Raylib.GetMonitorHeight(monitor);

        // Resize to cover the full screen
        Raylib.SetWindowSize(monW, monH);
        Raylib.SetWindowPosition(0, 0);
        Raylib.SetTargetFPS(TARGET_FPS);

        // On macOS Retina, GetMonitorWidth returns physical pixels but GLFW
        // works in screen coordinates. Use GetRenderWidth after window setup
        // to get the actual framebuffer/coordinate space we draw in.
        ScreenWidth = Raylib.GetRenderWidth();
        ScreenHeight = Raylib.GetRenderHeight();

        // Platform-specific setup
        WindowHelper.Setup();

        // Splash: paint a big cheddar in the middle of the screen so the user
        // gets a visible "loading" cue while sprite sheets / audio decode.
        // The dotnet restore step before the binary starts is outside our
        // control, but everything after this point is.
        DrawSplash();

        // Resolve asset path relative to the executable
        var exeDir = AppContext.BaseDirectory;
        // During development with `dotnet run`, assets are in the project root
        var assetBase = FindAssetBase(exeDir);
        _assets = new AssetCache(assetBase);
        _input = new InputManager();
        _audio = new AudioManager(_assets);

        // Multiplayer: enabled by default, uses ENet if available, falls back to offline
        _multiplayer = new MultiplayerManager(enabled: true);

        // Create and load the desktop pet scene
        _petScene = new DesktopPetScene(_assets, _input, _audio, _multiplayer, ScreenWidth, ScreenHeight);
        _petScene.Load();

        // Main loop
        while (!Raylib.WindowShouldClose())
        {
            float delta = Raylib.GetFrameTime();
            _input.Update();
            _multiplayer.Update(delta);
            _petScene.Update(delta);

            Raylib.BeginDrawing();
            Raylib.ClearBackground(Color.Blank);
            _petScene.Draw();
            Raylib.EndDrawing();
        }

        _multiplayer.Disconnect();
        _assets.UnloadAll();
        Raylib.CloseAudioDevice();
        Raylib.CloseWindow();
    }

    private void DrawSplash()
    {
        // Solid-pixel cheddar drawn at a chunky scale; full-grid permutation
        // (no cells hidden) so it shows the cheese complete.
        int n = CheeseSprites.CheddarCellCount;
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;

        Raylib.BeginDrawing();
        Raylib.ClearBackground(Color.Blank);
        var center = new Vector2(ScreenWidth / 2f, ScreenHeight / 2f);
        // Roughly 1/4 of the screen height tall — large enough to read as a
        // splash on any monitor, small enough not to feel obnoxious.
        float scale = MathF.Max(6f, ScreenHeight / 60f);
        CheeseSprites.DrawCheddarDissolve(center, scale, order, hideCount: 0);
        Raylib.EndDrawing();
    }

    /// <summary>
    /// Walk up from exeDir looking for an "assets" folder.
    /// </summary>
    private static string FindAssetBase(string startDir)
    {
        var dir = startDir;
        for (int i = 0; i < 8; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "assets")))
                return dir;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        // Fallback: assume current working directory
        return Directory.GetCurrentDirectory();
    }
}
