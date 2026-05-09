using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Raylib_cs;

namespace MouseHouse.Core;

/// <summary>
/// Platform-specific window helpers for transparent overlay behavior.
/// Handles click-through, always-on-top, and per-pixel transparency.
/// </summary>
public static class WindowHelper
{
    public static void Setup()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            SetupMacOS();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            SetupWindows();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            SetupLinux();
        }
    }

    /// <summary>
    /// Call each frame to update click-through state based on whether
    /// the mouse is over an opaque region (pet sprite, UI element, etc.).
    /// When over opaque content: capture mouse events.
    /// When over transparent area: pass events through to desktop.
    /// </summary>
    /// <summary>
    /// Returns the cursor position in Raylib's render coordinate space, queried
    /// directly from the OS. Works regardless of mouse-passthrough state — unlike
    /// Raylib.GetMousePosition(), which depends on mouseMoved/WM_MOUSEMOVE events
    /// that the OS suppresses while the window is set to ignore mouse events.
    /// </summary>
    public static Vector2 GetGlobalCursorPosition()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return GetGlobalCursorMacOS();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return GetGlobalCursorWindows();
        return Raylib.GetMousePosition();
    }

    /// <summary>
    /// Bitmask of currently-pressed mouse buttons, queried at OS level —
    /// works whether or not our window is set to ignore mouse events.
    /// Bit 0 = left, bit 1 = right. On non-supported platforms returns 0.
    /// </summary>
    public static uint GetPressedMouseButtons()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return GetPressedMouseButtonsMacOS();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return GetPressedMouseButtonsWindows();
        return 0;
    }

    // ── High-rate click poller ───────────────────────────────────────────
    // A 60 Hz frame loop polling [NSEvent pressedMouseButtons] misses
    // clicks shorter than ~16 ms — both Pressed and Released happen
    // between two polls and the edge detector sees nothing change.
    // A background thread sampling at ~250 Hz catches them: every
    // observed 0→1 / 1→0 transition increments an atomic counter that
    // InputManager reads each frame.

    private static int _leftPressCount;
    private static int _leftReleaseCount;
    private static int _rightPressCount;
    private static int _rightReleaseCount;

    // Per-transition cursor-position ring buffers. Each ring stores the
    // last <see cref="PosRingSize"/> positions, indexed by the count
    // value AT the moment the transition was observed. The reader
    // walks counters (e.g. consume one press per frame so quick
    // double-clicks don't get collapsed into a single bool) and looks
    // up the position for each consumed event by its index — so the
    // *first* of a same-frame double-click registers at its position
    // and the *second* registers at its position, not both at the
    // most-recent one.
    private const int PosRingSize = 32;
    private static readonly Vector2[] _leftPressPosRing = new Vector2[PosRingSize];
    private static readonly Vector2[] _leftReleasePosRing = new Vector2[PosRingSize];
    private static readonly Vector2[] _rightPressPosRing = new Vector2[PosRingSize];
    private static readonly Vector2[] _rightReleasePosRing = new Vector2[PosRingSize];

    private static Thread? _clickPoller;
    private static bool _clickPollerRunning;

    public static void StartClickPoller()
    {
        if (_clickPollerRunning) return;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        _clickPollerRunning = true;
        _clickPoller = new Thread(ClickPollerLoop)
        {
            IsBackground = true,
            Name = "OSClickPoller",
        };
        _clickPoller.Start();
    }

    private static void ClickPollerLoop()
    {
        bool prevLeft = false, prevRight = false;
        var sw = Stopwatch.StartNew();
        long nextTickTicks = 0;
        const long pollIntervalTicks = TimeSpan.TicksPerMillisecond; // 1 ms
        while (_clickPollerRunning)
        {
            uint b = GetPressedMouseButtons();
            bool left = (b & 1) != 0;
            bool right = (b & 2) != 0;
            if (left != prevLeft || right != prevRight)
            {
                var pos = GetGlobalCursorPosition();
                if (left && !prevLeft)
                {
                    int slot = Volatile.Read(ref _leftPressCount) % PosRingSize;
                    _leftPressPosRing[slot] = pos;
                    Interlocked.Increment(ref _leftPressCount);
                }
                if (!left && prevLeft)
                {
                    int slot = Volatile.Read(ref _leftReleaseCount) % PosRingSize;
                    _leftReleasePosRing[slot] = pos;
                    Interlocked.Increment(ref _leftReleaseCount);
                }
                if (right && !prevRight)
                {
                    int slot = Volatile.Read(ref _rightPressCount) % PosRingSize;
                    _rightPressPosRing[slot] = pos;
                    Interlocked.Increment(ref _rightPressCount);
                }
                if (!right && prevRight)
                {
                    int slot = Volatile.Read(ref _rightReleaseCount) % PosRingSize;
                    _rightReleasePosRing[slot] = pos;
                    Interlocked.Increment(ref _rightReleaseCount);
                }
            }
            prevLeft = left; prevRight = right;

            nextTickTicks += pollIntervalTicks;
            long now = sw.Elapsed.Ticks;
            if (nextTickTicks <= now)
            {
                nextTickTicks = now + pollIntervalTicks;
                continue;
            }
            long remaining = nextTickTicks - now;
            if (remaining > TimeSpan.TicksPerMillisecond)
                try { Thread.Sleep(0); } catch { break; }
            else
                Thread.SpinWait(50);
        }
    }

    /// <summary>
    /// Returns the press / release counters as monotonically-increasing
    /// integers. The InputManager subtracts the previous-frame snapshot
    /// to find out how many of each transition happened during the frame.
    /// </summary>
    public static (int leftPress, int leftRelease, int rightPress, int rightRelease) ReadClickCounters()
        => (Volatile.Read(ref _leftPressCount),
            Volatile.Read(ref _leftReleaseCount),
            Volatile.Read(ref _rightPressCount),
            Volatile.Read(ref _rightReleaseCount));

    /// <summary>
    /// Look up the cursor position recorded at the moment of the
    /// transition with the given 1-based index. Press number 1 is the
    /// first press the poller ever observed, press 2 the next, etc.
    /// Falls back to the most-recent slot if the requested index is
    /// older than the ring window — at <see cref="PosRingSize"/> = 32
    /// that's 32 historical clicks, far more than any normal frame
    /// would consume.
    /// </summary>
    public static Vector2 ReadLeftPressPosForIndex(int index)
        => _leftPressPosRing[((index - 1) % PosRingSize + PosRingSize) % PosRingSize];
    public static Vector2 ReadLeftReleasePosForIndex(int index)
        => _leftReleasePosRing[((index - 1) % PosRingSize + PosRingSize) % PosRingSize];
    public static Vector2 ReadRightPressPosForIndex(int index)
        => _rightPressPosRing[((index - 1) % PosRingSize + PosRingSize) % PosRingSize];
    public static Vector2 ReadRightReleasePosForIndex(int index)
        => _rightReleasePosRing[((index - 1) % PosRingSize + PosRingSize) % PosRingSize];

    public static void SetMousePassthrough(bool passthrough)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            SetMousePassthroughMacOS(passthrough);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            SetMousePassthroughWindows(passthrough);
        }
        // TODO: Linux implementation
    }

    /// <summary>
    /// Toggle the always-on-top state of the single shared overlay window. The
    /// pet and any open activity panel share this window (Raylib is single-
    /// window), so when an activity opens we lower the window to a normal
    /// z-level — that lets the user push other apps over the activity. When
    /// the activity closes we restore floating so the lone pet stays on top.
    /// </summary>
    public static void SetTopmost(bool topmost)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            SetTopmostMacOS(topmost);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            SetTopmostWindows(topmost);
        // TODO: Linux implementation
    }

    private static void SetTopmostMacOS(bool topmost)
    {
        if (_nsWindow == IntPtr.Zero) return;
        // NSStatusWindowLevel (25) stays above other apps' windows reliably;
        // NSFloatingWindowLevel (3) only floats above our own app's windows on
        // newer macOS, which is why the pet kept getting buried.
        ObjC_SetInt(_nsWindow, "setLevel:", topmost ? 25 : 0);
    }

    private static void SetTopmostWindows(bool topmost)
    {
        if (topmost)
            Raylib.SetWindowState(ConfigFlags.TopmostWindow);
        else
            Raylib.ClearWindowState(ConfigFlags.TopmostWindow);
    }

    // ---- macOS ----

    private static IntPtr _nsWindow = IntPtr.Zero;

    private static void SetupMacOS()
    {
        // Get the NSWindow handle via Objective-C runtime
        // Raylib uses GLFW which creates an NSWindow
        _nsWindow = GetNSWindow();

        if (_nsWindow != IntPtr.Zero)
        {
            // Make the window non-opaque (required for transparency)
            ObjC_SetBool(_nsWindow, "setOpaque:", false);

            // Set background color to clear
            IntPtr clearColor = ObjC_CallClass("NSColor", "clearColor");
            ObjC_SetObject(_nsWindow, "setBackgroundColor:", clearColor);

            // Set window level to status (above other apps' windows).
            // NSStatusWindowLevel = 25 — high enough to outrank app windows,
            // below screen-saver / system overlays.
            ObjC_SetInt(_nsWindow, "setLevel:", 25);

            // Allow the window to receive mouse events initially
            ObjC_SetBool(_nsWindow, "setIgnoresMouseEvents:", false);

            // Set collection behavior to allow the window on all spaces
            // NSWindowCollectionBehaviorCanJoinAllSpaces = 1 << 0
            ObjC_SetULong(_nsWindow, "setCollectionBehavior:", 1UL);
        }
    }

    private static bool _lastPassthrough = true;

    private static void SetMousePassthroughMacOS(bool passthrough)
    {
        if (_nsWindow != IntPtr.Zero)
        {
            ObjC_SetBool(_nsWindow, "setIgnoresMouseEvents:", passthrough);

            if (!passthrough && _lastPassthrough)
            {
                // Transitioning from passthrough to capturing — make the window
                // key so macOS delivers the click to Raylib immediately instead
                // of requiring an extra "activate" click.
                objc_msgSend_IntPtr(_nsWindow, sel_registerName("makeKeyWindow"));
            }
            _lastPassthrough = passthrough;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint { public double X, Y; }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern CGPoint objc_msgSend_CGPoint(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern ulong objc_msgSend_ULongRet(IntPtr receiver, IntPtr selector);

    private static uint GetPressedMouseButtonsMacOS()
    {
        // [NSEvent pressedMouseButtons] — global bitmask of mouse buttons
        // currently held down anywhere, independent of which window has
        // focus or our setIgnoresMouseEvents state. macOS bit layout
        // matches the bit-0=left / bit-1=right convention we expose.
        var nsEventClass = objc_getClass("NSEvent");
        if (nsEventClass == IntPtr.Zero) return 0;
        try
        {
            return (uint)objc_msgSend_ULongRet(nsEventClass, sel_registerName("pressedMouseButtons"));
        }
        catch { return 0; }
    }

    private static Vector2 GetGlobalCursorMacOS()
    {
        // [NSEvent mouseLocation] — global screen coords, bottom-left origin, in
        // points (logical units). Doesn't depend on event delivery to our window.
        var nsEventClass = objc_getClass("NSEvent");
        if (nsEventClass == IntPtr.Zero) return Raylib.GetMousePosition();
        var loc = objc_msgSend_CGPoint(nsEventClass, sel_registerName("mouseLocation"));

        // Convert to GLOBAL screen pixels (top-left origin). Use the
        // monitor's pixel height instead of Raylib.GetRenderHeight (the
        // window's render height) — Render height equals screen height
        // only for fullscreen windows like the main pet overlay; for
        // non-fullscreen sibling windows it'd give a much smaller
        // value and the Y conversion would be wildly off.
        var scale = Raylib.GetWindowScaleDPI();
        int monitor = Raylib.GetCurrentMonitor();
        int monitorH = Raylib.GetMonitorHeight(monitor);
        float monitorHPoints = monitorH / (scale.Y == 0 ? 1f : scale.Y);
        float xPx = (float)loc.X * scale.X;
        float yPx = ((float)monitorHPoints - (float)loc.Y) * scale.Y;
        return new Vector2(xPx, yPx);
    }

    /// <summary>
    /// Convert a global screen-pixel position (origin top-left of the
    /// primary screen) into window-local pixels for the *current*
    /// process's Raylib window. Used by sibling activities (e.g. Ohio
    /// Golf, Chess Puzzles) to translate at-click positions captured
    /// by the high-rate poller into the activity's coordinate space.
    /// For the main fullscreen overlay this is a no-op (window is at
    /// (0,0) of its monitor, so global == window-local).
    /// </summary>
    public static Vector2 GlobalScreenPxToWindowLocalPx(Vector2 globalPx)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return globalPx;
        var winPosPoints = Raylib.GetWindowPosition();   // points on macOS
        var scale = Raylib.GetWindowScaleDPI();
        return globalPx - new Vector2(
            winPosPoints.X * scale.X,
            winPosPoints.Y * scale.Y);
    }

    private static IntPtr GetNSWindow()
    {
        // Get [NSApplication sharedApplication]
        IntPtr nsApp = ObjC_CallClass("NSApplication", "sharedApplication");
        if (nsApp == IntPtr.Zero) return IntPtr.Zero;

        // Get the main window: [[NSApplication sharedApplication] mainWindow]
        IntPtr mainWindow = objc_msgSend_IntPtr(nsApp, sel_registerName("mainWindow"));
        if (mainWindow != IntPtr.Zero) return mainWindow;

        // Fallback: get the key window
        IntPtr keyWindow = objc_msgSend_IntPtr(nsApp, sel_registerName("keyWindow"));
        if (keyWindow != IntPtr.Zero) return keyWindow;

        // Fallback: get first window from windows array
        IntPtr windows = objc_msgSend_IntPtr(nsApp, sel_registerName("windows"));
        if (windows == IntPtr.Zero) return IntPtr.Zero;

        long count = objc_msgSend_Long(windows, sel_registerName("count"));
        if (count > 0)
        {
            return objc_msgSend_IntPtr_IntPtr(windows, sel_registerName("objectAtIndex:"), IntPtr.Zero);
        }

        return IntPtr.Zero;
    }

    // ---- Objective-C Runtime P/Invoke ----

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(string className);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(string selectorName);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_IntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_Bool(IntPtr receiver, IntPtr selector, bool arg);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_Int(IntPtr receiver, IntPtr selector, int arg);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_ULong(IntPtr receiver, IntPtr selector, ulong arg);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern long objc_msgSend_Long(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_Object(IntPtr receiver, IntPtr selector, IntPtr arg);

    private static IntPtr ObjC_CallClass(string className, string selectorName)
    {
        return objc_msgSend_IntPtr(objc_getClass(className), sel_registerName(selectorName));
    }

    private static void ObjC_SetBool(IntPtr obj, string selector, bool value)
    {
        objc_msgSend_Bool(obj, sel_registerName(selector), value);
    }

    private static void ObjC_SetInt(IntPtr obj, string selector, int value)
    {
        objc_msgSend_Int(obj, sel_registerName(selector), value);
    }

    private static void ObjC_SetULong(IntPtr obj, string selector, ulong value)
    {
        objc_msgSend_ULong(obj, sel_registerName(selector), value);
    }

    private static void ObjC_SetObject(IntPtr obj, string selector, IntPtr value)
    {
        objc_msgSend_Object(obj, sel_registerName(selector), value);
    }

    // ---- Windows ----

    private static void SetupWindows()
    {
        // The app boots with ConfigFlags.MousePassthroughWindow set, which GLFW
        // implements on Win32 by adding WS_EX_LAYERED | WS_EX_TRANSPARENT (and
        // setting up the layered attributes correctly). We want that initial
        // WS_EX_LAYERED setup so transparency works, but we immediately clear
        // the passthrough state so the window starts capturing clicks AND
        // cursor-move events (WM_MOUSEMOVE doesn't fire on a transparent
        // window, which otherwise leaves Raylib's cached cursor position stuck
        // at the window's creation point).
        //
        // Going through Raylib.ClearWindowState routes to GLFW's
        // SetWindowMousePassthrough(false), which correctly preserves the
        // layered attributes — unlike a raw SetWindowLongPtr toggle, which can
        // leave the layered window in a broken state.
        Raylib_cs.Raylib.ClearWindowState(Raylib_cs.ConfigFlags.MousePassthroughWindow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private static Vector2 GetGlobalCursorWindows()
    {
        // GetCursorPos returns screen-space pixels. Window is fullscreen at (0,0),
        // so this directly matches Raylib's render coord space.
        if (GetCursorPos(out var p))
            return new Vector2(p.X, p.Y);
        return Raylib.GetMousePosition();
    }

    private static void SetMousePassthroughWindows(bool passthrough)
    {
        if (passthrough)
            Raylib_cs.Raylib.SetWindowState(Raylib_cs.ConfigFlags.MousePassthroughWindow);
        else
            Raylib_cs.Raylib.ClearWindowState(Raylib_cs.ConfigFlags.MousePassthroughWindow);
    }

    // VK codes for mouse buttons
    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static uint GetPressedMouseButtonsWindows()
    {
        uint result = 0;
        if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0) result |= 1;
        if ((GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0) result |= 2;
        return result;
    }

    // ---- Linux ----

    private static void SetupLinux()
    {
        // TODO: X11 input shape masking for click-through
    }
}
