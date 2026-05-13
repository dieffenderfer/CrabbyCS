using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Medieval War — port of FUNGAMES/MEDWAR11 (Mark Brownstein / Burnham
/// Park Software, 1993, Medieval War v1.1).
///
/// Mechanics that come directly from README.TXT:
///
///   • Grid-based map with cities, forests, mountains, islands.
///   • You and one or more opponents start with one city each.
///   • Win by capturing every enemy city and destroying every enemy unit.
///   • Empty cities are captured by stepping onto them — your piece
///     might be destroyed, or might take the city; if it takes the city,
///     the piece disappears into the city as a garrison and the city
///     turns your colour.
///   • Cities build units; double-clicking a city changes production
///     (turns invested in the previous selection are lost).
///   • If you're stranded on an island, build a Light Galleon and load
///     a land piece onto it to cross the sea.
///   • Fog of war: unexplored squares stay black; squares within 1 of
///     a friendly piece are revealed and stay revealed (terrain) for
///     the rest of the game.
///
/// The exhaustive rule sheet lives in the binary .HLP we can't extract,
/// so combat values, build times, and the AI heuristic are inferred from
/// genre convention. Four unit types are bundled — Light Infantry,
/// Cavalry, Light Galleon, Catapult — which matches the "different unit
/// types" the README references without committing to specifics we
/// can't verify against the original.
/// </summary>
public class MedievalWarActivity : IActivity
{
    public Vector2 PanelSize => new(820, 540);
    public bool IsFinished { get; private set; }

    private const int FrameInset = 3;
    private const int GridCols = 18;
    private const int GridRows = 12;
    private const int Tile = 28;
    private const int SidebarW = 220;
    private const int Player = 0;
    private const int Ai = 1;

    private enum Terrain { Water, Plain, Forest, Mountain }

    private enum UnitType { LightInfantry, Cavalry, Galleon, Catapult }

    private record UnitStats(string Name, int Build, int Move, int Atk, int Def,
                             bool LandOnly, bool WaterOnly, int CargoSlots, int SiegeBonus);

    private static readonly Dictionary<UnitType, UnitStats> Stats = new()
    {
        [UnitType.LightInfantry] = new("Light Infantry", 4, 1, 2, 2, true,  false, 0, 0),
        [UnitType.Cavalry]       = new("Cavalry",        6, 2, 3, 1, true,  false, 0, 0),
        [UnitType.Galleon]       = new("Light Galleon", 10, 2, 1, 1, false, true,  1, 0),
        [UnitType.Catapult]      = new("Catapult",      12, 1, 4, 1, true,  false, 0, 2),
    };

    private class City
    {
        public int X, Y;
        public int Owner;                  // -1 neutral, 0 player, 1 ai
        public UnitType Producing;
        public int TurnsInvested;
    }

    private class Unit
    {
        public int X, Y;
        public int Owner;
        public UnitType Type;
        public int MovesLeft;
        public Unit? Cargo;
    }

    private Terrain[,] _terrain = new Terrain[GridCols, GridRows];
    private bool[,] _seen = new bool[GridCols, GridRows];        // ever-seen by player
    private readonly List<City> _cities = new();
    private readonly List<Unit> _units = new();
    private int _selectedUnitIdx = -1;
    private int _selectedCityIdx = -1;
    private int _turn;
    private bool _playerTurn = true;
    private bool _gameOver;
    private int _winner = -1;
    private string _status = "Move units one square at a time. End Turn when done.";
    private readonly Random _rng = new();
    private int _aiThinkFrames;          // tiny delay so the AI moves animate

    public void Load() => NewGame();
    public void Close() => IsFinished = true;

    // ── Map / setup ───────────────────────────────────────────────────────
    private void NewGame()
    {
        GenerateMap();
        _cities.Clear();
        _units.Clear();
        // Place 8 neutral cities, at least 3 tiles apart, on plain/forest tiles.
        var landCells = new List<(int x, int y)>();
        for (int x = 0; x < GridCols; x++)
            for (int y = 0; y < GridRows; y++)
                if (_terrain[x, y] == Terrain.Plain || _terrain[x, y] == Terrain.Forest)
                    landCells.Add((x, y));
        Shuffle(landCells);
        foreach (var (cx, cy) in landCells)
        {
            if (_cities.Count >= 10) break;
            bool tooClose = _cities.Any(c => Math.Abs(c.X - cx) + Math.Abs(c.Y - cy) < 3);
            if (tooClose) continue;
            _cities.Add(new City { X = cx, Y = cy, Owner = -1, Producing = UnitType.LightInfantry });
        }
        // Assign one city to each player — pick two that are far apart.
        var byDist = new List<(City a, City b, int d)>();
        for (int i = 0; i < _cities.Count; i++)
            for (int j = i + 1; j < _cities.Count; j++)
            {
                var a = _cities[i]; var b = _cities[j];
                byDist.Add((a, b, Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y)));
            }
        byDist.Sort((u, v) => v.d.CompareTo(u.d));
        if (byDist.Count > 0)
        {
            byDist[0].a.Owner = Player;
            byDist[0].b.Owner = Ai;
        }

        _seen = new bool[GridCols, GridRows];
        ApplyFog();

        _turn = 1;
        _playerTurn = true;
        _gameOver = false;
        _winner = -1;
        _selectedUnitIdx = -1;
        _selectedCityIdx = -1;
        _status = "Turn 1 — your move.";
    }

    private void GenerateMap()
    {
        // Cellular-automata terrain: random init, smooth with a majority
        // rule, then sprinkle forests and mountains on the land tiles.
        var land = new bool[GridCols, GridRows];
        for (int x = 0; x < GridCols; x++)
            for (int y = 0; y < GridRows; y++)
                land[x, y] = _rng.NextDouble() < 0.55;

        for (int iter = 0; iter < 4; iter++)
        {
            var next = new bool[GridCols, GridRows];
            for (int x = 0; x < GridCols; x++)
                for (int y = 0; y < GridRows; y++)
                {
                    int neighbors = 0;
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= GridCols || ny >= GridRows)
                                continue;
                            if (dx == 0 && dy == 0) continue;
                            if (land[nx, ny]) neighbors++;
                        }
                    next[x, y] = neighbors >= 4 || (land[x, y] && neighbors >= 3);
                }
            land = next;
        }

        _terrain = new Terrain[GridCols, GridRows];
        for (int x = 0; x < GridCols; x++)
            for (int y = 0; y < GridRows; y++)
            {
                if (!land[x, y]) _terrain[x, y] = Terrain.Water;
                else
                {
                    double r = _rng.NextDouble();
                    _terrain[x, y] = r < 0.12 ? Terrain.Mountain
                                   : r < 0.30 ? Terrain.Forest
                                   : Terrain.Plain;
                }
            }
    }

    private void ApplyFog()
    {
        foreach (var u in _units)
            if (u.Owner == Player) RevealAround(u.X, u.Y);
        foreach (var c in _cities)
            if (c.Owner == Player) RevealAround(c.X, c.Y);
    }

    private void RevealAround(int x, int y)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= GridCols || ny >= GridRows) continue;
                _seen[nx, ny] = true;
            }
    }

    // ── Update ────────────────────────────────────────────────────────────
    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;
        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        { IsFinished = true; return; }

        var menuBar = new Rectangle(FrameInset, FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        switch (RetroWidgets.MenuBarHitTest(menuBar, new[] { "New", "End Turn", "Help" },
                                            local, leftPressed))
        {
            case 0: NewGame(); return;
            case 1: if (_playerTurn && !_gameOver) EndPlayerTurn(); return;
            case 2:
                _status = "Click a unit, then click an adjacent tile. Cities make units; double-click to change production.";
                return;
        }

        if (_gameOver) return;

        if (!_playerTurn)
        {
            // Small delay between AI moves so player can read them.
            _aiThinkFrames++;
            if (_aiThinkFrames > 12)
            {
                _aiThinkFrames = 0;
                AiStepOnce();
            }
            return;
        }

        var grid = GridRect();
        if (leftPressed && RetroSkin.PointInRect(local, grid))
        {
            int gx = (int)((local.X - grid.X) / Tile);
            int gy = (int)((local.Y - grid.Y) / Tile);
            if (gx < 0 || gx >= GridCols || gy < 0 || gy >= GridRows) return;
            HandleGridClick(gx, gy);
        }
    }

    private void HandleGridClick(int gx, int gy)
    {
        // 1. If a unit is already selected, attempt move/attack.
        if (_selectedUnitIdx >= 0)
        {
            var u = _units[_selectedUnitIdx];
            if (u.Owner == Player && u.MovesLeft > 0)
            {
                int dx = gx - u.X, dy = gy - u.Y;
                if (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1 && (dx != 0 || dy != 0))
                {
                    AttemptMove(u, gx, gy);
                    return;
                }
            }
            // Click elsewhere → deselect.
            _selectedUnitIdx = -1;
        }

        // 2. Select your own unit or city under the click.
        int uIdx = _units.FindIndex(u => u.X == gx && u.Y == gy && u.Owner == Player);
        if (uIdx >= 0)
        {
            _selectedUnitIdx = uIdx;
            _selectedCityIdx = -1;
            var u = _units[uIdx];
            _status = $"Selected {Stats[u.Type].Name}. Moves left: {u.MovesLeft}.";
            return;
        }

        int cIdx = _cities.FindIndex(c => c.X == gx && c.Y == gy && c.Owner == Player);
        if (cIdx >= 0)
        {
            if (_selectedCityIdx == cIdx)
            {
                // Second click on the same city cycles production type —
                // matches the doc's "double-click to change" intent.
                CycleCityProduction(_cities[cIdx]);
            }
            else
            {
                _selectedCityIdx = cIdx;
                _selectedUnitIdx = -1;
                var c = _cities[cIdx];
                _status = $"City building {Stats[c.Producing].Name} ({c.TurnsInvested}/{Stats[c.Producing].Build}). Click again to change.";
            }
            return;
        }

        _selectedUnitIdx = -1;
        _selectedCityIdx = -1;
    }

    private void CycleCityProduction(City c)
    {
        UnitType[] order = { UnitType.LightInfantry, UnitType.Cavalry,
                             UnitType.Galleon, UnitType.Catapult };
        int idx = Array.IndexOf(order, c.Producing);
        c.Producing = order[(idx + 1) % order.Length];
        c.TurnsInvested = 0;
        _status = $"Now building {Stats[c.Producing].Name} in city ({Stats[c.Producing].Build} turns).";
    }

    private bool CanEnter(Unit u, int x, int y)
    {
        if (x < 0 || y < 0 || x >= GridCols || y >= GridRows) return false;
        var t = _terrain[x, y];
        if (t == Terrain.Mountain) return false;
        var stats = Stats[u.Type];
        bool waterTile = t == Terrain.Water;
        bool cityHere = _cities.Any(c => c.X == x && c.Y == y);
        if (stats.LandOnly && waterTile)
        {
            // Land units can only step on water if a friendly empty galleon is there.
            return _units.Any(g => g.X == x && g.Y == y && g.Type == UnitType.Galleon
                                   && g.Owner == u.Owner && g.Cargo == null);
        }
        if (stats.WaterOnly && !waterTile && !cityHere) return false;
        // Two friendly units never stack (except infantry loading onto galleon, handled above).
        var friend = _units.FirstOrDefault(f => f.X == x && f.Y == y && f.Owner == u.Owner);
        if (friend != null && !(friend.Type == UnitType.Galleon && stats.LandOnly && waterTile && friend.Cargo == null))
            return false;
        return true;
    }

    private void AttemptMove(Unit u, int gx, int gy)
    {
        if (!CanEnter(u, gx, gy))
        {
            _status = "Can't move there.";
            return;
        }

        // Enemy unit on the target → combat.
        var enemy = _units.FirstOrDefault(e => e.X == gx && e.Y == gy && e.Owner != u.Owner);
        if (enemy != null)
        {
            ResolveCombat(u, enemy, gx, gy);
            return;
        }

        // Loading onto a friendly galleon (land unit stepping on water).
        var ferry = _units.FirstOrDefault(f => f.X == gx && f.Y == gy
                                               && f.Type == UnitType.Galleon
                                               && f.Owner == u.Owner
                                               && f.Cargo == null);
        if (ferry != null && _terrain[gx, gy] == Terrain.Water)
        {
            ferry.Cargo = u;
            _units.Remove(u);
            _selectedUnitIdx = -1;
            _status = $"{Stats[u.Type].Name} boarded a galleon.";
            ApplyFog();
            return;
        }

        // Enemy city.
        var enemyCity = _cities.FirstOrDefault(c => c.X == gx && c.Y == gy && c.Owner != u.Owner);
        if (enemyCity != null)
        {
            int defenderRoll = 1 + _rng.Next(6);
            int attackerRoll = Stats[u.Type].Atk + Stats[u.Type].SiegeBonus + _rng.Next(6);
            if (enemyCity.Owner == -1)
            {
                // Empty / neutral city — coin flip, biased by attacker strength.
                bool capture = _rng.NextDouble() < 0.55 + Stats[u.Type].Atk * 0.06;
                if (capture)
                {
                    enemyCity.Owner = u.Owner;
                    _units.Remove(u);
                    _selectedUnitIdx = -1;
                    _status = $"Captured neutral city! Garrisoned by your {Stats[u.Type].Name}.";
                }
                else
                {
                    _units.Remove(u);
                    _selectedUnitIdx = -1;
                    _status = "Your piece was lost trying to take an unoccupied city.";
                }
            }
            else if (attackerRoll > defenderRoll)
            {
                enemyCity.Owner = u.Owner;
                _units.Remove(u);
                _selectedUnitIdx = -1;
                _status = $"You took the enemy city!";
            }
            else
            {
                _units.Remove(u);
                _selectedUnitIdx = -1;
                _status = "Your assault was repelled.";
            }
            ApplyFog();
            CheckEnd();
            return;
        }

        // Normal move.
        u.X = gx;
        u.Y = gy;
        u.MovesLeft--;
        // Galleon offloads cargo automatically when it stops next to land.
        if (u.Type == UnitType.Galleon && u.Cargo != null)
        {
            // Offer offload: place cargo on first adjacent land tile.
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    int nx = gx + dx, ny = gy + dy;
                    if (nx < 0 || ny < 0 || nx >= GridCols || ny >= GridRows) continue;
                    if (_terrain[nx, ny] == Terrain.Water) continue;
                    if (_terrain[nx, ny] == Terrain.Mountain) continue;
                    if (_units.Any(o => o.X == nx && o.Y == ny)) continue;
                    var cargo = u.Cargo;
                    cargo.X = nx; cargo.Y = ny;
                    _units.Add(cargo);
                    u.Cargo = null;
                    _status = $"Disembarked {Stats[cargo.Type].Name}.";
                    goto doneOffload;
                }
            doneOffload:;
        }
        ApplyFog();
    }

    private void ResolveCombat(Unit attacker, Unit defender, int gx, int gy)
    {
        var aStats = Stats[attacker.Type];
        var dStats = Stats[defender.Type];
        int aRoll = aStats.Atk + _rng.Next(6);
        int dRoll = dStats.Def + _rng.Next(6);
        // Terrain defense bonus
        if (_terrain[gx, gy] == Terrain.Forest) dRoll += 1;
        if (_terrain[gx, gy] == Terrain.Mountain) dRoll += 2;
        if (aRoll > dRoll)
        {
            _units.Remove(defender);
            attacker.X = gx;
            attacker.Y = gy;
            attacker.MovesLeft--;
            _status = $"{aStats.Name} destroyed enemy {dStats.Name}.";
        }
        else
        {
            _units.Remove(attacker);
            _selectedUnitIdx = -1;
            _status = $"Your {aStats.Name} was lost in the fight.";
        }
        ApplyFog();
        CheckEnd();
    }

    // ── End turn / AI ─────────────────────────────────────────────────────
    private void EndPlayerTurn()
    {
        ProcessProduction(Player);
        _playerTurn = false;
        _status = "AI is taking its turn...";
        // Refresh AI unit moves.
        foreach (var u in _units) if (u.Owner == Ai) u.MovesLeft = Stats[u.Type].Move;
    }

    private void AiStepOnce()
    {
        // Move one AI unit at a time per tick so the player can watch.
        var movable = _units.Where(u => u.Owner == Ai && u.MovesLeft > 0).ToList();
        if (movable.Count == 0)
        {
            ProcessProduction(Ai);
            foreach (var pu in _units) if (pu.Owner == Player) pu.MovesLeft = Stats[pu.Type].Move;
            _playerTurn = true;
            _turn++;
            _status = $"Turn {_turn} — your move.";
            CheckEnd();
            return;
        }

        var u = movable[0];
        var target = ChooseAiTarget(u);
        if (target == null) { u.MovesLeft = 0; return; }
        var (tx, ty) = target.Value;
        int dx = Math.Sign(tx - u.X);
        int dy = Math.Sign(ty - u.Y);
        // Try diagonal first, then horizontal/vertical fallbacks.
        if (TryAiStep(u, u.X + dx, u.Y + dy)) return;
        if (TryAiStep(u, u.X + dx, u.Y)) return;
        if (TryAiStep(u, u.X, u.Y + dy)) return;
        u.MovesLeft = 0;
    }

    private bool TryAiStep(Unit u, int gx, int gy)
    {
        if (gx == u.X && gy == u.Y) return false;
        // Enemy unit or city: attack regardless of CanEnter checks below.
        var enemyUnit = _units.FirstOrDefault(e => e.X == gx && e.Y == gy && e.Owner != u.Owner);
        var enemyCity = _cities.FirstOrDefault(c => c.X == gx && c.Y == gy && c.Owner != u.Owner);
        if (enemyUnit != null) { ResolveCombat(u, enemyUnit, gx, gy); return true; }
        if (enemyCity != null) { AttemptMove(u, gx, gy); return true; }

        if (!CanEnter(u, gx, gy)) return false;
        // Plain move.
        u.X = gx;
        u.Y = gy;
        u.MovesLeft--;
        return true;
    }

    private (int x, int y)? ChooseAiTarget(Unit u)
    {
        // Priority: nearest enemy city, then nearest neutral city, then
        // nearest enemy unit, then wander toward unexplored.
        (int x, int y)? best = null;
        int bestDist = int.MaxValue;
        foreach (var c in _cities)
        {
            if (c.Owner == Ai) continue;
            int d = Math.Abs(c.X - u.X) + Math.Abs(c.Y - u.Y);
            if (c.Owner == Player) d -= 2;     // bias toward player cities
            if (d < bestDist) { bestDist = d; best = (c.X, c.Y); }
        }
        if (best != null) return best;
        foreach (var e in _units)
        {
            if (e.Owner == u.Owner) continue;
            int d = Math.Abs(e.X - u.X) + Math.Abs(e.Y - u.Y);
            if (d < bestDist) { bestDist = d; best = (e.X, e.Y); }
        }
        return best;
    }

    private void ProcessProduction(int owner)
    {
        foreach (var c in _cities)
        {
            if (c.Owner != owner) continue;
            c.TurnsInvested++;
            var stats = Stats[c.Producing];
            if (c.TurnsInvested >= stats.Build)
            {
                // Find a placement: city tile if empty, else an adjacent free tile.
                bool placed = false;
                bool waterUnit = stats.WaterOnly;
                for (int dx = 0; dx <= 1 && !placed; dx++)
                    for (int dy = 0; dy <= 1 && !placed; dy++)
                        for (int sx = -1; sx <= 1 && !placed; sx += 2)
                            for (int sy = -1; sy <= 1 && !placed; sy += 2)
                            {
                                int nx = c.X + dx * sx;
                                int ny = c.Y + dy * sy;
                                if (nx < 0 || ny < 0 || nx >= GridCols || ny >= GridRows) continue;
                                bool waterTile = _terrain[nx, ny] == Terrain.Water;
                                if (waterUnit && !waterTile) continue;
                                if (!waterUnit && waterTile) continue;
                                if (_terrain[nx, ny] == Terrain.Mountain) continue;
                                if (_units.Any(u => u.X == nx && u.Y == ny)) continue;
                                _units.Add(new Unit
                                {
                                    X = nx, Y = ny, Owner = owner, Type = c.Producing,
                                    MovesLeft = stats.Move,
                                });
                                placed = true;
                            }
                if (placed) c.TurnsInvested = 0;
            }
        }
        ApplyFog();
    }

    private void CheckEnd()
    {
        bool playerCities = _cities.Any(c => c.Owner == Player);
        bool aiCities = _cities.Any(c => c.Owner == Ai);
        bool playerUnits = _units.Any(u => u.Owner == Player);
        bool aiUnits = _units.Any(u => u.Owner == Ai);
        if (!playerCities && !playerUnits) { _gameOver = true; _winner = Ai; _status = "The realm has fallen. Defeat."; }
        else if (!aiCities && !aiUnits) { _gameOver = true; _winner = Player; _status = "All enemy cities fallen. Victory!"; }
    }

    // ── Drawing ───────────────────────────────────────────────────────────
    private Rectangle GridRect()
    {
        float top = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 6;
        return new Rectangle(FrameInset + 8, top, GridCols * Tile, GridRows * Tile);
    }

    private Rectangle SidebarRect()
    {
        var g = GridRect();
        return new Rectangle(g.X + g.Width + 8, g.Y, SidebarW, g.Height);
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Medieval War", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", "End Turn", "Help" }, -1);

        DrawGrid(panelOffset);
        DrawSidebar(panelOffset);

        if (_gameOver)
        {
            var bannerR = new Rectangle(
                panelOffset.X + PanelSize.X / 2 - 200,
                panelOffset.Y + PanelSize.Y / 2 - 50,
                400, 100);
            RetroSkin.DrawRaised(bannerR);
            string msg = _winner == Player ? "Victory!" : "Defeat.";
            int mw = FontManager.MeasureText(msg, 28);
            FontManager.DrawText(msg,
                (int)(bannerR.X + (bannerR.Width - mw) / 2),
                (int)(bannerR.Y + 20),
                28, RetroSkin.BodyText);
            const string hint = "Press New for another scenario.";
            int hw = FontManager.MeasureText(hint, 14);
            FontManager.DrawText(hint,
                (int)(bannerR.X + (bannerR.Width - hw) / 2),
                (int)(bannerR.Y + 64),
                14, RetroSkin.DisabledText);
        }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(status, _status, $"Turn {_turn}  {(_playerTurn ? "(yours)" : "(AI)")}");
    }

    private void DrawGrid(Vector2 panelOffset)
    {
        var g = GridRect();
        var gAbs = new Rectangle(panelOffset.X + g.X, panelOffset.Y + g.Y, g.Width, g.Height);
        Raylib.DrawRectangleRec(gAbs, RetroSkin.DarkShadow);

        for (int x = 0; x < GridCols; x++)
        {
            for (int y = 0; y < GridRows; y++)
            {
                float px = gAbs.X + x * Tile;
                float py = gAbs.Y + y * Tile;
                if (!_seen[x, y])
                {
                    Raylib.DrawRectangle((int)px, (int)py, Tile, Tile, new Color((byte)12, (byte)16, (byte)28, (byte)255));
                    continue;
                }
                Color terrCol = _terrain[x, y] switch
                {
                    Terrain.Water    => new Color((byte) 88, (byte)132, (byte)200, (byte)255),
                    Terrain.Plain    => new Color((byte)188, (byte)204, (byte)136, (byte)255),
                    Terrain.Forest   => new Color((byte) 76, (byte)148, (byte) 88, (byte)255),
                    Terrain.Mountain => new Color((byte)140, (byte)128, (byte)112, (byte)255),
                    _                => RetroSkin.Face,
                };
                Raylib.DrawRectangle((int)px, (int)py, Tile, Tile, terrCol);
                // Terrain accent.
                if (_terrain[x, y] == Terrain.Forest)
                    Raylib.DrawCircle((int)(px + Tile / 2), (int)(py + Tile / 2), 4, new Color((byte)44, (byte)96, (byte)56, (byte)255));
                if (_terrain[x, y] == Terrain.Mountain)
                {
                    Raylib.DrawTriangle(
                        new Vector2(px + Tile / 2, py + 6),
                        new Vector2(px + Tile - 6, py + Tile - 6),
                        new Vector2(px + 6, py + Tile - 6),
                        new Color((byte)96, (byte)84, (byte)68, (byte)255));
                }
                Raylib.DrawRectangleLines((int)px, (int)py, Tile, Tile, new Color((byte)0, (byte)0, (byte)0, (byte)40));
            }
        }

        // Cities
        foreach (var c in _cities)
        {
            if (!_seen[c.X, c.Y]) continue;
            float px = gAbs.X + c.X * Tile;
            float py = gAbs.Y + c.Y * Tile;
            Color col = c.Owner switch
            {
                Player => new Color((byte)80, (byte)132, (byte)232, (byte)255),
                Ai     => new Color((byte)220, (byte)80, (byte)80, (byte)255),
                _      => new Color((byte)240, (byte)240, (byte)240, (byte)255),
            };
            // L-shaped structure per the doc.
            Raylib.DrawRectangle((int)(px + 6), (int)(py + 6), Tile - 12, Tile - 12, col);
            Raylib.DrawRectangle((int)(px + 6), (int)(py + 6), 5, Tile - 12, new Color((byte)0, (byte)0, (byte)0, (byte)100));
            Raylib.DrawRectangle((int)(px + 6), (int)(py + Tile - 11), Tile - 12, 5, new Color((byte)0, (byte)0, (byte)0, (byte)100));
            Raylib.DrawRectangleLines((int)(px + 6), (int)(py + 6), Tile - 12, Tile - 12, RetroSkin.DarkShadow);
        }

        // Units
        for (int i = 0; i < _units.Count; i++)
        {
            var u = _units[i];
            // Only show enemy units in currently-revealed (adjacent to friendlies) tiles.
            bool currentlyVisible = u.Owner == Player || AdjacentToFriendly(u.X, u.Y);
            if (!_seen[u.X, u.Y] || !currentlyVisible) continue;
            float px = gAbs.X + u.X * Tile;
            float py = gAbs.Y + u.Y * Tile;
            Color col = u.Owner == Player
                ? new Color((byte)40, (byte)100, (byte)200, (byte)255)
                : new Color((byte)180, (byte)40, (byte)40, (byte)255);
            DrawUnitGlyph(u.Type, px, py, col);
            if (i == _selectedUnitIdx)
            {
                Raylib.DrawRectangleLines((int)px + 1, (int)py + 1, Tile - 2, Tile - 2, new Color((byte)255, (byte)232, (byte)96, (byte)255));
            }
        }
    }

    private bool AdjacentToFriendly(int x, int y)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                int nx = x + dx, ny = y + dy;
                if (_units.Any(u => u.Owner == Player && u.X == nx && u.Y == ny)) return true;
                if (_cities.Any(c => c.Owner == Player && c.X == nx && c.Y == ny)) return true;
            }
        return false;
    }

    private static void DrawUnitGlyph(UnitType t, float px, float py, Color col)
    {
        float cx = px + Tile / 2f;
        float cy = py + Tile / 2f;
        switch (t)
        {
            case UnitType.LightInfantry:
                Raylib.DrawCircle((int)cx, (int)cy, 8, col);
                Raylib.DrawRectangle((int)cx - 1, (int)cy - 10, 2, 8, col);  // spear
                break;
            case UnitType.Cavalry:
                Raylib.DrawRectangle((int)px + 8, (int)py + 10, Tile - 16, Tile - 18, col);
                Raylib.DrawCircle((int)px + 10, (int)cy, 4, col);  // head
                Raylib.DrawRectangle((int)px + 6, (int)py + Tile - 9, 3, 5, col);
                Raylib.DrawRectangle((int)px + Tile - 9, (int)py + Tile - 9, 3, 5, col);
                break;
            case UnitType.Galleon:
                // Hull
                Raylib.DrawTriangle(
                    new Vector2(px + 4, cy + 4),
                    new Vector2(px + Tile - 4, cy + 4),
                    new Vector2(px + Tile - 8, cy + 10),
                    col);
                Raylib.DrawTriangle(
                    new Vector2(px + 4, cy + 4),
                    new Vector2(px + Tile - 8, cy + 10),
                    new Vector2(px + 8, cy + 10),
                    col);
                // Mast + sail
                Raylib.DrawRectangle((int)cx - 1, (int)py + 6, 2, (int)(cy - py - 2), col);
                Raylib.DrawTriangle(
                    new Vector2(cx, py + 6),
                    new Vector2(cx + 8, cy + 2),
                    new Vector2(cx, cy + 2),
                    col);
                break;
            case UnitType.Catapult:
                Raylib.DrawRectangle((int)px + 6, (int)cy + 2, Tile - 12, 6, col);
                Raylib.DrawCircle((int)px + 8, (int)cy + 9, 3, col);
                Raylib.DrawCircle((int)px + Tile - 8, (int)cy + 9, 3, col);
                Raylib.DrawLineEx(new Vector2(px + 10, cy + 2), new Vector2(cx, py + 6), 2, col);
                Raylib.DrawCircle((int)cx, (int)py + 5, 3, col);
                break;
        }
    }

    private void DrawSidebar(Vector2 panelOffset)
    {
        var sb = SidebarRect();
        var sbAbs = new Rectangle(panelOffset.X + sb.X, panelOffset.Y + sb.Y, sb.Width, sb.Height);
        RetroSkin.DrawSunken(sbAbs, RetroSkin.SunkenBg);

        int y = (int)sbAbs.Y + 12;
        FontManager.DrawText("MEDIEVAL WAR", (int)sbAbs.X + 12, y, 18, RetroSkin.BodyText);
        y += 26;

        if (_selectedUnitIdx >= 0 && _selectedUnitIdx < _units.Count)
        {
            var u = _units[_selectedUnitIdx];
            var s = Stats[u.Type];
            FontManager.DrawText(s.Name, (int)sbAbs.X + 12, y, 16, RetroSkin.BodyText); y += 20;
            FontManager.DrawText($"Move {s.Move}  Atk {s.Atk}  Def {s.Def}",
                (int)sbAbs.X + 12, y, 13, RetroSkin.BodyText); y += 16;
            FontManager.DrawText($"Moves left: {u.MovesLeft}",
                (int)sbAbs.X + 12, y, 13, RetroSkin.BodyText); y += 16;
            if (u.Cargo != null)
            {
                FontManager.DrawText($"Carrying: {Stats[u.Cargo.Type].Name}",
                    (int)sbAbs.X + 12, y, 13, RetroSkin.BodyText); y += 16;
            }
            y += 10;
        }
        else if (_selectedCityIdx >= 0 && _selectedCityIdx < _cities.Count)
        {
            var c = _cities[_selectedCityIdx];
            var s = Stats[c.Producing];
            FontManager.DrawText("CITY (yours)", (int)sbAbs.X + 12, y, 16, RetroSkin.BodyText); y += 20;
            FontManager.DrawText($"Building: {s.Name}", (int)sbAbs.X + 12, y, 13, RetroSkin.BodyText); y += 16;
            FontManager.DrawText($"Progress: {c.TurnsInvested}/{s.Build}",
                (int)sbAbs.X + 12, y, 13, RetroSkin.BodyText); y += 16;
            FontManager.DrawText("Click again to change build",
                (int)sbAbs.X + 12, y, 12, RetroSkin.DisabledText); y += 18;
        }
        else
        {
            FontManager.DrawText("Click a unit or city.",
                (int)sbAbs.X + 12, y, 13, RetroSkin.DisabledText); y += 18;
        }

        // Score panel
        int pCities = _cities.Count(c => c.Owner == Player);
        int aCities = _cities.Count(c => c.Owner == Ai);
        int pUnits = _units.Count(u => u.Owner == Player);
        int aUnits = _units.Count(u => u.Owner == Ai);
        y += 12;
        FontManager.DrawText("STANDINGS", (int)sbAbs.X + 12, y, 14, RetroSkin.BodyText); y += 18;
        FontManager.DrawText($"You   cities {pCities}  units {pUnits}",
            (int)sbAbs.X + 12, y, 13,
            new Color((byte)40, (byte)100, (byte)200, (byte)255)); y += 16;
        FontManager.DrawText($"AI    cities {aCities}  units {aUnits}",
            (int)sbAbs.X + 12, y, 13,
            new Color((byte)180, (byte)40, (byte)40, (byte)255)); y += 16;
        int neutral = _cities.Count(c => c.Owner == -1);
        FontManager.DrawText($"Neutral cities: {neutral}",
            (int)sbAbs.X + 12, y, 13, RetroSkin.BodyText); y += 24;

        // Legend
        FontManager.DrawText("UNITS", (int)sbAbs.X + 12, y, 14, RetroSkin.BodyText); y += 18;
        foreach (var kv in Stats)
        {
            FontManager.DrawText($"{kv.Value.Name}",
                (int)sbAbs.X + 32, y, 12, RetroSkin.BodyText);
            DrawUnitGlyph(kv.Key, sbAbs.X + 4, y - 6,
                new Color((byte)40, (byte)100, (byte)200, (byte)255));
            FontManager.DrawText($"({kv.Value.Build}t)",
                (int)(sbAbs.X + sb.Width - 36), y, 12, RetroSkin.DisabledText);
            y += 18;
        }
    }

    private void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
