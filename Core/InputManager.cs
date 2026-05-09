using System.Numerics;
using Raylib_cs;

namespace MouseHouse.Core;

/// <summary>
/// Tracks mouse and keyboard input state each frame.
///
/// <para>Press / release transitions captured by the high-rate poller in
/// <see cref="WindowHelper"/> are dispensed to activities one per frame
/// instead of being collapsed into a single <c>LeftPressed = true</c>
/// bool. A quick double-click whose two press transitions land in the
/// same 16 ms frame previously registered as a single click — the
/// second press was silently dropped. Now we walk the counters event
/// by event so no click gets lost no matter how fast the user clicks.
/// Each delivered event also reports the cursor position recorded at
/// *that* transition (not the most recent one), so a sequence of
/// rapid clicks at different points all hit the right spot.</para>
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
    /// Cursor position at the moment of the press / release event being
    /// reported this frame. Pulled from a per-transition ring buffer so
    /// each consumed event reports its own position even when several
    /// transitions queued up between frames.
    /// </summary>
    public Vector2 LeftPressedPos { get; private set; }
    public Vector2 LeftReleasedPos { get; private set; }
    public Vector2 RightPressedPos { get; private set; }
    public Vector2 RightReleasedPos { get; private set; }

    // Index of the most recently *consumed* transition for each kind.
    // The poller-side counters are >= these; any gap is unconsumed
    // events to dispense one-per-frame so quick multi-clicks aren't
    // collapsed into a single bool.
    private int _consumedLeftPress;
    private int _consumedLeftRelease;
    private int _consumedRightPress;
    private int _consumedRightRelease;
    private bool _initialized;

    public void Update()
    {
        MousePosition = Raylib.GetMousePosition();
        MouseDelta = Raylib.GetMouseDelta();

        var (lp, lr, rp, rr) = WindowHelper.ReadClickCounters();

        // First-frame sync — don't try to "consume" historical clicks
        // that happened before the input manager existed.
        if (!_initialized)
        {
            _consumedLeftPress = lp;
            _consumedLeftRelease = lr;
            _consumedRightPress = rp;
            _consumedRightRelease = rr;
            _initialized = true;
        }

        // If we've fallen behind by more than the ring window, jump
        // ahead — older positions have been overwritten in the ring
        // anyway, so trying to deliver them would report stale data.
        // 28 < 32 (ring size) keeps a small safety margin.
        const int MaxBacklog = 28;
        if (lp - _consumedLeftPress > MaxBacklog)       _consumedLeftPress = lp - MaxBacklog;
        if (lr - _consumedLeftRelease > MaxBacklog)     _consumedLeftRelease = lr - MaxBacklog;
        if (rp - _consumedRightPress > MaxBacklog)      _consumedRightPress = rp - MaxBacklog;
        if (rr - _consumedRightRelease > MaxBacklog)    _consumedRightRelease = rr - MaxBacklog;

        // Consume one transition per kind per frame (max). Activities
        // see one Pressed / one Released per frame — same shape they
        // already handle — but the queued events drain over consecutive
        // frames instead of getting dropped.
        bool firedLeftPress = false, firedLeftRelease = false;
        bool firedRightPress = false, firedRightRelease = false;

        if (_consumedLeftPress < lp)
        {
            _consumedLeftPress++;
            LeftPressedPos = WindowHelper.ReadLeftPressPosForIndex(_consumedLeftPress);
            firedLeftPress = true;
            Console.WriteLine($"[input] PRESS  consumed={_consumedLeftPress} lp={lp} pos=({LeftPressedPos.X:F0},{LeftPressedPos.Y:F0})");
        }
        if (_consumedLeftRelease < lr)
        {
            _consumedLeftRelease++;
            LeftReleasedPos = WindowHelper.ReadLeftReleasePosForIndex(_consumedLeftRelease);
            firedLeftRelease = true;
            Console.WriteLine($"[input] RELEASE consumed={_consumedLeftRelease} lr={lr} pos=({LeftReleasedPos.X:F0},{LeftReleasedPos.Y:F0})");
        }
        if (_consumedRightPress < rp)
        {
            _consumedRightPress++;
            RightPressedPos = WindowHelper.ReadRightPressPosForIndex(_consumedRightPress);
            firedRightPress = true;
        }
        if (_consumedRightRelease < rr)
        {
            _consumedRightRelease++;
            RightReleasedPos = WindowHelper.ReadRightReleasePosForIndex(_consumedRightRelease);
            firedRightRelease = true;
        }

        uint osBtns = WindowHelper.GetPressedMouseButtons();
        bool osLeft = (osBtns & 1) != 0;
        bool osRight = (osBtns & 2) != 0;

        // Combine with Raylib's event-driven state so platforms without
        // the high-rate poller (Windows / Linux) still get edge events.
        // On macOS the poller is the source of truth — Raylib's events
        // are missed while passthrough is on, so we *must* keep the
        // counter-based dispatch as the primary path.
        LeftPressed = Raylib.IsMouseButtonPressed(MouseButton.Left) || firedLeftPress;
        LeftReleased = Raylib.IsMouseButtonReleased(MouseButton.Left) || firedLeftRelease;
        LeftDown = Raylib.IsMouseButtonDown(MouseButton.Left) || osLeft;

        RightPressed = Raylib.IsMouseButtonPressed(MouseButton.Right) || firedRightPress;
        RightReleased = Raylib.IsMouseButtonReleased(MouseButton.Right) || firedRightRelease;
        RightDown = Raylib.IsMouseButtonDown(MouseButton.Right) || osRight;
    }

    public bool IsKeyPressed(KeyboardKey key) => Raylib.IsKeyPressed(key);
    public bool IsKeyDown(KeyboardKey key) => Raylib.IsKeyDown(key);
}
