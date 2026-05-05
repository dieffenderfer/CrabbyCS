using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Rattler Race — single snake on a walled grid. Eat food to grow and score.
/// Hit a wall or your own tail to lose. Arrow keys steer; Space pauses.
/// Speed steps up with score.
/// </summary>
public class RattlerRaceActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Cols = 28;
    private const int Rows = 20;
    private const int Cell = 14;
    private const int Margin = 12;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + Cols * Cell,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + Rows * Cell + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private List<(int x, int y)> _snake = new();
    private (int x, int y) _dir = (1, 0);
    private (int x, int y) _pendingDir = (1, 0);
    private (int x, int y) _food;
    private int _score;
    private bool _gameOver;
    private bool _paused;
    private float _stepTimer;
    private readonly Random _rng = new();

    public void Load() => Reset();

    private void Reset()
    {
        _snake.Clear();
        int sx = Cols / 2, sy = Rows / 2;
        for (int i = 0; i < 4; i++) _snake.Add((sx - i, sy));
        _dir = (1, 0); _pendingDir = (1, 0);
        _score = 0;
        _gameOver = false;
        _paused = false;
        SpawnFood();
        _stepTimer = 0;
    }

    private void SpawnFood()
    {
        while (true)
        {
            var p = (_rng.Next(Cols), _rng.Next(Rows));
            if (!_snake.Contains(p)) { _food = p; return; }
        }
    }

    private float StepInterval() => Math.Max(0.04f, 0.18f - _score * 0.0015f);

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

        if (Raylib.IsKeyPressed(KeyboardKey.Up) && _dir.y == 0) _pendingDir = (0, -1);
        if (Raylib.IsKeyPressed(KeyboardKey.Down) && _dir.y == 0) _pendingDir = (0, 1);
        if (Raylib.IsKeyPressed(KeyboardKey.Left) && _dir.x == 0) _pendingDir = (-1, 0);
        if (Raylib.IsKeyPressed(KeyboardKey.Right) && _dir.x == 0) _pendingDir = (1, 0);
        if (Raylib.IsKeyPressed(KeyboardKey.Space)) _paused = true;

        _stepTimer += delta;
        if (_stepTimer < StepInterval()) return;
        _stepTimer = 0;

        _dir = _pendingDir;
        var head = _snake[0];
        var nh = (head.x + _dir.x, head.y + _dir.y);
        if (nh.Item1 < 0 || nh.Item1 >= Cols || nh.Item2 < 0 || nh.Item2 >= Rows
            || _snake.Take(_snake.Count - 1).Contains(nh))
        { _gameOver = true; return; }

        _snake.Insert(0, nh);
        if (nh == _food) { _score += 10; SpawnFood(); }
        else _snake.RemoveAt(_snake.Count - 1);
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Rattler Race", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", _paused ? "Resume" : "Pause" }, -1);

        float bx = panelOffset.X + FrameInset + Margin;
        float by = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
        var arena = new Rectangle(bx - 4, by - 4, Cols * Cell + 8, Rows * Cell + 8);
        RetroSkin.DrawSunken(arena, new Color(8, 24, 8, 255));

        // Food
        Raylib.DrawRectangle((int)(bx + _food.x * Cell + 2), (int)(by + _food.y * Cell + 2),
            Cell - 4, Cell - 4, new Color(220, 60, 60, 255));

        // Snake
        for (int i = 0; i < _snake.Count; i++)
        {
            var (x, y) = _snake[i];
            var col = i == 0 ? new Color(80, 240, 80, 255) : new Color(40, 180, 40, 255);
            Raylib.DrawRectangle((int)(bx + x * Cell + 1), (int)(by + y * Cell + 1),
                Cell - 2, Cell - 2, col);
        }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string state = _gameOver ? "Game over" : _paused ? "Paused"
            : "Arrow keys steer  |  Space pauses";
        RetroWidgets.StatusBar(status, state, $"Score: {_score}   Length: {_snake.Count}");
    }

    public void Close() { }
}
