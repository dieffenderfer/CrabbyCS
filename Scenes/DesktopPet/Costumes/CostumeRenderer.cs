using System.Numerics;
using Raylib_cs;

namespace MouseHouse.Scenes.DesktopPet.Costumes;

public enum CostumeType
{
    None,
    PartyHat,
    WitchHat,
    TopHat,
    Crown,
    Scarf,
    Sunglasses,
}

/// <summary>
/// Procedurally-drawn pixel-art accessories that overlay the pet sprite.
/// Costumes are positioned relative to the pet's head — flipping with the
/// sprite so the hat tilts with the pet's facing. Coordinates are tuned for
/// the 76×76 mouse frame; the renderer scales linearly with the pet scale.
/// </summary>
public static class CostumeRenderer
{
    public static readonly (CostumeType Type, string Name)[] All =
    {
        (CostumeType.None,        "None"),
        (CostumeType.PartyHat,    "Party Hat"),
        (CostumeType.WitchHat,    "Witch Hat"),
        (CostumeType.TopHat,      "Top Hat"),
        (CostumeType.Crown,       "Crown"),
        (CostumeType.Scarf,       "Scarf"),
        (CostumeType.Sunglasses,  "Sunglasses"),
    };

    private const int FrameSize = 76;

    /// <summary>Draws the costume on top of the pet sprite at its current pose.</summary>
    public static void Draw(CostumeType type, Vector2 petPos, float scale, bool flipH, float yBob)
    {
        if (type == CostumeType.None) return;

        // Head anchor in sprite space (pet faces left by default; FlipH = true means facing right).
        // Empirically the head sits around (24, 16) in the 76px frame when facing left.
        Vector2 headLocal = new(24, 16 + yBob);
        Vector2 noseLocal = new(8, 38 + yBob);
        Vector2 neckLocal = new(28, 36 + yBob);

        if (flipH)
        {
            headLocal.X = FrameSize - headLocal.X;
            noseLocal.X = FrameSize - noseLocal.X;
            neckLocal.X = FrameSize - neckLocal.X;
        }

        Vector2 head = petPos + headLocal * scale;
        Vector2 nose = petPos + noseLocal * scale;
        Vector2 neck = petPos + neckLocal * scale;

        switch (type)
        {
            case CostumeType.PartyHat:    DrawPartyHat(head, scale); break;
            case CostumeType.WitchHat:    DrawWitchHat(head, scale); break;
            case CostumeType.TopHat:      DrawTopHat(head, scale); break;
            case CostumeType.Crown:       DrawCrown(head, scale); break;
            case CostumeType.Scarf:       DrawScarf(neck, scale, flipH); break;
            case CostumeType.Sunglasses:  DrawSunglasses(nose, scale, flipH); break;
        }
    }

    private static Color C(int r, int g, int b) => new((byte)r, (byte)g, (byte)b, (byte)255);

    private static void DrawPartyHat(Vector2 head, float scale)
    {
        // Cone hat with stripes + a pom-pom on top.
        float h = 22 * scale;
        float w = 18 * scale;
        var apex = head + new Vector2(0, -h);
        var left = head + new Vector2(-w / 2, 0);
        var right = head + new Vector2(w / 2, 0);
        Raylib.DrawTriangle(apex, left, right, C(255, 80, 120));
        // Stripes — alternating yellow horizontals on the cone.
        for (int i = 1; i <= 3; i++)
        {
            float t = i / 4f;
            var l = Vector2.Lerp(left, apex, t);
            var r = Vector2.Lerp(right, apex, t);
            Raylib.DrawLineEx(l, r, 1.5f * scale,
                i % 2 == 0 ? C(255, 240, 80) : C(255, 255, 255));
        }
        // Pom-pom
        Raylib.DrawCircleV(apex + new Vector2(0, -1 * scale), 3 * scale, C(255, 255, 255));
    }

    private static void DrawWitchHat(Vector2 head, float scale)
    {
        float h = 26 * scale;
        float w = 22 * scale;
        var apex = head + new Vector2(-2 * scale, -h);
        var left = head + new Vector2(-w / 2, 0);
        var right = head + new Vector2(w / 2, 0);
        // Brim
        Raylib.DrawEllipse((int)head.X, (int)head.Y, w / 2 + 4 * scale, 3 * scale, C(20, 16, 30));
        // Cone
        Raylib.DrawTriangle(apex, left, right, C(20, 16, 30));
        // Buckle band
        var bl = Vector2.Lerp(left, apex, 0.18f);
        var br = Vector2.Lerp(right, apex, 0.18f);
        Raylib.DrawLineEx(bl, br, 2.5f * scale, C(170, 130, 60));
        Raylib.DrawRectangleV(Vector2.Lerp(bl, br, 0.45f) - new Vector2(2 * scale, 1.5f * scale),
            new Vector2(4 * scale, 3 * scale), C(255, 220, 100));
    }

    private static void DrawTopHat(Vector2 head, float scale)
    {
        float h = 14 * scale;
        float w = 16 * scale;
        // Brim
        Raylib.DrawRectangleV(head + new Vector2(-w / 2 - 3 * scale, -2 * scale),
            new Vector2(w + 6 * scale, 3 * scale), C(20, 20, 24));
        // Crown
        Raylib.DrawRectangleV(head + new Vector2(-w / 2, -h),
            new Vector2(w, h - 2 * scale), C(20, 20, 24));
        // Hatband
        Raylib.DrawRectangleV(head + new Vector2(-w / 2, -4 * scale),
            new Vector2(w, 2 * scale), C(160, 30, 40));
    }

    private static void DrawCrown(Vector2 head, float scale)
    {
        float h = 10 * scale;
        float w = 18 * scale;
        var gold = C(255, 210, 80);
        // Base band
        Raylib.DrawRectangleV(head + new Vector2(-w / 2, -h / 2),
            new Vector2(w, h / 2), gold);
        // Three points (zigzag)
        for (int i = 0; i < 3; i++)
        {
            float x = -w / 2 + i * (w / 2);
            var p0 = head + new Vector2(x, -h / 2);
            var p1 = head + new Vector2(x + w / 4, -h);
            var p2 = head + new Vector2(x + w / 2, -h / 2);
            Raylib.DrawTriangle(p1, p2, p0, gold);
        }
        // Gem on the centre point
        Raylib.DrawCircleV(head + new Vector2(0, -h + 2 * scale), 1.5f * scale, C(220, 60, 80));
    }

    private static void DrawScarf(Vector2 neck, float scale, bool flipH)
    {
        var red = C(200, 50, 60);
        var darkRed = C(140, 30, 40);
        // Neck wrap
        Raylib.DrawRectangleV(neck + new Vector2(-10 * scale, -2 * scale),
            new Vector2(20 * scale, 5 * scale), red);
        Raylib.DrawRectangleV(neck + new Vector2(-10 * scale, 1 * scale),
            new Vector2(20 * scale, 1 * scale), darkRed);
        // Trailing tail behind the pet (the side it isn't facing toward).
        float tailX = flipH ? -14 * scale : 8 * scale;
        Raylib.DrawRectangleV(neck + new Vector2(tailX, 1 * scale),
            new Vector2(8 * scale, 8 * scale), red);
        Raylib.DrawRectangleV(neck + new Vector2(tailX + 1 * scale, 7 * scale),
            new Vector2(7 * scale, 1 * scale), darkRed);
    }

    private static void DrawSunglasses(Vector2 nose, float scale, bool flipH)
    {
        // Two black ovals + a bridge. Pet faces left by default — when flipped
        // the glasses naturally mirror because we draw relative to nose.
        var frame = C(20, 20, 24);
        float lensR = 4f * scale;
        float dx = flipH ? -1f : 1f;     // bridge slants slightly down toward face
        var leftLens = nose + new Vector2(-2 * scale * dx, -8 * scale);
        var rightLens = nose + new Vector2(8 * scale * dx, -10 * scale);
        Raylib.DrawCircleV(leftLens, lensR, frame);
        Raylib.DrawCircleV(rightLens, lensR, frame);
        Raylib.DrawLineEx(leftLens + new Vector2(lensR * 0.7f * dx, 0),
                          rightLens - new Vector2(lensR * 0.7f * dx, 0),
                          1.5f * scale, frame);
        // Tiny highlight on each lens for sheen
        Raylib.DrawCircleV(leftLens + new Vector2(-1 * scale, -1 * scale), 0.8f * scale, C(180, 200, 240));
        Raylib.DrawCircleV(rightLens + new Vector2(-1 * scale, -1 * scale), 0.8f * scale, C(180, 200, 240));
    }
}
