using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Data;
using MouseHouse.Rendering;
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
    private readonly PetStateMachine _pet;
    private readonly PopupMenu _menu;
    private readonly int _screenWidth;
    private readonly int _screenHeight;
    private PetSettings _settings;

    // Color mode spritesheets
    private readonly Dictionary<string, SpriteSheetSet> _colorModes = new();

    // Track whether mouse is over an interactive element
    private bool _mouseOverPet;
    private bool _mouseOverUI;

    // Time update throttle
    private float _timeUpdateTimer;

    public DesktopPetScene(AssetCache assets, InputManager input, AudioManager audio, int screenWidth, int screenHeight)
    {
        _assets = assets;
        _input = input;
        _audio = audio;
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
        _pet = new PetStateMachine();
        _menu = new PopupMenu();
        _menu.OnItemSelected += OnMenuItemSelected;
        _settings = PetSettings.Load();
    }

    public void Load()
    {
        // Load all color mode spritesheets
        _colorModes["2color"] = new SpriteSheetSet
        {
            Walk = _assets.GetSpriteSheet("assets/sprites/pets/mouse_walk.png", 8),
            Idle = _assets.GetSpriteSheet("assets/sprites/pets/mouse_idle.png", 8),
            Sleep = _assets.GetSpriteSheet("assets/sprites/pets/mouse_sleep.png", 12),
            SleepLoop = _assets.GetSpriteSheet("assets/sprites/pets/mouse_sleep_loop.png", 3),
            Jump = _assets.GetSpriteSheet("assets/sprites/pets/mouse_jump.png", 8),
        };

        _colorModes["1color"] = new SpriteSheetSet
        {
            Walk = _assets.GetSpriteSheet("assets/sprites/pets/mouse_1c_walk.png", 8),
            Idle = _assets.GetSpriteSheet("assets/sprites/pets/mouse_1c_idle.png", 8),
            Sleep = _assets.GetSpriteSheet("assets/sprites/pets/mouse_1c_sleep.png", 12),
            SleepLoop = _assets.GetSpriteSheet("assets/sprites/pets/mouse_1c_sleep_loop.png", 3),
            Jump = _assets.GetSpriteSheet("assets/sprites/pets/mouse_1c_jump.png", 8),
        };

        _colorModes["fullcolor"] = new SpriteSheetSet
        {
            Walk = _assets.GetSpriteSheet("assets/sprites/pets/mouse_fc_walk.png", 8),
            Idle = _assets.GetSpriteSheet("assets/sprites/pets/mouse_fc_idle.png", 8),
            Sleep = _assets.GetSpriteSheet("assets/sprites/pets/mouse_fc_sleep.png", 12),
            SleepLoop = _assets.GetSpriteSheet("assets/sprites/pets/mouse_fc_sleep_loop.png", 3),
            Jump = _assets.GetSpriteSheet("assets/sprites/pets/mouse_fc_jump.png", 8),
        };

        ApplyColorMode(_settings.ColorMode);

        if (_settings.ScaleOverride > 0)
            _pet.Scale = _settings.ScaleOverride;

        _audio.Muted = _settings.Muted;

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

        // Re-apply the active sheet for the current state
        _pet.RefreshActiveSheet();
    }

    public void Update(float delta)
    {
        // Update tweens
        TweenSystem.Update(delta);

        // Update time system every 30 seconds
        _timeUpdateTimer += delta;
        if (_timeUpdateTimer >= 30f)
        {
            _timeUpdateTimer = 0;
            TimeSystem.Update();
        }

        var mousePos = _input.MousePosition;

        // Check if mouse is over the pet sprite
        var (petPos, petSize) = _pet.GetBounds();
        _mouseOverPet = mousePos.X >= petPos.X && mousePos.X <= petPos.X + petSize.X
                     && mousePos.Y >= petPos.Y && mousePos.Y <= petPos.Y + petSize.Y;

        _mouseOverUI = _menu.ContainsPoint(mousePos);

        // Update popup menu first (it may consume clicks)
        bool menuConsumed = _menu.Update(mousePos, _input.LeftPressed, _input.RightPressed);

        // Toggle click-through
        bool shouldCapture = _mouseOverPet || _mouseOverUI || _pet.State == PetState.Dragging || _menu.Visible;
        WindowHelper.SetMousePassthrough(!shouldCapture);

        if (!menuConsumed)
        {
            if (_mouseOverPet && _input.LeftPressed)
                _pet.StartDrag(mousePos);
            else if (_pet.State == PetState.Dragging && _input.LeftReleased)
                _pet.EndDrag();

            if (_mouseOverPet && _input.RightPressed)
                ShowContextMenu(mousePos);
        }

        _pet.Update(delta, mousePos);
    }

    private void ShowContextMenu(Vector2 position)
    {
        var items = new List<MenuItem>();

        if (_pet.State == PetState.Sleeping)
            items.Add(MenuItem.Item("Wake Up", 1));
        else
            items.Add(MenuItem.Item("Sleep", 0));

        items.Add(MenuItem.Item("Jump", 15));
        items.Add(MenuItem.Separator());

        // Color mode submenu items
        items.Add(MenuItem.Item("2-Color Mode", 20, _settings.ColorMode != "2color"));
        items.Add(MenuItem.Item("1-Color Mode", 21, _settings.ColorMode != "1color"));
        items.Add(MenuItem.Item("Full Color Mode", 22, _settings.ColorMode != "fullcolor"));
        items.Add(MenuItem.Separator());

        // Scale options
        items.Add(MenuItem.Item("Scale 1x", 30, _pet.Scale != 1f));
        items.Add(MenuItem.Item("Scale 2x", 31, _pet.Scale != 2f));
        items.Add(MenuItem.Item("Scale 3x", 32, _pet.Scale != 3f));
        items.Add(MenuItem.Separator());

        items.Add(MenuItem.Item(_audio.Muted ? "Unmute Audio" : "Mute Audio", 16));
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

            // Color modes
            case 20: SetColorMode("2color"); break;
            case 21: SetColorMode("1color"); break;
            case 22: SetColorMode("fullcolor"); break;

            // Scale
            case 30: SetScale(1); break;
            case 31: SetScale(2); break;
            case 32: SetScale(3); break;

            case 16:
                _audio.Muted = !_audio.Muted;
                _settings.Muted = _audio.Muted;
                _settings.Save();
                break;

            case 99: Environment.Exit(0); break;
        }
    }

    private void SetColorMode(string mode)
    {
        _settings.ColorMode = mode;
        _settings.Save();
        ApplyColorMode(mode);
    }

    private void SetScale(int scale)
    {
        _pet.Scale = scale;
        _settings.ScaleOverride = scale;
        _settings.Save();
    }

    public void Draw()
    {
        // Draw pet
        var sheet = _pet.ActiveSheet;
        sheet?.DrawFrame(_pet.CurrentFrame, _pet.Position, _pet.Scale, _pet.FlipH);

        // Draw day/night overlay
        var overlay = TimeSystem.OverlayColor;
        if (overlay.A > 0)
            Raylib.DrawRectangle(0, 0, _screenWidth, _screenHeight, overlay);

        // Draw UI on top
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
