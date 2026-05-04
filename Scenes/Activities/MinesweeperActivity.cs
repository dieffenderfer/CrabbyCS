using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

public class MinesweeperActivity : IActivity
{
    private enum Difficulty { Beginner, Intermediate, Expert }

    private const int CellSize = 20;
    private const int GridPad = 6;
    private const int HeaderH = 40;
    private const int FrameInset = 3;

    private static readonly Color[] NumberColors =
    {
        new(  0,   0, 255, 255), // 1 blue
        new(  0, 128,   0, 255), // 2 green
        new(255,   0,   0, 255), // 3 red
        new(  0,   0, 128, 255), // 4 navy
        new(128,   0,   0, 255), // 5 maroon
        new(  0, 128, 128, 255), // 6 teal
        new(  0,   0,   0, 255), // 7 black
        new(128, 128, 128, 255), // 8 grey
    };

    private static readonly Color CounterBg = new(0, 0, 0, 255);
    private static readonly Color CounterFg = new(255, 0, 0, 255);
    private static readonly Color MineRed = new(255, 0, 0, 255);

    private Difficulty _difficulty = Difficulty.Beginner;
    private int _cols = 9, _rows = 9, _mineCount = 10;

    private int[,] _adj = new int[0, 0]; // -1 = mine
    private byte[,] _state = new byte[0, 0]; // 0 hidden, 1 revealed, 2 flag, 3 question
    private bool _placed;
    private bool _gameOver, _won;
    private int _flagsPlaced;
    private float _elapsed;
    private bool _smileyArmed;
    private (int c, int r) _losingCell = (-1, -1);

    private bool _cellHeld;
    private (int c, int r) _heldCell = (-1, -1);

    public bool IsFinished { get; private set; }

    public Vector2 PanelSize
    {
        get
        {
            int w = 2 * FrameInset + GridPad * 2 + _cols * CellSize;
            int minW = 220;
            if (w < minW) w = minW;
            int h = 2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
                  + HeaderH + GridPad * 2 + _rows * CellSize + RetroWidgets.StatusBarHeight;
            return new Vector2(w, h);
        }
    }

    public void Load() => Reset();

    private void ApplyDifficulty(Difficulty d)
    {
        _difficulty = d;
        (_cols, _rows, _mineCount) = d switch
        {
            Difficulty.Beginner     => (9, 9, 10),
            Difficulty.Intermediate => (16, 16, 40),
            Difficulty.Expert       => (30, 16, 99),
            _ => (9, 9, 10),
        };
        Reset();
    }

    private void Reset()
    {
        _adj = new int[_cols, _rows];
        _state = new byte[_cols, _rows];
        _placed = false;
        _gameOver = false;
        _won = false;
        _flagsPlaced = 0;
        _elapsed = 0f;
        _losingCell = (-1, -1);
        _cellHeld = false;
    }

    private void PlaceMines(int safeC, int safeR)
    {
        var rng = new Random();
        int placed = 0;
        int total = _cols * _rows;
        var safeSet = new HashSet<int>();
        // Guarantee the first click + its neighbors are safe (classic behavior).
        for (int dc = -1; dc <= 1; dc++)
            for (int dr = -1; dr <= 1; dr++)
            {
                int c = safeC + dc, r = safeR + dr;
                if (c >= 0 && c < _cols && r >= 0 && r < _rows)
                    safeSet.Add(c * _rows + r);
            }

        while (placed < _mineCount)
        {
            int idx = rng.Next(total);
            if (safeSet.Contains(idx)) continue;
            int c = idx / _rows, r = idx % _rows;
            if (_adj[c, r] == -1) continue;
            _adj[c, r] = -1;
            placed++;
        }

        for (int c = 0; c < _cols; c++)
            for (int r = 0; r < _rows; r++)
            {
                if (_adj[c, r] == -1) continue;
                int n = 0;
                for (int dc = -1; dc <= 1; dc++)
                    for (int dr = -1; dr <= 1; dr++)
                    {
                        if (dc == 0 && dr == 0) continue;
                        int nc = c + dc, nr = r + dr;
                        if (nc < 0 || nc >= _cols || nr < 0 || nr >= _rows) continue;
                        if (_adj[nc, nr] == -1) n++;
                    }
                _adj[c, r] = n;
            }

        _placed = true;
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        if (_placed && !_gameOver && !_won) _elapsed += delta;

        var local = mousePos - panelOffset;

        // Title bar close
        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        {
            IsFinished = true;
            return;
        }

        // Menu bar
        var menuBar = new Rectangle(FrameInset, FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        var menuItems = new[] { "New", "Beginner", "Intermediate", "Expert" };
        int menuClicked = RetroWidgets.MenuBarHitTest(menuBar, menuItems, local, leftPressed);
        switch (menuClicked)
        {
            case 0: Reset(); break;
            case 1: ApplyDifficulty(Difficulty.Beginner); break;
            case 2: ApplyDifficulty(Difficulty.Intermediate); break;
            case 3: ApplyDifficulty(Difficulty.Expert); break;
        }

        // Smiley reset button
        var smiley = SmileyRect();
        if (RetroSkin.PointInRect(local, smiley))
        {
            if (leftPressed) _smileyArmed = true;
            if (leftReleased && _smileyArmed) Reset();
        }
        if (leftReleased) _smileyArmed = false;

        // Grid clicks
        if (!_gameOver && !_won)
        {
            var (c, r) = HitCell(local);
            if (leftPressed && c >= 0)
            {
                _cellHeld = true;
                _heldCell = (c, r);
            }
            if (leftReleased)
            {
                if (_cellHeld && c == _heldCell.c && r == _heldCell.r && c >= 0)
                    Reveal(c, r);
                _cellHeld = false;
                _heldCell = (-1, -1);
            }
            if (rightPressed && c >= 0)
                ToggleFlag(c, r);
        }
        else if (leftReleased)
        {
            _cellHeld = false;
        }
    }

    private Rectangle SmileyRect()
    {
        const int sz = 26;
        float headerY = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight;
        float x = (PanelSize.X - sz) / 2f;
        float y = headerY + (HeaderH - sz) / 2f;
        return new Rectangle(x, y, sz, sz);
    }

    private (int c, int r) HitCell(Vector2 local)
    {
        var origin = GridOrigin();
        int gx = (int)((local.X - origin.X) / CellSize);
        int gy = (int)((local.Y - origin.Y) / CellSize);
        if (local.X < origin.X || local.Y < origin.Y) return (-1, -1);
        if (gx < 0 || gx >= _cols || gy < 0 || gy >= _rows) return (-1, -1);
        return (gx, gy);
    }

    private Vector2 GridOrigin()
    {
        float headerY = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight;
        float gridX = (PanelSize.X - _cols * CellSize) / 2f;
        float gridY = headerY + HeaderH + GridPad;
        return new Vector2(gridX, gridY);
    }

    private void ToggleFlag(int c, int r)
    {
        if (_state[c, r] == 1) return;
        if (_state[c, r] == 0)
        {
            _state[c, r] = 2;
            _flagsPlaced++;
        }
        else if (_state[c, r] == 2)
        {
            _state[c, r] = 3;
            _flagsPlaced--;
        }
        else
        {
            _state[c, r] = 0;
        }
    }

    private void Reveal(int c, int r)
    {
        if (_state[c, r] != 0 && _state[c, r] != 3) return;
        if (!_placed) PlaceMines(c, r);

        if (_adj[c, r] == -1)
        {
            _state[c, r] = 1;
            _losingCell = (c, r);
            _gameOver = true;
            // Reveal all mines
            for (int cc = 0; cc < _cols; cc++)
                for (int rr = 0; rr < _rows; rr++)
                    if (_adj[cc, rr] == -1 && _state[cc, rr] != 2) _state[cc, rr] = 1;
            return;
        }

        // BFS flood for zeros
        var stack = new Stack<(int, int)>();
        stack.Push((c, r));
        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            if (x < 0 || x >= _cols || y < 0 || y >= _rows) continue;
            if (_state[x, y] == 1 || _state[x, y] == 2) continue;
            if (_adj[x, y] == -1) continue;
            _state[x, y] = 1;
            if (_adj[x, y] == 0)
            {
                for (int dc = -1; dc <= 1; dc++)
                    for (int dr = -1; dr <= 1; dr++)
                    {
                        if (dc == 0 && dr == 0) continue;
                        stack.Push((x + dc, y + dr));
                    }
            }
        }

        CheckWin();
    }

    private void CheckWin()
    {
        for (int c = 0; c < _cols; c++)
            for (int r = 0; r < _rows; r++)
                if (_adj[c, r] != -1 && _state[c, r] != 1) return;
        _won = true;
        // Auto-flag remaining mines for visual completion
        for (int c = 0; c < _cols; c++)
            for (int r = 0; r < _rows; r++)
                if (_adj[c, r] == -1 && _state[c, r] != 2) { _state[c, r] = 2; _flagsPlaced++; }
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Minesweeper", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", "Beginner", "Intermediate", "Expert" }, -1);

        // Header strip (sunken inset around counter / smiley / timer)
        float headerY = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight;
        var headerInset = new Rectangle(panelOffset.X + FrameInset + 4, headerY + 4,
            PanelSize.X - 2 * FrameInset - 8, HeaderH - 8);
        RetroSkin.DrawSunken(headerInset, RetroSkin.Face);

        // Mine counter (left)
        DrawCounter(new Vector2(panelOffset.X + FrameInset + 12, headerY + 8), _mineCount - _flagsPlaced);
        // Timer (right)
        DrawCounter(new Vector2(panelOffset.X + PanelSize.X - FrameInset - 12 - 42, headerY + 8),
            Math.Min(999, (int)_elapsed));

        // Smiley
        var smiley = SmileyRect();
        var smileyAbs = new Rectangle(panelOffset.X + smiley.X, panelOffset.Y + smiley.Y,
            smiley.Width, smiley.Height);
        bool pressedLook = _smileyArmed && Raylib.IsMouseButtonDown(MouseButton.Left);
        if (pressedLook) RetroSkin.DrawPressed(smileyAbs);
        else RetroSkin.DrawRaised(smileyAbs);
        DrawSmileyFace(smileyAbs, pressedLook);

        // Grid
        var origin = GridOrigin();
        var gridAbs = new Vector2(panelOffset.X + origin.X, panelOffset.Y + origin.Y);
        // Sunken grid border
        var gridBorder = new Rectangle(gridAbs.X - 3, gridAbs.Y - 3,
            _cols * CellSize + 6, _rows * CellSize + 6);
        RetroSkin.DrawSunken(gridBorder, RetroSkin.Face);

        for (int c = 0; c < _cols; c++)
            for (int r = 0; r < _rows; r++)
                DrawCell(gridAbs, c, r);

        // Status
        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string state = _won ? "You win!" : _gameOver ? "Game over" : _placed ? "Playing..." : "Click to start";
        RetroWidgets.StatusBar(status, state, $"{_difficulty}  {_cols}x{_rows}  {_mineCount} mines");
    }

    private void DrawCell(Vector2 origin, int c, int r)
    {
        var rect = new Rectangle(origin.X + c * CellSize, origin.Y + r * CellSize, CellSize, CellSize);
        byte s = _state[c, r];
        bool revealed = s == 1;
        bool isMine = _adj[c, r] == -1;

        if (!revealed)
        {
            // Held cell shows pressed look
            bool held = _cellHeld && _heldCell.c == c && _heldCell.r == r
                && Raylib.IsMouseButtonDown(MouseButton.Left);
            if (held) RetroSkin.DrawPressed(rect);
            else RetroSkin.DrawRaised(rect);

            if (s == 2) DrawFlag(rect);
            else if (s == 3) RetroSkin.DrawText("?",
                (int)rect.X + 6, (int)rect.Y + 2, RetroSkin.BodyText, 16);
            return;
        }

        // Revealed: flat face with single grid line
        Color bg = (isMine && _losingCell == (c, r)) ? MineRed : RetroSkin.Face;
        Raylib.DrawRectangleRec(rect, bg);
        Raylib.DrawRectangleLines((int)rect.X, (int)rect.Y, CellSize + 1, CellSize + 1, RetroSkin.Shadow);

        if (isMine)
        {
            DrawMine(rect);
        }
        else if (_adj[c, r] > 0)
        {
            int n = _adj[c, r];
            var col = NumberColors[n - 1];
            string txt = n.ToString();
            int tw = RetroSkin.MeasureText(txt, 16);
            RetroSkin.DrawText(txt,
                (int)(rect.X + (CellSize - tw) / 2),
                (int)(rect.Y + 1),
                col, 16);
        }
    }

    private static void DrawFlag(Rectangle r)
    {
        int x = (int)r.X, y = (int)r.Y;
        // Pole
        Raylib.DrawRectangle(x + 9, y + 4, 2, 10, RetroSkin.BodyText);
        // Base
        Raylib.DrawRectangle(x + 5, y + 14, 10, 2, RetroSkin.BodyText);
        Raylib.DrawRectangle(x + 6, y + 13, 8, 1, RetroSkin.BodyText);
        // Triangle flag
        for (int i = 0; i < 5; i++)
            Raylib.DrawRectangle(x + 4 + i, y + 4 + i / 2, 5 - i, 1, MineRed);
        for (int i = 0; i < 4; i++)
            Raylib.DrawRectangle(x + 4 + i, y + 6 + i / 2, 5 - i, 1, MineRed);
    }

    private static void DrawMine(Rectangle r)
    {
        int cx = (int)r.X + CellSize / 2;
        int cy = (int)r.Y + CellSize / 2;
        // Body
        Raylib.DrawCircle(cx, cy, 5, RetroSkin.BodyText);
        // Spikes
        Raylib.DrawRectangle(cx - 1, cy - 8, 2, 16, RetroSkin.BodyText);
        Raylib.DrawRectangle(cx - 8, cy - 1, 16, 2, RetroSkin.BodyText);
        Raylib.DrawLine(cx - 5, cy - 5, cx + 5, cy + 5, RetroSkin.BodyText);
        Raylib.DrawLine(cx - 5, cy + 5, cx + 5, cy - 5, RetroSkin.BodyText);
        // Highlight
        Raylib.DrawRectangle(cx - 2, cy - 2, 2, 2, RetroSkin.Highlight);
    }

    private void DrawCounter(Vector2 pos, int value)
    {
        var rect = new Rectangle(pos.X, pos.Y, 42, 22);
        Raylib.DrawRectangleRec(rect, CounterBg);
        Raylib.DrawRectangleLinesEx(rect, 1, RetroSkin.Shadow);
        int v = Math.Clamp(value, -99, 999);
        string s = v < 0 ? "-" + (-v).ToString("D2") : v.ToString("D3");
        RetroSkin.DrawText(s, (int)pos.X + 4, (int)pos.Y + 2, CounterFg, 18);
    }

    private void DrawSmileyFace(Rectangle r, bool pressed)
    {
        int cx = (int)r.X + (int)r.Width / 2 + (pressed ? 1 : 0);
        int cy = (int)r.Y + (int)r.Height / 2 + (pressed ? 1 : 0);
        var yellow = new Color(255, 255, 0, 255);
        Raylib.DrawCircle(cx, cy, 9, RetroSkin.BodyText);
        Raylib.DrawCircle(cx, cy, 8, yellow);

        if (_gameOver)
        {
            // X eyes
            for (int i = -2; i <= 2; i++)
            {
                Raylib.DrawPixel(cx - 4 + i, cy - 3 + i, RetroSkin.BodyText);
                Raylib.DrawPixel(cx - 4 + i, cy - 3 - i, RetroSkin.BodyText);
                Raylib.DrawPixel(cx + 4 + i, cy - 3 + i, RetroSkin.BodyText);
                Raylib.DrawPixel(cx + 4 + i, cy - 3 - i, RetroSkin.BodyText);
            }
            // Frown
            Raylib.DrawLine(cx - 3, cy + 4, cx + 3, cy + 4, RetroSkin.BodyText);
            Raylib.DrawPixel(cx - 4, cy + 3, RetroSkin.BodyText);
            Raylib.DrawPixel(cx + 4, cy + 3, RetroSkin.BodyText);
        }
        else if (_won)
        {
            // Sunglasses
            Raylib.DrawRectangle(cx - 6, cy - 3, 4, 3, RetroSkin.BodyText);
            Raylib.DrawRectangle(cx + 2, cy - 3, 4, 3, RetroSkin.BodyText);
            Raylib.DrawRectangle(cx - 2, cy - 2, 4, 1, RetroSkin.BodyText);
            // Smile
            Raylib.DrawLine(cx - 3, cy + 3, cx + 3, cy + 3, RetroSkin.BodyText);
            Raylib.DrawPixel(cx - 4, cy + 2, RetroSkin.BodyText);
            Raylib.DrawPixel(cx + 4, cy + 2, RetroSkin.BodyText);
        }
        else
        {
            // Eyes
            Raylib.DrawRectangle(cx - 3, cy - 3, 2, 2, RetroSkin.BodyText);
            Raylib.DrawRectangle(cx + 1, cy - 3, 2, 2, RetroSkin.BodyText);
            // Smile
            Raylib.DrawLine(cx - 3, cy + 3, cx + 3, cy + 3, RetroSkin.BodyText);
            Raylib.DrawPixel(cx - 4, cy + 2, RetroSkin.BodyText);
            Raylib.DrawPixel(cx + 4, cy + 2, RetroSkin.BodyText);
        }
    }

    public void Close() { }
}
