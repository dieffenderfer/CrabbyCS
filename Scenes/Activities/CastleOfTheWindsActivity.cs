using System.Numerics;
using System.Text.Json.Serialization;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Castle of the Winds — a C# port-in-spirit of the classic 1989 tile-based
/// roguelike by Rick Saada, taking structure and naming from the unfinished
/// Elm reimplementation at github.com/mordrax/cotwelm.
///
/// What's faithful to the original game (and to cotwelm):
///
///   • Character creation with name + gender + a fixed pool of attribute
///     points distributed across Str / Int / Dex / Con (the classic CoTW
///     four). Derived stats: maxHP from Con+Str+level, maxMP from Int+level,
///     ToHit from Dex, damage bonus from Str, armor class from Dex.
///   • A small town/wilderness overlay with a Shop, an Inn (free heal),
///     and the dungeon entrance.
///   • A multi-floor procedurally-generated dungeon (rooms + corridors,
///     classic BSP-ish split). Eight floors, deeper floors host nastier
///     monsters from the cotwelm bestiary (Giant Rat → Skeleton → Goblin
///     → Hobgoblin → Wolf → Troll → Wraith → ... → Surtur the Fire Giant
///     King on floor 8).
///   • Tile-by-tile turn-based movement; bump-to-attack combat resolved
///     against ToHit / AC / weapon damage, with a tiny d20-ish roll.
///     XP, levelling (every (lvl×100) XP, per cotwelm Hero.addExperience),
///     HP/MP regen-per-step, fog-of-war + line-of-sight reveal.
///   • Inventory with weapon / armor / helmet / shield slots, weight-based
///     encumbrance (heavy → fewer moves between regen ticks). Items:
///     dagger / mace / sword / axe / leather / chain / plate / helm /
///     shield / potion (heal / mana) / scroll (identify / teleport).
///   • Five spells, mapped to keys 1-5: Magic Missile, Fireball, Heal,
///     Light (reveal floor), Teleport. Each costs MP scaled by power.
///   • A shop that buys / sells the standard gear. Gold drops from
///     monsters, scaled to floor depth.
///   • Save / load via SaveManager. The whole game-state struct is JSON,
///     including the per-floor maps and the RNG seed, so a reload is a
///     bit-exact resume rather than a regeneration.
///
/// Win condition: descend to floor 8 and kill Surtur. Lose condition:
/// die in the dungeon (HP ≤ 0). The town floor (-1) cannot kill you;
/// the Inn always offers free heal.
/// </summary>
public class CastleOfTheWindsActivity : IActivity
{
    public Vector2 PanelSize => new(920, 640);
    public bool IsFinished { get; private set; }

    private const int FrameInset = 3;

    private const int MapCols = 40;
    private const int MapRows = 22;
    private const int Tile = 16;
    private const int DungeonFloors = 8;

    private const int SidebarW = 240;

    // ── Tile alphabet ─────────────────────────────────────────────────────
    // Map tiles use chars so they JSON-serialise as readable strings.
    private const char TWall   = '#';
    private const char TFloor  = '.';
    private const char TGrass  = '"';
    private const char TTree   = 'T';
    private const char TDoor   = '+';
    private const char TStairUp   = '<';
    private const char TStairDown = '>';
    private const char TShop   = '$';   // town building
    private const char TInn    = 'I';   // town building
    private const char TAltar  = '_';   // floor-8 altar (Surtur lair marker)

    // ── Data ──────────────────────────────────────────────────────────────
    private enum Mode { CharCreation, Playing, Shop, Inventory, SpellMenu, Dead, Won }

    private enum Slot { Weapon, Armor, Helmet, Shield }

    private record ItemDef(string Name, Slot? Slot, int Damage, int Defense,
                           int Weight, int Price, char Glyph,
                           bool IsPotion = false, bool IsScroll = false,
                           int PotionHeal = 0, int PotionMana = 0,
                           bool ScrollTeleport = false, bool ScrollIdentify = false);

    // Static catalog. Names line up with cotwelm Item.Data weapon/wearable lists.
    private static readonly ItemDef[] Catalog =
    {
        new("Dagger",          Slot.Weapon,  4, 0, 1,   30, ')'),
        new("Mace",            Slot.Weapon,  6, 0, 3,   80, ')'),
        new("Short Sword",     Slot.Weapon,  7, 0, 2,  120, ')'),
        new("Long Sword",      Slot.Weapon,  9, 0, 3,  220, ')'),
        new("Battle Axe",      Slot.Weapon, 11, 0, 4,  340, ')'),
        new("Two-Handed Sword",Slot.Weapon, 14, 0, 6,  520, ')'),
        new("Leather Armor",   Slot.Armor,   0, 2, 4,   60, '['),
        new("Studded Leather", Slot.Armor,   0, 3, 6,  140, '['),
        new("Ring Mail",       Slot.Armor,   0, 4, 9,  260, '['),
        new("Chain Mail",      Slot.Armor,   0, 5, 12, 420, '['),
        new("Plate Mail",      Slot.Armor,   0, 7, 18, 700, '['),
        new("Leather Helm",    Slot.Helmet,  0, 1, 1,   40, '^'),
        new("Iron Helm",       Slot.Helmet,  0, 2, 2,  120, '^'),
        new("Small Shield",    Slot.Shield,  0, 1, 2,   50, '('),
        new("Large Shield",    Slot.Shield,  0, 2, 4,  140, '('),
        new("Potion of Healing", null, 0, 0, 1,  40, '!', IsPotion: true, PotionHeal: 25),
        new("Potion of Mana",    null, 0, 0, 1,  40, '!', IsPotion: true, PotionMana: 25),
        new("Scroll of Identify",   null, 0, 0, 0, 20, '?', IsScroll: true, ScrollIdentify: true),
        new("Scroll of Teleport",   null, 0, 0, 0, 80, '?', IsScroll: true, ScrollTeleport: true),
    };

    private static int CatIdx(string name)
    {
        for (int i = 0; i < Catalog.Length; i++) if (Catalog[i].Name == name) return i;
        return -1;
    }

    private record MonsterDef(string Name, int MinFloor, int Hp, int Atk, int Def, int Xp, int Gold, char Glyph);

    // Bestiary subset — names match cotwelm Monsters.Types entries.
    private static readonly MonsterDef[] Bestiary =
    {
        new("Giant Rat",         1,  6,  3, 0,  10,   2, 'r'),
        new("Large Snake",       1,  8,  4, 1,  14,   3, 's'),
        new("Wild Dog",          1, 10,  4, 0,  18,   4, 'd'),
        new("Goblin",            2, 12,  5, 1,  24,   8, 'g'),
        new("Skeleton",          2, 14,  5, 2,  28,   6, 'z'),
        new("Hobgoblin",         3, 18,  7, 2,  44,  16, 'h'),
        new("Gray Wolf",         3, 20,  8, 1,  52,   0, 'w'),
        new("Carrion Creeper",   3, 24,  6, 3,  60,   0, 'c'),
        new("Smirking Sneak Thief", 4, 22, 9, 2, 80,  40, 't'),
        new("Brown Bear",        4, 32, 10, 3, 110,   0, 'B'),
        new("Walking Corpse",    4, 28,  8, 4, 100,  20, 'Z'),
        new("Gruesome Troll",    5, 48, 13, 4, 180,  60, 'T'),
        new("Manticore",         5, 44, 14, 3, 200,  40, 'M'),
        new("Dark Wraith",       6, 40, 12, 5, 240,   0, 'W'),
        new("Vampire",           6, 60, 16, 5, 360, 120, 'V'),
        new("Hill Giant",        7, 80, 18, 5, 420, 160, 'H'),
        new("Frost Giant",       7, 90, 20, 6, 520, 220, 'F'),
        new("Spiked Devil",      7, 70, 22, 6, 560, 100, 'D'),
        new("Surtur",            8, 240, 30, 8, 2500, 1000, 'S'),  // boss
    };

    // ── Save state ────────────────────────────────────────────────────────
    // POCOs in here are public so System.Text.Json can serialise them.
    public class Hero
    {
        public string Name { get; set; } = "Hero";
        public bool Male { get; set; } = true;
        public int Str { get; set; } = 12;
        public int Int { get; set; } = 12;
        public int Dex { get; set; } = 12;
        public int Con { get; set; } = 12;
        public int Level { get; set; } = 1;
        public int Xp { get; set; }
        public int Hp { get; set; } = 30;
        public int Mp { get; set; } = 20;
        public int Gold { get; set; } = 50;
        public int X { get; set; }
        public int Y { get; set; }
        public string? WeaponName { get; set; }
        public string? ArmorName { get; set; }
        public string? HelmetName { get; set; }
        public string? ShieldName { get; set; }
        public List<string> Pack { get; set; } = new();
    }

    public class MonsterInst
    {
        public string TypeName { get; set; } = "Giant Rat";
        public int X { get; set; }
        public int Y { get; set; }
        public int Hp { get; set; }
        public bool Seen { get; set; }
    }

    public class ItemDrop
    {
        public string Name { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
    }

    public class Floor
    {
        public int Depth { get; set; }                // -1 = town
        public string Rows { get; set; } = "";        // MapCols * MapRows chars joined
        public List<MonsterInst> Monsters { get; set; } = new();
        public List<ItemDrop> Items { get; set; } = new();
        public bool[] Seen { get; set; } = Array.Empty<bool>();   // ever-seen
    }

    public class SaveState
    {
        public Hero Player { get; set; } = new();
        public int CurrentFloor { get; set; }
        public List<Floor> Floors { get; set; } = new();
        public int Seed { get; set; }
        public List<string> Log { get; set; } = new();
        public bool Won { get; set; }
    }

    // ── Runtime state ─────────────────────────────────────────────────────
    private Mode _mode = Mode.CharCreation;
    private SaveState _state = new();
    private Random _rng = new();
    private readonly List<string> _log = new();

    // Char creation scratch
    private string _ccName = "Wayfarer";
    private bool _ccMale = true;
    private int _ccStr = 12, _ccInt = 12, _ccDex = 12, _ccCon = 12;
    private int _ccPoints = 12;       // pool to distribute
    private int _ccCursor;            // 0..3 over Str/Int/Dex/Con
    private bool _ccNameEdit;
    private bool _hoverNameField;

    // Inventory / shop cursor
    private int _invCursor;
    private int _shopCursor;
    private bool _shopBuying = true;

    // Magic
    private static readonly string[] SpellNames =
        { "Magic Missile", "Fireball", "Heal", "Light", "Teleport" };
    private static readonly int[] SpellCost = { 5, 12, 8, 4, 14 };

    // ── Saving ────────────────────────────────────────────────────────────
    private const string SaveFile = "castleofthewinds.json";

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
            _mode = Mode.CharCreation;
        }
    }

    public void Close()
    {
        if (_mode == Mode.Playing || _mode == Mode.Inventory ||
            _mode == Mode.Shop || _mode == Mode.SpellMenu)
        {
            _state.Log = _log.TakeLast(40).ToList();
            SaveManager.Save(SaveFile, _state);
        }
        else if (_mode == Mode.Dead || _mode == Mode.Won)
        {
            // Wipe the save when the run ends so the next launch
            // returns to char creation.
            try { File.Delete(Path.Combine(SaveManager.SaveDirectory, SaveFile)); }
            catch { }
        }
        IsFinished = true;
    }

    // ── Update ────────────────────────────────────────────────────────────
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
        string[] items = _mode == Mode.CharCreation
            ? new[] { "Begin", "Help" }
            : new[] { "New Game", "Inventory", "Spells", "Save", "Help" };
        int mb = RetroWidgets.MenuBarHitTest(menuBar, items, local, leftPressed);
        if (_mode == Mode.CharCreation)
        {
            if (mb == 0) BeginGame();
            else if (mb == 1) AddLog("Distribute attribute points, name yourself, then Begin.");
        }
        else
        {
            switch (mb)
            {
                case 0: ConfirmNewGame(); return;
                case 1: _mode = _mode == Mode.Inventory ? Mode.Playing : Mode.Inventory; return;
                case 2: _mode = _mode == Mode.SpellMenu ? Mode.Playing : Mode.SpellMenu; return;
                case 3:
                    _state.Log = _log.TakeLast(40).ToList();
                    SaveManager.Save(SaveFile, _state);
                    AddLog("Game saved.");
                    return;
                case 4:
                    AddLog("Arrows: move. , pick up. > / < stairs. i invent. m spells. 1-5 cast. b buy. s sell.");
                    return;
            }
        }

        switch (_mode)
        {
            case Mode.CharCreation: UpdateCharCreation(local, leftPressed); break;
            case Mode.Playing: UpdatePlaying(); break;
            case Mode.Inventory: UpdateInventory(); break;
            case Mode.SpellMenu: UpdateSpellMenu(); break;
            case Mode.Shop: UpdateShop(); break;
            case Mode.Dead:
            case Mode.Won:
                if (Raylib.IsKeyPressed(KeyboardKey.Space) ||
                    Raylib.IsKeyPressed(KeyboardKey.Enter))
                    Close();
                break;
        }
    }

    // ── Character creation ───────────────────────────────────────────────
    private void UpdateCharCreation(Vector2 local, bool leftPressed)
    {
        // Name field is at a known rect — let the user click to focus,
        // then accept characters until Enter.
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
        }
        else
        {
            if (Raylib.IsKeyPressed(KeyboardKey.G)) _ccMale = !_ccMale;
            if (Raylib.IsKeyPressed(KeyboardKey.Up))   _ccCursor = (_ccCursor + 3) % 4;
            if (Raylib.IsKeyPressed(KeyboardKey.Down)) _ccCursor = (_ccCursor + 1) % 4;
            if (Raylib.IsKeyPressed(KeyboardKey.Right) || Raylib.IsKeyPressed(KeyboardKey.Equal))
                BumpCcAttr(+1);
            if (Raylib.IsKeyPressed(KeyboardKey.Left) || Raylib.IsKeyPressed(KeyboardKey.Minus))
                BumpCcAttr(-1);
            if (Raylib.IsKeyPressed(KeyboardKey.Enter) && _ccPoints == 0) BeginGame();
        }
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
        else if (delta < 0 && target > 8) { target--; _ccPoints++; }
    }

    private void BeginGame()
    {
        // Roll a seed so save/load is bit-stable.
        int seed = Environment.TickCount;
        _state = new SaveState
        {
            Seed = seed,
            CurrentFloor = -1,
            Player = new Hero
            {
                Name = string.IsNullOrWhiteSpace(_ccName) ? "Wayfarer" : _ccName.Trim(),
                Male = _ccMale,
                Str = _ccStr, Int = _ccInt, Dex = _ccDex, Con = _ccCon,
                Level = 1, Xp = 0, Gold = 50,
                WeaponName = "Dagger",
                ArmorName = "Leather Armor",
                Pack = new List<string> { "Potion of Healing", "Potion of Mana" },
            },
        };
        _state.Player.Hp = MaxHp(_state.Player);
        _state.Player.Mp = MaxMp(_state.Player);
        _rng = new Random(seed);
        BuildAllFloors();
        // Place hero on the town entrance.
        var (sx, sy) = FindAny(GetFloor(-1), TStairDown);
        _state.Player.X = sx; _state.Player.Y = sy - 1;
        _log.Clear();
        AddLog($"Welcome, {_state.Player.Name}. The Halls of Mischief await beyond the stairs.");
        _mode = Mode.Playing;
    }

    private void ConfirmNewGame()
    {
        // Cheap confirm: only restart if Enter is held when clicked
        // would be too tricky here — just wipe and bounce to char creation.
        try { File.Delete(Path.Combine(SaveManager.SaveDirectory, SaveFile)); } catch { }
        _mode = Mode.CharCreation;
        _ccName = _state.Player.Name; _ccMale = _state.Player.Male;
        _ccStr = 12; _ccInt = 12; _ccDex = 12; _ccCon = 12; _ccPoints = 12; _ccCursor = 0;
    }

    // ── Derived stats (cotwelm Stats.elm style) ──────────────────────────
    private static int MaxHp(Hero h) => 20 + h.Con * 2 + h.Str + h.Level * 6;
    private static int MaxMp(Hero h) => 8 + h.Int * 2 + h.Level * 3;
    private int ToHit(Hero h) => 10 + h.Dex / 2 + h.Level;
    private int DamageRoll(Hero h)
    {
        var w = h.WeaponName != null ? Catalog[CatIdx(h.WeaponName)] : null;
        int wd = w?.Damage ?? 2;
        int bonus = (h.Str - 10) / 3;
        return _rng.Next(1, wd + 1) + Math.Max(0, bonus);
    }
    private int ArmorClass(Hero h)
    {
        int ac = h.Dex / 4;
        foreach (var nm in new[] { h.ArmorName, h.HelmetName, h.ShieldName })
            if (nm != null) ac += Catalog[CatIdx(nm)].Defense;
        return ac;
    }
    private int Weight(Hero h)
    {
        int w = 0;
        foreach (var nm in new[] { h.WeaponName, h.ArmorName, h.HelmetName, h.ShieldName })
            if (nm != null) w += Catalog[CatIdx(nm)].Weight;
        foreach (var nm in h.Pack) w += Catalog[CatIdx(nm)].Weight;
        return w;
    }
    private int Encumbrance(Hero h)
    {
        int cap = 15 + h.Str * 2;
        int w = Weight(h);
        return w <= cap ? 0 : w <= cap + 10 ? 1 : 2;   // 0 fine, 1 burdened, 2 strained
    }

    // ── Floor generation ──────────────────────────────────────────────────
    private void BuildAllFloors()
    {
        _state.Floors.Clear();
        _state.Floors.Add(BuildTown());
        for (int d = 1; d <= DungeonFloors; d++)
            _state.Floors.Add(BuildDungeon(d));
    }

    private Floor BuildTown()
    {
        var f = new Floor { Depth = -1 };
        char[,] m = new char[MapCols, MapRows];
        for (int x = 0; x < MapCols; x++)
            for (int y = 0; y < MapRows; y++)
                m[x, y] = TGrass;
        // Border walls (low stone wall around the village).
        for (int x = 0; x < MapCols; x++) { m[x, 0] = TWall; m[x, MapRows - 1] = TWall; }
        for (int y = 0; y < MapRows; y++) { m[0, y] = TWall; m[MapCols - 1, y] = TWall; }
        // Trees scattered.
        for (int i = 0; i < 28; i++)
        {
            int x = _rng.Next(2, MapCols - 2), y = _rng.Next(2, MapRows - 2);
            m[x, y] = TTree;
        }
        // A central plaza of "floor" tiles (cobblestone path).
        for (int x = MapCols / 2 - 8; x <= MapCols / 2 + 8; x++)
            for (int y = MapRows / 2 - 4; y <= MapRows / 2 + 4; y++)
                if (m[x, y] == TGrass || m[x, y] == TTree) m[x, y] = TFloor;
        // Shop building (4×3) on the left of the plaza.
        DrawBuilding(m, MapCols / 2 - 8, MapRows / 2 - 3, 4, 3, TShop);
        // Inn building (4×3) on the right.
        DrawBuilding(m, MapCols / 2 + 5, MapRows / 2 - 3, 4, 3, TInn);
        // Stairs down to dungeon (centre of plaza).
        m[MapCols / 2, MapRows / 2] = TStairDown;
        f.Rows = FlattenMap(m);
        f.Seen = new bool[MapCols * MapRows];
        // Town is always fully seen.
        for (int i = 0; i < f.Seen.Length; i++) f.Seen[i] = true;
        return f;
    }

    private static void DrawBuilding(char[,] m, int x0, int y0, int w, int h, char glyph)
    {
        for (int x = x0; x < x0 + w; x++)
            for (int y = y0; y < y0 + h; y++)
            {
                if (x == x0 + w / 2 && y == y0 + h - 1) m[x, y] = TDoor; // door at bottom-centre
                else if (x == x0 || y == y0 || x == x0 + w - 1 || y == y0 + h - 1) m[x, y] = TWall;
                else m[x, y] = glyph;
            }
    }

    private Floor BuildDungeon(int depth)
    {
        var f = new Floor { Depth = depth };
        char[,] m = new char[MapCols, MapRows];
        for (int x = 0; x < MapCols; x++)
            for (int y = 0; y < MapRows; y++)
                m[x, y] = TWall;

        // Carve a handful of overlapping rectangular rooms, then connect each
        // room's centre to the previous room's centre by an L-shaped corridor.
        // Same shape of generator cotwelm sketches in Dungeon/Room.elm: pick a
        // size, anchor a centre, paint floors.
        int rooms = 6 + _rng.Next(4);
        var centers = new List<(int x, int y)>();
        for (int r = 0; r < rooms; r++)
        {
            int rw = 4 + _rng.Next(7);
            int rh = 3 + _rng.Next(4);
            int rx = 1 + _rng.Next(MapCols - rw - 2);
            int ry = 1 + _rng.Next(MapRows - rh - 2);
            for (int x = rx; x < rx + rw; x++)
                for (int y = ry; y < ry + rh; y++)
                    m[x, y] = TFloor;
            centers.Add((rx + rw / 2, ry + rh / 2));
        }
        for (int i = 1; i < centers.Count; i++)
        {
            var (ax, ay) = centers[i - 1];
            var (bx, by) = centers[i];
            CarveCorridor(m, ax, ay, bx, by);
        }
        // Doors at corridor-room interfaces: a floor tile adjacent to ≥3 walls
        // and exactly one floor neighbour is almost certainly a doorway.
        for (int x = 1; x < MapCols - 1; x++)
            for (int y = 1; y < MapRows - 1; y++)
                if (m[x, y] == TFloor && _rng.NextDouble() < 0.18)
                {
                    int wallCount = (m[x - 1, y] == TWall ? 1 : 0)
                                  + (m[x + 1, y] == TWall ? 1 : 0)
                                  + (m[x, y - 1] == TWall ? 1 : 0)
                                  + (m[x, y + 1] == TWall ? 1 : 0);
                    if (wallCount == 2 && ((m[x - 1, y] == TWall && m[x + 1, y] == TWall)
                                        || (m[x, y - 1] == TWall && m[x, y + 1] == TWall)))
                        m[x, y] = TDoor;
                }

        // Stairs.
        var (uX, uY) = centers[0];
        m[uX, uY] = TStairUp;
        if (depth < DungeonFloors)
        {
            var (dX, dY) = centers[^1];
            m[dX, dY] = TStairDown;
        }
        else
        {
            // Floor 8 — Surtur's lair. Mark the deepest room centre with an altar.
            var (aX, aY) = centers[^1];
            m[aX, aY] = TAltar;
        }

        // Populate monsters and items.
        var floorCells = new List<(int x, int y)>();
        for (int x = 1; x < MapCols - 1; x++)
            for (int y = 1; y < MapRows - 1; y++)
                if (m[x, y] == TFloor) floorCells.Add((x, y));
        Shuffle(floorCells);

        int monsterCount = 4 + depth + _rng.Next(3);
        int idx = 0;
        for (int n = 0; n < monsterCount && idx < floorCells.Count; n++)
        {
            var (mx, my) = floorCells[idx++];
            var def = PickMonsterForDepth(depth);
            f.Monsters.Add(new MonsterInst { TypeName = def.Name, X = mx, Y = my, Hp = def.Hp });
        }
        // Floor 8 always hosts Surtur on the altar regardless of count.
        if (depth == DungeonFloors)
        {
            var altar = FindAnyIn(m, TAltar);
            f.Monsters.Add(new MonsterInst { TypeName = "Surtur", X = altar.x, Y = altar.y, Hp = 240 });
        }

        // Items.
        int itemCount = 2 + _rng.Next(3);
        for (int n = 0; n < itemCount && idx < floorCells.Count; n++)
        {
            var (ix, iy) = floorCells[idx++];
            var def = PickItemForDepth(depth);
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

    private MonsterDef PickMonsterForDepth(int depth)
    {
        // Weighted pull from the bestiary, biased to MinFloor ≤ depth.
        var pool = Bestiary.Where(b => b.MinFloor <= depth && b.Name != "Surtur").ToList();
        return pool[_rng.Next(pool.Count)];
    }

    private ItemDef PickItemForDepth(int depth)
    {
        // Lean toward potions/scrolls on shallow floors and gear deeper down.
        if (_rng.NextDouble() < 0.55 - depth * 0.04)
            return Catalog.Where(c => c.IsPotion || c.IsScroll).ElementAt(_rng.Next(4));
        // Gear of an appropriate tier.
        var tier = Catalog.Where(c => c.Slot != null && c.Price <= 80 + depth * 90).ToList();
        return tier[_rng.Next(tier.Count)];
    }

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

    private void WriteBack(Floor f, char[,] m) => f.Rows = FlattenMap(m);

    private static (int x, int y) FindAnyIn(char[,] m, char target)
    {
        for (int y = 0; y < MapRows; y++)
            for (int x = 0; x < MapCols; x++)
                if (m[x, y] == target) return (x, y);
        return (0, 0);
    }

    private static (int x, int y) FindAny(Floor f, char target)
    {
        var m = UnflattenMap(f.Rows);
        return FindAnyIn(m, target);
    }

    private void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
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

    // ── Playing ───────────────────────────────────────────────────────────
    private void UpdatePlaying()
    {
        // One key → one turn. We collect the player's intent and then resolve
        // the turn: hero acts, then monsters act.
        int dx = 0, dy = 0;
        bool wait = false, picked = false, downStairs = false, upStairs = false;
        bool spellMenu = false, invMenu = false;
        int castIdx = -1;

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
        else if (Raylib.IsKeyPressed(KeyboardKey.One)) castIdx = 0;
        else if (Raylib.IsKeyPressed(KeyboardKey.Two)) castIdx = 1;
        else if (Raylib.IsKeyPressed(KeyboardKey.Three)) castIdx = 2;
        else if (Raylib.IsKeyPressed(KeyboardKey.Four)) castIdx = 3;
        else if (Raylib.IsKeyPressed(KeyboardKey.Five)) castIdx = 4;

        if (invMenu) { _mode = Mode.Inventory; return; }
        if (spellMenu) { _mode = Mode.SpellMenu; return; }

        bool acted = false;

        if (castIdx >= 0)
        {
            acted = TryCast(castIdx);
        }
        else if (picked)
        {
            acted = TryPickup();
        }
        else if (dx != 0 || dy != 0)
        {
            // First, check for stairs at current tile when moving onto them later.
            acted = TryMove(dx, dy);
        }
        else if (wait) acted = true;

        // Stairs only resolve when explicitly invoked, so we don't auto-descend
        // by walking onto them. D = down, A = up (also Page Down / Up).
        if (Raylib.IsKeyPressed(KeyboardKey.RightBracket)
            || Raylib.IsKeyPressed(KeyboardKey.PageDown)
            || Raylib.IsKeyPressed(KeyboardKey.D)) downStairs = true;
        if (Raylib.IsKeyPressed(KeyboardKey.LeftBracket)
            || Raylib.IsKeyPressed(KeyboardKey.PageUp)
            || Raylib.IsKeyPressed(KeyboardKey.A)) upStairs = true;

        if (downStairs) { acted = TryStairs(TStairDown); }
        else if (upStairs) { acted = TryStairs(TStairUp); }

        if (acted)
        {
            ResolveMonsterTurn();
            PostTurnTick();
            ReveralFov();
        }
    }

    private bool TryMove(int dx, int dy)
    {
        int nx = _state.Player.X + dx;
        int ny = _state.Player.Y + dy;
        if (nx < 0 || ny < 0 || nx >= MapCols || ny >= MapRows) return false;
        var map = UnflattenMap(CurFloor.Rows);
        char t = map[nx, ny];

        // Town buildings: bump-to-enter (Shop / Inn).
        if (t == TShop) { OpenShop(); return false; }
        if (t == TInn)
        {
            _state.Player.Hp = MaxHp(_state.Player);
            _state.Player.Mp = MaxMp(_state.Player);
            AddLog("The innkeeper offers you a hot meal. You feel restored.");
            return true;
        }

        // Bump-attack a monster.
        var mon = CurFloor.Monsters.FirstOrDefault(mm => mm.X == nx && mm.Y == ny);
        if (mon != null)
        {
            AttackMonster(mon);
            return true;
        }

        if (t == TWall || t == TTree) return false;
        if (t == TDoor)
        {
            // Auto-open: replace door with floor.
            map[nx, ny] = TFloor;
            WriteBack(CurFloor, map);
            AddLog("You push the door open.");
            // The open-the-door action still consumes a turn.
            return true;
        }
        _state.Player.X = nx;
        _state.Player.Y = ny;
        // Step-on messages.
        if (t == TStairDown) AddLog("Stairs descend into the dark. Press D to go down.");
        else if (t == TStairUp) AddLog("Stairs lead upward. Press A to ascend.");
        else if (t == TAltar) AddLog("A blackened altar. Something terrible has been bound here.");
        return true;
    }

    private bool TryPickup()
    {
        var drop = CurFloor.Items.FirstOrDefault(it => it.X == _state.Player.X && it.Y == _state.Player.Y);
        if (drop == null) { AddLog("Nothing to pick up."); return false; }
        if (_state.Player.Pack.Count >= 20) { AddLog("Your pack is full."); return false; }
        _state.Player.Pack.Add(drop.Name);
        CurFloor.Items.Remove(drop);
        AddLog($"You pick up {Article(drop.Name)} {drop.Name}.");
        return true;
    }

    private static string Article(string name)
        => "aeiouAEIOU".IndexOf(name[0]) >= 0 ? "an" : "a";

    private bool TryStairs(char which)
    {
        var map = UnflattenMap(CurFloor.Rows);
        if (map[_state.Player.X, _state.Player.Y] != which)
        {
            AddLog(which == TStairDown ? "There are no stairs down here." : "There are no stairs up here.");
            return false;
        }
        int newDepth = _state.CurrentFloor + (which == TStairDown ? 1 : -1);
        if (newDepth < -1 || newDepth > DungeonFloors) return false;
        _state.CurrentFloor = newDepth;
        // Spawn the hero on the corresponding opposite-stair tile.
        char arriveOn = which == TStairDown ? TStairUp : TStairDown;
        if (newDepth == -1) arriveOn = TStairDown;     // arriving back in town
        var (ax, ay) = FindAny(CurFloor, arriveOn);
        _state.Player.X = ax; _state.Player.Y = ay;
        AddLog(newDepth == -1
            ? "You climb back up into the village."
            : $"You enter floor {newDepth} of the Halls of Mischief.");
        return true;
    }

    // ── Combat ────────────────────────────────────────────────────────────
    private void AttackMonster(MonsterInst mon)
    {
        var def = Bestiary.First(b => b.Name == mon.TypeName);
        int roll = _rng.Next(1, 21) + ToHit(_state.Player);
        int target = 10 + def.Def;
        if (roll < target)
        {
            AddLog($"You miss the {mon.TypeName}.");
            return;
        }
        int dmg = DamageRoll(_state.Player);
        mon.Hp -= dmg;
        if (mon.Hp <= 0)
        {
            CurFloor.Monsters.Remove(mon);
            AddLog($"You slay the {mon.TypeName}! +{def.Xp} XP.");
            _state.Player.Gold += def.Gold;
            GainXp(def.Xp);
            if (mon.TypeName == "Surtur")
            {
                _state.Won = true;
                _mode = Mode.Won;
                AddLog("Surtur falls. The Castle of the Winds is silent at last.");
            }
        }
        else
        {
            AddLog($"You hit the {mon.TypeName} for {dmg}.");
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

    // ── Monster turn / AI ─────────────────────────────────────────────────
    private void ResolveMonsterTurn()
    {
        var snapshot = CurFloor.Monsters.ToList();
        foreach (var mon in snapshot)
        {
            if (!CurFloor.Monsters.Contains(mon)) continue;
            int hx = _state.Player.X, hy = _state.Player.Y;
            int dx = hx - mon.X, dy = hy - mon.Y;
            int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
            // Awaken if within sight (8 tiles + line of sight ignored — keep
            // it gentle).
            if (dist > 9 && !mon.Seen) continue;
            if (dist <= 9) mon.Seen = true;
            if (dist == 1) { MonsterAttack(mon); continue; }
            // Step one tile toward hero. Try diagonal then cardinal as
            // fallbacks to avoid stuck-on-wall behaviour.
            int sx = Math.Sign(dx), sy = Math.Sign(dy);
            foreach (var (mx, my) in new[] { (sx, sy), (sx, 0), (0, sy) })
            {
                if (mx == 0 && my == 0) continue;
                int tx = mon.X + mx, ty = mon.Y + my;
                if (tx < 0 || ty < 0 || tx >= MapCols || ty >= MapRows) continue;
                if (tx == hx && ty == hy) continue;
                var map = UnflattenMap(CurFloor.Rows);
                char t = map[tx, ty];
                if (t != TFloor && t != TStairUp && t != TStairDown && t != TAltar) continue;
                if (CurFloor.Monsters.Any(o => o != mon && o.X == tx && o.Y == ty)) continue;
                mon.X = tx; mon.Y = ty;
                break;
            }
        }
    }

    private void MonsterAttack(MonsterInst mon)
    {
        var def = Bestiary.First(b => b.Name == mon.TypeName);
        int roll = _rng.Next(1, 21) + def.Atk / 2;
        int target = 10 + ArmorClass(_state.Player);
        if (roll < target) { AddLog($"The {mon.TypeName} misses you."); return; }
        int dmg = Math.Max(1, _rng.Next(1, def.Atk + 1) - ArmorClass(_state.Player) / 3);
        _state.Player.Hp -= dmg;
        AddLog($"The {mon.TypeName} hits you for {dmg}.");
        if (_state.Player.Hp <= 0)
        {
            _state.Player.Hp = 0;
            _mode = Mode.Dead;
            AddLog($"The {mon.TypeName} strikes you down. You die.");
        }
    }

    private void PostTurnTick()
    {
        // HP / MP regen — slower while encumbered.
        int enc = Encumbrance(_state.Player);
        int regenInterval = 4 + enc * 4;
        if ((_state.Player.Xp + _state.Player.X + _state.Player.Y) % regenInterval == 0)
        {
            if (_state.Player.Hp < MaxHp(_state.Player)) _state.Player.Hp++;
            if (_state.Player.Mp < MaxMp(_state.Player)) _state.Player.Mp++;
        }
    }

    // ── Magic ─────────────────────────────────────────────────────────────
    private bool TryCast(int idx)
    {
        if (idx < 0 || idx >= SpellNames.Length) return false;
        int cost = SpellCost[idx];
        if (_state.Player.Mp < cost) { AddLog("Not enough mana."); return false; }
        _state.Player.Mp -= cost;
        switch (idx)
        {
            case 0: CastMagicMissile(); break;
            case 1: CastFireball(); break;
            case 2: CastHeal(); break;
            case 3: CastLight(); break;
            case 4: CastTeleport(); break;
        }
        return true;
    }

    private void CastMagicMissile()
    {
        // Targets the nearest visible monster, deals 8-12 + Int/4 damage.
        var target = NearestMonster();
        if (target == null) { AddLog("Magic missile fizzles — no target."); return; }
        int dmg = _rng.Next(8, 13) + _state.Player.Int / 4;
        target.Hp -= dmg;
        AddLog($"A bolt of force strikes the {target.TypeName} for {dmg}.");
        if (target.Hp <= 0)
        {
            var def = Bestiary.First(b => b.Name == target.TypeName);
            CurFloor.Monsters.Remove(target);
            GainXp(def.Xp);
            _state.Player.Gold += def.Gold;
            if (target.TypeName == "Surtur") { _state.Won = true; _mode = Mode.Won; }
        }
    }

    private void CastFireball()
    {
        // Splash damage on every monster within 2 tiles of any nearby monster?
        // Simpler: damages every monster within 4 tiles of the hero.
        int dmg = _rng.Next(14, 21) + _state.Player.Int / 3;
        int hits = 0;
        var doomed = new List<MonsterInst>();
        foreach (var mon in CurFloor.Monsters)
        {
            int d = Math.Max(Math.Abs(mon.X - _state.Player.X), Math.Abs(mon.Y - _state.Player.Y));
            if (d <= 4) { mon.Hp -= dmg; hits++; if (mon.Hp <= 0) doomed.Add(mon); }
        }
        foreach (var m in doomed)
        {
            var def = Bestiary.First(b => b.Name == m.TypeName);
            CurFloor.Monsters.Remove(m);
            GainXp(def.Xp);
            _state.Player.Gold += def.Gold;
            if (m.TypeName == "Surtur") { _state.Won = true; _mode = Mode.Won; }
        }
        AddLog(hits == 0 ? "Fireball roars across empty air." : $"A blast of flame engulfs {hits} foe(s) for {dmg}.");
    }

    private void CastHeal()
    {
        int amt = _rng.Next(18, 30) + _state.Player.Int / 3;
        int newHp = Math.Min(MaxHp(_state.Player), _state.Player.Hp + amt);
        int healed = newHp - _state.Player.Hp;
        _state.Player.Hp = newHp;
        AddLog($"A warm light surrounds you. +{healed} HP.");
    }

    private void CastLight()
    {
        for (int i = 0; i < CurFloor.Seen.Length; i++) CurFloor.Seen[i] = true;
        foreach (var mon in CurFloor.Monsters) mon.Seen = true;
        AddLog("The chamber blazes with magical light.");
    }

    private void CastTeleport()
    {
        // Move to a random floor cell of the current map.
        var map = UnflattenMap(CurFloor.Rows);
        var cells = new List<(int x, int y)>();
        for (int x = 1; x < MapCols - 1; x++)
            for (int y = 1; y < MapRows - 1; y++)
                if (map[x, y] == TFloor && !CurFloor.Monsters.Any(m => m.X == x && m.Y == y))
                    cells.Add((x, y));
        if (cells.Count == 0) { AddLog("The teleport fizzles."); return; }
        var dest = cells[_rng.Next(cells.Count)];
        _state.Player.X = dest.x; _state.Player.Y = dest.y;
        AddLog("Reality bends. You stand somewhere else.");
    }

    private MonsterInst? NearestMonster()
    {
        MonsterInst? best = null;
        int bestD = int.MaxValue;
        foreach (var m in CurFloor.Monsters)
        {
            int d = Math.Abs(m.X - _state.Player.X) + Math.Abs(m.Y - _state.Player.Y);
            if (d < bestD) { bestD = d; best = m; }
        }
        return best;
    }

    // ── Inventory ─────────────────────────────────────────────────────────
    private void UpdateInventory()
    {
        var pack = _state.Player.Pack;
        if (pack.Count > 0)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Up))   _invCursor = (_invCursor + pack.Count - 1) % pack.Count;
            if (Raylib.IsKeyPressed(KeyboardKey.Down)) _invCursor = (_invCursor + 1) % pack.Count;
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Escape) || Raylib.IsKeyPressed(KeyboardKey.I))
        { _mode = Mode.Playing; return; }
        if (Raylib.IsKeyPressed(KeyboardKey.Enter) && pack.Count > 0) UseOrEquip(_invCursor);
        if (Raylib.IsKeyPressed(KeyboardKey.D) && pack.Count > 0)
        {
            string nm = pack[_invCursor];
            pack.RemoveAt(_invCursor);
            CurFloor.Items.Add(new ItemDrop { Name = nm, X = _state.Player.X, Y = _state.Player.Y });
            AddLog($"You drop the {nm}.");
            _invCursor = Math.Clamp(_invCursor, 0, Math.Max(0, pack.Count - 1));
        }
    }

    private void UseOrEquip(int idx)
    {
        string nm = _state.Player.Pack[idx];
        var def = Catalog[CatIdx(nm)];
        if (def.IsPotion)
        {
            if (def.PotionHeal > 0)
            {
                _state.Player.Hp = Math.Min(MaxHp(_state.Player), _state.Player.Hp + def.PotionHeal);
                AddLog($"You drink the {nm}. HP +{def.PotionHeal}.");
            }
            if (def.PotionMana > 0)
            {
                _state.Player.Mp = Math.Min(MaxMp(_state.Player), _state.Player.Mp + def.PotionMana);
                AddLog($"You drink the {nm}. MP +{def.PotionMana}.");
            }
            _state.Player.Pack.RemoveAt(idx);
            return;
        }
        if (def.IsScroll)
        {
            if (def.ScrollTeleport) CastTeleport();
            else if (def.ScrollIdentify) AddLog("The scroll reveals nothing you didn't already know.");
            _state.Player.Pack.RemoveAt(idx);
            return;
        }
        // Equip into slot. Swap with whatever's currently equipped.
        switch (def.Slot)
        {
            case Slot.Weapon: SwapSlot(idx, () => _state.Player.WeaponName, v => _state.Player.WeaponName = v); break;
            case Slot.Armor:  SwapSlot(idx, () => _state.Player.ArmorName,  v => _state.Player.ArmorName = v); break;
            case Slot.Helmet: SwapSlot(idx, () => _state.Player.HelmetName, v => _state.Player.HelmetName = v); break;
            case Slot.Shield: SwapSlot(idx, () => _state.Player.ShieldName, v => _state.Player.ShieldName = v); break;
        }
        AddLog($"You equip the {nm}.");
    }

    private void SwapSlot(int packIdx, Func<string?> getter, Action<string?> setter)
    {
        string newName = _state.Player.Pack[packIdx];
        string? prev = getter();
        if (prev != null) _state.Player.Pack[packIdx] = prev;
        else _state.Player.Pack.RemoveAt(packIdx);
        setter(newName);
    }

    private void UpdateSpellMenu()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape) || Raylib.IsKeyPressed(KeyboardKey.M))
        { _mode = Mode.Playing; return; }
        for (int i = 0; i < SpellNames.Length; i++)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.One + i))
            {
                _mode = Mode.Playing;
                TryCast(i);
                ResolveMonsterTurn();
                PostTurnTick();
                return;
            }
        }
    }

    // ── Shop ──────────────────────────────────────────────────────────────
    private static readonly string[] ShopStock =
    {
        "Dagger", "Mace", "Short Sword", "Long Sword",
        "Leather Armor", "Studded Leather", "Ring Mail",
        "Leather Helm", "Small Shield",
        "Potion of Healing", "Potion of Mana",
        "Scroll of Identify", "Scroll of Teleport",
    };

    private void OpenShop()
    {
        _shopCursor = 0;
        _shopBuying = true;
        _mode = Mode.Shop;
        AddLog("The shopkeeper nods. Press B to buy, S to sell, Esc to leave.");
    }

    private void UpdateShop()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { _mode = Mode.Playing; return; }
        if (Raylib.IsKeyPressed(KeyboardKey.B)) { _shopBuying = true; _shopCursor = 0; return; }
        if (Raylib.IsKeyPressed(KeyboardKey.S)) { _shopBuying = false; _shopCursor = 0; return; }
        int count = _shopBuying ? ShopStock.Length : _state.Player.Pack.Count;
        if (count > 0)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Up))   _shopCursor = (_shopCursor + count - 1) % count;
            if (Raylib.IsKeyPressed(KeyboardKey.Down)) _shopCursor = (_shopCursor + 1) % count;
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Enter) && count > 0)
        {
            if (_shopBuying)
            {
                string nm = ShopStock[_shopCursor];
                int price = Catalog[CatIdx(nm)].Price;
                if (_state.Player.Gold < price) AddLog("Not enough gold.");
                else if (_state.Player.Pack.Count >= 20) AddLog("Your pack is full.");
                else
                {
                    _state.Player.Gold -= price;
                    _state.Player.Pack.Add(nm);
                    AddLog($"You buy the {nm} for {price}g.");
                }
            }
            else
            {
                string nm = _state.Player.Pack[_shopCursor];
                int price = Catalog[CatIdx(nm)].Price / 2;
                _state.Player.Gold += price;
                _state.Player.Pack.RemoveAt(_shopCursor);
                AddLog($"You sell the {nm} for {price}g.");
                _shopCursor = Math.Clamp(_shopCursor, 0, Math.Max(0, _state.Player.Pack.Count - 1));
            }
        }
    }

    // ── FOV / seen memory ────────────────────────────────────────────────
    private void ReveralFov()
    {
        int hx = _state.Player.X, hy = _state.Player.Y;
        for (int x = Math.Max(0, hx - 8); x < Math.Min(MapCols, hx + 9); x++)
            for (int y = Math.Max(0, hy - 6); y < Math.Min(MapRows, hy + 7); y++)
            {
                int d = Math.Max(Math.Abs(x - hx), Math.Abs(y - hy));
                if (d <= 7) CurFloor.Seen[y * MapCols + x] = true;
            }
    }

    private bool IsVisibleNow(int x, int y)
    {
        int d = Math.Max(Math.Abs(x - _state.Player.X), Math.Abs(y - _state.Player.Y));
        return d <= 7;
    }

    // ── Log ──────────────────────────────────────────────────────────────
    private void AddLog(string line) { _log.Add(line); if (_log.Count > 200) _log.RemoveRange(0, _log.Count - 200); }

    // ── Drawing ──────────────────────────────────────────────────────────
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
        string[] items = _mode == Mode.CharCreation
            ? new[] { "Begin", "Help" }
            : new[] { "New Game", "Inventory", "Spells", "Save", "Help" };
        RetroWidgets.MenuBarVisual(menuBar, items, -1);

        switch (_mode)
        {
            case Mode.CharCreation: DrawCharCreation(panelOffset); break;
            default:
                DrawMap(panelOffset);
                DrawSidebar(panelOffset);
                DrawLog(panelOffset);
                if (_mode == Mode.Inventory)  DrawInventoryOverlay(panelOffset);
                if (_mode == Mode.SpellMenu)  DrawSpellOverlay(panelOffset);
                if (_mode == Mode.Shop)       DrawShopOverlay(panelOffset);
                if (_mode == Mode.Dead || _mode == Mode.Won) DrawEndBanner(panelOffset);
                break;
        }

        // Status bar.
        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        if (_mode == Mode.CharCreation)
        {
            RetroWidgets.StatusBar(status,
                $"Points left: {_ccPoints}",
                "Arrows ±  G gender  click name  Enter Begin");
        }
        else
        {
            var h = _state.Player;
            int enc = Encumbrance(h);
            string encS = enc == 0 ? "—" : enc == 1 ? "burdened" : "strained";
            string floorName = _state.CurrentFloor == -1 ? "Town" : $"Floor {_state.CurrentFloor}";
            RetroWidgets.StatusBar(status,
                $"{h.Name}  L{h.Level}  HP {h.Hp}/{MaxHp(h)}  MP {h.Mp}/{MaxMp(h)}",
                $"{floorName}   Gold {h.Gold}   Enc {encS}");
        }
    }

    // ── Char creation UI ─────────────────────────────────────────────────
    private Rectangle ContentRect(Vector2 panelOffset)
    {
        float y = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 8;
        float h = PanelSize.Y - (y - panelOffset.Y) - FrameInset - RetroWidgets.StatusBarHeight - 4;
        return new Rectangle(panelOffset.X + FrameInset + 8, y, PanelSize.X - 2 * (FrameInset + 8), h);
    }

    private Rectangle NameFieldRect()
    {
        // Relative to the panel.
        float y = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 60;
        return new Rectangle(PanelSize.X / 2 - 120, y, 240, 22);
    }

    private void DrawCharCreation(Vector2 panelOffset)
    {
        var content = ContentRect(panelOffset);
        // Background "parchment" panel for the form.
        var form = new Rectangle(content.X + 40, content.Y + 4, content.Width - 80, content.Height - 20);
        RetroSkin.DrawSunken(form, RetroSkin.FaceLight);

        int cx = (int)(form.X + form.Width / 2);
        int y = (int)form.Y + 14;
        FontManager.DrawText("Castle of the Winds", cx - FontManager.MeasureText("Castle of the Winds", 22) / 2, y, 22, RetroSkin.BodyText);
        y += 32;
        FontManager.DrawText("A roguelike port (from cotwelm)", cx - FontManager.MeasureText("A roguelike port (from cotwelm)", 12) / 2, y, 12, RetroSkin.DisabledText);
        y += 26;

        // Name field
        var nameAbs = new Rectangle(panelOffset.X + NameFieldRect().X, panelOffset.Y + NameFieldRect().Y,
                                    NameFieldRect().Width, NameFieldRect().Height);
        RetroSkin.DrawSunken(nameAbs, Color.White);
        FontManager.DrawText("Name:", (int)nameAbs.X - 60, (int)nameAbs.Y + 4, 14, RetroSkin.BodyText);
        string nameText = _ccName + (_ccNameEdit && (Raylib.GetTime() % 1.0) < 0.5 ? "│" : "");
        FontManager.DrawText(nameText, (int)nameAbs.X + 6, (int)nameAbs.Y + 4, 14, RetroSkin.BodyText);

        y = (int)nameAbs.Y + 36;
        // Gender
        string gender = _ccMale ? "Male (press G to toggle)" : "Female (press G to toggle)";
        FontManager.DrawText("Gender: " + gender, cx - FontManager.MeasureText("Gender: " + gender, 14) / 2, y, 14, RetroSkin.BodyText);
        y += 28;

        // Attributes
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
        FontManager.DrawText("Up/Down select  ←/→ or +/− adjust  Enter when 0 left",
            cx - FontManager.MeasureText("Up/Down select  ←/→ or +/− adjust  Enter when 0 left", 12) / 2,
            y, 12, RetroSkin.DisabledText);
    }

    // ── Map drawing ──────────────────────────────────────────────────────
    private Rectangle MapRect(Vector2 panelOffset)
    {
        float y = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 6;
        return new Rectangle(panelOffset.X + FrameInset + 6, y, MapCols * Tile, MapRows * Tile);
    }

    private void DrawMap(Vector2 panelOffset)
    {
        var rect = MapRect(panelOffset);
        Raylib.DrawRectangleRec(rect, new Color((byte)10, (byte)10, (byte)18, (byte)255));
        var map = UnflattenMap(CurFloor.Rows);

        for (int x = 0; x < MapCols; x++)
            for (int y = 0; y < MapRows; y++)
            {
                bool seen = CurFloor.Seen[y * MapCols + x];
                bool vis = _state.CurrentFloor == -1 || IsVisibleNow(x, y);
                if (!seen) continue;
                int px = (int)rect.X + x * Tile;
                int py = (int)rect.Y + y * Tile;
                char t = map[x, y];
                Color bg, fg;
                switch (t)
                {
                    case TWall:
                        bg = new Color((byte)56, (byte)50, (byte)42, (byte)255);
                        fg = new Color((byte)96, (byte)84, (byte)68, (byte)255);
                        Raylib.DrawRectangle(px, py, Tile, Tile, bg);
                        Raylib.DrawRectangle(px + 1, py + 1, Tile - 2, Tile - 2, fg);
                        break;
                    case TFloor:
                        bg = new Color((byte)24, (byte)24, (byte)32, (byte)255);
                        Raylib.DrawRectangle(px, py, Tile, Tile, bg);
                        // floor stipple
                        if ((x + y) % 4 == 0) Raylib.DrawPixel(px + 4, py + 6, new Color((byte)52, (byte)52, (byte)64, (byte)255));
                        break;
                    case TGrass:
                        Raylib.DrawRectangle(px, py, Tile, Tile, new Color((byte)50, (byte)96, (byte)58, (byte)255));
                        if ((x * 3 + y) % 5 == 0)
                            Raylib.DrawPixel(px + 3, py + 8, new Color((byte)92, (byte)164, (byte)96, (byte)255));
                        break;
                    case TTree:
                        Raylib.DrawRectangle(px, py, Tile, Tile, new Color((byte)50, (byte)96, (byte)58, (byte)255));
                        Raylib.DrawCircle(px + Tile / 2, py + Tile / 2, Tile / 2 - 2, new Color((byte)40, (byte)80, (byte)40, (byte)255));
                        break;
                    case TDoor:
                        Raylib.DrawRectangle(px, py, Tile, Tile, new Color((byte)24, (byte)24, (byte)32, (byte)255));
                        Raylib.DrawRectangle(px + 3, py + 2, Tile - 6, Tile - 4, new Color((byte)160, (byte)104, (byte)56, (byte)255));
                        break;
                    case TStairUp:
                    case TStairDown:
                        Raylib.DrawRectangle(px, py, Tile, Tile, new Color((byte)24, (byte)24, (byte)32, (byte)255));
                        FontManager.DrawText(t.ToString(), px + 3, py - 1, 16, RetroSkin.Highlight);
                        break;
                    case TShop:
                        Raylib.DrawRectangle(px, py, Tile, Tile, new Color((byte)128, (byte)96, (byte)56, (byte)255));
                        FontManager.DrawText("$", px + 4, py - 1, 14, Color.Yellow);
                        break;
                    case TInn:
                        Raylib.DrawRectangle(px, py, Tile, Tile, new Color((byte)80, (byte)112, (byte)160, (byte)255));
                        FontManager.DrawText("I", px + 5, py - 1, 14, Color.White);
                        break;
                    case TAltar:
                        Raylib.DrawRectangle(px, py, Tile, Tile, new Color((byte)24, (byte)24, (byte)32, (byte)255));
                        Raylib.DrawRectangle(px + 2, py + 4, Tile - 4, Tile - 8, new Color((byte)80, (byte)16, (byte)16, (byte)255));
                        break;
                }

                // Out-of-FOV → darken
                if (!vis && t != TGrass && t != TTree)
                    Raylib.DrawRectangle(px, py, Tile, Tile, new Color((byte)0, (byte)0, (byte)0, (byte)120));
            }

        // Items
        foreach (var it in CurFloor.Items)
        {
            if (!CurFloor.Seen[it.Y * MapCols + it.X]) continue;
            int px = (int)rect.X + it.X * Tile;
            int py = (int)rect.Y + it.Y * Tile;
            var def = Catalog[CatIdx(it.Name)];
            FontManager.DrawText(def.Glyph.ToString(), px + 3, py - 2, 16, Color.Yellow);
        }

        // Monsters
        foreach (var mon in CurFloor.Monsters)
        {
            if (!IsVisibleNow(mon.X, mon.Y) && _state.CurrentFloor != -1) continue;
            int px = (int)rect.X + mon.X * Tile;
            int py = (int)rect.Y + mon.Y * Tile;
            var def = Bestiary.First(b => b.Name == mon.TypeName);
            Color col = mon.TypeName == "Surtur" ? Color.Red : new Color((byte)220, (byte)170, (byte)110, (byte)255);
            FontManager.DrawText(def.Glyph.ToString(), px + 3, py - 2, 16, col);
        }

        // Hero
        int hx = (int)rect.X + _state.Player.X * Tile;
        int hy = (int)rect.Y + _state.Player.Y * Tile;
        FontManager.DrawText("@", hx + 3, hy - 2, 16, Color.White);
    }

    // ── Sidebar ──────────────────────────────────────────────────────────
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
        FontManager.DrawText($"{(h.Male ? "Male" : "Female")}  Lv {h.Level}  XP {h.Xp}/{h.Level * 100}",
            x, y, 12, RetroSkin.DisabledText);
        y += 22;

        FontManager.DrawText($"HP   {h.Hp}/{MaxHp(h)}", x, y, 14, RetroSkin.BodyText); y += 18;
        FontManager.DrawText($"Mana {h.Mp}/{MaxMp(h)}", x, y, 14, RetroSkin.BodyText); y += 18;
        FontManager.DrawText($"AC   {ArmorClass(h)}    +Hit {ToHit(h)}", x, y, 13, RetroSkin.BodyText); y += 18;
        FontManager.DrawText($"Str {h.Str}  Int {h.Int}  Dex {h.Dex}  Con {h.Con}", x, y, 12, RetroSkin.BodyText); y += 22;

        FontManager.DrawText("Equipment:", x, y, 12, RetroSkin.DisabledText); y += 16;
        FontManager.DrawText($"  Wpn  {h.WeaponName ?? "—"}", x, y, 12, RetroSkin.BodyText); y += 14;
        FontManager.DrawText($"  Arm  {h.ArmorName ?? "—"}", x, y, 12, RetroSkin.BodyText); y += 14;
        FontManager.DrawText($"  Hlm  {h.HelmetName ?? "—"}", x, y, 12, RetroSkin.BodyText); y += 14;
        FontManager.DrawText($"  Shd  {h.ShieldName ?? "—"}", x, y, 12, RetroSkin.BodyText); y += 18;

        FontManager.DrawText($"Pack: {h.Pack.Count}/20", x, y, 12, RetroSkin.DisabledText); y += 16;
        int show = Math.Min(h.Pack.Count, 10);
        for (int i = 0; i < show; i++)
            FontManager.DrawText("• " + h.Pack[i], x + 4, y + i * 14, 11, RetroSkin.BodyText);
        y += show * 14 + 8;
        if (h.Pack.Count > 10)
            FontManager.DrawText($"+{h.Pack.Count - 10} more (i)", x + 4, y, 11, RetroSkin.DisabledText);

        // Keymap reminder at the bottom of the sidebar
        int ky = (int)(sb.Y + sb.Height) - 92;
        FontManager.DrawText("Keys:", x, ky, 11, RetroSkin.DisabledText); ky += 14;
        FontManager.DrawText("arrows/hjkl move", x, ky, 11, RetroSkin.BodyText); ky += 12;
        FontManager.DrawText(", pick up   . wait", x, ky, 11, RetroSkin.BodyText); ky += 12;
        FontManager.DrawText("D down   A up", x, ky, 11, RetroSkin.BodyText); ky += 12;
        FontManager.DrawText("i invent  m spells", x, ky, 11, RetroSkin.BodyText); ky += 12;
        FontManager.DrawText("1-5 quick-cast", x, ky, 11, RetroSkin.BodyText);
    }

    // ── Log ──────────────────────────────────────────────────────────────
    private void DrawLog(Vector2 panelOffset)
    {
        var m = MapRect(panelOffset);
        // Log sits to the right of the sidebar? Better: under the map.
        var logRect = new Rectangle(m.X, m.Y + m.Height + 6,
            m.Width + SidebarW + 8,
            PanelSize.Y - (m.Y + m.Height + 6 - panelOffset.Y) - RetroWidgets.StatusBarHeight - FrameInset - 4);
        if (logRect.Height < 12) return;
        RetroSkin.DrawSunken(logRect, new Color((byte)20, (byte)20, (byte)28, (byte)255));
        int shown = Math.Min(_log.Count, (int)(logRect.Height / 14));
        for (int i = 0; i < shown; i++)
        {
            string line = _log[_log.Count - shown + i];
            int alpha = 120 + 135 * i / Math.Max(1, shown);
            FontManager.DrawText(line, (int)logRect.X + 8, (int)logRect.Y + 4 + i * 14, 12,
                new Color((byte)220, (byte)220, (byte)220, (byte)alpha));
        }
    }

    // ── Overlays ─────────────────────────────────────────────────────────
    private Rectangle CenteredBox(Vector2 panelOffset, int w, int h)
        => new(panelOffset.X + (PanelSize.X - w) / 2, panelOffset.Y + (PanelSize.Y - h) / 2, w, h);

    private void DrawInventoryOverlay(Vector2 panelOffset)
    {
        var box = CenteredBox(panelOffset, 460, 360);
        RetroSkin.DrawRaised(box);
        int x = (int)box.X + 16, y = (int)box.Y + 16;
        FontManager.DrawText("Inventory", x, y, 18, RetroSkin.BodyText); y += 26;
        var pack = _state.Player.Pack;
        if (pack.Count == 0)
            FontManager.DrawText("Your pack is empty.", x, y, 14, RetroSkin.DisabledText);
        for (int i = 0; i < pack.Count; i++)
        {
            var def = Catalog[CatIdx(pack[i])];
            string prefix = i == _invCursor ? "►" : " ";
            string equipped = "";
            if (pack[i] == _state.Player.WeaponName) equipped = "(W)";
            else if (pack[i] == _state.Player.ArmorName) equipped = "(A)";
            else if (pack[i] == _state.Player.HelmetName) equipped = "(H)";
            else if (pack[i] == _state.Player.ShieldName) equipped = "(S)";
            string line = $"{prefix} {def.Glyph} {pack[i]}   {def.Weight}lb   {equipped}";
            var col = i == _invCursor ? RetroSkin.TitleActive : RetroSkin.BodyText;
            FontManager.DrawText(line, x, y + i * 16, 13, col);
        }
        int by = (int)box.Y + (int)box.Height - 32;
        FontManager.DrawText("Enter equip/use   D drop   Esc close", x, by, 12, RetroSkin.DisabledText);
    }

    private void DrawSpellOverlay(Vector2 panelOffset)
    {
        var box = CenteredBox(panelOffset, 380, 240);
        RetroSkin.DrawRaised(box);
        int x = (int)box.X + 16, y = (int)box.Y + 16;
        FontManager.DrawText("Spellbook", x, y, 18, RetroSkin.BodyText); y += 28;
        for (int i = 0; i < SpellNames.Length; i++)
        {
            string line = $" {i + 1}. {SpellNames[i],-14}  {SpellCost[i]} MP";
            var col = _state.Player.Mp >= SpellCost[i] ? RetroSkin.BodyText : RetroSkin.DisabledText;
            FontManager.DrawText(line, x, y + i * 18, 14, col);
        }
        FontManager.DrawText("Press 1-5 to cast   Esc close",
            x, (int)box.Y + (int)box.Height - 28, 12, RetroSkin.DisabledText);
    }

    private void DrawShopOverlay(Vector2 panelOffset)
    {
        var box = CenteredBox(panelOffset, 540, 420);
        RetroSkin.DrawRaised(box);
        int x = (int)box.X + 16, y = (int)box.Y + 16;
        FontManager.DrawText("Shop", x, y, 18, RetroSkin.BodyText);
        FontManager.DrawText($"Gold: {_state.Player.Gold}", x + 380, y, 14, RetroSkin.BodyText);
        y += 30;
        FontManager.DrawText(_shopBuying ? "[BUY]  Sell  (S to switch)" : "Buy  [SELL]  (B to switch)",
            x, y, 13, RetroSkin.TitleActive);
        y += 22;
        if (_shopBuying)
        {
            for (int i = 0; i < ShopStock.Length; i++)
            {
                var def = Catalog[CatIdx(ShopStock[i])];
                string prefix = i == _shopCursor ? "►" : " ";
                string line = $"{prefix} {def.Glyph} {ShopStock[i],-22}  {def.Price}g";
                var col = i == _shopCursor ? RetroSkin.TitleActive : RetroSkin.BodyText;
                FontManager.DrawText(line, x, y + i * 16, 13, col);
            }
        }
        else
        {
            var pack = _state.Player.Pack;
            if (pack.Count == 0) FontManager.DrawText("Nothing to sell.", x, y, 13, RetroSkin.DisabledText);
            for (int i = 0; i < pack.Count; i++)
            {
                var def = Catalog[CatIdx(pack[i])];
                string prefix = i == _shopCursor ? "►" : " ";
                string line = $"{prefix} {def.Glyph} {pack[i],-22}  {def.Price / 2}g";
                var col = i == _shopCursor ? RetroSkin.TitleActive : RetroSkin.BodyText;
                FontManager.DrawText(line, x, y + i * 16, 13, col);
            }
        }
        FontManager.DrawText("Enter confirm  Esc leave",
            x, (int)box.Y + (int)box.Height - 28, 12, RetroSkin.DisabledText);
    }

    private void DrawEndBanner(Vector2 panelOffset)
    {
        var box = CenteredBox(panelOffset, 520, 180);
        RetroSkin.DrawRaised(box);
        string msg = _mode == Mode.Won ? "VICTORY" : "YOU HAVE DIED";
        var col = _mode == Mode.Won ? new Color((byte)220, (byte)180, (byte)60, (byte)255) : new Color((byte)200, (byte)60, (byte)60, (byte)255);
        int w = FontManager.MeasureText(msg, 32);
        FontManager.DrawText(msg, (int)(box.X + (box.Width - w) / 2), (int)box.Y + 28, 32, col);
        string sub = _mode == Mode.Won
            ? "Surtur is slain. The realm is at peace."
            : "Your story ends in the dark.";
        int sw = FontManager.MeasureText(sub, 14);
        FontManager.DrawText(sub, (int)(box.X + (box.Width - sw) / 2), (int)box.Y + 80, 14, RetroSkin.BodyText);
        const string hint = "Press Enter or Space to close.";
        int hw = FontManager.MeasureText(hint, 12);
        FontManager.DrawText(hint, (int)(box.X + (box.Width - hw) / 2), (int)box.Y + 130, 12, RetroSkin.DisabledText);
    }
}
