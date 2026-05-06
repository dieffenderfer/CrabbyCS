using System.Numerics;
using Raylib_cs;

namespace MouseHouse.Scenes.DesktopPet.Cheese;

/// <summary>
/// Procedurally-drawn pixel-art cheese icons. Each cheese variety has a
/// distinctive silhouette (wedge / wheel / cube / stick) and palette so
/// they read at a glance without needing 12 hand-drawn sprite files.
/// All sprites are drawn centered on <paramref name="pos"/>, scaled by
/// <paramref name="scale"/> (1.0 = ~24 px), and respect <paramref name="alpha"/>
/// for fade-out. Drawn with hard-edge primitives so they look right on a
/// transparent overlay alongside the pixel-art mouse.
/// </summary>
public static class CheeseSprites
{
    public static void Draw(CheeseType type, Vector2 pos, float scale, byte alpha = 255)
    {
        switch (type)
        {
            case CheeseType.Cheddar:      DrawCheddar(pos, scale, alpha); break;
            case CheeseType.Swiss:        DrawSwiss(pos, scale, alpha); break;
            case CheeseType.Brie:         DrawBrie(pos, scale, alpha); break;
            case CheeseType.Gouda:        DrawGouda(pos, scale, alpha); break;
            case CheeseType.Parmesan:     DrawParmesan(pos, scale, alpha); break;
            case CheeseType.Mozzarella:   DrawMozzarella(pos, scale, alpha); break;
            case CheeseType.Blue:         DrawBlue(pos, scale, alpha); break;
            case CheeseType.Camembert:    DrawCamembert(pos, scale, alpha); break;
            case CheeseType.Feta:         DrawFeta(pos, scale, alpha); break;
            case CheeseType.PepperJack:   DrawPepperJack(pos, scale, alpha); break;
            case CheeseType.ColbyJack:    DrawColbyJack(pos, scale, alpha); break;
            case CheeseType.StringCheese: DrawStringCheese(pos, scale, alpha); break;
        }
    }

    private static Color C(int r, int g, int b, byte a) => new((byte)r, (byte)g, (byte)b, a);

    private static void Wedge(Vector2 pos, float scale, byte a, Color body, Color rind)
    {
        // Right-triangle wedge with a darker rind line along the hypotenuse.
        float s = 12 * scale;
        var p1 = pos + new Vector2(-s, s * 0.55f);
        var p2 = pos + new Vector2(s, s * 0.55f);
        var p3 = pos + new Vector2(-s, -s * 0.55f);
        Raylib.DrawTriangle(p1, p2, p3, new Color(body.R, body.G, body.B, a));
        // Crust edge
        Raylib.DrawLineEx(p3, p2, MathF.Max(1f, scale * 1.2f), new Color(rind.R, rind.G, rind.B, a));
        Raylib.DrawLineEx(p1, p3, MathF.Max(1f, scale * 1.2f), new Color(rind.R, rind.G, rind.B, a));
    }

    private static void DrawCheddar(Vector2 pos, float scale, byte a)
    {
        // Bright orange wedge with darker orange edge.
        Wedge(pos, scale, a, C(244, 168, 60, a), C(180, 110, 30, a));
        // Inner highlight for depth.
        var hi = new Color((byte)255, (byte)210, (byte)120, a);
        Raylib.DrawCircleV(pos + new Vector2(-scale * 4, -scale * 1), scale * 1.3f, hi);
    }

    private static void DrawSwiss(Vector2 pos, float scale, byte a)
    {
        // Pale yellow wedge with several round holes.
        Wedge(pos, scale, a, C(248, 232, 132, a), C(168, 140, 60, a));
        var hole = C(80, 60, 20, a);
        Raylib.DrawCircleV(pos + new Vector2(-scale * 3, scale * 0), scale * 1.4f, hole);
        Raylib.DrawCircleV(pos + new Vector2(-scale * 6, scale * 2.5f), scale * 1.0f, hole);
        Raylib.DrawCircleV(pos + new Vector2(-scale * 1, scale * 3.5f), scale * 0.9f, hole);
        Raylib.DrawCircleV(pos + new Vector2(scale * 3, scale * 1.5f), scale * 0.7f, hole);
    }

    private static void DrawBrie(Vector2 pos, float scale, byte a)
    {
        // Soft cream wedge with white bloomy rind on the curved edge.
        var body = C(248, 240, 200, a);
        var rind = C(240, 240, 232, a);
        float s = 12 * scale;
        var p1 = pos + new Vector2(-s, s * 0.55f);
        var p2 = pos + new Vector2(s, s * 0.55f);
        var p3 = pos + new Vector2(-s, -s * 0.55f);
        Raylib.DrawTriangle(p1, p2, p3, body);
        // Thicker pale rind on the long edge (hypotenuse-ish).
        Raylib.DrawLineEx(p2, p3, MathF.Max(2f, scale * 2.2f), rind);
        Raylib.DrawLineEx(p1, p2, MathF.Max(1f, scale * 1.4f), C(220, 210, 170, a));
    }

    private static void DrawGouda(Vector2 pos, float scale, byte a)
    {
        // Round wheel with red wax coating.
        Raylib.DrawCircleV(pos, 12 * scale, C(192, 60, 50, a));
        // Inner wedge cut showing yellow flesh.
        Raylib.DrawCircleSector(pos, 11 * scale, -10, 100, 16, C(238, 200, 110, a));
        Raylib.DrawCircleLines((int)pos.X, (int)pos.Y, 12 * scale, C(120, 30, 30, a));
    }

    private static void DrawParmesan(Vector2 pos, float scale, byte a)
    {
        // Hard pale wedge with slightly rough texture (small dots).
        Wedge(pos, scale, a, C(238, 220, 150, a), C(170, 140, 80, a));
        var grain = C(200, 170, 100, a);
        for (int i = 0; i < 6; i++)
        {
            float ox = -scale * (4 - i);
            float oy = -scale * 1 + (i * scale * 0.5f);
            Raylib.DrawCircleV(pos + new Vector2(ox, oy), scale * 0.4f, grain);
        }
    }

    private static void DrawMozzarella(Vector2 pos, float scale, byte a)
    {
        // White ball with subtle highlight.
        Raylib.DrawCircleV(pos, 11 * scale, C(252, 250, 244, a));
        Raylib.DrawCircleV(pos + new Vector2(-scale * 3, -scale * 3), scale * 2.6f, C(255, 255, 255, a));
        Raylib.DrawCircleLines((int)pos.X, (int)pos.Y, 11 * scale, C(180, 180, 180, a));
    }

    private static void DrawBlue(Vector2 pos, float scale, byte a)
    {
        // Pale wedge with blue/green veins.
        Wedge(pos, scale, a, C(232, 222, 208, a), C(140, 130, 110, a));
        var vein = C(70, 90, 130, a);
        for (int i = 0; i < 8; i++)
        {
            float ox = -scale * (5 - i * 0.7f);
            float oy = -scale * 2 + (i * scale * 0.6f);
            Raylib.DrawCircleV(pos + new Vector2(ox, oy), scale * 0.7f, vein);
        }
        Raylib.DrawCircleV(pos + new Vector2(-scale * 2, scale * 3), scale * 0.8f, vein);
        Raylib.DrawCircleV(pos + new Vector2(scale * 1, scale * 2), scale * 0.6f, C(120, 160, 110, a));
    }

    private static void DrawCamembert(Vector2 pos, float scale, byte a)
    {
        // Round wheel, white rind, cream center wedge cut.
        Raylib.DrawCircleV(pos, 12 * scale, C(248, 240, 220, a));
        Raylib.DrawCircleSector(pos, 11 * scale, 200, 320, 16, C(252, 244, 220, a));
        Raylib.DrawCircleLines((int)pos.X, (int)pos.Y, 12 * scale, C(200, 180, 140, a));
    }

    private static void DrawFeta(Vector2 pos, float scale, byte a)
    {
        // White crumbly cube with rough edges (a few jagged highlights).
        float s = 11 * scale;
        Raylib.DrawRectangleV(pos - new Vector2(s, s * 0.8f), new Vector2(s * 2, s * 1.6f), C(252, 252, 252, a));
        Raylib.DrawRectangleLinesEx(new Rectangle(pos.X - s, pos.Y - s * 0.8f, s * 2, s * 1.6f),
            MathF.Max(1f, scale), C(180, 180, 180, a));
        // Rough crumble dots.
        var crumb = C(220, 220, 220, a);
        for (int i = 0; i < 5; i++)
        {
            Raylib.DrawCircleV(pos + new Vector2(-s + i * scale * 1.7f, s * 0.5f - i * scale * 0.3f),
                scale * 0.5f, crumb);
        }
    }

    private static void DrawPepperJack(Vector2 pos, float scale, byte a)
    {
        // Pale wedge with red pepper flecks.
        Wedge(pos, scale, a, C(244, 232, 180, a), C(170, 150, 90, a));
        var pep = C(220, 50, 50, a);
        for (int i = 0; i < 7; i++)
        {
            float ox = -scale * (4 - i * 0.6f);
            float oy = scale * (-2 + i * 0.6f);
            Raylib.DrawRectangleV(pos + new Vector2(ox, oy), new Vector2(scale * 0.9f, scale * 0.5f), pep);
        }
    }

    private static void DrawColbyJack(Vector2 pos, float scale, byte a)
    {
        // Marbled orange + white wedge.
        Wedge(pos, scale, a, C(248, 220, 150, a), C(170, 130, 60, a));
        var orange = C(240, 160, 60, a);
        // Marble bands.
        for (int i = 0; i < 4; i++)
        {
            float y = -scale * 3 + i * scale * 1.6f;
            Raylib.DrawRectangleV(pos + new Vector2(-scale * 7, y),
                new Vector2(scale * 9, scale * 0.7f), orange);
        }
    }

    private static void DrawStringCheese(Vector2 pos, float scale, byte a)
    {
        // Vertical white-ish stick with a wrapper line.
        float w = 4 * scale;
        float h = 18 * scale;
        Raylib.DrawRectangleV(pos - new Vector2(w / 2, h / 2),
            new Vector2(w, h), C(252, 250, 240, a));
        Raylib.DrawRectangleLinesEx(new Rectangle(pos.X - w / 2, pos.Y - h / 2, w, h),
            MathF.Max(1f, scale), C(170, 170, 170, a));
        // Twist-tie / rip line at the top.
        Raylib.DrawLineEx(pos + new Vector2(-w / 2 - 1, -h / 2 + 2),
                          pos + new Vector2(w / 2 + 1, -h / 2 + 2),
                          MathF.Max(1f, scale), C(120, 110, 80, a));
    }
}
