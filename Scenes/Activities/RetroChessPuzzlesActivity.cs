using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Chess puzzles in the retro Win9x style. Each puzzle ships a starting
/// position (FEN) and a single expected best move; the player tries it, gets
/// graded, and steps to the next puzzle. Bundled puzzle set is constructed
/// from scratch and is intentionally short — small didactic positions, not
/// any specific real-game compositions.
/// </summary>
public class RetroChessPuzzlesActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Cell = 56;
    private const int Margin = 14;
    private const int Side = 8;
    private const int InfoWidth = 180;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + Side * Cell + InfoWidth + Margin,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + Side * Cell + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private const int P = 1, N = 2, B = 3, R = 4, Q = 5, K = 6;
    private int[,] _board = new int[Side, Side];
    private int _toMove = 1;
    private (int x, int y)? _sel;
    private List<(int x, int y)> _legalDest = new();
    private string _msg = "";

    // Puzzles: FEN-ish string + expected best move (uci format from-to e.g. "e2e4")
    private record class Puzzle(string Title, string Fen, int SideToMove, string Expected);
    private static readonly Puzzle[] Puzzles =
    {
        new("White to mate in 1 — back rank",
            "6k1/5ppp/8/8/8/8/8/R6K", 1, "a1a8"),
        new("White to mate in 1 — queen ladder",
            "7k/8/6Q1/8/8/8/8/7K", 1, "g6g7"),
        new("White to mate in 1 — supported queen",
            "5rk1/5ppp/8/8/8/8/3Q4/3R3K", 1, "d2d8"),
        new("White to mate in 1 — knight + bishop",
            "6k1/6pp/8/6N1/8/8/4B3/7K", 1, "e2c4"),
        new("White to win the queen — fork",
            "4k3/8/4q3/8/3N4/8/8/4K3", 1, "d4f5"),
        new("White to mate in 1 — rook lift",
            "k7/2R5/1K6/8/8/8/8/8", 1, "c7c8"),
        new("White to win material — pin",
            "4k3/8/8/3q4/8/8/3R4/3K4", 1, "d2d5"),
    };

    private int _puzzleIdx;
    private bool _solved;
    private bool _failed;
    private int _solvedCount;

    // 16×16 piece bitmaps — same shapes as the non-retro ChessPuzzleActivity so
    // the two games look like siblings. Each ushort is one row, bit (15-col) =
    // pixel at column col. The retro skin's font (W95F.otf) doesn't carry the
    // ♔♕♖♗♘♙ Unicode glyphs, so rendering pieces with text gives "?". Drawing
    // them as procedurally-generated textures sidesteps the font entirely.
    private static readonly Dictionary<int, ushort[]> PieceBitmaps = new()
    {
        [K] = new ushort[] { 0x0000, 0x0180, 0x0180, 0x07E0, 0x07E0, 0x03C0, 0x07E0, 0x0FF0, 0x0FF0, 0x1FF8, 0x3FFC, 0x7FFE, 0x3FFC, 0x1FF8, 0x7FFE, 0x0000 },
        [Q] = new ushort[] { 0x0000, 0x4992, 0x7FFE, 0x7FFE, 0x3FFC, 0x1FF8, 0x0FF0, 0x0FF0, 0x0FF0, 0x1FF8, 0x3FFC, 0x3FFC, 0x7FFE, 0x7FFE, 0xFFFF, 0x0000 },
        [R] = new ushort[] { 0x0000, 0x6666, 0x6666, 0x7FFE, 0x7FFE, 0x3FFC, 0x0FF0, 0x0FF0, 0x0FF0, 0x0FF0, 0x0FF0, 0x1FF8, 0x3FFC, 0x7FFE, 0xFFFF, 0x0000 },
        [B] = new ushort[] { 0x0000, 0x0180, 0x03C0, 0x03C0, 0x0660, 0x07E0, 0x0FF0, 0x0FF0, 0x07E0, 0x0FF0, 0x1FF8, 0x3FFC, 0x7FFE, 0x3FFC, 0xFFFF, 0x0000 },
        [N] = new ushort[] { 0x0000, 0x0FC0, 0x1FE0, 0x3FEC, 0x3FFC, 0x3FFC, 0x7BFF, 0x3FFC, 0x7FFE, 0x1FE0, 0x1FE0, 0x3FF0, 0x3FFC, 0x7FFE, 0xFFFF, 0x0000 },
        [P] = new ushort[] { 0x0000, 0x03C0, 0x07E0, 0x07E0, 0x07E0, 0x03C0, 0x03C0, 0x07E0, 0x0FF0, 0x0FF0, 0x1FF8, 0x3FFC, 0x7FFE, 0x7FFE, 0xFFFF, 0x0000 },
    };

    // Key: signed piece (positive = white, negative = black).
    private readonly Dictionary<int, Texture2D> _pieceTextures = new();

    private readonly RetroHelp _help = new()
    {
        Title = "Chess Puzzles — How to play",
        Lines = new[]
        {
            "Click your piece, click the destination to move it.",
            "Each position has one best move. Find it and you advance.",
            "A wrong move marks the puzzle failed; click Next to skip.",
            "Highlighted dots show the legal moves of the selected piece.",
        },
    };

    public void Load()
    {
        LoadPieceTextures();
        LoadPuzzle(0);
    }

    private void LoadPieceTextures()
    {
        const int gridSize = 16;
        const int pxSize = 2;            // 2× upscale → 32×32 texture, point-filtered for crisp pixels
        const int texSize = gridSize * pxSize;

        foreach (var (piece, bitmap) in PieceBitmaps)
        {
            GeneratePieceTexture(piece, bitmap, isWhite: true, texSize, pxSize, gridSize);
            GeneratePieceTexture(-piece, bitmap, isWhite: false, texSize, pxSize, gridSize);
        }
    }

    private void GeneratePieceTexture(int signedPiece, ushort[] bitmap, bool isWhite,
                                      int texSize, int pxSize, int gridSize)
    {
        var img = Raylib.GenImageColor(texSize, texSize, new Color(0, 0, 0, 0));
        var fillColor = isWhite ? new Color(245, 240, 225, 255) : new Color(35, 25, 20, 255);
        var outlineColor = isWhite ? new Color(20, 15, 10, 255) : new Color(180, 165, 145, 255);

        bool IsFilled(int r, int c) =>
            r >= 0 && r < gridSize && c >= 0 && c < gridSize &&
            (bitmap[r] & (1 << (gridSize - 1 - c))) != 0;

        void DrawBlock(int gridR, int gridC, Color color)
        {
            int x0 = gridC * pxSize;
            int y0 = gridR * pxSize;
            for (int dy = 0; dy < pxSize; dy++)
                for (int dx = 0; dx < pxSize; dx++)
                {
                    int x = x0 + dx, y = y0 + dy;
                    if (x >= 0 && x < texSize && y >= 0 && y < texSize)
                        Raylib.ImageDrawPixel(ref img, x, y, color);
                }
        }

        for (int r = 0; r < gridSize; r++)
            for (int c = 0; c < gridSize; c++)
            {
                if (!IsFilled(r, c)) continue;
                if (!IsFilled(r - 1, c)) DrawBlock(r - 1, c, outlineColor);
                if (!IsFilled(r + 1, c)) DrawBlock(r + 1, c, outlineColor);
                if (!IsFilled(r, c - 1)) DrawBlock(r, c - 1, outlineColor);
                if (!IsFilled(r, c + 1)) DrawBlock(r, c + 1, outlineColor);
            }

        for (int r = 0; r < gridSize; r++)
            for (int c = 0; c < gridSize; c++)
                if (IsFilled(r, c)) DrawBlock(r, c, fillColor);

        var tex = Raylib.LoadTextureFromImage(img);
        Raylib.SetTextureFilter(tex, TextureFilter.Point);
        Raylib.UnloadImage(img);
        _pieceTextures[signedPiece] = tex;
    }

    private void LoadPuzzle(int idx)
    {
        _puzzleIdx = ((idx % Puzzles.Length) + Puzzles.Length) % Puzzles.Length;
        var p = Puzzles[_puzzleIdx];
        ParseFen(p.Fen);
        _toMove = p.SideToMove;
        _sel = null; _legalDest.Clear();
        _solved = false; _failed = false;
        _msg = p.Title;
    }

    private void ParseFen(string fen)
    {
        _board = new int[Side, Side];
        // Read just the piece-placement field
        var board = fen.Split(' ')[0];
        var rows = board.Split('/');
        for (int r = 0; r < Math.Min(rows.Length, Side); r++)
        {
            int x = 0;
            foreach (var ch in rows[r])
            {
                if (char.IsDigit(ch)) { x += ch - '0'; continue; }
                int sign = char.IsUpper(ch) ? 1 : -1;
                int piece = char.ToUpper(ch) switch
                {
                    'P' => P, 'N' => N, 'B' => B, 'R' => R, 'Q' => Q, 'K' => K, _ => 0
                };
                if (x < Side && piece != 0) _board[x, r] = sign * piece;
                x++;
            }
        }
    }

    private static string SquareName(int x, int y) => $"{(char)('a' + x)}{8 - y}";
    private static string MoveName(int sx, int sy, int dx, int dy) => SquareName(sx, sy) + SquareName(dx, dy);

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
            new[] { "Reset", "Next", "Prev", "Help" }, local, leftPressed))
        {
            case 0: LoadPuzzle(_puzzleIdx); return;
            case 1: LoadPuzzle(_puzzleIdx + 1); return;
            case 2: LoadPuzzle(_puzzleIdx - 1); return;
            case 3: _help.Visible = !_help.Visible; return;
        }

        if (_help.HandleInput(local, leftPressed, PanelSize)) return;
        if (_solved || _failed) return;

        if (leftPressed)
        {
            float ox = FrameInset + Margin;
            float oy = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
            int gx = (int)((local.X - ox) / Cell);
            int gy = (int)((local.Y - oy) / Cell);
            if (gx < 0 || gx >= Side || gy < 0 || gy >= Side) return;

            if (_sel == null)
            {
                if (Math.Sign(_board[gx, gy]) == _toMove)
                {
                    _sel = (gx, gy);
                    _legalDest = LegalMovesFrom(gx, gy);
                }
                return;
            }
            if (Math.Sign(_board[gx, gy]) == _toMove)
            { _sel = (gx, gy); _legalDest = LegalMovesFrom(gx, gy); return; }
            if (!_legalDest.Contains((gx, gy))) { _sel = null; _legalDest.Clear(); return; }

            var (sx, sy) = _sel.Value;
            string move = MoveName(sx, sy, gx, gy);
            if (move == Puzzles[_puzzleIdx].Expected)
            {
                _solved = true;
                _solvedCount++;
                _msg = "Solved! Click Next.";
                // Apply the move so the user sees the result
                _board[gx, gy] = _board[sx, sy];
                _board[sx, sy] = 0;
            }
            else
            {
                _failed = true;
                _msg = "Not the best move — click Reset or Next.";
            }
            _sel = null; _legalDest.Clear();
        }
    }

    private List<(int x, int y)> LegalMovesFrom(int sx, int sy)
    {
        // Pseudo-legal moves only (puzzles are short — full check filtering is
        // overkill here; the puzzle's expected move is always legal).
        return PseudoMoves(sx, sy);
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

    private static bool InBoard(int x, int y) => x >= 0 && x < Side && y >= 0 && y < Side;

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Chess Puzzles", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "Reset", "Next", "Prev", "Help" }, -1);

        float bx = panelOffset.X + FrameInset + Margin;
        float by = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;

        var light = new Color(232, 216, 184, 255);
        var dark = new Color(120, 88, 56, 255);
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
                Raylib.DrawRectangle((int)(bx + x * Cell), (int)(by + y * Cell), Cell, Cell,
                    (x + y) % 2 == 0 ? light : dark);

        if (_sel != null)
            Raylib.DrawRectangle((int)(bx + _sel.Value.x * Cell), (int)(by + _sel.Value.y * Cell),
                Cell, Cell, new Color(120, 200, 80, 120));
        foreach (var (x, y) in _legalDest)
            Raylib.DrawCircle((int)(bx + x * Cell + Cell / 2), (int)(by + y * Cell + Cell / 2),
                8, new Color(60, 200, 60, 180));

        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                int p = _board[x, y]; if (p == 0) continue;
                if (!_pieceTextures.TryGetValue(p, out var tex)) continue;
                int sz = Cell - 8;
                float dx = bx + x * Cell + (Cell - sz) / 2f;
                float dy = by + y * Cell + (Cell - sz) / 2f;
                Raylib.DrawTexturePro(tex,
                    new Rectangle(0, 0, tex.Width, tex.Height),
                    new Rectangle(dx, dy, sz, sz),
                    Vector2.Zero, 0f, Color.White);
            }

        // Side panel
        float sx = bx + Side * Cell + Margin;
        var sidePanel = new Rectangle(sx, by, InfoWidth, Side * Cell);
        RetroSkin.DrawSunken(sidePanel, RetroSkin.Face);

        RetroSkin.DrawText("Puzzle", (int)sx + 8, (int)by + 8, RetroSkin.BodyText, 16);
        RetroSkin.DrawText($"#{_puzzleIdx + 1} / {Puzzles.Length}",
            (int)sx + 8, (int)by + 28, RetroSkin.BodyText, 16);

        RetroSkin.DrawText("Solved", (int)sx + 8, (int)by + 60, RetroSkin.BodyText, 16);
        RetroSkin.DrawText(_solvedCount.ToString(), (int)sx + 8, (int)by + 80, RetroSkin.BodyText, 16);

        RetroSkin.DrawText("To move", (int)sx + 8, (int)by + 112, RetroSkin.BodyText, 16);
        RetroSkin.DrawText(_toMove == 1 ? "White" : "Black",
            (int)sx + 8, (int)by + 132, RetroSkin.BodyText, 16);

        // Wrap title onto status bar; keep it short
        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(status, _msg.Length > 0 ? _msg : Puzzles[_puzzleIdx].Title,
            _solved ? "✓ correct" : _failed ? "✗ try again" : "thinking?");

        _help.Draw(panelOffset, PanelSize);
    }

    public void Close()
    {
        foreach (var tex in _pieceTextures.Values)
            Raylib.UnloadTexture(tex);
        _pieceTextures.Clear();
    }
}
