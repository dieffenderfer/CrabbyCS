using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Chess;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Chess puzzles in the retro Win9x style. Pulls puzzles from
/// lichess.org/api/puzzle/next via the shared <see cref="LichessClient"/>;
/// falls back to a small bundled puzzle set when offline. The player can
/// drag pieces or click source/destination, request a hint or full
/// solution playback, and step through multi-move puzzles with animated
/// opponent replies. Uses the shared <see cref="ChessEngine"/> for board
/// state and rule enforcement (castling, en passant, promotion, legal-move
/// filtering, checkmate detection).
/// </summary>
public class RetroChessPuzzlesActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Cell = 32;
    private const int Margin = 10;
    private const int Side = ChessEngine.BoardSide;
    private const int InfoWidth = 150;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + Side * Cell + InfoWidth + Margin,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + Side * Cell + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private readonly ChessEngine _engine = new();

    // Puzzle progression
    private string[] _solution = Array.Empty<string>();
    private int _movesMade;
    private bool _playerIsWhite = true;
    private bool _flipped;
    private int _rating;
    private string _puzzleId = "";
    private string[] _themes = Array.Empty<string>();
    private string _title = "";  // shown for offline puzzles in lieu of themes

    // Game-flow flags
    private bool _solved;
    private bool _failed;
    private float _failTimer;
    private bool _waitingForOpponent;
    private bool _showingAnswer;
    private int _solvedCount;

    // Loading
    private Task<LichessClient.FetchResult>? _fetchTask;
    private bool _loading;
    private bool _offlineMode;
    private string _loadError = "";
    // Cycled forward each time we fall back to an offline puzzle, so repeated
    // Next-clicks in offline mode walk through the bundled set instead of
    // hammering the same puzzle.
    private int _offlineCursor;
    private int _offlineIdx;

    // Selection / drag
    private (int x, int y) _sel = (-1, -1);
    private List<(int x, int y)> _legalDest = new();
    private bool _dragging;
    private (int x, int y) _dragFrom = (-1, -1);
    private Vector2 _dragPos;
    private (int x, int y) _dragHover = (-1, -1);

    // Animation
    private bool _animating;
    private float _animTimer;
    private float _animDuration;
    private Vector2 _animFromPx, _animToPx;
    private int _animPiece;
    private (int x, int y) _animFromSq, _animToSq;
    private string _animPromo = "";
    private bool _animIsOpponent;
    private bool _answerAnimating;
    private float _answerDelay;

    // Title-bar / status
    private string _statusMsg = "Loading puzzle…";

    // ── Bundled offline fallback puzzles ────────────────────────────────
    // Used only when the lichess API is unreachable. Each is a one-move
    // didactic position with a hand-written title. Themes are derived from
    // the title for the status bar.
    private record OfflinePuzzle(string Title, string Fen, bool WhiteToMove, string ExpectedUci, string[] Themes);
    private static readonly OfflinePuzzle[] OfflinePuzzles =
    {
        new("White to mate in 1 — back rank",
            "6k1/5ppp/8/8/8/8/8/R6K w - - 0 1", true, "a1a8",
            new[] { "mateIn1", "backRankMate" }),
        new("White to mate in 1 — queen ladder",
            "7k/8/6Q1/8/8/8/8/7K w - - 0 1", true, "g6g7", new[] { "mateIn1" }),
        new("White to mate in 1 — supported queen",
            "5rk1/5ppp/8/8/8/8/3Q4/3R3K w - - 0 1", true, "d2d8",
            new[] { "mateIn1", "kingsideAttack" }),
        new("White to mate in 1 — knight + bishop",
            "6k1/6pp/8/6N1/8/8/4B3/7K w - - 0 1", true, "e2c4",
            new[] { "mateIn1", "discoveredAttack" }),
        new("White to win the queen — fork",
            "4k3/8/4q3/8/3N4/8/8/4K3 w - - 0 1", true, "d4f5",
            new[] { "fork", "knight" }),
        new("White to mate in 1 — rook lift",
            "k7/2R5/1K6/8/8/8/8/8 w - - 0 1", true, "c7c8", new[] { "mateIn1" }),
        new("White to win material — pin",
            "4k3/8/8/3q4/8/8/3R4/3K4 w - - 0 1", true, "d2d5",
            new[] { "pin", "advantage" }),
    };

    private readonly RetroHelp _help = new()
    {
        Title = "Chess Puzzles — How to play",
        Lines = new[]
        {
            "Click your piece, click the destination — or drag.",
            "Find the best move; the engine answers with the opponent's reply.",
            "Hint highlights the source square. Show Move adds the target.",
            "Show Answer plays the full solution from here.",
            "Online puzzles come from lichess.org. Offline falls back to a",
            "small built-in set — the status bar shows which mode you're in.",
        },
    };

    // ── IActivity ───────────────────────────────────────────────────────

    public void Load() => StartFetch();

    public void Close() { }

    private void StartFetch()
    {
        ResetUiState();
        _engine.Reset();
        _loading = true;
        _offlineMode = false;
        _loadError = "";
        _statusMsg = "Loading puzzle…";
        _fetchTask = LichessClient.FetchNextAsync();
    }

    private void ResetUiState()
    {
        _sel = (-1, -1);
        _legalDest.Clear();
        _dragging = false;
        _dragFrom = (-1, -1);
        _dragHover = (-1, -1);
        _animating = false;
        _answerAnimating = false;
        _waitingForOpponent = false;
        _solved = false;
        _failed = false;
        _failTimer = 0;
        _showingAnswer = false;
        _movesMade = 0;
    }

    private void LoadFromLichess(LichessPuzzle p)
    {
        ResetUiState();
        if (!LichessClient.ApplyPuzzle(_engine, p))
        {
            // Bad PGN — drop to offline mode for this attempt
            LoadOffline(_offlineIdx);
            return;
        }
        _engine.RecordHistory = true;
        _solution = p.Solution;
        _rating = p.Rating;
        _puzzleId = p.Id;
        _themes = p.Themes;
        _title = "";
        _playerIsWhite = _engine.WhiteToMove;
        _flipped = !_playerIsWhite;
        _statusMsg = "Your move.";
    }

    private void LoadOffline(int idx)
    {
        ResetUiState();
        _offlineMode = true;
        idx = ((idx % OfflinePuzzles.Length) + OfflinePuzzles.Length) % OfflinePuzzles.Length;
        _offlineIdx = idx;
        var p = OfflinePuzzles[idx];
        _engine.LoadFen(p.Fen);
        _engine.RecordHistory = true;
        _engine.History.Clear();
        _solution = new[] { p.ExpectedUci };
        _rating = 0;
        _puzzleId = $"offline-{idx + 1}";
        _themes = p.Themes;
        _title = p.Title;
        _playerIsWhite = p.WhiteToMove;
        _flipped = !_playerIsWhite;
        _statusMsg = p.Title;
    }

    // ── Update ──────────────────────────────────────────────────────────

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;

        // Title bar close
        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        { IsFinished = true; return; }

        // Menu bar
        var menuItems = MenuItems();
        var menuBar = new Rectangle(FrameInset, FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        int menuClicked = RetroWidgets.MenuBarHitTest(menuBar, menuItems, local, leftPressed);
        if (menuClicked >= 0) { OnMenuClick(menuItems[menuClicked]); return; }

        // Help overlay
        if (_help.HandleInput(local, leftPressed, PanelSize)) return;

        // Poll fetch task
        if (_loading && _fetchTask != null && _fetchTask.IsCompleted)
        {
            _loading = false;
            var result = _fetchTask.Result;
            _fetchTask = null;
            if (result.Ok && result.Puzzle != null)
            {
                LoadFromLichess(result.Puzzle);
            }
            else
            {
                _loadError = result.Error.Length > 0 ? result.Error : "Failed to load";
                LoadOffline(_offlineCursor);
                _offlineCursor++;
            }
            return;
        }
        if (_loading) return;

        // Failed flash auto-clears (so player can keep trying)
        if (_failed)
        {
            _failTimer += delta;
            if (_failTimer >= 1.5f) { _failed = false; _failTimer = 0; }
        }

        // Move animation
        if (_animating)
        {
            _animTimer += delta;
            if (_animTimer >= _animDuration)
            {
                _animating = false;
                _engine.Board[_animFromSq.y, _animFromSq.x] = _animPiece;
                _engine.MakeMove((_animFromSq.y, _animFromSq.x), (_animToSq.y, _animToSq.x), _animPromo);

                if (_animIsOpponent)
                {
                    _movesMade++;
                    _waitingForOpponent = false;
                    if (_movesMade >= _solution.Length) MarkSolved();
                    else _statusMsg = "Your move.";
                }
                else if (_answerAnimating)
                {
                    _movesMade++;
                }
            }
            return;
        }

        // Answer auto-playback
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

        if (_solved || _waitingForOpponent || _showingAnswer) { CancelDrag(); return; }

        // Board interaction
        var (bx, by) = BoardOriginPx();
        bool overBoard = local.X >= bx && local.Y >= by &&
                         local.X < bx + Side * Cell && local.Y < by + Side * Cell;

        if (leftPressed && overBoard)
        {
            var sq = ScreenToSquare(local, bx, by);
            if (sq != (-1, -1) && _engine.Board[sq.y, sq.x] != 0 &&
                ChessEngine.IsWhite(_engine.Board[sq.y, sq.x]) == _playerIsWhite &&
                _engine.WhiteToMove == _playerIsWhite)
            {
                _sel = sq;
                _legalDest = ToXY(_engine.GetLegalMoves((sq.y, sq.x)));
                _dragging = true;
                _dragFrom = sq;
                _dragPos = local;
                _dragHover = (-1, -1);
            }
            else if (_sel != (-1, -1) && sq != (-1, -1) && _legalDest.Contains(sq))
            {
                TryPlayerMove(_sel, sq);
                _sel = (-1, -1);
                _legalDest.Clear();
            }
            else
            {
                _sel = (-1, -1);
                _legalDest.Clear();
            }
        }

        if (_dragging)
        {
            _dragPos = local;
            _dragHover = ScreenToSquare(local, bx, by);
        }

        if (leftReleased && _dragging)
        {
            var drop = ScreenToSquare(local, bx, by);
            _dragging = false;
            if (drop != (-1, -1) && drop != _dragFrom && _legalDest.Contains(drop))
            {
                TryPlayerMove(_dragFrom, drop);
                _sel = (-1, -1);
                _legalDest.Clear();
            }
            // If they dropped on a same-color piece, treat as a re-select.
            else if (drop != (-1, -1) && _engine.Board[drop.y, drop.x] != 0 &&
                     ChessEngine.IsWhite(_engine.Board[drop.y, drop.x]) == _playerIsWhite)
            {
                _sel = drop;
                _legalDest = ToXY(_engine.GetLegalMoves((drop.y, drop.x)));
            }
            // Otherwise keep current selection (so click-source/click-dest still works
            // if the user just tapped the source).
            _dragHover = (-1, -1);
            _dragFrom = (-1, -1);
        }
    }

    private string[] MenuItems()
    {
        // Adapt menu to state — solved/showing-answer collapses helper buttons.
        if (_solved || (_showingAnswer && _movesMade >= _solution.Length))
            return new[] { "Next", "Flip", "Help" };
        return new[] { "Next", "Hint", "Show Move", "Answer", "Flip", "Help" };
    }

    private void OnMenuClick(string item)
    {
        switch (item)
        {
            case "Next": StartFetch(); break;
            case "Hint": ShowHint(); break;
            case "Show Move": ShowMoveHint(); break;
            case "Answer": ShowAnswer(); break;
            case "Flip": _flipped = !_flipped; break;
            case "Help": _help.Visible = !_help.Visible; break;
        }
    }

    private static List<(int x, int y)> ToXY(List<(int r, int c)> rcs)
    {
        var list = new List<(int x, int y)>(rcs.Count);
        foreach (var rc in rcs) list.Add((rc.c, rc.r));
        return list;
    }

    private (int x, int y) ScreenToSquare(Vector2 local, float bx, float by)
    {
        if (local.X < bx || local.Y < by) return (-1, -1);
        int gx = (int)((local.X - bx) / Cell);
        int gy = (int)((local.Y - by) / Cell);
        if (gx < 0 || gx >= Side || gy < 0 || gy >= Side) return (-1, -1);
        if (_flipped) return (Side - 1 - gx, Side - 1 - gy);
        return (gx, gy);
    }

    private (float x, float y) BoardOriginPx()
        => (FrameInset + Margin,
            FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin);

    private Vector2 SquareToScreen(int x, int y)
    {
        var (bx, by) = BoardOriginPx();
        if (_flipped) return new Vector2(bx + (Side - 1 - x) * Cell, by + (Side - 1 - y) * Cell);
        return new Vector2(bx + x * Cell, by + y * Cell);
    }

    private void CancelDrag()
    {
        _dragging = false;
        _dragFrom = (-1, -1);
        _dragHover = (-1, -1);
    }

    private void TryPlayerMove((int x, int y) from, (int x, int y) to)
    {
        if (_movesMade >= _solution.Length) return;
        string expected = _solution[_movesMade];
        string playedUci = ChessEngine.SquareToUci((from.y, from.x)) +
                           ChessEngine.SquareToUci((to.y, to.x));

        // Auto-promote to queen for now (matches non-retro behavior). Lichess
        // solutions encode the chosen promotion piece in the UCI string.
        string promo = "";
        int piece = _engine.Board[from.y, from.x];
        if (Math.Abs(piece) == 1 && (to.y == 0 || to.y == 7))
        {
            promo = "q";
            playedUci += "q";
        }

        if (playedUci == expected)
        {
            _engine.MakeMove((from.y, from.x), (to.y, to.x), promo);
            _movesMade++;
            if (_movesMade >= _solution.Length) { MarkSolved(); return; }

            _waitingForOpponent = true;
            _statusMsg = "…";
            StartOpponentAnim();
        }
        else
        {
            _failed = true;
            _failTimer = 0;
            _statusMsg = "Wrong move — try again.";
        }
    }

    private void MarkSolved()
    {
        _solved = true;
        _solvedCount++;
        _statusMsg = _engine.IsCheckmate(_engine.WhiteToMove) ? "Checkmate! Click Next." : "Solved! Click Next.";
    }

    private void StartOpponentAnim()
    {
        if (_movesMade >= _solution.Length) return;
        StartMoveAnim(_solution[_movesMade], isOpponent: true, durationS: 0.32f);
    }

    private void StartMoveAnim(string uci, bool isOpponent, float durationS)
    {
        var fromRC = ChessEngine.UciToSquare(uci[..2]);
        var toRC = ChessEngine.UciToSquare(uci[2..4]);
        string promo = uci.Length > 4 ? uci[4..] : "";

        int p = _engine.Board[fromRC.r, fromRC.c];
        _animating = true;
        _animTimer = 0;
        _animDuration = durationS;
        _animFromPx = SquareToScreen(fromRC.c, fromRC.r);
        _animToPx = SquareToScreen(toRC.c, toRC.r);
        _animPiece = p;
        _animFromSq = (fromRC.c, fromRC.r);
        _animToSq = (toRC.c, toRC.r);
        _animPromo = promo;
        _animIsOpponent = isOpponent;

        // Hide source piece during the slide (we'll restore it inside MakeMove).
        _engine.Board[fromRC.r, fromRC.c] = 0;
    }

    private void ShowHint()
    {
        if (_solved || _showingAnswer || _movesMade >= _solution.Length) return;
        var srcRC = ChessEngine.UciToSquare(_solution[_movesMade][..2]);
        _sel = (srcRC.c, srcRC.r);
        _legalDest.Clear();
        _statusMsg = "Hint: move from " + _solution[_movesMade][..2];
    }

    private void ShowMoveHint()
    {
        if (_solved || _showingAnswer || _movesMade >= _solution.Length) return;
        string uci = _solution[_movesMade];
        var srcRC = ChessEngine.UciToSquare(uci[..2]);
        var dstRC = ChessEngine.UciToSquare(uci[2..4]);
        _sel = (srcRC.c, srcRC.r);
        _legalDest = new() { (dstRC.c, dstRC.r) };
        _statusMsg = "Move: " + uci;
    }

    private void ShowAnswer()
    {
        if (_solved || _showingAnswer) return;
        _showingAnswer = true;
        CancelDrag();
        _sel = (-1, -1);
        _legalDest.Clear();
        _answerAnimating = true;
        _answerDelay = 0;
        _statusMsg = "Solution playback…";
        AnimateNextAnswerMove();
    }

    private void AnimateNextAnswerMove()
    {
        if (_movesMade >= _solution.Length)
        {
            _answerAnimating = false;
            _statusMsg = _engine.IsCheckmate(_engine.WhiteToMove) ? "Checkmate." : "End of solution.";
            return;
        }
        StartMoveAnim(_solution[_movesMade], isOpponent: false, durationS: 0.4f);
    }

    // ── Draw ────────────────────────────────────────────────────────────

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Chess Puzzles", true);

        var menuItems = MenuItems();
        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, menuItems, -1);

        var (bxLocal, byLocal) = BoardOriginPx();
        float bx = panelOffset.X + bxLocal;
        float by = panelOffset.Y + byLocal;

        DrawBoardSquares(bx, by);
        DrawHighlights(bx, by);
        DrawCoordinates(bx, by);
        DrawPieces(bx, by, panelOffset);

        // Drag piece on top
        if (_dragging && _dragFrom != (-1, -1))
        {
            int p = _engine.Board[_dragFrom.y, _dragFrom.x];
            if (p != 0)
            {
                float dx = panelOffset.X + _dragPos.X - Cell / 2f;
                float dy = panelOffset.Y + _dragPos.Y - Cell / 2f;
                DrawPieceGlyph(p, (int)dx, (int)dy);
            }
        }

        DrawSidePanel(panelOffset, bx, by);
        DrawStatusBar(panelOffset);
        _help.Draw(panelOffset, PanelSize);
    }

    private void DrawBoardSquares(float bx, float by)
    {
        var light = new Color(232, 216, 184, 255);
        var dark = new Color(120, 88, 56, 255);
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                int dx = _flipped ? Side - 1 - x : x;
                int dy = _flipped ? Side - 1 - y : y;
                Raylib.DrawRectangle((int)(bx + dx * Cell), (int)(by + dy * Cell),
                    Cell, Cell, (x + y) % 2 == 0 ? light : dark);
            }

        // Frame around the board
        Raylib.DrawRectangleLines((int)bx - 1, (int)by - 1, Side * Cell + 2, Side * Cell + 2, RetroSkin.DarkShadow);
    }

    private void DrawHighlights(float bx, float by)
    {
        // Last move
        if (_engine.LastMoveFrom != (-1, -1))
        {
            DrawSquareTint(bx, by, _engine.LastMoveFrom.c, _engine.LastMoveFrom.r,
                new Color(220, 200, 80, 100));
            DrawSquareTint(bx, by, _engine.LastMoveTo.c, _engine.LastMoveTo.r,
                new Color(220, 200, 80, 100));
        }

        // Selected square
        if (_sel != (-1, -1))
            DrawSquareTint(bx, by, _sel.x, _sel.y, new Color(120, 200, 80, 120));

        // Legal destinations
        foreach (var (x, y) in _legalDest)
        {
            var pos = SquareForOrigin(bx, by, x, y);
            Raylib.DrawCircle((int)(pos.X + Cell / 2), (int)(pos.Y + Cell / 2), 7,
                new Color(60, 200, 60, 180));
        }

        // Drag hover
        if (_dragging && _dragHover != (-1, -1) && _legalDest.Contains(_dragHover))
            DrawSquareTint(bx, by, _dragHover.x, _dragHover.y, new Color(130, 195, 130, 120));

        // Failed flash
        if (_failed)
            DrawSquareTint(bx, by, _engine.LastMoveTo.c, _engine.LastMoveTo.r,
                new Color(230, 80, 50, 90));
    }

    private void DrawSquareTint(float bx, float by, int x, int y, Color c)
    {
        var p = SquareForOrigin(bx, by, x, y);
        Raylib.DrawRectangle((int)p.X, (int)p.Y, Cell, Cell, c);
    }

    private Vector2 SquareForOrigin(float bx, float by, int x, int y)
    {
        if (_flipped) return new Vector2(bx + (Side - 1 - x) * Cell, by + (Side - 1 - y) * Cell);
        return new Vector2(bx + x * Cell, by + y * Cell);
    }

    private void DrawCoordinates(float bx, float by)
    {
        const string files = "abcdefgh";
        const string ranks = "87654321";
        for (int i = 0; i < Side; i++)
        {
            int fi = _flipped ? Side - 1 - i : i;
            RetroSkin.DrawText(files[fi].ToString(),
                (int)(bx + i * Cell + Cell - 9), (int)(by + Side * Cell - 12),
                new Color(40, 24, 12, 220), 10);

            int ri = _flipped ? Side - 1 - i : i;
            RetroSkin.DrawText(ranks[ri].ToString(),
                (int)(bx + 2), (int)(by + i * Cell + 1),
                new Color(40, 24, 12, 220), 10);
        }
    }

    private void DrawPieces(float bx, float by, Vector2 panelOffset)
    {
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                if (_dragging && _dragFrom == (x, y)) continue;
                int p = _engine.Board[y, x];
                if (p == 0) continue;
                var pos = SquareForOrigin(bx, by, x, y);
                DrawPieceGlyph(p, (int)pos.X, (int)pos.Y);
            }

        if (_animating)
        {
            float t = Math.Min(_animTimer / _animDuration, 1f);
            t = 1f - (1f - t) * (1f - t);
            var pos = Vector2.Lerp(_animFromPx, _animToPx, t) + panelOffset;
            DrawPieceGlyph(_animPiece, (int)pos.X, (int)pos.Y);
        }
    }

    /// <summary>
    /// Render a single piece as its Unicode chess glyph, centred in a Cell-
    /// sized square at <paramref name="cellX"/>, <paramref name="cellY"/>.
    /// Routes through RetroSkin.DrawText so the GlyphFallback layer (bundled
    /// DejaVu Sans, which carries U+2654-265F) handles the codepoints —
    /// W95F.otf alone doesn't have them. A 4-directional outline pass in the
    /// contrasting colour keeps both white and black pieces readable on the
    /// cream / dark-brown checkerboard.
    /// </summary>
    private void DrawPieceGlyph(int piece, int cellX, int cellY)
    {
        if (piece == 0) return;
        bool white = piece > 0;
        string g = Math.Abs(piece) switch
        {
            1 => white ? "♙" : "♟",
            2 => white ? "♘" : "♞",
            3 => white ? "♗" : "♝",
            4 => white ? "♖" : "♜",
            5 => white ? "♕" : "♛",
            6 => white ? "♔" : "♚",
            _ => "?",
        };
        // Glyph size ~88% of cell — DejaVu chess glyphs render at ~0.7× their
        // font size visually, so fontSize 28 in a 32 px cell ends up ~22 px
        // tall (≈70% of the cell), which is the requested 70-80% range.
        const int fontSize = 28;
        int textW = RetroSkin.MeasureText(g, fontSize);
        int x = cellX + (Cell - textW) / 2;
        int y = cellY + (Cell - fontSize) / 2;
        var fill = white ? new Color(245, 240, 225, 255) : new Color(20, 20, 20, 255);
        var outline = white ? new Color(20, 20, 20, 220) : new Color(245, 240, 225, 220);
        RetroSkin.DrawText(g, x - 1, y, outline, fontSize);
        RetroSkin.DrawText(g, x + 1, y, outline, fontSize);
        RetroSkin.DrawText(g, x, y - 1, outline, fontSize);
        RetroSkin.DrawText(g, x, y + 1, outline, fontSize);
        RetroSkin.DrawText(g, x, y, fill, fontSize);
    }

    private void DrawSidePanel(Vector2 panelOffset, float bx, float by)
    {
        float sx = bx + Side * Cell + Margin;
        var sidePanel = new Rectangle(sx, by, InfoWidth, Side * Cell);
        RetroSkin.DrawSunken(sidePanel, RetroSkin.Face);

        int x = (int)sx + 8;
        int y = (int)by + 8;

        RetroSkin.DrawText("Solved", x, y, RetroSkin.BodyText, 14); y += 16;
        RetroSkin.DrawText(_solvedCount.ToString(), x, y, RetroSkin.BodyText, 14); y += 22;

        RetroSkin.DrawText(_playerIsWhite ? "Play as White" : "Play as Black",
            x, y, RetroSkin.BodyText, 12); y += 16;

        // Color swatch + "to move"
        var swatch = new Rectangle(x, y, 12, 12);
        Raylib.DrawRectangleRec(swatch,
            _engine.WhiteToMove ? new Color(245, 240, 225, 255) : new Color(35, 25, 20, 255));
        Raylib.DrawRectangleLines((int)swatch.X, (int)swatch.Y, (int)swatch.Width, (int)swatch.Height,
            RetroSkin.Shadow);
        RetroSkin.DrawText(_engine.WhiteToMove ? "White to move" : "Black to move",
            x + 18, y - 1, RetroSkin.BodyText, 12);
        y += 22;

        // Move history
        var hist = _engine.History;
        if (hist.Count > 0)
        {
            RetroSkin.DrawText("Moves", x, y, RetroSkin.BodyText, 12); y += 14;
            int moveNum = 1, mi = 0;
            int maxLines = (Side * Cell - (y - (int)by) - 36) / 14; // leave room for footer
            int linesDrawn = 0;
            if (!hist[0].white)
            {
                RetroSkin.DrawText($"{moveNum}…{hist[0].text}", x, y, RetroSkin.DisabledText, 12);
                y += 14; mi = 1; moveNum = 2; linesDrawn++;
            }
            while (mi < hist.Count && linesDrawn < maxLines)
            {
                string line = $"{moveNum}.{hist[mi].text}";
                if (mi + 1 < hist.Count) line += $"  {hist[mi + 1].text}";
                RetroSkin.DrawText(line, x, y, RetroSkin.DisabledText, 12);
                y += 14; mi += 2; moveNum++; linesDrawn++;
            }
        }

        // Footer: rating + id
        int fy = (int)(by + Side * Cell) - 30;
        if (_offlineMode)
        {
            RetroSkin.DrawText("Offline", x, fy, RetroSkin.DisabledText, 12);
        }
        else
        {
            string ratingText = _rating > 0 ? $"Rating: {_rating}" : "Rating: ?";
            RetroSkin.DrawText(ratingText, x, fy, RetroSkin.DisabledText, 12);
        }
        if (_puzzleId != "")
            RetroSkin.DrawText($"#{_puzzleId}", x, fy + 14, RetroSkin.DisabledText, 12);
    }

    private void DrawStatusBar(Vector2 panelOffset)
    {
        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);

        string left = StatusLeft();
        string right = StatusRight();
        RetroWidgets.StatusBar(status, left, right);
    }

    private string StatusLeft()
    {
        if (_loading) return "Loading puzzle from lichess.org…";
        if (_offlineMode)
        {
            // Show themes if the offline puzzle has any, plus a marker.
            string themes = LichessClient.FormatThemes(_themes, max: 3);
            string body = !string.IsNullOrEmpty(_title) ? _title :
                          !string.IsNullOrEmpty(themes) ? themes : "Offline puzzle";
            return $"Offline — {body}";
        }
        // Online: prefer current state message; fall back to themes.
        if (!string.IsNullOrEmpty(_statusMsg) && _statusMsg != "Your move.") return _statusMsg;
        string fmtThemes = LichessClient.FormatThemes(_themes, max: 3);
        return string.IsNullOrEmpty(fmtThemes) ? _statusMsg : fmtThemes;
    }

    private string StatusRight()
    {
        if (_solved) return "✓ correct";
        if (_failed) return "✗ wrong";
        if (_showingAnswer) return "answer";
        if (_waitingForOpponent || _animating) return "…";
        if (_loading) return "…";
        return _engine.WhiteToMove == _playerIsWhite ? "your move" : "opponent";
    }
}
