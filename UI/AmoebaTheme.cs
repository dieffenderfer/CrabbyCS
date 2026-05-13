using System.Numerics;
using Raylib_cs;

namespace MouseHouse.UI;

/// <summary>
/// Optional "amoeba dripped window" cosmetic effect — procedural green
/// slime that hangs from the bottom edge of windows. Crisp 1-px pixel-art
/// edges (no anti-aliasing) so it sits comfortably alongside the Win95-era
/// retro chrome. Drip heights are seeded by window position so they don't
/// shimmer when the window stays still and don't repeat exactly across
/// different windows that happen to share a width.
/// </summary>
public static class AmoebaTheme
{
    public static bool Enabled;

    private static readonly Color Slime    = new(96, 200, 80, 235);
    private static readonly Color SlimeMid = new(64, 160, 56, 245);
    private static readonly Color SlimeRim = new(36, 100, 36, 255);
    private static readonly Color Highlight = new(180, 232, 140, 220);

    private const int MinDrip = 4;
    private const int MaxDrip = 28;

    /// <summary>
    /// Draw a row of pixelated drips along the bottom edge of <paramref name="windowRect"/>.
    /// Each column-of-pixels gets an independent length sampled from a seeded RNG so
    /// the same window always grows the same drips between frames.
    /// </summary>
    public static void DrawDrips(Rectangle windowRect)
    {
        if (!Enabled) return;
        int x0 = (int)windowRect.X;
        int y0 = (int)(windowRect.Y + windowRect.Height);
        int w = (int)windowRect.Width;

        // Seed is derived from the window's integer top-left position so a
        // window that stays put grows a stable drip profile. Two windows at
        // the same coords would share a profile but that's vanishingly rare
        // for the small handful of widgets the app has.
        int seed = ((int)windowRect.X * 73856093) ^ ((int)windowRect.Y * 19349663)
                 ^ ((int)windowRect.Width * 83492791);
        var rng = new Random(seed);

        // Underglob — a thin slime band hugging the bottom edge so the drips
        // grow out of a continuous strip rather than 'floating' off the frame.
        Raylib.DrawRectangle(x0, y0, w, 2, SlimeMid);
        Raylib.DrawRectangle(x0, y0 + 2, w, 1, SlimeRim);

        // Columnar drips. Step every 3-7 px so the profile is irregular
        // without being shrieking-pixelated.
        int x = x0 + 2;
        while (x < x0 + w - 2)
        {
            int step = 3 + rng.Next(5);
            int dripW = 2 + rng.Next(4);
            int dripH = MinDrip + rng.Next(MaxDrip - MinDrip);

            // Body
            Raylib.DrawRectangle(x, y0, dripW, dripH, Slime);
            // 1-px dark rim left/right/bottom — gives the pixel-art outline.
            Raylib.DrawRectangle(x - 1, y0, 1, dripH, SlimeRim);
            Raylib.DrawRectangle(x + dripW, y0, 1, dripH, SlimeRim);
            Raylib.DrawRectangle(x - 1, y0 + dripH, dripW + 2, 1, SlimeRim);
            // Highlight on the left edge for that wet-plastic look.
            if (dripW >= 3)
                Raylib.DrawRectangle(x, y0, 1, Math.Max(1, dripH - 2), Highlight);

            // Occasional "tear" — a single pixel pinching off below the drip,
            // suggesting it's about to fall. Same seeded RNG so it doesn't
            // sparkle frame-to-frame.
            if (rng.Next(6) == 0)
            {
                int tearY = y0 + dripH + 2 + rng.Next(6);
                Raylib.DrawRectangle(x + dripW / 2, tearY, 2, 2, Slime);
                Raylib.DrawRectangle(x + dripW / 2 - 1, tearY, 1, 2, SlimeRim);
                Raylib.DrawRectangle(x + dripW / 2 + 2, tearY, 1, 2, SlimeRim);
                Raylib.DrawRectangle(x + dripW / 2, tearY + 2, 2, 1, SlimeRim);
            }

            x += dripW + step;
        }
    }

    /// <summary>Helper overload that builds the rect from origin + size.</summary>
    public static void DrawDrips(Vector2 origin, Vector2 size)
        => DrawDrips(new Rectangle(origin.X, origin.Y, size.X, size.Y));
}
