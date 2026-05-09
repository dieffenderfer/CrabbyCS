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
    private const int Cell = 36;
    private const int Margin = 10;
    private const int Side = ChessEngine.BoardSide;
    private const int InfoWidth = 160;

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

    // Right-click annotations (lichess-style): tap-to-toggle a circle on
    // a square, drag-between-squares to toggle an arrow. Stored in board
    // (x, y) coords — same convention the rest of this file uses for
    // squares.
    private static readonly Color AnnotationCol = new((byte)21, (byte)120, (byte)27, (byte)200);
    private readonly List<(int x, int y)> _circles = new();
    private readonly List<((int x, int y) from, (int x, int y) to)> _arrows = new();
    private bool _rightDragging;
    private (int x, int y) _rightDragFrom = (-1, -1);

    // Cross-fade transition when Next loads a new puzzle. Snapshot the
    // current board before reset; once the new state is applied, diff
    // it square-by-square and run a quick fade so unchanged pieces hold
    // still while changed ones cross-fade in/out. Total animation is a
    // few frames so the swap reads as a quick rearrangement, not a hard
    // cut.
    private int[,]? _prevBoardSnapshot;
    private bool[,]? _transitionDiff;
    private bool _transitionActive;
    private float _transitionTime;
    private const float TransitionDuration = 0.10f;

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
    private string _statusMsg = "Loading puzzle...";
    /// <summary>Toggled by clicking the (?) glyph in the status bar — when
    /// true the prettified theme strip replaces the normal status message.
    /// Resets to false on each new puzzle load so the user explicitly opts
    /// into "spoiler" theme info per puzzle.</summary>
    private bool _showThemes;
    private bool _showRating;     // off by default; click the rating row to toggle
    /// <summary>Panel-local rect of the "Rating: ****" / "Rating: 1500" row
    /// in the side panel — captured during draw and consumed by Update so
    /// clicking the row toggles the masked / shown state.</summary>
    private Rectangle _ratingRowRect;

    // ── Bundled offline fallback puzzles ────────────────────────────────
    // Used only when the lichess API is unreachable. Each is a one-move
    // didactic position with a hand-written title. Themes are derived from
    // the title for the status bar.
    private record OfflinePuzzle(string Title, string Fen, bool WhiteToMove, string ExpectedUci, string[] Themes);
    private static readonly OfflinePuzzle[] OfflinePuzzles =
    {
        new("White to mate in 1 - back rank",
            "6k1/5ppp/8/8/8/8/8/R6K w - - 0 1", true, "a1a8",
            new[] { "mateIn1", "backRankMate" }),
        new("White to mate in 1 - queen ladder",
            "7k/8/6Q1/8/8/8/8/7K w - - 0 1", true, "g6g7", new[] { "mateIn1" }),
        new("White to mate in 1 - supported queen",
            "5rk1/5ppp/8/8/8/8/3Q4/3R3K w - - 0 1", true, "d2d8",
            new[] { "mateIn1", "kingsideAttack" }),
        new("White to mate in 1 - knight + bishop",
            "6k1/6pp/8/6N1/8/8/4B3/7K w - - 0 1", true, "e2c4",
            new[] { "mateIn1", "discoveredAttack" }),
        new("White to win the queen - fork",
            "4k3/8/4q3/8/3N4/8/8/4K3 w - - 0 1", true, "d4f5",
            new[] { "fork", "knight" }),
        new("White to mate in 1 - rook lift",
            "k7/2R5/1K6/8/8/8/8/8 w - - 0 1", true, "c7c8", new[] { "mateIn1" }),
        new("White to win material - pin",
            "4k3/8/8/3q4/8/8/3R4/3K4 w - - 0 1", true, "d2d5",
            new[] { "pin", "advantage" }),
    };

    private readonly RetroHelp _help = new()
    {
        Title = "Chess Puzzles - How to play",
        Lines = new[]
        {
            "Click your piece, click the destination - or drag.",
            "Find the best move; the engine answers with the opponent's reply.",
            "Hint highlights the source square. Show Move adds the target.",
            "Show Answer plays the full solution from here.",
            "Online puzzles come from lichess.org. Offline falls back to a",
            "small built-in set - the status bar shows which mode you're in.",
        },
    };

    // ── IActivity ───────────────────────────────────────────────────────

    public void Load()
    {
        ChessBoardThemes.Load();
        ChessPieceFonts.Load();
        StartFetch();
    }

    public void Close() { }

    private void StartFetch()
    {
        // Snapshot the current board before we wipe it — the transition
        // animation diffs this against the new puzzle's starting state.
        SnapshotBoardForTransition();
        ResetUiState();
        _engine.Reset();
        _loading = true;
        _offlineMode = false;
        _loadError = "";
        _statusMsg = "Loading puzzle...";
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
        _showThemes = false;     // each new puzzle starts with themes hidden
        _circles.Clear();
        _arrows.Clear();
        _rightDragging = false;
        _rightDragFrom = (-1, -1);
    }

    /// <summary>Copy the current engine board into <see cref="_prevBoardSnapshot"/>
    /// so the next puzzle load can diff against it for the cross-fade.</summary>
    private void SnapshotBoardForTransition()
    {
        int n = ChessEngine.BoardSide;
        _prevBoardSnapshot ??= new int[n, n];
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
                _prevBoardSnapshot[r, c] = _engine.Board[r, c];
    }

    /// <summary>Build the per-square diff against the snapshot and start
    /// the transition timer. Called after a new puzzle's board state is
    /// applied — diffSquares cross-fade their old/new pieces while every
    /// other square renders the engine board normally.</summary>
    private void BeginPieceTransition()
    {
        if (_prevBoardSnapshot == null) return;
        int n = ChessEngine.BoardSide;
        _transitionDiff ??= new bool[n, n];
        bool any = false;
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                bool diff = _prevBoardSnapshot[r, c] != _engine.Board[r, c];
                _transitionDiff[r, c] = diff;
                if (diff) any = true;
            }
        _transitionActive = any;
        _transitionTime = 0f;
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
        _statusMsg = "";
        BeginPieceTransition();
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
        BeginPieceTransition();
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

        // Tick the cross-fade transition that runs after a Next-load
        // when the board state has changed. Once aged out it stops
        // contributing alpha and DrawPieces falls back to the engine
        // board straight.
        if (_transitionActive)
        {
            _transitionTime += delta;
            if (_transitionTime >= TransitionDuration) _transitionActive = false;
        }

        // Click on the side-panel rating row toggles the rating mask.
        // Replicates the original (non-retro) chess-puzzle behaviour
        // where the rating text was directly clickable.
        if (leftPressed && _ratingRowRect.Width > 0
            && RetroSkin.PointInRect(local, _ratingRowRect))
        {
            _showRating = !_showRating;
            return;
        }

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
                    else _statusMsg = "";
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

        // Compute board bounds — needed by both the annotation handlers
        // (right-click) and the regular play interaction (left-click)
        // below, so do it before the early-return-on-solved guard.
        var (bx, by) = BoardOriginPx();
        bool overBoard = local.X >= bx && local.Y >= by &&
                         local.X < bx + Side * Cell && local.Y < by + Side * Cell;

        // Right-click annotations (lichess-style). Tap a square: toggle a
        // ring on it. Drag from one square to another: toggle an arrow
        // between them. Stays enabled even while waiting for the opponent
        // / showing the answer so the player can mark up the board freely.
        if (rightPressed && overBoard)
        {
            var sq = ScreenToSquare(local, bx, by);
            if (sq != (-1, -1))
            {
                _rightDragging = true;
                _rightDragFrom = sq;
            }
        }
        if (_rightDragging && Raylib.IsMouseButtonReleased(MouseButton.Right))
        {
            var sq = ScreenToSquare(local, bx, by);
            if (sq != (-1, -1) && _rightDragFrom != (-1, -1))
            {
                if (sq == _rightDragFrom) ToggleCircle(sq);
                else                       ToggleArrow(_rightDragFrom, sq);
            }
            _rightDragging = false;
            _rightDragFrom = (-1, -1);
        }

        // Left-click on the board clears any annotations the player has
        // drawn — same wipe-on-interact behaviour the original puzzle
        // had so circles/arrows don't pile up across moves.
        if (leftPressed && overBoard && (_circles.Count > 0 || _arrows.Count > 0))
        {
            _circles.Clear();
            _arrows.Clear();
        }

        // Click anywhere outside the board (side panel, dead space)
        // clears the active piece selection — fixes the long-standing
        // "the move dots stick around forever" complaint where a tap
        // off-board was being silently ignored by the on-board handler.
        if (leftPressed && !overBoard && _sel != (-1, -1))
        {
            _sel = (-1, -1);
            _legalDest.Clear();
        }

        if (_solved || _waitingForOpponent || _showingAnswer) { CancelDrag(); return; }

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
        // "Theme" / "Rating" toggle whether the puzzle theme strip and rating
        // are revealed; the labels flip between Show/Hide so the bare button
        // tells the user what clicking it will do.
        string themeLabel = _showThemes ? "Hide Theme" : "Show Theme";
        string ratingLabel = _showRating ? "Hide Rating" : "Show Rating";
        if (_solved || (_showingAnswer && _movesMade >= _solution.Length))
            return new[] { "Next", "Flip", "Board", themeLabel, ratingLabel, "Help" };
        return new[] { "Next", "Hint", "Show Move", "Answer", "Flip", "Board", themeLabel, ratingLabel, "Help" };
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
            case "Board":
                ChessBoardThemes.Cycle();
                break;
            case "Show Theme":
            case "Hide Theme":
                _showThemes = !_showThemes;
                break;
            case "Show Rating":
            case "Hide Rating":
                _showRating = !_showRating;
                break;
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
            _statusMsg = "...";
            StartOpponentAnim();
        }
        else
        {
            _failed = true;
            _failTimer = 0;
            _statusMsg = "Wrong move - try again.";
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
        _statusMsg = "Solution playback...";
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
        DrawAnnotations(bx, by);

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
        // Pick up theme changes made by another process (or earlier in
        // this one) so the live UI matches whatever's persisted.
        ChessBoardThemes.PollExternalChange();
        var theme = ChessBoardThemes.Current;
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                int dx = _flipped ? Side - 1 - x : x;
                int dy = _flipped ? Side - 1 - y : y;
                Raylib.DrawRectangle((int)(bx + dx * Cell), (int)(by + dy * Cell),
                    Cell, Cell, (x + y) % 2 == 0 ? theme.Light : theme.Dark);
            }

        // Frame around the board
        Raylib.DrawRectangleLines((int)bx - 1, (int)by - 1, Side * Cell + 2, Side * Cell + 2, RetroSkin.DarkShadow);
    }

    private void DrawHighlights(float bx, float by)
    {
        var theme = ChessBoardThemes.Current;

        // Last move
        if (_engine.LastMoveFrom != (-1, -1))
        {
            DrawSquareTint(bx, by, _engine.LastMoveFrom.c, _engine.LastMoveFrom.r, theme.LastMoveTint);
            DrawSquareTint(bx, by, _engine.LastMoveTo.c, _engine.LastMoveTo.r, theme.LastMoveTint);
        }

        // Selected square
        if (_sel != (-1, -1))
            DrawSquareTint(bx, by, _sel.x, _sel.y, theme.SelectedTint);

        // Legal destinations
        foreach (var (x, y) in _legalDest)
        {
            var pos = SquareForOrigin(bx, by, x, y);
            Raylib.DrawCircle((int)(pos.X + Cell / 2), (int)(pos.Y + Cell / 2), 7, theme.LegalDot);
        }

        // Drag hover
        if (_dragging && _dragHover != (-1, -1) && _legalDest.Contains(_dragHover))
            DrawSquareTint(bx, by, _dragHover.x, _dragHover.y, theme.SelectedTint);

        // Failed flash
        if (_failed)
            DrawSquareTint(bx, by, _engine.LastMoveTo.c, _engine.LastMoveTo.r,
                new Color((byte)230, (byte)80, (byte)50, (byte)90));
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
        var col = ChessBoardThemes.Current.CoordLabel;
        // Jacquard12 needs a slightly larger nominal size than W95F to read
        // at the same visual weight in a 32 px cell — 14 lands cleanly.
        const int size = 14;
        for (int i = 0; i < Side; i++)
        {
            int fi = _flipped ? Side - 1 - i : i;
            BoardLabelFont.DrawText(files[fi].ToString(),
                (int)(bx + i * Cell + Cell - 10), (int)(by + Side * Cell - 14),
                size, col);

            int ri = _flipped ? Side - 1 - i : i;
            BoardLabelFont.DrawText(ranks[ri].ToString(),
                (int)(bx + 2), (int)(by + i * Cell - 1),
                size, col);
        }
    }

    private void DrawPieces(float bx, float by, Vector2 panelOffset)
    {
        bool transition = _transitionActive
            && _transitionDiff != null
            && _prevBoardSnapshot != null;
        float t01 = transition ? Math.Clamp(_transitionTime / TransitionDuration, 0f, 1f) : 1f;
        byte oldAlpha = (byte)Math.Clamp((int)((1f - t01) * 255), 0, 255);
        byte newAlpha = (byte)Math.Clamp((int)(t01 * 255), 0, 255);

        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                if (_dragging && _dragFrom == (x, y)) continue;
                var pos = SquareForOrigin(bx, by, x, y);
                int p = _engine.Board[y, x];

                if (transition && _transitionDiff![y, x])
                {
                    // Diff square: cross-fade old → new. An unchanged
                    // square below this branch always renders the live
                    // engine board at full opacity, so pieces that didn't
                    // move stay rock-steady while only the changing ones
                    // animate.
                    int oldP = _prevBoardSnapshot![y, x];
                    if (oldP != 0 && oldAlpha > 0)
                        DrawPieceGlyph(oldP, (int)pos.X, (int)pos.Y, oldAlpha);
                    if (p != 0 && newAlpha > 0)
                        DrawPieceGlyph(p, (int)pos.X, (int)pos.Y, newAlpha);
                }
                else if (p != 0)
                {
                    DrawPieceGlyph(p, (int)pos.X, (int)pos.Y);
                }
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
    /// sized square. Draws directly with the user-picked piece font (see
    /// ChessPieceFonts) — bypassing RetroSkin.DrawText so we always render
    /// the chosen face for chess pieces specifically, regardless of which
    /// run the GlyphFallback layer would route U+2654-265F to.
    ///
    /// Both colours use the SOLID (U+265A-F) filled-silhouette glyphs.
    /// The outline-only "white" Unicode glyphs (U+2654-2659) leave the
    /// interior transparent, so the square's colour shows through any
    /// fill we paint — that's why an earlier white-fill attempt looked
    /// like a hollow outline floating on the wood. Filling the solid
    /// glyph in white + a 1 px black outline gives the proper "white
    /// piece with shape" silhouette the user wanted.
    /// </summary>
    private void DrawPieceGlyph(int piece, int cellX, int cellY, byte alpha = 255)
    {
        if (piece == 0 || alpha == 0) return;
        bool white = piece > 0;
        int kind = Math.Abs(piece);
        string g = kind switch
        {
            1 => "♟",  // pawn
            2 => "♞",  // knight
            3 => "♝",  // bishop
            4 => "♜",  // rook
            5 => "♛",  // queen
            6 => "♚",  // king
            _ => "?",
        };
        // Per-piece size tweaks: queen / king / knight read best a touch
        // larger; bishop a touch larger but less; pawn a touch smaller.
        int fontSize = kind switch
        {
            1 => 30,  // pawn
            2 => 36,  // knight
            3 => 33,  // bishop
            4 => 32,  // rook (baseline)
            5 => 36,  // queen
            6 => 36,  // king
            _ => 32,
        };
        ChessPieceFonts.PollExternalChange();
        var font = ChessPieceFonts.GetFont();
        int textW = (int)Raylib.MeasureTextEx(font, g, fontSize, 0).X;
        int x = cellX + (Cell - textW) / 2;
        int y = cellY + (Cell - fontSize) / 2;
        if (white)
        {
            // 1 px black outline — 4-cardinal stamp only (N/S/E/W). The
            // earlier 8-direction stamp also hit the diagonals, and with
            // the bilinear-filtered font those diagonal hits compounded
            // into a fuzzy halo that read as a thick blurry edge. Skip
            // the diagonals; the 4-cardinal stamp gives a clean 1 px
            // outline that reads as crisp against the board squares.
            var outline = new Color((byte)0, (byte)0, (byte)0, alpha);
            var fill = new Color((byte)250, (byte)248, (byte)240, alpha);
            Raylib.DrawTextEx(font, g, new Vector2(x - 1, y), fontSize, 0, outline);
            Raylib.DrawTextEx(font, g, new Vector2(x + 1, y), fontSize, 0, outline);
            Raylib.DrawTextEx(font, g, new Vector2(x, y - 1), fontSize, 0, outline);
            Raylib.DrawTextEx(font, g, new Vector2(x, y + 1), fontSize, 0, outline);
            Raylib.DrawTextEx(font, g, new Vector2(x, y), fontSize, 0, fill);
        }
        else
        {
            var col = new Color((byte)20, (byte)20, (byte)20, alpha);
            Raylib.DrawTextEx(font, g, new Vector2(x, y), fontSize, 0, col);
        }
    }

    private void DrawSidePanel(Vector2 panelOffset, float bx, float by)
    {
        float sx = bx + Side * Cell + Margin;
        var sidePanel = new Rectangle(sx, by, InfoWidth, Side * Cell);
        RetroSkin.DrawSunken(sidePanel, RetroSkin.Face);

        int x = (int)sx + 8;
        int y = (int)by + 8;

        // "Solved: 5" — single line. Used to be split across two rows
        // for emphasis but the count is small (1-2 digits) and the
        // header line wasted vertical space the move history needed.
        RetroSkin.DrawText($"Solved: {_solvedCount}", x, y, RetroSkin.BodyText, 14);
        y += 22;

        // Player colour line. Used to be paired with a "White to move"
        // / "Black to move" row driven by _engine.WhiteToMove, but the
        // off-turn state lasts only the brief opponent reply animation
        // — so the second row was misleading 99% of the time. Drop it;
        // the swatch on this row already conveys the player's colour.
        var swatch = new Rectangle(x, y + 1, 14, 14);
        Raylib.DrawRectangleRec(swatch,
            _playerIsWhite ? new Color(245, 240, 225, 255) : new Color(35, 25, 20, 255));
        Raylib.DrawRectangleLines((int)swatch.X, (int)swatch.Y, (int)swatch.Width, (int)swatch.Height,
            RetroSkin.Shadow);
        RetroSkin.DrawText(_playerIsWhite ? "Play as white" : "Play as black",
            x + 20, y, RetroSkin.BodyText, 14);
        y += 24;

        // Move history
        var hist = _engine.History;
        if (hist.Count > 0)
        {
            RetroSkin.DrawText("Moves", x, y, RetroSkin.BodyText, 13); y += 15;
            int moveNum = 1, mi = 0;
            int maxLines = (Side * Cell - (y - (int)by) - 38) / 14; // leave room for footer
            int linesDrawn = 0;
            if (!hist[0].white)
            {
                RetroSkin.DrawText($"{moveNum}...{hist[0].text}", x, y, RetroSkin.DisabledText, 13);
                y += 14; mi = 1; moveNum = 2; linesDrawn++;
            }
            while (mi < hist.Count && linesDrawn < maxLines)
            {
                string line = $"{moveNum}.{hist[mi].text}";
                if (mi + 1 < hist.Count) line += $"  {hist[mi + 1].text}";
                RetroSkin.DrawText(line, x, y, RetroSkin.DisabledText, 13);
                y += 14; mi += 2; moveNum++; linesDrawn++;
            }
        }

        // Footer: rating + id. The rating row is clickable to toggle
        // between masked ("Rating: ****") and revealed; we capture the
        // panel-local rect so the Update click-handler can hit-test it.
        int fy = (int)(by + Side * Cell) - 32;
        if (_offlineMode)
        {
            RetroSkin.DrawText("Offline", x, fy, RetroSkin.DisabledText, 13);
            _ratingRowRect = default;     // not clickable in offline mode
        }
        else
        {
            string ratingText = _showRating
                ? (_rating > 0 ? $"Rating: {_rating}" : "Rating: ?")
                : "Rating: ****";
            RetroSkin.DrawText(ratingText, x, fy, RetroSkin.DisabledText, 13);
            // Stash a panel-local rect (Update works in panel-local
            // coords). Width spans the side panel so the click target
            // is generous — the actual text is short.
            _ratingRowRect = new Rectangle(
                x - panelOffset.X, fy - panelOffset.Y, InfoWidth - 16, 16);
        }
        if (_puzzleId != "")
            RetroSkin.DrawText($"#{_puzzleId}", x, fy + 16, RetroSkin.DisabledText, 13);
    }

    /// <summary>Add or remove a circle annotation on the given square.</summary>
    private void ToggleCircle((int x, int y) sq)
    {
        if (!_circles.Remove(sq)) _circles.Add(sq);
    }

    /// <summary>Add or remove an arrow annotation between two squares.</summary>
    private void ToggleArrow((int x, int y) from, (int x, int y) to)
    {
        int idx = _arrows.FindIndex(a => a.from == from && a.to == to);
        if (idx >= 0) _arrows.RemoveAt(idx);
        else _arrows.Add((from, to));
    }

    /// <summary>
    /// Render right-click annotations: rings on circled squares, arrows
    /// between marked squares, plus a live preview while the user is
    /// mid-drag. Drawn after pieces so the marks sit on top.
    /// </summary>
    private void DrawAnnotations(float bx, float by)
    {
        float cellHalf = Cell / 2f;
        foreach (var sq in _circles)
        {
            var pos = SquareForOrigin(bx, by, sq.x, sq.y);
            float cx = pos.X + cellHalf;
            float cy = pos.Y + cellHalf;
            float outer = cellHalf - 1f;
            float inner = outer - 2.5f;
            Raylib.DrawRing(new Vector2(cx, cy), inner, outer, 0, 360, 32, AnnotationCol);
        }
        foreach (var (from, to) in _arrows)
        {
            var fp = SquareForOrigin(bx, by, from.x, from.y) + new Vector2(cellHalf, cellHalf);
            var tp = SquareForOrigin(bx, by, to.x, to.y) + new Vector2(cellHalf, cellHalf);
            DrawAnnotationArrow(fp, tp);
        }
        // Live preview during right-drag is omitted — committed
        // annotations are visible the moment the user releases.
    }

    /// <summary>Lichess-style arrow with a chunky head.</summary>
    private static void DrawAnnotationArrow(Vector2 from, Vector2 to)
    {
        var dir = to - from;
        float len = dir.Length();
        if (len < 1f) return;
        var u = dir / len;
        var n = new Vector2(-u.Y, u.X);
        const float thickness = 5.5f;
        const float headLen = 11f;
        const float headW = 8.5f;
        var shaftEnd = to - u * headLen;
        Raylib.DrawLineEx(from, shaftEnd, thickness, AnnotationCol);
        var h2 = shaftEnd + n * headW;
        var h3 = shaftEnd - n * headW;
        Raylib.DrawTriangle(to, h2, h3, AnnotationCol);
        Raylib.DrawTriangle(to, h3, h2, AnnotationCol);
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
        if (_loading) return "Loading puzzle from lichess.org...";
        // Theme strip only when the user has toggled "Show Theme". Themes
        // can spoil the solution category so the default is hidden.
        if (_showThemes && _themes.Length > 0)
        {
            string themes = LichessClient.FormatThemes(_themes, max: 3);
            if (!string.IsNullOrEmpty(themes)) return $"Themes: {themes}";
        }
        if (_offlineMode && string.IsNullOrEmpty(_statusMsg))
        {
            string body = !string.IsNullOrEmpty(_title) ? _title : "Offline puzzle";
            return $"Offline: {body}";
        }
        return _statusMsg;
    }

    /// <summary>
    /// The right-hand status panel. Used to read "your move" — uninformative.
    /// Now surfaces transient state (loading / solved / wrong / answer) and
    /// otherwise shows the current board theme name so the user has live
    /// confirmation of the visual setting they're cycling through.
    /// Hint / Show Move actions override the board name so the user gets
    /// the action feedback in the right panel instead of a stale theme
    /// readout while a hint is on screen.
    /// </summary>
    private string StatusRight()
    {
        if (_solved) return "✓ correct";
        if (_failed) return "✗ wrong";
        if (_showingAnswer) return "answer";
        if (_waitingForOpponent || _animating) return "...";
        if (_loading) return "...";
        if (_statusMsg.StartsWith("Hint:") || _statusMsg.StartsWith("Move:"))
            return _statusMsg;
        return $"Board: {ChessBoardThemes.Current.Name}";
    }
}
