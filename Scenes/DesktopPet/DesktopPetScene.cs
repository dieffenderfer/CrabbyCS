using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
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

    // Track whether mouse is over an interactive element
    private bool _mouseOverPet;
    private bool _mouseOverUI;

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

        _mouseOverUI = _menu.ContainsPoint(mousePos);

        // Update popup menu first (it may consume clicks)
        bool menuConsumed = _menu.Update(mousePos, _input.LeftPressed, _input.RightPressed);

        // Toggle click-through: pass through when not over pet, UI, or dragging
        bool shouldCapture = _mouseOverPet || _mouseOverUI || _pet.State == PetState.Dragging || _menu.Visible;
        WindowHelper.SetMousePassthrough(!shouldCapture);

        if (!menuConsumed)
        {
            // Left click on pet -> drag
            if (_mouseOverPet && _input.LeftPressed)
            {
                _pet.StartDrag(mousePos);
            }
            else if (_pet.State == PetState.Dragging && _input.LeftReleased)
            {
                _pet.EndDrag();
            }

            // Right-click on pet -> show context menu
            if (_mouseOverPet && _input.RightPressed)
            {
                ShowContextMenu(mousePos);
            }
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

        items.Add(MenuItem.Item("Blow Bubbles", 2));
        items.Add(MenuItem.Item("Jump", 15));
        items.Add(MenuItem.Separator());
        items.Add(MenuItem.Item("Solitaire", 3, false)); // TODO: not yet implemented
        items.Add(MenuItem.Item("Hearts", 4, false));
        items.Add(MenuItem.Item("Chess Puzzles", 5, false));
        items.Add(MenuItem.Item("Go Fishing", 6, false));
        items.Add(MenuItem.Item("Paint", 7, false));
        items.Add(MenuItem.Separator());
        items.Add(MenuItem.Item("Mute Audio", 16));
        items.Add(MenuItem.Separator());
        items.Add(MenuItem.Item("Quit", 99));

        _menu.SetItems(items);
        _menu.Show(position);
    }

    private void OnMenuItemSelected(int id)
    {
        switch (id)
        {
            case 0: // Sleep
                _pet.EnterSleeping();
                break;
            case 1: // Wake Up
                _pet.EnterIdle();
                break;
            case 2: // Blow Bubbles
                // TODO: implement bubbles
                _pet.EnterIdle();
                break;
            case 15: // Jump
                _pet.EnterJumping();
                break;
            case 16: // Mute
                _audio.Muted = !_audio.Muted;
                break;
            case 99: // Quit
                Environment.Exit(0);
                break;
        }
    }

    public void Draw()
    {
        // Draw pet
        var sheet = _pet.ActiveSheet;
        if (sheet != null)
        {
            sheet.DrawFrame(
                _pet.CurrentFrame,
                _pet.Position,
                _pet.Scale,
                _pet.FlipH
            );
        }

        // Draw UI on top
        _menu.Draw();
    }
}
