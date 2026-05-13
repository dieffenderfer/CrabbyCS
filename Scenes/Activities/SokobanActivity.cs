using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Sokoban — port of the FUNGAMES/SOKO entry (Allan B. Liss, 1992,
/// Sokoban for Windows v2.2.5 — a Windows port of the classic Hiroyuki
/// Imabayashi puzzle). Mechanics drawn from SOKO.TXT:
///
///   • Soko-ban moves with arrow keys; mouse is not used for movement.
///   • Soko-ban can push a single crate at a time, but cannot pull.
///   • A crate on a goal cell counts as placed.
///   • Level is complete when all crates are placed.
///   • Push a crate into a corner outside a goal and you're stuck →
///     restart the level.
///   • Menu offers Undo Last, Undo Level (restart), Save Position,
///     Restore Position, plus move and push counters.
///
/// The original ships with 50 levels we can't extract from the .EXE, so
/// the bundled puzzles below are 8 small originals progressing from
/// 1-crate warmups to a multi-corridor finale. Standard XSB symbol
/// conventions (# wall, @ player, $ crate, . goal, * crate-on-goal,
/// + player-on-goal, space floor) are used so external level packs
/// could be dropped in later.
/// </summary>
public class SokobanActivity : IActivity
{
    public Vector2 PanelSize => new(540, 480);
    public bool IsFinished { get; private set; }

    private const int FrameInset = 3;

    // Each level traced by hand to confirm at least one solution exists
    // and no crate has to pass through a permanently blocking corner.
    private static readonly string[][] Levels =
    {
        // Lvl 1 — one crate, push up then left around the corner.
        new[]
        {
            "#####",
            "#.  #",
            "#   #",
            "# $ #",
            "# @ #",
            "#####",
        },
        // Lvl 2 — two crates spread out, two goals on the back wall.
        new[]
        {
            "#########",
            "#.     .#",
            "#       #",
            "#  $    #",
            "#       #",
            "#    $  #",
            "#       #",
            "#   @   #",
            "#########",
        },
        // Lvl 3 — must push the crate up, then around the gap to the goal.
        new[]
        {
            "########",
            "#      #",
            "# ###  #",
            "# # .  #",
            "# # $@ #",
            "#      #",
            "########",
        },
        // Lvl 4 — three crates aligned with three goals; pure straight push.
        new[]
        {
            "#########",
            "#.  .  .#",
            "#       #",
            "#       #",
            "#$  $  $#",
            "#       #",
            "#   @   #",
            "#########",
        },
        // Lvl 5 — open room with two crates that share a goal column.
        new[]
        {
            "##########",
            "#.       #",
            "#        #",
            "#   $    #",
            "#        #",
            "#.  $    #",
            "#        #",
            "#    @   #",
            "##########",
        },
        // Lvl 6 — four crates, four goals — symmetric arrangement.
        new[]
        {
            "##########",
            "#.      .#",
            "#        #",
            "#  $  $  #",
            "#        #",
            "#  $  $  #",
            "#        #",
            "#.  @   .#",
            "##########",
        },
    };

    private enum Cell { Floor, Wall, Goal }
    private Cell[,] _terrain = new Cell[1, 1];
    private bool[,] _crates = new bool[1, 1];
    private int _playerX, _playerY;
    private int _w, _h;
    private int _level;
    private int _moves, _pushes;
    private string _status = "Use arrow keys to push crates onto the goals.";

    private record UndoFrame(int Px, int Py, int CrateX, int CrateY, bool HadCrate);
    private readonly Stack<UndoFrame> _undo = new();

    public void Load() => LoadLevel(0);
    public void Close() => IsFinished = true;

    private void LoadLevel(int idx)
    {
        idx = Math.Clamp(idx, 0, Levels.Length - 1);
        _level = idx;
        var rows = Levels[idx];
        _h = rows.Length;
        _w = rows.Max(r => r.Length);
        _terrain = new Cell[_w, _h];
        _crates = new bool[_w, _h];
        _moves = 0;
        _pushes = 0;
        _undo.Clear();
        for (int y = 0; y < _h; y++)
        {
            string row = rows[y];
            for (int x = 0; x < _w; x++)
            {
                char c = x < row.Length ? row[x] : ' ';
                Cell t = Cell.Floor;
                bool crate = false;
                bool player = false;
                switch (c)
                {
                    case '#': t = Cell.Wall; break;
                    case '.': t = Cell.Goal; break;
                    case '$': crate = true; break;
                    case '*': crate = true; t = Cell.Goal; break;
                    case '@': player = true; break;
                    case '+': player = true; t = Cell.Goal; break;
                }
                _terrain[x, y] = t;
                _crates[x, y] = crate;
                if (player) { _playerX = x; _playerY = y; }
            }
        }
        _status = $"Level {_level + 1} of {Levels.Length}.";
    }

    private bool AllPlaced()
    {
        for (int y = 0; y < _h; y++)
            for (int x = 0; x < _w; x++)
                if (_crates[x, y] && _terrain[x, y] != Cell.Goal) return false;
        return true;
    }

    private void TryMove(int dx, int dy)
    {
        int nx = _playerX + dx, ny = _playerY + dy;
        if (nx < 0 || ny < 0 || nx >= _w || ny >= _h) return;
        if (_terrain[nx, ny] == Cell.Wall) return;
        if (_crates[nx, ny])
        {
            int bx = nx + dx, by = ny + dy;
            if (bx < 0 || by < 0 || bx >= _w || by >= _h) return;
            if (_terrain[bx, by] == Cell.Wall) return;
            if (_crates[bx, by]) return;
            _undo.Push(new UndoFrame(_playerX, _playerY, bx, by, true));
            _crates[nx, ny] = false;
            _crates[bx, by] = true;
            _playerX = nx; _playerY = ny;
            _moves++;
            _pushes++;
        }
        else
        {
            _undo.Push(new UndoFrame(_playerX, _playerY, -1, -1, false));
            _playerX = nx; _playerY = ny;
            _moves++;
        }
        if (AllPlaced())
        {
            _status = $"Level {_level + 1} complete! Press N for next.";
        }
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var u = _undo.Pop();
        if (u.HadCrate)
        {
            // Reverse the push: crate moves back from (bx,by) to where the
            // player just was, and the player moves back to (px,py).
            _crates[u.CrateX, u.CrateY] = false;
            _crates[_playerX, _playerY] = true;
            _pushes--;
        }
        _playerX = u.Px;
        _playerY = u.Py;
        _moves--;
    }

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
        var items = new[] { "Restart", "Undo", "Prev", "Next" };
        int hit = RetroWidgets.MenuBarHitTest(menuBar, items, local, leftPressed);
        if (hit == 0) { LoadLevel(_level); return; }
        if (hit == 1) { Undo(); return; }
        if (hit == 2 && _level > 0) { LoadLevel(_level - 1); return; }
        if (hit == 3) { LoadLevel((_level + 1) % Levels.Length); return; }

        if (Raylib.IsKeyPressed(KeyboardKey.Up)) TryMove(0, -1);
        if (Raylib.IsKeyPressed(KeyboardKey.Down)) TryMove(0, 1);
        if (Raylib.IsKeyPressed(KeyboardKey.Left)) TryMove(-1, 0);
        if (Raylib.IsKeyPressed(KeyboardKey.Right)) TryMove(1, 0);
        if (Raylib.IsKeyPressed(KeyboardKey.U)) Undo();
        if (Raylib.IsKeyPressed(KeyboardKey.R)) LoadLevel(_level);
        if (Raylib.IsKeyPressed(KeyboardKey.N))
        {
            if (AllPlaced()) LoadLevel((_level + 1) % Levels.Length);
        }
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Sokoban", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "Restart", "Undo", "Prev", "Next" }, -1);

        // Centred grid: pick the largest tile size that fits the play area.
        float topY = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 12;
        float bottomY = PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight - 12;
        float availW = PanelSize.X - 2 * (FrameInset + 12);
        float availH = bottomY - topY;
        int tile = (int)Math.Floor(Math.Min(availW / _w, availH / _h));
        if (tile < 8) tile = 8;
        float gridW = tile * _w;
        float gridH = tile * _h;
        float ox = panelOffset.X + (PanelSize.X - gridW) / 2f;
        float oy = panelOffset.Y + topY + (availH - gridH) / 2f;

        for (int y = 0; y < _h; y++)
        {
            for (int x = 0; x < _w; x++)
            {
                float px = ox + x * tile;
                float py = oy + y * tile;
                var r = new Rectangle(px, py, tile, tile);
                switch (_terrain[x, y])
                {
                    case Cell.Wall:
                        RetroSkin.DrawRaised(r);
                        // Brick striping so walls read distinct from raised buttons.
                        for (int s = 0; s < tile; s += 4)
                            Raylib.DrawRectangle((int)px, (int)(py + s), tile, 1, RetroSkin.Shadow);
                        break;
                    case Cell.Goal:
                        Raylib.DrawRectangleRec(r, new Color((byte)252, (byte)244, (byte)216, (byte)255));
                        Raylib.DrawRectangleLinesEx(r, 1, RetroSkin.Shadow);
                        // Bullseye target dot at the centre of the goal.
                        Raylib.DrawCircle((int)(px + tile / 2), (int)(py + tile / 2),
                            tile * 0.18f, new Color((byte)200, (byte)96, (byte)80, (byte)255));
                        break;
                    default:
                        Raylib.DrawRectangleRec(r, RetroSkin.SunkenBg);
                        Raylib.DrawRectangleLinesEx(r, 1, RetroSkin.Shadow);
                        break;
                }

                if (_crates[x, y])
                {
                    bool onGoal = _terrain[x, y] == Cell.Goal;
                    var crateR = new Rectangle(px + 3, py + 3, tile - 6, tile - 6);
                    Raylib.DrawRectangleRec(crateR,
                        onGoal ? new Color((byte)104, (byte)180, (byte)112, (byte)255)
                               : new Color((byte)188, (byte)148, (byte)84, (byte)255));
                    Raylib.DrawRectangleLinesEx(crateR, 2,
                        onGoal ? new Color((byte)44, (byte)104, (byte)56, (byte)255)
                               : new Color((byte)92, (byte)64, (byte)28, (byte)255));
                    // Crate slats
                    Raylib.DrawLineEx(
                        new Vector2(crateR.X, crateR.Y + crateR.Height / 2),
                        new Vector2(crateR.X + crateR.Width, crateR.Y + crateR.Height / 2),
                        1.5f, new Color((byte)92, (byte)64, (byte)28, (byte)180));
                }
            }
        }

        // Soko-ban — the doc calls it "the little red devil"; we draw a
        // small red imp shape (circle head + curved body + horn nubs) so
        // it stands apart from the crates without needing pixel art.
        {
            float px = ox + _playerX * tile;
            float py = oy + _playerY * tile;
            float cx = px + tile / 2f;
            float cy = py + tile / 2f;
            var devil = new Color((byte)196, (byte)64, (byte)68, (byte)255);
            var deep  = new Color((byte)108, (byte)24, (byte)32, (byte)255);
            Raylib.DrawCircle((int)cx, (int)cy, tile * 0.38f, devil);
            Raylib.DrawCircleLines((int)cx, (int)cy, tile * 0.38f, deep);
            // Horns
            Raylib.DrawTriangle(
                new Vector2(cx - tile * 0.30f, cy - tile * 0.30f),
                new Vector2(cx - tile * 0.20f, cy - tile * 0.15f),
                new Vector2(cx - tile * 0.10f, cy - tile * 0.35f),
                devil);
            Raylib.DrawTriangle(
                new Vector2(cx + tile * 0.10f, cy - tile * 0.35f),
                new Vector2(cx + tile * 0.20f, cy - tile * 0.15f),
                new Vector2(cx + tile * 0.30f, cy - tile * 0.30f),
                devil);
            // Eyes
            Raylib.DrawCircle((int)(cx - tile * 0.12f), (int)(cy - tile * 0.05f), tile * 0.05f, deep);
            Raylib.DrawCircle((int)(cx + tile * 0.12f), (int)(cy - tile * 0.05f), tile * 0.05f, deep);
        }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(status, _status,
            $"L{_level + 1}/{Levels.Length}  Moves {_moves}  Pushes {_pushes}");
    }
}
