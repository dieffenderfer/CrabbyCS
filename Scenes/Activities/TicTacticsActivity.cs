using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// 4×4 tic-tac-toe variant. Player is X, computer is O. Win by getting four
/// in a row horizontally, vertically, or on a long diagonal. AI uses minimax
/// with alpha-beta and a depth cap so move time stays snappy.
/// </summary>
public class TicTacticsActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Grid = 4;
    private const int CellSize = 56;
    private const int Margin = 20;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + Grid * CellSize,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + Grid * CellSize + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private const int Empty = 0, X = 1, O = 2;
    private int[,] _board = new int[Grid, Grid];
    private int _toMove;
    private int _winner;          // 0 none, 1 X, 2 O, 3 draw
    private int _aiDepth = 4;     // "Easy"=2, "Normal"=4, "Hard"=6
    private string _difficulty = "Normal";
    private float _aiThinkDelay;

    public void Load() => Reset();

    private void Reset()
    {
        for (int y = 0; y < Grid; y++)
            for (int x = 0; x < Grid; x++)
                _board[x, y] = Empty;
        _toMove = X;
        _winner = 0;
        _aiThinkDelay = 0;
    }

    private Vector2 CellTopLeft(int x, int y)
    {
        return new Vector2(
            FrameInset + Margin + x * CellSize,
            FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin + y * CellSize);
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
        int m = RetroWidgets.MenuBarHitTest(menuBar, new[] { "New", "Easy", "Normal", "Hard" }, local, leftPressed);
        switch (m)
        {
            case 0: Reset(); break;
            case 1: _difficulty = "Easy";   _aiDepth = 2; Reset(); break;
            case 2: _difficulty = "Normal"; _aiDepth = 4; Reset(); break;
            case 3: _difficulty = "Hard";   _aiDepth = 6; Reset(); break;
        }

        if (_winner != 0) return;

        if (_toMove == X && leftPressed)
        {
            var origin = CellTopLeft(0, 0);
            int gx = (int)((local.X - origin.X) / CellSize);
            int gy = (int)((local.Y - origin.Y) / CellSize);
            if (local.X < origin.X || local.Y < origin.Y) return;
            if (gx < 0 || gx >= Grid || gy < 0 || gy >= Grid) return;
            if (_board[gx, gy] != Empty) return;
            _board[gx, gy] = X;
            EvaluateEnd();
            if (_winner == 0) { _toMove = O; _aiThinkDelay = 0.25f; }
        }

        if (_toMove == O)
        {
            _aiThinkDelay -= delta;
            if (_aiThinkDelay <= 0)
            {
                AiMove();
                EvaluateEnd();
                _toMove = X;
            }
        }
    }

    private void AiMove()
    {
        int bestScore = int.MinValue;
        (int x, int y) best = (-1, -1);
        for (int y = 0; y < Grid; y++)
            for (int x = 0; x < Grid; x++)
            {
                if (_board[x, y] != Empty) continue;
                _board[x, y] = O;
                int s = -Negamax(_aiDepth - 1, X, int.MinValue + 1, -bestScore);
                _board[x, y] = Empty;
                if (s > bestScore) { bestScore = s; best = (x, y); }
            }
        if (best.x >= 0) _board[best.x, best.y] = O;
    }

    private int Negamax(int depth, int player, int alpha, int beta)
    {
        int term = TerminalScore(player);
        if (term != int.MinValue) return term;
        if (depth == 0) return Heuristic(player);

        int best = int.MinValue + 1;
        for (int y = 0; y < Grid; y++)
            for (int x = 0; x < Grid; x++)
            {
                if (_board[x, y] != Empty) continue;
                _board[x, y] = player;
                int s = -Negamax(depth - 1, Other(player), -beta, -alpha);
                _board[x, y] = Empty;
                if (s > best) best = s;
                if (best > alpha) alpha = best;
                if (alpha >= beta) return best;
            }
        return best;
    }

    private static int Other(int p) => p == X ? O : X;

    private int TerminalScore(int sideToMove)
    {
        int w = CheckWinner();
        if (w == sideToMove) return 1000;
        if (w == Other(sideToMove)) return -1000;
        if (w == 3) return 0;
        return int.MinValue;
    }

    private int Heuristic(int sideToMove)
    {
        // +score for lines where only sideToMove can still win, weighted by count
        int score = 0;
        foreach (var line in AllLines())
        {
            int mine = 0, opp = 0;
            foreach (var (x, y) in line)
            {
                if (_board[x, y] == sideToMove) mine++;
                else if (_board[x, y] != Empty) opp++;
            }
            if (mine > 0 && opp == 0) score += mine * mine;
            if (opp > 0 && mine == 0) score -= opp * opp;
        }
        return score;
    }

    private static IEnumerable<(int x, int y)[]> AllLines()
    {
        for (int y = 0; y < Grid; y++)
            yield return new[] { (0, y), (1, y), (2, y), (3, y) };
        for (int x = 0; x < Grid; x++)
            yield return new[] { (x, 0), (x, 1), (x, 2), (x, 3) };
        yield return new[] { (0, 0), (1, 1), (2, 2), (3, 3) };
        yield return new[] { (3, 0), (2, 1), (1, 2), (0, 3) };
    }

    private int CheckWinner()
    {
        foreach (var line in AllLines())
        {
            int v = _board[line[0].x, line[0].y];
            if (v == Empty) continue;
            bool all = true;
            foreach (var (x, y) in line) if (_board[x, y] != v) { all = false; break; }
            if (all) return v;
        }
        bool full = true;
        for (int y = 0; y < Grid; y++) for (int x = 0; x < Grid; x++) if (_board[x, y] == Empty) full = false;
        return full ? 3 : 0;
    }

    private void EvaluateEnd()
    {
        int w = CheckWinner();
        if (w != 0) _winner = w;
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "TicTactics", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", "Easy", "Normal", "Hard" }, -1);

        // Body inset
        float bodyY = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
        var boardRect = new Rectangle(panelOffset.X + FrameInset + Margin,
            panelOffset.Y + bodyY, Grid * CellSize, Grid * CellSize);
        RetroSkin.DrawSunken(boardRect, RetroSkin.Face);

        // Grid lines
        for (int i = 1; i < Grid; i++)
        {
            int xx = (int)(boardRect.X + i * CellSize);
            int yy = (int)(boardRect.Y + i * CellSize);
            Raylib.DrawLine(xx, (int)boardRect.Y, xx, (int)(boardRect.Y + boardRect.Height), RetroSkin.Shadow);
            Raylib.DrawLine((int)boardRect.X, yy, (int)(boardRect.X + boardRect.Width), yy, RetroSkin.Shadow);
        }

        for (int y = 0; y < Grid; y++)
            for (int x = 0; x < Grid; x++)
            {
                int cx = (int)(boardRect.X + x * CellSize + CellSize / 2);
                int cy = (int)(boardRect.Y + y * CellSize + CellSize / 2);
                if (_board[x, y] == X) DrawX(cx, cy);
                else if (_board[x, y] == O) DrawO(cx, cy);
            }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string state = _winner switch
        {
            1 => "You win!", 2 => "Computer wins", 3 => "Draw",
            _ => _toMove == X ? "Your move (X)" : "Computer thinking..."
        };
        RetroWidgets.StatusBar(status, state, _difficulty);
    }

    private static void DrawX(int cx, int cy)
    {
        var col = new Color(0, 0, 192, 255);
        for (int t = -2; t <= 2; t++)
        {
            Raylib.DrawLine(cx - 18, cy - 18 + t, cx + 18, cy + 18 + t, col);
            Raylib.DrawLine(cx - 18, cy + 18 + t, cx + 18, cy - 18 + t, col);
        }
    }

    private static void DrawO(int cx, int cy)
    {
        var col = new Color(192, 0, 0, 255);
        Raylib.DrawCircle(cx, cy, 20, col);
        Raylib.DrawCircle(cx, cy, 16, RetroSkin.Face);
    }

    public void Close() { }
}
