using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Pipe Dream — place pipe pieces from a queue onto a grid before the water
/// reaches them. Score one point per filled connected pipe; survive past the
/// target to advance. Kinds: H, V, four corners, plus a cross.
/// </summary>
public class PipeDreamActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Cols = 9;
    private const int Rows = 7;
    private const int Cell = 36;
    private const int Margin = 16;
    private const int QueueSize = 5;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + Cols * Cell + Cell + Margin,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + Rows * Cell + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private enum PieceType { H, V, NE, NW, SE, SW, Cross, Source }
    // bit flags for connections (N=1, E=2, S=4, W=8)
    private static int Connections(PieceType t) => t switch
    {
        PieceType.H => 2 | 8,
        PieceType.V => 1 | 4,
        PieceType.NE => 1 | 2,
        PieceType.NW => 1 | 8,
        PieceType.SE => 4 | 2,
        PieceType.SW => 4 | 8,
        PieceType.Cross => 1 | 2 | 4 | 8,
        PieceType.Source => 4,  // emits south
        _ => 0,
    };

    private PieceType?[,] _board = new PieceType?[Cols, Rows];
    private bool[,] _filled = new bool[Cols, Rows];
    private List<PieceType> _queue = new();
    private (int x, int y) _source;
    private (int x, int y) _flowCell;
    private int _flowFrom; // direction water entered the current cell from
    private float _flowTimer;
    private float _initialDelay;
    private int _score;
    private bool _flowing;
    private bool _gameOver;
    private readonly Random _rng = new();

    public void Load() => Reset();

    private void Reset()
    {
        _board = new PieceType?[Cols, Rows];
        _filled = new bool[Cols, Rows];
        _queue.Clear();
        for (int i = 0; i < QueueSize; i++) _queue.Add(RandomPipe());
        _source = (_rng.Next(2, Cols - 2), 0);
        _board[_source.x, _source.y] = PieceType.Source;
        _flowing = false;
        _gameOver = false;
        _score = 0;
        _initialDelay = 12f;
        _flowTimer = 0;
    }

    private PieceType RandomPipe()
    {
        var pool = new[] { PieceType.H, PieceType.V, PieceType.NE, PieceType.NW, PieceType.SE, PieceType.SW };
        // 1 in 12 chance of cross
        if (_rng.Next(12) == 0) return PieceType.Cross;
        return pool[_rng.Next(pool.Length)];
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
        if (RetroWidgets.MenuBarHitTest(menuBar, new[] { "New" }, local, leftPressed) == 0)
        { Reset(); return; }

        if (!_flowing)
        {
            _initialDelay -= delta;
            if (_initialDelay <= 0) StartFlow();
        }
        else
        {
            _flowTimer += delta;
            float interval = Math.Max(0.25f, 1.5f - _score * 0.04f);
            if (_flowTimer >= interval)
            {
                _flowTimer = 0;
                AdvanceFlow();
            }
        }

        if (leftPressed && !_gameOver)
        {
            float bx = FrameInset + Margin;
            float by = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
            int gx = (int)((local.X - bx) / Cell);
            int gy = (int)((local.Y - by) / Cell);
            if (gx >= 0 && gx < Cols && gy >= 0 && gy < Rows
                && _board[gx, gy] == null && !(_source.x == gx && _source.y == gy))
            {
                _board[gx, gy] = _queue[0];
                _queue.RemoveAt(0);
                _queue.Add(RandomPipe());
            }
        }
    }

    private void StartFlow()
    {
        _flowing = true;
        _filled[_source.x, _source.y] = true;
        _flowCell = _source;
        _flowFrom = 1; // entered from north (top boundary)
    }

    private void AdvanceFlow()
    {
        // From current cell, find which way water exits given _flowFrom
        if (_board[_flowCell.x, _flowCell.y] is not PieceType cur) { _gameOver = true; return; }
        int conns = Connections(cur);
        // Source emits south
        int exitDir;
        if (cur == PieceType.Source) exitDir = 4;
        else
        {
            int incoming = _flowFrom;          // which side water came from
            int remaining = conns & ~incoming; // valid exits
            if (remaining == 0) { _gameOver = true; return; }
            exitDir = remaining;               // for L/H/V, exactly one bit; cross handled below
            if (cur == PieceType.Cross) exitDir = 15 & ~incoming & OppositeOf(incoming);
        }

        var (nx, ny) = exitDir switch
        {
            1 => (_flowCell.x, _flowCell.y - 1),
            2 => (_flowCell.x + 1, _flowCell.y),
            4 => (_flowCell.x, _flowCell.y + 1),
            8 => (_flowCell.x - 1, _flowCell.y),
            _ => (-1, -1),
        };
        if (nx < 0 || nx >= Cols || ny < 0 || ny >= Rows) { _gameOver = true; return; }
        if (_board[nx, ny] is not PieceType next) { _gameOver = true; return; }
        int nextConns = Connections(next);
        int entryDir = OppositeOf(exitDir);
        if ((nextConns & entryDir) == 0) { _gameOver = true; return; }
        _flowCell = (nx, ny);
        _filled[nx, ny] = true;
        _flowFrom = entryDir;
        _score++;
    }

    private static int OppositeOf(int d) => d switch { 1 => 4, 2 => 8, 4 => 1, 8 => 2, _ => 0 };

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Pipe Dream", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New" }, -1);

        float bx = panelOffset.X + FrameInset + Margin;
        float by = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
        var board = new Rectangle(bx - 3, by - 3, Cols * Cell + 6, Rows * Cell + 6);
        RetroSkin.DrawSunken(board, new Color(80, 80, 80, 255));

        for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Cols; x++)
            {
                int px = (int)(bx + x * Cell), py = (int)(by + y * Cell);
                Raylib.DrawRectangle(px, py, Cell, Cell, new Color(64, 64, 64, 255));
                Raylib.DrawRectangleLines(px, py, Cell, Cell, new Color(48, 48, 48, 255));
                if (_board[x, y] is PieceType t)
                    DrawPipe(px, py, t, _filled[x, y]);
            }

        // Queue
        float qx = panelOffset.X + FrameInset + Margin + Cols * Cell + Margin;
        float qy = by;
        RetroSkin.DrawText("NEXT", (int)qx, (int)(qy - 16), RetroSkin.BodyText, 16);
        for (int i = 0; i < _queue.Count; i++)
        {
            int px = (int)qx, py = (int)(qy + i * (Cell + 4));
            Raylib.DrawRectangle(px, py, Cell, Cell, new Color(64, 64, 64, 255));
            Raylib.DrawRectangleLines(px, py, Cell, Cell, RetroSkin.Shadow);
            DrawPipe(px, py, _queue[i], false);
        }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string state = _gameOver ? "Pipe broke" : _flowing ? "Flowing..." : $"Flow in {(int)Math.Ceiling(_initialDelay)}s";
        RetroWidgets.StatusBar(status, state, $"Filled: {_score}");
    }

    private static void DrawPipe(int px, int py, PieceType t, bool filled)
    {
        var bg = filled ? new Color(80, 160, 240, 255) : new Color(180, 180, 180, 255);
        int cx = px + Cell / 2, cy = py + Cell / 2;
        int conns = Connections(t);
        int thick = 8;

        if ((conns & 1) != 0) Raylib.DrawRectangle(cx - thick / 2, py, thick, cy - py, bg);
        if ((conns & 4) != 0) Raylib.DrawRectangle(cx - thick / 2, cy, thick, py + Cell - cy, bg);
        if ((conns & 8) != 0) Raylib.DrawRectangle(px, cy - thick / 2, cx - px, thick, bg);
        if ((conns & 2) != 0) Raylib.DrawRectangle(cx, cy - thick / 2, px + Cell - cx, thick, bg);

        if (t == PieceType.Source)
            Raylib.DrawCircle(cx, cy, 8, new Color(40, 100, 200, 255));
    }

    public void Close() { }
}
