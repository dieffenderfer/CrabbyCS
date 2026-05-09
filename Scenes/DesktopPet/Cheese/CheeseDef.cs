namespace MouseHouse.Scenes.DesktopPet.Cheese;

public enum CheeseType
{
    Cheddar,
    Swiss,
    Brie,
    Gouda,
    Parmesan,
    Mozzarella,
    Blue,
    Camembert,
    Feta,
    PepperJack,
    ColbyJack,
    StringCheese,
    Edam,
    Goat,
}

/// <summary>
/// Per-variety configuration: display name, how attractive it is to the
/// pet (faster walk speed for favorites), how long the eating animation
/// lasts, and which reaction overlay to fire when finished.
/// </summary>
public record CheeseDef(
    CheeseType Type,
    string Name,
    float Preference,        // 0.5..2.0 — multiplies walk speed when seeking
    float EatSeconds,        // how long to chew before reaction
    string Reaction,         // tag the reaction system reads
    string Glyph             // single-char emoji-ish glyph used in the menu
);

public static class Cheeses
{
    public static readonly CheeseDef[] All =
    {
        new(CheeseType.Cheddar,      "Cheddar",       1.8f, 1.4f, "chomp",   "▮"),
        new(CheeseType.Swiss,        "Swiss",         1.2f, 2.0f, "nibble",  "◉"),
        new(CheeseType.Brie,         "Brie",          1.0f, 2.4f, "sleepy",  "◗"),
        new(CheeseType.Gouda,        "Gouda",         1.4f, 1.6f, "warm",    "●"),
        new(CheeseType.Parmesan,     "Parmesan",      1.1f, 2.6f, "savor",   "△"),
        new(CheeseType.Mozzarella,   "Mozzarella",    1.2f, 1.6f, "cool",    "○"),
        new(CheeseType.Blue,         "Blue",          0.8f, 1.8f, "stink",   "✷"),
        new(CheeseType.Camembert,    "Camembert",     1.1f, 2.0f, "love",    "◍"),
        new(CheeseType.Feta,         "Feta",          0.7f, 1.8f, "salty",   "▪"),
        new(CheeseType.PepperJack,   "Pepper Jack",   1.3f, 1.6f, "spicy",   "✺"),
        new(CheeseType.ColbyJack,    "Colby-Jack",    1.3f, 1.6f, "swirl",   "◐"),
        new(CheeseType.StringCheese, "String Cheese", 1.4f, 2.4f, "peel",    "│"),
        new(CheeseType.Edam,         "Edam",          1.3f, 1.6f, "warm",    "●"),
        new(CheeseType.Goat,         "Goat Cheese",   1.0f, 1.8f, "tangy",   "◊"),
    };

    public static CheeseDef Get(CheeseType t) => All[(int)t];
}
