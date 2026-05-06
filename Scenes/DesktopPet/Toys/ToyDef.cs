namespace MouseHouse.Scenes.DesktopPet.Toys;

public enum ToyType
{
    Bed,
    Wheel,
    WaterBottle,
    Ball,
}

/// <summary>
/// Static metadata per toy variety: how big it is, what part of it the pet
/// stands on when interacting, how long the interaction loop lasts before
/// the pet might wander off, and how appealing it is when several toys
/// are on screen at once.
/// </summary>
public record ToyDef(
    ToyType Type,
    string Name,
    string Glyph,            // for the menu, drawn through GlyphFallback
    int Width,
    int Height,
    /// <summary>
    /// Offset from the toy's top-left to where the pet should park its
    /// center while interacting. Drives both arrival detection and where
    /// the use-state animation plays.
    /// </summary>
    System.Numerics.Vector2 InteractAnchor,
    float UseSeconds,
    float Preference          // ≥ 1 attracts faster; < 1 dawdles
);

public static class Toys
{
    public static readonly ToyDef[] All =
    {
        new(ToyType.Bed,         "Bed",          "▭", 64, 28, new(32, 14), 6.0f, 0.9f),
        new(ToyType.Wheel,       "Wheel",        "◯", 72, 76, new(36, 60), 5.0f, 1.3f),
        new(ToyType.WaterBottle, "Water Bottle", "▽", 24, 56, new(12, 56), 2.5f, 1.1f),
        new(ToyType.Ball,        "Toy Ball",     "●", 22, 22, new(11, 11), 4.0f, 1.5f),
    };

    public static ToyDef Get(ToyType t) => All[(int)t];
}
