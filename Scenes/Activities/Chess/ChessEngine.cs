namespace MouseHouse.Scenes.Activities.Chess;

/// <summary>
/// Shared chess board state and move logic for both ChessPuzzleActivity (the
/// non-retro Lichess puzzle game) and RetroChessPuzzlesActivity (the retro skin
/// version). Holds the 8x8 board, side to move, castling rights, en-passant
/// square, and optional move history. Generates legal moves with full rule
/// enforcement: castling through-check, en passant, promotion, leaves-king-in-
/// check filtering, and checkmate detection. Also handles FEN parsing, SAN
/// (PGN) move parsing, and UCI-square conversions.
///
/// 0 = empty. Sign indicates side: positive = white, negative = black.
/// |piece| 1..6 = pawn, knight, bishop, rook, queen, king.
/// </summary>
public class ChessEngine
{
    public const int BoardSide = 8;

    public int[,] Board { get; private set; } = new int[BoardSide, BoardSide];
    public bool WhiteToMove { get; set; } = true;
    public bool CastleWK, CastleWQ, CastleBK, CastleBQ;
    public (int r, int c) EnPassantSq { get; set; } = (-1, -1);
    public (int r, int c) LastMoveFrom { get; set; } = (-1, -1);
    public (int r, int c) LastMoveTo { get; set; } = (-1, -1);

    public List<(string text, bool white)> History { get; } = new();
    public bool RecordHistory { get; set; }

    public void Reset()
    {
        Board = new int[BoardSide, BoardSide];
        WhiteToMove = true;
        CastleWK = CastleWQ = CastleBK = CastleBQ = false;
        EnPassantSq = (-1, -1);
        LastMoveFrom = (-1, -1);
        LastMoveTo = (-1, -1);
        History.Clear();
        RecordHistory = false;
    }

    public void LoadFen(string fen)
    {
        Board = new int[BoardSide, BoardSide];
        var parts = fen.Split(' ');
        var rows = parts[0].Split('/');
        for (int r = 0; r < BoardSide; r++)
        {
            int col = 0;
            foreach (char ch in rows[r])
            {
                if (ch >= '1' && ch <= '8') col += ch - '0';
                else { Board[r, col] = PieceFromFenChar(ch); col++; }
            }
        }
        WhiteToMove = parts.Length <= 1 || parts[1] == "w";
        CastleWK = CastleWQ = CastleBK = CastleBQ = false;
        if (parts.Length > 2)
        {
            CastleWK = parts[2].Contains('K');
            CastleWQ = parts[2].Contains('Q');
            CastleBK = parts[2].Contains('k');
            CastleBQ = parts[2].Contains('q');
        }
        EnPassantSq = (-1, -1);
        if (parts.Length > 3 && parts[3] != "-") EnPassantSq = UciToSquare(parts[3]);
    }

    public static int PieceFromFenChar(char c) => c switch
    {
        'P' => 1, 'N' => 2, 'B' => 3, 'R' => 4, 'Q' => 5, 'K' => 6,
        'p' => -1, 'n' => -2, 'b' => -3, 'r' => -4, 'q' => -5, 'k' => -6,
        _ => 0,
    };

    public static char FenCharFromPiece(int p) => p switch
    {
        1 => 'P', 2 => 'N', 3 => 'B', 4 => 'R', 5 => 'Q', 6 => 'K',
        -1 => 'p', -2 => 'n', -3 => 'b', -4 => 'r', -5 => 'q', -6 => 'k',
        _ => '\0',
    };

    public static bool IsWhite(int piece) => piece > 0;
    public static bool InBounds(int r, int c) => r >= 0 && r < BoardSide && c >= 0 && c < BoardSide;
    private static bool IsEnemy(int piece, bool isWhiteTurn) => piece != 0 && IsWhite(piece) != isWhiteTurn;
    private static bool IsFriendly(int piece, bool isWhiteTurn) => piece != 0 && IsWhite(piece) == isWhiteTurn;

    public static (int r, int c) UciToSquare(string s) => (8 - (s[1] - '0'), s[0] - 'a');
    public static string SquareToUci((int r, int c) sq) => $"{(char)('a' + sq.c)}{8 - sq.r}";

    // ── Move generation ───────────────────────────────────────────────

    public List<(int r, int c)> GetLegalMoves((int r, int c) from)
    {
        int piece = Board[from.r, from.c];
        if (piece == 0 || IsWhite(piece) != WhiteToMove) return new();

        var pseudo = GetPseudoLegalMoves(from, piece, IsWhite(piece));
        var legal = new List<(int r, int c)>();
        foreach (var m in pseudo)
        {
            // Don't allow capturing a king (lichess puzzles never need this and
            // it's nonsense outside a real game).
            if (Math.Abs(Board[m.r, m.c]) == 6) continue;
            if (!LeavesKingInCheck(from, m, IsWhite(piece))) legal.Add(m);
        }
        return legal;
    }

    public List<(int r, int c)> GetPseudoLegalMoves((int r, int c) from, int piece, bool isW)
    {
        return Math.Abs(piece) switch
        {
            1 => PawnMoves(from, isW),
            2 => KnightMoves(from, isW),
            3 => BishopMoves(from, isW),
            4 => RookMoves(from, isW),
            5 => Combine(BishopMoves(from, isW), RookMoves(from, isW)),
            6 => KingMoves(from, isW),
            _ => new(),
        };
    }

    private static List<T> Combine<T>(List<T> a, List<T> b) { a.AddRange(b); return a; }

    private List<(int r, int c)> PawnMoves((int r, int c) from, bool isW)
    {
        var moves = new List<(int r, int c)>();
        int dir = isW ? -1 : 1;
        int startRow = isW ? 6 : 1;
        int nr = from.r + dir;
        if (InBounds(nr, from.c) && Board[nr, from.c] == 0)
        {
            moves.Add((nr, from.c));
            int nr2 = from.r + dir * 2;
            if (from.r == startRow && Board[nr2, from.c] == 0) moves.Add((nr2, from.c));
        }
        foreach (int dc in new[] { -1, 1 })
        {
            int nc = from.c + dc;
            if (!InBounds(nr, nc)) continue;
            if (IsEnemy(Board[nr, nc], isW)) moves.Add((nr, nc));
            else if ((nr, nc) == EnPassantSq) moves.Add((nr, nc));
        }
        return moves;
    }

    private List<(int r, int c)> KnightMoves((int r, int c) from, bool isW)
    {
        var moves = new List<(int r, int c)>();
        int[][] offsets = { new[] { -2, -1 }, new[] { -2, 1 }, new[] { -1, -2 }, new[] { -1, 2 },
                            new[] { 1, -2 }, new[] { 1, 2 }, new[] { 2, -1 }, new[] { 2, 1 } };
        foreach (var o in offsets)
        {
            int tr = from.r + o[0], tc = from.c + o[1];
            if (InBounds(tr, tc) && !IsFriendly(Board[tr, tc], isW)) moves.Add((tr, tc));
        }
        return moves;
    }

    private List<(int r, int c)> SlidingMoves((int r, int c) from, bool isW, int[][] dirs)
    {
        var moves = new List<(int r, int c)>();
        foreach (var d in dirs)
        {
            int r = from.r + d[0], c = from.c + d[1];
            while (InBounds(r, c))
            {
                if (Board[r, c] == 0) moves.Add((r, c));
                else if (IsEnemy(Board[r, c], isW)) { moves.Add((r, c)); break; }
                else break;
                r += d[0]; c += d[1];
            }
        }
        return moves;
    }

    private static readonly int[][] BishopDirs =
        { new[] { -1, -1 }, new[] { -1, 1 }, new[] { 1, -1 }, new[] { 1, 1 } };
    private static readonly int[][] RookDirs =
        { new[] { -1, 0 }, new[] { 1, 0 }, new[] { 0, -1 }, new[] { 0, 1 } };
    private static readonly int[][] QueenDirs =
        { new[] { -1, -1 }, new[] { -1, 1 }, new[] { 1, -1 }, new[] { 1, 1 },
          new[] { -1, 0 }, new[] { 1, 0 }, new[] { 0, -1 }, new[] { 0, 1 } };

    private List<(int r, int c)> BishopMoves((int r, int c) from, bool isW) => SlidingMoves(from, isW, BishopDirs);
    private List<(int r, int c)> RookMoves((int r, int c) from, bool isW) => SlidingMoves(from, isW, RookDirs);

    private List<(int r, int c)> KingMoves((int r, int c) from, bool isW)
    {
        var moves = new List<(int r, int c)>();
        for (int dr = -1; dr <= 1; dr++)
        for (int dc = -1; dc <= 1; dc++)
        {
            if (dr == 0 && dc == 0) continue;
            int tr = from.r + dr, tc = from.c + dc;
            if (InBounds(tr, tc) && !IsFriendly(Board[tr, tc], isW)) moves.Add((tr, tc));
        }
        // Castling — only legal from starting king square, with rights, with
        // the path clear, and with no square in the king's path attacked.
        int kingRow = isW ? 7 : 0;
        if (from == (kingRow, 4))
        {
            bool ck = isW ? CastleWK : CastleBK;
            bool cq = isW ? CastleWQ : CastleBQ;
            if (ck && Board[kingRow, 5] == 0 && Board[kingRow, 6] == 0 &&
                !IsSquareAttacked((kingRow, 4), !isW) &&
                !IsSquareAttacked((kingRow, 5), !isW) &&
                !IsSquareAttacked((kingRow, 6), !isW))
                moves.Add((kingRow, 6));
            if (cq && Board[kingRow, 3] == 0 && Board[kingRow, 2] == 0 && Board[kingRow, 1] == 0 &&
                !IsSquareAttacked((kingRow, 4), !isW) &&
                !IsSquareAttacked((kingRow, 3), !isW) &&
                !IsSquareAttacked((kingRow, 2), !isW))
                moves.Add((kingRow, 2));
        }
        return moves;
    }

    public bool IsSquareAttacked((int r, int c) sq, bool byWhite)
    {
        for (int r = 0; r < 8; r++)
        for (int c = 0; c < 8; c++)
        {
            int p = Board[r, c];
            if (p == 0 || IsWhite(p) != byWhite) continue;
            int absP = Math.Abs(p);
            if (absP == 1)
            {
                int dir = byWhite ? -1 : 1;
                if ((r + dir, c - 1) == sq || (r + dir, c + 1) == sq) return true;
            }
            else if (absP == 2)
            {
                int[][] offsets = { new[] { -2, -1 }, new[] { -2, 1 }, new[] { -1, -2 }, new[] { -1, 2 },
                                    new[] { 1, -2 }, new[] { 1, 2 }, new[] { 2, -1 }, new[] { 2, 1 } };
                foreach (var o in offsets) if ((r + o[0], c + o[1]) == sq) return true;
            }
            else if (absP == 3) { if (AttacksSliding((r, c), sq, BishopDirs)) return true; }
            else if (absP == 4) { if (AttacksSliding((r, c), sq, RookDirs)) return true; }
            else if (absP == 5) { if (AttacksSliding((r, c), sq, QueenDirs)) return true; }
            else if (absP == 6) { if (Math.Abs(r - sq.r) <= 1 && Math.Abs(c - sq.c) <= 1 && (r, c) != sq) return true; }
        }
        return false;
    }

    private bool AttacksSliding((int r, int c) from, (int r, int c) target, int[][] dirs)
    {
        foreach (var d in dirs)
        {
            int r = from.r + d[0], c = from.c + d[1];
            while (InBounds(r, c))
            {
                if ((r, c) == target) return true;
                if (Board[r, c] != 0) break;
                r += d[0]; c += d[1];
            }
        }
        return false;
    }

    public (int r, int c) FindKing(bool isW)
    {
        int val = isW ? 6 : -6;
        for (int r = 0; r < 8; r++)
        for (int c = 0; c < 8; c++)
            if (Board[r, c] == val) return (r, c);
        return (-1, -1);
    }

    public bool IsCheckmate(bool isWhiteSide)
    {
        var kingPos = FindKing(isWhiteSide);
        if (kingPos == (-1, -1)) return false;
        if (!IsSquareAttacked(kingPos, !isWhiteSide)) return false;
        bool savedWtm = WhiteToMove;
        WhiteToMove = isWhiteSide;
        bool hasLegal = false;
        for (int r = 0; r < 8 && !hasLegal; r++)
        for (int c = 0; c < 8 && !hasLegal; c++)
        {
            int p = Board[r, c];
            if (p == 0 || IsWhite(p) != isWhiteSide) continue;
            if (GetLegalMoves((r, c)).Count > 0) hasLegal = true;
        }
        WhiteToMove = savedWtm;
        return !hasLegal;
    }

    public bool LeavesKingInCheck((int r, int c) from, (int r, int c) to, bool isW)
    {
        int captured = Board[to.r, to.c];
        int piece = Board[from.r, from.c];
        Board[to.r, to.c] = piece;
        Board[from.r, from.c] = 0;
        int epCaptured = 0;
        (int r, int c) epPos = (-1, -1);
        if (Math.Abs(piece) == 1 && to == EnPassantSq)
        {
            epPos = (from.r, to.c);
            epCaptured = Board[epPos.r, epPos.c];
            Board[epPos.r, epPos.c] = 0;
        }
        var kingPos = FindKing(isW);
        bool inCheck = IsSquareAttacked(kingPos, !isW);
        Board[from.r, from.c] = piece;
        Board[to.r, to.c] = captured;
        if (epPos != (-1, -1)) Board[epPos.r, epPos.c] = epCaptured;
        return inCheck;
    }

    public static string PieceSymbol(int absP) => absP switch
    {
        2 => "N", 3 => "B", 4 => "R", 5 => "Q", 6 => "K", _ => "",
    };

    public void MakeMove((int r, int c) from, (int r, int c) to, string promo = "")
    {
        int piece = Board[from.r, from.c];
        int absPiece = Math.Abs(piece);

        if (RecordHistory)
        {
            bool captured = Board[to.r, to.c] != 0 || (absPiece == 1 && to == EnPassantSq);
            string moveText;
            if (absPiece == 6 && Math.Abs(to.c - from.c) == 2)
                moveText = to.c == 6 ? "O-O" : "O-O-O";
            else
            {
                moveText = PieceSymbol(absPiece);
                if (absPiece == 1 && captured) moveText = SquareToUci(from)[..1];
                if (captured) moveText += "x";
                moveText += SquareToUci(to);
                if (promo != "") moveText += "=" + promo.ToUpper();
            }
            History.Add((moveText, IsWhite(piece)));
        }

        if (absPiece == 1 && to == EnPassantSq) Board[from.r, to.c] = 0;
        if (absPiece == 1 && Math.Abs(to.r - from.r) == 2) EnPassantSq = ((from.r + to.r) / 2, from.c);
        else EnPassantSq = (-1, -1);

        if (absPiece == 6 && Math.Abs(to.c - from.c) == 2)
        {
            int rookRow = from.r;
            if (to.c == 6) { Board[rookRow, 5] = Board[rookRow, 7]; Board[rookRow, 7] = 0; }
            else if (to.c == 2) { Board[rookRow, 3] = Board[rookRow, 0]; Board[rookRow, 0] = 0; }
        }

        if (absPiece == 6)
        {
            if (IsWhite(piece)) { CastleWK = false; CastleWQ = false; }
            else { CastleBK = false; CastleBQ = false; }
        }
        if (absPiece == 4)
        {
            if (from == (7, 7)) CastleWK = false;
            else if (from == (7, 0)) CastleWQ = false;
            else if (from == (0, 7)) CastleBK = false;
            else if (from == (0, 0)) CastleBQ = false;
        }

        Board[to.r, to.c] = piece;
        Board[from.r, from.c] = 0;

        if (absPiece == 1 && (to.r == 0 || to.r == 7))
        {
            int promoPiece = promo switch { "r" => 4, "b" => 3, "n" => 2, _ => 5 };
            if (!IsWhite(piece)) promoPiece = -promoPiece;
            Board[to.r, to.c] = promoPiece;
        }

        WhiteToMove = !WhiteToMove;
        LastMoveFrom = from;
        LastMoveTo = to;
    }

    // ── PGN / SAN parsing ─────────────────────────────────────────────

    public static List<string> ParsePgnMoves(string pgn)
    {
        var result = new List<string>();
        foreach (var rawTok in pgn.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = rawTok.Trim();
            if (t.Length == 0) continue;
            if (t.EndsWith('.')) continue;
            if (t == "1-0" || t == "0-1" || t == "1/2-1/2" || t == "*") continue;
            result.Add(t);
        }
        return result;
    }

    public bool ApplySanMove(string san)
    {
        var clean = san.Replace("+", "").Replace("#", "").Replace("!", "").Replace("?", "");

        if (clean == "O-O" || clean == "0-0")
        {
            int row = WhiteToMove ? 7 : 0;
            MakeMove((row, 4), (row, 6));
            return true;
        }
        if (clean == "O-O-O" || clean == "0-0-0")
        {
            int row = WhiteToMove ? 7 : 0;
            MakeMove((row, 4), (row, 2));
            return true;
        }

        string promo = "";
        int eq = clean.IndexOf('=');
        if (eq >= 0) { promo = clean[(eq + 1)..].ToLower(); clean = clean[..eq]; }

        int pieceType = 1;
        if (clean.Length > 0 && "KQRBN".IndexOf(clean[0]) >= 0)
        {
            pieceType = clean[0] switch { 'K' => 6, 'Q' => 5, 'R' => 4, 'B' => 3, 'N' => 2, _ => 1 };
            clean = clean[1..];
        }
        clean = clean.Replace("x", "");
        if (clean.Length < 2) return false;

        var targetStr = clean[^2..];
        var target = UciToSquare(targetStr);
        var disambig = clean[..^2];
        int disambigCol = -1, disambigRow = -1;
        foreach (var ch in disambig)
        {
            if (ch >= 'a' && ch <= 'h') disambigCol = ch - 'a';
            else if (ch >= '1' && ch <= '8') disambigRow = 8 - (ch - '0');
        }

        int expected = WhiteToMove ? pieceType : -pieceType;
        for (int r = 0; r < 8; r++)
        for (int c = 0; c < 8; c++)
        {
            if (Board[r, c] != expected) continue;
            if (disambigCol >= 0 && c != disambigCol) continue;
            if (disambigRow >= 0 && r != disambigRow) continue;

            var from = (r, c);
            var pseudo = GetPseudoLegalMoves(from, expected, WhiteToMove);
            if (!pseudo.Contains(target)) continue;
            if (LeavesKingInCheck(from, target, WhiteToMove)) continue;

            MakeMove(from, target, promo);
            return true;
        }
        return false;
    }
}
