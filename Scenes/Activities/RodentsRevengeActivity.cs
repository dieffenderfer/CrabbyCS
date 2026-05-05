using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Rodent's Revenge — push movable blocks around a walled grid to trap the
/// cats. The mouse moves with the arrow keys and pushes contiguous lines of
/// blocks in the same direction. A cat with no orthogonal escape (all four
/// sides blocked by walls or blocks) is captured. Cats wandering into the
/// mouse end the round.
/// </summary>
public class RodentsRevengeActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Cols = 18;
    private const int Rows = 14;
    private const int Cell = 22;
    private const int Margin = 14;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + Cols * Cell,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + Rows * Cell + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private readonly RetroHelp _help = new()
    {
        Title = "Rodent's Revenge — How to play",
        Lines = new[]
        {
            "Steer the mouse with the arrow keys.",
            "Pushing into a movable block slides every block",
            "behind it in that direction (if there's room).",
            "Trap a cat by leaving it with no orthogonal escape —",
            "all four sides blocked by walls or blocks.",
            "Touching a cat ends the level. Clear all cats to advance.",
        },
    };

    private const byte Empty = 0, Wall = 1, Block = 2, Mouse = 3, Cat = 4, TrappedCat = 5;
    private byte[,] _grid = new byte[Cols, Rows];
    private (int x, int y) _mouse;
    private List<(int x, int y)> _cats = new();
    private float _catTimer;
    private int _captured;
    private int _level = 1;
    private bool _gameOver;
    private bool _won;
    private readonly Random _rng = new();

    public void Load() => Reset();

    private void Reset()
    {
        _grid = new byte[Cols, Rows];
        for (int x = 0; x < Cols; x++) { _grid[x, 0] = Wall; _grid[x, Rows - 1] = Wall; }
        for (int y = 0; y < Rows; y++) { _grid[0, y] = Wall; _grid[Cols - 1, y] = Wall; }

        // Seed pushable blocks on a sparse interior pattern
        for (int y = 2; y < Rows - 2; y += 2)
            for (int x = 2; x < Cols - 2; x += 2)
                if (_rng.Next(3) > 0) _grid[x, y] = Block;

        _mouse = (Cols / 2, Rows / 2);
        _grid[_mouse.x, _mouse.y] = Mouse;

        _cats.Clear();
        int catCount = Math.Min(2 + _level, 6);
        for (int i = 0; i < catCount; i++)
        {
            while (true)
            {
                int cx = _rng.Next(2, Cols - 2);
                int cy = _rng.Next(2, Rows - 2);
                if (_grid[cx, cy] == Empty
                    && Math.Abs(cx - _mouse.x) + Math.Abs(cy - _mouse.y) > 6)
                {
                    _cats.Add((cx, cy));
                    _grid[cx, cy] = Cat;
                    break;
                }
            }
        }

        _captured = 0;
        _gameOver = false;
        _won = false;
        _catTimer = 0;
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
        switch (RetroWidgets.MenuBarHitTest(menuBar, new[] { "New", "Help" }, local, leftPressed))
        {
            case 0: _level = 1; Reset(); return;
            case 1: _help.Visible = !_help.Visible; return;
        }
        if (_help.HandleInput(local, leftPressed, PanelSize)) return;

        if (_gameOver) return;

        if (_won && Raylib.IsKeyPressed(KeyboardKey.Enter)) { _level++; Reset(); return; }
        if (_won) return;

        int dx = 0, dy = 0;
        if (Raylib.IsKeyPressed(KeyboardKey.Up)) dy = -1;
        else if (Raylib.IsKeyPressed(KeyboardKey.Down)) dy = 1;
        else if (Raylib.IsKeyPressed(KeyboardKey.Left)) dx = -1;
        else if (Raylib.IsKeyPressed(KeyboardKey.Right)) dx = 1;

        if (dx != 0 || dy != 0) TryMove(dx, dy);

        _catTimer += delta;
        if (_catTimer > 0.45f) { _catTimer = 0; StepCats(); }

        CheckTrappedCats();
        if (_cats.Count == 0) _won = true;
    }

    private void TryMove(int dx, int dy)
    {
        int nx = _mouse.x + dx, ny = _mouse.y + dy;
        if (nx < 0 || nx >= Cols || ny < 0 || ny >= Rows) return;
        var dest = _grid[nx, ny];
        if (dest == Wall || dest == TrappedCat) return;
        if (dest == Cat) { _gameOver = true; return; }
        if (dest == Block)
        {
            // Find run of blocks in same direction; push if free space at end
            int rx = nx, ry = ny;
            while (rx >= 0 && rx < Cols && ry >= 0 && ry < Rows && _grid[rx, ry] == Block)
            { rx += dx; ry += dy; }
            if (rx < 0 || rx >= Cols || ry < 0 || ry >= Rows) return;
            if (_grid[rx, ry] != Empty) return;
            _grid[rx, ry] = Block;
        }

        _grid[_mouse.x, _mouse.y] = Empty;
        _mouse = (nx, ny);
        _grid[nx, ny] = Mouse;
    }

    private void StepCats()
    {
        for (int i = 0; i < _cats.Count; i++)
        {
            var (cx, cy) = _cats[i];
            if (_grid[cx, cy] == TrappedCat) continue;
            // Move toward mouse if possible
            int dx = Math.Sign(_mouse.x - cx), dy = Math.Sign(_mouse.y - cy);
            var prefs = new (int, int)[] { (dx, 0), (0, dy), (-dx, 0), (0, -dy) };
            foreach (var (mx, my) in prefs)
            {
                if (mx == 0 && my == 0) continue;
                int nx = cx + mx, ny = cy + my;
                if (nx < 0 || nx >= Cols || ny < 0 || ny >= Rows) continue;
                if (_grid[nx, ny] == Mouse) { _gameOver = true; return; }
                if (_grid[nx, ny] != Empty) continue;
                _grid[cx, cy] = Empty;
                _grid[nx, ny] = Cat;
                _cats[i] = (nx, ny);
                break;
            }
        }
    }

    private void CheckTrappedCats()
    {
        for (int i = _cats.Count - 1; i >= 0; i--)
        {
            var (cx, cy) = _cats[i];
            bool trapped = true;
            foreach (var (mx, my) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
            {
                int nx = cx + mx, ny = cy + my;
                if (nx < 0 || nx >= Cols || ny < 0 || ny >= Rows) continue;
                if (_grid[nx, ny] == Empty || _grid[nx, ny] == Mouse) { trapped = false; break; }
            }
            if (trapped)
            {
                _grid[cx, cy] = TrappedCat;
                _cats.RemoveAt(i);
                _captured++;
            }
        }
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Rodent's Revenge", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", "Help" }, -1);

        float bx = panelOffset.X + FrameInset + Margin;
        float by = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;

        for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Cols; x++)
            {
                int px = (int)(bx + x * Cell), py = (int)(by + y * Cell);
                var rect = new Rectangle(px, py, Cell, Cell);
                Raylib.DrawRectangleRec(rect, new Color(232, 224, 200, 255));
                switch (_grid[x, y])
                {
                    case Wall:
                        RetroSkin.DrawRaised(rect);
                        Raylib.DrawRectangle(px + 4, py + 4, Cell - 8, Cell - 8, new Color(120, 96, 64, 255));
                        break;
                    case Block:
                        RetroSkin.DrawRaised(rect);
                        Raylib.DrawRectangle(px + 5, py + 5, Cell - 10, Cell - 10, new Color(180, 140, 96, 255));
                        break;
                    case Mouse:
                        Raylib.DrawCircle(px + Cell / 2, py + Cell / 2, Cell / 3, new Color(180, 180, 180, 255));
                        Raylib.DrawCircle(px + Cell / 2 - 4, py + Cell / 2 - 4, 2, RetroSkin.BodyText);
                        Raylib.DrawCircle(px + Cell / 2 + 4, py + Cell / 2 - 4, 2, RetroSkin.BodyText);
                        break;
                    case Cat:
                        Raylib.DrawCircle(px + Cell / 2, py + Cell / 2, Cell / 3, new Color(80, 60, 40, 255));
                        Raylib.DrawTriangle(
                            new Vector2(px + 4, py + 6), new Vector2(px + 8, py + 4), new Vector2(px + 8, py + 10),
                            new Color(80, 60, 40, 255));
                        Raylib.DrawTriangle(
                            new Vector2(px + Cell - 8, py + 4), new Vector2(px + Cell - 4, py + 6), new Vector2(px + Cell - 8, py + 10),
                            new Color(80, 60, 40, 255));
                        Raylib.DrawCircle(px + Cell / 2 - 4, py + Cell / 2, 2, new Color(255, 220, 0, 255));
                        Raylib.DrawCircle(px + Cell / 2 + 4, py + Cell / 2, 2, new Color(255, 220, 0, 255));
                        break;
                    case TrappedCat:
                        Raylib.DrawCircle(px + Cell / 2, py + Cell / 2, Cell / 3, new Color(120, 120, 120, 255));
                        Raylib.DrawText("Z", px + Cell / 2 - 4, py + 4, 12, RetroSkin.BodyText);
                        break;
                }
            }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string state = _gameOver ? "Caught by a cat" : _won ? "Cleared! Press Enter for next" : "Push blocks to corner the cats";
        RetroWidgets.StatusBar(status, state, $"Level: {_level}   Trapped: {_captured}");

        _help.Draw(panelOffset, PanelSize);
    }

    public void Close() { }
}
