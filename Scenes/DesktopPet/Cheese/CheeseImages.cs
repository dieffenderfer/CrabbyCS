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
        }
        _loaded = true;
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
    /// Draw the cheese centred on <paramref name="center"/>, scaled so
    /// the sprite reads at roughly 28-32 px tall at scale=1.0. Honours
    /// <paramref name="alpha"/> for the eaten-tail fade.
    /// </summary>
    public static void Draw(CheeseType type, Vector2 center, float scale, byte alpha = 255)
    {
        if (!TryGet(type, out var tex)) return;
        // Treat the sprite as already pixel-art-sized; use scale as a
        // multiplier on its native pixels.
        float w = tex.Width * scale;
        float h = tex.Height * scale;
        var src = new Rectangle(0, 0, tex.Width, tex.Height);
        var dst = new Rectangle(center.X - w / 2f, center.Y - h / 2f, w, h);
        var tint = new Color((byte)255, (byte)255, (byte)255, alpha);
        Raylib.DrawTexturePro(tex, src, dst, Vector2.Zero, 0f, tint);
    }
}
