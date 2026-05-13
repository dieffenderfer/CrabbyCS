using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Link Four — port of FUNGAMES/LINK4130 (R&amp;R Engineering, 1992/1994).
/// Mechanics drawn directly from LINK4.TXT:
///
///   • Two players stack red and blue marbles in a 7×7 grid.
///   • A win is four-in-a-row counting eight-directional adjacency:
///     horizontal, vertical, both diagonals.
///   • The game ends as soon as a player makes a "link" of four,
///     or when the grid fills with no winner.
///   • The original supports four canned AI levels plus a rule-based
///     custom search; we implement Easy/Medium/Hard as minimax depths
///     1/3/5 with a simple threat-count evaluation. The rule-based
///     scripting was a quirky Windows-3.x feature that's out of scope.
///
/// "Stacking" in the original means gravity drops, like Connect Four —
/// you choose a column and the marble lands in the lowest empty cell.
/// </summary>
public class LinkFourActivity : IActivity
{
    public Vector2 PanelSize => new(520, 540);
    public bool IsFinished { get; private set; }

    private const int FrameInset = 3;
    private const int Cols = 7;
    private const int Rows = 7;
    private const int Win = 4;

    private enum Player { None, Red, Blue }
    private enum Mode { VsAi, PassAndPlay }
    private enum Diff { Easy, Medium, Hard }

    private Player[,] _board = new Player[Cols, Rows];
    private Player _turn = Player.Red;
    private Player _winner = Player.None;
    private Mode _mode = Mode.VsAi;
    private Diff _difficulty = Diff.Medium;
    private int _hoverCol = -1;
    private float _aiThinkTimer;
    private bool _aiPending;
    private (int x, int y)[] _winLine = Array.Empty<(int, int)>();
    private string _status = "Red to move — click a column to drop a marble.";

    public void Load() => Reset();
    public void Close() => IsFinished = true;

    private void Reset()
    {
        Array.Clear(_board);
        _turn = Player.Red;
        _winner = Player.None;
        _winLine = Array.Empty<(int, int)>();
        _aiPending = false;
        _aiThinkTimer = 0;
        _status = "Red to move — click a column to drop a marble.";
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
            "New",
            _mode == Mode.VsAi ? "Mode: vs AI" : "Mode: 2-Player",
            _difficulty switch { Diff.Easy => "AI: Easy", Diff.Medium => "AI: Med", _ => "AI: Hard" },
        };
        switch (RetroWidgets.MenuBarHitTest(menuBar, items, local, leftPressed))
        {
            case 0: Reset(); return;
            case 1:
                _mode = _mode == Mode.VsAi ? Mode.PassAndPlay : Mode.VsAi;
                Reset();
                return;
            case 2:
                _difficulty = (Diff)(((int)_difficulty + 1) % 3);
                _status = "AI difficulty: " + _difficulty;
                return;
        }

        // Hover + drop on the grid.
        var grid = GridRect();
        _hoverCol = -1;
        if (RetroSkin.PointInRect(local, grid))
        {
            int col = (int)((local.X - grid.X) / (grid.Width / Cols));
            if (col >= 0 && col < Cols) _hoverCol = col;
        }

        if (_winner == Player.None && !_aiPending && leftPressed && _hoverCol >= 0
            && (_mode == Mode.PassAndPlay || _turn == Player.Red))
        {
            if (Drop(_hoverCol, _turn)) AfterMove();
        }

        // AI move runs after a short delay so the player can see the human
        // move land before the AI plays.
        if (_aiPending)
        {
            _aiThinkTimer -= delta;
            if (_aiThinkTimer <= 0)
            {
                _aiPending = false;
                int col = ChooseAiMove(_turn);
                if (col >= 0)
                {
                    Drop(col, _turn);
                    AfterMove();
                }
            }
        }
    }

    private bool Drop(int col, Player p)
    {
        for (int r = Rows - 1; r >= 0; r--)
        {
            if (_board[col, r] == Player.None)
            {
                _board[col, r] = p;
                return true;
            }
        }
        return false;
    }

    private void AfterMove()
    {
        if (CheckWin(_turn, out var line))
        {
            _winner = _turn;
            _winLine = line;
            _status = (_winner == Player.Red ? "Red" : "Blue") + " wins!";
            return;
        }
        if (BoardFull())
        {
            _winner = Player.None;
            _status = "Draw — no link of four.";
            return;
        }
        _turn = _turn == Player.Red ? Player.Blue : Player.Red;
        _status = (_turn == Player.Red ? "Red" : "Blue") + "'s turn.";
        if (_mode == Mode.VsAi && _turn == Player.Blue)
        {
            _aiPending = true;
            _aiThinkTimer = 0.4f;
        }
    }

    private bool BoardFull()
    {
        for (int c = 0; c < Cols; c++) if (_board[c, 0] == Player.None) return false;
        return true;
    }

    // ── Win check ─────────────────────────────────────────────────────────
    private bool CheckWin(Player p, out (int x, int y)[] line)
    {
        var dirs = new (int dx, int dy)[] { (1, 0), (0, 1), (1, 1), (1, -1) };
        for (int x = 0; x < Cols; x++)
        {
            for (int y = 0; y < Rows; y++)
            {
                if (_board[x, y] != p) continue;
                foreach (var (dx, dy) in dirs)
                {
                    int run = 1;
                    while (run < Win)
                    {
                        int nx = x + dx * run, ny = y + dy * run;
                        if (nx < 0 || ny < 0 || nx >= Cols || ny >= Rows) break;
                        if (_board[nx, ny] != p) break;
                        run++;
                    }
                    if (run >= Win)
                    {
                        var pts = new (int, int)[Win];
                        for (int i = 0; i < Win; i++) pts[i] = (x + dx * i, y + dy * i);
                        line = pts;
                        return true;
                    }
                }
            }
        }
        line = Array.Empty<(int, int)>();
        return false;
    }

    // ── Minimax AI ────────────────────────────────────────────────────────
    private int ChooseAiMove(Player ai)
    {
        int depth = _difficulty switch { Diff.Easy => 1, Diff.Medium => 3, _ => 5 };
        int bestCol = -1;
        int bestScore = int.MinValue;
        foreach (int c in ColumnOrder())
        {
            int row = TopOf(c);
            if (row < 0) continue;
            _board[c, row] = ai;
            int score = -Negamax(depth - 1, int.MinValue + 1, int.MaxValue, Opponent(ai), ai);
            _board[c, row] = Player.None;
            if (score > bestScore) { bestScore = score; bestCol = c; }
        }
        return bestCol;
    }

    private int Negamax(int depth, int alpha, int beta, Player toMove, Player ai)
    {
        if (CheckWin(ai, out _)) return 100000;
        if (CheckWin(Opponent(ai), out _)) return -100000;
        if (depth == 0 || BoardFull()) return Evaluate(ai);
        int best = int.MinValue + 1;
        foreach (int c in ColumnOrder())
        {
            int row = TopOf(c);
            if (row < 0) continue;
            _board[c, row] = toMove;
            int score = -Negamax(depth - 1, -beta, -alpha, Opponent(toMove), ai);
            _board[c, row] = Player.None;
            if (toMove != ai) score = -score; // flip sign so AI maximises its own score
            // Standard negamax already flips sign — the above adjustment is a
            // no-op for the AI's own moves and a double-negation for the
            // opponent's, ensuring the score is always expressed from the
            // AI's perspective at the root.
            if (score > best) best = score;
            if (best > alpha) alpha = best;
            if (alpha >= beta) break;
        }
        return best;
    }

    // Centre-out column ordering for the AI search — centre columns are
    // worth more in connect-4-likes so exploring them first widens the
    // alpha-beta cutoffs.
    private static IEnumerable<int> ColumnOrder()
    {
        int mid = Cols / 2;
        yield return mid;
        for (int d = 1; d <= mid; d++)
        {
            if (mid + d < Cols) yield return mid + d;
            if (mid - d >= 0) yield return mid - d;
        }
    }

    private int TopOf(int col)
    {
        for (int r = Rows - 1; r >= 0; r--)
            if (_board[col, r] == Player.None) return r;
        return -1;
    }

    private static Player Opponent(Player p) =>
        p == Player.Red ? Player.Blue : Player.Red;

    private int Evaluate(Player ai)
    {
        // Count two- and three-in-a-row windows for each side. Each 4-cell
        // window scores by how many friendly pieces it contains (with no
        // enemy pieces blocking it), so threats build smoothly toward a
        // win without needing hand-tuned heuristics.
        int score = 0;
        var dirs = new (int dx, int dy)[] { (1, 0), (0, 1), (1, 1), (1, -1) };
        var opp = Opponent(ai);
        for (int x = 0; x < Cols; x++)
            for (int y = 0; y < Rows; y++)
                foreach (var (dx, dy) in dirs)
                {
                    int ax = 0, ox = 0;
                    bool inBounds = true;
                    for (int i = 0; i < Win; i++)
                    {
                        int nx = x + dx * i, ny = y + dy * i;
                        if (nx < 0 || ny < 0 || nx >= Cols || ny >= Rows) { inBounds = false; break; }
                        if (_board[nx, ny] == ai) ax++;
                        else if (_board[nx, ny] == opp) ox++;
                    }
                    if (!inBounds) continue;
                    if (ax > 0 && ox > 0) continue; // contested → no score
                    if (ax > 0) score += ax * ax;
                    else if (ox > 0) score -= ox * ox;
                }
        return score;
    }

    // ── Layout / draw ─────────────────────────────────────────────────────
    private Rectangle GridRect()
    {
        float top = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 16;
        float side = Math.Min(PanelSize.X - 40, PanelSize.Y - top - RetroWidgets.StatusBarHeight - 24);
        return new Rectangle((PanelSize.X - side) / 2f, top, side, side);
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Link Four", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        var items = new[] {
            "New",
            _mode == Mode.VsAi ? "Mode: vs AI" : "Mode: 2-Player",
            _difficulty switch { Diff.Easy => "AI: Easy", Diff.Medium => "AI: Med", _ => "AI: Hard" },
        };
        RetroWidgets.MenuBarVisual(menuBar, items, -1);

        var grid = GridRect();
        var gridAbs = new Rectangle(panelOffset.X + grid.X, panelOffset.Y + grid.Y,
            grid.Width, grid.Height);
        Raylib.DrawRectangleRec(gridAbs, new Color((byte)90, (byte)108, (byte)164, (byte)255));
        Raylib.DrawRectangleLinesEx(gridAbs, 2, RetroSkin.DarkShadow);

        float cellW = grid.Width / Cols;
        float cellH = grid.Height / Rows;
        // Hover preview column highlight + ghost marble at the landing row.
        if (_hoverCol >= 0 && _winner == Player.None
            && (_mode == Mode.PassAndPlay || _turn == Player.Red))
        {
            float hx = gridAbs.X + _hoverCol * cellW;
            Raylib.DrawRectangle((int)hx, (int)gridAbs.Y, (int)cellW, (int)gridAbs.Height,
                new Color((byte)255, (byte)255, (byte)255, (byte)28));
            int landingRow = TopOf(_hoverCol);
            if (landingRow >= 0)
            {
                float cx = gridAbs.X + (_hoverCol + 0.5f) * cellW;
                float cy = gridAbs.Y + (landingRow + 0.5f) * cellH;
                var col = _turn == Player.Red
                    ? new Color((byte)220, (byte)80, (byte)80, (byte)160)
                    : new Color((byte)80, (byte)120, (byte)220, (byte)160);
                Raylib.DrawCircle((int)cx, (int)cy, Math.Min(cellW, cellH) * 0.42f, col);
            }
        }

        // Marble grid — empty cells are dim circles to read as the board
        // holes; placed marbles use red/blue.
        for (int x = 0; x < Cols; x++)
        {
            for (int y = 0; y < Rows; y++)
            {
                float cx = gridAbs.X + (x + 0.5f) * cellW;
                float cy = gridAbs.Y + (y + 0.5f) * cellH;
                float r = Math.Min(cellW, cellH) * 0.42f;
                var p = _board[x, y];
                Color col = p switch
                {
                    Player.Red => new Color((byte)220, (byte)80, (byte)80, (byte)255),
                    Player.Blue => new Color((byte)80, (byte)120, (byte)220, (byte)255),
                    _ => new Color((byte)60, (byte)76, (byte)128, (byte)255),
                };
                Raylib.DrawCircle((int)cx, (int)cy, r, col);
                if (p != Player.None)
                {
                    // Highlight gloss.
                    Raylib.DrawCircle((int)(cx - r * 0.3f), (int)(cy - r * 0.3f), r * 0.18f,
                        new Color((byte)255, (byte)255, (byte)255, (byte)100));
                }
            }
        }

        // Win line — draw a thicker ring around each winning marble.
        foreach (var (wx, wy) in _winLine)
        {
            float cx = gridAbs.X + (wx + 0.5f) * cellW;
            float cy = gridAbs.Y + (wy + 0.5f) * cellH;
            float r = Math.Min(cellW, cellH) * 0.46f;
            Raylib.DrawCircleLines((int)cx, (int)cy, r, RetroSkin.TitleActive);
            Raylib.DrawCircleLines((int)cx, (int)cy, r - 1, RetroSkin.TitleActive);
        }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(status, _status,
            _mode == Mode.VsAi ? $"vs AI ({_difficulty})" : "Pass and Play");
    }
}
