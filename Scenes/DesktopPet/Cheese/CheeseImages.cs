using System.Numerics;
using Raylib_cs;

namespace MouseHouse.Scenes.DesktopPet.Cheese;

/// <summary>
/// Loads and serves the per-variety cheese PNG sprites in
/// <c>assets/pet/cheese/cheese_*.png</c>. Replaces the procedural pixel
/// art that used to live in <see cref="CheeseSprites"/> for the
/// placed-on-desktop cheese and the placement-mode ghost.
///
/// Each <see cref="CheeseType"/> maps to a filename suffix; types that
/// don't have a hand-drawn PNG fall back to a sensible substitute so
/// the menu's full cheese roster keeps working.
/// </summary>
public static class CheeseImages
{
    private static readonly Dictionary<CheeseType, Texture2D> _textures = new();
    // Per-type opaque-pixel info: each cell is (x, y, color). Used by the
    // dissolve renderer (CheeseManager) so eaten cheese visually flicks
    // out one pixel at a time — same snap-out feel as the old procedural
    // CheddarDissolve, just driven by the actual sprite art.
    public readonly record struct OpaquePixel(int X, int Y, Color Color);
    private static readonly Dictionary<CheeseType, OpaquePixel[]> _opaquePixels = new();
    private static readonly Dictionary<CheeseType, (int W, int H)> _spriteSizes = new();
    private static bool _loaded;

    /// <summary>Map a CheeseType to the basename in assets/pet/cheese/.
    /// Types without their own PNG fall back to a near neighbour so the
    /// existing menu items still render.</summary>
    private static string FileFor(CheeseType t) => t switch
    {
        CheeseType.Cheddar      => "cheese_cheddar.png",
        CheeseType.Swiss        => "cheese_swiss.png",
        CheeseType.Brie         => "cheese_brie.png",
        CheeseType.Gouda        => "cheese_gouda.png",
        CheeseType.Parmesan     => "cheese_parmesan.png",
        CheeseType.Mozzarella   => "cheese_mozzarella.png",
        CheeseType.Blue         => "cheese_blue.png",
        CheeseType.Camembert    => "cheese_camembert.png",
        CheeseType.Feta         => "cheese_feta.png",
        CheeseType.Edam         => "cheese_edam.png",
        CheeseType.Goat         => "cheese_goat.png",
        // No bespoke art yet — share a close-cousin sprite so the menu
        // entry still does something visible.
        CheeseType.PepperJack   => "cheese_cheddar.png",
        CheeseType.ColbyJack    => "cheese_cheddar.png",
        CheeseType.StringCheese => "cheese_mozzarella.png",
        _                       => "cheese_cheddar.png",
    };

    /// <summary>
    /// The list of cheese types that have a real PNG of their own (used
    /// when randomly picking a cheese to place — we prefer to spawn
    /// varieties the player will actually see distinct art for).
    /// </summary>
    public static readonly CheeseType[] Available =
    {
        CheeseType.Cheddar, CheeseType.Swiss, CheeseType.Brie,
        CheeseType.Gouda, CheeseType.Parmesan, CheeseType.Mozzarella,
        CheeseType.Blue, CheeseType.Camembert, CheeseType.Feta,
        CheeseType.Edam, CheeseType.Goat,
    };

    public static void Load(MouseHouse.Core.AssetCache assets)
    {
        if (_loaded) return;
        foreach (var t in Available)
        {
            // Funnel through AssetCache so the textures hot-reload along
            // with the rest of the project's pixel art (and so we don't
            // double-load if other code asks for the same file).
            var rel = "assets/pet/cheese/" + FileFor(t);
            var tex = assets.GetTexture(rel);
            if (tex.Width == 0) continue;
            _textures[t] = tex;

            // Snapshot the sprite's opaque pixels so the per-pixel
            // dissolve renderer can drop them one at a time. Done once
            // at load — the sprites are ~30x30 so the buffer is tiny
            // (~1k entries each).
            var fullPath = Path.Combine(assets.BasePath, rel);
            var img = Raylib.LoadImage(fullPath);
            Raylib.ImageFormat(ref img, PixelFormat.UncompressedR8G8B8A8);
            var list = new List<OpaquePixel>(img.Width * img.Height);
            unsafe
            {
                var pixels = (byte*)img.Data;
                for (int y = 0; y < img.Height; y++)
                {
                    for (int x = 0; x < img.Width; x++)
                    {
                        int i = (y * img.Width + x) * 4;
                        byte a = pixels[i + 3];
                        if (a < 16) continue;       // skip near-transparent
                        list.Add(new OpaquePixel(x, y,
                            new Color(pixels[i], pixels[i + 1], pixels[i + 2], a)));
                    }
                }
            }
            _opaquePixels[t] = list.ToArray();
            _spriteSizes[t] = (img.Width, img.Height);
            Raylib.UnloadImage(img);
        }
        _loaded = true;
    }

    public static OpaquePixel[] GetOpaquePixels(CheeseType t)
    {
        if (_opaquePixels.TryGetValue(t, out var arr)) return arr;
        var alias = AliasOf(t);
        if (alias.HasValue && _opaquePixels.TryGetValue(alias.Value, out arr)) return arr;
        return Array.Empty<OpaquePixel>();
    }

    public static (int W, int H) GetSpriteSize(CheeseType t)
    {
        if (_spriteSizes.TryGetValue(t, out var s)) return s;
        var alias = AliasOf(t);
        if (alias.HasValue && _spriteSizes.TryGetValue(alias.Value, out s)) return s;
        return (0, 0);
    }

    // Falling-pixel animation parameters. A pixel transitions to
    // hidden when its slot crosses into [0..hideCount); for the next
    // FallLife seconds it renders falling under Gravity and fading,
    // so the cheese visibly sheds chunks instead of teleporting them
    // out of existence.
    private const float FallLife = 0.22f;
    private const float Gravity  = 460f;     // px/s²

    /// <summary>
    /// Pixel-by-pixel dissolve render with a brief fall-under-gravity
    /// animation per newly-hidden pixel. <paramref name="dissolveOrder"/>
    /// is a permutation of [0..opaquePixelCount); the first
    /// <paramref name="hideCount"/> entries are slated for hiding.
    /// <paramref name="hideTimes"/> is per-pixel state (sized to the
    /// opaque-pixel count) that records when each pixel first crossed
    /// into the hidden range — set to -1 means "still visible". On the
    /// frame a pixel transitions, we stamp <paramref name="time"/> into
    /// hideTimes; the pixel then renders at its original position with
    /// a downward offset and fading alpha until <c>age &gt;= FallLife</c>,
    /// after which it stops drawing.
    /// </summary>
    public static void DrawDissolve(CheeseType type, Vector2 center,
        int[] dissolveOrder, int hideCount, int cellPx,
        float[] hideTimes, float time)
    {
        if (cellPx < 1) cellPx = 1;
        var pixels = GetOpaquePixels(type);
        if (pixels.Length == 0) return;
        var (sw, sh) = GetSpriteSize(type);
        int x0 = (int)MathF.Round(center.X) - (sw * cellPx) / 2;
        int y0 = (int)MathF.Round(center.Y) - (sh * cellPx) / 2;

        int n = pixels.Length;
        // Stale instance from before this code path existed — sizes don't
        // line up. Render fully visible without animation.
        if (dissolveOrder.Length < n || hideTimes.Length < n)
        {
            for (int i = 0; i < n; i++)
            {
                var p = pixels[i];
                Raylib.DrawRectangle(x0 + p.X * cellPx, y0 + p.Y * cellPx,
                    cellPx, cellPx, p.Color);
            }
            return;
        }

        int hide = Math.Clamp(hideCount, 0, n);

        // Hidden slots [0..hide). Stamp hideTime on first sight, then
        // render with fall offset and a smooth linear alpha fade across
        // FallLife.
        for (int slot = 0; slot < hide; slot++)
        {
            int pi = dissolveOrder[slot];
            if (hideTimes[pi] < 0f) hideTimes[pi] = time;
            float age = time - hideTimes[pi];
            if (age >= FallLife) continue;
            var p = pixels[pi];
            float dy = 0.5f * Gravity * age * age;
            byte alpha = (byte)Math.Clamp((int)(255f * (1f - age / FallLife)), 0, 255);
            var col = new Color(p.Color.R, p.Color.G, p.Color.B, alpha);
            Raylib.DrawRectangle(x0 + p.X * cellPx,
                                 y0 + (int)(p.Y * cellPx + dy),
                                 cellPx, cellPx, col);
        }
        // Visible slots [hide..n). Render at their original sprite-pixel
        // positions with full opacity.
        for (int slot = hide; slot < n; slot++)
        {
            int pi = dissolveOrder[slot];
            var p = pixels[pi];
            Raylib.DrawRectangle(x0 + p.X * cellPx, y0 + p.Y * cellPx,
                cellPx, cellPx, p.Color);
        }
    }

    public static bool TryGet(CheeseType t, out Texture2D tex)
    {
        if (_textures.TryGetValue(t, out tex)) return true;
        // Fallback path for the alias types (PepperJack etc).
        var alias = AliasOf(t);
        if (alias.HasValue && _textures.TryGetValue(alias.Value, out tex)) return true;
        tex = default;
        return false;
    }

    private static CheeseType? AliasOf(CheeseType t) => t switch
    {
        CheeseType.PepperJack   => CheeseType.Cheddar,
        CheeseType.ColbyJack    => CheeseType.Cheddar,
        CheeseType.StringCheese => CheeseType.Mozzarella,
        _ => null,
    };

    /// <summary>
    /// Draw the cheese centred on <paramref name="center"/>. Each native
    /// sprite pixel is rendered as a <paramref name="cellPx"/>-square
    /// block so the result scales cleanly with the pet's scale (and the
    /// dissolve renderer below uses the same cell size, so an in-progress
    /// eat can't visually mismatch a fresh cheese in the same frame).
    /// </summary>
    public static void Draw(CheeseType type, Vector2 center, int cellPx)
    {
        if (!TryGet(type, out var tex)) return;
        if (cellPx < 1) cellPx = 1;
        float w = tex.Width * cellPx;
        float h = tex.Height * cellPx;
        var src = new Rectangle(0, 0, tex.Width, tex.Height);
        var dst = new Rectangle(center.X - w / 2f, center.Y - h / 2f, w, h);
        Raylib.DrawTexturePro(tex, src, dst, Vector2.Zero, 0f, Color.White);
    }
}
