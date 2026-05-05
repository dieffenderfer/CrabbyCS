using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Conway's Life with a two-player mode. Each cell is owned by Red or Blue
/// (or empty). Standard B3/S23 rules; new births take the majority color of
/// their three living neighbors. Click to toggle / paint with the active
/// color. Run starts the simulation; speed cycles between three rates.
/// </summary>
public class LifeGenesisActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Cols = 50;
    private const int Rows = 32;
    private const int Cell = 12;
    private const int Margin = 14;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + Cols * Cell,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + Rows * Cell + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private const byte Empty = 0, Red = 1, Blue = 2;
    private byte[,] _grid = new byte[Cols, Rows];
    private byte[,] _next = new byte[Cols, Rows];
    private bool _running;
    private byte _activeColor = Red;
    private float _stepTimer;
    private float _stepInterval = 0.12f;
    private int _generation;
    private readonly Random _rng = new();

    public void Load() => Reset();

    private void Reset()
    {
        for (int x = 0; x < Cols; x++) for (int y = 0; y < Rows; y++) _grid[x, y] = Empty;
        _running = false;
        _generation = 0;
        _stepTimer = 0;
    }

    private void Randomize()
    {
        for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Cols; x++)
                _grid[x, y] = _rng.Next(4) == 0 ? (_rng.Next(2) == 0 ? Red : Blue) : Empty;
        _generation = 0;
    }

    private void Step()
    {
        for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Cols; x++)
            {
                int alive = 0, redN = 0, blueN = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = (x + dx + Cols) % Cols, ny = (y + dy + Rows) % Rows;
                        var n = _grid[nx, ny];
                        if (n != Empty) { alive++; if (n == Red) redN++; else blueN++; }
                    }
                var cur = _grid[x, y];
                if (cur != Empty)
                    _next[x, y] = (alive == 2 || alive == 3) ? cur : Empty;
                else
                    _next[x, y] = alive == 3 ? (redN > blueN ? Red : redN < blueN ? Blue : (_rng.Next(2) == 0 ? Red : Blue)) : Empty;
            }
        (_grid, _next) = (_next, _grid);
        _generation++;
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
        var items = new[] {
            "New", _running ? "Pause" : "Run", "Step", "Random",
            $"Speed: {SpeedLabel()}", $"Color: {(_activeColor == Red ? "Red" : "Blue")}"
        };
        switch (RetroWidgets.MenuBarHitTest(menuBar, items, local, leftPressed))
        {
            case 0: Reset(); return;
            case 1: _running = !_running; return;
            case 2: Step(); return;
            case 3: Randomize(); return;
            case 4: CycleSpeed(); return;
            case 5: _activeColor = _activeColor == Red ? Blue : Red; return;
        }

        if (_running)
        {
            _stepTimer += delta;
            if (_stepTimer >= _stepInterval) { _stepTimer = 0; Step(); }
        }

        // Paint
        if (Raylib.IsMouseButtonDown(MouseButton.Left))
        {
            float bx = FrameInset + Margin;
            float by = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
            int gx = (int)((local.X - bx) / Cell);
            int gy = (int)((local.Y - by) / Cell);
            if (gx >= 0 && gx < Cols && gy >= 0 && gy < Rows)
                _grid[gx, gy] = _activeColor;
        }
        if (Raylib.IsMouseButtonDown(MouseButton.Right))
        {
            float bx = FrameInset + Margin;
            float by = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
            int gx = (int)((local.X - bx) / Cell);
            int gy = (int)((local.Y - by) / Cell);
            if (gx >= 0 && gx < Cols && gy >= 0 && gy < Rows)
                _grid[gx, gy] = Empty;
        }
    }

    private void CycleSpeed()
    {
        _stepInterval = _stepInterval switch
        {
            > 0.18f => 0.06f,
            > 0.10f => 0.20f,
            _ => 0.12f,
        };
    }

    private string SpeedLabel() => _stepInterval switch
    {
        < 0.08f => "Fast", < 0.15f => "Normal", _ => "Slow"
    };

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Life Genesis", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        var items = new[] {
            "New", _running ? "Pause" : "Run", "Step", "Random",
            $"Speed: {SpeedLabel()}", $"Color: {(_activeColor == Red ? "Red" : "Blue")}"
        };
        RetroWidgets.MenuBarVisual(menuBar, items, -1);

        float bx = panelOffset.X + FrameInset + Margin;
        float by = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
        var board = new Rectangle(bx - 3, by - 3, Cols * Cell + 6, Rows * Cell + 6);
        RetroSkin.DrawSunken(board, new Color(0, 0, 0, 255));

        for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Cols; x++)
            {
                if (_grid[x, y] == Empty) continue;
                var col = _grid[x, y] == Red
                    ? new Color(220, 60, 60, 255)
                    : new Color(60, 100, 220, 255);
                Raylib.DrawRectangle((int)(bx + x * Cell), (int)(by + y * Cell), Cell - 1, Cell - 1, col);
            }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        int red = 0, blue = 0;
        for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Cols; x++)
            { if (_grid[x, y] == Red) red++; else if (_grid[x, y] == Blue) blue++; }
        RetroWidgets.StatusBar(status,
            "Left=paint  Right=erase  Run for autoplay",
            $"Gen {_generation}  R:{red}  B:{blue}");
    }

    public void Close() { }
}
