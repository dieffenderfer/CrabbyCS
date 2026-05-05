using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Original tile-puzzle in the spirit of the genre — you guide a chip-collector
/// across a grid, picking up chips, using keys to open same-colored doors, and
/// reaching the exit once every chip on the level is collected. Bumpers act as
/// pushable blocks; ooze tiles end the run. Three original built-in levels.
/// </summary>
public class ChipsChallengeActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Margin = 12;
    private const int Cell = 24;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + 18 * Cell,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + 14 * Cell + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    // Tile codes
    private const char Floor = '.';
    private const char Wall = '#';
    private const char Chip = 'c';
    private const char Bump = 'B';
    private const char Door = 'D';
    private const char Key = 'k';
    private const char Exit = 'X';
    private const char Ooze = '~';
    private const char Player = 'P';

    // Three original levels (handcrafted for this implementation; each is
    // 18 cols × 14 rows). Symbols match the constants above; spaces become
    // floor on parse.
    private static readonly string[][] Levels = new[]
    {
        new[]
        {
            "##################",
            "#P  c   #   c    #",
            "#   #   D   #    #",
            "#   #   #   #    #",
            "#   #c  #   #c   #",
            "#####   #####    #",
            "#               k#",
            "# c             ##",
            "#   ###   #####  #",
            "#   #X#   #   #c #",
            "#   ###   # c #  #",
            "#         #####  #",
            "# c              #",
            "##################",
        },
        new[]
        {
            "##################",
            "#P  ~~~     B    #",
            "#   ~~~     B  c #",
            "#           B    #",
            "#  c        B    #",
            "#####D########## #",
            "#                #",
            "#  c   k       c #",
            "#                #",
            "#####D########## #",
            "#                #",
            "#       c     X k#",
            "#                #",
            "##################",
        },
        new[]
        {
            "##################",
            "#P    c c c   k  #",
            "#  ###########D###",
            "#  #~~~~~~~~~~~  #",
            "#  #~~~~~~~~~~~  #",
            "#  ###############",
            "#                #",
            "# k    BBBBB     #",
            "#      B   B  c  #",
            "#  c   B X B     #",
            "#      BBBDB     #",
            "#                #",
            "#  c          c  #",
            "##################",
        },
    };

    private char[,] _grid = new char[18, 14];
    private (int x, int y) _player;
    private int _chipsLeft;
    private int _keys;
    private int _level;
    private float _moveCool;
    private bool _won;
    private bool _dead;

    public void Load() => LoadLevel(0);

    private void LoadLevel(int idx)
    {
        _level = idx;
        var rows = Levels[idx % Levels.Length];
        _grid = new char[18, 14];
        _chipsLeft = 0;
        _keys = 0;
        _won = false;
        _dead = false;
        for (int y = 0; y < 14; y++)
        {
            string row = rows[y].PadRight(18);
            for (int x = 0; x < 18; x++)
            {
                char c = row[x];
                if (c == ' ') c = Floor;
                if (c == Player) { _player = (x, y); _grid[x, y] = Floor; }
                else
                {
                    _grid[x, y] = c;
                    if (c == Chip) _chipsLeft++;
                }
            }
        }
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
        switch (RetroWidgets.MenuBarHitTest(menuBar, new[] { "Restart", "Next" }, local, leftPressed))
        {
            case 0: LoadLevel(_level); return;
            case 1: LoadLevel(_level + 1); return;
        }

        if (_won || _dead) return;

        _moveCool -= delta;
        if (_moveCool > 0) return;

        int dx = 0, dy = 0;
        if (Raylib.IsKeyDown(KeyboardKey.Left)) dx = -1;
        else if (Raylib.IsKeyDown(KeyboardKey.Right)) dx = 1;
        else if (Raylib.IsKeyDown(KeyboardKey.Up)) dy = -1;
        else if (Raylib.IsKeyDown(KeyboardKey.Down)) dy = 1;

        if (dx != 0 || dy != 0) { TryMove(dx, dy); _moveCool = 0.13f; }
    }

    private void TryMove(int dx, int dy)
    {
        int nx = _player.x + dx, ny = _player.y + dy;
        if (nx < 0 || nx >= 18 || ny < 0 || ny >= 14) return;
        char t = _grid[nx, ny];
        if (t == Wall) return;
        if (t == Door)
        {
            if (_keys <= 0) return;
            _keys--;
            _grid[nx, ny] = Floor;
        }
        if (t == Bump)
        {
            int bx = nx + dx, by = ny + dy;
            if (bx < 0 || bx >= 18 || by < 0 || by >= 14) return;
            char b = _grid[bx, by];
            if (b != Floor) return;
            _grid[bx, by] = Bump;
            _grid[nx, ny] = Floor;
        }
        if (t == Chip) { _grid[nx, ny] = Floor; _chipsLeft--; }
        if (t == Key) { _grid[nx, ny] = Floor; _keys++; }
        if (t == Exit) { if (_chipsLeft == 0) _won = true; else return; }
        if (t == Ooze) { _dead = true; }
        _player = (nx, ny);
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Chip's Challenge", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "Restart", "Next" }, -1);

        float bx = panelOffset.X + FrameInset + Margin;
        float by = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;

        for (int y = 0; y < 14; y++)
            for (int x = 0; x < 18; x++)
                DrawTile(bx + x * Cell, by + y * Cell, _grid[x, y]);

        // Player
        int px = (int)(bx + _player.x * Cell + 3);
        int py = (int)(by + _player.y * Cell + 3);
        Raylib.DrawRectangle(px, py, Cell - 6, Cell - 6, new Color(220, 200, 80, 255));
        Raylib.DrawCircle(px + 6, py + 6, 2, RetroSkin.BodyText);
        Raylib.DrawCircle(px + Cell - 12, py + 6, 2, RetroSkin.BodyText);
        Raylib.DrawRectangleLines(px, py, Cell - 6, Cell - 6, RetroSkin.BodyText);

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string state = _won ? "Level cleared! Press Next" : _dead ? "Stuck — Restart" : "← → ↑ ↓ to move";
        RetroWidgets.StatusBar(status, state,
            $"Lvl {_level + 1}/{Levels.Length}   Chips {_chipsLeft}   Keys {_keys}");
    }

    private static void DrawTile(float x, float y, char t)
    {
        int ix = (int)x, iy = (int)y;
        switch (t)
        {
            case Wall:
                Raylib.DrawRectangle(ix, iy, Cell, Cell, new Color(96, 96, 96, 255));
                Raylib.DrawRectangle(ix + 2, iy + 2, Cell - 4, Cell - 4, new Color(160, 160, 160, 255));
                break;
            case Floor:
                Raylib.DrawRectangle(ix, iy, Cell, Cell, new Color(40, 40, 40, 255));
                Raylib.DrawRectangleLines(ix, iy, Cell, Cell, new Color(28, 28, 28, 255));
                break;
            case Chip:
                Raylib.DrawRectangle(ix, iy, Cell, Cell, new Color(40, 40, 40, 255));
                Raylib.DrawCircle(ix + Cell / 2, iy + Cell / 2, Cell / 3, new Color(80, 200, 220, 255));
                Raylib.DrawCircleLines(ix + Cell / 2, iy + Cell / 2, Cell / 3, RetroSkin.BodyText);
                break;
            case Key:
                Raylib.DrawRectangle(ix, iy, Cell, Cell, new Color(40, 40, 40, 255));
                Raylib.DrawCircle(ix + 8, iy + Cell / 2, 4, new Color(232, 200, 0, 255));
                Raylib.DrawRectangle(ix + 12, iy + Cell / 2 - 1, 8, 2, new Color(232, 200, 0, 255));
                Raylib.DrawRectangle(ix + 18, iy + Cell / 2, 4, 3, new Color(232, 200, 0, 255));
                break;
            case Door:
                Raylib.DrawRectangle(ix, iy, Cell, Cell, new Color(184, 100, 40, 255));
                Raylib.DrawRectangle(ix + 4, iy + 4, Cell - 8, Cell - 8, new Color(232, 152, 80, 255));
                Raylib.DrawCircle(ix + Cell - 8, iy + Cell / 2, 2, RetroSkin.BodyText);
                break;
            case Exit:
                for (int cy = 0; cy < Cell; cy += 4)
                    for (int cx = 0; cx < Cell; cx += 4)
                    {
                        bool b = ((cx + cy) / 4) % 2 == 0;
                        Raylib.DrawRectangle(ix + cx, iy + cy, 4, 4, b ? Color.White : Color.Black);
                    }
                break;
            case Ooze:
                Raylib.DrawRectangle(ix, iy, Cell, Cell, new Color(80, 144, 80, 255));
                Raylib.DrawCircle(ix + 6, iy + 6, 3, new Color(40, 96, 40, 255));
                Raylib.DrawCircle(ix + Cell - 8, iy + Cell - 10, 4, new Color(40, 96, 40, 255));
                break;
            case Bump:
                Raylib.DrawRectangle(ix, iy, Cell, Cell, new Color(40, 40, 40, 255));
                Raylib.DrawRectangle(ix + 3, iy + 3, Cell - 6, Cell - 6, new Color(180, 144, 80, 255));
                Raylib.DrawRectangleLines(ix + 3, iy + 3, Cell - 6, Cell - 6, new Color(96, 64, 24, 255));
                break;
        }
    }

    public void Close() { }
}
