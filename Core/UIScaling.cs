namespace MouseHouse.Core;

/// <summary>
/// Global integer-pixel UI scale factor. Shared between the main pet exe
/// and the sibling MouseHouse.Activities process so input helpers
/// (WindowHelper, InputManager) can convert OS pixel coordinates into
/// logical coordinates without depending on <see cref="App"/>.
/// </summary>
public static class UIScaling
{
    /// <summary>
    /// Current scale factor. 1 = native pixels, 2 = 2× pixel doubling, etc.
    /// Set once at startup from persisted settings and updated at runtime
    /// via the Appearance → UI Scale menu.
    /// </summary>
    public static float Factor { get; set; } = 1f;
}
