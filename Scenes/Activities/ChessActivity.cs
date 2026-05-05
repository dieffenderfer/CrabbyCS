using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Chess vs computer. Standard piece movement (auto-queen promotion, no
/// castling, no en passant — kept compact for the Pack-4 cut). Click your
/// piece, click destination. Negamax with simple material+square AI.
/// </summary>
public class ChessActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Cell = 56;
    private const int Margin = 14;
    private const int Side = 8;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + Side * Cell,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + Side * Cell + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    // Pieces: P=1 N=2 B=3 R=4 Q=5 K=6, sign = color (positive = white)
    private const int P = 1, N = 2, B = 3, R = 4, Q = 5, K = 6;
    private int[,] _board = new int[Side, Side];
    private int _toMove = 1;            // +1 white, -1 black
    private (int x, int y)? _sel;
    private List<(int x, int y)> _legalDest = new();
    private string _result = "";
    private int _aiDepth = 3;
    private string _difficulty = "Normal";
    private float _aiThink;

    public void Load() => Reset();

    private void Reset()
    {
        _board = new int[Side, Side];
        for (int x = 0; x < Side; x++) { _board[x, 1] = -P; _board[x, 6] = P; }
        int[] back = { R, N, B, Q, K, B, N, R };
        for (int x = 0; x < Side; x++) { _board[x, 0] = -back[x]; _board[x, 7] = back[x]; }
        _toMove = 1;
        _sel = null;
        _legalDest.Clear();
        _result = "";
        _aiThink = 0;
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
        switch (RetroWidgets.MenuBarHitTest(menuBar, new[] { "New", "Easy", "Normal", "Hard" }, local, leftPressed))
        {
            case 0: Reset(); return;
            case 1: _difficulty = "Easy"; _aiDepth = 2; Reset(); return;
            case 2: _difficulty = "Normal"; _aiDepth = 3; Reset(); return;
            case 3: _difficulty = "Hard"; _aiDepth = 4; Reset(); return;
        }

        if (_result != "") return;

        if (_toMove == 1 && leftPressed)
        {
            float ox = FrameInset + Margin;
            float oy = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
            int gx = (int)((local.X - ox) / Cell);
            int gy = (int)((local.Y - oy) / Cell);
            if (gx < 0 || gx >= Side || gy < 0 || gy >= Side) return;

            if (_sel == null)
            {
                if (Math.Sign(_board[gx, gy]) == 1)
                {
                    _sel = (gx, gy);
                    _legalDest = LegalMovesFrom(gx, gy, 1);
                }
                return;
            }
            // Reselect own piece
            if (Math.Sign(_board[gx, gy]) == 1) { _sel = (gx, gy); _legalDest = LegalMovesFrom(gx, gy, 1); return; }
            // Try move
            if (_legalDest.Contains((gx, gy)))
            {
                MakeMove(_sel.Value.x, _sel.Value.y, gx, gy);
                _sel = null; _legalDest.Clear();
                _toMove = -1;
                _aiThink = 0.2f;
                EvaluateEnd(-1);
            }
            else { _sel = null; _legalDest.Clear(); }
        }

        if (_toMove == -1)
        {
            _aiThink -= delta;
            if (_aiThink <= 0)
            {
                AiMove();
                _toMove = 1;
                EvaluateEnd(1);
            }
        }
    }

    private void EvaluateEnd(int side)
    {
        var moves = AllLegalMoves(side);
        if (moves.Count > 0) return;
        if (KingInCheck(side)) _result = side == 1 ? "Checkmate — computer wins" : "Checkmate — you win!";
        else _result = "Stalemate";
    }

    private void MakeMove(int sx, int sy, int dx, int dy)
    {
        int p = _board[sx, sy];
        _board[dx, dy] = p;
        _board[sx, sy] = 0;
        // Auto-queen promotion
        if (Math.Abs(p) == P && (dy == 0 || dy == 7))
            _board[dx, dy] = Math.Sign(p) * Q;
    }

    private List<(int x, int y, int dx, int dy)> AllLegalMoves(int side)
    {
        var list = new List<(int x, int y, int dx, int dy)>();
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                if (Math.Sign(_board[x, y]) != side) continue;
                foreach (var (dx, dy) in LegalMovesFrom(x, y, side))
                    list.Add((x, y, dx, dy));
            }
        return list;
    }

    private List<(int x, int y)> LegalMovesFrom(int sx, int sy, int side)
    {
        var pseudo = PseudoMoves(sx, sy);
        var legal = new List<(int x, int y)>();
        foreach (var (dx, dy) in pseudo)
        {
            int captured = _board[dx, dy];
            int piece = _board[sx, sy];
            _board[dx, dy] = piece; _board[sx, sy] = 0;
            if (!KingInCheck(side)) legal.Add((dx, dy));
            _board[sx, sy] = piece; _board[dx, dy] = captured;
        }
        return legal;
    }

    private List<(int x, int y)> PseudoMoves(int sx, int sy)
    {
        var moves = new List<(int x, int y)>();
        int p = _board[sx, sy]; if (p == 0) return moves;
        int side = Math.Sign(p);
        switch (Math.Abs(p))
        {
            case P:
                int dy = -side;
                if (InBoard(sx, sy + dy) && _board[sx, sy + dy] == 0)
                {
                    moves.Add((sx, sy + dy));
                    int startRow = side == 1 ? 6 : 1;
                    if (sy == startRow && _board[sx, sy + 2 * dy] == 0)
                        moves.Add((sx, sy + 2 * dy));
                }
                foreach (int ddx in new[] { -1, 1 })
                {
                    int nx = sx + ddx, ny = sy + dy;
                    if (InBoard(nx, ny) && _board[nx, ny] != 0 && Math.Sign(_board[nx, ny]) != side)
                        moves.Add((nx, ny));
                }
                break;
            case N:
                foreach (var (ddx, ddy) in new[] { (1, 2), (2, 1), (-1, 2), (-2, 1), (1, -2), (2, -1), (-1, -2), (-2, -1) })
                {
                    int nx = sx + ddx, ny = sy + ddy;
                    if (InBoard(nx, ny) && Math.Sign(_board[nx, ny]) != side) moves.Add((nx, ny));
                }
                break;
            case B: SlideMoves(sx, sy, side, moves, new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) }); break;
            case R: SlideMoves(sx, sy, side, moves, new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }); break;
            case Q: SlideMoves(sx, sy, side, moves, new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) }); break;
            case K:
                for (int ddx = -1; ddx <= 1; ddx++)
                    for (int ddy = -1; ddy <= 1; ddy++)
                    {
                        if (ddx == 0 && ddy == 0) continue;
                        int nx = sx + ddx, ny = sy + ddy;
                        if (InBoard(nx, ny) && Math.Sign(_board[nx, ny]) != side) moves.Add((nx, ny));
                    }
                break;
        }
        return moves;
    }

    private void SlideMoves(int sx, int sy, int side, List<(int x, int y)> moves, (int dx, int dy)[] dirs)
    {
        foreach (var (ddx, ddy) in dirs)
        {
            int nx = sx + ddx, ny = sy + ddy;
            while (InBoard(nx, ny))
            {
                if (_board[nx, ny] == 0) moves.Add((nx, ny));
                else { if (Math.Sign(_board[nx, ny]) != side) moves.Add((nx, ny)); break; }
                nx += ddx; ny += ddy;
            }
        }
    }

    private bool InBoard(int x, int y) => x >= 0 && x < Side && y >= 0 && y < Side;

    private bool KingInCheck(int side)
    {
        // Find king
        int kx = -1, ky = -1;
        for (int y = 0; y < Side; y++) for (int x = 0; x < Side; x++)
                if (_board[x, y] == side * K) { kx = x; ky = y; }
        if (kx < 0) return true;
        // Any opponent pseudo-move attacks (kx, ky)?
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                if (Math.Sign(_board[x, y]) != -side) continue;
                foreach (var (dx, dy) in PseudoMoves(x, y))
                    if (dx == kx && dy == ky) return true;
            }
        return false;
    }

    // ── AI ────────────────────────────────────────────────────────────────
    private static readonly int[] PieceValue = { 0, 100, 320, 330, 500, 900, 20000 };

    private int Evaluate(int side)
    {
        int v = 0;
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                int p = _board[x, y];
                if (p == 0) continue;
                int sign = Math.Sign(p);
                int abs = Math.Abs(p);
                int pv = PieceValue[abs];
                // Center bias for pawns and knights
                if (abs == P || abs == N)
                {
                    int center = 4 - Math.Min(Math.Abs(3 - x), Math.Abs(4 - x));
                    pv += center * 4;
                }
                v += sign * pv;
            }
        return side * v;
    }

    private int Negamax(int depth, int side, int alpha, int beta)
    {
        if (depth == 0) return Evaluate(side);
        var moves = AllLegalMoves(side);
        if (moves.Count == 0) return KingInCheck(side) ? -100000 : 0;
        int best = int.MinValue + 1;
        foreach (var (sx, sy, dx, dy) in moves)
        {
            int captured = _board[dx, dy];
            int piece = _board[sx, sy];
            _board[dx, dy] = piece; _board[sx, sy] = 0;
            // Auto-queen promotion preview
            int promo = 0;
            if (Math.Abs(piece) == P && (dy == 0 || dy == 7))
            { promo = piece; _board[dx, dy] = Math.Sign(piece) * Q; }
            int v = -Negamax(depth - 1, -side, -beta, -alpha);
            if (promo != 0) _board[dx, dy] = promo;
            _board[sx, sy] = piece; _board[dx, dy] = captured;
            if (v > best) best = v;
            if (best > alpha) alpha = best;
            if (alpha >= beta) return best;
        }
        return best;
    }

    private void AiMove()
    {
        var moves = AllLegalMoves(-1);
        if (moves.Count == 0) return;
        int bestScore = int.MinValue;
        var best = moves[0];
        foreach (var m in moves)
        {
            var (sx, sy, dx, dy) = m;
            int captured = _board[dx, dy];
            int piece = _board[sx, sy];
            _board[dx, dy] = piece; _board[sx, sy] = 0;
            int promo = 0;
            if (Math.Abs(piece) == P && (dy == 0 || dy == 7))
            { promo = piece; _board[dx, dy] = -Q; }
            int v = -Negamax(_aiDepth - 1, 1, int.MinValue + 1, -bestScore);
            if (promo != 0) _board[dx, dy] = promo;
            _board[sx, sy] = piece; _board[dx, dy] = captured;
            if (v > bestScore) { bestScore = v; best = m; }
        }
        MakeMove(best.x, best.y, best.dx, best.dy);
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Chess", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", "Easy", "Normal", "Hard" }, -1);

        float bx = panelOffset.X + FrameInset + Margin;
        float by = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;

        var light = new Color(232, 216, 184, 255);
        var dark = new Color(120, 88, 56, 255);

        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                Raylib.DrawRectangle((int)(bx + x * Cell), (int)(by + y * Cell), Cell, Cell,
                    (x + y) % 2 == 0 ? light : dark);
            }

        if (_sel != null)
        {
            Raylib.DrawRectangle((int)(bx + _sel.Value.x * Cell), (int)(by + _sel.Value.y * Cell),
                Cell, Cell, new Color(120, 200, 80, 120));
        }
        foreach (var (x, y) in _legalDest)
            Raylib.DrawCircle((int)(bx + x * Cell + Cell / 2), (int)(by + y * Cell + Cell / 2),
                8, new Color(60, 200, 60, 180));

        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                int p = _board[x, y]; if (p == 0) continue;
                string g = Math.Abs(p) switch
                { P => "♙", N => "♘", B => "♗", R => "♖", Q => "♕", K => "♔", _ => "?" };
                var col = p > 0 ? Color.White : Color.Black;
                int sz = Cell - 16;
                int tx = (int)(bx + x * Cell + (Cell - sz) / 2);
                int ty = (int)(by + y * Cell + (Cell - sz) / 2 - 4);
                RetroSkin.DrawText(g, tx, ty, col, sz);
                // Black piece outlined
                if (p < 0)
                    RetroSkin.DrawText(g, tx, ty, new Color(255, 255, 255, 80), sz);
            }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string state = _result.Length > 0 ? _result
            : _toMove == 1 ? "Your move (white)" : "Computer thinking...";
        RetroWidgets.StatusBar(status, state, _difficulty);
    }

    public void Close() { }
}
