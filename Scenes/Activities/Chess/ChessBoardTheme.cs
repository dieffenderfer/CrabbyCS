using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities.Chess;

/// <summary>
/// One coherent board palette — the two square colours plus the matching
/// coordinate-label colour, the three move-accent tints (last-move
/// highlight, selected-square fill, legal-move dot), and the piece fill
/// colours. Themes hold all of these together so the accents always
/// read well against their squares and the pieces stand out against
/// both light and dark squares.
///
/// WhitePiece / BlackPiece default to the cream-and-charcoal pairing
/// the activity has always used — themes that want a coloured piece
/// look (or that fix a piece/square contrast collision, like Coral's
/// cream squares hiding cream-white pieces) override them.
/// </summary>
public record ChessBoardTheme(
    string Name,
    Color Light,
    Color Dark,
    Color CoordLabel,
    Color LastMoveTint,
    Color SelectedTint,
    Color LegalDot,
    Color? WhitePiece = null,
    Color? BlackPiece = null);

public static class ChessBoardThemes
{
    private static Color C(int r, int g, int b, int a = 255)
        => new((byte)r, (byte)g, (byte)b, (byte)a);

    public static readonly ChessBoardTheme[] All =
    {
        // Gunmetal — default. Cool-grey board several shades darker than
        // the surrounding Win9x chrome so the pieces sit on it without
        // visually melting into the panel face. Light/dark steps tuned
        // wide enough that the board still reads as a chequerboard at a
        // glance, with desaturated steel accents for last-move /
        // selection / legal-dot tints.
        new("Gunmetal",
            Light:        C(140, 142, 146),
            Dark:         C( 78,  82,  88),
            CoordLabel:   C(220, 222, 228, 230),
            LastMoveTint: C(245, 220, 110, 120),
            SelectedTint: C(140, 200, 255, 130),
            LegalDot:     C(110, 170, 220, 210)),

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

        // Coral — warm pink pairing. The previous cream light square
        // sat almost on top of the cream "white" piece fill, so white
        // pieces vanished on light squares. Pulling the light squares
        // toward pink-coral gives the cream pieces enough contrast,
        // and the dark squares stay the same warm coral red.
        new("Coral",
            Light:        C(252, 196, 188),
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

        // Royal — navy and gold. Cream squares stay neutral and let
        // the two team colours carry the look; the pieces become the
        // focal point instead of being the default cream/charcoal.
        new("Royal",
            Light:        C(232, 220, 184),
            Dark:         C( 78,  88, 130),
            CoordLabel:   C( 32,  40,  72, 230),
            LastMoveTint: C(255, 220, 110, 120),
            SelectedTint: C(180, 210, 255, 130),
            LegalDot:     C(220, 180,  60, 200),
            WhitePiece:   C(232, 184,  72),   // burnished gold
            BlackPiece:   C( 28,  40,  88)),  // deep navy

        // Cherry — strawberry-cream pairing. White and black pieces
        // become hot pink and forest green for a candy-shop look that
        // doesn't read as "competitive chess" so much as "fun board".
        new("Cherry",
            Light:        C(252, 232, 232),
            Dark:         C(200, 110, 124),
            CoordLabel:   C(100,  30,  44, 230),
            LastMoveTint: C(255, 220, 130, 120),
            SelectedTint: C(255, 200, 220, 140),
            LegalDot:     C(180,  60,  90, 200),
            WhitePiece:   C(232,  72, 132),   // hot pink
            BlackPiece:   C( 40,  72,  56)),  // forest green

        // Ocean — teal-on-sand with deep-sea pieces. The light
        // squares are warm sand so the dark navy pieces pop, and
        // the dark squares are a desaturated teal so the seafoam
        // "white" piece colour reads as a coherent water palette.
        new("Ocean",
            Light:        C(232, 224, 196),
            Dark:         C( 88, 140, 144),
            CoordLabel:   C( 30,  70,  80, 230),
            LastMoveTint: C(255, 220, 110, 120),
            SelectedTint: C(180, 230, 230, 130),
            LegalDot:     C( 40, 170, 180, 200),
            WhitePiece:   C(196, 232, 220),   // seafoam
            BlackPiece:   C( 20,  56,  88)),  // deep ocean
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

    /// <summary>Set the active theme by index without persisting —
    /// used for hover-previews in the theme submenu so cursor wobble
    /// over a name doesn't write to disk. Caller is expected to
    /// restore the committed index when the preview ends, or call
    /// <see cref="Commit"/> to make the change stick.</summary>
    public static void SetPreview(int idx)
    {
        if (idx < 0 || idx >= All.Length) return;
        _idx = idx;
    }

    /// <summary>Set the active theme by index AND persist — the
    /// commit-on-click variant for the theme submenu.</summary>
    public static void Commit(int idx)
    {
        if (idx < 0 || idx >= All.Length) return;
        _idx = idx;
        Save();
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
