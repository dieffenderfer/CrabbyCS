using System.Net.Http;
using System.Numerics;
using System.Text.Json;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities;

public class ChessPuzzleActivity : IActivity
{
    public Vector2 PanelSize => new(460, 340);
    public bool IsFinished { get; private set; }

    private readonly AssetCache _assets;

    private const int BoardSize = 8;
    private const int SquareSize = 32;
    private const int MenuHeight = 20;
    private static readonly Vector2 BoardOffset = new(24, 36);

    // Board colors
    private static readonly Color LightSq = new(194, 179, 143, 255);
    private static readonly Color DarkSq = new(115, 82, 46, 255);
    private static readonly Color SelectedCol = new(120, 150, 200, 255);
    private static readonly Color ValidMoveCol = new(100, 160, 100, 255);
    private static readonly Color LastMoveCol = new(205, 190, 100, 255);
    private static readonly Color LastMoveBorder = new(220, 200, 80, 255);
    private static readonly Color HoverCol = new(130, 195, 130, 255);
    private static readonly Color BgColor = new(31, 26, 20, 255);
    private static readonly Color PanelBg = new(45, 40, 33, 255);
    private static readonly Color TextCol = new(230, 217, 179, 255);
    private static readonly Color DimTextCol = new(153, 140, 115, 255);
    private static readonly Color CorrectCol = new(51, 230, 77, 255);
    private static readonly Color WrongCol = new(230, 77, 51, 255);
    private static readonly Color CoordCol = new(179, 166, 128, 255);

    // Board state: 0=empty, positive=white, negative=black
    // 1=pawn, 2=knight, 3=bishop, 4=rook, 5=queen, 6=king
    private int[,] _board = new int[8, 8];
    private bool _whiteToMove = true;
    private bool _castleWK, _castleWQ, _castleBK, _castleBQ;
    private (int r, int c) _enPassantSq = (-1, -1);

    // Puzzle state
    private string[] _puzzleSolution = [];
    private int _puzzleMovesMade;
    private bool _playerIsWhite = true;
    private int _puzzleRating;
    private string _puzzleId = "";
    private bool _waitingForOpponent;
    private bool _puzzleSolved;
    private bool _puzzleFailed;
    private bool _showingAnswer;
    private bool _flipped;
    private bool _ratingHidden = true;
    private float _failTimer;

    // Online puzzle loading
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private Task<LichessPuzzleRaw?>? _fetchTask;
    private bool _loading;
    private bool _loadFailed;
    private string _loadError = "";

    private record LichessPuzzleRaw(string Pgn, string[] Solution, int Rating, string Id);

    // Selection & dragging
    private (int r, int c) _selectedSq = (-1, -1);
    private List<(int r, int c)> _validMoves = new();
    private (int r, int c) _lastMoveFrom = (-1, -1);
    private (int r, int c) _lastMoveTo = (-1, -1);
    private bool _dragging;
    private (int r, int c) _dragFrom = (-1, -1);
    private Vector2 _dragPos;
    private (int r, int c) _dragHoverSq = (-1, -1);

    // Right-click annotations (lichess-style)
    private static readonly Color AnnotationCol = new(21, 120, 27, 200);
    private readonly List<(int r, int c)> _circles = new();
    private readonly List<((int r, int c) from, (int r, int c) to)> _arrows = new();
    private bool _rightDragging;
    private (int r, int c) _rightDragFrom = (-1, -1);

    // Move history
    private List<(string text, bool white)> _moveHistory = new();
    private bool _recordingMoves;

    // Animation
    private bool _animating;
    private float _animTimer;
    private float _animDuration;
    private Vector2 _animFrom, _animTo;
    private int _animPiece;
    private (int r, int c) _animMoveFrom, _animMoveTo;
    private string _animPromo = "";
    private bool _animIsOpponentResponse;

    // Answer animation queue
    private bool _answerAnimating;
    private float _answerDelay;

    // 16x16 piece bitmaps. Each ushort is one row, bit (15-col) = pixel at column col.
    // Designed to be recognizable: cross-king, spike-crown queen, crenellated rook,
    // mitre+cleft bishop, horse-head knight, round-headed pawn.
    private static readonly Dictionary<char, ushort[]> PieceBitmaps = new()
    {
        ['K'] = new ushort[] { 0x0000, 0x0180, 0x0180, 0x07E0, 0x07E0, 0x03C0, 0x07E0, 0x0FF0, 0x0FF0, 0x1FF8, 0x3FFC, 0x7FFE, 0x3FFC, 0x1FF8, 0x7FFE, 0x0000 },
        ['Q'] = new ushort[] { 0x0000, 0x4992, 0x7FFE, 0x7FFE, 0x3FFC, 0x1FF8, 0x0FF0, 0x0FF0, 0x0FF0, 0x1FF8, 0x3FFC, 0x3FFC, 0x7FFE, 0x7FFE, 0xFFFF, 0x0000 },
        ['R'] = new ushort[] { 0x0000, 0x6666, 0x6666, 0x7FFE, 0x7FFE, 0x3FFC, 0x0FF0, 0x0FF0, 0x0FF0, 0x0FF0, 0x0FF0, 0x1FF8, 0x3FFC, 0x7FFE, 0xFFFF, 0x0000 },
        ['B'] = new ushort[] { 0x0000, 0x0180, 0x03C0, 0x03C0, 0x0660, 0x07E0, 0x0FF0, 0x0FF0, 0x07E0, 0x0FF0, 0x1FF8, 0x3FFC, 0x7FFE, 0x3FFC, 0xFFFF, 0x0000 },
        ['N'] = new ushort[] { 0x0000, 0x0FC0, 0x1FE0, 0x3FEC, 0x3FFC, 0x3FFC, 0x7BFF, 0x3FFC, 0x7FFE, 0x1FE0, 0x1FE0, 0x3FF0, 0x3FFC, 0x7FFE, 0xFFFF, 0x0000 },
        ['P'] = new ushort[] { 0x0000, 0x03C0, 0x07E0, 0x07E0, 0x07E0, 0x03C0, 0x03C0, 0x07E0, 0x0FF0, 0x0FF0, 0x1FF8, 0x3FFC, 0x7FFE, 0x7FFE, 0xFFFF, 0x0000 },
    };

    private readonly Dictionary<char, Texture2D> _pieceTextures = new();

    public ChessPuzzleActivity(AssetCache assets)
    {
        _assets = assets;
    }

    public void Load()
    {
        LoadPieceTextures();
        InitBoard();
        LoadRandomPuzzle();
    }

    public void Close()
    {
        foreach (var tex in _pieceTextures.Values)
            Raylib.UnloadTexture(tex);
        _pieceTextures.Clear();
    }

    private void LoadPieceTextures()
    {
        const int gridSize = 16;
        const int pxSize = 2;            // each bitmap pixel becomes 2x2 screen pixels → 32x32 texture
        const int texSize = gridSize * pxSize;

        foreach (var (uppercase, bitmap) in PieceBitmaps)
        {
            GeneratePieceTexture(uppercase, bitmap, isWhite: true, texSize, pxSize, gridSize);
            GeneratePieceTexture(char.ToLower(uppercase), bitmap, isWhite: false, texSize, pxSize, gridSize);
        }
    }

    private void GeneratePieceTexture(char ch, ushort[] bitmap, bool isWhite,
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

        // Outline pass: paint outline pixel at every empty in-bounds 4-neighbor of a filled cell
        for (int r = 0; r < gridSize; r++)
        for (int c = 0; c < gridSize; c++)
        {
            if (!IsFilled(r, c)) continue;
            if (!IsFilled(r - 1, c)) DrawBlock(r - 1, c, outlineColor);
            if (!IsFilled(r + 1, c)) DrawBlock(r + 1, c, outlineColor);
            if (!IsFilled(r, c - 1)) DrawBlock(r, c - 1, outlineColor);
            if (!IsFilled(r, c + 1)) DrawBlock(r, c + 1, outlineColor);
        }

        // Fill pass: paint filled cells (overwrites any outline pixel at the same spot)
        for (int r = 0; r < gridSize; r++)
        for (int c = 0; c < gridSize; c++)
        {
            if (IsFilled(r, c))
                DrawBlock(r, c, fillColor);
        }

        var tex = Raylib.LoadTextureFromImage(img);
        Raylib.SetTextureFilter(tex, TextureFilter.Point);
        Raylib.UnloadImage(img);
        _pieceTextures[ch] = tex;
    }

    // ─── Board / FEN ───

    private void InitBoard()
    {
        _board = new int[8, 8];
    }

    private static int PieceFromFenChar(char c) => c switch
    {
        'P' => 1, 'N' => 2, 'B' => 3, 'R' => 4, 'Q' => 5, 'K' => 6,
        'p' => -1, 'n' => -2, 'b' => -3, 'r' => -4, 'q' => -5, 'k' => -6,
        _ => 0
    };

    private static char FenCharFromPiece(int p) => p switch
    {
        1 => 'P', 2 => 'N', 3 => 'B', 4 => 'R', 5 => 'Q', 6 => 'K',
        -1 => 'p', -2 => 'n', -3 => 'b', -4 => 'r', -5 => 'q', -6 => 'k',
        _ => '\0'
    };

    private void LoadFen(string fen)
    {
        InitBoard();
        var parts = fen.Split(' ');
        var rows = parts[0].Split('/');
        for (int r = 0; r < 8; r++)
        {
            int col = 0;
            foreach (char ch in rows[r])
            {
                if (ch >= '1' && ch <= '8')
                    col += ch - '0';
                else
                {
                    _board[r, col] = PieceFromFenChar(ch);
                    col++;
                }
            }
        }
        _whiteToMove = parts.Length <= 1 || parts[1] == "w";
        _castleWK = _castleWQ = _castleBK = _castleBQ = false;
        if (parts.Length > 2)
        {
            _castleWK = parts[2].Contains('K');
            _castleWQ = parts[2].Contains('Q');
            _castleBK = parts[2].Contains('k');
            _castleBQ = parts[2].Contains('q');
        }
        _enPassantSq = (-1, -1);
        if (parts.Length > 3 && parts[3] != "-")
            _enPassantSq = UciToSquare(parts[3]);
    }

    private static (int r, int c) UciToSquare(string s) =>
        (8 - (s[1] - '0'), s[0] - 'a');

    private static string SquareToUci((int r, int c) sq) =>
        $"{(char)('a' + sq.c)}{8 - sq.r}";

    // ─── Move generation ───

    private static bool IsWhite(int piece) => piece > 0;
    private static bool IsEnemy(int piece, bool isWhiteTurn) => piece != 0 && IsWhite(piece) != isWhiteTurn;
    private static bool IsFriendly(int piece, bool isWhiteTurn) => piece != 0 && IsWhite(piece) == isWhiteTurn;
    private static bool InBounds(int r, int c) => r >= 0 && r < 8 && c >= 0 && c < 8;

    private List<(int r, int c)> GetLegalMoves((int r, int c) from)
    {
        int piece = _board[from.r, from.c];
        if (piece == 0 || IsWhite(piece) != _whiteToMove) return [];

        var pseudo = GetPseudoLegalMoves(from, piece, IsWhite(piece));
        var legal = new List<(int r, int c)>();
        foreach (var m in pseudo)
        {
            if (Math.Abs(_board[m.r, m.c]) == 6) continue;
            if (!LeavesKingInCheck(from, m, IsWhite(piece)))
                legal.Add(m);
        }
        return legal;
    }

    private List<(int r, int c)> GetPseudoLegalMoves((int r, int c) from, int piece, bool isW)
    {
        return Math.Abs(piece) switch
        {
            1 => PawnMoves(from, isW),
            2 => KnightMoves(from, isW),
            3 => BishopMoves(from, isW),
            4 => RookMoves(from, isW),
            5 => [.. BishopMoves(from, isW), .. RookMoves(from, isW)],
            6 => KingMoves(from, isW),
            _ => []
        };
    }

    private List<(int r, int c)> PawnMoves((int r, int c) from, bool isW)
    {
        var moves = new List<(int r, int c)>();
        int dir = isW ? -1 : 1;
        int startRow = isW ? 6 : 1;
        int nr = from.r + dir;
        if (InBounds(nr, from.c) && _board[nr, from.c] == 0)
        {
            moves.Add((nr, from.c));
            int nr2 = from.r + dir * 2;
            if (from.r == startRow && _board[nr2, from.c] == 0)
                moves.Add((nr2, from.c));
        }
        foreach (int dc in new[] { -1, 1 })
        {
            int nc = from.c + dc;
            if (!InBounds(nr, nc)) continue;
            if (IsEnemy(_board[nr, nc], isW))
                moves.Add((nr, nc));
            else if ((nr, nc) == _enPassantSq)
                moves.Add((nr, nc));
        }
        return moves;
    }

    private List<(int r, int c)> KnightMoves((int r, int c) from, bool isW)
    {
        var moves = new List<(int r, int c)>();
        int[][] offsets = [[-2,-1],[-2,1],[-1,-2],[-1,2],[1,-2],[1,2],[2,-1],[2,1]];
        foreach (var o in offsets)
        {
            int tr = from.r + o[0], tc = from.c + o[1];
            if (InBounds(tr, tc) && !IsFriendly(_board[tr, tc], isW))
                moves.Add((tr, tc));
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
                if (_board[r, c] == 0) { moves.Add((r, c)); }
                else if (IsEnemy(_board[r, c], isW)) { moves.Add((r, c)); break; }
                else break;
                r += d[0]; c += d[1];
            }
        }
        return moves;
    }

    private List<(int r, int c)> BishopMoves((int r, int c) from, bool isW) =>
        SlidingMoves(from, isW, [[-1,-1],[-1,1],[1,-1],[1,1]]);

    private List<(int r, int c)> RookMoves((int r, int c) from, bool isW) =>
        SlidingMoves(from, isW, [[-1,0],[1,0],[0,-1],[0,1]]);

    private List<(int r, int c)> KingMoves((int r, int c) from, bool isW)
    {
        var moves = new List<(int r, int c)>();
        for (int dr = -1; dr <= 1; dr++)
        for (int dc = -1; dc <= 1; dc++)
        {
            if (dr == 0 && dc == 0) continue;
            int tr = from.r + dr, tc = from.c + dc;
            if (InBounds(tr, tc) && !IsFriendly(_board[tr, tc], isW))
                moves.Add((tr, tc));
        }
        // Castling
        int kingRow = isW ? 7 : 0;
        if (from == (kingRow, 4))
        {
            bool ck = isW ? _castleWK : _castleBK;
            bool cq = isW ? _castleWQ : _castleBQ;
            if (ck && _board[kingRow, 5] == 0 && _board[kingRow, 6] == 0 &&
                !IsSquareAttacked((kingRow, 4), !isW) && !IsSquareAttacked((kingRow, 5), !isW) && !IsSquareAttacked((kingRow, 6), !isW))
                moves.Add((kingRow, 6));
            if (cq && _board[kingRow, 3] == 0 && _board[kingRow, 2] == 0 && _board[kingRow, 1] == 0 &&
                !IsSquareAttacked((kingRow, 4), !isW) && !IsSquareAttacked((kingRow, 3), !isW) && !IsSquareAttacked((kingRow, 2), !isW))
                moves.Add((kingRow, 2));
        }
        return moves;
    }

    private bool IsSquareAttacked((int r, int c) sq, bool byWhite)
    {
        for (int r = 0; r < 8; r++)
        for (int c = 0; c < 8; c++)
        {
            int p = _board[r, c];
            if (p == 0 || IsWhite(p) != byWhite) continue;
            int absP = Math.Abs(p);
            if (absP == 1)
            {
                int dir = byWhite ? -1 : 1;
                if ((r + dir, c - 1) == sq || (r + dir, c + 1) == sq) return true;
            }
            else if (absP == 2)
            {
                int[][] offsets = [[-2,-1],[-2,1],[-1,-2],[-1,2],[1,-2],[1,2],[2,-1],[2,1]];
                foreach (var o in offsets)
                    if ((r + o[0], c + o[1]) == sq) return true;
            }
            else if (absP == 3)
            {
                if (AttacksSliding((r, c), sq, [[-1,-1],[-1,1],[1,-1],[1,1]])) return true;
            }
            else if (absP == 4)
            {
                if (AttacksSliding((r, c), sq, [[-1,0],[1,0],[0,-1],[0,1]])) return true;
            }
            else if (absP == 5)
            {
                if (AttacksSliding((r, c), sq, [[-1,-1],[-1,1],[1,-1],[1,1],[-1,0],[1,0],[0,-1],[0,1]])) return true;
            }
            else if (absP == 6)
            {
                if (Math.Abs(r - sq.r) <= 1 && Math.Abs(c - sq.c) <= 1 && (r, c) != sq) return true;
            }
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
                if (_board[r, c] != 0) break;
                r += d[0]; c += d[1];
            }
        }
        return false;
    }

    private (int r, int c) FindKing(bool isW)
    {
        int val = isW ? 6 : -6;
        for (int r = 0; r < 8; r++)
        for (int c = 0; c < 8; c++)
            if (_board[r, c] == val) return (r, c);
        return (-1, -1);
    }

    private bool IsCheckmate(bool isWhiteSide)
    {
        var kingPos = FindKing(isWhiteSide);
        if (kingPos == (-1, -1)) return false;
        if (!IsSquareAttacked(kingPos, !isWhiteSide)) return false;
        bool savedWtm = _whiteToMove;
        _whiteToMove = isWhiteSide;
        bool hasLegal = false;
        for (int r = 0; r < 8 && !hasLegal; r++)
        for (int c = 0; c < 8 && !hasLegal; c++)
        {
            int p = _board[r, c];
            if (p == 0 || IsWhite(p) != isWhiteSide) continue;
            if (GetLegalMoves((r, c)).Count > 0) hasLegal = true;
        }
        _whiteToMove = savedWtm;
        return !hasLegal;
    }

    private bool LeavesKingInCheck((int r, int c) from, (int r, int c) to, bool isW)
    {
        int captured = _board[to.r, to.c];
        int piece = _board[from.r, from.c];
        _board[to.r, to.c] = piece;
        _board[from.r, from.c] = 0;
        int epCaptured = 0;
        (int r, int c) epPos = (-1, -1);
        if (Math.Abs(piece) == 1 && to == _enPassantSq)
        {
            epPos = (from.r, to.c);
            epCaptured = _board[epPos.r, epPos.c];
            _board[epPos.r, epPos.c] = 0;
        }
        var kingPos = FindKing(isW);
        bool inCheck = IsSquareAttacked(kingPos, !isW);
        _board[from.r, from.c] = piece;
        _board[to.r, to.c] = captured;
        if (epPos != (-1, -1))
            _board[epPos.r, epPos.c] = epCaptured;
        return inCheck;
    }

    private static string PieceSymbol(int absP) => absP switch
    {
        2 => "N", 3 => "B", 4 => "R", 5 => "Q", 6 => "K", _ => ""
    };

    private void MakeMove((int r, int c) from, (int r, int c) to, string promo = "")
    {
        int piece = _board[from.r, from.c];
        int absPiece = Math.Abs(piece);

        if (_recordingMoves)
        {
            bool captured = _board[to.r, to.c] != 0 || (absPiece == 1 && to == _enPassantSq);
            string moveText;
            if (absPiece == 6 && Math.Abs(to.c - from.c) == 2)
                moveText = to.c == 6 ? "O-O" : "O-O-O";
            else
            {
                moveText = PieceSymbol(absPiece);
                if (absPiece == 1 && captured)
                    moveText = SquareToUci(from)[..1];
                if (captured) moveText += "x";
                moveText += SquareToUci(to);
                if (promo != "") moveText += "=" + promo.ToUpper();
            }
            _moveHistory.Add((moveText, IsWhite(piece)));
        }

        // En passant capture
        if (absPiece == 1 && to == _enPassantSq)
            _board[from.r, to.c] = 0;

        // Update en passant
        if (absPiece == 1 && Math.Abs(to.r - from.r) == 2)
            _enPassantSq = ((from.r + to.r) / 2, from.c);
        else
            _enPassantSq = (-1, -1);

        // Castling rook move
        if (absPiece == 6 && Math.Abs(to.c - from.c) == 2)
        {
            int rookRow = from.r;
            if (to.c == 6) { _board[rookRow, 5] = _board[rookRow, 7]; _board[rookRow, 7] = 0; }
            else if (to.c == 2) { _board[rookRow, 3] = _board[rookRow, 0]; _board[rookRow, 0] = 0; }
        }

        // Update castling rights
        if (absPiece == 6)
        {
            if (IsWhite(piece)) { _castleWK = false; _castleWQ = false; }
            else { _castleBK = false; _castleBQ = false; }
        }
        if (absPiece == 4)
        {
            if (from == (7, 7)) _castleWK = false;
            else if (from == (7, 0)) _castleWQ = false;
            else if (from == (0, 7)) _castleBK = false;
            else if (from == (0, 0)) _castleBQ = false;
        }

        _board[to.r, to.c] = piece;
        _board[from.r, from.c] = 0;

        // Promotion
        if (absPiece == 1 && (to.r == 0 || to.r == 7))
        {
            int promoPiece = promo switch
            {
                "r" => 4, "b" => 3, "n" => 2, _ => 5
            };
            if (!IsWhite(piece)) promoPiece = -promoPiece;
            _board[to.r, to.c] = promoPiece;
        }

        _whiteToMove = !_whiteToMove;
        _lastMoveFrom = from;
        _lastMoveTo = to;
    }

    // ─── Puzzle loading ───

    private void LoadRandomPuzzle()
    {
        _puzzleSolved = false;
        _puzzleFailed = false;
        _showingAnswer = false;
        _selectedSq = (-1, -1);
        _validMoves.Clear();
        _lastMoveFrom = (-1, -1);
        _lastMoveTo = (-1, -1);
        _waitingForOpponent = false;
        _puzzleMovesMade = 0;
        _moveHistory.Clear();
        _recordingMoves = false;
        _animating = false;
        _answerAnimating = false;
        _circles.Clear();
        _arrows.Clear();
        _rightDragging = false;
        _rightDragFrom = (-1, -1);
        CancelDrag();

        _loading = true;
        _loadFailed = false;
        _loadError = "";
        InitBoard();
        _fetchTask = FetchLichessPuzzle();
    }

    private async Task<LichessPuzzleRaw?> FetchLichessPuzzle()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://lichess.org/api/puzzle/next");
            request.Headers.Add("Accept", "application/json");
            var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var puzzle = root.GetProperty("puzzle");
            var game = root.GetProperty("game");

            var pgn = game.GetProperty("pgn").GetString() ?? "";
            var rating = puzzle.GetProperty("rating").GetInt32();
            var id = puzzle.GetProperty("id").GetString() ?? "";
            var solutionArr = puzzle.GetProperty("solution");
            var solution = new string[solutionArr.GetArrayLength()];
            for (int i = 0; i < solution.Length; i++)
                solution[i] = solutionArr[i].GetString()!;

            if (solution.Length == 0 || pgn == "")
            {
                _loadError = "Empty puzzle from server";
                return null;
            }

            return new LichessPuzzleRaw(pgn, solution, rating, id);
        }
        catch (HttpRequestException)
        {
            _loadError = "No connection";
            return null;
        }
        catch (TaskCanceledException)
        {
            _loadError = "Request timed out";
            return null;
        }
        catch (Exception ex)
        {
            _loadError = "Error: " + ex.Message;
            return null;
        }
    }

    // Replay the PGN moves from the starting position to reach the puzzle position.
    // The Lichess PGN includes the opponent's setup move; after replaying all moves
    // it's the player's turn to find solution[0].
    private bool ApplyLichessPuzzle(LichessPuzzleRaw raw)
    {
        LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        _castleWK = _castleWQ = _castleBK = _castleBQ = true;
        _lastMoveFrom = (-1, -1);
        _lastMoveTo = (-1, -1);

        var moves = ParsePgnMoves(raw.Pgn);
        foreach (var san in moves)
        {
            if (!ApplySanMove(san))
            {
                _loadError = $"Bad move in PGN: {san}";
                return false;
            }
        }

        _puzzleSolution = raw.Solution;
        _puzzleRating = raw.Rating;
        _puzzleId = raw.Id;
        _puzzleMovesMade = 0;
        _playerIsWhite = _whiteToMove;
        _flipped = !_playerIsWhite;
        _recordingMoves = true;
        return true;
    }

    private static List<string> ParsePgnMoves(string pgn)
    {
        var result = new List<string>();
        foreach (var rawTok in pgn.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = rawTok.Trim();
            if (t.Length == 0) continue;
            if (t.EndsWith('.')) continue;          // move numbers like "1." "2."
            if (t == "1-0" || t == "0-1" || t == "1/2-1/2" || t == "*") continue;
            result.Add(t);
        }
        return result;
    }

    private bool ApplySanMove(string san)
    {
        // Strip annotation chars
        var clean = san.Replace("+", "").Replace("#", "").Replace("!", "").Replace("?", "");

        // Castling
        if (clean == "O-O" || clean == "0-0")
        {
            int row = _whiteToMove ? 7 : 0;
            MakeMove((row, 4), (row, 6));
            return true;
        }
        if (clean == "O-O-O" || clean == "0-0-0")
        {
            int row = _whiteToMove ? 7 : 0;
            MakeMove((row, 4), (row, 2));
            return true;
        }

        // Promotion: e8=Q or exd1=Q
        string promo = "";
        int eq = clean.IndexOf('=');
        if (eq >= 0)
        {
            promo = clean[(eq + 1)..].ToLower();
            clean = clean[..eq];
        }

        // Piece type (default pawn)
        int pieceType = 1;
        if (clean.Length > 0 && "KQRBN".IndexOf(clean[0]) >= 0)
        {
            pieceType = clean[0] switch
            {
                'K' => 6, 'Q' => 5, 'R' => 4, 'B' => 3, 'N' => 2, _ => 1
            };
            clean = clean[1..];
        }

        // Strip capture marker
        clean = clean.Replace("x", "");

        if (clean.Length < 2) return false;

        var targetStr = clean[^2..];
        var target = UciToSquare(targetStr);

        // Disambiguation chars (file letter and/or rank digit) before the target
        var disambig = clean[..^2];
        int disambigCol = -1, disambigRow = -1;
        foreach (var ch in disambig)
        {
            if (ch >= 'a' && ch <= 'h') disambigCol = ch - 'a';
            else if (ch >= '1' && ch <= '8') disambigRow = 8 - (ch - '0');
        }

        int expected = _whiteToMove ? pieceType : -pieceType;
        for (int r = 0; r < 8; r++)
        for (int c = 0; c < 8; c++)
        {
            if (_board[r, c] != expected) continue;
            if (disambigCol >= 0 && c != disambigCol) continue;
            if (disambigRow >= 0 && r != disambigRow) continue;

            var from = (r, c);
            var pseudo = GetPseudoLegalMoves(from, expected, _whiteToMove);
            if (!pseudo.Contains(target)) continue;
            if (LeavesKingInCheck(from, target, _whiteToMove)) continue;

            MakeMove(from, target, promo);
            return true;
        }
        return false;
    }

    // ─── Coordinate conversion ───

    private Vector2 BoardToScreen(int row, int col)
    {
        if (_flipped)
            return BoardOffset + new Vector2((7 - col) * SquareSize, (7 - row) * SquareSize);
        return BoardOffset + new Vector2(col * SquareSize, row * SquareSize);
    }

    private (int r, int c) ScreenToBoard(Vector2 pos)
    {
        var local = pos - BoardOffset;
        if (local.X < 0 || local.Y < 0) return (-1, -1);
        int col = (int)(local.X / SquareSize);
        int row = (int)(local.Y / SquareSize);
        if (col >= 8 || row >= 8) return (-1, -1);
        return _flipped ? (7 - row, 7 - col) : (row, col);
    }

    // ─── Input handling ───

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset, bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var localMouse = mousePos - panelOffset - new Vector2(0, MenuHeight);

        // Poll online puzzle fetch
        if (_loading && _fetchTask != null && _fetchTask.IsCompleted)
        {
            _loading = false;
            var result = _fetchTask.Result;
            _fetchTask = null;
            if (result != null && ApplyLichessPuzzle(result))
            {
                _loadFailed = false;
            }
            else
            {
                _loadFailed = true;
                if (_loadError == "") _loadError = "Failed to load puzzle";
            }
        }

        // Loading/failed state: only handle retry button
        if (_loading || _loadFailed)
        {
            if (leftPressed && _loadFailed)
            {
                float centerX = BoardOffset.X + 4 * SquareSize;
                float centerY = BoardOffset.Y + 4 * SquareSize;
                float btnW = 100, btnH = 22;
                float btnX = centerX - btnW / 2;
                float btnY = centerY + 14;
                if (localMouse.X >= btnX && localMouse.X <= btnX + btnW &&
                    localMouse.Y >= btnY && localMouse.Y <= btnY + btnH)
                {
                    LoadRandomPuzzle();
                }
            }
            return;
        }

        // Failed state auto-clear
        if (_puzzleFailed)
        {
            _failTimer += delta;
            if (_failTimer >= 1.5f)
            {
                _puzzleFailed = false;
                _failTimer = 0;
            }
        }

        // Animation update
        if (_animating)
        {
            _animTimer += delta;
            if (_animTimer >= _animDuration)
            {
                _animating = false;
                _board[_animMoveFrom.r, _animMoveFrom.c] = _animPiece;
                MakeMove(_animMoveFrom, _animMoveTo, _animPromo);

                if (_animIsOpponentResponse)
                {
                    _puzzleMovesMade++;
                    _waitingForOpponent = false;
                    if (_puzzleMovesMade >= _puzzleSolution.Length)
                        _puzzleSolved = true;
                }
                else if (_answerAnimating)
                {
                    _puzzleMovesMade++;
                }
            }
            return;
        }

        // Answer animation
        if (_answerAnimating)
        {
            _answerDelay += delta;
            if (_answerDelay >= 0.4f)
            {
                _answerDelay = 0;
                AnimateNextAnswerMove();
            }
            return;
        }

        // Right-click annotations: tap a square to toggle a circle, drag between
        // two squares to toggle an arrow (lichess-style).
        bool rightReleased = Raylib.IsMouseButtonReleased(MouseButton.Right);
        if (rightPressed)
        {
            var sq = ScreenToBoard(localMouse);
            if (sq != (-1, -1))
            {
                _rightDragging = true;
                _rightDragFrom = sq;
            }
        }
        if (rightReleased && _rightDragging)
        {
            var sq = ScreenToBoard(localMouse);
            if (sq != (-1, -1) && _rightDragFrom != (-1, -1))
            {
                if (sq == _rightDragFrom)
                    ToggleCircle(sq);
                else
                    ToggleArrow(_rightDragFrom, sq);
            }
            _rightDragging = false;
            _rightDragFrom = (-1, -1);
        }

        // Any left-click on the board clears all annotations.
        if (leftPressed)
        {
            var clickSq = ScreenToBoard(localMouse);
            if (clickSq != (-1, -1) && (_circles.Count > 0 || _arrows.Count > 0))
            {
                _circles.Clear();
                _arrows.Clear();
            }
        }

        // Button clicks
        if (leftPressed)
        {
            float panelX = BoardOffset.X + 8 * SquareSize + 12;
            float btnWidth = 140;
            var btnMouse = localMouse;

            // Check button area
            if (btnMouse.X >= panelX && btnMouse.X <= panelX + btnWidth)
            {
                float btnY = GetButtonStartY();
                float btnH = 22;
                float btnGap = 28;

                if (_puzzleSolved || (_showingAnswer && _puzzleMovesMade >= _puzzleSolution.Length))
                {
                    if (btnMouse.Y >= btnY && btnMouse.Y < btnY + btnH)
                    { LoadRandomPuzzle(); return; }
                }
                else if (!_showingAnswer)
                {
                    // Hint
                    if (btnMouse.Y >= btnY && btnMouse.Y < btnY + btnH)
                    { ShowHint(); return; }
                    btnY += btnGap;
                    // Show Move
                    if (btnMouse.Y >= btnY && btnMouse.Y < btnY + btnH)
                    { ShowMove(); return; }
                    btnY += btnGap;
                    // Show Answer
                    if (btnMouse.Y >= btnY && btnMouse.Y < btnY + btnH)
                    { ShowAnswer(); return; }
                    btnY += btnGap;
                    // Skip
                    if (btnMouse.Y >= btnY && btnMouse.Y < btnY + btnH)
                    { LoadRandomPuzzle(); return; }
                }
            }

            // Rating toggle
            float bottomY = BoardOffset.Y + 8 * SquareSize - 14;
            if (btnMouse.X >= panelX && btnMouse.X <= panelX + 140 &&
                btnMouse.Y >= bottomY && btnMouse.Y < bottomY + 18)
            {
                _ratingHidden = !_ratingHidden;
                return;
            }
        }

        if (_puzzleSolved || _puzzleFailed || _waitingForOpponent || _showingAnswer)
        {
            if (leftPressed) CancelDrag();
            return;
        }

        // Board interaction
        float boardRight = BoardOffset.X + 8 * SquareSize;
        if (localMouse.X > boardRight)
        {
            // In info panel, skip board interaction
        }
        else if (leftPressed)
        {
            var sq = ScreenToBoard(localMouse);
            if (sq != (-1, -1))
            {
                int piece = _board[sq.r, sq.c];
                bool isPlayerPiece = piece != 0 && IsWhite(piece) == _playerIsWhite;
                if (isPlayerPiece)
                {
                    _selectedSq = sq;
                    _validMoves = GetLegalMoves(sq);
                    _dragging = true;
                    _dragFrom = sq;
                    _dragPos = localMouse;
                    _dragHoverSq = (-1, -1);
                }
            }
        }

        if (_dragging)
        {
            _dragPos = localMouse;
            _dragHoverSq = ScreenToBoard(localMouse);
        }

        if (leftReleased && _dragging)
        {
            var dropSq = ScreenToBoard(localMouse);
            _dragging = false;
            _dragHoverSq = (-1, -1);

            if (dropSq != (-1, -1) && dropSq != _dragFrom && _validMoves.Contains(dropSq))
                TryPlayerMove(_dragFrom, dropSq);
            else
            {
                _selectedSq = (-1, -1);
                _validMoves.Clear();
            }
            _dragFrom = (-1, -1);
        }
    }

    private float GetButtonStartY()
    {
        float y = 30 + 22; // turn indicator

        // Status text height (matches DrawInfoPanel: empty only for show-answer-complete)
        bool statusEmpty = !_puzzleSolved && _showingAnswer && _puzzleMovesMade >= _puzzleSolution.Length;
        if (!statusEmpty)
            y += 18;

        // Checkmate line
        if (_puzzleSolved || (_showingAnswer && _puzzleMovesMade >= _puzzleSolution.Length))
        {
            if (IsCheckmate(_whiteToMove))
                y += 18;
        }

        // Move history height
        if (_moveHistory.Count > 0)
        {
            int lines = 0;
            int i = 0;
            if (!_moveHistory[0].white) { lines++; i = 1; }
            while (i < _moveHistory.Count) { lines++; i += 2; }
            y += lines * 14;
        }
        y += 10;
        return y;
    }

    private void TryPlayerMove((int r, int c) from, (int r, int c) to)
    {
        string expectedUci = _puzzleMovesMade < _puzzleSolution.Length ? _puzzleSolution[_puzzleMovesMade] : "";
        string playerUci = SquareToUci(from) + SquareToUci(to);

        int piece = _board[from.r, from.c];
        string promo = "";
        if (Math.Abs(piece) == 1 && (to.r == 0 || to.r == 7))
        {
            promo = "q";
            playerUci += "q";
        }

        if (expectedUci != "" && playerUci == expectedUci)
        {
            MakeMove(from, to, promo);
            _puzzleMovesMade++;
            _selectedSq = (-1, -1);
            _validMoves.Clear();

            if (_puzzleMovesMade >= _puzzleSolution.Length)
            {
                _puzzleSolved = true;
                return;
            }

            _waitingForOpponent = true;
            StartOpponentAnimation();
        }
        else
        {
            _puzzleFailed = true;
            _failTimer = 0;
            _selectedSq = (-1, -1);
            _validMoves.Clear();
        }
    }

    private void StartOpponentAnimation()
    {
        if (_puzzleMovesMade >= _puzzleSolution.Length) return;
        string oppUci = _puzzleSolution[_puzzleMovesMade];
        var from = UciToSquare(oppUci[..2]);
        var to = UciToSquare(oppUci[2..4]);
        string promo = oppUci.Length > 4 ? oppUci[4..] : "";

        int pieceVal = _board[from.r, from.c];
        _animating = true;
        _animTimer = 0;
        _animDuration = 0.35f;
        _animFrom = BoardToScreen(from.r, from.c);
        _animTo = BoardToScreen(to.r, to.c);
        _animPiece = pieceVal;
        _animMoveFrom = from;
        _animMoveTo = to;
        _animPromo = promo;
        _animIsOpponentResponse = true;

        // Temporarily hide the piece at source for animation
        _board[from.r, from.c] = 0;
    }

    private void ShowHint()
    {
        if (_puzzleSolved || _showingAnswer || _puzzleMovesMade >= _puzzleSolution.Length) return;
        var move = UciToSquare(_puzzleSolution[_puzzleMovesMade][..2]);
        _selectedSq = move;
        _validMoves.Clear();
    }

    private void ShowMove()
    {
        if (_puzzleSolved || _showingAnswer || _puzzleMovesMade >= _puzzleSolution.Length) return;
        string uci = _puzzleSolution[_puzzleMovesMade];
        _selectedSq = UciToSquare(uci[..2]);
        _validMoves = [UciToSquare(uci[2..4])];
    }

    private void ShowAnswer()
    {
        if (_puzzleSolved || _showingAnswer) return;
        _showingAnswer = true;
        CancelDrag();
        _selectedSq = (-1, -1);
        _validMoves.Clear();
        _answerAnimating = true;
        _answerDelay = 0;
        AnimateNextAnswerMove();
    }

    private void AnimateNextAnswerMove()
    {
        if (_puzzleMovesMade >= _puzzleSolution.Length)
        {
            _answerAnimating = false;
            return;
        }

        string uci = _puzzleSolution[_puzzleMovesMade];
        var from = UciToSquare(uci[..2]);
        var to = UciToSquare(uci[2..4]);
        string promo = uci.Length > 4 ? uci[4..] : "";

        int pieceVal = _board[from.r, from.c];
        _animating = true;
        _animTimer = 0;
        _animDuration = 0.4f;
        _animFrom = BoardToScreen(from.r, from.c);
        _animTo = BoardToScreen(to.r, to.c);
        _animPiece = pieceVal;
        _animMoveFrom = from;
        _animMoveTo = to;
        _animPromo = promo;
        _animIsOpponentResponse = false;

        _board[from.r, from.c] = 0;

        // When animation completes, increment and queue next
        // (handled in Update when _animating finishes and _answerAnimating is true)
    }

    private void ToggleCircle((int r, int c) sq)
    {
        if (!_circles.Remove(sq)) _circles.Add(sq);
    }

    private void ToggleArrow((int r, int c) from, (int r, int c) to)
    {
        int idx = _arrows.FindIndex(a => a.from == from && a.to == to);
        if (idx >= 0) _arrows.RemoveAt(idx);
        else _arrows.Add((from, to));
    }

    private void DrawAnnotations(Vector2 offset)
    {
        // The window is a transparent overlay, so default alpha blending would
        // reduce the panel's destination alpha and leak through to the desktop.
        // Use a separate blend so RGB blends normally but dst alpha is preserved.
        const int GL_SRC_ALPHA = 0x0302;
        const int GL_ONE_MINUS_SRC_ALPHA = 0x0303;
        const int GL_FUNC_ADD = 0x8006;
        Rlgl.SetBlendFactorsSeparate(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA, 0, 1, GL_FUNC_ADD, GL_FUNC_ADD);
        Raylib.BeginBlendMode(BlendMode.CustomSeparate);

        foreach (var sq in _circles)
        {
            var sp = BoardToScreen(sq.r, sq.c) + offset;
            float cx = sp.X + SquareSize / 2f;
            float cy = sp.Y + SquareSize / 2f;
            float outer = SquareSize / 2f - 1;
            float inner = outer - 3;
            Raylib.DrawRing(new Vector2(cx, cy), inner, outer, 0, 360, 32, AnnotationCol);
        }

        foreach (var a in _arrows)
        {
            var fp = BoardToScreen(a.from.r, a.from.c) + offset
                     + new Vector2(SquareSize / 2f, SquareSize / 2f);
            var tp = BoardToScreen(a.to.r, a.to.c) + offset
                     + new Vector2(SquareSize / 2f, SquareSize / 2f);
            DrawArrow(fp, tp, AnnotationCol);
        }

        // Live preview of in-progress arrow drag.
        if (_rightDragging && _rightDragFrom != (-1, -1))
        {
            var mouseLocal = Raylib.GetMousePosition() - offset;
            var hoverSq = ScreenToBoard(mouseLocal);
            var fp = BoardToScreen(_rightDragFrom.r, _rightDragFrom.c) + offset
                     + new Vector2(SquareSize / 2f, SquareSize / 2f);
            if (hoverSq != (-1, -1) && hoverSq != _rightDragFrom)
            {
                var tp = BoardToScreen(hoverSq.r, hoverSq.c) + offset
                         + new Vector2(SquareSize / 2f, SquareSize / 2f);
                DrawArrow(fp, tp, AnnotationCol);
            }
        }

        Raylib.EndBlendMode();
    }

    private static void DrawArrow(Vector2 from, Vector2 to, Color color)
    {
        var dir = to - from;
        float len = dir.Length();
        if (len < 1f) return;
        var u = dir / len;
        var n = new Vector2(-u.Y, u.X);
        const float thickness = 7f;
        const float headLen = 14f;
        const float headW = 11f;
        var shaftEnd = to - u * headLen;

        Raylib.DrawLineEx(from, shaftEnd, thickness, color);

        var h2 = shaftEnd + n * headW;
        var h3 = shaftEnd - n * headW;
        // Draw both windings — Raylib_cs DrawTriangle culls one direction.
        Raylib.DrawTriangle(to, h2, h3, color);
        Raylib.DrawTriangle(to, h3, h2, color);
    }

    private void CancelDrag()
    {
        _dragging = false;
        _dragFrom = (-1, -1);
        _dragHoverSq = (-1, -1);
    }

    // ─── Drawing ───

    public void Draw(Vector2 panelOffset)
    {
        var off = panelOffset;

        // Background
        Raylib.DrawRectangle((int)off.X, (int)off.Y, (int)PanelSize.X, (int)PanelSize.Y, BgColor);

        // Menu bar
        Raylib.DrawRectangle((int)off.X, (int)off.Y, (int)PanelSize.X, MenuHeight, new Color(50, 45, 38, 255));
        FontManager.DrawText("Puzzle", (int)off.X + 8, (int)off.Y + 4, 12, TextCol);

        // Close button [X]
        FontManager.DrawText("[X]", (int)(off.X + PanelSize.X - 28), (int)off.Y + 4, 12, TextCol);

        var boardOff = off + new Vector2(0, MenuHeight);

        // Loading / failed state overlay
        if (_loading || _loadFailed)
        {
            DrawBoard(boardOff);
            DrawCoordinates(boardOff);
            float centerX = BoardOffset.X + 4 * SquareSize + boardOff.X;
            float centerY = BoardOffset.Y + 4 * SquareSize + boardOff.Y;
            if (_loading)
            {
                FontManager.DrawText("Loading puzzle...", (int)(centerX - 45), (int)(centerY - 6), 12, TextCol);
            }
            else
            {
                FontManager.DrawText(_loadError, (int)(centerX - 40), (int)(centerY - 20), 12, WrongCol);
                float btnW = 100, btnH = 22;
                float btnX = centerX - btnW / 2;
                float btnY = centerY + 4;
                DrawButton("Retry", btnX, btnY, btnW, btnH,
                    new Color(64, 56, 46, 255), new Color(128, 115, 89, 255));
            }
            return;
        }

        DrawBoard(boardOff);
        DrawPieces(boardOff);
        DrawCoordinates(boardOff);

        // Draw animation piece
        if (_animating)
        {
            float t = Math.Min(_animTimer / _animDuration, 1f);
            t = 1f - (1f - t) * (1f - t); // ease out quad
            var pos = Vector2.Lerp(_animFrom, _animTo, t) + boardOff;
            char ch = FenCharFromPiece(_animPiece);
            if (ch != '\0' && _pieceTextures.TryGetValue(ch, out var tex))
                Raylib.DrawTextureEx(tex, pos, 0, 1, Color.White);
        }

        // Draw drag piece
        if (_dragging && _dragFrom != (-1, -1))
        {
            int piece = _board[_dragFrom.r, _dragFrom.c];
            if (piece == 0)
            {
                // Piece might be at the original position since we don't hide it during drag
                // We'll draw on top
            }
            char ch = FenCharFromPiece(_board[_dragFrom.r, _dragFrom.c]);
            if (ch == '\0')
            {
                // Try to find piece - we need to look up what was there
                // Actually for drag, the piece is still on the board, we draw it at cursor
                // Let's use a different approach: store the piece value when drag starts
            }
            // Draw the dragged piece at cursor
            int dragPiece = 0;
            for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
                if ((r, c) == _dragFrom) dragPiece = _board[r, c];

            ch = FenCharFromPiece(dragPiece);
            if (ch != '\0' && _pieceTextures.TryGetValue(ch, out var tex))
            {
                var drawPos = _dragPos + boardOff - new Vector2(16, 16);
                Raylib.DrawTextureEx(tex, drawPos, 0, 1, Color.White);
            }
        }

        DrawAnnotations(boardOff);

        DrawInfoPanel(boardOff);
    }

    private void DrawBoard(Vector2 offset)
    {
        for (int r = 0; r < 8; r++)
        for (int c = 0; c < 8; c++)
        {
            var screenPos = BoardToScreen(r, c) + offset;
            bool isLight = (r + c) % 2 == 0;
            Raylib.DrawRectangle((int)screenPos.X, (int)screenPos.Y, SquareSize, SquareSize,
                isLight ? LightSq : DarkSq);

            // Last move highlight
            if ((r, c) == _lastMoveFrom || (r, c) == _lastMoveTo)
            {
                Raylib.DrawRectangle((int)screenPos.X, (int)screenPos.Y, SquareSize, SquareSize, LastMoveCol);
                int bw = 2;
                Raylib.DrawRectangle((int)screenPos.X, (int)screenPos.Y, SquareSize, bw, LastMoveBorder);
                Raylib.DrawRectangle((int)screenPos.X, (int)(screenPos.Y + SquareSize - bw), SquareSize, bw, LastMoveBorder);
                Raylib.DrawRectangle((int)screenPos.X, (int)screenPos.Y, bw, SquareSize, LastMoveBorder);
                Raylib.DrawRectangle((int)(screenPos.X + SquareSize - bw), (int)screenPos.Y, bw, SquareSize, LastMoveBorder);
            }

            // Selected square
            if ((r, c) == _selectedSq)
                Raylib.DrawRectangle((int)screenPos.X, (int)screenPos.Y, SquareSize, SquareSize, SelectedCol);

            // Valid move dots
            if (_validMoves.Contains((r, c)))
            {
                int dotSize = 8;
                int dotOff = (SquareSize - dotSize) / 2;
                Raylib.DrawRectangle((int)(screenPos.X + dotOff), (int)(screenPos.Y + dotOff), dotSize, dotSize, ValidMoveCol);
            }

            // Drag hover highlight
            if (_dragging && (r, c) == _dragHoverSq && _dragHoverSq != _dragFrom && _validMoves.Contains(_dragHoverSq))
                Raylib.DrawRectangle((int)screenPos.X, (int)screenPos.Y, SquareSize, SquareSize, HoverCol);
        }
    }

    private void DrawPieces(Vector2 offset)
    {
        for (int r = 0; r < 8; r++)
        for (int c = 0; c < 8; c++)
        {
            if (_dragging && (r, c) == _dragFrom) continue;
            int piece = _board[r, c];
            if (piece == 0) continue;
            char ch = FenCharFromPiece(piece);
            if (ch != '\0' && _pieceTextures.TryGetValue(ch, out var tex))
            {
                var screenPos = BoardToScreen(r, c) + offset;
                Raylib.DrawTextureEx(tex, screenPos, 0, 1f, Color.White);
            }
        }
    }

    private void DrawCoordinates(Vector2 offset)
    {
        string files = "abcdefgh";
        string ranks = "87654321";
        for (int i = 0; i < 8; i++)
        {
            int fi = _flipped ? 7 - i : i;
            var filePos = BoardOffset + offset + new Vector2(i * SquareSize + SquareSize / 2 - 3, 8 * SquareSize + 2);
            FontManager.DrawText(files[fi].ToString(), (int)filePos.X, (int)filePos.Y, 10, CoordCol);

            int ri = _flipped ? 7 - i : i;
            var rankPos = BoardOffset + offset + new Vector2(-12, i * SquareSize + SquareSize / 2 - 5);
            FontManager.DrawText(ranks[ri].ToString(), (int)rankPos.X, (int)rankPos.Y, 10, CoordCol);
        }
    }

    private void DrawInfoPanel(Vector2 offset)
    {
        float panelX = BoardOffset.X + 8 * SquareSize + 12;
        float y = 30;

        // Turn indicator
        string turnText = _playerIsWhite ? "Play as white" : "Play as black";
        FontManager.DrawText(turnText, (int)(panelX + offset.X + 16), (int)(y + offset.Y), 10, TextCol);
        var colorSquare = _playerIsWhite ? Color.White : new Color(38, 31, 26, 255);
        Raylib.DrawRectangle((int)(panelX + offset.X), (int)(y + 1 + offset.Y), 12, 12, colorSquare);
        Raylib.DrawRectangleLines((int)(panelX + offset.X - 1), (int)(y + offset.Y), 14, 14, DimTextCol);
        y += 22;

        // Status
        string statusText;
        Color statusColor;
        if (_puzzleSolved)
        { statusText = "Correct!"; statusColor = CorrectCol; }
        else if (_showingAnswer && _puzzleMovesMade >= _puzzleSolution.Length)
        { statusText = ""; statusColor = TextCol; }
        else if (_puzzleFailed)
        { statusText = "Wrong move"; statusColor = WrongCol; }
        else if (_waitingForOpponent || _animating)
        { statusText = "..."; statusColor = DimTextCol; }
        else
        { statusText = "Your move"; statusColor = TextCol; }

        if (statusText != "")
        {
            FontManager.DrawText(statusText, (int)(panelX + offset.X), (int)(y + offset.Y), 12, statusColor);
            y += 18;
        }

        // Checkmate indicator
        if (_puzzleSolved || (_showingAnswer && _puzzleMovesMade >= _puzzleSolution.Length))
        {
            if (IsCheckmate(_whiteToMove))
            {
                FontManager.DrawText("Checkmate!", (int)(panelX + offset.X), (int)(y + offset.Y), 12, new Color(255, 217, 51, 255));
                y += 18;
            }
        }

        // Move history
        if (_moveHistory.Count > 0)
        {
            int moveNum = 1;
            int mi = 0;
            if (!_moveHistory[0].white)
            {
                string line = $"{moveNum}...{_moveHistory[0].text}";
                FontManager.DrawText(line, (int)(panelX + offset.X), (int)(y + offset.Y), 10, DimTextCol);
                y += 14; mi = 1; moveNum = 2;
            }
            while (mi < _moveHistory.Count)
            {
                string line = $"{moveNum}.{_moveHistory[mi].text}";
                if (mi + 1 < _moveHistory.Count)
                    line += $"  {_moveHistory[mi + 1].text}";
                FontManager.DrawText(line, (int)(panelX + offset.X), (int)(y + offset.Y), 10, DimTextCol);
                y += 14; mi += 2; moveNum++;
            }
        }
        y += 10;

        // Buttons
        float btnWidth = 140;
        float btnHeight = 22;
        float btnGap = 28;
        var btnBg = new Color(64, 56, 46, 255);
        var btnBorder = new Color(128, 115, 89, 255);

        if (_puzzleSolved || (_showingAnswer && _puzzleMovesMade >= _puzzleSolution.Length))
        {
            DrawButton("Next Puzzle", panelX + offset.X, y + offset.Y, btnWidth, btnHeight, btnBg, btnBorder);
        }
        else if (!_showingAnswer)
        {
            DrawButton("Hint", panelX + offset.X, y + offset.Y, btnWidth, btnHeight, btnBg, btnBorder);
            y += btnGap;
            DrawButton("Show Move", panelX + offset.X, y + offset.Y, btnWidth, btnHeight, btnBg, btnBorder);
            y += btnGap;
            DrawButton("Show Answer", panelX + offset.X, y + offset.Y, btnWidth, btnHeight, btnBg, btnBorder);
            y += btnGap;
            DrawButton("Skip", panelX + offset.X, y + offset.Y, btnWidth, btnHeight, btnBg, btnBorder);
        }

        // Bottom: rating
        float bottomY = BoardOffset.Y + 8 * SquareSize - 14;
        string ratingText = _ratingHidden ? "Rating: ****" : $"Rating: {_puzzleRating}";
        FontManager.DrawText(ratingText, (int)(panelX + offset.X), (int)(bottomY + offset.Y), 10, DimTextCol);

        if (_puzzleId != "")
            FontManager.DrawText($"#{_puzzleId}", (int)(panelX + offset.X), (int)(bottomY + 14 + offset.Y), 10, DimTextCol);
    }

    private static void DrawButton(string text, float x, float y, float w, float h, Color bg, Color border)
    {
        Raylib.DrawRectangle((int)x, (int)y, (int)w, (int)h, bg);
        Raylib.DrawRectangleLines((int)x, (int)y, (int)w, (int)h, border);
        int textW = FontManager.MeasureText(text, 10);
        FontManager.DrawText(text, (int)(x + (w - textW) / 2), (int)(y + 6), 10, TextCol);
    }

    private record PuzzleData(string Fen, string[] Solution, int Rating, string Id);

    // ─── Offline puzzles (commented out — now loading from Lichess API) ───
    /*
    private static readonly PuzzleData[] OfflinePuzzles =
    [
        new("r1bqkb1r/pppp1ppp/2n2n2/4p2Q/2B1P3/8/PPPP1PPP/RNB1K1NR w KQkq - 4 4", ["h5f7"], 600, "00sHx"),
        new("r2qk2r/ppp2ppp/2n1b3/3np1N1/2B5/1P6/P1PP1PPP/RNBQK2R w KQkq - 0 7", ["d1g4"], 750, "00sI0"),
        new("r1b1kb1r/pppp1ppp/5n2/4p1q1/2BnP3/2N2N2/PPPP1PPP/R1BQK2R w KQkq - 0 5", ["c3d5"], 800, "00sI1"),
        new("r1bqkbnr/pppppppp/2n5/8/4P3/5N2/PPPP1PPP/RNBQKB1R b KQkq - 2 2", ["d7d5"], 700, "00sI2"),
        new("rnbqkb1r/pppppppp/5n2/8/4P3/2N5/PPPP1PPP/R1BQKBNR b KQkq - 2 2", ["d7d5"], 650, "00sI3"),
        new("r1bqkbnr/pppp1ppp/2n5/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R w KQkq - 2 3", ["f1b5"], 700, "01a"),
        new("rnbqkb1r/ppp1pppp/3p1n2/8/3PP3/8/PPP2PPP/RNBQKBNR w KQkq - 0 3", ["e4e5"], 750, "01b"),
        new("rnbqkbnr/ppp1pppp/8/3p4/4P3/8/PPPP1PPP/RNBQKBNR w KQkq d6 0 2", ["e4d5"], 500, "01c"),
        new("r1bqkbnr/pppppppp/2n5/8/3PP3/8/PPP2PPP/RNBQKBNR b KQkq - 0 2", ["d7d5"], 600, "01d"),
        new("rnbqkbnr/pppp1ppp/8/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R b KQkq - 1 2", ["b8c6"], 500, "01e"),
        new("r1bqk2r/pppp1ppp/2n2n2/2b1p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4", ["c2c3"], 800, "02a"),
        new("r1bqkbnr/1ppp1ppp/p1n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 0 4", ["c4a2"], 850, "02b"),
        new("rnbqkb1r/pppp1ppp/4pn2/8/2PP4/8/PP2PPPP/RNBQKBNR w KQkq - 0 3", ["b1c3"], 700, "02c"),
        new("rnbqk2r/pppp1ppp/4pn2/8/1bPP4/2N5/PP2PPPP/R1BQKBNR w KQkq - 2 4", ["e2e3"], 750, "02d"),
        new("rnbqkbnr/pppp1ppp/4p3/8/4P3/8/PPPP1PPP/RNBQKBNR w KQkq - 0 2", ["d2d4"], 600, "02e"),
        new("r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R b KQkq - 3 3", ["f8c5"], 700, "03a"),
        new("rnbqkbnr/pp1ppppp/8/2p5/4P3/8/PPPP1PPP/RNBQKBNR w KQkq c6 0 2", ["g1f3"], 550, "03b"),
        new("rnbqkbnr/pp1ppppp/8/2p5/4P3/5N2/PPPP1PPP/RNBQKB1R b KQkq - 1 2", ["d7d6"], 600, "03c"),
        new("r1bqkbnr/pp1ppppp/2n5/2p5/4P3/5N2/PPPP1PPP/RNBQKB1R w KQkq - 2 3", ["d2d4"], 650, "03d"),
        new("r1bqkbnr/pp2pppp/2np4/2p5/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 0 4", ["d2d4"], 800, "03e"),
        new("r2qr1k1/ppp2ppp/2n5/3Np1b1/2B1P3/3P4/PPP2PPP/R2QK2R w KQ - 3 10", ["d5f6"], 1100, "04a"),
        new("r1b1k2r/ppppqppp/2n2n2/4p3/2B1P1b1/2NP1N2/PPP2PPP/R1BQK2R w KQkq - 5 6", ["c3d5"], 1000, "04b"),
        new("r2qkb1r/ppp2ppp/2n1bn2/3pp3/4P3/1BN2N2/PPPP1PPP/R1BQK2R w KQkq - 4 5", ["e4d5"], 900, "04c"),
        new("r1bqk2r/ppp2ppp/2n2n2/3pp3/1bP5/2N1PN2/PP1P1PPP/R1BQKB1R w KQkq - 0 5", ["c4d5"], 850, "04d"),
        new("r1bq1rk1/pppp1ppp/2n2n2/2b1p3/2B1P3/2NP1N2/PPP2PPP/R1BQ1RK1 b - - 5 6", ["c6d4"], 950, "04e"),
        new("r2q1rk1/pppb1ppp/2np1n2/4p1B1/2B1P3/2NP1N2/PPP2PPP/R2QK2R w KQ - 0 7", ["c3d5"], 1050, "05a"),
        new("r1bq1rk1/ppp2ppp/2n2n2/3pp3/1bPP4/2N1PN2/PP3PPP/R1BQKB1R w KQ - 0 6", ["c4d5"], 900, "05b"),
        new("r2qkb1r/pp1bpppp/2np1n2/8/3NP3/2N5/PPP1BPPP/R1BQK2R w KQkq - 3 6", ["d4c6"], 950, "05c"),
        new("rnb1k2r/pppp1ppp/4pn2/8/1bPP2q1/2N1P3/PP3PPP/R1BQKBNR w KQkq - 1 5", ["g1f3"], 800, "05d"),
        new("r1bq1rk1/ppppbppp/2n2n2/4p3/2B1P3/3P1N2/PPP2PPP/RNBQ1RK1 w - - 5 6", ["b1c3"], 750, "05e"),
        new("6k1/5ppp/8/8/8/8/5PPP/4R1K1 w - - 0 1", ["e1e8"], 400, "m1a"),
        new("6k1/5ppp/8/8/8/5q2/5PPP/6K1 b - - 0 1", ["f3f2"], 450, "m1b"),
        new("5rk1/5ppp/8/8/1Q6/8/5PPP/6K1 w - - 0 1", ["b4g4"], 500, "m1c"),
        new("r1bqkbnr/pppp1Qpp/2n5/4p3/2B1P3/8/PPPP1PPP/RNB1K1NR b KQkq - 0 4", ["e8e7"], 350, "m1d"),
        new("rnbqk2r/pppp1ppp/5n2/4p3/1bB1P3/2N2Q2/PPPP1PPP/R1B1K1NR w KQkq - 4 4", ["f3f7"], 550, "m1e"),
        new("r1bqkbnr/pppp1ppp/8/4p3/2BnP3/8/PPPP1PPP/RNBQK1NR w KQkq - 0 4", ["c4f7"], 600, "m1f"),
        new("rnbqkb1r/pppp1ppp/5n2/4p2Q/4P3/8/PPPP1PPP/RNB1KBNR w KQkq - 2 3", ["h5e5"], 500, "m1g"),
        new("r1bqkbnr/ppp2ppp/2n5/3pp3/4P3/3B1N2/PPPP1PPP/RNBQK2R w KQkq - 2 4", ["e4d5"], 700, "m1h"),
        new("rnbqk1nr/pppp1ppp/8/2b1p3/4PP2/8/PPPP2PP/RNBQKBNR b KQkq - 0 3", ["d8h4"], 450, "m1i"),
        new("r1bqk1nr/pppp1ppp/2n5/2b1p3/2B1P3/5Q2/PPPP1PPP/RNB1K1NR w KQkq - 4 4", ["f3f7"], 500, "m1j"),
        new("2r3k1/5ppp/8/1Q6/8/8/5PPP/4R1K1 w - - 0 1", ["b5f5"], 900, "m2a"),
        new("r4rk1/ppp2ppp/8/3qN3/8/2P5/PP3PPP/R2Q1RK1 w - - 0 1", ["d1d5"], 850, "m2b"),
        new("r1b2rk1/pppp1ppp/2n2q2/8/2B1N3/8/PPPP1PPP/R1BQK2R w KQ - 4 7", ["e4f6"], 1000, "m2c"),
        new("r1bq1rk1/pppp1Npp/2n2n2/2b1p3/2B1P3/8/PPPP1PPP/RNBQK2R b KQ - 0 6", ["d8e7"], 800, "m2d"),
        new("r2qk2r/ppp2ppp/2n1b3/8/2BnN3/8/PPPP1PPP/R1BQK2R w KQkq - 0 8", ["e4f6"], 1050, "m2e"),
        new("8/8/8/5k2/8/2K5/4Q3/8 w - - 0 1", ["e2e4"], 600, "eg1"),
        new("8/8/8/8/5k2/8/3RK3/8 w - - 0 1", ["d2d4"], 550, "eg2"),
        new("8/5pk1/8/8/8/8/R4PK1/8 w - - 0 1", ["a2a7"], 700, "eg3"),
        new("8/8/4k3/8/8/3K4/8/5R2 w - - 0 1", ["f1e1"], 650, "eg4"),
        new("8/8/3k4/8/3K4/8/8/R7 w - - 0 1", ["a1a6"], 500, "eg5"),
        new("r1b1k2r/ppppqppp/2n2n2/2b1p3/2B1P3/2NP1N2/PPP2PPP/R1BQK2R w KQkq - 0 6", ["c3d5"], 1100, "mg1"),
        new("r2qkb1r/pp2pppp/2p2n2/3p1b2/3P4/2N2N2/PPP1PPPP/R1BQKB1R w KQkq - 0 5", ["c3e4"], 950, "mg2"),
        new("r1bqk2r/pppp1ppp/2n2n2/4p3/1bP5/2N2NP1/PP1PPP1P/R1BQKB1R b KQkq - 0 5", ["b4c3"], 800, "mg3"),
        new("r1bq1rk1/ppp1bppp/2n2n2/3p4/2PP4/2N2N2/PP2PPPP/R1BQKB1R w KQ - 0 6", ["c4d5"], 850, "mg4"),
        new("r2q1rk1/pp2ppbp/2np1np1/8/2PNP3/2N1B3/PP2BPPP/R2Q1RK1 b - - 0 9", ["d6d5"], 1000, "mg5"),
        new("r1bq1rk1/pp3ppp/2nbpn2/3p4/2PP4/1PN1PN2/PB3PPP/R2QKB1R w KQ - 0 8", ["c4d5"], 900, "mg6"),
        new("r2qk2r/ppp1bppp/2n1bn2/3pp3/4P3/1BN2N2/PPPP1PPP/R1BQK2R w KQkq - 4 5", ["e4d5"], 850, "mg7"),
        new("r1bqkb1r/pp1n1ppp/2p1pn2/3p4/2PP4/2NBPN2/PP3PPP/R1BQK2R b KQkq - 0 6", ["f8d6"], 750, "mg8"),
        new("r1bq1rk1/pp2bppp/2n1pn2/2pp4/2PP4/2N1PN2/PPB2PPP/R1BQ1RK1 w - - 0 8", ["c4d5"], 900, "mg9"),
        new("r2q1rk1/ppp1bppp/2n2n2/3pp1B1/2PP4/2N1P3/PP3PPP/R2QKBNR w KQ - 0 7", ["c4d5"], 850, "mg10"),
        new("r3kb1r/ppp1pppp/5n2/3q4/3P4/4BN2/PPP2PPP/R2QKB1R b KQkq - 0 7", ["d5a2"], 1050, "t1"),
        new("rnb1k2r/ppp1qppp/3bpn2/3p4/2PP4/2N2N2/PP2PPPP/R1BQKB1R w KQkq - 4 5", ["c4d5"], 900, "t2"),
        new("r1bqkb1r/pp2pppp/2np1n2/2p5/2B1P3/2N2N2/PPPP1PPP/R1BQK2R w KQkq - 0 5", ["e4e5"], 950, "t3"),
        new("r1bqk2r/1pppbppp/p1n2n2/4p3/B3P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 2 5", ["d2d4"], 800, "t4"),
        new("rnbq1rk1/ppp1bppp/4pn2/3p4/2PP4/2N2N2/PP2PPPP/R1BQKB1R w KQ - 0 6", ["c4d5"], 750, "t5"),
        new("r1bqk2r/pppp1ppp/2n2n2/2b1p3/2BPP3/5N2/PPP2PPP/RNBQK2R b KQkq d3 0 4", ["e5d4"], 700, "t6"),
        new("r1bqkbnr/ppp2ppp/2np4/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 0 4", ["c4f7"], 1050, "t7"),
        new("r2qk2r/ppp2ppp/2np1n2/2b1p1B1/2B1P1b1/2NP1N2/PPP2PPP/R2QK2R w KQkq - 0 6", ["c3d5"], 1100, "t8"),
        new("r1b1kb1r/ppp2ppp/2n2n2/3qp3/3P4/2N2N2/PPP1PPPP/R1BQKB1R w KQkq - 0 5", ["d4e5"], 800, "t9"),
        new("r1bqkbnr/pppp1ppp/2n5/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R w KQkq - 2 3", ["d2d4"], 650, "t10"),
        new("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1", ["e7e5"], 400, "o1"),
        new("rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR w KQkq e6 0 2", ["g1f3"], 450, "o2"),
        new("r1bqkbnr/pppppppp/2n5/8/4P3/8/PPPP1PPP/RNBQKBNR w KQkq - 1 2", ["d2d4"], 500, "o3"),
        new("rnbqkb1r/pppppppp/5n2/8/4P3/8/PPPP1PPP/RNBQKBNR w KQkq - 1 2", ["e4e5"], 550, "o4"),
        new("rnbqkbnr/pppppppp/8/8/3P4/8/PPP1PPPP/RNBQKBNR b KQkq d3 0 1", ["d7d5"], 400, "o5"),
        new("rnbqkbnr/ppp1pppp/8/3p4/3P4/8/PPP1PPPP/RNBQKBNR w KQkq d6 0 2", ["c2c4"], 500, "o6"),
        new("rnbqkbnr/ppp1pppp/8/3p4/2PP4/8/PP2PPPP/RNBQKBNR b KQkq c3 0 2", ["e7e6"], 450, "o7"),
        new("rnbqkbnr/pppppppp/8/8/2P5/8/PP1PPPPP/RNBQKBNR b KQkq c3 0 1", ["e7e5"], 400, "o8"),
        new("rnbqkbnr/pppppppp/8/8/8/5N2/PPPPPPPP/RNBQKB1R b KQkq - 1 1", ["d7d5"], 400, "o9"),
        new("rnbqkbnr/pppp1ppp/4p3/8/3P4/8/PPP1PPPP/RNBQKBNR w KQkq - 0 2", ["e2e4"], 500, "o10"),
        new("r1bqk2r/pppp1ppp/2n2n2/4N3/2B1P3/8/PPPP1PPP/RNBQK2R b KQkq - 0 4", ["d7d5"], 800, "f1"),
        new("r1b1kbnr/pppp1ppp/2n5/4p3/2BPP2q/5N2/PPP2PPP/RNBQK2R b KQkq - 3 4", ["h4f2"], 900, "f2"),
        new("r1bqkb1r/ppp2ppp/2n2n2/3pp3/2B1P3/2N2N2/PPPP1PPP/R1BQK2R w KQkq d6 0 4", ["e4d5"], 750, "f3"),
        new("rnbqkb1r/ppp2ppp/3p1n2/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 0 4", ["d2d4"], 700, "f4"),
        new("r1bqk2r/ppppbppp/2n2n2/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4", ["d2d4"], 750, "f5"),
        new("rnbqk2r/pppp1ppp/5n2/4p3/1b2P3/2N2N2/PPPP1PPP/R1BQKB1R w KQkq - 4 4", ["d2d3"], 700, "p1"),
        new("r1bqkb1r/ppp2ppp/2np1n2/4p3/2B1P3/2N2N2/PPPP1PPP/R1BQK2R w KQkq - 0 5", ["d2d3"], 750, "p2"),
        new("rnbq1rk1/pppp1ppp/5n2/4p1B1/2B1P3/2N2N2/PPPP1PPP/R2QK2R b KQ - 5 5", ["h7h6"], 800, "p3"),
        new("r1bqk2r/ppppbppp/2n2n2/4p1B1/2B1P3/2N2N2/PPPP1PPP/R2QK2R b KQkq - 5 5", ["d7d6"], 750, "p4"),
        new("rnbqk2r/ppp1bppp/3p1n2/4p1B1/2B1P3/2N2N2/PPPP1PPP/R2QK2R b KQkq - 5 6", ["h7h6"], 800, "p5"),
        new("r1bqk2r/pppp1ppp/2n5/2b1pN2/2B1P3/8/PPPP1PPP/RNBQK2R w KQkq - 4 5", ["f5g7"], 1100, "d1"),
        new("r2qkb1r/ppp2ppp/2n1bn2/3Np3/2B1P3/8/PPPP1PPP/RNBQK2R w KQkq - 0 6", ["d5f6"], 1000, "d2"),
        new("r1bqk2r/pppp1ppp/2n2n2/2b1p3/2BPP3/2N2N2/PPP2PPP/R1BQK2R b KQkq - 0 5", ["e5d4"], 850, "d3"),
        new("r1bq1rk1/pppp1ppp/2n2n2/2b1p1N1/2B1P3/8/PPPP1PPP/RNBQK2R w KQ - 5 6", ["g5f7"], 1150, "d4"),
        new("r2q1rk1/ppp2ppp/2n1bn2/3Np1B1/2B1P3/8/PPPP1PPP/RN1QK2R w KQ - 0 8", ["d5f6"], 1100, "d5"),
        new("r1bqk2r/pppp1ppp/2n2n2/4p3/2B1P3/2N2N2/PPPP1PPP/R1BQK2R b KQkq - 4 4", ["d7d5"], 750, "x1"),
        new("rnbqkb1r/pp2pppp/2p2n2/3p4/2PP4/2N5/PP2PPPP/R1BQKBNR w KQkq - 0 4", ["c4d5"], 700, "x2"),
        new("r1bqk2r/pppp1ppp/2n5/2b1p3/2B1P1n1/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 5", ["c2c3"], 850, "x3"),
        new("rnb1kb1r/pppp1ppp/5n2/4p1q1/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 3 4", ["d2d4"], 900, "x4"),
        new("2k4r/pp3R2/2p5/3p4/3qp3/P7/1PP5/1K3Q2 w - - 1 1", ["f1f5", "c8b8", "f5f4", "b8a8", "f7f8", "h8f8", "f4f8"], 1898, "7DNoH"),
        new("k3r3/1b6/pp4p1/5pPp/5P2/P1P3P1/1PB1r2q/1KQR2R1 b - - 1 1", ["e2c2", "c1c2", "b7e4"], 1573, "oxIMK"),
        new("q5nr/1ppknQpp/3p4/1P2p3/4P3/B1PP1b2/B5PP/5K2 w - - 1 1", ["a2e6", "d7d8", "f7f8"], 1525, "00sHx"),
        new("r3r1k1/p4ppp/2p2n2/1p6/3P1qb1/2NQ2R1/PPB2PP1/R1B3K1 b - - 1 1", ["e8e1", "g1h2", "e1c1", "a1c1", "f4h6", "h2g1", "h6c1"], 2675, "00sJ9"),
        new("Q1b2r1k/p2Rp2p/5bp1/q7/5P2/4B3/PPP3PP/2K2B1R b - - 0 1", ["a5e1", "d7d1", "e1e3", "c1b1", "e3b6"], 2337, "00sJb"),
        new("1r4k1/p4ppp/2Q5/3pq3/8/P6P/2PR1PP1/1R4K1 b - - 0 1", ["b8b1", "d2d1", "b1d1"], 1152, "004X6"),
        new("r6k/pp2r2p/4Rp1Q/3p4/8/1N1P2b1/PqP3PP/7K w - - 0 1", ["e6e7", "b2b1", "b3c1", "b1c1", "h6c1"], 1922, "00008"),
        new("5rk1/1p3ppp/pq1Q1b2/8/8/1P3N2/P4PPP/3R2K1 b - - 1 1", ["f8d8", "d6d8", "f6d8"], 1542, "0000D"),
        new("8/5R2/1p2P3/p4r2/P6p/1P3Pk1/4K3/8 b - - 1 1", ["f5e5", "e2f1", "e5e6"], 1385, "0008Q"),
        new("r2qr1k1/b1p2ppp/p5n1/P1p1p3/4P1n1/B2P2Pb/3NBP1P/RN1QR1K1 w - - 0 1", ["e2g4", "h3g4", "d1g4"], 1084, "0009B"),
        new("6k1/5p1p/4p3/4q3/3n4/2Q3P1/PP1N1P1P/6K1 b - - 1 1", ["d4e2", "g1f1", "e2c3"], 1550, "000Pw"),
        new("2Q2bk1/5p1p/p5p1/2p3P1/4B3/7P/qPr2P2/2K4R w - - 0 1", ["e4c2", "a2a1", "c2b1"], 1600, "000Sa"),
        new("r4r2/1p3pkp/p7/3R1p1Q/3P4/8/P1q2P2/3R2K1 w - - 0 1", ["d5c5", "c2e4", "h5g5", "g7h8", "g5f6"], 2861, "000VW"),
        new("8/8/4k1p1/2KpP2P/5P2/8/8/8 b - - 0 1", ["g6h5", "f4f5", "e6e5", "f5f6", "e5f6"], 1574, "000Vc"),
        new("r1bq3r/pp1nbkp1/2p1p2p/8/2BP4/1PN3P1/P3QP1P/3R1RK1 w - - 0 1", ["e2e6", "f7f8", "e6f7"], 1575, "000hf"),
        new("2r2rk1/3nqp1p/p3p1p1/np1N4/3P4/P2BP3/1PQ2PPP/2R2RK1 b - - 0 1", ["e6d5", "c2c8", "f8c8"], 1687, "HxxIU"),
        new("r1b1kb1r/pppp1ppp/2n1p3/4N3/3P2q1/4n3/PPP1BPPP/RN1Q1RK1 w kq - 0 1", ["f2e3", "g4g5", "e5f7", "g5e3", "g1h1"], 2185, "00LZf"),
        new("4r3/p5k1/2R4p/2Pp4/1P1pr1P1/P6P/8/3R3K b - - 0 1", ["e4e1", "d1e1", "e8e1", "h1g2", "d4d3"], 1138, "0048h"),
        new("4qk2/1b3R2/p7/1p2Q3/4P2P/P2P3K/2r5/3R4 b - - 0 1", ["e8f7", "e5h8", "f8e7"], 1711, "004Ao"),
        new("8/5k2/4R2p/p7/5rPK/8/7P/8 w - - 1 1", ["e6h6", "f4f6", "h6h7", "f7g6", "h7a7"], 2058, "004Ax"),
        new("r1bk2r1/ppq2NQp/3bpn2/1Bpn4/5P2/1P6/PBPP2PP/RN2K2R b KQ - 0 1", ["d8e7", "g7g8", "f6g8"], 1511, "004BW"),
        new("2rq1rk1/7p/1n4pb/1R2Q3/pPpP1P2/P1B5/3N2PP/2R3K1 b - - 0 1", ["f8e8", "e5e8", "d8e8"], 2125, "008nF"),
        new("6k1/2R3pp/2p4q/1p1p4/3P4/P7/1PP2R2/1K1Nr3 w - - 1 1", ["c7c8", "e1e8", "c8e8"], 976, "008oX"),
        new("8/8/3p4/4kp2/1pP3pP/2RK2P1/8/8 b - - 0 1", ["b4c3", "d3c3", "f5f4", "c3d3", "f4g3"], 1691, "00CcK"),
        new("6k1/ppR2pp1/4p1p1/4P1N1/3r2P1/1P4K1/P3r3/8 w - - 1 1", ["c7c8", "d4d8", "c8d8"], 550, "00Cfq"),
        new("5r1k/6b1/3p3p/1P1q2pQ/r5P1/3p1N1P/3R2P1/3R3K b - - 1 1", ["f8f3", "g2f3", "d5f3"], 1362, "00CiZ"),
        new("3r2k1/pp4bp/4qpp1/3Pp3/8/4Q2P/4B1P1/2rR3K w - - 0 1", ["d5e6", "d8d1", "e2d1", "c1d1", "h1h2"], 1620, "00Cqg"),
        new("8/6pp/8/3kP3/1p1P2P1/1rpK3P/4R3/8 w - - 0 1", ["e5e6", "c3c2", "d3c2", "b3c3", "c2d2"], 2218, "00D12"),
        new("rn2kbnr/pp6/2p2p2/4P3/4P1pq/2N3N1/PPPB1KB1/R2Q1R2 b kq - 1 1", ["f8c5", "d2e3", "c5e3", "f2e3", "h4g3"], 1086, "00DBg"),
        new("2rr2k1/p5p1/1p5p/2pq1p1P/8/P4QR1/5PP1/4R1K1 w - - 0 1", ["e1e8", "g8f7", "f3d5", "d8d5", "e8c8"], 1705, "00DII"),
        new("1R6/6kp/3p1pp1/2r1p3/PP6/8/2r2PPP/1R4K1 b - - 0 1", ["c2c1", "b1c1", "c5c1"], 529, "00DYf"),
        new("8/5k2/7p/p1P1bPpP/Pp2P3/1P1p1K2/5B2/8 b - - 1 1", ["g5g4", "f3e3", "e5d4", "e3d3", "d4f2"], 2304, "00DZe"),
        new("3r1bnr/2p2ppp/2bk4/R7/5P2/2N5/4N1PP/1R4K1 w - - 1 1", ["b1d1", "d6e7", "a5e5", "e7f6", "d1d8"], 1575, "00DkJ"),
        new("8/8/6R1/2pk2P1/1r5P/6K1/8/8 b - - 1 1", ["c4c3", "g5g7", "c3c2"], 1352, "00Dxh"),
        new("5rk1/p4p1p/4p1p1/5nq1/8/5QPP/5PK1/1R1R4 b - - 1 1", ["f5h4", "g2f1", "h4f3"], 1204, "00Dt6"),
        new("1r4k1/p4p1p/6p1/3rb3/K7/2PpB3/1P1R1PPP/3R4 b - - 1 1", ["d5d6", "b2b4", "e5c3", "e3c5", "d6a6"], 2290, "00E4Z"),
        new("6k1/5pp1/2R1p2p/8/P1B5/1P4P1/1q3QKP/3r4 b - - 1 1", ["d1d2", "c6c8", "g8h7", "c4d3", "f7f5", "c8c2", "d2f2"], 2288, "00EJb"),
        new("8/5P1P/1p4Kr/8/6P1/8/2p5/k7 w - - 1 1", ["g6h6", "c2c1q", "g4g5", "c1c6", "h6g7"], 2603, "00EUu"),
        new("3r4/p5k1/1p1qpr1p/1Q1pn1p1/3P1pP1/1PP5/P5PP/4RRK1 w - - 0 1", ["d4e5", "d6c5", "b5c5", "b6c5", "e5f6"], 1317, "00Ea3"),
        new("N2k3r/1b1n1Bpp/p3P3/1pb5/6P1/4p3/PPP4P/1K1R3R b - - 0 1", ["b7h1", "d1d7", "d8c8", "d7c7", "c8b8"], 1782, "00EgR"),
        new("3r4/6k1/1p1pr1p1/p1p2p2/P1P1p1P1/1P1n4/3R1PBP/4R1K1 w - - 1 1", ["d2d3", "e4d3", "e1e6", "d3d2", "g2f3"], 1402, "00Erm"),
        new("2k3r1/pppb1prp/1q6/8/Q7/2P1R1P1/P4P1P/4R1K1 w - - 1 1", ["e3e8", "d7e8", "e1e8", "g8e8", "a4e8"], 908, "00F6y"),
        new("5Rbk/6pp/8/p3P3/Pp1pq3/1Q6/1P4PP/6K1 b - - 1 1", ["e4e1", "f8f1", "e1f1", "g1f1", "g8b3"], 1595, "00FAe"),
        new("8/1pp5/p2p3p/3P1Pk1/P5P1/1P3K1R/8/2r5 b - - 1 1", ["c1c3", "f3g2", "c3h3", "g2h3", "h6h5", "g4h5", "g5h5"], 2236, "00FHO"),
        new("5k2/3b2q1/pn4p1/1rp2p2/8/8/1P2Q1P1/1K2R2R w - - 1 1", ["h1h8", "g7h8", "e2e7", "f8g8", "e7d8"], 1581, "00GVf"),
        new("1r1r2k1/pN4pp/2n1b3/2R2p2/2P1p3/8/P4PPP/3BR1K1 b - - 0 1", ["b8b7", "c5c6", "b7b1", "g1f1", "d8d1", "e1d1", "b1d1", "f1e2", "e6d7"], 2085, "00GWg"),
        new("5r2/5p1k/6pp/ppqp1P2/7Q/5N2/6PP/5N1K w - - 0 1", ["f3g5", "h7g7", "f5f6", "g7f6", "g5e4"], 1834, "00GiQ"),
        new("1n5k/6p1/p2q1rPp/1ppB4/8/3P4/PPP1rPQ1/2K4R w - - 0 1", ["h1h6", "g7h6", "g6g7", "h8h7", "g7g8q"], 1165, "00GuD"),
        new("3R4/1pp1r1kp/4r1p1/p1P5/5Q2/P4PPq/1P5P/3R2K1 b - - 1 1", ["e6e1", "d1e1", "e7e1", "g1f2", "h3f1"], 899, "00Gv1"),
        new("r7/p4kp1/1p4p1/2qNn3/Q7/4PP2/PP3K2/6R1 w - - 1 1", ["a4f4", "f7e6", "f4e4", "a8f8", "g1g6"], 2264, "00Gz6"),
        new("4rrk1/ppp2pp1/7p/3n4/3P3q/1P2p2P/PB4P1/R2QRBK1 b - - 1 1", ["h4f2", "g1h2", "f2b2"], 2033, "00H2I"),
        new("3r4/5k2/p4Pp1/2K3Pp/2R5/P7/8/8 b - - 0 1", ["d8c8", "c5d4", "c8c4", "d4c4", "h5h4"], 1430, "00G81"),
        new("r5k1/5pp1/1p1rb1qp/3pR3/p1pP4/P1P3Q1/5PPN/4R1K1 w - - 1 1", ["g3g6", "f7g6", "e5e6", "d6e6", "e1e6"], 1328, "00GAf"),
    ];
    */
}
