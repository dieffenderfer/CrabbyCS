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

    // Snapshot of the high-rate click counters from the last frame. The
    // background poller in WindowHelper samples [NSEvent pressedMouseButtons]
    // at ~250 Hz, so even sub-frame clicks (shorter than ~16 ms) bump the
    // counters; subtracting the previous-frame value gives the number of
    // each transition that happened during this frame.
    private int _lastLeftPressCount;
    private int _lastLeftReleaseCount;
    private int _lastRightPressCount;
    private int _lastRightReleaseCount;

    public void Update()
    {
        MousePosition = Raylib.GetMousePosition();
        MouseDelta = Raylib.GetMouseDelta();

        var (lp, lr, rp, rr) = WindowHelper.ReadClickCounters();
        bool osLeftPressed = lp != _lastLeftPressCount;
        bool osLeftReleased = lr != _lastLeftReleaseCount;
        bool osRightPressed = rp != _lastRightPressCount;
        bool osRightReleased = rr != _lastRightReleaseCount;
        _lastLeftPressCount = lp;
        _lastLeftReleaseCount = lr;
        _lastRightPressCount = rp;
        _lastRightReleaseCount = rr;

        uint osBtns = WindowHelper.GetPressedMouseButtons();
        bool osLeft = (osBtns & 1) != 0;
        bool osRight = (osBtns & 2) != 0;

        // OR with Raylib's event-driven state for parity on platforms
        // where the high-rate poller isn't running.
        LeftPressed = Raylib.IsMouseButtonPressed(MouseButton.Left) || osLeftPressed;
        LeftReleased = Raylib.IsMouseButtonReleased(MouseButton.Left) || osLeftReleased;
        LeftDown = Raylib.IsMouseButtonDown(MouseButton.Left) || osLeft;

        RightPressed = Raylib.IsMouseButtonPressed(MouseButton.Right) || osRightPressed;
        RightReleased = Raylib.IsMouseButtonReleased(MouseButton.Right) || osRightReleased;
        RightDown = Raylib.IsMouseButtonDown(MouseButton.Right) || osRight;
    }

    public bool IsKeyPressed(KeyboardKey key) => Raylib.IsKeyPressed(key);
    public bool IsKeyDown(KeyboardKey key) => Raylib.IsKeyDown(key);
}
