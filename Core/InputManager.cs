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

    // Mirror of the previous frame's OS-level button state. We OR Raylib's
    // event-driven IsMouseButtonPressed with an OS-polled edge so clicks
    // that happen while macOS has our window in ignoresMouseEvents (and
    // therefore never reach Raylib's event queue) still register.
    private bool _prevOsLeft;
    private bool _prevOsRight;

    public void Update()
    {
        MousePosition = Raylib.GetMousePosition();
        MouseDelta = Raylib.GetMouseDelta();

        uint osBtns = WindowHelper.GetPressedMouseButtons();
        bool osLeft = (osBtns & 1) != 0;
        bool osRight = (osBtns & 2) != 0;

        bool ralLeftPressed = Raylib.IsMouseButtonPressed(MouseButton.Left);
        bool ralLeftReleased = Raylib.IsMouseButtonReleased(MouseButton.Left);
        bool ralLeftDown = Raylib.IsMouseButtonDown(MouseButton.Left);
        bool ralRightPressed = Raylib.IsMouseButtonPressed(MouseButton.Right);
        bool ralRightReleased = Raylib.IsMouseButtonReleased(MouseButton.Right);
        bool ralRightDown = Raylib.IsMouseButtonDown(MouseButton.Right);

        LeftPressed = ralLeftPressed || (osLeft && !_prevOsLeft);
        LeftReleased = ralLeftReleased || (!osLeft && _prevOsLeft);
        LeftDown = ralLeftDown || osLeft;

        RightPressed = ralRightPressed || (osRight && !_prevOsRight);
        RightReleased = ralRightReleased || (!osRight && _prevOsRight);
        RightDown = ralRightDown || osRight;

        _prevOsLeft = osLeft;
        _prevOsRight = osRight;
    }

    public bool IsKeyPressed(KeyboardKey key) => Raylib.IsKeyPressed(key);
    public bool IsKeyDown(KeyboardKey key) => Raylib.IsKeyDown(key);
}
