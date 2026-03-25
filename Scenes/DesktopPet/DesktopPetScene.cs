using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.DesktopPet;

/// <summary>
/// Main desktop pet scene. Renders the mouse on a fullscreen transparent overlay.
/// </summary>
public class DesktopPetScene
{
    private readonly AssetCache _assets;
    private readonly InputManager _input;
    private readonly PetStateMachine _pet;
    private readonly int _screenWidth;
    private readonly int _screenHeight;

    // Track whether mouse is over the pet (for click-through toggling)
    private bool _mouseOverPet;

    public DesktopPetScene(AssetCache assets, InputManager input, int screenWidth, int screenHeight)
    {
        _assets = assets;
        _input = input;
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
        _pet = new PetStateMachine();
    }

    public void Load()
    {
        // Load mouse spritesheets (76px frames)
        _pet.WalkSheet = _assets.GetSpriteSheet("assets/sprites/pets/mouse_walk.png", 8);
        _pet.IdleSheet = _assets.GetSpriteSheet("assets/sprites/pets/mouse_idle.png", 8);
        _pet.SleepSheet = _assets.GetSpriteSheet("assets/sprites/pets/mouse_sleep.png", 12);
        _pet.SleepLoopSheet = _assets.GetSpriteSheet("assets/sprites/pets/mouse_sleep_loop.png", 3);
        _pet.JumpSheet = _assets.GetSpriteSheet("assets/sprites/pets/mouse_jump.png", 8);

        _pet.Init(_screenWidth, _screenHeight);
    }

    public void Update(float delta)
    {
        var mousePos = _input.MousePosition;

        // Check if mouse is over the pet sprite
        var (petPos, petSize) = _pet.GetBounds();
        _mouseOverPet = mousePos.X >= petPos.X && mousePos.X <= petPos.X + petSize.X
                     && mousePos.Y >= petPos.Y && mousePos.Y <= petPos.Y + petSize.Y;

        // Toggle click-through: pass through when mouse is NOT over the pet
        WindowHelper.SetMousePassthrough(!_mouseOverPet && _pet.State != PetState.Dragging);

        // Handle input
        if (_mouseOverPet && _input.LeftPressed)
        {
            _pet.StartDrag(mousePos);
        }
        else if (_pet.State == PetState.Dragging && _input.LeftReleased)
        {
            _pet.EndDrag();
        }

        // Right-click on pet -> toggle sleep (placeholder for menu later)
        if (_mouseOverPet && _input.RightPressed)
        {
            if (_pet.State == PetState.Sleeping)
                _pet.EnterIdle();
            else
                _pet.EnterSleeping();
        }

        // ESC to quit
        if (_input.IsKeyPressed(KeyboardKey.Escape))
        {
            // Handled by App via WindowShouldClose
        }

        _pet.Update(delta, mousePos);
    }

    public void Draw()
    {
        var sheet = _pet.ActiveSheet;
        if (sheet == null) return;

        sheet.DrawFrame(
            _pet.CurrentFrame,
            _pet.Position,
            _pet.Scale,
            _pet.FlipH
        );
    }
}
