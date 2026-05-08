namespace MouseHouse.Core;

/// <summary>
/// File-based broadcast of the active retro theme between the pet's main
/// process and the sibling activity processes. Activities are launched
/// with --theme=... at startup, but theme hover-previews from the pet
/// menu need to apply live too. The pet writes the current theme name
/// each time it changes; activities poll the file's mtime and call
/// RetroSkin.SetTheme when it advances.
/// </summary>
public static class ThemeSync
{
    private static readonly string PathStr =
        Path.Combine(SaveManager.SaveDirectory, "theme_sync.txt");

    public static void Write(string themeName)
    {
        try
        {
            Directory.CreateDirectory(SaveManager.SaveDirectory);
            File.WriteAllText(PathStr, themeName);
        }
        catch { /* best effort; the next write will catch the sibling up */ }
    }

    /// <summary>
    /// Returns the current theme name if the file's mtime has advanced
    /// since <paramref name="lastSeenUtc"/>, otherwise null. On a hit,
    /// updates <paramref name="lastSeenUtc"/> so the next call only sees
    /// further changes.
    /// </summary>
    public static string? Poll(ref DateTime lastSeenUtc)
    {
        try
        {
            if (!File.Exists(PathStr)) return null;
            var mtime = File.GetLastWriteTimeUtc(PathStr);
            if (mtime <= lastSeenUtc) return null;
            lastSeenUtc = mtime;
            var name = File.ReadAllText(PathStr).Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch { return null; }
    }
}
