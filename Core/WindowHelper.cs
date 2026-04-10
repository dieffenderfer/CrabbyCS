using System.Runtime.InteropServices;

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

            // Set window level to floating (above normal windows)
            // NSFloatingWindowLevel = 3
            ObjC_SetInt(_nsWindow, "setLevel:", 3);

            // Allow the window to receive mouse events initially
            ObjC_SetBool(_nsWindow, "setIgnoresMouseEvents:", false);

            // Set collection behavior to allow the window on all spaces
            // NSWindowCollectionBehaviorCanJoinAllSpaces = 1 << 0
            ObjC_SetULong(_nsWindow, "setCollectionBehavior:", 1UL);
        }
    }

    private static void SetMousePassthroughMacOS(bool passthrough)
    {
        if (_nsWindow != IntPtr.Zero)
        {
            ObjC_SetBool(_nsWindow, "setIgnoresMouseEvents:", passthrough);
        }
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

    private static void SetMousePassthroughWindows(bool passthrough)
    {
        if (passthrough)
            Raylib_cs.Raylib.SetWindowState(Raylib_cs.ConfigFlags.MousePassthroughWindow);
        else
            Raylib_cs.Raylib.ClearWindowState(Raylib_cs.ConfigFlags.MousePassthroughWindow);
    }

    // ---- Linux ----

    private static void SetupLinux()
    {
        // TODO: X11 input shape masking for click-through
    }
}
