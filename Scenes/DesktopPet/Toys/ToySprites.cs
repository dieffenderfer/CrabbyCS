using System.Numerics;
using Raylib_cs;

namespace MouseHouse.Scenes.DesktopPet.Toys;

/// <summary>
/// Procedurally-drawn pixel-art toys, same approach as the cheese sprites.
/// Each toy is anchored at its top-left for predictable hit-testing and
/// pet-routing. Variable state (wheel rotation, ball position) is passed
/// in by the manager so each toy is just a draw routine.
/// </summary>
public static class ToySprites
{
    private static Color C(int r, int g, int b, int a = 255) => new((byte)r, (byte)g, (byte)b, (byte)a);

    public static void DrawBed(Vector2 origin, int w, int h)
    {
        // Rounded cushion: dark wooden base + striped fabric pillow.
        var frame = C(110, 70, 40);
        var stripeA = C(220, 120, 120);
        var stripeB = C(240, 220, 200);
        var shadow = C(0, 0, 0, 70);

        // Soft drop shadow under the bed.
        Raylib.DrawEllipse((int)origin.X + w / 2, (int)origin.Y + h - 1,
            w / 2 - 2, 5, shadow);

        // Frame base (the wooden tray).
        Raylib.DrawRectangleRounded(new Rectangle(origin.X, origin.Y + 4, w, h - 4),
            0.4f, 6, frame);
        // Inner cushion.
        Raylib.DrawRectangleRounded(new Rectangle(origin.X + 3, origin.Y + 7, w - 6, h - 12),
            0.5f, 6, stripeB);
        // Fabric stripes
        for (int i = 0; i < 4; i++)
        {
            int yy = (int)origin.Y + 8 + i * 4;
            Raylib.DrawRectangle((int)origin.X + 4, yy, w - 8, 2, stripeA);
        }
        // Headboard at the right end.
        Raylib.DrawRectangleRounded(new Rectangle(origin.X + w - 12, origin.Y, 12, h),
            0.5f, 6, frame);
    }

    public static void DrawWheel(Vector2 origin, int w, int h, float angleRad)
    {
        // Hamster wheel: big circle (rim), spokes that rotate by angleRad,
        // axle pin, small base/stand.
        float cx = origin.X + w / 2f;
        float cy = origin.Y + h / 2f - 2;
        float radius = MathF.Min(w, h - 8) / 2f - 2;

        // Stand
        var stand = C(120, 90, 60);
        Raylib.DrawRectangleRounded(new Rectangle(cx - 6, cy + radius - 4, 12, 18), 0.4f, 4, stand);
        Raylib.DrawRectangleRounded(new Rectangle(origin.X + 4, origin.Y + h - 6,
            w - 8, 4), 0.6f, 4, stand);

        // Outer rim (thick)
        var rim = C(200, 200, 210);
        Raylib.DrawCircleV(new Vector2(cx, cy), radius, rim);
        Raylib.DrawCircleV(new Vector2(cx, cy), radius - 4, C(40, 40, 50));

        // Spokes
        var spoke = C(180, 180, 190);
        const int spokeCount = 8;
        for (int i = 0; i < spokeCount; i++)
        {
            float a = angleRad + i * (MathF.PI * 2f / spokeCount);
            var p = new Vector2(cx + MathF.Cos(a) * (radius - 4),
                                cy + MathF.Sin(a) * (radius - 4));
            Raylib.DrawLineEx(new Vector2(cx, cy), p, 2f, spoke);
        }

        // Axle hub
        Raylib.DrawCircleV(new Vector2(cx, cy), 3, C(80, 80, 90));
    }

    public static void DrawWaterBottle(Vector2 origin, int w, int h, float drip)
    {
        // Cap, transparent body with water level, drip tip at the bottom.
        var cap = C(200, 60, 60);
        var bottle = C(220, 230, 240, 220);
        var water = C(120, 180, 230, 180);
        var stem = C(160, 160, 170);

        // Wall mount at the top.
        Raylib.DrawRectangle((int)origin.X, (int)origin.Y, w, 4, cap);
        // Body
        Raylib.DrawRectangleRounded(new Rectangle(origin.X + 2, origin.Y + 3, w - 4, h - 14),
            0.4f, 6, bottle);
        // Water inside (level oscillates slowly via drip param 0..1)
        float waterLevel = 0.55f + 0.10f * drip;
        int waterTop = (int)(origin.Y + 5 + (h - 18) * (1 - waterLevel));
        Raylib.DrawRectangle((int)origin.X + 4, waterTop, w - 8,
            (int)(origin.Y + h - 11) - waterTop, water);
        // Stem + drip tip at the bottom.
        Raylib.DrawRectangle((int)origin.X + w / 2 - 1, (int)origin.Y + h - 12, 2, 6, stem);
        // A drop at the tip if we're "actively dripping" (drip > 0.6).
        if (drip > 0.6f)
            Raylib.DrawCircle((int)origin.X + w / 2, (int)origin.Y + h - 4,
                1.5f + (drip - 0.6f) * 4f, water);
    }

    public static void DrawBall(Vector2 center, float radius, float spin)
    {
        // Striped rubber ball: red & white wedges that rotate as it rolls.
        var red = C(220, 60, 60);
        var white = C(248, 248, 248);
        Raylib.DrawCircleV(center, radius, white);
        // Two wedges 180° apart, rotating with spin.
        Raylib.DrawCircleSector(center, radius - 0.5f,
            (spin * 57.2958f) % 360, (spin * 57.2958f + 60) % 360 + (spin > 0 ? 0 : 360), 12, red);
        Raylib.DrawCircleSector(center, radius - 0.5f,
            (spin * 57.2958f + 180) % 360, (spin * 57.2958f + 240) % 360, 12, red);
        // Outline
        Raylib.DrawCircleLines((int)center.X, (int)center.Y, radius, C(40, 30, 30));
        // Tiny highlight
        Raylib.DrawCircleV(center + new Vector2(-radius * 0.4f, -radius * 0.4f),
            MathF.Max(1f, radius * 0.18f), C(255, 255, 255, 220));
    }
}
