using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Stones — abstract placement on a 13×13 grid. Get five in a row
/// horizontally, vertically, or diagonally to win. Player is black, the
/// computer is white and uses a small heuristic-driven move picker that
/// scores the local pattern around each empty cell.
/// </summary>
public class StonesActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Grid = 13;
    private const int CellSize = 28;
    private const int Margin = 18;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + Grid * CellSize,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + Grid * CellSize + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private readonly RetroHelp _help = new()
    {
        Title = "Stones — How to play",
        Lines = new[]
        {
            "Place a black stone by clicking any empty grid point.",
            "The computer answers with a white stone.",
            "First to five in a row wins — horizontal, vertical,",
            "or either diagonal.",
            "(Beta: this is a small Five-in-a-Row stand-in",
            "for the original Stones mechanics.)",
        },
    };

    private const int Empty = 0, Black = 1, White = 2;
    private int[,] _board = new int[Grid, Grid];
    private int _toMove;
    private int _winner;
    private float _aiDelay;
    private readonly Random _rng = new();

    public void Load() => Reset();

    private void Reset()
    {
        for (int y = 0; y < Grid; y++) for (int x = 0; x < Grid; x++) _board[x, y] = Empty;
        _toMove = Black;
        _winner = 0;
        _aiDelay = 0;
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
            case 0: Reset(); return;
            case 1: _help.Visible = !_help.Visible; return;
        }
        if (_help.HandleInput(local, leftPressed, PanelSize)) return;

        if (_winner != 0) return;

        if (_toMove == Black && leftPressed)
        {
            float ox = FrameInset + Margin, oy = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
            int gx = (int)((local.X - ox) / CellSize);
            int gy = (int)((local.Y - oy) / CellSize);
            if (local.X < ox || local.Y < oy) return;
            if (gx < 0 || gx >= Grid || gy < 0 || gy >= Grid) return;
            if (_board[gx, gy] != Empty) return;
            _board[gx, gy] = Black;
            CheckWin(gx, gy, Black);
            if (_winner == 0) { _toMove = White; _aiDelay = 0.2f; }
        }

        if (_toMove == White)
        {
            _aiDelay -= delta;
            if (_aiDelay <= 0)
            {
                AiMove();
                _toMove = Black;
            }
        }
    }

    private void AiMove()
    {
        int bestScore = int.MinValue;
        var bests = new List<(int x, int y)>();
        for (int y = 0; y < Grid; y++)
            for (int x = 0; x < Grid; x++)
            {
                if (_board[x, y] != Empty) continue;
                if (!HasNeighbor(x, y, 2)) continue;
                int s = ScoreCell(x, y, White) + ScoreCell(x, y, Black);
                if (s > bestScore) { bestScore = s; bests.Clear(); bests.Add((x, y)); }
                else if (s == bestScore) bests.Add((x, y));
            }
        if (bests.Count == 0) bests.Add((Grid / 2, Grid / 2));
        var pick = bests[_rng.Next(bests.Count)];
        _board[pick.x, pick.y] = White;
        CheckWin(pick.x, pick.y, White);
    }

    private bool HasNeighbor(int x, int y, int radius)
    {
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || nx >= Grid || ny < 0 || ny >= Grid) continue;
                if (_board[nx, ny] != Empty) return true;
            }
        return false;
    }

    private int ScoreCell(int x, int y, int color)
    {
        int total = 0;
        foreach (var (dx, dy) in new[] { (1, 0), (0, 1), (1, 1), (1, -1) })
        {
            int run = 1;
            for (int k = 1; k < 5; k++)
            {
                int nx = x + dx * k, ny = y + dy * k;
                if (nx < 0 || nx >= Grid || ny < 0 || ny >= Grid) break;
                if (_board[nx, ny] != color) break;
                run++;
            }
            for (int k = 1; k < 5; k++)
            {
                int nx = x - dx * k, ny = y - dy * k;
                if (nx < 0 || nx >= Grid || ny < 0 || ny >= Grid) break;
                if (_board[nx, ny] != color) break;
                run++;
            }
            total += run switch { >= 5 => 100000, 4 => 1000, 3 => 100, 2 => 10, _ => 1 };
        }
        return total;
    }

    private void CheckWin(int x, int y, int color)
    {
        foreach (var (dx, dy) in new[] { (1, 0), (0, 1), (1, 1), (1, -1) })
        {
            int run = 1;
            for (int k = 1; k < 5; k++)
            {
                int nx = x + dx * k, ny = y + dy * k;
                if (nx < 0 || nx >= Grid || ny < 0 || ny >= Grid) break;
                if (_board[nx, ny] != color) break;
                run++;
            }
            for (int k = 1; k < 5; k++)
            {
                int nx = x - dx * k, ny = y - dy * k;
                if (nx < 0 || nx >= Grid || ny < 0 || ny >= Grid) break;
                if (_board[nx, ny] != color) break;
                run++;
            }
            if (run >= 5) { _winner = color; return; }
        }
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Stones", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", "Help" }, -1);

        float bx = panelOffset.X + FrameInset + Margin;
        float by = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
        var board = new Rectangle(bx - 4, by - 4, Grid * CellSize + 8, Grid * CellSize + 8);
        Raylib.DrawRectangleRec(board, new Color(196, 152, 92, 255));
        for (int i = 0; i <= Grid; i++)
        {
            int xx = (int)(bx + i * CellSize);
            int yy = (int)(by + i * CellSize);
            Raylib.DrawLine(xx, (int)by, xx, (int)by + Grid * CellSize, new Color(64, 40, 16, 255));
            Raylib.DrawLine((int)bx, yy, (int)bx + Grid * CellSize, yy, new Color(64, 40, 16, 255));
        }

        for (int y = 0; y < Grid; y++)
            for (int x = 0; x < Grid; x++)
            {
                if (_board[x, y] == Empty) continue;
                int cx = (int)(bx + x * CellSize + CellSize / 2);
                int cy = (int)(by + y * CellSize + CellSize / 2);
                var col = _board[x, y] == Black ? new Color(20, 20, 20, 255) : new Color(240, 240, 240, 255);
                Raylib.DrawCircle(cx, cy, CellSize / 2 - 3, col);
                Raylib.DrawCircleLines(cx, cy, CellSize / 2 - 3, new Color(0, 0, 0, 200));
                Raylib.DrawCircle(cx - 2, cy - 2, 2, _board[x, y] == Black ? new Color(80, 80, 80, 200) : new Color(255, 255, 255, 240));
            }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string state = _winner switch
        {
            1 => "Black wins!", 2 => "White wins",
            _ => _toMove == Black ? "Your move (black)" : "Computer thinking..."
        };
        RetroWidgets.StatusBar(status, state, "Five in a row");

        _help.Draw(panelOffset, PanelSize);
    }

    public void Close() { }
}
