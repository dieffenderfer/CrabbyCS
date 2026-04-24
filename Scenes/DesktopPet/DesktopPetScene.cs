using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Data;
using MouseHouse.Net;
using MouseHouse.Rendering;
using MouseHouse.Scenes.Activities;
using MouseHouse.Scenes.BeachHub;
using MouseHouse.Scenes.DesktopPet.Events;
using MouseHouse.UI;

namespace MouseHouse.Scenes.DesktopPet;

/// <summary>
/// Main desktop pet scene. Renders the mouse on a fullscreen transparent overlay.
/// </summary>
public class DesktopPetScene
{
    private readonly AssetCache _assets;
    private readonly InputManager _input;
    private readonly AudioManager _audio;
    private readonly MultiplayerManager _mp;
    private readonly PetStateMachine _pet;
    private readonly PopupMenu _menu;
    private readonly int _screenWidth;
    private readonly int _screenHeight;
    private PetSettings _settings;
    private EventManager _events = null!;

    // Active activity (rendered as a draggable panel)
    private IActivity? _activeActivity;
    private Vector2 _activityOffset; // top-left corner of the activity panel
    private bool _draggingActivity;
    private Vector2 _activityDragOffset;
    private const int ActivityTitleBarHeight = 28;

    // Color mode spritesheets
    private readonly Dictionary<string, SpriteSheetSet> _colorModes = new();

    // Status bubble above pet
    private readonly StatusBubble _statusBubble = new();

    // Track whether mouse is over an interactive element
    private bool _mouseOverPet;
    private bool _mouseOverUI;

    // Time update throttle
    private float _timeUpdateTimer;

    public DesktopPetScene(AssetCache assets, InputManager input, AudioManager audio, MultiplayerManager multiplayer, int screenWidth, int screenHeight)
    {
        _assets = assets;
        _input = input;
        _audio = audio;
        _mp = multiplayer;
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
        _pet = new PetStateMachine();
        _menu = new PopupMenu();
        _menu.OnItemSelected += OnMenuItemSelected;
        _settings = PetSettings.Load();
    }

    public void Load()
    {
        _colorModes["2color"] = new SpriteSheetSet
        {
            Walk = _assets.GetSpriteSheetWithAlpha("assets/sprites/pets/mouse_walk.png", 8),
            Idle = _assets.GetSpriteSheetWithAlpha("assets/sprites/pets/mouse_idle.png", 8),
            Sleep = _assets.GetSpriteSheetWithAlpha("assets/sprites/pets/mouse_sleep.png", 12),
            SleepLoop = _assets.GetSpriteSheetWithAlpha("assets/sprites/pets/mouse_sleep_loop.png", 3),
            Jump = _assets.GetSpriteSheetWithAlpha("assets/sprites/pets/mouse_jump.png", 8),
        };

        _colorModes["1color"] = new SpriteSheetSet
        {
            Walk = _assets.GetSpriteSheetWithAlpha("assets/sprites/pets/mouse_1c_walk.png", 8),
            Idle = _assets.GetSpriteSheetWithAlpha("assets/sprites/pets/mouse_1c_idle.png", 8),
            Sleep = _assets.GetSpriteSheetWithAlpha("assets/sprites/pets/mouse_1c_sleep.png", 12),
            SleepLoop = _assets.GetSpriteSheetWithAlpha("assets/sprites/pets/mouse_1c_sleep_loop.png", 3),
            Jump = _assets.GetSpriteSheetWithAlpha("assets/sprites/pets/mouse_1c_jump.png", 8),
        };

        _colorModes["fullcolor"] = new SpriteSheetSet
        {
            Walk = _assets.GetSpriteSheetWithAlpha("assets/sprites/pets/mouse_fc_walk.png", 8),
            Idle = _assets.GetSpriteSheetWithAlpha("assets/sprites/pets/mouse_fc_idle.png", 8),
            Sleep = _assets.GetSpriteSheetWithAlpha("assets/sprites/pets/mouse_fc_sleep.png", 12),
            SleepLoop = _assets.GetSpriteSheetWithAlpha("assets/sprites/pets/mouse_fc_sleep_loop.png", 3),
            Jump = _assets.GetSpriteSheetWithAlpha("assets/sprites/pets/mouse_fc_jump.png", 8),
        };

        ApplyColorMode(_settings.ColorMode);

        if (_settings.ScaleOverride > 0.01f)
            _pet.Scale = _settings.ScaleOverride;

        _audio.Muted = _settings.Muted;

        _events = new EventManager(_assets, _screenWidth, _screenHeight);
        _events.SetColorMode(_settings.ColorMode);

        _pet.Init(_screenWidth, _screenHeight);
        TimeSystem.Update();
    }

    private void ApplyColorMode(string mode)
    {
        if (!_colorModes.TryGetValue(mode, out var sheets))
            sheets = _colorModes["2color"];

        _pet.WalkSheet = sheets.Walk;
        _pet.IdleSheet = sheets.Idle;
        _pet.SleepSheet = sheets.Sleep;
        _pet.SleepLoopSheet = sheets.SleepLoop;
        _pet.JumpSheet = sheets.Jump;
        _pet.RefreshActiveSheet();
    }

    public void Update(float delta)
    {
        TweenSystem.Update(delta);

        _timeUpdateTimer += delta;
        if (_timeUpdateTimer >= 30f)
        {
            _timeUpdateTimer = 0;
            TimeSystem.Update();
        }

        var mousePos = _input.MousePosition;
        bool activityConsumed = false;

        // Handle activity panel if one is open
        if (_activeActivity != null)
        {
            var panelRect = new Rectangle(_activityOffset.X, _activityOffset.Y,
                _activeActivity.PanelSize.X, _activeActivity.PanelSize.Y);
            bool mouseOverPanel = Raylib.CheckCollisionPointRec(mousePos, panelRect);

            // ESC closes the activity
            if (_input.IsKeyPressed(KeyboardKey.Escape))
            {
                CloseActivity();
                activityConsumed = true;
            }
            else
            {
                // Handle title bar dragging
                var titleBarRect = new Rectangle(_activityOffset.X, _activityOffset.Y,
                    _activeActivity.PanelSize.X - 40, ActivityTitleBarHeight);
                var closeRect = new Rectangle(
                    _activityOffset.X + _activeActivity.PanelSize.X - 40,
                    _activityOffset.Y, 40, ActivityTitleBarHeight);

                if (_draggingActivity)
                {
                    _activityOffset = mousePos - _activityDragOffset;
                    activityConsumed = true;
                    if (_input.LeftReleased)
                        _draggingActivity = false;
                }
                else if (_input.LeftPressed && Raylib.CheckCollisionPointRec(mousePos, closeRect))
                {
                    CloseActivity();
                    activityConsumed = true;
                }
                else if (_input.LeftPressed && Raylib.CheckCollisionPointRec(mousePos, titleBarRect))
                {
                    _draggingActivity = true;
                    _activityDragOffset = mousePos - _activityOffset;
                    activityConsumed = true;
                }
                else if (_input.LeftPressed && mouseOverPanel
                    && _activeActivity is SolitaireActivity)
                {
                    var newRect = new Rectangle(
                        _activityOffset.X + _activeActivity.PanelSize.X - 100,
                        _activityOffset.Y, 60, ActivityTitleBarHeight);
                    if (Raylib.CheckCollisionPointRec(mousePos, newRect))
                    {
                        _activeActivity.Close();
                        OpenActivity(new SolitaireActivity(_assets));
                        activityConsumed = true;
                    }
                }

                if (!activityConsumed && _activeActivity != null)
                {
                    _activeActivity.Update(delta, mousePos, _activityOffset,
                        mouseOverPanel && _input.LeftPressed,
                        mouseOverPanel && _input.LeftReleased,
                        mouseOverPanel && _input.RightPressed);

                    if (_activeActivity?.IsFinished == true)
                        _activeActivity = null;
                }

                if (mouseOverPanel || _draggingActivity)
                    activityConsumed = true;
            }
        }

        // Pet and desktop always update — activities don't block them
        _mouseOverPet = !activityConsumed
            && _pet.ActiveSheet != null
            && _pet.ActiveSheet.HitTest(_pet.CurrentFrame, _pet.Position, _pet.Scale, _pet.FlipH, mousePos);

        _mouseOverUI = _menu.ContainsPoint(mousePos) || _statusBubble.ContainsPoint(mousePos);

        bool statusConsumed = _statusBubble.Update(delta, mousePos, !activityConsumed && _input.LeftPressed);
        bool menuConsumed = _menu.Update(mousePos,
            !activityConsumed && _input.LeftPressed,
            !activityConsumed && _input.RightPressed);

        bool shouldCapture = activityConsumed || _draggingActivity
            || _mouseOverPet || _mouseOverUI || _pet.State == PetState.Dragging
            || _menu.Visible || _statusBubble.IsEditing;
        WindowHelper.SetMousePassthrough(!shouldCapture);

        if (!activityConsumed && !menuConsumed && !statusConsumed)
        {
            if (_mouseOverPet && _input.LeftPressed)
                _pet.StartDrag(mousePos);
            else if (_pet.State == PetState.Dragging && _input.LeftReleased)
                _pet.EndDrag();

            if (_mouseOverPet && _input.RightPressed)
                ShowContextMenu(mousePos);
        }

        _pet.Update(delta, mousePos);
        _events.Update(delta);
    }

    private void OpenActivity(IActivity activity)
    {
        _activeActivity = activity;
        _activeActivity.Load();
        _draggingActivity = false;
        _activityOffset = new Vector2(
            (_screenWidth - activity.PanelSize.X) / 2f,
            (_screenHeight - activity.PanelSize.Y) / 2f
        );
    }

    private void CloseActivity()
    {
        _activeActivity?.Close();
        _activeActivity = null;
        _draggingActivity = false;
    }

    private void ShowContextMenu(Vector2 position)
    {
        var items = new List<MenuItem>();

        if (_pet.State == PetState.Sleeping)
            items.Add(MenuItem.Item("Wake Up", 1));
        else
            items.Add(MenuItem.Item("Sleep", 0));

        items.Add(MenuItem.Item("Jump", 15));
        items.Add(MenuItem.Item("Walk Right", 17));
        items.Add(MenuItem.Item("Walk Left", 18));
        items.Add(MenuItem.Item(_statusBubble.Visible ? "Clear Status" : "Set Status", 70));
        items.Add(MenuItem.Separator());

        // Activities submenu
        items.Add(MenuItem.Submenu("Activities", new List<MenuItem>
        {
            MenuItem.Item("Beach", 40),
            MenuItem.Item("Go Fishing", 6),
            MenuItem.Item("Cooking", 41),
            MenuItem.Item("Gardening", 43),
            MenuItem.Item("Dance", 44),
            MenuItem.Item("Kite Flying", 45),
            MenuItem.Item("Stargazing", 42),
            MenuItem.Item("Paint", 7),
            MenuItem.Item("Solitaire", 3),
            MenuItem.Item("Chess Puzzles", 8),
        }));

        // Appearance submenu
        items.Add(MenuItem.Submenu("Appearance", new List<MenuItem>
        {
            MenuItem.Item("2-Color Mode", 20, _settings.ColorMode != "2color"),
            MenuItem.Item("1-Color Mode", 21, _settings.ColorMode != "1color"),
            MenuItem.Item("Full Color Mode", 22, _settings.ColorMode != "fullcolor"),
            MenuItem.Separator(),
            MenuItem.Item("Scale 1x", 30, _pet.Scale != 1f),
            MenuItem.Item("Scale 1.5x", 33, _pet.Scale != 1.5f),
            MenuItem.Item("Scale 2x", 31, _pet.Scale != 2f),
            MenuItem.Item("Scale 3x", 32, _pet.Scale != 3f),
        }));

        items.Add(MenuItem.Separator());
        items.Add(MenuItem.Item(_audio.Muted ? "Unmute Audio" : "Mute Audio", 16));
        items.Add(MenuItem.Item(_events.Enabled ? "Disable Events" : "Enable Events", 51));
        items.Add(MenuItem.Item("Spawn Event", 50, _events.Enabled));

        // Multiplayer submenu (only shown when enabled)
        if (_mp.Enabled)
        {
            var mpItems = new List<MenuItem>();
            if (!_mp.IsConnected)
            {
                mpItems.Add(MenuItem.Item("Host Game", 60));
                mpItems.Add(MenuItem.Item("Join Game...", 61));
            }
            else
            {
                var who = _mp.RemoteName ?? "peer";
                mpItems.Add(MenuItem.Item($"Connected: {who}", 62, false));
                mpItems.Add(MenuItem.Item("Disconnect", 63));
                if (_mp.IsHost)
                    mpItems.Add(MenuItem.Item("Kick Visitor", 64));
            }
            items.Add(MenuItem.Submenu("Multiplayer", mpItems));
        }

        items.Add(MenuItem.Separator());
        items.Add(MenuItem.Item("Quit", 99));

        _menu.SetItems(items);
        _menu.Show(position);
    }

    private void OnMenuItemSelected(int id)
    {
        switch (id)
        {
            case 0: _pet.EnterSleeping(); break;
            case 1: _pet.EnterIdle(); break;
            case 15: _pet.EnterJumping(); break;
            case 17: _pet.EnterWalkingDirection(1f); break;
            case 18: _pet.EnterWalkingDirection(-1f); break;

            // Activities
            case 40: OpenActivity(new BeachHubScene(_assets, _audio)); break;
            case 6: OpenActivity(new FishingActivity(_assets, _audio)); break;
            case 41: OpenActivity(new CookingActivity(_assets, _audio)); break;
            case 42: OpenActivity(new StargazingActivity(_assets, _audio)); break;
            case 43: OpenActivity(new GardeningActivity(_assets, _audio)); break;
            case 44: OpenActivity(new DanceActivity(_assets, _audio)); break;
            case 45: OpenActivity(new KiteFlyingActivity(_assets, _audio)); break;
            case 7: OpenActivity(new PaintActivity(_assets)); break;
            case 3: OpenActivity(new SolitaireActivity(_assets)); break;
            case 8: OpenActivity(new ChessPuzzleActivity(_assets)); break;

            // Color modes
            case 20: SetColorMode("2color"); break;
            case 21: SetColorMode("1color"); break;
            case 22: SetColorMode("fullcolor"); break;

            // Scale
            case 30: SetScale(1f); break;
            case 33: SetScale(1.5f); break;
            case 31: SetScale(2f); break;
            case 32: SetScale(3f); break;

            case 16:
                _audio.Muted = !_audio.Muted;
                _settings.Muted = _audio.Muted;
                _settings.Save();
                break;

            case 70:
                if (_statusBubble.Visible) _statusBubble.Hide();
                else _statusBubble.StartEditing();
                break;

            case 50: _events.ForceSpawn(); break;
            case 51: _events.Enabled = !_events.Enabled; break;

            // Multiplayer
            case 60: // Host
                _mp.Host();
                var code = _mp.GetVisitCode();
                if (code != null)
                    Console.WriteLine($"Visit code: {code}");
                break;
            case 61: // Join — for now just log; TODO: text input UI
                Console.WriteLine("Join: paste visit code as command-line argument");
                break;
            case 63: _mp.Disconnect(); break;
            case 64: _mp.KickVisitor(); break;

            case 99: Environment.Exit(0); break;
        }
    }

    private void SetColorMode(string mode)
    {
        _settings.ColorMode = mode;
        _settings.Save();
        ApplyColorMode(mode);
        _events.SetColorMode(mode);
    }

    private void SetScale(float scale)
    {
        _pet.Scale = scale;
        _settings.ScaleOverride = scale;
        _settings.Save();
    }

    public void Draw()
    {
        // Draw events behind everything
        _events.Draw();

        // Draw pet
        var sheet = _pet.ActiveSheet;
        sheet?.DrawFrame(_pet.CurrentFrame, _pet.Position, _pet.Scale, _pet.FlipH);

        // Draw status bubble above pet
        var (bubblePetPos, bubblePetSize) = _pet.GetBounds();
        _statusBubble.Draw(bubblePetPos, bubblePetSize);

        // Draw activity panel on top (no full-screen dim — pet stays visible)
        if (_activeActivity != null)
        {
            // Drop shadow
            Raylib.DrawRectangle((int)_activityOffset.X + 4, (int)_activityOffset.Y + 4,
                (int)_activeActivity.PanelSize.X, (int)_activeActivity.PanelSize.Y,
                new Color(0, 0, 0, 80));
            _activeActivity.Draw(_activityOffset);
        }

        // Draw popup menu on top of everything
        _menu.Draw();
    }

    private class SpriteSheetSet
    {
        public SpriteSheet Walk = null!;
        public SpriteSheet Idle = null!;
        public SpriteSheet Sleep = null!;
        public SpriteSheet SleepLoop = null!;
        public SpriteSheet Jump = null!;
    }
}
