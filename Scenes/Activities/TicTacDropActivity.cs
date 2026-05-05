using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Tic Tac Drop — Connect-Four on a 7×6 board. Click a column to drop your
/// disc; first to four in a row (any direction) wins. Computer plays the
/// other color with a small minimax.
/// </summary>
public class TicTacDropActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Cols = 7;
    private const int Rows = 6;
    private const int Cell = 50;
    private const int Margin = 18;
    private const int Empty = 0, Red = 1, Yellow = 2;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + Cols * Cell,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + Rows * Cell + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private readonly RetroHelp _help = new()
    {
        Title = "Tic Tac Drop — How to play",
        Lines = new[]
        {
            "Click any column to drop your red disc into it.",
            "Discs stack from the bottom up — gravity does the rest.",
            "First to four in a row wins (horizontal, vertical, or diagonal).",
            "Computer plays yellow.",
            "Easy / Normal / Hard set the AI search depth.",
        },
    };

    private int[,] _board = new int[Cols, Rows];
    private int _toMove;
    private int _winner;
    private int _aiDepth = 5;
    private string _difficulty = "Normal";
    private float _aiDelay;

    public void Load() => Reset();

    private void Reset()
    {
        for (int x = 0; x < Cols; x++) for (int y = 0; y < Rows; y++) _board[x, y] = Empty;
        _toMove = Red;
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
        switch (RetroWidgets.MenuBarHitTest(menuBar, new[] { "New", "Easy", "Normal", "Hard", "Help" }, local, leftPressed))
        {
            case 0: Reset(); return;
            case 1: _difficulty = "Easy"; _aiDepth = 3; Reset(); return;
            case 2: _difficulty = "Normal"; _aiDepth = 5; Reset(); return;
            case 3: _difficulty = "Hard"; _aiDepth = 7; Reset(); return;
            case 4: _help.Visible = !_help.Visible; return;
        }
        if (_help.HandleInput(local, leftPressed, PanelSize)) return;

        if (_winner != 0) return;

        if (_toMove == Red && leftPressed)
        {
            float ox = FrameInset + Margin;
            int gx = (int)((local.X - ox) / Cell);
            if (gx < 0 || gx >= Cols) return;
            if (DropIn(gx, Red))
            {
                CheckWin(Red);
                if (_winner == 0) { _toMove = Yellow; _aiDelay = 0.25f; }
            }
        }

        if (_toMove == Yellow)
        {
            _aiDelay -= delta;
            if (_aiDelay <= 0)
            {
                AiMove();
                CheckWin(Yellow);
                _toMove = Red;
            }
        }
    }

    private bool DropIn(int col, int color)
    {
        for (int y = Rows - 1; y >= 0; y--)
            if (_board[col, y] == Empty) { _board[col, y] = color; return true; }
        return false;
    }

    private void Undrop(int col)
    {
        for (int y = 0; y < Rows; y++)
            if (_board[col, y] != Empty) { _board[col, y] = Empty; return; }
    }

    private void AiMove()
    {
        int bestScore = int.MinValue;
        int best = -1;
        for (int c = 0; c < Cols; c++)
        {
            if (_board[c, 0] != Empty) continue;
            DropIn(c, Yellow);
            int s = -Negamax(_aiDepth - 1, Red, int.MinValue + 1, -bestScore);
            Undrop(c);
            if (s > bestScore) { bestScore = s; best = c; }
        }
        if (best >= 0) DropIn(best, Yellow);
    }

    private int Negamax(int depth, int side, int alpha, int beta)
    {
        int w = CheckWinnerOnly();
        if (w == side) return 1000;
        if (w != 0 && w != 3) return -1000;
        if (w == 3) return 0;
        if (depth == 0) return Heuristic(side);

        int best = int.MinValue + 1;
        for (int c = 0; c < Cols; c++)
        {
            if (_board[c, 0] != Empty) continue;
            DropIn(c, side);
            int s = -Negamax(depth - 1, Other(side), -beta, -alpha);
            Undrop(c);
            if (s > best) best = s;
            if (best > alpha) alpha = best;
            if (alpha >= beta) return best;
        }
        return best;
    }

    private static int Other(int p) => p == Red ? Yellow : Red;

    private int Heuristic(int side)
    {
        int score = 0;
        foreach (var line in AllLines())
        {
            int mine = 0, opp = 0;
            foreach (var (x, y) in line)
            {
                if (_board[x, y] == side) mine++;
                else if (_board[x, y] != Empty) opp++;
            }
            if (mine > 0 && opp == 0) score += mine * mine;
            if (opp > 0 && mine == 0) score -= opp * opp;
        }
        return score;
    }

    private static IEnumerable<(int x, int y)[]> AllLines()
    {
        for (int y = 0; y < Rows; y++)
            for (int x = 0; x <= Cols - 4; x++)
                yield return new[] { (x, y), (x + 1, y), (x + 2, y), (x + 3, y) };
        for (int x = 0; x < Cols; x++)
            for (int y = 0; y <= Rows - 4; y++)
                yield return new[] { (x, y), (x, y + 1), (x, y + 2), (x, y + 3) };
        for (int x = 0; x <= Cols - 4; x++)
            for (int y = 0; y <= Rows - 4; y++)
                yield return new[] { (x, y), (x + 1, y + 1), (x + 2, y + 2), (x + 3, y + 3) };
        for (int x = 0; x <= Cols - 4; x++)
            for (int y = 3; y < Rows; y++)
                yield return new[] { (x, y), (x + 1, y - 1), (x + 2, y - 2), (x + 3, y - 3) };
    }

    private int CheckWinnerOnly()
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
        for (int x = 0; x < Cols; x++) if (_board[x, 0] == Empty) full = false;
        return full ? 3 : 0;
    }

    private void CheckWin(int player) { int w = CheckWinnerOnly(); if (w != 0) _winner = w; }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Tic Tac Drop", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", "Easy", "Normal", "Hard", "Help" }, -1);

        float bx = panelOffset.X + FrameInset + Margin;
        float by = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
        var board = new Rectangle(bx - 4, by - 4, Cols * Cell + 8, Rows * Cell + 8);
        Raylib.DrawRectangleRec(board, new Color(48, 96, 192, 255));

        for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Cols; x++)
            {
                int cx = (int)(bx + x * Cell + Cell / 2);
                int cy = (int)(by + y * Cell + Cell / 2);
                int v = _board[x, y];
                Color col = v switch
                {
                    Red => new Color(220, 60, 60, 255),
                    Yellow => new Color(232, 200, 64, 255),
                    _ => new Color(16, 32, 80, 255),
                };
                Raylib.DrawCircle(cx, cy, Cell / 2 - 4, col);
                if (v != Empty)
                    Raylib.DrawCircle(cx - 4, cy - 4, 4, new Color(255, 255, 255, 80));
            }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string state = _winner switch
        {
            Red => "Red wins!", Yellow => "Yellow wins", 3 => "Draw",
            _ => _toMove == Red ? "Your move (red)" : "Computer thinking..."
        };
        RetroWidgets.StatusBar(status, state, _difficulty);

        _help.Draw(panelOffset, PanelSize);
    }

    public void Close() { }
}
