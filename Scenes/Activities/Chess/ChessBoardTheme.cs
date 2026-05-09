using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities.Chess;

/// <summary>
/// One coherent board palette — the two square colours plus the matching
/// coordinate-label colour and the three move-accent tints (last-move
/// highlight, selected-square fill, legal-move dot). Themes hold all of
/// these together so the accents always read well against their squares.
/// </summary>
public record ChessBoardTheme(
    string Name,
    Color Light,
    Color Dark,
    Color CoordLabel,
    Color LastMoveTint,
    Color SelectedTint,
    Color LegalDot);

public static class ChessBoardThemes
{
    private static Color C(int r, int g, int b, int a = 255)
        => new((byte)r, (byte)g, (byte)b, (byte)a);

    public static readonly ChessBoardTheme[] All =
    {
        // Win98 Slate — default. Light squares slightly darker than the
        // canonical Win98 chrome grey (192,192,192), darker squares a
        // notch lower so the board reads against the rest of the retro
        // UI without competing with it. Coordinate labels and accents
        // tuned to a cool steel hue that matches.
        new("Win98 Slate",
            Light:        C(178, 178, 178),
            Dark:         C(128, 128, 128),
            CoordLabel:   C( 40,  40,  44, 230),
            LastMoveTint: C(245, 220, 110, 120),
            SelectedTint: C(140, 200, 255, 130),
            LegalDot:     C( 60,  90, 130, 200)),

        // Classic Brown — the original look.
        new("Classic Brown",
            Light:        C(232, 216, 184),
            Dark:         C(120,  88,  56),
            CoordLabel:   C( 40,  24,  12, 220),
            LastMoveTint: C(220, 200,  80, 110),
            SelectedTint: C(120, 200,  80, 120),
            LegalDot:     C( 60, 200,  60, 180)),

        // Tournament Green — Lichess / chess.com default.
        new("Tournament Green",
            Light:        C(238, 238, 210),
            Dark:         C(118, 150,  86),
            CoordLabel:   C( 60,  80,  50, 230),
            LastMoveTint: C(255, 230, 110, 120),
            SelectedTint: C(255, 235, 130, 130),
            LegalDot:     C(150, 200,  80, 200)),

        // Slate Blue — cool and quiet.
        new("Slate Blue",
            Light:        C(222, 227, 236),
            Dark:         C( 95, 115, 155),
            CoordLabel:   C( 40,  55,  90, 230),
            LastMoveTint: C(255, 220, 130, 120),
            SelectedTint: C(180, 220, 255, 130),
            LegalDot:     C( 90, 170, 220, 200)),

        // Midnight — dark mode for late-night puzzles.
        new("Midnight",
            Light:        C( 92, 100, 120),
            Dark:         C( 44,  52,  72),
            CoordLabel:   C(200, 205, 225, 230),
            LastMoveTint: C(255, 200,  90, 110),
            SelectedTint: C(140, 200, 255, 120),
            LegalDot:     C(120, 220, 160, 180)),

        // Coral — warm peach pairing.
        new("Coral",
            Light:        C(252, 228, 205),
            Dark:         C(225, 134, 125),
            CoordLabel:   C(120,  60,  55, 230),
            LastMoveTint: C(255, 220, 110, 120),
            SelectedTint: C(255, 200, 170, 140),
            LegalDot:     C(220,  90, 110, 190)),

        // Walnut — rich darker wood, more contrast than Classic Brown.
        new("Walnut",
            Light:        C(218, 184, 140),
            Dark:         C(105,  72,  45),
            CoordLabel:   C( 50,  32,  20, 230),
            LastMoveTint: C(230, 200,  90, 120),
            SelectedTint: C(150, 200,  90, 130),
            LegalDot:     C( 90, 200,  90, 180)),

        // Pearl — bright, low-contrast museum palette.
        new("Pearl",
            Light:        C(245, 240, 235),
            Dark:         C(180, 168, 158),
            CoordLabel:   C( 80,  70,  60, 230),
            LastMoveTint: C(255, 220, 130, 120),
            SelectedTint: C(180, 210, 230, 130),
            LegalDot:     C(120, 170, 200, 190)),
    };

    private static int _idx;
    public static ChessBoardTheme Current => All[_idx];
    public static int CurrentIdx => _idx;

    private static string PathStr
        => Path.Combine(SaveManager.SaveDirectory, "chess_board_theme.txt");
    private static DateTime _lastSeenUtc = DateTime.MinValue;

    /// <summary>Load persisted theme. Safe to call multiple times.</summary>
    public static void Load()
    {
        try
        {
            if (!File.Exists(PathStr)) return;
            var name = File.ReadAllText(PathStr).Trim();
            for (int i = 0; i < All.Length; i++)
                if (All[i].Name == name) { _idx = i; break; }
            _lastSeenUtc = File.GetLastWriteTimeUtc(PathStr);
        }
        catch { /* fall back to default */ }
    }

    /// <summary>Move to the next theme and persist. Returns the new theme.</summary>
    public static ChessBoardTheme Cycle()
    {
        _idx = (_idx + 1) % All.Length;
        Save();
        return Current;
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(SaveManager.SaveDirectory);
            File.WriteAllText(PathStr, Current.Name);
            _lastSeenUtc = File.GetLastWriteTimeUtc(PathStr);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Pick up theme changes written by another process (the retro chess
    /// activity runs in MouseHouse.Activities companion when packaged).
    /// Same pattern as ThemeSync. Returns true if the theme just changed.
    /// </summary>
    public static bool PollExternalChange()
    {
        try
        {
            if (!File.Exists(PathStr)) return false;
            var mtime = File.GetLastWriteTimeUtc(PathStr);
            if (mtime <= _lastSeenUtc) return false;
            _lastSeenUtc = mtime;
            var name = File.ReadAllText(PathStr).Trim();
            for (int i = 0; i < All.Length; i++)
                if (All[i].Name == name) { _idx = i; return true; }
            return false;
        }
        catch { return false; }
    }
}
