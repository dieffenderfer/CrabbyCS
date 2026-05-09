using System.Runtime.InteropServices;
using Raylib_cs;
using MouseHouse.Data;
using MouseHouse.Net;
using MouseHouse.Scenes.DesktopPet;

namespace MouseHouse.Core;

/// <summary>
/// Main application class. Manages the fullscreen transparent overlay window
/// and the core game loop.
/// </summary>
public class App
{
    private const int TARGET_FPS = 60;
    private const string WINDOW_TITLE = "Mouse House";

    /// <summary>Logical screen dimensions (physical / UIScaling.Factor).</summary>
    public int ScreenWidth { get; private set; }
    public int ScreenHeight { get; private set; }

    /// <summary>Physical render dimensions before UI scaling.</summary>
    public static int PhysicalWidth { get; private set; }
    public static int PhysicalHeight { get; private set; }

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

        // Resize to cover the full screen. On Windows, use 1 px less so DWM
        // doesn't treat the borderless window as a fullscreen exclusive surface
        // — that would disable composition and break per-pixel transparency.
        int winW = monW;
        int winH = monH;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            winW = monW - 1;
            winH = monH - 1;
        }
        Raylib.SetWindowSize(winW, winH);
        Raylib.SetWindowPosition(0, 0);
        Raylib.SetTargetFPS(TARGET_FPS);

        // On macOS Retina, GetMonitorWidth returns physical pixels but GLFW
        // works in screen coordinates. Use GetRenderWidth after window setup
        // to get the actual framebuffer/coordinate space we draw in.
        PhysicalWidth = Raylib.GetRenderWidth();
        PhysicalHeight = Raylib.GetRenderHeight();

        // Load the persisted UI scale. Default to 2x on Windows (pixel-art
        // assets are tiny on modern high-res monitors) and 1x elsewhere.
        var earlySettings = PetSettings.Load();
        UIScaling.Factor = earlySettings.UIScaleOverride > 0.01f
            ? earlySettings.UIScaleOverride
            : (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 2f : 1f);
        ScreenWidth = (int)(PhysicalWidth / UIScaling.Factor);
        ScreenHeight = (int)(PhysicalHeight / UIScaling.Factor);

        // Platform-specific setup
        WindowHelper.Setup();
        WindowHelper.StartClickPoller();

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
            Rlgl.PushMatrix();
            Rlgl.Scalef(UIScaling.Factor, UIScaling.Factor, 1);
            _petScene.Draw();
            Rlgl.PopMatrix();
            Raylib.EndDrawing();
        }

        _multiplayer.Disconnect();
        _assets.UnloadAll();
        Raylib.CloseAudioDevice();
        Raylib.CloseWindow();
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
