using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Castle of the Winds — a substantial C# port of Rick Saada's 1989
/// tile-based RPG (Part 1: A Question of Vengeance), taking structure and
/// naming from the Elm reimplementation at github.com/mordrax/cotwelm.
///
/// What's here, in cotwelm/CoTW vocabulary:
///
///   • Title + intro screens.
///   • Character creation (cotwelm CharCreation.elm): name, gender, age,
///     point-buy across Strength / Intellect / Dexterity / Constitution
///     (cotwelm Attributes.elm uses the same four).
///   • Twelve equipment slots from cotwelm Equipment.elm:
///         Weapon · Armour · Shield · Helmet · Bracers · Gauntlets ·
///         Neckwear · Overgarment · Left Ring · Right Ring · Boots · Belt
///   • Item catalog drawing from Item/Data.elm — weapons (Dagger →
///     Two-Handed Sword), armour (Leather → Meteoric Steel Plate /
///     Elven Chain Mail), shields (wooden / iron / steel sizes), helmets
///     (Leather → Enchanted Helm of Storms), bracers / gauntlets of
///     defense / slaying / dexterity / strength, rings of resistance /
///     regeneration / stealth, amulets, cloaks, boots, belts (with
///     potion slots), plus food, potions, and scrolls.
///   • A small overworld (Sosaria Hold + wilderness) and three named
///     dungeons reachable from the wilderness: the Tomb of Hastur (3
///     small floors), the Sewer (3 floors), and the Halls of Mischief
///     (the canonical 8-floor main dungeon, Surtur on the final altar).
///   • Town (cotwelm Building.elm style): Player's House, General Store,
///     Armory, Magic Shop, Junk Shop, Healer, Sage (identify), Bank,
///     Trainer, plus the Inn for a free heal.
///   • Bestiary of 40+ creatures from Monsters.Types.elm with attack
///     flags (Poison, Acid, Fire, Ice, Lightning, Drain, Steal, Ranged,
///     Spell). Special attacks actually fire — vipers poison, vampires
///     drain levels, wraiths drain XP, dragons breathe.
///   • 24 spells across the six classic CoTW schools — Attack, Defense,
///     Healing, Movement, Divination, Transmute — learned from
///     spellbooks bought / found, cast from a paged menu.
///   • Hunger clock (food: rations, lembas, mushrooms). Stress levels:
///     Normal / Burdened / Stressed / Strained / Overloaded mapped to
///     move-cost penalties and regen rate (cotwelm Hero/Stats tick).
///   • Status effects: Poisoned, Diseased, Confused, Blind, Slowed,
///     Hasted, Blessed, Stunned. Status decays per turn.
///   • Identification: potions and scrolls roll random appearances per
///     game (purple potion / fizzy potion / "Klaatu" scroll …) and
///     remember themselves once identified.
///   • Per-floor seen-memory + radius-of-light field of view.
///   • Save / load via SaveManager (whole game state as JSON).
///
/// The activity is one self-contained file in the project's house style.
/// It's the largest activity in the codebase by intent: Castle of the
/// Winds is a large game and a partial port wouldn't capture the feel.
/// </summary>
public partial class CastleOfTheWindsActivity : IActivity
{
    public Vector2 PanelSize => new(960, 660);
    public bool IsFinished { get; private set; }

    private const int FrameInset = 3;

    private const int MapCols = 44;
    private const int MapRows = 22;
    // Sprite atlases are native 32×32; we render at that size 1:1.
    private const int Tile = 32;
    // Viewport that fits inside the panel — the map scrolls under the hero.
    private const int ViewCols = 20;
    private const int ViewRows = 14;

    private const int SidebarW = 240;

    // ── Asset textures (cotwelm sprite sheets) ───────────────────────────
    private readonly AssetCache _assets;
    private Texture2D _tilesTex, _monstersTex, _itemsTex, _buildingsTex,
                       _spellsTex, _spellFxTex, _ripTex, _splashTex;
    private bool _texturesLoaded;

    public CastleOfTheWindsActivity(AssetCache assets) { _assets = assets; }

    private void EnsureTextures()
    {
        if (_texturesLoaded) return;
        _tilesTex     = _assets.GetTexture("assets/castleofthewinds/tiles.png");
        _monstersTex  = _assets.GetTexture("assets/castleofthewinds/monsters.png");
        _itemsTex     = _assets.GetTexture("assets/castleofthewinds/items.png");
        _buildingsTex = _assets.GetTexture("assets/castleofthewinds/buildings.png");
        _spellsTex    = _assets.GetTexture("assets/castleofthewinds/spells.png");
        _spellFxTex   = _assets.GetTexture("assets/castleofthewinds/spell_effects.png");
        _ripTex       = _assets.GetTexture("assets/castleofthewinds/RIP_blank.png");
        _splashTex    = _assets.GetTexture("assets/castleofthewinds/landing_cotw1.png");
        _texturesLoaded = true;
    }

    // Draw a sprite from one of the cotwelm sheets. (col, row) index by
    // 32-pixel tiles; (px, py) lets us address sprites at off-grid offsets
    // (the items.png has a few of those).
    private void DrawSprite(Texture2D tex, int srcX, int srcY, int dstX, int dstY, int dstSize = Tile)
    {
        var src = new Rectangle(srcX, srcY, 32, 32);
        var dst = new Rectangle(dstX, dstY, dstSize, dstSize);
        Raylib.DrawTexturePro(tex, src, dst, Vector2.Zero, 0, Color.White);
    }

    // ── Sprite coordinate tables (from cotwelm CSS) ──────────────────────
    // Each entry maps an in-game name to a pixel offset in its atlas.

    private static readonly (int x, int y) TileFloorDark   = (0, 64);   // dark-dgn
    private static readonly (int x, int y) TileFloorLit    = (0, 160);  // lit-dgn
    private static readonly (int x, int y) TileWallDark    = (32, 64);  // wall-dark-dgn
    private static readonly (int x, int y) TileWallLit     = (32, 160); // wall-lit-dgn
    private static readonly (int x, int y) TileGrass       = (0, 32);
    private static readonly (int x, int y) TileVegePatch   = (128, 32); // tree-ish
    private static readonly (int x, int y) TilePath        = (0, 128);
    private static readonly (int x, int y) TileWater       = (0, 96);
    private static readonly (int x, int y) TileMountain    = (64, 64);  // castle-corner-parapet
    private static readonly (int x, int y) TileDoorClosed  = (64, 160);
    private static readonly (int x, int y) TileStairsUp    = (64, 128);
    private static readonly (int x, int y) TileStairsDown  = (96, 128);
    private static readonly (int x, int y) TileAltar       = (128, 96);
    private static readonly (int x, int y) TileFountain    = (96, 96);
    private static readonly (int x, int y) TileWell        = (160, 32);
    private static readonly (int x, int y) TileMineEntrance= (64, 0);
    private static readonly (int x, int y) TilePortcullis  = (96, 0);
    private static readonly (int x, int y) TileSign        = (160, 0);
    private static readonly (int x, int y) TileWagon       = (192, 32);
    private static readonly (int x, int y) TileTownWall    = (192, 96);
    private static readonly (int x, int y) TileAshes       = (192, 64);
    private static readonly (int x, int y) TilePillar      = (192, 160);

    // Monster sheet — 14 cols × 5 rows. Names match my Bestiary strings
    // (lower-cased, hyphenated) so the lookup is just kebab(name).
    private static readonly Dictionary<string, (int x, int y)> MonsterSprite = new()
    {
        ["Giant Rat"]               = (96, 0),
        ["Large Snake"]             = (128, 0),
        ["Giant Red Ant"]           = (160, 0),
        ["Wild Dog"]                = (192, 0),
        ["Skeleton"]                = (224, 0),
        ["Giant Trapdoor Spider"]   = (256, 0),
        ["Giant Bat"]               = (288, 0),
        ["Carrion Creeper"]         = (320, 0),
        ["Giant Scorpion"]          = (352, 0),
        ["Green Slime"]             = (384, 0),
        ["Viper"]                   = (416, 0),
        ["Huge Ogre"]               = (0, 32),
        ["Walking Corpse"]          = (32, 32),
        ["Huge Lizard"]             = (64, 32),
        ["Goblin"]                  = (96, 32),
        ["Hobgoblin"]               = (128, 32),
        ["Shadow"]                  = (160, 32),
        ["Smirking Sneak Thief"]    = (192, 32),
        ["Gray Wolf"]               = (224, 32),
        ["White Wolf"]              = (256, 32),
        ["Brown Bear"]              = (288, 32),
        ["Cave Bear"]               = (320, 32),
        ["Gelatinous Glob"]         = (352, 32),
        ["Gruesome Troll"]          = (384, 32),
        ["Manticore"]               = (416, 32),
        ["Animated Bronze Statue"]  = (0, 64),
        ["Animated Iron Statue"]    = (32, 64),
        ["Animated Marble Statue"]  = (64, 64),
        ["Animated Wooden Statue"]  = (96, 64),
        ["Bandit"]                  = (128, 64),
        ["Evil Warrior"]            = (160, 64),
        ["Wizard"]                  = (192, 64),
        ["Necromancer"]             = (224, 64),
        ["Barrow Wight"]            = (256, 64),
        ["Dark Wraith"]             = (288, 64),
        ["Eerie Ghost"]             = (320, 64),
        ["Spectre"]                 = (352, 64),
        ["Vampire"]                 = (384, 64),
        ["Ice Devil"]               = (416, 64),
        ["Rat-Man"]                 = (0, 96),
        ["Wolf-Man"]                = (32, 96),
        ["Bear-Man"]                = (64, 96),
        ["Bull-Man"]                = (96, 96),
        ["Spiked Devil"]            = (128, 96),
        ["Horned Devil"]            = (160, 96),
        ["Abyss Fiend"]             = (192, 96),
        ["Wind Elemental"]          = (224, 96),
        ["Dust Elemental"]          = (256, 96),
        ["Fire Elemental"]          = (288, 96),
        ["Water Elemental"]         = (320, 96),
        ["Magma Elemental"]         = (352, 96),
        ["Ice Elemental"]           = (384, 96),
        ["Earth Elemental"]         = (416, 96),
        ["Hill Giant"]              = (0, 128),
        ["Two-Headed Giant"]        = (32, 128),
        ["Frost Giant"]             = (64, 128),
        ["Stone Giant"]             = (96, 128),
        ["Fire Giant"]              = (128, 128),
        ["Surtur"]                  = (160, 128),
        ["Fire Giant King"]         = (192, 128),
        ["Frost Giant King"]        = (224, 128),
        ["Hill Giant King"]         = (256, 128),
        ["Stone Giant King"]        = (288, 128),
        ["Red Dragon"]              = (320, 128),
        ["Blue Dragon"]             = (352, 128),
        ["White Dragon"]            = (384, 128),
        ["Green Dragon"]            = (416, 128),
    };
    private static readonly (int x, int y) HeroMaleSprite   = (0, 0);
    private static readonly (int x, int y) HeroFemaleSprite = (32, 0);

    // Item sprite picker — maps a catalog item name to an (atlas, x, y).
    // The original game reuses a handful of generic icons for many items,
    // so this is a category lookup rather than a per-name table.
    private (int x, int y) ItemSpriteFor(string name)
    {
        var def = Def(name);
        // Potions
        if (def.IsPotion)
        {
            if (def.PotionHeal >= 60) return (128, 0);   // potion-major-heal
            if (def.PotionHeal > 0)   return (96, 0);    // potion-medium-heal
            if (def.PotionMana > 0)   return (64, 0);    // potion-minor-heal (re-tinted)
            if (def.PotionCurePoison) return (224, 0);   // potion-divination (purple-ish)
            if (def.PotionStrength)   return (160, 0);   // potion-gain-attribute
            if (def.PotionExtraHp || def.PotionExtraMp) return (192, 0); // potion-lose-attribute graphic re-used
            return (0, 0);                                // potion
        }
        if (def.IsScroll) return (256, 32);              // scroll
        if (def.IsBook)   return (32, 32);               // spell-book
        if (def.IsFood)   return name == "Apple" ? (128, 704) : (0, 704);  // apple / parchment-ish
        if (def.IsLight)  return name.Contains("Lantern") ? (224, 64) : (0, 704);  // staff-light / parchment

        if (def.Slot == null) return (0, 704); // parchment fallback

        switch (def.Slot.Value)
        {
            case Slot.Weapon:
                if (name.Contains("Sword") || name == "Broken Sword") return (0, 192);
                if (name.Contains("Mace")) return (0, 224);
                if (name.Contains("Hammer")) return (0, 768);
                if (name.Contains("Axe")) return (0, 160);
                if (name == "Club") return (0, 896);
                if (name == "Spear") return (0, 864);
                if (name == "Flail") return (0, 736);
                if (name.Contains("Morning Star")) return (0, 832);
                if (name == "Quarterstaff") return (0, 64);   // staff
                return (0, 192);                              // default sword
            case Slot.Armor:
                if (name == "Rusty Armour") return (32, 800);   // broken-armour
                return name.Contains("Leather") ? (0, 256) : (0, 288);
            case Slot.Shield:
                return name.Contains("Wooden") ? (0, 352) : (0, 320);
            case Slot.Helmet:
                if (name.Contains("Detect")) return (98, 384);
                if (name.Contains("Storms")) return (32, 704);
                return name.Contains("Leather") ? (0, 416) : (0, 384);
            case Slot.Bracers:
                return def.Defense >= 2 ? (32, 448) : (0, 448);
            case Slot.Gauntlets:
                if (name.Contains("Slaying")) return (92, 480);
                return def.Defense >= 2 ? (32, 480) : (0, 480);
            case Slot.Neckwear:
                if (name.Contains("Life")) return (32, 128);     // amulet-cursed re-used
                return (0, 128);                                 // amulet
            case Slot.Overgarment:
                return (0, 512);
            case Slot.LeftRing:
            case Slot.RightRing:
                return (0, 576);
            case Slot.Boots:
                if (name.Contains("Speed")) return (64, 544);
                if (name.Contains("Levitation")) return (96, 544);
                return (0, 544);
            case Slot.Belt:
                if (def.ContainerSlots >= 4) return (64, 672);   // utility-belt
                if (def.ContainerSlots == 3) return (32, 672);   // wand-quiver-belt
                return (0, 672);                                 // slot-belt
        }
        return (0, 704);
    }

    // ── Viewport ─────────────────────────────────────────────────────────
    // Returns the top-left (in map cells) of the visible viewport, centred
    // on the hero and clamped to map bounds.
    private (int x, int y) ViewportOrigin()
    {
        int vx = _state.Player.X - ViewCols / 2;
        int vy = _state.Player.Y - ViewRows / 2;
        vx = Math.Clamp(vx, 0, Math.Max(0, MapCols - ViewCols));
        vy = Math.Clamp(vy, 0, Math.Max(0, MapRows - ViewRows));
        return (vx, vy);
    }

    // ── Map glyphs ────────────────────────────────────────────────────────
    private const char TWall   = '#';
    private const char TFloor  = '.';
    private const char TGrass  = '"';
    private const char TTree   = 'T';
    private const char TWater  = '~';
    private const char TMountain = '^';
    private const char TDoor   = '+';
    private const char TStairUp   = '<';
    private const char TStairDown = '>';
    // Town building tiles — each is a doormat that opens a service.
    private const char TGenStore  = 'G';
    private const char TArmory    = 'A';
    private const char TMagicShop = 'M';
    private const char TJunkShop  = 'J';
    private const char THealer    = 'H';
    private const char TSage      = 'S';
    private const char TBank      = 'B';
    private const char TTrainer   = 'R';
    private const char TInn       = 'N';
    private const char THouse     = 'P';   // Player's house
    private const char TAltar  = '_';      // Surtur lair marker
    // Wilderness extras — overworld-only dungeon entrances.
    private const char TTombEnt = 't';     // Tomb of Hastur entrance
    private const char TSewerEnt = 'r';    // Sewer entrance
    private const char THallsEnt = 'h';    // Halls of Mischief entrance
    private const char TTownEnt = 'p';     // back to town (Sosaria gate)

    // ── Floor index conventions ──────────────────────────────────────────
    // Negative = town/world; positive = inside a dungeon. Dungeons live in
    // contiguous ranges so depth math stays simple.
    private const int FloorTitle      = -3;   // not stored; presentation only
    private const int FloorTown       = -1;
    private const int FloorWilderness = 0;
    private const int HallsBase   = 1;        // Halls of Mischief 1..8
    private const int HallsTop    = 8;
    private const int TombBase    = 11;       // Tomb of Hastur 11..13
    private const int TombTop     = 13;
    private const int SewerBase   = 21;       // Sewer 21..23
    private const int SewerTop    = 23;

    private static string DungeonName(int d)
    {
        if (d == FloorTown) return "Sosaria Hold";
        if (d == FloorWilderness) return "Wilderness";
        if (d >= HallsBase && d <= HallsTop) return $"Halls of Mischief {d}";
        if (d >= TombBase  && d <= TombTop)  return $"Tomb of Hastur {d - TombBase + 1}";
        if (d >= SewerBase && d <= SewerTop) return $"Sewer {d - SewerBase + 1}";
        return $"Level {d}";
    }
    private static int RelativeDepth(int d)
    {
        if (d >= HallsBase && d <= HallsTop) return d - HallsBase + 1;
        if (d >= TombBase  && d <= TombTop)  return d - TombBase  + 2; // tomb is tougher
        if (d >= SewerBase && d <= SewerTop) return d - SewerBase + 3;
        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Game data (items, spells, monsters, buildings)
    // ─────────────────────────────────────────────────────────────────────

    private enum Slot { Weapon, Armor, Shield, Helmet, Bracers, Gauntlets,
                        Neckwear, Overgarment, LeftRing, RightRing, Boots, Belt }

    private static readonly string[] SlotNames =
    {
        "Weapon", "Armour", "Shield", "Helmet", "Bracers", "Gauntlets",
        "Neckwear", "Cloak", "Left Ring", "Right Ring", "Boots", "Belt",
    };

    [Flags]
    private enum ItemFlag
    {
        None = 0,
        Cursed = 1,
        ProtectFire = 2,
        ProtectCold = 4,
        ProtectPoison = 8,
        ProtectLightning = 16,
        ProtectAcid = 32,
        Regen = 64,
        Stealth = 128,
        Speed = 256,
        DetectMonsters = 512,
        AddStr = 1024,
        AddDex = 2048,
        AddInt = 4096,
        AddCon = 8192,
        Telepathy = 16384,
        LifeSaving = 32768,
        Levitate = 65536,
        Light = 131072,
    }

    private record ItemDef(string Name, Slot? Slot, int Damage, int Defense,
                           int Weight, int Price, char Glyph,
                           ItemFlag Flags = ItemFlag.None,
                           bool TwoHand = false,
                           bool IsPotion = false, bool IsScroll = false,
                           bool IsFood = false, bool IsBook = false,
                           bool IsLight = false,
                           int PotionHeal = 0, int PotionMana = 0,
                           bool PotionCurePoison = false, bool PotionExtraHp = false,
                           bool PotionExtraMp = false, bool PotionStrength = false,
                           bool ScrollIdentify = false, bool ScrollTeleport = false,
                           bool ScrollMagicMap = false, bool ScrollEnchant = false,
                           bool ScrollHolyWord = false,
                           int FoodNutrition = 0,
                           int BookSpellIdx = -1,
                           bool IsContainer = false,
                           int ContainerSlots = 0);

    // Catalog is the single source of truth. Names line up with cotwelm
    // Item/Data.elm and CoTW canon.
    private static readonly ItemDef[] Catalog =
    {
        // ── Weapons ──
        new("Broken Sword",      Slot.Weapon,  3, 0, 2,    10, ')'),
        new("Club",              Slot.Weapon,  4, 0, 2,    15, ')'),
        new("Dagger",            Slot.Weapon,  4, 0, 1,    30, ')'),
        new("Hammer",            Slot.Weapon,  5, 0, 3,    50, ')'),
        new("Hand Axe",          Slot.Weapon,  6, 0, 3,    60, ')'),
        new("Quarterstaff",      Slot.Weapon,  6, 0, 4,    50, ')', TwoHand: true),
        new("Spear",             Slot.Weapon,  7, 0, 3,    80, ')'),
        new("Short Sword",       Slot.Weapon,  7, 0, 2,   120, ')'),
        new("Mace",              Slot.Weapon,  8, 0, 4,   140, ')'),
        new("Flail",             Slot.Weapon,  9, 0, 5,   180, ')'),
        new("Axe",               Slot.Weapon,  9, 0, 4,   200, ')'),
        new("War Hammer",        Slot.Weapon, 10, 0, 6,   260, ')'),
        new("Long Sword",        Slot.Weapon, 10, 0, 3,   240, ')'),
        new("Battle Axe",        Slot.Weapon, 12, 0, 5,   340, ')'),
        new("Broad Sword",       Slot.Weapon, 12, 0, 4,   360, ')'),
        new("Morning Star",      Slot.Weapon, 13, 0, 6,   420, ')'),
        new("Bastard Sword",     Slot.Weapon, 14, 0, 5,   500, ')'),
        new("Two-Handed Sword",  Slot.Weapon, 16, 0, 7,   650, ')', TwoHand: true),

        // ── Armour ──
        new("Rusty Armour",      Slot.Armor,   0,  2, 8,    20, '['),
        new("Leather Armour",    Slot.Armor,   0,  3, 4,    60, '['),
        new("Studded Leather",   Slot.Armor,   0,  4, 6,   140, '['),
        new("Ring Mail",         Slot.Armor,   0,  5, 9,   260, '['),
        new("Scale Mail",        Slot.Armor,   0,  6, 11,  340, '['),
        new("Chain Mail",        Slot.Armor,   0,  7, 12,  440, '['),
        new("Splint Mail",       Slot.Armor,   0,  8, 14,  560, '['),
        new("Plate Mail",        Slot.Armor,   0,  9, 16,  720, '['),
        new("Plate Armour",      Slot.Armor,   0, 10, 18,  900, '['),
        new("Elven Chain Mail",  Slot.Armor,   0, 11, 8,  1400, '['),
        new("Meteoric Steel Plate", Slot.Armor, 0, 13, 18, 2200, '['),

        // ── Shields ──
        new("Small Wooden Shield",  Slot.Shield, 0, 1, 2,   40, '('),
        new("Medium Wooden Shield", Slot.Shield, 0, 2, 4,   90, '('),
        new("Large Wooden Shield",  Slot.Shield, 0, 3, 6,  150, '('),
        new("Small Iron Shield",    Slot.Shield, 0, 2, 4,  130, '('),
        new("Medium Iron Shield",   Slot.Shield, 0, 3, 6,  220, '('),
        new("Large Iron Shield",    Slot.Shield, 0, 4, 9,  340, '('),
        new("Small Steel Shield",   Slot.Shield, 0, 4, 4,  340, '('),
        new("Large Steel Shield",   Slot.Shield, 0, 6, 9,  720, '('),

        // ── Helmets ──
        new("Leather Helm",        Slot.Helmet, 0, 1, 1,    40, '^'),
        new("Iron Helm",           Slot.Helmet, 0, 2, 2,   120, '^'),
        new("Steel Helm",          Slot.Helmet, 0, 3, 3,   240, '^'),
        new("Meteoric Steel Helm", Slot.Helmet, 0, 4, 3,   620, '^'),
        new("Helm of Detect Monsters", Slot.Helmet, 0, 2, 2, 1100, '^', Flags: ItemFlag.DetectMonsters),
        new("Enchanted Helm of Storms", Slot.Helmet, 0, 5, 3, 2500, '^', Flags: ItemFlag.ProtectLightning),

        // ── Bracers / Gauntlets ──
        new("Bracers",                  Slot.Bracers, 0, 1, 2,   80, ']'),
        new("Bracers of Defense",       Slot.Bracers, 0, 3, 2,  420, ']'),
        new("Gauntlets",                Slot.Gauntlets, 0, 1, 2, 80, ']'),
        new("Gauntlets of Protection",  Slot.Gauntlets, 0, 3, 2, 380, ']'),
        new("Gauntlets of Slaying",     Slot.Gauntlets, 2, 0, 2, 520, ']'),
        new("Gauntlets of Strength",    Slot.Gauntlets, 0, 1, 2, 600, ']', Flags: ItemFlag.AddStr),
        new("Gauntlets of Dexterity",   Slot.Gauntlets, 0, 1, 2, 600, ']', Flags: ItemFlag.AddDex),

        // ── Neckwear (amulets) ──
        new("Amulet of Life Saving",    Slot.Neckwear,  0, 0, 1, 2000, '"', Flags: ItemFlag.LifeSaving),
        new("Amulet of Regeneration",   Slot.Neckwear,  0, 0, 1, 1200, '"', Flags: ItemFlag.Regen),
        new("Amulet of Telepathy",      Slot.Neckwear,  0, 0, 1, 1100, '"', Flags: ItemFlag.Telepathy | ItemFlag.DetectMonsters),
        new("Amulet of Speed",          Slot.Neckwear,  0, 0, 1, 1500, '"', Flags: ItemFlag.Speed),

        // ── Overgarment (cloaks) ──
        new("Cloak",                    Slot.Overgarment, 0, 1, 2,  120, '%'),
        new("Cloak of Protection",      Slot.Overgarment, 0, 3, 2,  600, '%', Flags: ItemFlag.Stealth),
        new("Elven Cloak",              Slot.Overgarment, 0, 2, 1,  900, '%', Flags: ItemFlag.Stealth),

        // ── Rings ──
        new("Ring of Protection",   Slot.LeftRing,  0, 2, 0,  600, '=', Flags: ItemFlag.None),
        new("Ring of Fire Resist",  Slot.LeftRing,  0, 0, 0,  500, '=', Flags: ItemFlag.ProtectFire),
        new("Ring of Cold Resist",  Slot.LeftRing,  0, 0, 0,  500, '=', Flags: ItemFlag.ProtectCold),
        new("Ring of Lightning Resist", Slot.LeftRing, 0, 0, 0, 500, '=', Flags: ItemFlag.ProtectLightning),
        new("Ring of Regeneration", Slot.LeftRing,  0, 0, 0,  900, '=', Flags: ItemFlag.Regen),
        new("Ring of Stealth",      Slot.LeftRing,  0, 0, 0,  700, '=', Flags: ItemFlag.Stealth),
        new("Ring of Strength",     Slot.LeftRing,  0, 0, 0,  700, '=', Flags: ItemFlag.AddStr),
        new("Ring of Levitation",   Slot.LeftRing,  0, 0, 0,  600, '=', Flags: ItemFlag.Levitate),

        // ── Boots ──
        new("Leather Boots",   Slot.Boots, 0, 1, 2,   60, ']'),
        new("Iron Boots",      Slot.Boots, 0, 2, 4,  180, ']'),
        new("Elven Boots",     Slot.Boots, 0, 1, 1,  500, ']', Flags: ItemFlag.Stealth),
        new("Boots of Speed",  Slot.Boots, 0, 1, 2, 1100, ']', Flags: ItemFlag.Speed),
        new("Boots of Levitation", Slot.Boots, 0, 1, 2, 700, ']', Flags: ItemFlag.Levitate),

        // ── Belt ──
        new("Two-Slot Belt",   Slot.Belt, 0, 0, 1,   40, ']', IsContainer: true, ContainerSlots: 2),
        new("Three-Slot Belt", Slot.Belt, 0, 0, 1,  100, ']', IsContainer: true, ContainerSlots: 3),
        new("Four-Slot Belt",  Slot.Belt, 0, 0, 1,  220, ']', IsContainer: true, ContainerSlots: 4),

        // ── Potions ──
        new("Potion of Healing",        null, 0, 0, 1,   40, '!', IsPotion: true, PotionHeal: 25),
        new("Potion of Mana",           null, 0, 0, 1,   40, '!', IsPotion: true, PotionMana: 25),
        new("Potion of Greater Healing",null, 0, 0, 1,   90, '!', IsPotion: true, PotionHeal: 60),
        new("Potion of Greater Mana",   null, 0, 0, 1,   90, '!', IsPotion: true, PotionMana: 60),
        new("Potion of Cure Poison",    null, 0, 0, 1,   60, '!', IsPotion: true, PotionCurePoison: true),
        new("Potion of Extra HP",       null, 0, 0, 1,  300, '!', IsPotion: true, PotionExtraHp: true),
        new("Potion of Extra Mana",     null, 0, 0, 1,  300, '!', IsPotion: true, PotionExtraMp: true),
        new("Potion of Strength",       null, 0, 0, 1,  400, '!', IsPotion: true, PotionStrength: true),

        // ── Scrolls ──
        new("Scroll of Identify",   null, 0, 0, 0,  40, '?', IsScroll: true, ScrollIdentify: true),
        new("Scroll of Teleport",   null, 0, 0, 0, 100, '?', IsScroll: true, ScrollTeleport: true),
        new("Scroll of Magic Map",  null, 0, 0, 0, 120, '?', IsScroll: true, ScrollMagicMap: true),
        new("Scroll of Enchantment",null, 0, 0, 0, 400, '?', IsScroll: true, ScrollEnchant: true),
        new("Scroll of Holy Word",  null, 0, 0, 0, 250, '?', IsScroll: true, ScrollHolyWord: true),

        // ── Food ──
        new("Rations",      null, 0, 0, 1,   8, '%', IsFood: true, FoodNutrition: 400),
        new("Lembas",       null, 0, 0, 1,  40, '%', IsFood: true, FoodNutrition: 1200),
        new("Mushroom",     null, 0, 0, 0,   4, '%', IsFood: true, FoodNutrition: 150),
        new("Apple",        null, 0, 0, 0,   3, '%', IsFood: true, FoodNutrition: 100),

        // ── Light sources ──
        new("Torch",        null, 0, 0, 1,  10, ',', IsLight: true, Flags: ItemFlag.Light),
        new("Brass Lantern",null, 0, 0, 2,  80, ',', IsLight: true, Flags: ItemFlag.Light),

        // ── Spellbooks ── (BookSpellIdx points into Spells[])
        new("Book of Magic Arrow",   null, 0, 0, 1,  120, '+', IsBook: true, BookSpellIdx: 0),
        new("Book of Fireball",      null, 0, 0, 1,  220, '+', IsBook: true, BookSpellIdx: 1),
        new("Book of Lightning Bolt",null, 0, 0, 1,  220, '+', IsBook: true, BookSpellIdx: 2),
        new("Book of Cone of Cold",  null, 0, 0, 1,  320, '+', IsBook: true, BookSpellIdx: 3),
        new("Book of Acid Bolt",     null, 0, 0, 1,  220, '+', IsBook: true, BookSpellIdx: 4),
        new("Book of Force Bolt",    null, 0, 0, 1,  180, '+', IsBook: true, BookSpellIdx: 5),
        new("Book of Lesser Heal",   null, 0, 0, 1,  100, '+', IsBook: true, BookSpellIdx: 6),
        new("Book of Greater Heal",  null, 0, 0, 1,  280, '+', IsBook: true, BookSpellIdx: 7),
        new("Book of Cure Poison",   null, 0, 0, 1,  120, '+', IsBook: true, BookSpellIdx: 8),
        new("Book of Restoration",   null, 0, 0, 1,  360, '+', IsBook: true, BookSpellIdx: 9),
        new("Book of Resist Fire",   null, 0, 0, 1,  140, '+', IsBook: true, BookSpellIdx: 10),
        new("Book of Resist Cold",   null, 0, 0, 1,  140, '+', IsBook: true, BookSpellIdx: 11),
        new("Book of Reflect",       null, 0, 0, 1,  220, '+', IsBook: true, BookSpellIdx: 12),
        new("Book of Protection",    null, 0, 0, 1,  260, '+', IsBook: true, BookSpellIdx: 13),
        new("Book of Bless",         null, 0, 0, 1,  220, '+', IsBook: true, BookSpellIdx: 14),
        new("Book of Phase Door",    null, 0, 0, 1,  140, '+', IsBook: true, BookSpellIdx: 15),
        new("Book of Teleport",      null, 0, 0, 1,  240, '+', IsBook: true, BookSpellIdx: 16),
        new("Book of Haste",         null, 0, 0, 1,  300, '+', IsBook: true, BookSpellIdx: 17),
        new("Book of Levitate",      null, 0, 0, 1,  220, '+', IsBook: true, BookSpellIdx: 18),
        new("Book of Detect Items",  null, 0, 0, 1,  120, '+', IsBook: true, BookSpellIdx: 19),
        new("Book of Detect Monsters",null,0, 0, 1,  120, '+', IsBook: true, BookSpellIdx: 20),
        new("Book of Magic Map",     null, 0, 0, 1,  240, '+', IsBook: true, BookSpellIdx: 21),
        new("Book of Identify",      null, 0, 0, 1,  220, '+', IsBook: true, BookSpellIdx: 22),
        new("Book of Light",         null, 0, 0, 1,   80, '+', IsBook: true, BookSpellIdx: 23),
    };

    private static readonly Dictionary<string, int> _catIdx = BuildCatIndex();
    private static Dictionary<string, int> BuildCatIndex()
    {
        var d = new Dictionary<string, int>(Catalog.Length);
        for (int i = 0; i < Catalog.Length; i++) d[Catalog[i].Name] = i;
        return d;
    }
    private static int CatIdx(string name) => _catIdx.TryGetValue(name, out var i) ? i : 0;
    private static ItemDef Def(string name) => Catalog[CatIdx(name)];

    // ── Spell definitions ────────────────────────────────────────────────
    private enum School { Attack, Defense, Healing, Movement, Divination, Transmute }

    private record SpellDef(string Name, School School, int Cost, int MinIntellect, string Description);

    private static readonly SpellDef[] Spells =
    {
        // 0-5  Attack
        new("Magic Arrow",      School.Attack,    5, 10, "Bolt of force at nearest visible foe"),
        new("Fireball",         School.Attack,   14, 14, "Splash damage in a 4-tile radius"),
        new("Lightning Bolt",   School.Attack,   12, 14, "Pierces along a line through foes"),
        new("Cone of Cold",     School.Attack,   18, 16, "Wide arc of freezing damage"),
        new("Acid Bolt",        School.Attack,   10, 12, "Etching acid bolt — corrodes armour"),
        new("Force Bolt",       School.Attack,    8, 11, "Pure kinetic bolt at nearest foe"),
        // 6-9  Healing
        new("Lesser Heal",      School.Healing,   6, 10, "Restore 15-25 HP"),
        new("Greater Heal",     School.Healing,  14, 14, "Restore 40-70 HP"),
        new("Cure Poison",      School.Healing,   6, 11, "End the Poisoned status"),
        new("Restoration",      School.Healing,  20, 16, "Full HP + clear status"),
        // 10-14 Defense
        new("Resist Fire",      School.Defense,  10, 12, "Protect from fire (30 turns)"),
        new("Resist Cold",      School.Defense,  10, 12, "Protect from cold (30 turns)"),
        new("Reflect",          School.Defense,  14, 13, "Reflect 1 incoming attack"),
        new("Protection",       School.Defense,  10, 12, "+2 AC for 30 turns"),
        new("Bless",            School.Defense,   8, 11, "+2 to-hit, +2 damage (30 turns)"),
        // 15-18 Movement
        new("Phase Door",       School.Movement,  6, 11, "Random short hop (≤ 6 tiles)"),
        new("Teleport",         School.Movement, 14, 13, "Random hop anywhere on floor"),
        new("Haste",            School.Movement, 16, 14, "Two moves per turn (15 turns)"),
        new("Levitate",         School.Movement,  8, 12, "Pass over water (30 turns)"),
        // 19-23 Divination
        new("Detect Items",     School.Divination, 6, 11, "Reveal all item drops on floor"),
        new("Detect Monsters",  School.Divination, 6, 11, "Reveal monster positions on floor"),
        new("Magic Map",        School.Divination,12, 12, "Reveal the dungeon's geometry"),
        new("Identify",         School.Divination, 8, 12, "Identify one unknown item"),
        new("Light",            School.Divination, 4, 10, "Fully light current floor"),
    };

    // ── Monster definitions ──────────────────────────────────────────────
    [Flags]
    private enum MonFlag
    {
        None      = 0,
        Ranged    = 1 << 0,
        Poison    = 1 << 1,
        Acid      = 1 << 2,
        Fire      = 1 << 3,
        Ice       = 1 << 4,
        Lightning = 1 << 5,
        Drain     = 1 << 6,
        Steal     = 1 << 7,
        Spell     = 1 << 8,
        Undead    = 1 << 9,
        Dragon    = 1 << 10,
        Boss      = 1 << 11,
    }

    private record MonDef(string Name, int MinFloor, int Hp, int Atk, int Def,
                          int Xp, int Gold, char Glyph, MonFlag Flags = MonFlag.None);

    // Names match cotwelm Monsters.Types.elm (a meaningful subset).
    private static readonly MonDef[] Bestiary =
    {
        new("Giant Rat",            1,  6,  3, 0,  10,   2, 'r'),
        new("Large Snake",          1,  8,  4, 1,  14,   3, 's'),
        new("Giant Red Ant",        1,  9,  4, 2,  16,   0, 'a'),
        new("Wild Dog",             1, 10,  4, 0,  18,   4, 'd'),
        new("Giant Bat",            1,  8,  3, 1,  16,   0, 'B', MonFlag.Ranged),
        new("Goblin",               2, 12,  5, 1,  24,   8, 'g'),
        new("Skeleton",             2, 14,  5, 2,  28,   6, 'z', MonFlag.Undead),
        new("Giant Trapdoor Spider",2, 18,  6, 2,  32,   0, 'S', MonFlag.Poison),
        new("Viper",                2, 14,  5, 2,  36,   0, 'v', MonFlag.Poison),
        new("Hobgoblin",            3, 20,  7, 2,  44,  16, 'h'),
        new("Gray Wolf",            3, 22,  8, 1,  52,   0, 'w'),
        new("Carrion Creeper",      3, 24,  6, 3,  60,   0, 'c'),
        new("Walking Corpse",       3, 28,  8, 4, 100,  20, 'Z', MonFlag.Undead),
        new("Giant Scorpion",       3, 26,  9, 4,  84,   0, 'C', MonFlag.Poison),
        new("Green Slime",          3, 30,  6, 6,  80,   0, 'j', MonFlag.Acid),
        new("Smirking Sneak Thief", 4, 24, 10, 3,  90,  40, 't', MonFlag.Steal),
        new("White Wolf",           4, 30,  9, 2,  90,   0, 'w', MonFlag.Ice),
        new("Brown Bear",           4, 36, 11, 3, 110,   0, 'b'),
        new("Huge Lizard",          4, 40, 11, 4, 120,   0, 'L'),
        new("Shadow",               4, 26,  8, 5, 130,   0, ',', MonFlag.Drain),
        new("Gruesome Troll",       5, 56, 14, 4, 200,  60, 'T'),
        new("Manticore",            5, 48, 14, 3, 220,  40, 'M', MonFlag.Ranged),
        new("Wizard",               5, 38, 10, 3, 240, 120, 'W', MonFlag.Spell | MonFlag.Ranged),
        new("Bandit",               5, 36, 12, 4, 180,  90, 'B', MonFlag.Steal),
        new("Evil Warrior",         5, 50, 14, 5, 240, 100, 'E'),
        new("Necromancer",          6, 46, 12, 4, 320, 200, 'N', MonFlag.Spell | MonFlag.Drain),
        new("Dark Wraith",          6, 42, 13, 5, 280,   0, 'W', MonFlag.Undead | MonFlag.Drain),
        new("Eerie Ghost",          6, 38, 11, 6, 280,   0, 'G', MonFlag.Undead),
        new("Vampire",              7, 70, 18, 5, 460, 160, 'V', MonFlag.Undead | MonFlag.Drain),
        new("Spectre",              7, 60, 16, 6, 420,   0, 'p', MonFlag.Undead | MonFlag.Drain),
        new("Spiked Devil",         7, 70, 20, 6, 520, 100, 'D', MonFlag.Poison),
        new("Horned Devil",         7, 80, 22, 7, 620, 140, 'D', MonFlag.Fire),
        new("Wind Elemental",       6, 50, 14, 5, 320,   0, 'e', MonFlag.Lightning),
        new("Fire Elemental",       6, 60, 16, 5, 400,   0, 'e', MonFlag.Fire),
        new("Ice Elemental",        6, 60, 16, 5, 400,   0, 'e', MonFlag.Ice),
        new("Magma Elemental",      7, 70, 18, 6, 500,   0, 'e', MonFlag.Fire),
        new("Hill Giant",           7, 90, 20, 5, 520, 200, 'H'),
        new("Frost Giant",          7, 100, 22, 6, 620, 240, 'F', MonFlag.Ice),
        new("Stone Giant",          7, 110, 24, 7, 720, 260, 'O'),
        new("Two-Headed Giant",     7, 120, 26, 6, 780, 300, 'O'),
        new("Red Dragon",           8, 180, 26, 7, 1400, 600, 'd', MonFlag.Dragon | MonFlag.Fire | MonFlag.Ranged),
        new("White Dragon",         8, 160, 24, 6, 1200, 500, 'd', MonFlag.Dragon | MonFlag.Ice | MonFlag.Ranged),
        new("Blue Dragon",          8, 170, 25, 7, 1300, 550, 'd', MonFlag.Dragon | MonFlag.Lightning | MonFlag.Ranged),
        new("Green Dragon",         8, 150, 22, 6, 1100, 450, 'd', MonFlag.Dragon | MonFlag.Acid | MonFlag.Ranged),
        new("Hill Giant King",      8, 200, 28, 7, 1800, 800, 'K'),
        new("Frost Giant King",     8, 220, 30, 7, 2000, 900, 'K', MonFlag.Ice),
        new("Surtur",               8, 320, 34, 9, 4000, 1500,'S', MonFlag.Boss | MonFlag.Fire),
    };
    private static readonly Dictionary<string, int> _monIdx = BuildMonIndex();
    private static Dictionary<string, int> BuildMonIndex()
    {
        var d = new Dictionary<string, int>();
        for (int i = 0; i < Bestiary.Length; i++) d[Bestiary[i].Name] = i;
        return d;
    }
    private static MonDef MDef(string name) => Bestiary[_monIdx[name]];

    // ── Building defs ────────────────────────────────────────────────────
    private record BuildingDef(string Name, Mode Mode, char Tile, string Description);

    private static readonly BuildingDef[] Buildings =
    {
        new("General Store", Mode.Shop, TGenStore, "Food, torches, scrolls, basic gear"),
        new("Armory",        Mode.Shop, TArmory,   "Weapons, armour, shields, helmets"),
        new("Magic Shop",    Mode.Shop, TMagicShop,"Spellbooks, wands, magical curiosities"),
        new("Junk Shop",     Mode.Shop, TJunkShop, "Cursed and broken oddities — cheap"),
        new("Healer",        Mode.Healer, THealer, "Cure status / restore HP for gold"),
        new("Sage",          Mode.Sage,   TSage,   "Identify unknown items for gold"),
        new("Bank",          Mode.Bank,   TBank,   "Deposit and withdraw gold"),
        new("Trainer",       Mode.Trainer,TTrainer,"Spend XP for permanent stat boosts"),
        new("Inn",           Mode.Playing,TInn,    "Free heal and meal"),
        new("Player's House",Mode.House,  THouse,  "Personal stash"),
    };
    private static BuildingDef? BuildingForTile(char c)
    {
        foreach (var b in Buildings) if (b.Tile == c) return b;
        return null;
    }

    // ── Mode ─────────────────────────────────────────────────────────────
    private enum Mode
    {
        Title, Intro, CharCreation,
        Playing,
        Inventory, SpellMenu, ItemDetail,
        Shop, Healer, Sage, Bank, Trainer, House,
        Dead, Won
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Save state POCOs
    // ─────────────────────────────────────────────────────────────────────

    public class StatusEffect
    {
        public string Name { get; set; } = "";   // matches enum names below
        public int TurnsLeft { get; set; }
        public int Magnitude { get; set; }       // for poison damage, etc
    }

    public class Hero
    {
        public string Name { get; set; } = "Hero";
        public bool Male { get; set; } = true;
        public int Age { get; set; } = 20;
        public int Str { get; set; } = 12;
        public int Int { get; set; } = 12;
        public int Dex { get; set; } = 12;
        public int Con { get; set; } = 12;
        public int MaxHpBonus { get; set; }       // permanent boosts (potion of extra HP / trainer)
        public int MaxMpBonus { get; set; }
        public int Level { get; set; } = 1;
        public int Xp { get; set; }
        public int Hp { get; set; } = 30;
        public int Mp { get; set; } = 20;
        public int Gold { get; set; } = 50;
        public int Bank { get; set; }
        public int Hunger { get; set; } = 2000;   // 0 starves
        public int X { get; set; }
        public int Y { get; set; }
        public Dictionary<string, string?> Equip { get; set; } = new();   // slot enum name → item name
        public List<string> Pack { get; set; } = new();
        public List<string> Stash { get; set; } = new();   // player's house storage
        public List<StatusEffect> Effects { get; set; } = new();
        public HashSet<int> KnownSpells { get; set; } = new();    // index into Spells[]
        public HashSet<string> IdentifiedAppearances { get; set; } = new();   // appearance keys
        public bool LifeSavingTriggered { get; set; }
    }

    public class MonInst
    {
        public string TypeName { get; set; } = "Giant Rat";
        public int X { get; set; }
        public int Y { get; set; }
        public int Hp { get; set; }
        public bool Asleep { get; set; } = true;
    }

    public class ItemDrop
    {
        public string Name { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
    }

    public class Floor
    {
        public int Depth { get; set; }
        public string Rows { get; set; } = "";
        public List<MonInst> Monsters { get; set; } = new();
        public List<ItemDrop> Items { get; set; } = new();
        public bool[] Seen { get; set; } = Array.Empty<bool>();
        public bool Mapped { get; set; }   // Magic Map cast on this floor
    }

    public class SaveState
    {
        public Hero Player { get; set; } = new();
        public int CurrentFloor { get; set; } = FloorTown;
        public List<Floor> Floors { get; set; } = new();
        public int Seed { get; set; }
        public List<string> Log { get; set; } = new();
        public bool Won { get; set; }
        public int Turn { get; set; }
        // Per-game randomisation of potion / scroll appearances.
        public Dictionary<string, string> PotionAppearance { get; set; } = new();
        public Dictionary<string, string> ScrollAppearance { get; set; } = new();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Runtime state
    // ─────────────────────────────────────────────────────────────────────
    private Mode _mode = Mode.Title;
    private SaveState _state = new();
    private Random _rng = new();
    private readonly List<string> _log = new();
    private int _scrollLog;                       // log scrollback offset

    // Char creation scratch
    private string _ccName = "Wayfarer";
    private bool _ccMale = true;
    private int _ccAge = 22;
    private int _ccStr = 12, _ccInt = 12, _ccDex = 12, _ccCon = 12;
    private int _ccPoints = 16;
    private int _ccCursor;
    private bool _ccNameEdit;
    private bool _hoverNameField;

    // Generic submenu cursors
    private int _invCursor;
    private int _spellCursor;
    private int _shopCursor;
    private int _stashCursor;
    private bool _shopBuying = true;
    private bool _stashing = true;             // House: deposit vs withdraw
    private List<string> _shopStock = new();    // re-populated when entering a shop
    private string _activeShop = "";
    private string _selectedItemName = "";      // For ItemDetail mode
    private bool _hasteFreeMove;                // tracks the bonus move

    // Pending targeted action — "cast then click target", "scroll of teleport", etc.
    // Held in user-facing prompt language.
    private string _prompt = "";

    private const string SaveFile = "castleofthewinds.json";

    // ─────────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────────────────
    public void Load()
    {
        var loaded = SaveManager.Load<SaveState>(SaveFile);
        if (loaded != null && loaded.Floors.Count > 0 && loaded.Player.Hp > 0 && !loaded.Won)
        {
            _state = loaded;
            _rng = new Random(_state.Seed ^ unchecked((int)0x9E3779B9));
            _log.Clear();
            _log.AddRange(_state.Log);
            _mode = Mode.Playing;
        }
        else
        {
            _mode = Mode.Title;
        }
    }

    public void Close()
    {
        if (_mode != Mode.Title && _mode != Mode.Intro &&
            _mode != Mode.CharCreation && _mode != Mode.Dead && _mode != Mode.Won)
        {
            _state.Log = _log.TakeLast(80).ToList();
            SaveManager.Save(SaveFile, _state);
        }
        else if (_mode == Mode.Dead || _mode == Mode.Won)
        {
            try { File.Delete(Path.Combine(SaveManager.SaveDirectory, SaveFile)); } catch { }
        }
        IsFinished = true;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Update — top dispatch
    // ─────────────────────────────────────────────────────────────────────
    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;
        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        { Close(); return; }

        var menuBar = new Rectangle(FrameInset, FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);

        string[] items = MenuItemsForMode();
        int mb = RetroWidgets.MenuBarHitTest(menuBar, items, local, leftPressed);
        HandleMenuClick(mb);

        switch (_mode)
        {
            case Mode.Title: UpdateTitle(); break;
            case Mode.Intro: UpdateIntro(); break;
            case Mode.CharCreation: UpdateCharCreation(local, leftPressed); break;
            case Mode.Playing: UpdatePlaying(); break;
            case Mode.Inventory: UpdateInventory(); break;
            case Mode.SpellMenu: UpdateSpellMenu(); break;
            case Mode.ItemDetail: UpdateItemDetail(); break;
            case Mode.Shop: UpdateShop(); break;
            case Mode.Healer: UpdateHealer(); break;
            case Mode.Sage: UpdateSage(); break;
            case Mode.Bank: UpdateBank(); break;
            case Mode.Trainer: UpdateTrainer(); break;
            case Mode.House: UpdateHouse(); break;
            case Mode.Dead:
            case Mode.Won:
                if (Raylib.IsKeyPressed(KeyboardKey.Space) ||
                    Raylib.IsKeyPressed(KeyboardKey.Enter))
                    Close();
                break;
        }
    }

    private string[] MenuItemsForMode() => _mode switch
    {
        Mode.Title => new[] { "New Game", "Continue", "About" },
        Mode.Intro => new[] { "Continue", "Skip" },
        Mode.CharCreation => new[] { "Begin", "Roll", "Help" },
        _ when _mode == Mode.Dead || _mode == Mode.Won => new[] { "Close" },
        _ => new[] { "Menu", "Inventory", "Spells", "Save", "Quit" },
    };

    private void HandleMenuClick(int mb)
    {
        if (mb < 0) return;
        switch (_mode)
        {
            case Mode.Title:
                if (mb == 0) { _mode = Mode.Intro; }
                else if (mb == 1)
                {
                    var loaded = SaveManager.Load<SaveState>(SaveFile);
                    if (loaded != null && loaded.Floors.Count > 0)
                    { _state = loaded; _rng = new Random(_state.Seed ^ 0x12345); _log.Clear(); _log.AddRange(_state.Log); _mode = Mode.Playing; }
                    else AddLog("No saved game.");
                }
                else if (mb == 2) AddLog("Castle of the Winds (1989) by Rick Saada. Port-in-spirit from cotwelm.");
                break;
            case Mode.Intro:
                if (mb == 0 || mb == 1) _mode = Mode.CharCreation;
                break;
            case Mode.CharCreation:
                if (mb == 0) BeginGame();
                else if (mb == 1) RollAttributes();
                else if (mb == 2) AddLog("Distribute attribute points, name yourself, then Begin.");
                break;
            case Mode.Dead:
            case Mode.Won:
                if (mb == 0) Close();
                break;
            default:
                switch (mb)
                {
                    case 0:
                        // "Menu" → bounce to title (with confirmation via save)
                        _state.Log = _log.TakeLast(80).ToList();
                        SaveManager.Save(SaveFile, _state);
                        _mode = Mode.Title;
                        break;
                    case 1: _mode = _mode == Mode.Inventory ? Mode.Playing : Mode.Inventory; break;
                    case 2: _mode = _mode == Mode.SpellMenu ? Mode.Playing : Mode.SpellMenu; break;
                    case 3:
                        _state.Log = _log.TakeLast(80).ToList();
                        SaveManager.Save(SaveFile, _state);
                        AddLog("Game saved.");
                        break;
                    case 4: Close(); break;
                }
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Title / Intro / Char creation
    // ─────────────────────────────────────────────────────────────────────
    private void UpdateTitle()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.Space))
            _mode = Mode.Intro;
    }

    private void UpdateIntro()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.Space)
            || Raylib.IsKeyPressed(KeyboardKey.Escape))
            _mode = Mode.CharCreation;
    }

    private void UpdateCharCreation(Vector2 local, bool leftPressed)
    {
        var nameRect = NameFieldRect();
        _hoverNameField = RetroSkin.PointInRect(local, nameRect);
        if (leftPressed) _ccNameEdit = _hoverNameField;

        if (_ccNameEdit)
        {
            int ch;
            while ((ch = Raylib.GetCharPressed()) > 0)
            {
                if (_ccName.Length < 16 && ch >= 32 && ch < 127)
                    _ccName += (char)ch;
            }
            if (Raylib.IsKeyPressed(KeyboardKey.Backspace) && _ccName.Length > 0)
                _ccName = _ccName[..^1];
            if (Raylib.IsKeyPressed(KeyboardKey.Enter)) _ccNameEdit = false;
            return;
        }

        if (Raylib.IsKeyPressed(KeyboardKey.G)) _ccMale = !_ccMale;
        if (Raylib.IsKeyPressed(KeyboardKey.R)) RollAttributes();
        if (Raylib.IsKeyPressed(KeyboardKey.Up))   _ccCursor = (_ccCursor + 3) % 4;
        if (Raylib.IsKeyPressed(KeyboardKey.Down)) _ccCursor = (_ccCursor + 1) % 4;
        if (Raylib.IsKeyPressed(KeyboardKey.Right) || Raylib.IsKeyPressed(KeyboardKey.Equal))
            BumpCcAttr(+1);
        if (Raylib.IsKeyPressed(KeyboardKey.Left) || Raylib.IsKeyPressed(KeyboardKey.Minus))
            BumpCcAttr(-1);
        if (Raylib.IsKeyPressed(KeyboardKey.Enter) && _ccPoints == 0) BeginGame();
    }

    private void RollAttributes()
    {
        // CoTW classic — roll 3d6 per attribute, with one free re-roll the
        // user can take as many times as they like. Points pool reflects
        // anything left after the roll if it came out under 4×12 = 48.
        _ccStr = 6 + _rng.Next(13);
        _ccInt = 6 + _rng.Next(13);
        _ccDex = 6 + _rng.Next(13);
        _ccCon = 6 + _rng.Next(13);
        int sum = _ccStr + _ccInt + _ccDex + _ccCon;
        _ccPoints = Math.Max(0, 64 - sum);
        _ccAge = 18 + _rng.Next(10);
        AddLog($"Rolled: Str {_ccStr}  Int {_ccInt}  Dex {_ccDex}  Con {_ccCon}  ({_ccPoints} bonus points)");
    }

    private void BumpCcAttr(int delta)
    {
        ref int target = ref _ccStr;
        switch (_ccCursor)
        {
            case 0: target = ref _ccStr; break;
            case 1: target = ref _ccInt; break;
            case 2: target = ref _ccDex; break;
            case 3: target = ref _ccCon; break;
        }
        if (delta > 0 && _ccPoints > 0 && target < 25) { target++; _ccPoints--; }
        else if (delta < 0 && target > 6) { target--; _ccPoints++; }
    }

    private void BeginGame()
    {
        int seed = Environment.TickCount;
        _state = new SaveState
        {
            Seed = seed,
            CurrentFloor = FloorTown,
            Player = new Hero
            {
                Name = string.IsNullOrWhiteSpace(_ccName) ? "Wayfarer" : _ccName.Trim(),
                Male = _ccMale,
                Age = _ccAge,
                Str = _ccStr, Int = _ccInt, Dex = _ccDex, Con = _ccCon,
                Level = 1, Xp = 0, Gold = 50, Hunger = 2000,
                Pack = new List<string> {
                    "Potion of Healing", "Potion of Mana", "Rations", "Torch",
                },
                Equip = new Dictionary<string, string?>(),
            },
        };
        // Start equip — short sword + leather + leather boots.
        _state.Player.Equip[nameof(Slot.Weapon)] = "Dagger";
        _state.Player.Equip[nameof(Slot.Armor)]  = "Leather Armour";
        _state.Player.Equip[nameof(Slot.Boots)]  = "Leather Boots";
        _state.Player.Hp = MaxHp(_state.Player);
        _state.Player.Mp = MaxMp(_state.Player);
        // Starter spells per attribute (cotwelm doesn't have a list but
        // CoTW's "spells you start with" is shaped by intellect).
        _state.Player.KnownSpells.Add(0);   // Magic Arrow
        _state.Player.KnownSpells.Add(6);   // Lesser Heal
        if (_state.Player.Int >= 11) _state.Player.KnownSpells.Add(23); // Light
        if (_state.Player.Int >= 12) _state.Player.KnownSpells.Add(15); // Phase Door
        _rng = new Random(seed);
        RandomizeAppearances();
        BuildAllFloors();
        var (sx, sy) = FindAny(GetFloor(FloorTown), TStairUp);
        if (sx == 0 && sy == 0) (sx, sy) = (MapCols / 2, MapRows / 2);
        _state.Player.X = sx; _state.Player.Y = sy;
        _log.Clear();
        AddLog($"Welcome to Sosaria Hold, {_state.Player.Name}.");
        AddLog("Step onto a building's doorway to enter it. Wander out through the gates to the wilderness.");
        _mode = Mode.Playing;
        ReveralFov();
    }

    private void RandomizeAppearances()
    {
        // Each potion / scroll gets one of these scrambled appearance names.
        // Same instance throughout the run — saved with the state.
        var potionAdj = new[]
        {
            "purple", "fizzy", "cloudy", "milky", "viscous", "smoky",
            "swirling", "amber", "crimson", "pale green", "iridescent",
            "black", "azure",
        };
        var scrollWords = new[]
        {
            "KLAATU", "BERATA", "NIKTO", "ASTUM", "PERDOH", "VEX VRENN",
            "HOTHRIK", "SHEM", "MIRTH", "QUVAR", "OOLM", "ZAR FENN",
            "GALEM", "RUKHEN",
        };
        var rng = new Random(_state.Seed ^ unchecked((int)0xCAFEBABE));
        var potions = Catalog.Where(c => c.IsPotion).ToList();
        Shuffle(potions, rng);
        var scrolls = Catalog.Where(c => c.IsScroll).ToList();
        Shuffle(scrolls, rng);
        _state.PotionAppearance.Clear();
        _state.ScrollAppearance.Clear();
        for (int i = 0; i < potions.Count; i++)
            _state.PotionAppearance[potions[i].Name] = potionAdj[i % potionAdj.Length] + " potion";
        for (int i = 0; i < scrolls.Count; i++)
            _state.ScrollAppearance[scrolls[i].Name] = "scroll titled \"" + scrollWords[i % scrollWords.Length] + "\"";
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Derived stats
    // ─────────────────────────────────────────────────────────────────────
    private int MaxHp(Hero h)
    {
        int v = 20 + h.Con * 2 + h.Str + h.Level * 6 + h.MaxHpBonus;
        if (HasFlag(h, ItemFlag.AddCon)) v += 8;
        return v;
    }
    private int MaxMp(Hero h)
    {
        int v = 8 + h.Int * 2 + h.Level * 3 + h.MaxMpBonus;
        if (HasFlag(h, ItemFlag.AddInt)) v += 6;
        return v;
    }
    private int EffStr(Hero h) => h.Str + (HasFlag(h, ItemFlag.AddStr) ? 3 : 0);
    private int EffDex(Hero h) => h.Dex + (HasFlag(h, ItemFlag.AddDex) ? 3 : 0);
    private int ToHit(Hero h)
    {
        int v = 10 + EffDex(h) / 2 + h.Level;
        if (HasStatus(h, "Blessed")) v += 2;
        if (HasStatus(h, "Confused")) v -= 4;
        return v;
    }
    private int DamageRoll(Hero h)
    {
        var w = EqDef(h, Slot.Weapon);
        int wd = w?.Damage ?? 2;
        var g = EqDef(h, Slot.Gauntlets);
        int gd = g?.Damage ?? 0;
        int bonus = (EffStr(h) - 10) / 3;
        int roll = _rng.Next(1, wd + 1) + gd + Math.Max(0, bonus);
        if (HasStatus(h, "Blessed")) roll += 2;
        return roll;
    }
    private int ArmorClass(Hero h)
    {
        int ac = EffDex(h) / 4;
        foreach (Slot s in Enum.GetValues<Slot>())
            ac += EqDef(h, s)?.Defense ?? 0;
        if (HasStatus(h, "Protected")) ac += 2;
        return ac;
    }
    private int Weight(Hero h)
    {
        int w = 0;
        foreach (Slot s in Enum.GetValues<Slot>())
            w += EqDef(h, s)?.Weight ?? 0;
        foreach (var nm in h.Pack) w += Def(nm).Weight;
        return w;
    }
    private int Encumbrance(Hero h)
    {
        // Five-band stress level matching CoTW vocabulary.
        int cap = 15 + EffStr(h) * 2;
        int w = Weight(h);
        if (w <= cap) return 0;          // Normal
        if (w <= cap + 8) return 1;      // Burdened
        if (w <= cap + 16) return 2;     // Stressed
        if (w <= cap + 24) return 3;     // Strained
        return 4;                         // Overloaded
    }
    private static readonly string[] EncNames = { "Normal", "Burdened", "Stressed", "Strained", "Overloaded" };

    private ItemDef? EqDef(Hero h, Slot s)
    {
        if (!h.Equip.TryGetValue(s.ToString(), out var nm) || nm == null) return null;
        return Def(nm);
    }
    private bool HasFlag(Hero h, ItemFlag f)
    {
        foreach (Slot s in Enum.GetValues<Slot>())
            if ((EqDef(h, s)?.Flags ?? 0) is var fl && (fl & f) != 0) return true;
        return false;
    }
    private bool HasStatus(Hero h, string name)
    {
        foreach (var e in h.Effects) if (e.Name == name && e.TurnsLeft > 0) return true;
        return false;
    }
    private void AddStatus(Hero h, string name, int turns, int mag = 0)
    {
        var e = h.Effects.FirstOrDefault(x => x.Name == name);
        if (e != null) { e.TurnsLeft = Math.Max(e.TurnsLeft, turns); e.Magnitude = Math.Max(e.Magnitude, mag); }
        else h.Effects.Add(new StatusEffect { Name = name, TurnsLeft = turns, Magnitude = mag });
    }
    private void RemoveStatus(Hero h, string name)
    {
        h.Effects.RemoveAll(e => e.Name == name);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Floor generation
    // ─────────────────────────────────────────────────────────────────────
    private void BuildAllFloors()
    {
        _state.Floors.Clear();
        _state.Floors.Add(BuildTown());
        _state.Floors.Add(BuildWilderness());
        for (int d = HallsBase; d <= HallsTop; d++) _state.Floors.Add(BuildDungeon(d));
        for (int d = TombBase;  d <= TombTop;  d++) _state.Floors.Add(BuildDungeon(d));
        for (int d = SewerBase; d <= SewerTop; d++) _state.Floors.Add(BuildDungeon(d));
    }

    private Floor BuildTown()
    {
        var f = new Floor { Depth = FloorTown };
        char[,] m = new char[MapCols, MapRows];
        for (int x = 0; x < MapCols; x++)
            for (int y = 0; y < MapRows; y++)
                m[x, y] = TGrass;
        for (int x = 0; x < MapCols; x++) { m[x, 0] = TWall; m[x, MapRows - 1] = TWall; }
        for (int y = 0; y < MapRows; y++) { m[0, y] = TWall; m[MapCols - 1, y] = TWall; }
        // A cobblestone plaza.
        for (int x = 4; x < MapCols - 4; x++)
            for (int y = 4; y < MapRows - 4; y++)
                m[x, y] = TFloor;
        // Trees outside the plaza for atmosphere.
        for (int i = 0; i < 26; i++)
        {
            int x = _rng.Next(2, MapCols - 2), y = _rng.Next(2, MapRows - 2);
            if (m[x, y] == TGrass) m[x, y] = TTree;
        }
        // Buildings along three rows of the plaza.
        // Each is a 4×3 building with a glyph-doormat at the bottom centre.
        DrawBuilding(m, 6,  5, 4, 3, TGenStore);
        DrawBuilding(m, 12, 5, 4, 3, TArmory);
        DrawBuilding(m, 18, 5, 4, 3, TMagicShop);
        DrawBuilding(m, 24, 5, 4, 3, TJunkShop);
        DrawBuilding(m, 30, 5, 4, 3, THealer);
        DrawBuilding(m, 36, 5, 4, 3, TSage);
        DrawBuilding(m, 8,  14, 4, 3, TBank);
        DrawBuilding(m, 14, 14, 4, 3, TTrainer);
        DrawBuilding(m, 22, 14, 4, 3, TInn);
        DrawBuilding(m, 30, 14, 4, 3, THouse);
        // Town gate to the wilderness — stairs-up tile (we use < ambiguously
        // here: it's the "exit upward to the overworld" tile).
        m[MapCols / 2, MapRows - 2] = TStairUp;
        // A signpost dropped as a torch on the ground so the player notices.
        f.Items.Add(new ItemDrop { Name = "Torch", X = MapCols / 2, Y = MapRows - 3 });
        f.Rows = FlattenMap(m);
        f.Seen = new bool[MapCols * MapRows];
        for (int i = 0; i < f.Seen.Length; i++) f.Seen[i] = true;
        return f;
    }

    private static void DrawBuilding(char[,] m, int x0, int y0, int w, int h, char glyph)
    {
        for (int x = x0; x < x0 + w; x++)
            for (int y = y0; y < y0 + h; y++)
            {
                if (x == x0 + w / 2 && y == y0 + h - 1) m[x, y] = glyph;  // doormat
                else if (x == x0 || y == y0 || x == x0 + w - 1 || y == y0 + h - 1) m[x, y] = TWall;
                else m[x, y] = TFloor;
            }
    }

    private Floor BuildWilderness()
    {
        var f = new Floor { Depth = FloorWilderness };
        char[,] m = new char[MapCols, MapRows];
        // Cellular-automata land/water.
        bool[,] land = new bool[MapCols, MapRows];
        for (int x = 0; x < MapCols; x++)
            for (int y = 0; y < MapRows; y++)
                land[x, y] = _rng.NextDouble() < 0.62;
        for (int iter = 0; iter < 4; iter++)
        {
            var next = new bool[MapCols, MapRows];
            for (int x = 0; x < MapCols; x++)
                for (int y = 0; y < MapRows; y++)
                {
                    int n = 0;
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= MapCols || ny >= MapRows) { n++; continue; }
                            if (dx == 0 && dy == 0) continue;
                            if (land[nx, ny]) n++;
                        }
                    next[x, y] = n >= 4 || (land[x, y] && n >= 3);
                }
            land = next;
        }
        for (int x = 0; x < MapCols; x++)
            for (int y = 0; y < MapRows; y++)
            {
                if (!land[x, y]) m[x, y] = TWater;
                else
                {
                    double r = _rng.NextDouble();
                    m[x, y] = r < 0.08 ? TMountain
                             : r < 0.25 ? TTree
                             : TGrass;
                }
            }
        // Town gate back home — left edge.
        int gx = 2, gy = MapRows / 2;
        while (m[gx, gy] != TGrass && gy < MapRows - 2) gy++;
        m[gx, gy] = TTownEnt;
        // Dungeon entrances scattered on land.
        PlaceOn(m, TGrass, THallsEnt, "Halls", 3);
        PlaceOn(m, TGrass, TTombEnt,  "Tomb",  1);
        PlaceOn(m, TGrass, TSewerEnt, "Sewer", 1);

        f.Rows = FlattenMap(m);
        f.Seen = new bool[MapCols * MapRows];
        // Wilderness reveals as you walk (FoV applies).
        return f;
    }

    private void PlaceOn(char[,] m, char target, char glyph, string label, int attempts)
    {
        for (int i = 0; i < attempts * 80; i++)
        {
            int x = _rng.Next(4, MapCols - 4);
            int y = _rng.Next(2, MapRows - 2);
            if (m[x, y] == target) { m[x, y] = glyph; return; }
        }
    }

    private Floor BuildDungeon(int depth)
    {
        var f = new Floor { Depth = depth };
        char[,] m = new char[MapCols, MapRows];
        for (int x = 0; x < MapCols; x++)
            for (int y = 0; y < MapRows; y++)
                m[x, y] = TWall;

        int targetRooms = 6 + _rng.Next(5);
        var centers = new List<(int x, int y)>();
        for (int r = 0; r < targetRooms; r++)
        {
            int rw = 4 + _rng.Next(7);
            int rh = 3 + _rng.Next(5);
            int rx = 1 + _rng.Next(MapCols - rw - 2);
            int ry = 1 + _rng.Next(MapRows - rh - 2);
            for (int x = rx; x < rx + rw; x++)
                for (int y = ry; y < ry + rh; y++)
                    m[x, y] = TFloor;
            centers.Add((rx + rw / 2, ry + rh / 2));
        }
        for (int i = 1; i < centers.Count; i++)
            CarveCorridor(m, centers[i - 1].x, centers[i - 1].y, centers[i].x, centers[i].y);

        // Doors at corridor-room joins.
        for (int x = 1; x < MapCols - 1; x++)
            for (int y = 1; y < MapRows - 1; y++)
                if (m[x, y] == TFloor && _rng.NextDouble() < 0.18)
                {
                    int hWalls = (m[x - 1, y] == TWall ? 1 : 0) + (m[x + 1, y] == TWall ? 1 : 0);
                    int vWalls = (m[x, y - 1] == TWall ? 1 : 0) + (m[x, y + 1] == TWall ? 1 : 0);
                    if ((hWalls == 2 && vWalls == 0) || (vWalls == 2 && hWalls == 0))
                        m[x, y] = TDoor;
                }

        // Stairs (or surface entrance) — first room is up, last is down (or altar).
        var (uX, uY) = centers[0];
        m[uX, uY] = TStairUp;
        bool topOfDungeon = depth == HallsTop || depth == TombTop || depth == SewerTop;
        if (!topOfDungeon)
        {
            var (dX, dY) = centers[^1];
            m[dX, dY] = TStairDown;
        }
        else if (depth == HallsTop)
        {
            // Surtur's lair.
            var (aX, aY) = centers[^1];
            m[aX, aY] = TAltar;
        }

        // Monsters.
        var floorCells = new List<(int x, int y)>();
        for (int x = 1; x < MapCols - 1; x++)
            for (int y = 1; y < MapRows - 1; y++)
                if (m[x, y] == TFloor) floorCells.Add((x, y));
        Shuffle(floorCells, _rng);

        int rel = RelativeDepth(depth);
        int monsterCount = 5 + rel + _rng.Next(4);
        int idx = 0;
        for (int n = 0; n < monsterCount && idx < floorCells.Count; n++)
        {
            var (mx, my) = floorCells[idx++];
            var def = PickMonsterForDepth(rel);
            f.Monsters.Add(new MonInst { TypeName = def.Name, X = mx, Y = my, Hp = def.Hp });
        }
        if (depth == HallsTop)
        {
            var altar = FindAnyIn(m, TAltar);
            f.Monsters.Add(new MonInst { TypeName = "Surtur", X = altar.x, Y = altar.y, Hp = MDef("Surtur").Hp, Asleep = false });
        }

        int itemCount = 2 + _rng.Next(4) + rel / 3;
        for (int n = 0; n < itemCount && idx < floorCells.Count; n++)
        {
            var (ix, iy) = floorCells[idx++];
            var def = PickItemForDepth(rel);
            f.Items.Add(new ItemDrop { Name = def.Name, X = ix, Y = iy });
        }

        f.Rows = FlattenMap(m);
        f.Seen = new bool[MapCols * MapRows];
        return f;
    }

    private void CarveCorridor(char[,] m, int x1, int y1, int x2, int y2)
    {
        int x = x1, y = y1;
        bool horizFirst = _rng.NextDouble() < 0.5;
        if (horizFirst)
        {
            while (x != x2) { if (m[x, y] == TWall) m[x, y] = TFloor; x += Math.Sign(x2 - x); }
            while (y != y2) { if (m[x, y] == TWall) m[x, y] = TFloor; y += Math.Sign(y2 - y); }
        }
        else
        {
            while (y != y2) { if (m[x, y] == TWall) m[x, y] = TFloor; y += Math.Sign(y2 - y); }
            while (x != x2) { if (m[x, y] == TWall) m[x, y] = TFloor; x += Math.Sign(x2 - x); }
        }
    }

    private MonDef PickMonsterForDepth(int depth)
    {
        var pool = Bestiary.Where(b => b.MinFloor <= depth && (b.Flags & MonFlag.Boss) == 0).ToList();
        // Bias toward stuff near current depth.
        var weighted = pool.SelectMany(b =>
            Enumerable.Repeat(b, Math.Max(1, 6 - Math.Abs(depth - b.MinFloor)))).ToList();
        return weighted[_rng.Next(weighted.Count)];
    }

    private ItemDef PickItemForDepth(int depth)
    {
        double r = _rng.NextDouble();
        if (r < 0.30) return Catalog.Where(c => c.IsPotion || c.IsScroll || c.IsFood).ElementAt(_rng.Next(13));
        if (r < 0.45) return Catalog.Where(c => c.IsBook).ElementAt(_rng.Next(24));
        // Gear of an appropriate tier.
        var tier = Catalog.Where(c => c.Slot != null && c.Price <= 120 + depth * 140).ToList();
        if (tier.Count == 0) return Def("Dagger");
        return tier[_rng.Next(tier.Count)];
    }

    // ── Map helpers ──────────────────────────────────────────────────────
    private static string FlattenMap(char[,] m)
    {
        var sb = new System.Text.StringBuilder(MapCols * MapRows);
        for (int y = 0; y < MapRows; y++)
            for (int x = 0; x < MapCols; x++)
                sb.Append(m[x, y]);
        return sb.ToString();
    }
    private static char[,] UnflattenMap(string rows)
    {
        var m = new char[MapCols, MapRows];
        for (int y = 0; y < MapRows; y++)
            for (int x = 0; x < MapCols; x++)
                m[x, y] = rows[y * MapCols + x];
        return m;
    }
    private static void WriteBack(Floor f, char[,] m) => f.Rows = FlattenMap(m);
    private static (int x, int y) FindAnyIn(char[,] m, char target)
    {
        for (int y = 0; y < MapRows; y++)
            for (int x = 0; x < MapCols; x++)
                if (m[x, y] == target) return (x, y);
        return (0, 0);
    }
    private static (int x, int y) FindAny(Floor f, char target)
        => FindAnyIn(UnflattenMap(f.Rows), target);

    private void Shuffle<T>(IList<T> list, Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private Floor GetFloor(int depth)
    {
        foreach (var f in _state.Floors)
            if (f.Depth == depth) return f;
        return _state.Floors[0];
    }
    private Floor CurFloor => GetFloor(_state.CurrentFloor);

    // ─────────────────────────────────────────────────────────────────────
    //  Playing loop
    // ─────────────────────────────────────────────────────────────────────
    private void UpdatePlaying()
    {
        if (HasStatus(_state.Player, "Confused") && _rng.Next(3) == 0)
        {
            // Random direction this turn.
            int rd = _rng.Next(8);
            int[] dxs = { 0, 0, -1, 1, -1, 1, -1, 1 };
            int[] dys = {-1, 1,  0, 0, -1,-1,  1, 1 };
            HandleStep(dxs[rd], dys[rd]);
            return;
        }

        int dx = 0, dy = 0;
        bool wait = false, picked = false, downStairs = false, upStairs = false;
        bool invMenu = false, spellMenu = false;
        int castIdx = -1;
        bool useItem = false;

        if (Raylib.IsKeyPressed(KeyboardKey.Up) || Raylib.IsKeyPressed(KeyboardKey.K))         dy = -1;
        else if (Raylib.IsKeyPressed(KeyboardKey.Down) || Raylib.IsKeyPressed(KeyboardKey.J))  dy = 1;
        else if (Raylib.IsKeyPressed(KeyboardKey.Left) || Raylib.IsKeyPressed(KeyboardKey.H))  dx = -1;
        else if (Raylib.IsKeyPressed(KeyboardKey.Right) || Raylib.IsKeyPressed(KeyboardKey.L)) dx = 1;
        else if (Raylib.IsKeyPressed(KeyboardKey.Y)) { dx = -1; dy = -1; }
        else if (Raylib.IsKeyPressed(KeyboardKey.U)) { dx =  1; dy = -1; }
        else if (Raylib.IsKeyPressed(KeyboardKey.B)) { dx = -1; dy =  1; }
        else if (Raylib.IsKeyPressed(KeyboardKey.N)) { dx =  1; dy =  1; }
        else if (Raylib.IsKeyPressed(KeyboardKey.Period)) wait = true;
        else if (Raylib.IsKeyPressed(KeyboardKey.Comma)) picked = true;
        else if (Raylib.IsKeyPressed(KeyboardKey.I)) invMenu = true;
        else if (Raylib.IsKeyPressed(KeyboardKey.M)) spellMenu = true;
        else if (Raylib.IsKeyPressed(KeyboardKey.Q)) useItem = true;
        for (int i = 0; i < 9; i++)
            if (Raylib.IsKeyPressed(KeyboardKey.One + i)) { castIdx = i; break; }
        if (Raylib.IsKeyPressed(KeyboardKey.D)) downStairs = true;
        if (Raylib.IsKeyPressed(KeyboardKey.A)) upStairs = true;
        if (Raylib.IsKeyPressed(KeyboardKey.PageDown)) downStairs = true;
        if (Raylib.IsKeyPressed(KeyboardKey.PageUp)) upStairs = true;
        // Log scroll
        if (Raylib.IsKeyPressed(KeyboardKey.LeftBracket)) _scrollLog = Math.Min(_scrollLog + 5, Math.Max(0, _log.Count - 1));
        if (Raylib.IsKeyPressed(KeyboardKey.RightBracket)) _scrollLog = Math.Max(0, _scrollLog - 5);

        if (invMenu) { _mode = Mode.Inventory; _invCursor = 0; return; }
        if (spellMenu) { _mode = Mode.SpellMenu; _spellCursor = 0; return; }
        if (useItem) { _mode = Mode.Inventory; _invCursor = 0; _prompt = "Choose an item to use."; return; }

        bool acted = false;
        if (castIdx >= 0) acted = TryCastByKnownIndex(castIdx);
        else if (picked) acted = TryPickup();
        else if (downStairs) acted = TryStairs(TStairDown);
        else if (upStairs) acted = TryStairs(TStairUp);
        else if (dx != 0 || dy != 0) acted = HandleStep(dx, dy);
        else if (wait) acted = true;

        if (acted) ResolveEndOfTurn();
    }

    private bool HandleStep(int dx, int dy)
    {
        int nx = _state.Player.X + dx;
        int ny = _state.Player.Y + dy;
        if (nx < 0 || ny < 0 || nx >= MapCols || ny >= MapRows) return false;
        var map = UnflattenMap(CurFloor.Rows);
        char t = map[nx, ny];

        // Building doormats: stepping onto them opens the service.
        var building = BuildingForTile(t);
        if (building != null)
        {
            if (building.Tile == TInn)
            {
                _state.Player.Hp = MaxHp(_state.Player);
                _state.Player.Mp = MaxMp(_state.Player);
                AddLog("The innkeeper offers a warm meal and a bed. You are restored.");
                return true;
            }
            _activeShop = building.Name;
            switch (building.Mode)
            {
                case Mode.Shop: OpenShopFor(building.Name); break;
                case Mode.Healer: _mode = Mode.Healer; break;
                case Mode.Sage: _mode = Mode.Sage; _shopCursor = 0; break;
                case Mode.Bank: _mode = Mode.Bank; break;
                case Mode.Trainer: _mode = Mode.Trainer; break;
                case Mode.House: _mode = Mode.House; _stashing = true; _stashCursor = 0; break;
            }
            return false;
        }

        // Dungeon entrances (on the wilderness floor).
        if (_state.CurrentFloor == FloorWilderness)
        {
            switch (t)
            {
                case THallsEnt:  TravelTo(HallsBase); return false;
                case TTombEnt:   TravelTo(TombBase);  return false;
                case TSewerEnt:  TravelTo(SewerBase); return false;
                case TTownEnt:   TravelTo(FloorTown); return false;
                case TWater:
                    if (!HasFlag(_state.Player, ItemFlag.Levitate)
                        && !HasStatus(_state.Player, "Levitating"))
                    { AddLog("The water is too deep — you'd drown."); return false; }
                    break;
                case TMountain: AddLog("The mountains are impassable here."); return false;
            }
        }

        // Town gate.
        if (_state.CurrentFloor == FloorTown && t == TStairUp)
        { TravelTo(FloorWilderness); return false; }

        // Bump-attack a monster.
        var mon = CurFloor.Monsters.FirstOrDefault(mm => mm.X == nx && mm.Y == ny);
        if (mon != null) { AttackMonster(mon); return true; }

        if (t == TWall || t == TTree || t == TMountain) return false;
        if (t == TWater && !HasFlag(_state.Player, ItemFlag.Levitate)
                       && !HasStatus(_state.Player, "Levitating"))
        { AddLog("You can't cross water."); return false; }
        if (t == TDoor)
        {
            map[nx, ny] = TFloor;
            WriteBack(CurFloor, map);
            return true;
        }

        _state.Player.X = nx;
        _state.Player.Y = ny;
        if (t == TStairDown) AddLog("Stairs descend into darkness. (Press D to go down.)");
        else if (t == TStairUp) AddLog("Stairs rise above. (Press A to ascend.)");
        else if (t == TAltar) AddLog("A blackened altar. Surtur is bound here.");
        return true;
    }

    private void TravelTo(int newDepth)
    {
        char arrivalTile;
        if (newDepth == FloorTown)
        {
            arrivalTile = TStairUp;       // town gate
        }
        else if (newDepth == FloorWilderness)
        {
            // Coming from town → place on the wilderness town-gate.
            arrivalTile = TTownEnt;
        }
        else
        {
            // Entering a dungeon from the wilderness, or moving between dungeon
            // floors → land on the stairs-up.
            arrivalTile = TStairUp;
        }
        _state.CurrentFloor = newDepth;
        var (ax, ay) = FindAny(CurFloor, arrivalTile);
        if (ax == 0 && ay == 0)
        {
            // Fallback — any walkable tile.
            var m = UnflattenMap(CurFloor.Rows);
            for (int y = 1; y < MapRows - 1 && (ax == 0 && ay == 0); y++)
                for (int x = 1; x < MapCols - 1 && (ax == 0 && ay == 0); x++)
                    if (m[x, y] == TFloor || m[x, y] == TGrass) { ax = x; ay = y; }
        }
        _state.Player.X = ax; _state.Player.Y = ay;
        AddLog(newDepth == FloorTown ? "You return to Sosaria Hold."
              : newDepth == FloorWilderness ? "You step out into the wilderness."
              : $"You enter {DungeonName(newDepth)}.");
        ReveralFov();
    }

    private bool TryPickup()
    {
        var drop = CurFloor.Items.FirstOrDefault(it => it.X == _state.Player.X && it.Y == _state.Player.Y);
        if (drop == null) { AddLog("Nothing to pick up."); return false; }
        if (_state.Player.Pack.Count >= 26) { AddLog("Your pack is full."); return false; }
        _state.Player.Pack.Add(drop.Name);
        CurFloor.Items.Remove(drop);
        AddLog($"You pick up {ArticleFor(drop.Name)} {DisplayName(drop.Name)}.");
        return true;
    }

    private string ArticleFor(string name)
    {
        string disp = DisplayName(name);
        if (disp.Contains("scroll titled")) return "a";
        char c = disp[0];
        return "aeiouAEIOU".IndexOf(c) >= 0 ? "an" : "a";
    }

    private string DisplayName(string realName)
    {
        if (_state.Player.IdentifiedAppearances.Contains(realName)) return realName;
        if (_state.PotionAppearance.TryGetValue(realName, out var a)) return a;
        if (_state.ScrollAppearance.TryGetValue(realName, out var b)) return b;
        return realName;
    }
    private bool IsIdentified(string realName)
    {
        var def = Def(realName);
        if (!def.IsPotion && !def.IsScroll) return true;
        return _state.Player.IdentifiedAppearances.Contains(realName);
    }
    private void Identify(string realName)
    {
        if (_state.Player.IdentifiedAppearances.Add(realName))
            AddLog($"That was {ArticleFor(realName)} {realName}!");
    }

    private bool TryStairs(char which)
    {
        var map = UnflattenMap(CurFloor.Rows);
        if (map[_state.Player.X, _state.Player.Y] != which)
        {
            AddLog(which == TStairDown ? "There are no stairs down here." : "There are no stairs up here.");
            return false;
        }
        int cur = _state.CurrentFloor;
        int next = cur + (which == TStairDown ? 1 : -1);
        // If we're at the top of a dungeon and go up, exit to wilderness.
        bool isDungeon = cur >= HallsBase;
        bool topOfDungeon =
            (cur == HallsBase || cur == TombBase || cur == SewerBase) && which == TStairUp;
        if (topOfDungeon) next = FloorWilderness;
        if (which == TStairDown && (cur == HallsTop || cur == TombTop || cur == SewerTop))
        { AddLog("There is nowhere further down."); return false; }
        if (!isDungeon && which == TStairDown)
        {
            // Town floor: < is the gate to wilderness; no general "go down" tile.
            AddLog("There are no stairs down here.");
            return false;
        }
        TravelTo(next);
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Combat
    // ─────────────────────────────────────────────────────────────────────
    private void AttackMonster(MonInst mon)
    {
        var def = MDef(mon.TypeName);
        int roll = _rng.Next(1, 21) + ToHit(_state.Player);
        int target = 10 + def.Def;
        if (roll < target) { AddLog($"You miss the {mon.TypeName}."); return; }
        int dmg = DamageRoll(_state.Player);
        mon.Hp -= dmg;
        mon.Asleep = false;
        if (mon.Hp <= 0) { KillMonster(mon); return; }
        AddLog($"You hit the {mon.TypeName} for {dmg}.");
    }

    private void KillMonster(MonInst mon)
    {
        var def = MDef(mon.TypeName);
        CurFloor.Monsters.Remove(mon);
        AddLog($"You slay the {mon.TypeName}! +{def.Xp} XP.");
        _state.Player.Gold += def.Gold;
        GainXp(def.Xp);
        if ((def.Flags & MonFlag.Boss) != 0)
        {
            _state.Won = true;
            _mode = Mode.Won;
            AddLog("Surtur falls. The Castle of the Winds is silent at last.");
        }
        // Occasional corpse-drop.
        if (_rng.NextDouble() < 0.20)
        {
            var item = PickItemForDepth(RelativeDepth(_state.CurrentFloor));
            CurFloor.Items.Add(new ItemDrop { Name = item.Name, X = mon.X, Y = mon.Y });
        }
    }

    private void GainXp(int amount)
    {
        _state.Player.Xp += amount;
        while (_state.Player.Xp >= _state.Player.Level * 100)
        {
            _state.Player.Xp -= _state.Player.Level * 100;
            _state.Player.Level++;
            _state.Player.Hp = MaxHp(_state.Player);
            _state.Player.Mp = MaxMp(_state.Player);
            AddLog($"You reach level {_state.Player.Level}!");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Monster AI + special attacks
    // ─────────────────────────────────────────────────────────────────────
    private void ResolveMonsterTurn()
    {
        var snapshot = CurFloor.Monsters.ToList();
        foreach (var mon in snapshot)
        {
            if (!CurFloor.Monsters.Contains(mon)) continue;
            var def = MDef(mon.TypeName);
            int hx = _state.Player.X, hy = _state.Player.Y;
            int dx = hx - mon.X, dy = hy - mon.Y;
            int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));

            // Stealth: monsters notice us less far if we have it.
            int wakeDist = HasFlag(_state.Player, ItemFlag.Stealth) || HasStatus(_state.Player, "Stealthed") ? 5 : 9;
            if (dist > wakeDist && mon.Asleep) continue;
            if (dist <= wakeDist) mon.Asleep = false;
            if (mon.Asleep) continue;

            if (dist == 1) { MonsterMelee(mon, def); continue; }

            // Ranged / spell attacks.
            if ((def.Flags & (MonFlag.Ranged | MonFlag.Spell)) != 0 && dist <= 8 && _rng.NextDouble() < 0.35)
            { MonsterRanged(mon, def); continue; }

            // Step toward hero (or away if slowed/low-hp).
            int sx = Math.Sign(dx), sy = Math.Sign(dy);
            foreach (var (mx, my) in new[] { (sx, sy), (sx, 0), (0, sy) })
            {
                if (mx == 0 && my == 0) continue;
                int tx = mon.X + mx, ty = mon.Y + my;
                if (tx < 0 || ty < 0 || tx >= MapCols || ty >= MapRows) continue;
                if (tx == hx && ty == hy) continue;
                var map = UnflattenMap(CurFloor.Rows);
                char t = map[tx, ty];
                if (t == TWall || t == TTree || t == TMountain || t == TWater) continue;
                if (CurFloor.Monsters.Any(o => o != mon && o.X == tx && o.Y == ty)) continue;
                mon.X = tx; mon.Y = ty;
                break;
            }
        }
    }

    private void MonsterMelee(MonInst mon, MonDef def)
    {
        int roll = _rng.Next(1, 21) + def.Atk / 2;
        int target = 10 + ArmorClass(_state.Player);
        if (roll < target) { AddLog($"The {mon.TypeName} misses you."); return; }
        int dmg = ApplyResist(_rng.Next(1, def.Atk + 1) - ArmorClass(_state.Player) / 3, def.Flags);
        dmg = Math.Max(1, dmg);
        DamageHero(dmg, def.Flags, mon);
        // Status side-effects.
        if ((def.Flags & MonFlag.Poison) != 0 && _rng.NextDouble() < 0.4
            && !HasFlag(_state.Player, ItemFlag.ProtectPoison))
        { AddStatus(_state.Player, "Poisoned", 10 + _rng.Next(8), 1 + RelativeDepth(_state.CurrentFloor) / 3);
          AddLog("You feel poison spreading."); }
        if ((def.Flags & MonFlag.Steal) != 0 && _rng.NextDouble() < 0.3 && _state.Player.Pack.Count > 0)
        {
            int stolenIdx = _rng.Next(_state.Player.Pack.Count);
            string sname = _state.Player.Pack[stolenIdx];
            _state.Player.Pack.RemoveAt(stolenIdx);
            AddLog($"The {mon.TypeName} steals your {DisplayName(sname)}!");
        }
        if ((def.Flags & MonFlag.Drain) != 0 && _rng.NextDouble() < 0.25)
        {
            _state.Player.Xp = Math.Max(0, _state.Player.Xp - 20);
            AddLog($"You feel the {mon.TypeName} drain your life force.");
        }
        AddLog($"The {mon.TypeName} hits you for {dmg}.");
    }

    private void MonsterRanged(MonInst mon, MonDef def)
    {
        string verb = "shoots at you";
        if ((def.Flags & MonFlag.Fire) != 0) verb = "breathes flame";
        else if ((def.Flags & MonFlag.Ice) != 0) verb = "breathes a frost cone";
        else if ((def.Flags & MonFlag.Lightning) != 0) verb = "hurls a thunderbolt";
        else if ((def.Flags & MonFlag.Acid) != 0) verb = "spits acid";
        else if ((def.Flags & MonFlag.Spell) != 0) verb = "casts a spell at you";

        int dmg = ApplyResist(_rng.Next(def.Atk / 2, def.Atk + 1), def.Flags);
        DamageHero(Math.Max(1, dmg), def.Flags, mon);
        AddLog($"The {mon.TypeName} {verb} ({dmg}).");
    }

    private int ApplyResist(int dmg, MonFlag f)
    {
        if ((f & MonFlag.Fire) != 0 && HasFlag(_state.Player, ItemFlag.ProtectFire)) dmg /= 2;
        if ((f & MonFlag.Ice)  != 0 && HasFlag(_state.Player, ItemFlag.ProtectCold)) dmg /= 2;
        if ((f & MonFlag.Lightning) != 0 && HasFlag(_state.Player, ItemFlag.ProtectLightning)) dmg /= 2;
        if ((f & MonFlag.Acid) != 0 && HasFlag(_state.Player, ItemFlag.ProtectAcid)) dmg /= 2;
        if ((f & MonFlag.Fire) != 0 && HasStatus(_state.Player, "Resist Fire")) dmg /= 2;
        if ((f & MonFlag.Ice)  != 0 && HasStatus(_state.Player, "Resist Cold")) dmg /= 2;
        return dmg;
    }

    private void DamageHero(int dmg, MonFlag flags, MonInst? source)
    {
        // Reflect: bounces the attack back at the source, if any.
        if (HasStatus(_state.Player, "Reflect") && source != null)
        {
            source.Hp -= dmg;
            RemoveStatus(_state.Player, "Reflect");
            AddLog("Your shield of force reflects the blow!");
            if (source.Hp <= 0) KillMonster(source);
            return;
        }
        _state.Player.Hp -= dmg;
        if (_state.Player.Hp <= 0)
        {
            if (HasFlag(_state.Player, ItemFlag.LifeSaving) && !_state.Player.LifeSavingTriggered)
            {
                _state.Player.LifeSavingTriggered = true;
                _state.Player.Hp = MaxHp(_state.Player) / 2;
                AddLog("The amulet of life saving shatters — you live!");
                // Burn the amulet.
                _state.Player.Equip[nameof(Slot.Neckwear)] = null;
                return;
            }
            _state.Player.Hp = 0;
            _mode = Mode.Dead;
            AddLog("You have died.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  End-of-turn bookkeeping
    // ─────────────────────────────────────────────────────────────────────
    private void ResolveEndOfTurn()
    {
        _state.Turn++;
        ResolveMonsterTurn();

        // Hasted: free bonus action every other turn until expiry.
        if (HasStatus(_state.Player, "Hasted"))
        {
            if (!_hasteFreeMove) { _hasteFreeMove = true; }
            else { _hasteFreeMove = false; }
        }

        // Hunger.
        if (_state.CurrentFloor != FloorTown)
        {
            _state.Player.Hunger -= 1 + Encumbrance(_state.Player) / 2;
            if (_state.Player.Hunger <= 0)
            {
                _state.Player.Hunger = 0;
                _state.Player.Hp -= 1;
                if (_state.Turn % 5 == 0) AddLog("You are starving!");
                if (_state.Player.Hp <= 0) { _mode = Mode.Dead; AddLog("You starved to death."); return; }
            }
            else if (_state.Player.Hunger < 200 && _state.Turn % 20 == 0)
                AddLog("You feel hungry.");
        }

        // Status ticks.
        foreach (var e in _state.Player.Effects.ToList())
        {
            e.TurnsLeft--;
            if (e.Name == "Poisoned" && e.TurnsLeft > 0)
            {
                _state.Player.Hp -= e.Magnitude;
                if (_state.Player.Hp <= 0) { _mode = Mode.Dead; AddLog("The poison overcomes you."); return; }
            }
            if (e.TurnsLeft <= 0)
            {
                AddLog($"You are no longer {e.Name.ToLowerInvariant()}.");
                _state.Player.Effects.Remove(e);
            }
        }

        // Regen.
        int enc = Encumbrance(_state.Player);
        int regenInterval = 4 + enc * 4;
        if (HasFlag(_state.Player, ItemFlag.Regen)) regenInterval = Math.Max(2, regenInterval - 2);
        if (_state.Turn % regenInterval == 0)
        {
            if (_state.Player.Hp < MaxHp(_state.Player)) _state.Player.Hp++;
            if (_state.Player.Mp < MaxMp(_state.Player)) _state.Player.Mp++;
        }

        ReveralFov();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Spells
    // ─────────────────────────────────────────────────────────────────────
    private bool TryCastByKnownIndex(int slot)
    {
        // 1-9 keys cast the first 9 known spells in spellbook order.
        var known = _state.Player.KnownSpells.OrderBy(i => i).ToList();
        if (slot >= known.Count) { AddLog("You don't know that many spells."); return false; }
        return CastSpell(known[slot]);
    }

    private bool CastSpell(int spellIdx)
    {
        if (!_state.Player.KnownSpells.Contains(spellIdx)) { AddLog("You don't know that spell."); return false; }
        var s = Spells[spellIdx];
        if (_state.Player.Int < s.MinIntellect) { AddLog($"That spell needs {s.MinIntellect} Int."); return false; }
        if (_state.Player.Mp < s.Cost) { AddLog("Not enough mana."); return false; }
        _state.Player.Mp -= s.Cost;
        switch (spellIdx)
        {
            case 0:  CastMagicMissile(8, 13); break;       // Magic Arrow
            case 1:  CastSplash(14, 22, MonFlag.Fire, "fire", 4); break; // Fireball
            case 2:  CastLine(13, 19, MonFlag.Lightning, "lightning"); break;
            case 3:  CastSplash(18, 28, MonFlag.Ice, "cold", 5); break;
            case 4:  CastMagicMissile(10, 16); AddLog("Acid sizzles."); break;
            case 5:  CastMagicMissile(8, 12); break;
            case 6:  HealHero(_rng.Next(15, 26)); break;
            case 7:  HealHero(_rng.Next(40, 71)); break;
            case 8:  RemoveStatus(_state.Player, "Poisoned"); AddLog("You feel the toxin clear."); break;
            case 9:
                _state.Player.Hp = MaxHp(_state.Player);
                _state.Player.Mp = MaxMp(_state.Player);
                _state.Player.Effects.Clear();
                AddLog("A wave of restoration sweeps over you.");
                break;
            case 10: AddStatus(_state.Player, "Resist Fire", 30); AddLog("You feel cool."); break;
            case 11: AddStatus(_state.Player, "Resist Cold", 30); AddLog("You feel warm."); break;
            case 12: AddStatus(_state.Player, "Reflect", 1, 0); AddLog("A shimmering shield surrounds you."); break;
            case 13: AddStatus(_state.Player, "Protected", 30); AddLog("Your skin hardens."); break;
            case 14: AddStatus(_state.Player, "Blessed", 30); AddLog("You feel blessed."); break;
            case 15: PhaseDoor(); break;
            case 16: TeleportRandom(); break;
            case 17: AddStatus(_state.Player, "Hasted", 15); AddLog("Time slows around you."); break;
            case 18: AddStatus(_state.Player, "Levitating", 30); AddLog("Your feet leave the ground."); break;
            case 19: foreach (var it in CurFloor.Items) CurFloor.Seen[it.Y * MapCols + it.X] = true;
                     AddLog("Items glimmer in the dark."); break;
            case 20: foreach (var m in CurFloor.Monsters) m.Asleep = false;
                     AddLog("You sense every monster on this floor."); break;
            case 21: CurFloor.Mapped = true;
                     for (int i = 0; i < CurFloor.Seen.Length; i++) CurFloor.Seen[i] = true;
                     AddLog("The dungeon's geometry is laid bare."); break;
            case 22:
                // identify: hands back the first unidentified pack item.
                var unk = _state.Player.Pack.FirstOrDefault(p => !IsIdentified(p));
                if (unk != null) Identify(unk);
                else AddLog("Nothing in your pack is unknown.");
                break;
            case 23: for (int i = 0; i < CurFloor.Seen.Length; i++) CurFloor.Seen[i] = true;
                     AddLog("Magical light fills the chamber."); break;
        }
        return true;
    }

    private void CastMagicMissile(int loDmg, int hiDmg)
    {
        var t = NearestMonster();
        if (t == null) { AddLog("No target."); return; }
        int dmg = _rng.Next(loDmg, hiDmg + 1) + _state.Player.Int / 4;
        t.Hp -= dmg;
        AddLog($"A bolt strikes the {t.TypeName} for {dmg}.");
        if (t.Hp <= 0) KillMonster(t);
    }

    private void CastSplash(int loDmg, int hiDmg, MonFlag flag, string label, int radius)
    {
        int dmg = _rng.Next(loDmg, hiDmg + 1) + _state.Player.Int / 3;
        int hits = 0;
        var doomed = new List<MonInst>();
        foreach (var mon in CurFloor.Monsters)
        {
            int d = Math.Max(Math.Abs(mon.X - _state.Player.X), Math.Abs(mon.Y - _state.Player.Y));
            if (d > radius) continue;
            mon.Hp -= dmg; hits++;
            if (mon.Hp <= 0) doomed.Add(mon);
        }
        foreach (var m in doomed) KillMonster(m);
        AddLog(hits == 0 ? $"Your {label} engulfs only air." : $"A burst of {label} hits {hits} foe(s) for {dmg}.");
    }

    private void CastLine(int loDmg, int hiDmg, MonFlag flag, string label)
    {
        // Pick the direction of the nearest monster and trace a 6-tile line.
        var near = NearestMonster();
        if (near == null) { AddLog($"Your {label} dissipates."); return; }
        int dx = Math.Sign(near.X - _state.Player.X);
        int dy = Math.Sign(near.Y - _state.Player.Y);
        if (dx == 0 && dy == 0) dy = -1;
        int dmg = _rng.Next(loDmg, hiDmg + 1) + _state.Player.Int / 3;
        int x = _state.Player.X, y = _state.Player.Y;
        int hits = 0;
        for (int step = 0; step < 8; step++)
        {
            x += dx; y += dy;
            if (x < 0 || y < 0 || x >= MapCols || y >= MapRows) break;
            var m = UnflattenMap(CurFloor.Rows);
            if (m[x, y] == TWall) break;
            var mon = CurFloor.Monsters.FirstOrDefault(o => o.X == x && o.Y == y);
            if (mon != null)
            {
                mon.Hp -= dmg; hits++;
                if (mon.Hp <= 0) KillMonster(mon);
            }
        }
        AddLog(hits == 0 ? $"Your {label} crackles harmlessly." : $"Your {label} strikes {hits} foe(s) for {dmg}.");
    }

    private void HealHero(int amt)
    {
        int newHp = Math.Min(MaxHp(_state.Player), _state.Player.Hp + amt);
        int healed = newHp - _state.Player.Hp;
        _state.Player.Hp = newHp;
        AddLog($"You are healed (+{healed} HP).");
    }

    private void PhaseDoor()
    {
        for (int attempt = 0; attempt < 80; attempt++)
        {
            int dx = _rng.Next(-6, 7), dy = _rng.Next(-6, 7);
            int nx = _state.Player.X + dx, ny = _state.Player.Y + dy;
            if (nx < 1 || ny < 1 || nx >= MapCols - 1 || ny >= MapRows - 1) continue;
            var m = UnflattenMap(CurFloor.Rows);
            if (m[nx, ny] != TFloor) continue;
            if (CurFloor.Monsters.Any(mm => mm.X == nx && mm.Y == ny)) continue;
            _state.Player.X = nx; _state.Player.Y = ny;
            AddLog("You phase a short distance."); return;
        }
        AddLog("Your phase door fizzles.");
    }

    private void TeleportRandom()
    {
        var m = UnflattenMap(CurFloor.Rows);
        var cells = new List<(int x, int y)>();
        for (int x = 1; x < MapCols - 1; x++)
            for (int y = 1; y < MapRows - 1; y++)
                if ((m[x, y] == TFloor || m[x, y] == TGrass)
                    && !CurFloor.Monsters.Any(mm => mm.X == x && mm.Y == y))
                    cells.Add((x, y));
        if (cells.Count == 0) { AddLog("Teleport fizzles."); return; }
        var dest = cells[_rng.Next(cells.Count)];
        _state.Player.X = dest.x; _state.Player.Y = dest.y;
        AddLog("Reality bends — you stand somewhere else.");
    }

    private MonInst? NearestMonster()
    {
        MonInst? best = null;
        int bestD = int.MaxValue;
        foreach (var m in CurFloor.Monsters)
        {
            int d = Math.Max(Math.Abs(m.X - _state.Player.X), Math.Abs(m.Y - _state.Player.Y));
            if (d < bestD) { bestD = d; best = m; }
        }
        return best;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Inventory / item use
    // ─────────────────────────────────────────────────────────────────────
    private void UpdateInventory()
    {
        var pack = _state.Player.Pack;
        int n = pack.Count;
        if (n > 0)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Up))   _invCursor = (_invCursor + n - 1) % n;
            if (Raylib.IsKeyPressed(KeyboardKey.Down)) _invCursor = (_invCursor + 1) % n;
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Escape) || Raylib.IsKeyPressed(KeyboardKey.I))
        { _mode = Mode.Playing; _prompt = ""; return; }
        if (Raylib.IsKeyPressed(KeyboardKey.Enter) && n > 0) UseOrEquip(_invCursor);
        if (Raylib.IsKeyPressed(KeyboardKey.D) && n > 0)
        {
            string nm = pack[_invCursor];
            pack.RemoveAt(_invCursor);
            CurFloor.Items.Add(new ItemDrop { Name = nm, X = _state.Player.X, Y = _state.Player.Y });
            AddLog($"You drop the {DisplayName(nm)}.");
            _invCursor = Math.Clamp(_invCursor, 0, Math.Max(0, pack.Count - 1));
        }
        if (Raylib.IsKeyPressed(KeyboardKey.X) && n > 0)
        {
            // Examine.
            _selectedItemName = pack[_invCursor];
            _mode = Mode.ItemDetail;
        }
    }

    private void UseOrEquip(int idx)
    {
        string nm = _state.Player.Pack[idx];
        var def = Def(nm);

        if (def.IsPotion)
        {
            if (def.PotionHeal > 0)
            { _state.Player.Hp = Math.Min(MaxHp(_state.Player), _state.Player.Hp + def.PotionHeal);
              AddLog($"You drink the {DisplayName(nm)} — HP +{def.PotionHeal}."); }
            if (def.PotionMana > 0)
            { _state.Player.Mp = Math.Min(MaxMp(_state.Player), _state.Player.Mp + def.PotionMana);
              AddLog($"You drink the {DisplayName(nm)} — MP +{def.PotionMana}."); }
            if (def.PotionCurePoison) { RemoveStatus(_state.Player, "Poisoned"); AddLog("The poison clears."); }
            if (def.PotionExtraHp) { _state.Player.MaxHpBonus += 10; _state.Player.Hp = MaxHp(_state.Player); AddLog("You feel hardier."); }
            if (def.PotionExtraMp) { _state.Player.MaxMpBonus += 10; _state.Player.Mp = MaxMp(_state.Player); AddLog("You feel keener."); }
            if (def.PotionStrength) { _state.Player.Str = Math.Min(25, _state.Player.Str + 1); AddLog("You feel stronger."); }
            Identify(nm);
            _state.Player.Pack.RemoveAt(idx);
            _mode = Mode.Playing; _prompt = ""; ResolveEndOfTurn();
            return;
        }

        if (def.IsScroll)
        {
            if (def.ScrollIdentify)
            {
                // Identify the next unknown thing in pack.
                var unk = _state.Player.Pack.Where((p, i) => i != idx && !IsIdentified(p)).FirstOrDefault();
                if (unk != null) Identify(unk);
                else AddLog("Nothing else needs identifying.");
            }
            if (def.ScrollTeleport) TeleportRandom();
            if (def.ScrollMagicMap)
            { CurFloor.Mapped = true; for (int i = 0; i < CurFloor.Seen.Length; i++) CurFloor.Seen[i] = true;
              AddLog("The dungeon's geometry is laid bare."); }
            if (def.ScrollEnchant)
            {
                // Enchants the equipped weapon (+1 dmg perm) or armour (+1 def). For
                // simplicity, swap with a higher-tier piece.
                AddLog("A scroll of enchantment shimmers — but you sense it's only a one-time blessing.");
                _state.Player.MaxHpBonus += 2;
            }
            if (def.ScrollHolyWord)
            {
                int killed = 0;
                foreach (var mon in CurFloor.Monsters.ToList())
                    if ((MDef(mon.TypeName).Flags & MonFlag.Undead) != 0) { CurFloor.Monsters.Remove(mon); killed++; }
                AddLog(killed == 0 ? "The word echoes unanswered." : $"The holy word destroys {killed} undead.");
            }
            Identify(nm);
            _state.Player.Pack.RemoveAt(idx);
            _mode = Mode.Playing; _prompt = ""; ResolveEndOfTurn();
            return;
        }

        if (def.IsFood)
        {
            _state.Player.Hunger = Math.Min(2500, _state.Player.Hunger + def.FoodNutrition);
            AddLog($"You eat the {nm}.");
            _state.Player.Pack.RemoveAt(idx);
            _mode = Mode.Playing; ResolveEndOfTurn();
            return;
        }

        if (def.IsBook)
        {
            int sp = def.BookSpellIdx;
            if (sp < 0 || sp >= Spells.Length) { AddLog("Nothing to learn."); return; }
            if (_state.Player.KnownSpells.Add(sp))
                AddLog($"You learn {Spells[sp].Name}.");
            else AddLog("You already know that spell.");
            _state.Player.Pack.RemoveAt(idx);
            _mode = Mode.Playing;
            return;
        }

        if (def.IsLight)
        {
            AddLog($"You light the {nm}.");
            // Lights consume themselves over time. For now, they just identify
            // and stay in pack (they grant ambient light via inventory check).
            return;
        }

        if (def.Slot is Slot s)
        {
            // Two-handed weapons forcibly unequip the shield.
            if (def.TwoHand)
            {
                var sh = _state.Player.Equip.GetValueOrDefault(nameof(Slot.Shield));
                if (sh != null) { _state.Player.Pack.Add(sh); _state.Player.Equip[nameof(Slot.Shield)] = null; AddLog($"You sling your {sh} on your back."); }
            }
            // Rings need to pick which finger — left first if empty.
            if (s == Slot.LeftRing || s == Slot.RightRing)
            {
                var left = _state.Player.Equip.GetValueOrDefault(nameof(Slot.LeftRing));
                if (left == null) SwapEquip(idx, nameof(Slot.LeftRing));
                else SwapEquip(idx, nameof(Slot.RightRing));
            }
            else SwapEquip(idx, s.ToString());
            AddLog($"You equip the {nm}.");
            _mode = Mode.Playing;
        }
    }

    private void SwapEquip(int packIdx, string slotKey)
    {
        string newName = _state.Player.Pack[packIdx];
        _state.Player.Equip.TryGetValue(slotKey, out var prev);
        if (prev != null) _state.Player.Pack[packIdx] = prev;
        else _state.Player.Pack.RemoveAt(packIdx);
        _state.Player.Equip[slotKey] = newName;
    }

    private void UpdateSpellMenu()
    {
        var known = _state.Player.KnownSpells.OrderBy(i => i).ToList();
        int n = known.Count;
        if (n > 0)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Up))   _spellCursor = (_spellCursor + n - 1) % n;
            if (Raylib.IsKeyPressed(KeyboardKey.Down)) _spellCursor = (_spellCursor + 1) % n;
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Escape) || Raylib.IsKeyPressed(KeyboardKey.M))
        { _mode = Mode.Playing; return; }
        if (Raylib.IsKeyPressed(KeyboardKey.Enter) && n > 0)
        {
            _mode = Mode.Playing;
            if (CastSpell(known[_spellCursor])) ResolveEndOfTurn();
        }
    }

    private void UpdateItemDetail()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape) || Raylib.IsKeyPressed(KeyboardKey.X)
            || Raylib.IsKeyPressed(KeyboardKey.Enter))
        { _mode = Mode.Inventory; }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Town services
    // ─────────────────────────────────────────────────────────────────────
    private void OpenShopFor(string name)
    {
        _activeShop = name;
        _shopBuying = true;
        _shopCursor = 0;
        _shopStock = name switch
        {
            "General Store" => new List<string> {
                "Rations", "Lembas", "Apple", "Torch", "Brass Lantern",
                "Scroll of Identify", "Scroll of Magic Map", "Scroll of Teleport",
                "Potion of Healing", "Potion of Mana", "Cloak",
                "Two-Slot Belt", "Three-Slot Belt",
            },
            "Armory" => new List<string> {
                "Dagger", "Mace", "Short Sword", "Long Sword", "Battle Axe",
                "Bastard Sword", "Two-Handed Sword",
                "Leather Armour", "Studded Leather", "Ring Mail", "Chain Mail", "Splint Mail",
                "Small Wooden Shield", "Medium Iron Shield", "Large Iron Shield",
                "Leather Helm", "Iron Helm", "Steel Helm",
                "Leather Boots", "Iron Boots", "Bracers", "Gauntlets",
            },
            "Magic Shop" => new List<string> {
                "Book of Magic Arrow", "Book of Fireball", "Book of Lightning Bolt",
                "Book of Cone of Cold", "Book of Acid Bolt", "Book of Force Bolt",
                "Book of Lesser Heal", "Book of Greater Heal", "Book of Cure Poison",
                "Book of Resist Fire", "Book of Resist Cold", "Book of Bless",
                "Book of Protection", "Book of Reflect",
                "Book of Phase Door", "Book of Teleport", "Book of Haste", "Book of Levitate",
                "Book of Detect Items", "Book of Detect Monsters", "Book of Magic Map",
                "Book of Identify", "Book of Light",
                "Potion of Greater Healing", "Potion of Greater Mana",
                "Potion of Cure Poison", "Potion of Strength",
            },
            "Junk Shop" => new List<string> {
                "Broken Sword", "Rusty Armour", "Mushroom", "Apple",
                "Cloak", "Two-Slot Belt", "Bracers", "Gauntlets",
            },
            _ => new List<string>(),
        };
        _mode = Mode.Shop;
    }

    private void UpdateShop()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { _mode = Mode.Playing; return; }
        if (Raylib.IsKeyPressed(KeyboardKey.B)) { _shopBuying = true; _shopCursor = 0; return; }
        if (Raylib.IsKeyPressed(KeyboardKey.S)) { _shopBuying = false; _shopCursor = 0; return; }

        var list = _shopBuying ? _shopStock : _state.Player.Pack;
        int count = list.Count;
        if (count > 0)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Up))   _shopCursor = (_shopCursor + count - 1) % count;
            if (Raylib.IsKeyPressed(KeyboardKey.Down)) _shopCursor = (_shopCursor + 1) % count;
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Enter) && count > 0)
        {
            if (_shopBuying)
            {
                string nm = list[_shopCursor];
                int price = Def(nm).Price;
                if (_state.Player.Gold < price) AddLog("Not enough gold.");
                else if (_state.Player.Pack.Count >= 26) AddLog("Your pack is full.");
                else { _state.Player.Gold -= price; _state.Player.Pack.Add(nm); AddLog($"You buy the {DisplayName(nm)} for {price}g."); }
            }
            else
            {
                string nm = list[_shopCursor];
                int price = Def(nm).Price / 2;
                _state.Player.Gold += price;
                _state.Player.Pack.RemoveAt(_shopCursor);
                AddLog($"You sell the {DisplayName(nm)} for {price}g.");
                _shopCursor = Math.Clamp(_shopCursor, 0, Math.Max(0, _state.Player.Pack.Count - 1));
            }
        }
    }

    private void UpdateHealer()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { _mode = Mode.Playing; return; }
        if (Raylib.IsKeyPressed(KeyboardKey.One))
        {
            int cost = 40;
            if (_state.Player.Gold < cost) { AddLog("Not enough gold."); return; }
            _state.Player.Gold -= cost;
            _state.Player.Hp = MaxHp(_state.Player);
            AddLog("The healer mends every wound.");
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Two))
        {
            int cost = 25;
            if (_state.Player.Gold < cost) { AddLog("Not enough gold."); return; }
            _state.Player.Gold -= cost;
            _state.Player.Effects.Clear();
            AddLog("Status afflictions are dispelled.");
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Three))
        {
            int cost = 80;
            if (_state.Player.Gold < cost) { AddLog("Not enough gold."); return; }
            _state.Player.Gold -= cost;
            _state.Player.Hp = MaxHp(_state.Player);
            _state.Player.Mp = MaxMp(_state.Player);
            _state.Player.Effects.Clear();
            AddLog("Greater restoration courses through you.");
        }
    }

    private void UpdateSage()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { _mode = Mode.Playing; return; }
        var pack = _state.Player.Pack;
        int n = pack.Count;
        if (n > 0)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Up))   _shopCursor = (_shopCursor + n - 1) % n;
            if (Raylib.IsKeyPressed(KeyboardKey.Down)) _shopCursor = (_shopCursor + 1) % n;
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Enter) && n > 0)
        {
            string nm = pack[_shopCursor];
            int cost = 30;
            if (IsIdentified(nm)) { AddLog($"The {nm} needs no identification."); return; }
            if (_state.Player.Gold < cost) { AddLog("Not enough gold."); return; }
            _state.Player.Gold -= cost;
            Identify(nm);
        }
    }

    private void UpdateBank()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { _mode = Mode.Playing; return; }
        int amount = Raylib.IsKeyDown(KeyboardKey.LeftShift) ? 100 : 10;
        if (Raylib.IsKeyPressed(KeyboardKey.D))
        {
            int put = Math.Min(_state.Player.Gold, amount);
            _state.Player.Gold -= put;
            _state.Player.Bank += put;
            AddLog($"Deposited {put}g.");
        }
        if (Raylib.IsKeyPressed(KeyboardKey.W))
        {
            int take = Math.Min(_state.Player.Bank, amount);
            _state.Player.Gold += take;
            _state.Player.Bank -= take;
            AddLog($"Withdrew {take}g.");
        }
    }

    private void UpdateTrainer()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { _mode = Mode.Playing; return; }
        // Each stat costs increasing XP; spending costs gold too.
        int cost = 200 + _state.Player.Level * 60;
        int gcost = 200;
        if (_state.Player.Xp < cost || _state.Player.Gold < gcost)
        {
            // Still let the user back out.
            return;
        }
        bool spent = false;
        if (Raylib.IsKeyPressed(KeyboardKey.One)) { _state.Player.Str++; spent = true; AddLog($"Str → {_state.Player.Str}"); }
        if (Raylib.IsKeyPressed(KeyboardKey.Two)) { _state.Player.Int++; spent = true; AddLog($"Int → {_state.Player.Int}"); }
        if (Raylib.IsKeyPressed(KeyboardKey.Three)) { _state.Player.Dex++; spent = true; AddLog($"Dex → {_state.Player.Dex}"); }
        if (Raylib.IsKeyPressed(KeyboardKey.Four)) { _state.Player.Con++; spent = true; AddLog($"Con → {_state.Player.Con}"); }
        if (spent) { _state.Player.Xp -= cost; _state.Player.Gold -= gcost; }
    }

    private void UpdateHouse()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { _mode = Mode.Playing; return; }
        if (Raylib.IsKeyPressed(KeyboardKey.Tab)) { _stashing = !_stashing; _stashCursor = 0; return; }
        var src = _stashing ? _state.Player.Pack : _state.Player.Stash;
        var dst = _stashing ? _state.Player.Stash : _state.Player.Pack;
        int n = src.Count;
        if (n > 0)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Up))   _stashCursor = (_stashCursor + n - 1) % n;
            if (Raylib.IsKeyPressed(KeyboardKey.Down)) _stashCursor = (_stashCursor + 1) % n;
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Enter) && n > 0)
        {
            if (!_stashing && _state.Player.Pack.Count >= 26) { AddLog("Pack full."); return; }
            string nm = src[_stashCursor];
            dst.Add(nm);
            src.RemoveAt(_stashCursor);
            _stashCursor = Math.Clamp(_stashCursor, 0, Math.Max(0, src.Count - 1));
            AddLog(_stashing ? $"You stash the {DisplayName(nm)}." : $"You take the {DisplayName(nm)} from your stash.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  FOV / log
    // ─────────────────────────────────────────────────────────────────────
    private void ReveralFov()
    {
        // Larger radius if Telepathy / Detect Monsters is on.
        int radius = 7;
        if (HasFlag(_state.Player, ItemFlag.Telepathy)) radius = 12;
        if (HasFlag(_state.Player, ItemFlag.DetectMonsters)) radius = Math.Max(radius, 10);
        int hx = _state.Player.X, hy = _state.Player.Y;
        for (int x = Math.Max(0, hx - radius); x < Math.Min(MapCols, hx + radius + 1); x++)
            for (int y = Math.Max(0, hy - radius); y < Math.Min(MapRows, hy + radius + 1); y++)
            {
                int d = Math.Max(Math.Abs(x - hx), Math.Abs(y - hy));
                if (d <= radius) CurFloor.Seen[y * MapCols + x] = true;
            }
    }

    private bool IsVisibleNow(int x, int y)
    {
        int radius = 7;
        if (HasFlag(_state.Player, ItemFlag.Telepathy)) radius = 12;
        int d = Math.Max(Math.Abs(x - _state.Player.X), Math.Abs(y - _state.Player.Y));
        return d <= radius;
    }

    private void AddLog(string line) { _log.Add(line); if (_log.Count > 400) _log.RemoveRange(0, _log.Count - 400); _scrollLog = 0; }

    // ─────────────────────────────────────────────────────────────────────
    //  Drawing entry
    // ─────────────────────────────────────────────────────────────────────
    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Castle of the Winds", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, MenuItemsForMode(), -1);

        switch (_mode)
        {
            case Mode.Title:    DrawTitle(panelOffset); break;
            case Mode.Intro:    DrawIntro(panelOffset); break;
            case Mode.CharCreation: DrawCharCreation(panelOffset); break;
            default:
                DrawMap(panelOffset);
                DrawSidebar(panelOffset);
                DrawLog(panelOffset);
                if (_mode == Mode.Inventory) DrawInventoryOverlay(panelOffset);
                if (_mode == Mode.SpellMenu) DrawSpellOverlay(panelOffset);
                if (_mode == Mode.ItemDetail) DrawItemDetail(panelOffset);
                if (_mode == Mode.Shop) DrawShopOverlay(panelOffset);
                if (_mode == Mode.Healer) DrawHealerOverlay(panelOffset);
                if (_mode == Mode.Sage) DrawSageOverlay(panelOffset);
                if (_mode == Mode.Bank) DrawBankOverlay(panelOffset);
                if (_mode == Mode.Trainer) DrawTrainerOverlay(panelOffset);
                if (_mode == Mode.House) DrawHouseOverlay(panelOffset);
                if (_mode == Mode.Dead || _mode == Mode.Won) DrawEndBanner(panelOffset);
                break;
        }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        if (_mode == Mode.Title || _mode == Mode.Intro)
            RetroWidgets.StatusBar(status, "Castle of the Winds — Part 1: A Question of Vengeance", "Enter to continue");
        else if (_mode == Mode.CharCreation)
            RetroWidgets.StatusBar(status,
                $"Points left: {_ccPoints}",
                "Up/Down select  ←/→ adjust  R re-roll  G gender  Enter Begin");
        else
        {
            var h = _state.Player;
            int enc = Encumbrance(h);
            string statusEffects = string.Join(" ", h.Effects.Where(e => e.TurnsLeft > 0).Select(e => $"[{e.Name}]"));
            string hunger = h.Hunger < 200 ? "  HUNGRY" : h.Hunger < 60 ? "  STARVING" : "";
            RetroWidgets.StatusBar(status,
                $"{h.Name}  L{h.Level}  HP {h.Hp}/{MaxHp(h)}  MP {h.Mp}/{MaxMp(h)}",
                $"{DungeonName(_state.CurrentFloor)}  G {h.Gold}  Bank {h.Bank}  Enc {EncNames[enc]}{hunger}  {statusEffects}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Title / intro / char creation drawing
    // ─────────────────────────────────────────────────────────────────────
    private Rectangle ContentRect(Vector2 panelOffset)
    {
        float y = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 8;
        float h = PanelSize.Y - (y - panelOffset.Y) - FrameInset - RetroWidgets.StatusBarHeight - 4;
        return new Rectangle(panelOffset.X + FrameInset + 8, y, PanelSize.X - 2 * (FrameInset + 8), h);
    }

    private void DrawTitle(Vector2 panelOffset)
    {
        EnsureTextures();
        var c = ContentRect(panelOffset);
        Raylib.DrawRectangleRec(c, new Color((byte)16, (byte)16, (byte)28, (byte)255));
        // Splash image, scaled to fit inside the content rect while preserving aspect.
        float sx = c.Width / _splashTex.Width;
        float sy = c.Height / _splashTex.Height;
        float scale = Math.Min(sx, sy);
        int dw = (int)(_splashTex.Width * scale);
        int dh = (int)(_splashTex.Height * scale);
        int dx = (int)(c.X + (c.Width - dw) / 2);
        int dy = (int)c.Y;
        Raylib.DrawTexturePro(_splashTex,
            new Rectangle(0, 0, _splashTex.Width, _splashTex.Height),
            new Rectangle(dx, dy, dw, dh), Vector2.Zero, 0, Color.White);

        int cx = (int)(c.X + c.Width / 2);
        int prompt_y = (int)(c.Y + c.Height) - 32;
        string menu = "▶  Press Enter to begin a new game";
        FontManager.DrawText(menu, cx - FontManager.MeasureText(menu, 14) / 2, prompt_y, 14,
            new Color((byte)240, (byte)220, (byte)160, (byte)255));
    }

    private static readonly string[] IntroText =
    {
        "The winter is bitter on the road to Bjarnarhaven.",
        "Your godparents, Olaf and Greta, were the only family you had —",
        "homesteaders at the foot of the Halls of Mischief, deep in the",
        "Norse hinterlands. They raised you when nobody else would.",
        "",
        "When you reach their farm you find the longhouse burned, the",
        "stables empty, and your godparents murdered. In the ashes you",
        "find a single sign of the killer's master: the seal of Surtur,",
        "the Fire Giant King.",
        "",
        "You bury Olaf and Greta and ride to Sosaria Hold, the closest",
        "town. There must be a way into the Halls. There must be a",
        "reckoning. There must be a question of vengeance answered.",
        "",
        "(Press Enter to continue.)",
    };

    private void DrawIntro(Vector2 panelOffset)
    {
        var c = ContentRect(panelOffset);
        Raylib.DrawRectangleRec(c, new Color((byte)16, (byte)16, (byte)28, (byte)255));
        int x = (int)c.X + 30;
        int y = (int)c.Y + 30;
        foreach (var line in IntroText)
        {
            FontManager.DrawText(line, x, y, 14, RetroSkin.BodyText);
            y += 22;
        }
    }

    private Rectangle NameFieldRect()
    {
        float y = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 80;
        return new Rectangle(PanelSize.X / 2 - 120, y, 240, 22);
    }

    private void DrawCharCreation(Vector2 panelOffset)
    {
        var content = ContentRect(panelOffset);
        var form = new Rectangle(content.X + 60, content.Y + 4, content.Width - 120, content.Height - 20);
        RetroSkin.DrawSunken(form, RetroSkin.FaceLight);

        int cx = (int)(form.X + form.Width / 2);
        int y = (int)form.Y + 14;
        FontManager.DrawText("Character Creation", cx - FontManager.MeasureText("Character Creation", 22) / 2, y, 22, RetroSkin.BodyText);
        y += 36;

        var nameRel = NameFieldRect();
        var nameAbs = new Rectangle(panelOffset.X + nameRel.X, panelOffset.Y + nameRel.Y, nameRel.Width, nameRel.Height);
        RetroSkin.DrawSunken(nameAbs, Color.White);
        FontManager.DrawText("Name:", (int)nameAbs.X - 60, (int)nameAbs.Y + 4, 14, RetroSkin.BodyText);
        string nameText = _ccName + (_ccNameEdit && (Raylib.GetTime() % 1.0) < 0.5 ? "│" : "");
        FontManager.DrawText(nameText, (int)nameAbs.X + 6, (int)nameAbs.Y + 4, 14, RetroSkin.BodyText);

        y = (int)nameAbs.Y + 34;
        string gender = _ccMale ? "Male  (G to toggle)" : "Female  (G to toggle)";
        FontManager.DrawText("Gender: " + gender, cx - FontManager.MeasureText("Gender: " + gender, 14) / 2, y, 14, RetroSkin.BodyText);
        y += 22;
        FontManager.DrawText($"Age: {_ccAge}", cx - FontManager.MeasureText($"Age: {_ccAge}", 14) / 2, y, 14, RetroSkin.BodyText);
        y += 24;

        string[] names = { "Strength", "Intellect", "Dexterity", "Constitution" };
        int[] vals = { _ccStr, _ccInt, _ccDex, _ccCon };
        for (int i = 0; i < 4; i++)
        {
            string mark = i == _ccCursor ? "►" : " ";
            string line = $"{mark} {names[i],-13}  {vals[i],2}";
            var col = i == _ccCursor ? RetroSkin.TitleActive : RetroSkin.BodyText;
            FontManager.DrawText(line, cx - 90, y, 16, col);
            y += 22;
        }
        y += 8;
        FontManager.DrawText($"Points remaining: {_ccPoints}",
            cx - FontManager.MeasureText($"Points remaining: {_ccPoints}", 14) / 2, y, 14, RetroSkin.BodyText);
        y += 22;
        FontManager.DrawText("Up/Down select  ←/→ adjust  R reroll  Enter when 0 left",
            cx - FontManager.MeasureText("Up/Down select  ←/→ adjust  R reroll  Enter when 0 left", 12) / 2,
            y, 12, RetroSkin.DisabledText);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Map drawing
    // ─────────────────────────────────────────────────────────────────────
    private Rectangle MapRect(Vector2 panelOffset)
    {
        float y = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 6;
        return new Rectangle(panelOffset.X + FrameInset + 6, y, ViewCols * Tile, ViewRows * Tile);
    }

    private void DrawMap(Vector2 panelOffset)
    {
        EnsureTextures();
        var rect = MapRect(panelOffset);
        Raylib.DrawRectangleRec(rect, new Color((byte)10, (byte)10, (byte)18, (byte)255));
        var map = UnflattenMap(CurFloor.Rows);

        bool outdoors = _state.CurrentFloor == FloorTown || _state.CurrentFloor == FloorWilderness;
        var (vx, vy) = ViewportOrigin();

        for (int sx = 0; sx < ViewCols; sx++)
            for (int sy = 0; sy < ViewRows; sy++)
            {
                int x = vx + sx, y = vy + sy;
                if (x < 0 || y < 0 || x >= MapCols || y >= MapRows) continue;
                bool seen = CurFloor.Seen[y * MapCols + x];
                bool vis = outdoors || IsVisibleNow(x, y);
                int px = (int)rect.X + sx * Tile;
                int py = (int)rect.Y + sy * Tile;
                if (!seen) continue;
                char t = map[x, y];
                DrawTile(px, py, t, vis);
                if (!vis && t != TGrass && t != TTree && t != TWater)
                    Raylib.DrawRectangle(px, py, Tile, Tile, new Color((byte)0, (byte)0, (byte)0, (byte)110));
            }

        // Items
        foreach (var it in CurFloor.Items)
        {
            if (!CurFloor.Seen[it.Y * MapCols + it.X]) continue;
            int sx = it.X - vx, sy = it.Y - vy;
            if (sx < 0 || sy < 0 || sx >= ViewCols || sy >= ViewRows) continue;
            int px = (int)rect.X + sx * Tile;
            int py = (int)rect.Y + sy * Tile;
            var (ix, iy) = ItemSpriteFor(it.Name);
            DrawSprite(_itemsTex, ix, iy, px, py);
        }

        // Monsters
        foreach (var mon in CurFloor.Monsters)
        {
            if (!IsVisibleNow(mon.X, mon.Y) && !outdoors && !HasFlag(_state.Player, ItemFlag.Telepathy)) continue;
            int sx = mon.X - vx, sy = mon.Y - vy;
            if (sx < 0 || sy < 0 || sx >= ViewCols || sy >= ViewRows) continue;
            int px = (int)rect.X + sx * Tile;
            int py = (int)rect.Y + sy * Tile;
            if (MonsterSprite.TryGetValue(mon.TypeName, out var ms))
                DrawSprite(_monstersTex, ms.x, ms.y, px, py);
            else
            {
                // Fallback: glyph from the bestiary def.
                var def = MDef(mon.TypeName);
                FontManager.DrawText(def.Glyph.ToString(), px + 8, py + 4, 18, Color.Yellow);
            }
        }

        // Hero
        int hsx = _state.Player.X - vx, hsy = _state.Player.Y - vy;
        if (hsx >= 0 && hsy >= 0 && hsx < ViewCols && hsy < ViewRows)
        {
            int px = (int)rect.X + hsx * Tile;
            int py = (int)rect.Y + hsy * Tile;
            var hero = _state.Player.Male ? HeroMaleSprite : HeroFemaleSprite;
            DrawSprite(_monstersTex, hero.x, hero.y, px, py);
        }
    }

    // Renders a single map cell using cotwelm's sprite sheet.
    private void DrawTile(int px, int py, char t, bool litFov)
    {
        (int x, int y) src;
        bool useTileSheet = true;

        switch (t)
        {
            case TWall:
                src = litFov ? TileWallLit : TileWallDark; break;
            case TFloor:
                src = litFov ? TileFloorLit : TileFloorDark; break;
            case TGrass: src = TileGrass; break;
            case TTree:  src = TileVegePatch; break;
            case TWater: src = TileWater; break;
            case TMountain: src = TileMountain; break;
            case TDoor:  src = TileDoorClosed; break;
            case TStairUp:   src = TileStairsUp; break;
            case TStairDown: src = TileStairsDown; break;
            case TAltar:     src = TileAltar; break;

            // Wilderness dungeon entrances — use distinctive tile glyphs.
            case TTombEnt:   src = TileMineEntrance; break;
            case TSewerEnt:  src = TilePortcullis; break;
            case THallsEnt:  src = TileMineEntrance; break;
            case TTownEnt:   src = TilePath; break;

            // Town building doormats — pick a different tile graphic per shop
            // so each shop reads at a glance, then overlay a letter so the
            // user can still tell them apart unambiguously.
            case TGenStore:  src = TileWell; break;
            case TArmory:    src = TileTownWall; break;
            case TMagicShop: src = TileFountain; break;
            case TJunkShop:  src = TileAshes; break;
            case THealer:    src = TileSign; break;
            case TSage:      src = TileSign; break;
            case TBank:      src = TilePath; break;
            case TTrainer:   src = TilePath; break;
            case TInn:       src = TileWagon; break;
            case THouse:     src = TilePillar; break;
            default:
                useTileSheet = false; src = TileFloorDark; break;
        }
        if (useTileSheet) DrawSprite(_tilesTex, src.x, src.y, px, py);
        else
            Raylib.DrawRectangle(px, py, Tile, Tile, RetroSkin.Face);

        // Letter overlay on building doormats so a colour-blind player can
        // still distinguish G / A / M / J / H / S / B / R / N / P.
        if (t == TGenStore || t == TArmory || t == TMagicShop || t == TJunkShop
            || t == THealer || t == TSage || t == TBank || t == TTrainer
            || t == TInn || t == THouse)
        {
            FontManager.DrawText(t.ToString(), px + 11, py + 6, 18, Color.White);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Sidebar / log drawing
    // ─────────────────────────────────────────────────────────────────────
    private Rectangle SidebarRect(Vector2 panelOffset)
    {
        var m = MapRect(panelOffset);
        return new Rectangle(m.X + m.Width + 8, m.Y, SidebarW, m.Height);
    }

    private void DrawSidebar(Vector2 panelOffset)
    {
        var sb = SidebarRect(panelOffset);
        RetroSkin.DrawSunken(sb, RetroSkin.FaceLight);
        int x = (int)sb.X + 8;
        int y = (int)sb.Y + 8;
        var h = _state.Player;

        FontManager.DrawText(h.Name, x, y, 16, RetroSkin.BodyText); y += 20;
        FontManager.DrawText($"{(h.Male ? "Male" : "Female")}  Age {h.Age}  L{h.Level}",
            x, y, 12, RetroSkin.DisabledText); y += 18;
        FontManager.DrawText($"XP  {h.Xp} / {h.Level * 100}", x, y, 12, RetroSkin.DisabledText); y += 22;

        FontManager.DrawText($"HP   {h.Hp}/{MaxHp(h)}", x, y, 14, RetroSkin.BodyText); y += 18;
        FontManager.DrawText($"Mana {h.Mp}/{MaxMp(h)}", x, y, 14, RetroSkin.BodyText); y += 18;
        FontManager.DrawText($"AC {ArmorClass(h)}   +Hit {ToHit(h)}", x, y, 12, RetroSkin.BodyText); y += 18;
        FontManager.DrawText($"Str {EffStr(h)}  Int {h.Int}", x, y, 12, RetroSkin.BodyText); y += 14;
        FontManager.DrawText($"Dex {EffDex(h)}  Con {h.Con}", x, y, 12, RetroSkin.BodyText); y += 18;
        FontManager.DrawText($"Gold {h.Gold} (+{h.Bank} bank)", x, y, 12, RetroSkin.BodyText); y += 14;
        int hunger = h.Hunger;
        string hStr = hunger > 1800 ? "Sated" : hunger > 800 ? "Fed" : hunger > 200 ? "Peckish" : hunger > 60 ? "Hungry" : "Starving";
        FontManager.DrawText($"Food {hStr}", x, y, 12, RetroSkin.BodyText); y += 18;

        FontManager.DrawText("Equipment", x, y, 12, RetroSkin.DisabledText); y += 16;
        Slot[] slotsOrder = { Slot.Weapon, Slot.Armor, Slot.Shield, Slot.Helmet, Slot.Bracers, Slot.Gauntlets,
                              Slot.Neckwear, Slot.Overgarment, Slot.LeftRing, Slot.RightRing, Slot.Boots, Slot.Belt };
        for (int i = 0; i < slotsOrder.Length; i++)
        {
            var nm = h.Equip.GetValueOrDefault(slotsOrder[i].ToString());
            string short3 = SlotNames[(int)slotsOrder[i]].Substring(0, Math.Min(4, SlotNames[(int)slotsOrder[i]].Length));
            FontManager.DrawText($" {short3,-4}{(nm != null ? DisplayName(nm) : "—")}", x, y + i * 12, 11, RetroSkin.BodyText);
        }
        y += slotsOrder.Length * 12 + 4;

        FontManager.DrawText($"Pack ({h.Pack.Count}/26)", x, y, 12, RetroSkin.DisabledText); y += 14;
        int show = Math.Min(h.Pack.Count, 8);
        for (int i = 0; i < show; i++)
            FontManager.DrawText("• " + DisplayName(h.Pack[i]), x + 2, y + i * 12, 11, RetroSkin.BodyText);
        if (h.Pack.Count > show)
            FontManager.DrawText($"+{h.Pack.Count - show} more (i)", x + 2, y + show * 12, 11, RetroSkin.DisabledText);

        // Keys reminder at the very bottom.
        int ky = (int)(sb.Y + sb.Height) - 80;
        FontManager.DrawText("hjkl/arrows move  yubn diag", x, ky, 10, RetroSkin.DisabledText); ky += 11;
        FontManager.DrawText(", pick up   . wait", x, ky, 10, RetroSkin.DisabledText); ky += 11;
        FontManager.DrawText("D down  A up   i inv  m spells", x, ky, 10, RetroSkin.DisabledText); ky += 11;
        FontManager.DrawText("1-9 quick-cast known spell", x, ky, 10, RetroSkin.DisabledText); ky += 11;
        FontManager.DrawText("step a doormat to enter shop", x, ky, 10, RetroSkin.DisabledText);
    }

    private void DrawLog(Vector2 panelOffset)
    {
        var m = MapRect(panelOffset);
        var logRect = new Rectangle(m.X, m.Y + m.Height + 6,
            m.Width + SidebarW + 8,
            PanelSize.Y - (m.Y + m.Height + 6 - panelOffset.Y) - RetroWidgets.StatusBarHeight - FrameInset - 4);
        if (logRect.Height < 12) return;
        RetroSkin.DrawSunken(logRect, new Color((byte)20, (byte)20, (byte)28, (byte)255));
        int shown = Math.Min(_log.Count, (int)(logRect.Height / 14));
        int start = Math.Max(0, _log.Count - shown - _scrollLog);
        for (int i = 0; i < shown; i++)
        {
            if (start + i >= _log.Count) break;
            string line = _log[start + i];
            int alpha = 130 + 125 * i / Math.Max(1, shown);
            FontManager.DrawText(line, (int)logRect.X + 8, (int)logRect.Y + 4 + i * 14, 12,
                new Color((byte)220, (byte)220, (byte)220, (byte)alpha));
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Overlays
    // ─────────────────────────────────────────────────────────────────────
    private Rectangle CenteredBox(Vector2 panelOffset, int w, int h)
        => new(panelOffset.X + (PanelSize.X - w) / 2, panelOffset.Y + (PanelSize.Y - h) / 2, w, h);

    private void DrawInventoryOverlay(Vector2 panelOffset)
    {
        var box = CenteredBox(panelOffset, 520, 460);
        RetroSkin.DrawRaised(box);
        int x = (int)box.X + 16, y = (int)box.Y + 16;
        FontManager.DrawText("Inventory", x, y, 18, RetroSkin.BodyText); y += 26;
        if (!string.IsNullOrEmpty(_prompt))
        { FontManager.DrawText(_prompt, x, y, 12, RetroSkin.TitleActive); y += 16; }
        var pack = _state.Player.Pack;
        if (pack.Count == 0)
            FontManager.DrawText("Your pack is empty.", x, y, 14, RetroSkin.DisabledText);
        int rowH = 14;
        int maxRows = Math.Min(pack.Count, (int)((box.Height - 80) / rowH));
        int start = Math.Max(0, Math.Min(_invCursor - maxRows / 2, pack.Count - maxRows));
        rowH = 22;  // make room for 20×20 item sprite per row
        maxRows = Math.Min(pack.Count, (int)((box.Height - 80) / rowH));
        start = Math.Max(0, Math.Min(_invCursor - maxRows / 2, pack.Count - maxRows));
        for (int i = 0; i < maxRows; i++)
        {
            int idx = start + i;
            if (idx >= pack.Count) break;
            var def = Def(pack[idx]);
            string prefix = idx == _invCursor ? "►" : " ";
            string slotMark = "";
            foreach (var kv in _state.Player.Equip)
                if (kv.Value == pack[idx]) { slotMark = $" ({kv.Key[..2]})"; break; }
            // Mini item sprite, drawn from items.png at 20×20.
            var (sx, sy) = ItemSpriteFor(pack[idx]);
            DrawSprite(_itemsTex, sx, sy, x + 12, y + i * rowH - 2, 20);
            string line = $"{prefix}  {DisplayName(pack[idx])}{slotMark}";
            string right = $"{def.Weight}lb";
            var col = idx == _invCursor ? RetroSkin.TitleActive : RetroSkin.BodyText;
            FontManager.DrawText(line, x + 36, y + i * rowH + 2, 13, col);
            FontManager.DrawText(right, x + 380, y + i * rowH + 2, 12, RetroSkin.DisabledText);
        }
        int by = (int)box.Y + (int)box.Height - 30;
        FontManager.DrawText("Enter use/equip   D drop   X examine   Esc close", x, by, 12, RetroSkin.DisabledText);
    }

    private void DrawSpellOverlay(Vector2 panelOffset)
    {
        var box = CenteredBox(panelOffset, 540, 460);
        RetroSkin.DrawRaised(box);
        int x = (int)box.X + 16, y = (int)box.Y + 16;
        FontManager.DrawText("Spellbook", x, y, 18, RetroSkin.BodyText); y += 26;
        var known = _state.Player.KnownSpells.OrderBy(i => i).ToList();
        if (known.Count == 0)
            FontManager.DrawText("You know no spells. Find a spellbook!", x, y, 13, RetroSkin.DisabledText);
        for (int i = 0; i < known.Count; i++)
        {
            var s = Spells[known[i]];
            string prefix = i == _spellCursor ? "►" : " ";
            string keyHint = i < 9 ? $"({i + 1})" : "";
            string line = $"{prefix} {s.Name,-18} {keyHint,-4}  {s.Cost} MP  [{s.School}]";
            var col = i == _spellCursor ? RetroSkin.TitleActive
                    : _state.Player.Mp < s.Cost ? RetroSkin.DisabledText
                    : RetroSkin.BodyText;
            FontManager.DrawText(line, x, y + i * 16, 13, col);
        }
        if (known.Count > 0 && _spellCursor < known.Count)
        {
            var s = Spells[known[_spellCursor]];
            FontManager.DrawText(s.Description, x, (int)box.Y + (int)box.Height - 56, 12, RetroSkin.DisabledText);
        }
        FontManager.DrawText("Enter cast   Esc close",
            x, (int)box.Y + (int)box.Height - 28, 12, RetroSkin.DisabledText);
    }

    private void DrawItemDetail(Vector2 panelOffset)
    {
        var box = CenteredBox(panelOffset, 480, 320);
        RetroSkin.DrawRaised(box);
        int x = (int)box.X + 16, y = (int)box.Y + 16;
        var def = Def(_selectedItemName);
        FontManager.DrawText(DisplayName(_selectedItemName), x, y, 18, RetroSkin.BodyText); y += 26;
        FontManager.DrawText($"Glyph {def.Glyph}   Weight {def.Weight}lb   Price {def.Price}g", x, y, 12, RetroSkin.DisabledText); y += 22;
        if (def.Slot.HasValue) { FontManager.DrawText($"Slot: {SlotNames[(int)def.Slot.Value]}", x, y, 13, RetroSkin.BodyText); y += 16; }
        if (def.Damage > 0) { FontManager.DrawText($"Damage: 1-{def.Damage}", x, y, 13, RetroSkin.BodyText); y += 16; }
        if (def.Defense > 0) { FontManager.DrawText($"Defense: +{def.Defense}", x, y, 13, RetroSkin.BodyText); y += 16; }
        if (def.TwoHand)   { FontManager.DrawText("Two-handed.", x, y, 13, RetroSkin.BodyText); y += 16; }
        if (def.Flags != 0) { FontManager.DrawText("Magical: " + def.Flags, x, y, 12, RetroSkin.BodyText); y += 16; }
        if (def.IsPotion) { FontManager.DrawText("Potion. Drink to consume.", x, y, 12, RetroSkin.BodyText); y += 16; }
        if (def.IsScroll) { FontManager.DrawText("Scroll. Read to consume.", x, y, 12, RetroSkin.BodyText); y += 16; }
        if (def.IsBook)   { FontManager.DrawText("Spellbook. Read to learn the spell.", x, y, 12, RetroSkin.BodyText); y += 16; }
        if (def.IsFood)   { FontManager.DrawText($"Food: {def.FoodNutrition} nutrition", x, y, 12, RetroSkin.BodyText); y += 16; }
        FontManager.DrawText("Enter / Esc close", x, (int)box.Y + (int)box.Height - 28, 12, RetroSkin.DisabledText);
    }

    private void DrawShopOverlay(Vector2 panelOffset)
    {
        var box = CenteredBox(panelOffset, 600, 480);
        RetroSkin.DrawRaised(box);
        int x = (int)box.X + 16, y = (int)box.Y + 16;
        FontManager.DrawText(_activeShop, x, y, 18, RetroSkin.BodyText);
        FontManager.DrawText($"Gold: {_state.Player.Gold}", x + 440, y, 14, RetroSkin.BodyText); y += 26;
        FontManager.DrawText(_shopBuying ? "[BUY]  Sell  (B/S to switch)" : "Buy  [SELL]  (B/S to switch)",
            x, y, 13, RetroSkin.TitleActive); y += 18;
        var list = _shopBuying ? _shopStock : _state.Player.Pack;
        int rowH = 14;
        int maxRows = Math.Min(list.Count, (int)((box.Height - 100) / rowH));
        int start = Math.Max(0, Math.Min(_shopCursor - maxRows / 2, list.Count - maxRows));
        for (int i = 0; i < maxRows; i++)
        {
            int idx = start + i;
            if (idx >= list.Count) break;
            var def = Def(list[idx]);
            string prefix = idx == _shopCursor ? "►" : " ";
            int price = _shopBuying ? def.Price : def.Price / 2;
            string line = $"{prefix} {def.Glyph} {DisplayName(list[idx]),-26} {price}g";
            var col = idx == _shopCursor ? RetroSkin.TitleActive : RetroSkin.BodyText;
            FontManager.DrawText(line, x, y + i * rowH, 13, col);
        }
        FontManager.DrawText("Enter confirm   Esc leave",
            x, (int)box.Y + (int)box.Height - 28, 12, RetroSkin.DisabledText);
    }

    private void DrawHealerOverlay(Vector2 panelOffset)
    {
        var box = CenteredBox(panelOffset, 460, 240);
        RetroSkin.DrawRaised(box);
        int x = (int)box.X + 16, y = (int)box.Y + 16;
        FontManager.DrawText("Healer", x, y, 18, RetroSkin.BodyText); y += 26;
        FontManager.DrawText($"Gold: {_state.Player.Gold}", x, y, 12, RetroSkin.DisabledText); y += 22;
        FontManager.DrawText(" 1.  Heal HP                40g", x, y, 14, RetroSkin.BodyText); y += 20;
        FontManager.DrawText(" 2.  Cure all status        25g", x, y, 14, RetroSkin.BodyText); y += 20;
        FontManager.DrawText(" 3.  Full restoration       80g", x, y, 14, RetroSkin.BodyText); y += 22;
        FontManager.DrawText("Esc to leave", x, (int)box.Y + (int)box.Height - 28, 12, RetroSkin.DisabledText);
    }

    private void DrawSageOverlay(Vector2 panelOffset)
    {
        var box = CenteredBox(panelOffset, 520, 420);
        RetroSkin.DrawRaised(box);
        int x = (int)box.X + 16, y = (int)box.Y + 16;
        FontManager.DrawText("Sage — Identify (30g each)", x, y, 18, RetroSkin.BodyText); y += 26;
        var pack = _state.Player.Pack;
        for (int i = 0; i < pack.Count; i++)
        {
            string prefix = i == _shopCursor ? "►" : " ";
            string mark = IsIdentified(pack[i]) ? "✓" : "?";
            string line = $"{prefix} {mark} {DisplayName(pack[i])}";
            var col = i == _shopCursor ? RetroSkin.TitleActive
                    : IsIdentified(pack[i]) ? RetroSkin.DisabledText
                    : RetroSkin.BodyText;
            FontManager.DrawText(line, x, y + i * 14, 13, col);
        }
        FontManager.DrawText("Enter identify   Esc leave",
            x, (int)box.Y + (int)box.Height - 28, 12, RetroSkin.DisabledText);
    }

    private void DrawBankOverlay(Vector2 panelOffset)
    {
        var box = CenteredBox(panelOffset, 440, 220);
        RetroSkin.DrawRaised(box);
        int x = (int)box.X + 16, y = (int)box.Y + 16;
        FontManager.DrawText("Bank of Sosaria", x, y, 18, RetroSkin.BodyText); y += 26;
        FontManager.DrawText($"On hand:  {_state.Player.Gold}g", x, y, 14, RetroSkin.BodyText); y += 18;
        FontManager.DrawText($"On deposit: {_state.Player.Bank}g", x, y, 14, RetroSkin.BodyText); y += 24;
        FontManager.DrawText("D deposit 10g   W withdraw 10g   (Shift = ×10)", x, y, 12, RetroSkin.BodyText); y += 18;
        FontManager.DrawText("Esc to leave", x, y + 8, 12, RetroSkin.DisabledText);
    }

    private void DrawTrainerOverlay(Vector2 panelOffset)
    {
        var box = CenteredBox(panelOffset, 480, 260);
        RetroSkin.DrawRaised(box);
        int x = (int)box.X + 16, y = (int)box.Y + 16;
        int cost = 200 + _state.Player.Level * 60;
        FontManager.DrawText("Trainer", x, y, 18, RetroSkin.BodyText); y += 26;
        FontManager.DrawText($"Cost: {cost} XP + 200g per stat", x, y, 12, RetroSkin.DisabledText); y += 22;
        FontManager.DrawText($" 1. +1 Strength   ({_state.Player.Str})", x, y, 14, RetroSkin.BodyText); y += 18;
        FontManager.DrawText($" 2. +1 Intellect  ({_state.Player.Int})", x, y, 14, RetroSkin.BodyText); y += 18;
        FontManager.DrawText($" 3. +1 Dexterity  ({_state.Player.Dex})", x, y, 14, RetroSkin.BodyText); y += 18;
        FontManager.DrawText($" 4. +1 Constitution ({_state.Player.Con})", x, y, 14, RetroSkin.BodyText); y += 22;
        FontManager.DrawText("Esc to leave", x, y, 12, RetroSkin.DisabledText);
    }

    private void DrawHouseOverlay(Vector2 panelOffset)
    {
        var box = CenteredBox(panelOffset, 600, 460);
        RetroSkin.DrawRaised(box);
        int x = (int)box.X + 16, y = (int)box.Y + 16;
        FontManager.DrawText("Player's House — Stash", x, y, 18, RetroSkin.BodyText); y += 26;
        FontManager.DrawText(_stashing ? "[Pack]  → Stash  (Tab to switch)" : "Pack ← [Stash]  (Tab to switch)",
            x, y, 13, RetroSkin.TitleActive); y += 20;
        var src = _stashing ? _state.Player.Pack : _state.Player.Stash;
        for (int i = 0; i < src.Count; i++)
        {
            string prefix = i == _stashCursor ? "►" : " ";
            string line = $"{prefix} {Def(src[i]).Glyph} {DisplayName(src[i])}";
            var col = i == _stashCursor ? RetroSkin.TitleActive : RetroSkin.BodyText;
            FontManager.DrawText(line, x, y + i * 14, 13, col);
        }
        FontManager.DrawText("Enter move   Esc leave",
            x, (int)box.Y + (int)box.Height - 28, 12, RetroSkin.DisabledText);
    }

    private void DrawEndBanner(Vector2 panelOffset)
    {
        EnsureTextures();
        if (_mode == Mode.Dead)
        {
            // Use cotwelm's RIP_blank.png tombstone, scaled to fit.
            int tw = _ripTex.Width, th = _ripTex.Height;
            int dw = Math.Min(tw, (int)PanelSize.X - 80);
            int dh = th * dw / tw;
            int dx = (int)panelOffset.X + ((int)PanelSize.X - dw) / 2;
            int dy = (int)panelOffset.Y + ((int)PanelSize.Y - dh) / 2 - 10;
            Raylib.DrawTexturePro(_ripTex,
                new Rectangle(0, 0, tw, th),
                new Rectangle(dx, dy, dw, dh), Vector2.Zero, 0, Color.White);
            // Engrave name + epitaph on the stone.
            string name = _state.Player.Name;
            int nw = FontManager.MeasureText(name, 18);
            FontManager.DrawText(name, dx + (dw - nw) / 2, dy + dh / 2 - 30, 18, Color.Black);
            string sub = $"Slain on {DungeonName(_state.CurrentFloor)}";
            int sw = FontManager.MeasureText(sub, 12);
            FontManager.DrawText(sub, dx + (dw - sw) / 2, dy + dh / 2, 12, Color.Black);
            string hint = "Press Enter to close.";
            int hw = FontManager.MeasureText(hint, 12);
            FontManager.DrawText(hint, (int)panelOffset.X + ((int)PanelSize.X - hw) / 2,
                dy + dh + 6, 12, RetroSkin.DisabledText);
            return;
        }
        // Victory: gold banner over the play area.
        var box = CenteredBox(panelOffset, 540, 200);
        RetroSkin.DrawRaised(box);
        string msg = "VICTORY";
        var col = new Color((byte)220, (byte)180, (byte)60, (byte)255);
        int w2 = FontManager.MeasureText(msg, 32);
        FontManager.DrawText(msg, (int)(box.X + (box.Width - w2) / 2), (int)box.Y + 28, 32, col);
        string sub2 = "Surtur is slain. Olaf and Greta are avenged.";
        int sw2 = FontManager.MeasureText(sub2, 14);
        FontManager.DrawText(sub2, (int)(box.X + (box.Width - sw2) / 2), (int)box.Y + 80, 14, RetroSkin.BodyText);
        const string hint2 = "Press Enter or Space to close.";
        int hw2 = FontManager.MeasureText(hint2, 12);
        FontManager.DrawText(hint2, (int)(box.X + (box.Width - hw2) / 2), (int)box.Y + 140, 12, RetroSkin.DisabledText);
    }
}
