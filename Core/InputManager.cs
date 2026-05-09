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

    /// <summary>
    /// Cursor position at the moment of the most recent left/right press
    /// (or release), sampled inside the high-rate poller in WindowHelper.
    /// Use this for click hit-testing instead of the current cursor — the
    /// frame loop ticks at 60 Hz, so by the time a frame reads the cursor
    /// after a press counter increment, the user may have moved 5-30 px
    /// past the target. Hit-testing at the at-press position fixes the
    /// "I clicked it but nothing happened" feel.
    /// </summary>
    public Vector2 LeftPressedPos { get; private set; }
    public Vector2 LeftReleasedPos { get; private set; }
    public Vector2 RightPressedPos { get; private set; }
    public Vector2 RightReleasedPos { get; private set; }

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

        // At-click cursor positions — only meaningful when the matching
        // pressed/released flag is true; otherwise the value is the
        // cursor at the previous transition (still safe to read).
        LeftPressedPos = WindowHelper.ReadLastLeftPressPos();
        LeftReleasedPos = WindowHelper.ReadLastLeftReleasePos();
        RightPressedPos = WindowHelper.ReadLastRightPressPos();
        RightReleasedPos = WindowHelper.ReadLastRightReleasePos();
    }

    public bool IsKeyPressed(KeyboardKey key) => Raylib.IsKeyPressed(key);
    public bool IsKeyDown(KeyboardKey key) => Raylib.IsKeyDown(key);
}
