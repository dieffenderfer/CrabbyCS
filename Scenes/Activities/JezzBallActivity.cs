using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// JezzBall — bouncing balls trapped in a rectangle. Click to extend a wall
/// horizontally or vertically (right-click toggles orientation). Walls grow
/// from the click point until they hit existing walls; balls hitting a
/// growing wall destroy it (and cost a life). Reduce open area to less than
/// 25% to clear the level.
/// </summary>
public class JezzBallActivity : IActivity
{
    private const int FrameInset = 3;
    private const int CellPx = 8;
    private const int CellsX = 56;
    private const int CellsY = 36;
    private const int Margin = 14;
    private const float WallSpeed = 220f; // pixels per second

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + CellsX * CellPx,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + CellsY * CellPx + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private readonly RetroHelp _help = new()
    {
        Title = "JezzBall — How to play",
        Lines = new[]
        {
            "Carve walls into the rectangle to shrink the play area.",
            "Click an empty cell to start a wall growing in both",
            "directions until it hits an edge.",
            "Right-click toggles between horizontal and vertical.",
            "If a ball hits a still-growing wall, the wall breaks",
            "and you lose a life. Cover 75% of the area to win.",
        },
    };

    private bool[,] _wall = new bool[CellsX, CellsY];
    private record struct Ball(float X, float Y, float Vx, float Vy);
    private List<Ball> _balls = new();
    private record class Wall(int Cx, int Cy, bool Horizontal)
    {
        public float P1, P2;          // pixel offsets, growing in opposite directions
    }
    private List<Wall> _growing = new();
    private bool _vertical;            // wall orientation; right-click toggles
    private int _lives = 3;
    private int _level = 1;
    private bool _gameOver;
    private bool _wonLevel;
    private readonly Random _rng = new();

    public void Load() => Reset();

    private void Reset()
    {
        _wall = new bool[CellsX, CellsY];
        for (int x = 0; x < CellsX; x++) { _wall[x, 0] = true; _wall[x, CellsY - 1] = true; }
        for (int y = 0; y < CellsY; y++) { _wall[0, y] = true; _wall[CellsX - 1, y] = true; }

        _balls.Clear();
        for (int i = 0; i < 1 + _level; i++)
        {
            float ang = (float)(_rng.NextDouble() * Math.PI * 2);
            _balls.Add(new Ball(
                _rng.Next(8, CellsX - 8) * CellPx,
                _rng.Next(8, CellsY - 8) * CellPx,
                MathF.Cos(ang) * 90f,
                MathF.Sin(ang) * 90f));
        }
        _growing.Clear();
        _gameOver = false;
        _wonLevel = false;
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
        switch (RetroWidgets.MenuBarHitTest(menuBar,
            new[] { "New", _vertical ? "Vertical" : "Horizontal", "Help" }, local, leftPressed))
        {
            case 0: _level = 1; _lives = 3; Reset(); return;
            case 1: _vertical = !_vertical; return;
            case 2: _help.Visible = !_help.Visible; return;
        }
        if (_help.HandleInput(local, leftPressed, PanelSize)) return;

        if (rightPressed) { _vertical = !_vertical; return; }
        if (_gameOver) return;

        if (_wonLevel)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Enter)) { _level++; Reset(); }
            return;
        }

        // Click to start a wall
        if (leftPressed)
        {
            float bx = FrameInset + Margin;
            float by = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
            int gx = (int)((local.X - bx) / CellPx);
            int gy = (int)((local.Y - by) / CellPx);
            if (gx > 0 && gx < CellsX - 1 && gy > 0 && gy < CellsY - 1 && !_wall[gx, gy])
            {
                _growing.Add(new Wall(gx, gy, !_vertical));
            }
        }

        // Grow walls
        for (int i = _growing.Count - 1; i >= 0; i--)
        {
            var w = _growing[i];
            w.P1 += WallSpeed * delta;
            w.P2 += WallSpeed * delta;
            int reached1 = (int)(w.P1 / CellPx);
            int reached2 = (int)(w.P2 / CellPx);

            // Set cells along the wall
            bool finished1 = false, finished2 = false;
            for (int k = 0; k <= reached1; k++)
            {
                int x = w.Horizontal ? w.Cx - k : w.Cx;
                int y = w.Horizontal ? w.Cy : w.Cy - k;
                if (x < 0 || y < 0) { finished1 = true; break; }
                if (_wall[x, y] && k > 0) { finished1 = true; break; }
                _wall[x, y] = true;
            }
            for (int k = 0; k <= reached2; k++)
            {
                int x = w.Horizontal ? w.Cx + k : w.Cx;
                int y = w.Horizontal ? w.Cy : w.Cy + k;
                if (x >= CellsX || y >= CellsY) { finished2 = true; break; }
                if (_wall[x, y] && k > 0) { finished2 = true; break; }
                _wall[x, y] = true;
            }
            if (finished1 && finished2) { _growing.RemoveAt(i); continue; }
        }

        // Move balls
        for (int i = 0; i < _balls.Count; i++)
        {
            var b = _balls[i];
            b.X += b.Vx * delta; b.Y += b.Vy * delta;
            int gx = (int)(b.X / CellPx), gy = (int)(b.Y / CellPx);
            if (gx < 0 || gx >= CellsX || gy < 0 || gy >= CellsY) continue;
            // Collide with walls (cell-level)
            if (_wall[gx, gy])
            {
                // Determine which axis we crossed by sampling the previous cell
                int oldGx = (int)((b.X - b.Vx * delta) / CellPx);
                int oldGy = (int)((b.Y - b.Vy * delta) / CellPx);
                bool wallX = oldGx >= 0 && oldGx < CellsX && oldGy >= 0 && oldGy < CellsY && _wall[oldGx, gy];
                bool wallY = oldGx >= 0 && oldGx < CellsX && oldGy >= 0 && oldGy < CellsY && _wall[gx, oldGy];
                if (wallX) b.Vx = -b.Vx;
                if (wallY) b.Vy = -b.Vy;
                if (!wallX && !wallY) { b.Vx = -b.Vx; b.Vy = -b.Vy; }
                b.X += b.Vx * delta; b.Y += b.Vy * delta;
                // Destroy any growing wall this ball touches
                for (int k = _growing.Count - 1; k >= 0; k--)
                {
                    var w = _growing[k];
                    if (Math.Abs(b.X - w.Cx * CellPx) < 12 && Math.Abs(b.Y - w.Cy * CellPx) < 12)
                    {
                        _growing.RemoveAt(k);
                        _lives--;
                        if (_lives <= 0) _gameOver = true;
                    }
                }
            }
            _balls[i] = b;
        }

        // Check level cleared (open area < 25%)
        int total = (CellsX - 2) * (CellsY - 2);
        int open = 0;
        for (int x = 1; x < CellsX - 1; x++) for (int y = 1; y < CellsY - 1; y++) if (!_wall[x, y]) open++;
        if (open * 4 < total) _wonLevel = true;
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "JezzBall", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", _vertical ? "Vertical" : "Horizontal", "Help" }, -1);

        float bx = panelOffset.X + FrameInset + Margin;
        float by = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;

        Raylib.DrawRectangle((int)bx, (int)by, CellsX * CellPx, CellsY * CellPx, new Color(8, 16, 32, 255));

        for (int x = 0; x < CellsX; x++)
            for (int y = 0; y < CellsY; y++)
                if (_wall[x, y])
                    Raylib.DrawRectangle((int)(bx + x * CellPx), (int)(by + y * CellPx),
                        CellPx, CellPx, new Color(160, 144, 96, 255));

        foreach (var b in _balls)
            Raylib.DrawCircle((int)(bx + b.X), (int)(by + b.Y), 6, new Color(220, 80, 80, 255));

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string state = _gameOver ? "Game over"
            : _wonLevel ? "Level cleared! Press Enter"
            : "Click to start a wall  |  Right-click toggles orientation";
        RetroWidgets.StatusBar(status, state, $"Lvl {_level}   Lives {_lives}");

        _help.Draw(panelOffset, PanelSize);
    }

    public void Close() { }
}
