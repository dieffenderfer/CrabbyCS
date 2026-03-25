using System.Numerics;
using Raylib_cs;

namespace MouseHouse.Core;

/// <summary>
/// Tracks mouse and keyboard input state each frame.
/// </summary>
public class InputManager
{
    public Vector2 MousePosition { get; private set; }
    public Vector2 MouseDelta { get; private set; }
    public bool LeftPressed { get; private set; }
    public bool LeftReleased { get; private set; }
    public bool LeftDown { get; private set; }
    public bool RightPressed { get; private set; }
    public bool RightReleased { get; private set; }
    public bool RightDown { get; private set; }

    public void Update()
    {
        MousePosition = Raylib.GetMousePosition();
        MouseDelta = Raylib.GetMouseDelta();

        LeftPressed = Raylib.IsMouseButtonPressed(MouseButton.Left);
        LeftReleased = Raylib.IsMouseButtonReleased(MouseButton.Left);
        LeftDown = Raylib.IsMouseButtonDown(MouseButton.Left);

        RightPressed = Raylib.IsMouseButtonPressed(MouseButton.Right);
        RightReleased = Raylib.IsMouseButtonReleased(MouseButton.Right);
        RightDown = Raylib.IsMouseButtonDown(MouseButton.Right);
    }

    public bool IsKeyPressed(KeyboardKey key) => Raylib.IsKeyPressed(key);
    public bool IsKeyDown(KeyboardKey key) => Raylib.IsKeyDown(key);
}
