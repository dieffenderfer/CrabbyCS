using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities;

public class ChessPuzzleActivity : IActivity
{
    public Vector2 PanelSize => new(800, 600);
    public bool IsFinished { get; private set; }

    private readonly AssetCache _assets;

    private const int BoardSize = 8;
    private const int SquareSize = 56;
    private const int MenuHeight = 28;
    private static readonly Vector2 BoardOffset = new(40, 60);

    // Board colors
    private static readonly Color LightSq = new(194, 179, 143, 255);
    private static readonly Color DarkSq = new(115, 82, 46, 255);
    private static readonly Color SelectedCol = new(77, 128, 204, 128);
    private static readonly Color ValidMoveCol = new(51, 153, 51, 102);
    private static readonly Color LastMoveCol = new(217, 191, 26, 140);
    private static readonly Color LastMoveBorder = new(242, 217, 38, 217);
    private static readonly Color HoverCol = new(77, 204, 77, 115);
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

    // Selection & dragging
    private (int r, int c) _selectedSq = (-1, -1);
    private List<(int r, int c)> _validMoves = new();
    private (int r, int c) _lastMoveFrom = (-1, -1);
    private (int r, int c) _lastMoveTo = (-1, -1);
    private bool _dragging;
    private (int r, int c) _dragFrom = (-1, -1);
    private Vector2 _dragPos;
    private (int r, int c) _dragHoverSq = (-1, -1);

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

    // Piece bitmaps (8x8 bit patterns)
    private static readonly Dictionary<char, byte[]> PieceBitmaps = new()
    {
        ['K'] = [0x10, 0x38, 0x10, 0x7C, 0xFE, 0xFE, 0x7C, 0x00],
        ['Q'] = [0x54, 0x54, 0x38, 0x7C, 0xFE, 0xFE, 0x7C, 0x00],
        ['R'] = [0x00, 0xAA, 0xFE, 0x7C, 0x7C, 0x7C, 0xFE, 0x00],
        ['B'] = [0x10, 0x38, 0x7C, 0x38, 0x38, 0x7C, 0xFE, 0x00],
        ['N'] = [0x30, 0x70, 0xF8, 0x78, 0x30, 0x38, 0x78, 0xFC],
        ['P'] = [0x00, 0x00, 0x18, 0x3C, 0x3C, 0x18, 0x7E, 0x00],
    };

    // Piece textures generated at runtime
    private readonly Dictionary<char, Texture2D> _pieceTextures = new();

    private int _currentPuzzleIndex = -1;
    private readonly Random _rng = new();

    public ChessPuzzleActivity(AssetCache assets)
    {
        _assets = assets;
    }

    public void Load()
    {
        GeneratePieceTextures();
        InitBoard();
        LoadRandomPuzzle();
    }

    public void Close()
    {
        foreach (var tex in _pieceTextures.Values)
            Raylib.UnloadTexture(tex);
        _pieceTextures.Clear();
    }

    // ─── Piece texture generation ───

    private void GeneratePieceTextures()
    {
        int pxSize = 6;
        int texSize = 8 * pxSize;
        foreach (var (ch, bitmap) in PieceBitmaps)
        {
            GeneratePieceTexture(ch, bitmap, pxSize, texSize, true);
            GeneratePieceTexture(char.ToLower(ch), bitmap, pxSize, texSize, false);
        }
    }

    private void GeneratePieceTexture(char ch, byte[] bitmap, int pxSize, int texSize, bool isWhite)
    {
        var img = Raylib.GenImageColor(texSize, texSize, new Color(0, 0, 0, 0));
        var fillColor = isWhite ? new Color(242, 235, 217, 255) : new Color(38, 31, 26, 255);
        var outlineColor = isWhite ? new Color(26, 20, 13, 255) : new Color(140, 128, 107, 255);

        // Outline pass
        for (int row = 0; row < 8; row++)
        {
            byte bits = bitmap[row];
            for (int col = 0; col < 8; col++)
            {
                if ((bits & (1 << (7 - col))) == 0) continue;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dy == 0 && dx == 0) continue;
                    int nr = row + dy, nc = col + dx;
                    bool neighborFilled = nr >= 0 && nr < 8 && nc >= 0 && nc < 8 &&
                                          (bitmap[nr] & (1 << (7 - nc))) != 0;
                    if (neighborFilled) continue;
                    int px = (col + dx) * pxSize, py = (row + dy) * pxSize;
                    for (int sy = 0; sy < pxSize; sy++)
                    for (int sx = 0; sx < pxSize; sx++)
                    {
                        int fx = px + sx, fy = py + sy;
                        if (fx >= 0 && fx < texSize && fy >= 0 && fy < texSize)
                            Raylib.ImageDrawPixel(ref img, fx, fy, outlineColor);
                    }
                }
            }
        }

        // Fill pass
        for (int row = 0; row < 8; row++)
        {
            byte bits = bitmap[row];
            for (int col = 0; col < 8; col++)
            {
                if ((bits & (1 << (7 - col))) == 0) continue;
                int px = col * pxSize, py = row * pxSize;
                for (int sy = 0; sy < pxSize; sy++)
                for (int sx = 0; sx < pxSize; sx++)
                    Raylib.ImageDrawPixel(ref img, px + sx, py + sy, fillColor);
            }
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
        CancelDrag();

        int idx = _rng.Next(OfflinePuzzles.Length);
        while (idx == _currentPuzzleIndex && OfflinePuzzles.Length > 1)
            idx = _rng.Next(OfflinePuzzles.Length);
        _currentPuzzleIndex = idx;

        var p = OfflinePuzzles[idx];
        LoadFen(p.Fen);
        _puzzleSolution = p.Solution;
        _puzzleRating = p.Rating;
        _puzzleId = p.Id;
        _puzzleMovesMade = 0;
        _playerIsWhite = _whiteToMove;
        _flipped = !_playerIsWhite;
        _recordingMoves = true;
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

        // Button clicks
        if (leftPressed)
        {
            float panelX = BoardOffset.X + 8 * SquareSize + 20;
            float btnWidth = 250;
            var btnMouse = localMouse;

            // Check button area
            if (btnMouse.X >= panelX && btnMouse.X <= panelX + btnWidth)
            {
                float btnY = GetButtonStartY();
                float btnH = 32;
                float btnGap = 42;

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
            float bottomY = BoardOffset.Y + 8 * SquareSize - 20;
            if (btnMouse.X >= panelX && btnMouse.X <= panelX + 200 &&
                btnMouse.Y >= bottomY && btnMouse.Y < bottomY + 24)
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
        float y = 50 + 36 + 30; // turn indicator + status
        // Move history height
        if (_moveHistory.Count > 0)
        {
            int lines = 0;
            int i = 0;
            if (!_moveHistory[0].white) { lines++; i = 1; }
            while (i < _moveHistory.Count) { lines++; i += 2; }
            y += lines * 22;
        }
        y += 16;
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
        _animFrom = BoardToScreen(from.r, from.c) + new Vector2(4, 4);
        _animTo = BoardToScreen(to.r, to.c) + new Vector2(4, 4);
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
        _animFrom = BoardToScreen(from.r, from.c) + new Vector2(4, 4);
        _animTo = BoardToScreen(to.r, to.c) + new Vector2(4, 4);
        _animPiece = pieceVal;
        _animMoveFrom = from;
        _animMoveTo = to;
        _animPromo = promo;
        _animIsOpponentResponse = false;

        _board[from.r, from.c] = 0;

        // When animation completes, increment and queue next
        // (handled in Update when _animating finishes and _answerAnimating is true)
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
        Raylib.DrawText("Puzzle", (int)off.X + 12, (int)off.Y + 6, 16, TextCol);

        // Close button [X]
        Raylib.DrawText("[X]", (int)(off.X + PanelSize.X - 36), (int)off.Y + 6, 16, TextCol);

        var boardOff = off + new Vector2(0, MenuHeight);

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
                var drawPos = _dragPos + boardOff - new Vector2(24, 24);
                Raylib.DrawTextureEx(tex, drawPos, 0, 1, Color.White);
            }
        }

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
                int bw = 3;
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
                int dotSize = 14;
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
                var screenPos = BoardToScreen(r, c) + offset + new Vector2(4, 4);
                Raylib.DrawTextureEx(tex, screenPos, 0, 1, Color.White);
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
            var filePos = BoardOffset + offset + new Vector2(i * SquareSize + SquareSize / 2 - 4, 8 * SquareSize + 4);
            Raylib.DrawText(files[fi].ToString(), (int)filePos.X, (int)filePos.Y, 14, CoordCol);

            int ri = _flipped ? 7 - i : i;
            var rankPos = BoardOffset + offset + new Vector2(-16, i * SquareSize + SquareSize / 2 - 7);
            Raylib.DrawText(ranks[ri].ToString(), (int)rankPos.X, (int)rankPos.Y, 14, CoordCol);
        }
    }

    private void DrawInfoPanel(Vector2 offset)
    {
        float panelX = BoardOffset.X + 8 * SquareSize + 20;
        float y = 50;

        // Turn indicator
        string turnText = _playerIsWhite ? "Play as white" : "Play as black";
        Raylib.DrawText(turnText, (int)(panelX + offset.X + 24), (int)(y + offset.Y), 16, TextCol);
        var colorSquare = _playerIsWhite ? Color.White : new Color(38, 31, 26, 255);
        Raylib.DrawRectangle((int)(panelX + offset.X), (int)(y + 2 + offset.Y), 16, 16, colorSquare);
        Raylib.DrawRectangleLines((int)(panelX + offset.X - 1), (int)(y + 1 + offset.Y), 18, 18, DimTextCol);
        y += 36;

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
            Raylib.DrawText(statusText, (int)(panelX + offset.X), (int)(y + offset.Y), 18, statusColor);
            y += 30;
        }

        // Checkmate indicator
        if (_puzzleSolved || (_showingAnswer && _puzzleMovesMade >= _puzzleSolution.Length))
        {
            if (IsCheckmate(_whiteToMove))
            {
                Raylib.DrawText("Checkmate!", (int)(panelX + offset.X), (int)(y + offset.Y), 18, new Color(255, 217, 51, 255));
                y += 30;
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
                Raylib.DrawText(line, (int)(panelX + offset.X), (int)(y + offset.Y), 14, DimTextCol);
                y += 22; mi = 1; moveNum = 2;
            }
            while (mi < _moveHistory.Count)
            {
                string line = $"{moveNum}.{_moveHistory[mi].text}";
                if (mi + 1 < _moveHistory.Count)
                    line += $"  {_moveHistory[mi + 1].text}";
                Raylib.DrawText(line, (int)(panelX + offset.X), (int)(y + offset.Y), 14, DimTextCol);
                y += 22; mi += 2; moveNum++;
            }
        }
        y += 16;

        // Buttons
        float btnWidth = 250;
        float btnHeight = 32;
        float btnGap = 42;
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
        float bottomY = BoardOffset.Y + 8 * SquareSize - 20;
        string ratingText = _ratingHidden ? "Rating: ****" : $"Rating: {_puzzleRating}";
        Raylib.DrawText(ratingText, (int)(panelX + offset.X), (int)(bottomY + offset.Y), 14, DimTextCol);

        if (_puzzleId != "")
            Raylib.DrawText($"#{_puzzleId}", (int)(panelX + offset.X), (int)(bottomY + 22 + offset.Y), 12, DimTextCol);
    }

    private static void DrawButton(string text, float x, float y, float w, float h, Color bg, Color border)
    {
        Raylib.DrawRectangle((int)x, (int)y, (int)w, (int)h, bg);
        Raylib.DrawRectangleLines((int)x, (int)y, (int)w, (int)h, border);
        int textW = Raylib.MeasureText(text, 14);
        Raylib.DrawText(text, (int)(x + (w - textW) / 2), (int)(y + 9), 14, TextCol);
    }

    // ─── Offline puzzles ───

    private record PuzzleData(string Fen, string[] Solution, int Rating, string Id);

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
        new("r1bqk2r/ppppbppp/2n2n2/4p3/2BPP3/5N2/PPP2PPP/RNBQK2R b KQkq - 0 5", ["d7d5"], 800, "x5"),
    ];
}
