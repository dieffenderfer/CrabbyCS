using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Falling-block puzzle. Standard 10×20 well, seven tetromino shapes drawn
/// from a 7-bag randomizer, simple rotation (no SRS kicks — the 1990 Windows
/// version predated SRS), level-based gravity, line-clear scoring.
///
/// Controls: Left / Right / Down arrows, Up rotates, Space hard-drops, P pauses.
/// </summary>
public class TetrisActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Cols = 10;
    private const int Rows = 20;
    private const int Cell = 16;
    private const int SidePanelW = 96;
    private const int Margin = 12;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + Cols * Cell + Margin + SidePanelW,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + 2 * Margin + Rows * Cell + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private static readonly Color[] Colors =
    {
        new(  0, 192, 192, 255), // I cyan
        new(224, 224,   0, 255), // O yellow
        new(176,   0, 192, 255), // T magenta
        new(  0, 192,   0, 255), // S green
        new(192,   0,   0, 255), // Z red
        new(224, 128,   0, 255), // L orange
        new(  0,   0, 192, 255), // J blue
    };

    private int[,] _well = new int[Cols, Rows]; // 0 empty, 1..7 piece+1
    private int _curType, _curRot, _curX, _curY;
    private int _nextType;
    private int[] _bag = Array.Empty<int>();
    private int _bagIdx;

    private float _gravityTimer;
    private int _level = 1;
    private int _score;
    private int _lines;
    private bool _gameOver;
    private bool _paused;
    private float _flashTimer;
    private List<int> _flashRows = new();

    private readonly Random _rng = new();

    public void Load() => Reset();

    private void Reset()
    {
        for (int x = 0; x < Cols; x++) for (int y = 0; y < Rows; y++) _well[x, y] = 0;
        _bag = NewBag(); _bagIdx = 0;
        _nextType = NextFromBag();
        SpawnNext();
        _gravityTimer = 0;
        _level = 1; _score = 0; _lines = 0;
        _gameOver = false; _paused = false;
        _flashTimer = 0; _flashRows.Clear();
    }

    private int[] NewBag()
    {
        var b = new[] { 0, 1, 2, 3, 4, 5, 6 };
        for (int i = b.Length - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (b[i], b[j]) = (b[j], b[i]);
        }
        return b;
    }

    private int NextFromBag()
    {
        if (_bagIdx >= _bag.Length) { _bag = NewBag(); _bagIdx = 0; }
        return _bag[_bagIdx++];
    }

    private void SpawnNext()
    {
        _curType = _nextType;
        _nextType = NextFromBag();
        _curRot = 0;
        _curX = Cols / 2 - 2;
        _curY = -1;
        if (Collides(_curType, _curRot, _curX, _curY + 1)) _gameOver = true;
    }

    private bool Collides(int type, int rot, int px, int py)
    {
        var s = Shapes[type];
        for (int i = 0; i < 4; i++)
        {
            int rx = s[rot * 8 + i * 2 + 0] + px;
            int ry = s[rot * 8 + i * 2 + 1] + py;
            if (rx < 0 || rx >= Cols || ry >= Rows) return true;
            if (ry < 0) continue;
            if (_well[rx, ry] != 0) return true;
        }
        return false;
    }

    private void Lock()
    {
        var s = Shapes[_curType];
        for (int i = 0; i < 4; i++)
        {
            int rx = s[_curRot * 8 + i * 2 + 0] + _curX;
            int ry = s[_curRot * 8 + i * 2 + 1] + _curY;
            if (ry >= 0 && ry < Rows && rx >= 0 && rx < Cols) _well[rx, ry] = _curType + 1;
        }
        ClearLines();
        SpawnNext();
    }

    private void ClearLines()
    {
        _flashRows.Clear();
        for (int y = Rows - 1; y >= 0; y--)
        {
            bool full = true;
            for (int x = 0; x < Cols; x++) if (_well[x, y] == 0) { full = false; break; }
            if (full) _flashRows.Add(y);
        }
        if (_flashRows.Count == 0) return;

        // Apply scoring + collapse
        int n = _flashRows.Count;
        _score += n switch { 1 => 100, 2 => 300, 3 => 500, _ => 800 } * _level;
        _lines += n;
        _level = 1 + _lines / 10;

        // Compact: write rows that aren't cleared into a fresh stack from bottom up.
        var temp = new int[Cols, Rows];
        int writeY = Rows - 1;
        for (int y = Rows - 1; y >= 0; y--)
        {
            if (_flashRows.Contains(y)) continue;
            for (int x = 0; x < Cols; x++) temp[x, writeY] = _well[x, y];
            writeY--;
        }
        _well = temp;
        _flashTimer = 0.15f;
    }

    private float GravityInterval()
    {
        // Roughly NES Tetris frame counts → seconds
        float[] table = { 0.80f, 0.72f, 0.63f, 0.55f, 0.47f, 0.38f, 0.30f, 0.22f, 0.13f, 0.10f, 0.08f };
        int i = Math.Min(_level - 1, table.Length - 1);
        return Math.Max(0.05f, table[i]);
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
        int m = RetroWidgets.MenuBarHitTest(menuBar, new[] { "New", _paused ? "Resume" : "Pause" }, local, leftPressed);
        if (m == 0) { Reset(); return; }
        if (m == 1) { _paused = !_paused; return; }

        if (_gameOver || _paused) return;

        if (_flashTimer > 0)
        {
            _flashTimer -= delta;
            return;
        }

        if (Raylib.IsKeyPressed(KeyboardKey.Left) && !Collides(_curType, _curRot, _curX - 1, _curY)) _curX--;
        if (Raylib.IsKeyPressed(KeyboardKey.Right) && !Collides(_curType, _curRot, _curX + 1, _curY)) _curX++;
        if (Raylib.IsKeyPressed(KeyboardKey.Up))
        {
            int nr = (_curRot + 1) % (Shapes[_curType].Length / 8);
            if (!Collides(_curType, nr, _curX, _curY)) _curRot = nr;
        }
        if (Raylib.IsKeyPressed(KeyboardKey.P)) _paused = true;
        if (Raylib.IsKeyPressed(KeyboardKey.Space))
        {
            while (!Collides(_curType, _curRot, _curX, _curY + 1)) { _curY++; _score += 2; }
            Lock(); return;
        }

        float interval = Raylib.IsKeyDown(KeyboardKey.Down) ? Math.Min(0.05f, GravityInterval()) : GravityInterval();
        _gravityTimer += delta;
        if (_gravityTimer >= interval)
        {
            _gravityTimer = 0;
            if (Collides(_curType, _curRot, _curX, _curY + 1)) Lock();
            else _curY++;
        }
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Tetris", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", _paused ? "Resume" : "Pause" }, -1);

        // Well
        float wx = panelOffset.X + FrameInset + Margin;
        float wy = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
        var wellRect = new Rectangle(wx - 2, wy - 2, Cols * Cell + 4, Rows * Cell + 4);
        RetroSkin.DrawSunken(wellRect, new Color(0, 0, 0, 255));

        for (int x = 0; x < Cols; x++)
            for (int y = 0; y < Rows; y++)
                if (_well[x, y] != 0)
                    DrawBlock((int)wx + x * Cell, (int)wy + y * Cell, Colors[_well[x, y] - 1]);

        if (!_gameOver && _flashTimer <= 0)
        {
            var s = Shapes[_curType];
            for (int i = 0; i < 4; i++)
            {
                int rx = s[_curRot * 8 + i * 2 + 0] + _curX;
                int ry = s[_curRot * 8 + i * 2 + 1] + _curY;
                if (ry < 0) continue;
                DrawBlock((int)wx + rx * Cell, (int)wy + ry * Cell, Colors[_curType]);
            }
        }

        // Side panel: NEXT, score, level, lines
        float sx = wx + Cols * Cell + Margin;
        var sideRect = new Rectangle(sx, wy, SidePanelW, Rows * Cell);
        RetroSkin.DrawSunken(sideRect, RetroSkin.Face);

        RetroSkin.DrawText("NEXT", (int)sx + 8, (int)wy + 8, RetroSkin.BodyText);
        // Center the next-piece preview in a 4×4 box
        var sNext = Shapes[_nextType];
        for (int i = 0; i < 4; i++)
        {
            int rx = sNext[i * 2 + 0];
            int ry = sNext[i * 2 + 1];
            DrawBlock((int)sx + 16 + rx * Cell, (int)wy + 28 + ry * Cell, Colors[_nextType]);
        }

        int infoY = (int)wy + 28 + 5 * Cell;
        RetroSkin.DrawText($"SCORE", (int)sx + 8, infoY, RetroSkin.BodyText);
        RetroSkin.DrawText($"{_score}", (int)sx + 8, infoY + 16, RetroSkin.BodyText);
        RetroSkin.DrawText($"LINES", (int)sx + 8, infoY + 40, RetroSkin.BodyText);
        RetroSkin.DrawText($"{_lines}", (int)sx + 8, infoY + 56, RetroSkin.BodyText);
        RetroSkin.DrawText($"LEVEL", (int)sx + 8, infoY + 80, RetroSkin.BodyText);
        RetroSkin.DrawText($"{_level}", (int)sx + 8, infoY + 96, RetroSkin.BodyText);

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string state = _gameOver ? "Game over" : _paused ? "Paused" : "← → ↓ move  ↑ rotate  Space drop  P pause";
        RetroWidgets.StatusBar(status, state, $"L{_level}");
    }

    private static void DrawBlock(int x, int y, Color col)
    {
        var rect = new Rectangle(x, y, Cell, Cell);
        Raylib.DrawRectangleRec(rect, col);
        // Bevel
        Raylib.DrawRectangle(x, y, Cell, 2, new Color(255, 255, 255, 100));
        Raylib.DrawRectangle(x, y, 2, Cell, new Color(255, 255, 255, 100));
        Raylib.DrawRectangle(x, y + Cell - 2, Cell, 2, new Color(0, 0, 0, 100));
        Raylib.DrawRectangle(x + Cell - 2, y, 2, Cell, new Color(0, 0, 0, 100));
    }

    // Per-piece rotation tables, packed as rotations × 8 ints (4 cells × (x, y)).
    private static readonly int[][] Shapes = new int[][]
    {
        // I — 2 rotations
        new[] { 0,1, 1,1, 2,1, 3,1,   2,0, 2,1, 2,2, 2,3 },
        // O — 1 rotation
        new[] { 1,0, 2,0, 1,1, 2,1 },
        // T — 4 rotations
        new[] { 0,1, 1,1, 2,1, 1,0,
                1,0, 1,1, 1,2, 2,1,
                0,1, 1,1, 2,1, 1,2,
                1,0, 1,1, 1,2, 0,1 },
        // S — 2 rotations
        new[] { 1,1, 2,1, 0,2, 1,2,
                1,0, 1,1, 2,1, 2,2 },
        // Z — 2 rotations
        new[] { 0,1, 1,1, 1,2, 2,2,
                2,0, 1,1, 2,1, 1,2 },
        // L — 4 rotations
        new[] { 0,1, 1,1, 2,1, 2,0,
                1,0, 1,1, 1,2, 2,2,
                0,1, 1,1, 2,1, 0,2,
                0,0, 1,0, 1,1, 1,2 },
        // J — 4 rotations
        new[] { 0,0, 0,1, 1,1, 2,1,
                1,0, 2,0, 1,1, 1,2,
                0,1, 1,1, 2,1, 2,2,
                1,0, 1,1, 0,2, 1,2 },
    };

    public void Close() { }
}
