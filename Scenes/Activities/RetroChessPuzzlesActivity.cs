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
    private const int Margin = 10;
    private const int Side = ChessEngine.BoardSide;
    private const int InfoWidth = 160;
    // Panel size derived from the layout instead of arbitrary
    // rounding. The chess UI fits in exactly
    //   W = 2*FrameInset + 3*Margin + InfoWidth + 8*cell = 196 + 8*cell
    //   H = 2*FrameInset + TitleBar + MenuBar + 2*Margin + StatusBar
    //         + 8*cell = 88 + 8*cell
    // At cell=41 that's 524×416 — and the aspect-locked resize
    // uses 524/416 ≈ 1.260 as the canonical ratio. The previous
    // 600×420 default left a 76 px stripe of dead space to the
    // right of the side info pane; the new default uses exactly
    // the space the UI needs, no slack. Min / max kept on-ratio
    // (scale 0.75 and 2.0 from default).
    private static readonly Vector2 PanelDefault = new(524, 416);
    private static readonly Vector2 PanelMin = new(393, 312);
    private static readonly Vector2 PanelMax = new(1048, 832);
    private const int ResizeGripSize = 14;
    private const int CellMin = 20;

    // _cell pixel size is derived from _panelSize each time the
    // panel is resized — the board (Side × _cell) fits the
    // available canvas; the side info panel stays fixed at
    // InfoWidth. Initial 41 matches RecomputeCell()'s result for
    // PanelDefault so layout is sane before Load() runs.
    private int _cell = 41;
    private Vector2 _panelSize = PanelDefault;
    private bool _resizing;
    private Vector2 _resizeStartMouse;
    private Vector2 _resizeStartSize;

    public Vector2 PanelSize => _panelSize;

    /// <summary>
    /// Recompute _cell from the current _panelSize. The board fits
    /// the smaller of (available board width, available board
    /// height); slack on the other axis becomes blank space inside
    /// the panel, which the chrome handles fine. Side info panel
    /// width is fixed (InfoWidth) so it stays a stable column.
    /// </summary>
    private void RecomputeCell()
    {
        // X: 2*FrameInset + 3*Margin + InfoWidth = 196 non-board pixels.
        // Y: 2*FrameInset + TitleBar + MenuBar + 2*Margin + StatusBar
        //    = 88 non-board pixels at the default chrome sizes.
        int boardW = (int)_panelSize.X - 2 * FrameInset - 3 * Margin - InfoWidth;
        int boardH = (int)_panelSize.Y - 2 * FrameInset
                     - RetroWidgets.TitleBarHeight - RetroWidgets.MenuBarHeight
                     - 2 * Margin - RetroWidgets.StatusBarHeight;
        _cell = Math.Max(CellMin, Math.Min(boardW / Side, boardH / Side));
    }

    public bool IsFinished { get; private set; }

    // ── Menu bar (top-level + dropdowns) ────────────────────────────
    // 4 top-level entries: Game, Solver, Display, Help. Game / Solver
    // / Display open dropdowns built dynamically (so toggle items can
    // show a leading "✓ " when active and helper items can gray out
    // when not applicable to the current board state). Help is a
    // leaf — clicking it toggles the help overlay directly.
    private static readonly string[] MenuBarLabels =
        { "Game", "Solver", "Display", "Help" };
    private record MenuEntry(string Label, Action? Action,
        bool Separator = false, bool Disabled = false,
        Action<bool>? HoverPreview = null,
        // KeepOpen: Action fires but the dropdown stays open and is
        // rebuilt — used for the Categories checkboxes so the user
        // can toggle several without re-navigating.
        bool KeepOpen = false,
        // OpensSubmenu: hint that the Action replaces the dropdown
        // contents with a sub-view (currently only Game ▶ Categories).
        bool OpensSubmenu = false);
    private int _openMenu = -1;
    private List<MenuEntry> _openMenuEntries = new();
    private Rectangle _openMenuRect;
    // Theme submenu state. When a Display dropdown is open we
    // snapshot the committed theme index so that hovering theme
    // names can render the board in a non-persisted preview theme
    // and we have somewhere to revert to when the cursor leaves
    // or the menu closes without a click. -1 means no snapshot
    // (no preview is active).
    private int _themeCommittedIdx = -1;
    private int _themeHoveredIdx = -1;
    // True while the open dropdown is showing the Categories sub-view
    // (Game ▶ Categories) instead of the bar item's normal dropdown.
    // KeepOpen toggles rebuild the dropdown by checking this flag —
    // when set, BuildCategoriesEntries is the source instead of
    // BuildMenuEntries(_openMenu).
    private bool _categoriesSubmenuOpen;

    // Right-click on the title bar pops the retro theme switcher so the
    // user can reskin the OS chrome without leaving the game.
    private readonly MouseHouse.UI.ThemeMenuController _themeMenu = new();
    public bool IsThemeMenuOpen => _themeMenu.Visible;

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
    /// <summary>Destination square of the most recent wrong move attempt,
    /// in board (x, y) coords. Tinted red while <see cref="_failed"/> is set
    /// so the player sees their own square flagged — not the opponent's
    /// previous move (which is what _engine.LastMoveTo points to).</summary>
    private (int x, int y) _failedTo = (-1, -1);
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

    // ── Netplay (chess race) hooks ──────────────────────────────────────
    // Same pattern as WorldTeeClassicActivity: set via ConfigureNetplay
    // before Load(); switches the activity into a fixed-queue, time-
    // limited race mode and routes each solve/fail/finish event back
    // to the peer via the sink.
    private INetplayChessSink? _netplay;
    private int _netplayPuzzleIndex;
    private float _netplayTimeRemaining;
    private bool _netplayTimeUp;
    /// <summary>Brief auto-advance delay after a solve so the player
    /// sees the green "Solved!" before the next puzzle slams in.</summary>
    private float _netplayAdvanceDelay;
    private const float NetplayAdvanceDelaySec = 0.8f;

    public bool IsNetplay => _netplay != null;

    /// <summary>Configure this activity for a chess race BEFORE Load.
    /// After Load runs we're already mid-fetch and the state machine
    /// has committed; calling later is a no-op.</summary>
    public void ConfigureNetplay(INetplayChessSink session)
    {
        _netplay = session;
        _netplayPuzzleIndex = 0;
        _netplayTimeRemaining = session.TimeLimitSeconds;
        _netplayTimeUp = false;
        _netplayAdvanceDelay = 0;
    }

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

    // Lichess-style transition when Next loads a new puzzle: snapshot
    // the old board, then once the new state lands match same-typed
    // pieces between old and new by minimum distance and *slide* them
    // to their new squares. Pieces with no match in the new board
    // fade out, pieces newly arriving in the new board fade in.
    // Squares whose piece is identical and unmoved render normally
    // through DrawPieces — only changed squares are listed here.
    private struct PieceSlide { public int Piece; public (int x, int y) From; public (int x, int y) To; }
    private struct PieceFade  { public int Piece; public (int x, int y) Pos;  public bool FadeIn; }
    private int[,]? _prevBoardSnapshot;
    private readonly List<PieceSlide> _transitionSlides = new();
    private readonly List<PieceFade>  _transitionFades  = new();
    /// <summary>Squares whose live engine-board piece should NOT be drawn
    /// during the transition because the slide / fade list is responsible
    /// for it. Indexed [y, x].</summary>
    private bool[,]? _transitionSkip;
    private bool _transitionActive;
    private float _transitionTime;
    private const float TransitionDuration = 0.22f;

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
    /// Persists across puzzle loads.</summary>
    private bool _showThemes = true;
    private bool _showRating;     // off by default; click the rating row to toggle

    // Category filter — replaces the old Mate-Only single toggle.
    // The user picks any subset of the categories declared in
    // CategoryDefs and a fetched puzzle is accepted only if its
    // themes[] intersects ANY of the enabled categories' theme keys
    // (OR logic across selected categories — most intuitive for
    // "mix" semantics). Empty selection = no filter = every puzzle
    // qualifies. Lichess's /api/puzzle/next has no server-side
    // theme filter, so we reject+refetch client-side up to
    // FilterMaxAttempts times before accepting whatever came back
    // last (so a long unlucky streak still progresses).
    private readonly HashSet<string> _enabledCategories = new();
    private int _filterAttempts;
    private const int FilterMaxAttempts = 20;

    // Training mode caps the puzzle rating to TrainingRatingMax (~beginner
    // territory) so newcomers don't get hammered with 2000+ tactics. Uses
    // the same client-side reject-and-refetch pattern as the category
    // filter.
    private bool _trainingMode;
    private int _trainingFilterAttempts;
    private const int TrainingRatingMax = 1100;
    private const int TrainingFilterMaxAttempts = 8;

    // Category definition table. Each entry maps a stable category ID
    // (persisted to disk) to a human-readable label and the set of
    // Lichess theme keys that count as belonging to the category.
    // "Mate in 3+" intentionally groups 3/4/5 because individually
    // they're rare enough that refetching for "mateIn5 specifically"
    // would routinely hit the attempt cap. Order here is the order
    // shown in the submenu.
    private record CategoryDef(string Id, string Label, string[] ThemeKeys);
    private static readonly CategoryDef[] CategoryDefs =
    {
        new("mateIn1",          "Mate in 1",         new[] { "mateIn1" }),
        new("mateIn2",          "Mate in 2",         new[] { "mateIn2" }),
        new("mateIn3plus",      "Mate in 3+",        new[] { "mateIn3", "mateIn4", "mateIn5" }),
        new("fork",             "Fork",              new[] { "fork" }),
        new("pin",              "Pin",               new[] { "pin" }),
        new("skewer",           "Skewer",            new[] { "skewer" }),
        new("discoveredAttack", "Discovered Attack", new[] { "discoveredAttack" }),
        new("doubleCheck",      "Double Check",      new[] { "doubleCheck" }),
        new("hangingPiece",     "Hanging Piece",     new[] { "hangingPiece" }),
        new("sacrifice",        "Sacrifice",         new[] { "sacrifice" }),
        new("promotion",        "Promotion",         new[] { "promotion" }),
        new("opening",          "Opening",           new[] { "opening" }),
        new("middlegame",       "Middlegame",        new[] { "middlegame" }),
        new("endgame",          "Endgame",           new[] { "endgame" }),
        new("crushing",         "Crushing",          new[] { "crushing" }),
        new("equality",         "Equality",          new[] { "equality" }),
    };
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
        LoadWindowSize();
        LoadCategories();
        RecomputeCell();
        if (_netplay != null) LoadNextNetplayPuzzle();
        else StartFetch();
    }

    public void Close()
    {
        // Safety-net persist on close. Already saved on every
        // grip-release; this catches the corner case where the
        // user resized and then closed without releasing
        // (shouldn't happen on macOS but the cost is trivial).
        SaveWindowSize();
        // Netplay teardown — explicit disconnect if we're bailing
        // mid-race so the peer's scoreboard flips immediately
        // instead of waiting out the 30 s stale window. Then
        // persist + unregister via the same RecordAndUnregister
        // path golf uses; idempotent so repeat closes are safe.
        if (_netplay != null)
        {
            if (!_netplay.LocalFinished) _netplay.OnLocalQuit();
            _netplay.RecordAndUnregister();
            _netplay = null;
        }
    }

    /// <summary>
    /// Load the next puzzle out of the netplay session's shared queue
    /// into the engine. Each puzzle carries either a Lichess-style
    /// PGN+Solution (online host) or an offline-style FEN+ExpectedUci
    /// (offline host fallback); we render both the same way as the
    /// solo path's LoadFromLichess / LoadOffline.
    /// </summary>
    private void LoadNextNetplayPuzzle()
    {
        if (_netplay == null) return;
        if (_netplayPuzzleIndex >= _netplay.Puzzles.Count)
        {
            // Queue exhausted — emit finish and lock the UI on the
            // last solved state.
            EmitNetplayFinishIfNeeded();
            return;
        }
        var p = _netplay.Puzzles[_netplayPuzzleIndex];
        SnapshotBoardForTransition();
        ResetUiState();
        _engine.Reset();
        _loading = false;
        _loadError = "";

        if (!string.IsNullOrEmpty(p.Fen) && !string.IsNullOrEmpty(p.ExpectedUci))
        {
            // Bundled-style puzzle (single-move expected solution).
            _offlineMode = true;
            _engine.LoadFen(p.Fen!);
            _engine.RecordHistory = true;
            _engine.History.Clear();
            _solution = new[] { p.ExpectedUci! };
            _rating = p.Rating;
            _puzzleId = string.IsNullOrEmpty(p.Id) ? "race" : p.Id;
            _themes = Array.Empty<string>();
            _title = p.Title ?? "";
            _playerIsWhite = p.WhiteToMove;
            _flipped = !_playerIsWhite;
            _statusMsg = _title;
            BeginPieceTransition();
        }
        else
        {
            // Lichess-style puzzle — replay PGN to reach position.
            _offlineMode = false;
            var lp = new LichessPuzzle(p.Pgn, p.Solution, p.Rating, p.Id,
                Array.Empty<string>());
            if (!LichessClient.ApplyPuzzle(_engine, lp))
            {
                // Bad PGN in the shared queue — skip it as failed and
                // try the next so a single broken envelope entry
                // doesn't softlock the race.
                _netplay.OnLocalFailed(_netplayPuzzleIndex, _solvedCount);
                _netplayPuzzleIndex++;
                LoadNextNetplayPuzzle();
                return;
            }
            _engine.RecordHistory = true;
            _solution = lp.Solution;
            _rating = lp.Rating;
            _puzzleId = lp.Id;
            _themes = Array.Empty<string>();
            _title = "";
            _playerIsWhite = _engine.WhiteToMove;
            _flipped = !_playerIsWhite;
            _statusMsg = "";
            BeginPieceTransition();
        }
    }

    private void EmitNetplayFinishIfNeeded()
    {
        if (_netplay == null) return;
        if (_netplay.LocalFinished) return;
        _netplay.OnLocalFinish(_solvedCount);
    }

    // ── Window-size persistence ─────────────────────────────────────
    // Tiny text file matching the pattern ChessBoardTheme /
    // ChessPieceFonts already use in this folder — "WIDTHxHEIGHT".
    private static string WindowSizePath
        => Path.Combine(MouseHouse.Core.SaveManager.SaveDirectory,
            "chess_window_size.txt");

    private void LoadWindowSize()
    {
        try
        {
            if (!File.Exists(WindowSizePath)) return;
            var parts = File.ReadAllText(WindowSizePath).Trim().Split('x');
            if (parts.Length != 2) return;
            if (!int.TryParse(parts[0], out int w)) return;
            if (!int.TryParse(parts[1], out int h)) return;
            int cw = Math.Clamp(w, (int)PanelMin.X, (int)PanelMax.X);
            int ch = Math.Clamp(h, (int)PanelMin.Y, (int)PanelMax.Y);
            // Aspect-ratio snap. The resize grip locks the panel
            // to PanelDefault's 10:7, but a saved config from the
            // broken-era (840×520, anything pre-aspect-lock) can
            // load off-ratio. Anything more than 3% off the
            // canonical ratio gets pulled onto the diagonal: if
            // the saved aspect is wider than canonical the height
            // wins (width shrinks in to match); if taller the
            // width wins. After the snap the new partner
            // dimension is re-clamped in case it tripped a bound.
            float canonicalRatio = PanelDefault.X / PanelDefault.Y;
            float currentRatio = (float)cw / ch;
            if (Math.Abs(currentRatio - canonicalRatio) / canonicalRatio > 0.03f)
            {
                if (currentRatio > canonicalRatio)
                    cw = (int)MathF.Round(ch * canonicalRatio);
                else
                    ch = (int)MathF.Round(cw / canonicalRatio);
                cw = Math.Clamp(cw, (int)PanelMin.X, (int)PanelMax.X);
                ch = Math.Clamp(ch, (int)PanelMin.Y, (int)PanelMax.Y);
            }
            _panelSize = new Vector2(cw, ch);
            // Upgrade path: re-persist after clamp + snap so the
            // next launch starts at the corrected value instead of
            // silently sanitizing every boot. Catches both the
            // bounds-change case (820-floor era) and the aspect-
            // snap case (off-ratio saves from before this commit).
            if (cw != w || ch != h) SaveWindowSize();
        }
        catch { /* fall back to PanelDefault */ }
    }

    private void SaveWindowSize()
    {
        try
        {
            Directory.CreateDirectory(MouseHouse.Core.SaveManager.SaveDirectory);
            File.WriteAllText(WindowSizePath,
                $"{(int)_panelSize.X}x{(int)_panelSize.Y}");
        }
        catch { /* best-effort */ }
    }

    // ── Category filter persistence ─────────────────────────────────
    // One line, comma-separated category IDs. Unknown IDs (e.g. from
    // a future build or a renamed category) are silently dropped on
    // load so a downgrade can't poison the set. Saved whenever the
    // user toggles a category or hits Clear/Reset.
    private static string PuzzleFiltersPath
        => Path.Combine(MouseHouse.Core.SaveManager.SaveDirectory,
            "chess_puzzle_filters.txt");

    private void LoadCategories()
    {
        try
        {
            if (!File.Exists(PuzzleFiltersPath)) return;
            var raw = File.ReadAllText(PuzzleFiltersPath).Trim();
            if (raw.Length == 0) return;
            var validIds = new HashSet<string>();
            foreach (var def in CategoryDefs) validIds.Add(def.Id);
            _enabledCategories.Clear();
            foreach (var id in raw.Split(','))
            {
                var s = id.Trim();
                if (validIds.Contains(s)) _enabledCategories.Add(s);
            }
        }
        catch { /* fall back to empty selection */ }
    }

    private void SaveCategories()
    {
        try
        {
            Directory.CreateDirectory(MouseHouse.Core.SaveManager.SaveDirectory);
            // Persist in CategoryDefs order so the file is stable
            // (round-trips identical instead of depending on
            // HashSet iteration order).
            var ordered = new List<string>();
            foreach (var def in CategoryDefs)
                if (_enabledCategories.Contains(def.Id))
                    ordered.Add(def.Id);
            File.WriteAllText(PuzzleFiltersPath, string.Join(",", ordered));
        }
        catch { /* best-effort */ }
    }

    // ── Resize grip ─────────────────────────────────────────────────
    private Rectangle ResizeGripLocal()
        => new(_panelSize.X - ResizeGripSize - FrameInset,
               _panelSize.Y - ResizeGripSize - FrameInset,
               ResizeGripSize, ResizeGripSize);

    /// <summary>Bottom-right grip drag → resize the panel. Aspect-
    /// ratio-locked to PanelDefault's 10:7 — the canvas is a square
    /// board + fixed-width info column, so off-axis stretch produces
    /// ugly shapes. The cursor moves freely; only the panel snaps
    /// to the diagonal. Dominant-axis-wins: whichever of
    /// (Δx/DefaultW, Δy/DefaultH) has the bigger absolute value
    /// signs the scale factor, then it's applied uniformly to both
    /// dimensions. Pure-horizontal and pure-vertical drags both
    /// resize (one axis just contributes nothing, the other drives
    /// it solo). Must run FIRST in Update so a mid-drag wins over
    /// title-bar / menu / piece input.</summary>
    private bool HandleResizeGrip(Vector2 local, bool leftPressed, bool leftReleased)
    {
        var grip = ResizeGripLocal();
        if (!_resizing && leftPressed && RetroSkin.PointInRect(local, grip))
        {
            _resizing = true;
            _resizeStartMouse = local;
            _resizeStartSize = _panelSize;
            return true;
        }
        if (_resizing)
        {
            var delta = local - _resizeStartMouse;
            float scaleX = delta.X / PanelDefault.X;
            float scaleY = delta.Y / PanelDefault.Y;
            // Dominant axis wins (abs-larger, signed). Using signed
            // max() instead would zero-out pure-vertical shrinks
            // (scaleX=0, scaleY<0 → max=0 → no resize) which the
            // user would experience as a dead grip.
            float scale = Math.Abs(scaleX) > Math.Abs(scaleY) ? scaleX : scaleY;
            float w = _resizeStartSize.X + scale * PanelDefault.X;
            float h = _resizeStartSize.Y + scale * PanelDefault.Y;
            // Per-axis clamp, then re-derive the partner dimension
            // through the locked ratio so we never leave the 10:7
            // diagonal even at the min/max corners.
            float ratioWH = PanelDefault.X / PanelDefault.Y;
            if (w < PanelMin.X) { w = PanelMin.X; h = w / ratioWH; }
            if (w > PanelMax.X) { w = PanelMax.X; h = w / ratioWH; }
            if (h < PanelMin.Y) { h = PanelMin.Y; w = h * ratioWH; }
            if (h > PanelMax.Y) { h = PanelMax.Y; w = h * ratioWH; }
            _panelSize = new Vector2((int)w, (int)h);
            RecomputeCell();
            if (leftReleased)
            {
                _resizing = false;
                SaveWindowSize();
            }
            return true;
        }
        return false;
    }

    private void StartFetch()
    {
        // In netplay we don't fetch — the shared queue is already
        // loaded. The "Next" menu in netplay path falls through to
        // the puzzle-advance helper instead.
        if (_netplay != null)
        {
            // Treat an explicit Next-click as "skip / give up" so the
            // peer sees the puzzle advance event.
            _netplay.OnLocalFailed(_netplayPuzzleIndex, _solvedCount);
            _netplayPuzzleIndex++;
            LoadNextNetplayPuzzle();
            return;
        }
        // Snapshot the current board before we wipe it — the transition
        // animation diffs this against the new puzzle's starting state.
        SnapshotBoardForTransition();
        ResetUiState();
        _engine.Reset();
        _loading = true;
        _offlineMode = false;
        _loadError = "";
        _filterAttempts = 0;
        _trainingFilterAttempts = 0;
        _statusMsg = _trainingMode ? "Loading training puzzle..."
                     : _enabledCategories.Count > 0 ? "Loading filtered puzzle..."
                     : "Loading puzzle...";
        _fetchTask = LichessClient.FetchNextAsync();
    }

    /// <summary>Returns true if the puzzle's themes[] intersects ANY
    /// enabled category. Empty selection ⇒ everything passes (no
    /// filter). Each category expands to one or more Lichess theme
    /// keys via <see cref="CategoryDefs"/> — e.g. "Mate in 3+" matches
    /// mateIn3 / mateIn4 / mateIn5.</summary>
    private bool MatchesCategories(string[]? themes)
    {
        if (_enabledCategories.Count == 0) return true;
        if (themes == null || themes.Length == 0) return false;
        foreach (var def in CategoryDefs)
        {
            if (!_enabledCategories.Contains(def.Id)) continue;
            foreach (var key in def.ThemeKeys)
                foreach (var t in themes)
                    if (t == key) return true;
        }
        return false;
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
        _failedTo = (-1, -1);
        _showingAnswer = false;
        _movesMade = 0;
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

    /// <summary>Build the slide / fade lists for a Next-load. For each
    /// piece type-and-colour, pair its old positions to its new ones by
    /// minimum board distance — paired entries become slides; unmatched
    /// olds become fade-outs; unmatched news become fade-ins. Squares
    /// that didn't change at all aren't enumerated; DrawPieces just
    /// renders them straight from the engine board.</summary>
    private void BeginPieceTransition()
    {
        _transitionSlides.Clear();
        _transitionFades.Clear();
        _transitionActive = false;
        _transitionTime = 0f;
        if (_prevBoardSnapshot == null) return;

        int n = ChessEngine.BoardSide;
        _transitionSkip ??= new bool[n, n];
        Array.Clear(_transitionSkip, 0, _transitionSkip.Length);

        // Bucket positions by signed piece value so each (colour, kind)
        // matches against itself (white pawns to white pawns, etc).
        var oldBuckets = new Dictionary<int, List<(int x, int y)>>();
        var newBuckets = new Dictionary<int, List<(int x, int y)>>();
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                int op = _prevBoardSnapshot[r, c];
                int np = _engine.Board[r, c];
                if (op != 0)
                {
                    if (!oldBuckets.TryGetValue(op, out var l)) { l = new(); oldBuckets[op] = l; }
                    l.Add((c, r));
                }
                if (np != 0)
                {
                    if (!newBuckets.TryGetValue(np, out var l)) { l = new(); newBuckets[np] = l; }
                    l.Add((c, r));
                }
            }

        foreach (var kv in oldBuckets)
        {
            int piece = kv.Key;
            var oldList = kv.Value;
            if (!newBuckets.TryGetValue(piece, out var newList))
            {
                // No same-typed pieces in the new board — every old one
                // fades out where it was.
                foreach (var p in oldList)
                {
                    _transitionFades.Add(new PieceFade { Piece = piece, Pos = p, FadeIn = false });
                    _transitionSkip[p.y, p.x] = true;
                }
                continue;
            }

            // Greedy nearest-pair match. Picks the shortest old→new edge
            // each iteration so pieces that didn't move actually pair to
            // themselves (zero distance) and read as "stayed put" instead
            // of getting matched across the board.
            while (oldList.Count > 0 && newList.Count > 0)
            {
                float bestD = float.MaxValue;
                int bestI = 0, bestJ = 0;
                for (int i = 0; i < oldList.Count; i++)
                    for (int j = 0; j < newList.Count; j++)
                    {
                        var o = oldList[i]; var nn = newList[j];
                        float dx = o.x - nn.x; float dy = o.y - nn.y;
                        float d = dx * dx + dy * dy;
                        if (d < bestD) { bestD = d; bestI = i; bestJ = j; }
                    }
                var op = oldList[bestI];
                var npP = newList[bestJ];
                oldList.RemoveAt(bestI);
                newList.RemoveAt(bestJ);

                if (op == npP) continue;     // same square, no animation
                _transitionSlides.Add(new PieceSlide { Piece = piece, From = op, To = npP });
                _transitionSkip[op.y, op.x] = true;
                _transitionSkip[npP.y, npP.x] = true;
            }

            // Any new positions left unmatched fade in (extra promoted
            // queens, etc.).
            foreach (var p in newList)
            {
                _transitionFades.Add(new PieceFade { Piece = piece, Pos = p, FadeIn = true });
                _transitionSkip[p.y, p.x] = true;
            }
        }

        // Pieces only in the new board (no old bucket at all) — pure
        // fade-ins.
        foreach (var kv in newBuckets)
        {
            if (oldBuckets.ContainsKey(kv.Key)) continue;
            foreach (var p in kv.Value)
            {
                _transitionFades.Add(new PieceFade { Piece = kv.Key, Pos = p, FadeIn = true });
                _transitionSkip[p.y, p.x] = true;
            }
        }

        _transitionActive = _transitionSlides.Count > 0 || _transitionFades.Count > 0;
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

        // Theme menu modally consumes input while open — run it before
        // anything else so a click anywhere dismisses it cleanly.
        if (_themeMenu.Visible)
        {
            _themeMenu.Update(mousePos, leftPressed, rightPressed);
            return;
        }

        // Solo mode: Enter or Space advances to the next puzzle once
        // the current one is resolved (either solved correctly or
        // the user clicked Show Answer and the playback finished).
        // Netplay handles its own auto-advance. Guard rail: the
        // check uses IsResolved which is false during play, so a
        // stray spacebar mid-puzzle won't skip.
        if (IsResolved && _netplay == null
            && (Raylib.IsKeyPressed(KeyboardKey.Enter)
                || Raylib.IsKeyPressed(KeyboardKey.KpEnter)
                || Raylib.IsKeyPressed(KeyboardKey.Space)))
        {
            StartFetch();
            return;
        }

        // Right-click on the title bar opens the retro theme switcher,
        // anchored just below the title bar so the menu drops into
        // the canvas. Right-clicks on the board are still consumed
        // by the lichess-style annotation handler further down.
        var titleBarRight = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (rightPressed && RetroSkin.PointInRect(local, titleBarRight))
        {
            var screenPos = new Vector2(
                panelOffset.X + FrameInset,
                panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight);
            _themeMenu.Show(screenPos);
            return;
        }

        // Resize grip first — when the user is mid-drag we want it
        // to win over any other interaction, including pieces /
        // menu / title bar. The grip lives in the bottom-right
        // outside of any board / panel hit zone, so this is a
        // strict precedence rule, not an overlap negotiation.
        if (HandleResizeGrip(local, leftPressed, leftReleased)) return;

        // Netplay race housekeeping. Ticks down the timer; once it
        // hits zero we emit the finish event (which freezes our
        // score on the peer's board) but stay in the activity so
        // the player can still see the final state. Also handles
        // the brief auto-advance after a solve.
        if (_netplay != null)
        {
            if (!_netplay.LocalFinished && !_netplayTimeUp)
            {
                _netplayTimeRemaining -= delta;
                if (_netplayTimeRemaining <= 0)
                {
                    _netplayTimeRemaining = 0;
                    _netplayTimeUp = true;
                    _netplay.OnLocalFinish(_solvedCount);
                }
            }
            if (_netplayAdvanceDelay > 0)
            {
                _netplayAdvanceDelay -= delta;
                if (_netplayAdvanceDelay <= 0)
                {
                    _netplayAdvanceDelay = 0;
                    _netplayPuzzleIndex++;
                    LoadNextNetplayPuzzle();
                }
            }
        }

        // Title bar close
        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        { IsFinished = true; return; }

        // Menu bar (top-level + dropdowns). HandleMenuBarInput
        // returns true when the click was absorbed (either opened
        // a dropdown, fired an entry, or was swallowed because a
        // dropdown is open) — in any of those cases the board
        // beneath shouldn't see the click this frame.
        if (HandleMenuBarInput(local, leftPressed)) return;

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

        // Next Puzzle button (status bar right slot when the current
        // puzzle is resolved). Lives here so it wins over the rating-
        // row click below, even though those rects don't overlap —
        // intent-ordering: a solo player who's done with the puzzle
        // is more likely to be reaching for "next" than fiddling
        // with mask state. Netplay-mode hides the button (the race
        // auto-advances).
        if (leftPressed && IsResolved && _netplay == null
            && RetroSkin.PointInRect(local, NextPuzzleButtonLocal()))
        {
            StartFetch();
            return;
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

        // Click on the status-bar left slot toggles the theme tag
        // strip — the strip itself acts as the affordance to hide it,
        // and clicking the (now-empty) slot brings it back. Only
        // fires when the puzzle has themes; an empty themes array
        // means nothing to toggle and the click falls through to
        // other handlers.
        if (leftPressed && _themes.Length > 0
            && RetroSkin.PointInRect(local, StatusLeftSlotLocal()))
        {
            _showThemes = !_showThemes;
            return;
        }

        // Poll fetch task
        if (_loading && _fetchTask != null && _fetchTask.IsCompleted)
        {
            var result = _fetchTask.Result;
            _fetchTask = null;
            // Category filter: reject puzzles that don't match the
            // user's selected categories and refetch — capped at
            // FilterMaxAttempts so a long unlucky streak still
            // progresses instead of hanging in "filtering" forever.
            // No selection ⇒ MatchesCategories returns true and we
            // skip this branch entirely (the cheap empty-set path).
            if (_enabledCategories.Count > 0 && result.Ok && result.Puzzle != null
                && !MatchesCategories(result.Puzzle.Themes)
                && _filterAttempts < FilterMaxAttempts)
            {
                _filterAttempts++;
                _statusMsg = $"Filtering puzzles ({_filterAttempts}/{FilterMaxAttempts})...";
                _fetchTask = LichessClient.FetchNextAsync();
                return;
            }
            // Training mode rejects puzzles harder than TrainingRatingMax.
            // Easy puzzles are common at the low end of the curve so retries
            // converge fast in practice.
            if (_trainingMode && result.Ok && result.Puzzle != null
                && result.Puzzle.Rating > TrainingRatingMax
                && _trainingFilterAttempts < TrainingFilterMaxAttempts)
            {
                _trainingFilterAttempts++;
                _statusMsg = $"Looking for an easy puzzle ({_trainingFilterAttempts}/{TrainingFilterMaxAttempts})...";
                _fetchTask = LichessClient.FetchNextAsync();
                return;
            }
            _loading = false;
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
            if (_failTimer >= 1.5f) { _failed = false; _failTimer = 0; _failedTo = (-1, -1); }
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
                         local.X < bx + Side * _cell && local.Y < by + Side * _cell;

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
            // Debug log so we can prove the click is reaching the board
            // hit-test path. Comment out once the bug's nailed.
            Console.WriteLine($"[chess] press @ local=({local.X:F0},{local.Y:F0}) sq=({sq.x},{sq.y}) sel=({_sel.x},{_sel.y}) leftRel={leftReleased} dragging={_dragging}");
            // Click on the already-selected piece toggles the legal-move
            // dots back off — same way the original chess UI handled it.
            // Has to come BEFORE the own-piece branch below, otherwise
            // re-clicking just re-selects the same square.
            if (sq != (-1, -1) && sq == _sel)
            {
                _sel = (-1, -1);
                _legalDest.Clear();
                _dragging = false;
                _dragFrom = (-1, -1);
            }
            else if (sq != (-1, -1) && _engine.Board[sq.y, sq.x] != 0 &&
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
            Console.WriteLine($"[chess] release @ local=({local.X:F0},{local.Y:F0}) drop=({drop.x},{drop.y}) dragFrom=({_dragFrom.x},{_dragFrom.y}) sel=({_sel.x},{_sel.y}) legalDestCount={_legalDest.Count}");
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

    private List<MenuEntry> BuildMenuEntries(int barIdx)
    {
        bool solverDisabled =
            _solved || (_showingAnswer && _movesMade >= _solution.Length);
        switch (barIdx)
        {
            case 0: // Game
                int catCount = _enabledCategories.Count;
                string catLabel = catCount == 0
                    ? "Categories: any ▸"
                    : $"Categories: {catCount} selected ▸";
                return new List<MenuEntry>
                {
                    new("New Puzzle", () => StartFetch()),
                    new("", null, Separator: true),
                    new(catLabel, () => OpenCategoriesSubmenu(), OpensSubmenu: true),
                    new((_trainingMode ? "✓ " : "    ") + "Training",
                        () => ToggleTraining()),
                };
            case 1: // Solver
                return new List<MenuEntry>
                {
                    new("Hint",        () => ShowHint(),     Disabled: solverDisabled),
                    new("Show Move",   () => ShowMoveHint(), Disabled: solverDisabled),
                    new("Show Answer", () => ShowAnswer(),   Disabled: solverDisabled),
                };
            case 2: // Display
                return BuildDisplayMenuEntries();
            default:
                return new List<MenuEntry>();
        }
    }

    /// <summary>The Categories submenu — a flat list of category
    /// checkboxes plus three quick-action rows at the top. Rebuilt
    /// from CategoryDefs each open + after every sticky toggle so
    /// the checkbox glyphs stay in sync with _enabledCategories.
    /// Each toggle row carries KeepOpen=true so the user can flip
    /// several categories without re-navigating the menu.</summary>
    private List<MenuEntry> BuildCategoriesEntries()
    {
        var list = new List<MenuEntry>
        {
            new("Clear all", () =>
            {
                _enabledCategories.Clear();
                SaveCategories();
            }, KeepOpen: true),
            new("Reset to mate puzzles", () =>
            {
                _enabledCategories.Clear();
                _enabledCategories.Add("mateIn1");
                _enabledCategories.Add("mateIn2");
                _enabledCategories.Add("mateIn3plus");
                SaveCategories();
            }, KeepOpen: true),
            new("", null, Separator: true),
        };
        foreach (var def in CategoryDefs)
        {
            string id = def.Id; // capture for closure
            bool on = _enabledCategories.Contains(id);
            // Bracket-style checkbox instead of ☑/☐ — the retro font
            // path has spotty Unicode coverage (FormatThemes ships a
            // similar ASCII workaround) and "[x] Fork" reads as a
            // checkbox glyph anywhere.
            list.Add(new MenuEntry(
                Label: (on ? "[x] " : "[ ] ") + def.Label,
                Action: () =>
                {
                    if (!_enabledCategories.Remove(id))
                        _enabledCategories.Add(id);
                    SaveCategories();
                },
                KeepOpen: true));
        }
        return list;
    }

    /// <summary>Swap the open dropdown's contents to the Categories
    /// submenu without closing — keeps the dropdown rect anchored
    /// where the Game ▶ dropdown opened so it reads as a nested
    /// view, not a teleported new menu. Sets _categoriesSubmenuOpen
    /// so KeepOpen click rebuilds rebuild the right entry list.</summary>
    private void OpenCategoriesSubmenu()
    {
        _categoriesSubmenuOpen = true;
        _openMenuEntries = BuildCategoriesEntries();
        ResizeCurrentDropdownRect();
    }

    private List<MenuEntry> BuildDisplayMenuEntries()
    {
        var list = new List<MenuEntry>
        {
            new("Flip Board", () => _flipped = !_flipped),
            new("", null, Separator: true),
        };
        // Theme picker — each named theme is its own row, with the
        // current selection prefixed by ✓ and a HoverPreview callback
        // so the board live-renders the theme as the cursor passes
        // over it. The Action commits; HoverPreview only mutates the
        // in-memory selection (no Save()) so cursor wobble doesn't
        // touch disk.
        for (int i = 0; i < ChessBoardThemes.All.Length; i++)
        {
            int idx = i; // capture for closures
            bool selected = idx == ChessBoardThemes.CurrentIdx;
            list.Add(new MenuEntry(
                Label: (selected ? "✓ " : "    ") + ChessBoardThemes.All[idx].Name,
                Action: () => ChessBoardThemes.Commit(idx),
                HoverPreview: hovered =>
                {
                    if (hovered) ChessBoardThemes.SetPreview(idx);
                    else if (_themeCommittedIdx >= 0)
                        ChessBoardThemes.SetPreview(_themeCommittedIdx);
                }));
        }
        list.Add(new MenuEntry("", null, Separator: true));
        list.Add(new MenuEntry(
            (_showThemes ? "✓ " : "    ") + "Theme Tags",
            () => _showThemes = !_showThemes));
        list.Add(new MenuEntry(
            (_showRating ? "✓ " : "    ") + "Rating",
            () => _showRating = !_showRating));
        return list;
    }

    private void ToggleTraining()
    {
        _trainingMode = !_trainingMode;
        _statusMsg = _trainingMode
            ? $"Training: next fetch caps rating at {TrainingRatingMax}."
            : "Training off: next fetch returns any puzzle.";
    }

    // ── Menu bar geometry + dropdown state ──────────────────────────
    // Mirrors PaintActivity's dropdown system: top-level slot widths
    // are MeasureText(label) + 12, dropdown rect sized to fit the
    // longest entry + 36 padding, anchored under the clicked slot.
    private const int MenuItemHPad = 12;
    private const int DropdownRowH = 18;

    private Rectangle MenuBarRectLocal()
        => new(FrameInset, FrameInset + RetroWidgets.TitleBarHeight,
               PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);

    private static int MenuBarHitIndex(Rectangle bar, Vector2 local)
    {
        int x = (int)bar.X + 4;
        for (int i = 0; i < MenuBarLabels.Length; i++)
        {
            int w = RetroSkin.MeasureText(MenuBarLabels[i]) + MenuItemHPad;
            var slot = new Rectangle(x, bar.Y + 2, w, bar.Height - 4);
            if (RetroSkin.PointInRect(local, slot)) return i;
            x += w;
        }
        return -1;
    }

    /// <summary>Open dropdown <paramref name="idx"/> (or switch to it
    /// from another open one). Help is a leaf — it toggles the help
    /// overlay directly instead of opening a dropdown.</summary>
    private void OpenMenu(int idx)
    {
        if (MenuBarLabels[idx] == "Help")
        {
            CloseMenu();
            _help.Visible = !_help.Visible;
            return;
        }
        // Switching between dropdowns (hover-driven or click-driven)
        // bypasses CloseMenu, so revert any pre-existing theme
        // preview from the prior session before snapshotting for
        // this one. Display → Game → Display would otherwise leak
        // the previewed theme as the new "committed" baseline.
        if (_themeCommittedIdx >= 0)
        {
            ChessBoardThemes.SetPreview(_themeCommittedIdx);
            _themeCommittedIdx = -1;
        }
        _themeHoveredIdx = -1;
        if (MenuBarLabels[idx] == "Display")
            _themeCommittedIdx = ChessBoardThemes.CurrentIdx;
        _categoriesSubmenuOpen = false;
        _openMenu = idx;
        _openMenuEntries = BuildMenuEntries(idx);
        ResizeCurrentDropdownRect();
    }

    /// <summary>Re-anchor + re-size _openMenuRect under whatever
    /// bar item is currently "owning" the dropdown, sized to fit
    /// the longest entry in _openMenuEntries. Called from OpenMenu
    /// (initial open), OpenCategoriesSubmenu (sub-view swap), and
    /// the KeepOpen toggle path (entry text length changes when
    /// checkbox glyphs flip).</summary>
    private void ResizeCurrentDropdownRect()
    {
        if (_openMenu < 0) return;
        var bar = MenuBarRectLocal();
        int x = (int)bar.X + 4;
        for (int i = 0; i < _openMenu; i++)
            x += RetroSkin.MeasureText(MenuBarLabels[i]) + MenuItemHPad;
        int w = 0;
        foreach (var e in _openMenuEntries)
        {
            int eW = RetroSkin.MeasureText(e.Label) + 36;
            if (eW > w) w = eW;
        }
        w = Math.Max(w, 140);
        int h = _openMenuEntries.Count * DropdownRowH + 4;
        _openMenuRect = new Rectangle(x, bar.Y + bar.Height, w, h);
    }

    private void CloseMenu()
    {
        // If a theme preview was active, revert to the committed
        // index — the user dismissed the menu without clicking,
        // so cursor wobble shouldn't change the theme. If they
        // DID click, the action ran first and updated
        // _themeCommittedIdx, making this SetPreview a no-op.
        if (_themeCommittedIdx >= 0)
        {
            ChessBoardThemes.SetPreview(_themeCommittedIdx);
            _themeCommittedIdx = -1;
        }
        _themeHoveredIdx = -1;
        _categoriesSubmenuOpen = false;
        _openMenu = -1;
        _openMenuEntries.Clear();
    }

    private int DropdownHitIndex(Vector2 local)
    {
        if (!RetroSkin.PointInRect(local, _openMenuRect)) return -1;
        int rel = (int)(local.Y - _openMenuRect.Y - 2);
        return rel / DropdownRowH;
    }

    /// <summary>Single entry point for menu input. Returns true when
    /// the click was absorbed by the menu system (and the caller
    /// should skip further routing). Mirrors PaintActivity's
    /// HandleMenuBarInput.</summary>
    private bool HandleMenuBarInput(Vector2 local, bool leftPressed)
    {
        var bar = MenuBarRectLocal();
        if (leftPressed && RetroSkin.PointInRect(local, bar))
        {
            int idx = MenuBarHitIndex(bar, local);
            if (idx >= 0)
            {
                if (_openMenu == idx) CloseMenu();
                else OpenMenu(idx);
                return true;
            }
        }
        // Hovering across the bar with one open: switch dropdowns.
        if (_openMenu >= 0 && RetroSkin.PointInRect(local, bar))
        {
            int idx = MenuBarHitIndex(bar, local);
            if (idx >= 0 && idx != _openMenu && MenuBarLabels[idx] != "Help")
                OpenMenu(idx);
        }
        if (_openMenu >= 0)
        {
            // Track hover transitions and fire HoverPreview on
            // enter / exit so the theme submenu can live-preview
            // each row as the cursor passes over it. Separator
            // and disabled rows count as "no hover" so cursor
            // resting on them reverts the preview.
            int hovered = DropdownHitIndex(local);
            if (hovered < 0 || hovered >= _openMenuEntries.Count
                || _openMenuEntries[hovered].Separator
                || _openMenuEntries[hovered].Disabled)
                hovered = -1;
            if (hovered != _themeHoveredIdx)
            {
                if (_themeHoveredIdx >= 0 && _themeHoveredIdx < _openMenuEntries.Count)
                    _openMenuEntries[_themeHoveredIdx].HoverPreview?.Invoke(false);
                _themeHoveredIdx = hovered;
                if (hovered >= 0)
                    _openMenuEntries[hovered].HoverPreview?.Invoke(true);
            }

            if (leftPressed)
            {
                if (hovered >= 0)
                {
                    var e = _openMenuEntries[hovered];
                    var act = e.Action;
                    if (e.KeepOpen)
                    {
                        // Sticky toggle (Categories checkboxes /
                        // quick-actions). Fire action, then rebuild
                        // the current sub-view so checkbox glyphs
                        // update without closing the menu. Hover
                        // index stays valid — same row is still
                        // under the cursor.
                        act?.Invoke();
                        _openMenuEntries = _categoriesSubmenuOpen
                            ? BuildCategoriesEntries()
                            : BuildMenuEntries(_openMenu);
                        ResizeCurrentDropdownRect();
                        return true;
                    }
                    if (e.OpensSubmenu)
                    {
                        // Replace dropdown contents with a sub-view
                        // (currently only Game ▶ Categories).
                        // Action does the swap; nothing else to do.
                        act?.Invoke();
                        return true;
                    }
                    // Update the snapshot BEFORE CloseMenu's revert
                    // runs, so a theme-row click stays committed
                    // instead of bouncing back to the pre-open
                    // value. For non-theme rows _themeCommittedIdx
                    // is -1 (only set on Display open) so this is
                    // a no-op anywhere else.
                    if (_themeCommittedIdx >= 0)
                        _themeCommittedIdx = ChessBoardThemes.CurrentIdx;
                    CloseMenu();
                    act?.Invoke();
                    return true;
                }
                else if (!RetroSkin.PointInRect(local, _openMenuRect)
                      && !RetroSkin.PointInRect(local, bar))
                {
                    CloseMenu();
                }
                return true; // swallow clicks while any menu is open
            }
            // Mouse hover inside the dropdown rect is also swallowed so
            // the underlying board doesn't paint highlights through it.
            return RetroSkin.PointInRect(local, _openMenuRect);
        }
        return false;
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
        int gx = (int)((local.X - bx) / _cell);
        int gy = (int)((local.Y - by) / _cell);
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
        if (_flipped) return new Vector2(bx + (Side - 1 - x) * _cell, by + (Side - 1 - y) * _cell);
        return new Vector2(bx + x * _cell, by + y * _cell);
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
            _failedTo = to;
            _statusMsg = "Wrong move - try again.";
        }
    }

    private void MarkSolved()
    {
        _solved = true;
        _solvedCount++;
        if (_netplay != null)
        {
            _netplay.OnLocalSolved(_netplayPuzzleIndex, _solvedCount);
            _netplayAdvanceDelay = NetplayAdvanceDelaySec;
            _statusMsg = _engine.IsCheckmate(_engine.WhiteToMove)
                ? "Checkmate!" : "Solved!";
        }
        else
        {
            _statusMsg = _engine.IsCheckmate(_engine.WhiteToMove)
                ? "Checkmate! Press Enter for next." : "Solved! Press Enter for next.";
        }
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
        // Look up the piece sitting on the hint square and name it
        // in lowercase — "Hint: move the knight on e5" reads as a
        // natural sentence; "Hint: move from e5" required the user
        // to glance at the board to see what piece was being asked
        // about. Falls back to "piece" if the square is somehow
        // empty (shouldn't happen for a valid puzzle, but worth
        // not crashing on).
        int piece = _engine.Board[srcRC.r, srcRC.c];
        string name = PieceName(Math.Abs(piece));
        _statusMsg = $"Hint: move the {name} on " + _solution[_movesMade][..2];
    }

    private static string PieceName(int absP) => absP switch
    {
        1 => "pawn",
        2 => "knight",
        3 => "bishop",
        4 => "rook",
        5 => "queen",
        6 => "king",
        _ => "piece",
    };

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

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, MenuBarLabels, _openMenu);

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
                float dx = panelOffset.X + _dragPos.X - _cell / 2f;
                float dy = panelOffset.Y + _dragPos.Y - _cell / 2f;
                DrawPieceGlyph(p, (int)dx, (int)dy);
            }
        }

        DrawSidePanel(panelOffset, bx, by);
        DrawStatusBar(panelOffset);
        DrawResizeGrip(panelOffset);
        if (_netplay != null) DrawNetplayScoreboard(panelOffset);
        _help.Draw(panelOffset, PanelSize);
        // Menu dropdowns sit above board + help overlay so they're
        // never clipped. Theme menu still sits above the dropdown
        // so a right-click theme switcher always wins.
        if (_openMenu >= 0) DrawDropdown(panelOffset);
        _themeMenu.Draw();
    }

    /// <summary>Three diagonal hatch lines in the bottom-right
    /// corner — classic Win9x sizing handle, copied from
    /// PaintActivity.DrawResizeGrip so the affordance reads the
    /// same across resizable retro windows.</summary>
    private void DrawResizeGrip(Vector2 panelOffset)
    {
        var grip = ResizeGripLocal();
        int gx = (int)(grip.X + panelOffset.X);
        int gy = (int)(grip.Y + panelOffset.Y);
        for (int d = 2; d < ResizeGripSize; d += 4)
        {
            for (int t = 0; t < 2; t++)
            {
                Raylib.DrawLine(gx + ResizeGripSize - d - t, gy + ResizeGripSize - 2,
                                gx + ResizeGripSize - 2,    gy + ResizeGripSize - d - t,
                                t == 0 ? RetroSkin.DarkShadow : RetroSkin.Highlight);
            }
        }
    }

    private void DrawDropdown(Vector2 panelOffset)
    {
        var r = new Rectangle(_openMenuRect.X + panelOffset.X,
            _openMenuRect.Y + panelOffset.Y,
            _openMenuRect.Width, _openMenuRect.Height);
        RetroSkin.DrawRaised(r);
        Raylib.DrawRectangleLines((int)r.X, (int)r.Y,
            (int)r.Width, (int)r.Height, RetroSkin.DarkShadow);

        var mouse = Raylib.GetMousePosition();
        int hovered = -1;
        if (mouse.X >= r.X && mouse.X < r.X + r.Width
         && mouse.Y >= r.Y && mouse.Y < r.Y + r.Height)
            hovered = (int)(mouse.Y - r.Y - 2) / DropdownRowH;

        for (int i = 0; i < _openMenuEntries.Count; i++)
        {
            var e = _openMenuEntries[i];
            int y = (int)r.Y + 2 + i * DropdownRowH;
            if (e.Separator)
            {
                Raylib.DrawLine((int)r.X + 4, y + DropdownRowH / 2,
                                (int)(r.X + r.Width - 4), y + DropdownRowH / 2,
                                RetroSkin.Shadow);
                continue;
            }
            if (i == hovered && !e.Disabled)
            {
                Raylib.DrawRectangle((int)r.X + 2, y,
                    (int)r.Width - 4, DropdownRowH, RetroSkin.TitleActive);
                RetroSkin.DrawText(e.Label, (int)r.X + 8, y + 2,
                    RetroSkin.TitleText);
            }
            else
            {
                var color = e.Disabled ? RetroSkin.DisabledText : RetroSkin.BodyText;
                RetroSkin.DrawText(e.Label, (int)r.X + 8, y + 2, color);
            }
        }
    }

    private void DrawNetplayScoreboard(Vector2 panelOffset)
    {
        var s = _netplay!;
        const int boxW = 220;
        const int boxH = 84;
        // Anchor to the top-right of the panel, just under the menu
        // bar. Mirrors WorldTeeClassicActivity's scoreboard position.
        int bx = (int)(panelOffset.X + PanelSize.X - boxW - 6 - FrameInset);
        int by = (int)(panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight
                       + RetroWidgets.MenuBarHeight + 6);
        Raylib.DrawRectangle(bx, by, boxW, boxH,
            new Color((byte)0, (byte)0, (byte)0, (byte)170));
        Raylib.DrawRectangleLines(bx, by, boxW, boxH,
            new Color((byte)244, (byte)200, (byte)80, (byte)200));

        string header = $"RACE · {s.StartingBand}";
        RetroSkin.DrawText(header, bx + 6, by + 4,
            new Color((byte)244, (byte)200, (byte)80, (byte)255),
            RetroSkin.BodyFontSize - 1);

        // Countdown — turns red in the last 10 seconds.
        int timeRemaining = (int)MathF.Ceiling(_netplayTimeRemaining);
        if (timeRemaining < 0) timeRemaining = 0;
        var timeCol = timeRemaining <= 10
            ? new Color((byte)240, (byte)80, (byte)80, (byte)255)
            : new Color((byte)244, (byte)200, (byte)80, (byte)255);
        int mm = timeRemaining / 60;
        int ss = timeRemaining % 60;
        RetroSkin.DrawText($"{mm}:{ss:D2}",
            bx + boxW - 48, by + 4, timeCol, RetroSkin.BodyFontSize - 1);

        var col = new Color((byte)232, (byte)232, (byte)248, (byte)255);
        string youLine = s.LocalFinished
            ? $"You: done · {s.LocalSolved} solved"
            : $"You: P{_netplayPuzzleIndex + 1} ({_rating}) · {s.LocalSolved} solved";
        string peerLine = s.PeerDisconnected
            ? $"{s.PeerName}: left"
            : s.PeerFinished
                ? $"{s.PeerName}: done · {s.PeerSolved} solved"
                : s.IsPeerStale
                    ? $"{s.PeerName}: …no signal"
                    : $"{s.PeerName}: P{s.PeerPuzzleIndex + 1} · {s.PeerSolved} solved";
        RetroSkin.DrawText(youLine, bx + 6, by + 26, col, RetroSkin.BodyFontSize - 2);
        RetroSkin.DrawText(peerLine, bx + 6, by + 44, col, RetroSkin.BodyFontSize - 2);

        if ((s.LocalFinished && s.PeerFinished)
            || (s.LocalFinished && s.PeerDisconnected)
            || (_netplayTimeUp && s.PeerFinished))
        {
            string winner;
            if (s.PeerDisconnected) winner = "Peer left — solo finish.";
            else if (s.LocalSolved == s.PeerSolved
                     && s.LocalLastSolveMs == s.PeerLastSolveMs) winner = "It's a tie.";
            else winner = s.LocalWon()
                ? "🏆 You won!"
                : $"🏆 {s.PeerName} won!";
            RetroSkin.DrawText(winner, bx + 6, by + 64,
                new Color((byte)80, (byte)240, (byte)80, (byte)255),
                RetroSkin.BodyFontSize - 2);
        }
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
                Raylib.DrawRectangle((int)(bx + dx * _cell), (int)(by + dy * _cell),
                    _cell, _cell, (x + y) % 2 == 0 ? theme.Light : theme.Dark);
            }

        // Frame around the board
        Raylib.DrawRectangleLines((int)bx - 1, (int)by - 1, Side * _cell + 2, Side * _cell + 2, RetroSkin.DarkShadow);
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
            Raylib.DrawCircle((int)(pos.X + _cell / 2), (int)(pos.Y + _cell / 2), 7, theme.LegalDot);
        }

        // Drag hover
        if (_dragging && _dragHover != (-1, -1) && _legalDest.Contains(_dragHover))
            DrawSquareTint(bx, by, _dragHover.x, _dragHover.y, theme.SelectedTint);

        // Failed flash — tint the square the player just tried to move to.
        if (_failed && _failedTo != (-1, -1))
            DrawSquareTint(bx, by, _failedTo.x, _failedTo.y,
                new Color((byte)230, (byte)80, (byte)50, (byte)90));
    }

    private void DrawSquareTint(float bx, float by, int x, int y, Color c)
    {
        var p = SquareForOrigin(bx, by, x, y);
        Raylib.DrawRectangle((int)p.X, (int)p.Y, _cell, _cell, c);
    }

    private Vector2 SquareForOrigin(float bx, float by, int x, int y)
    {
        if (_flipped) return new Vector2(bx + (Side - 1 - x) * _cell, by + (Side - 1 - y) * _cell);
        return new Vector2(bx + x * _cell, by + y * _cell);
    }

    private void DrawCoordinates(float bx, float by)
    {
        const string files = "abcdefgh";
        const string ranks = "87654321";
        var col = ChessBoardThemes.Current.CoordLabel;
        // Jacquard12 needs a slightly larger nominal size than W95F to read
        // at the same visual weight in a 32 px cell — 14 lands cleanly
        // at the default 36 px cell. Scale with the current cell size so
        // labels grow with the board when the window is resized.
        int size = Math.Max(10, 14 * _cell / 36);
        for (int i = 0; i < Side; i++)
        {
            int fi = _flipped ? Side - 1 - i : i;
            BoardLabelFont.DrawText(files[fi].ToString(),
                (int)(bx + i * _cell + _cell - 10), (int)(by + Side * _cell - 14),
                size, col);

            int ri = _flipped ? Side - 1 - i : i;
            BoardLabelFont.DrawText(ranks[ri].ToString(),
                (int)(bx + 2), (int)(by + i * _cell - 1),
                size, col);
        }
    }

    private void DrawPieces(float bx, float by, Vector2 panelOffset)
    {
        bool transition = _transitionActive && _transitionSkip != null;

        // Pass 1: stationary pieces (engine board, minus squares the
        // transition is animating).
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                if (_dragging && _dragFrom == (x, y)) continue;
                if (transition && _transitionSkip![y, x]) continue;
                int p = _engine.Board[y, x];
                if (p == 0) continue;
                var pos = SquareForOrigin(bx, by, x, y);
                DrawPieceGlyph(p, (int)pos.X, (int)pos.Y);
            }

        // Pass 2: transition layer — slides interpolated linearly with a
        // mild ease-out, fades alpha-only. Drawn on top of the stationary
        // pass so a sliding piece glides over any square it crosses.
        if (transition)
        {
            float t = Math.Clamp(_transitionTime / TransitionDuration, 0f, 1f);
            float te = 1f - (1f - t) * (1f - t);     // ease-out quad
            byte fadeInAlpha  = (byte)Math.Clamp((int)(t * 255), 0, 255);
            byte fadeOutAlpha = (byte)Math.Clamp((int)((1f - t) * 255), 0, 255);

            foreach (var s in _transitionSlides)
            {
                var fromP = SquareForOrigin(bx, by, s.From.x, s.From.y);
                var toP   = SquareForOrigin(bx, by, s.To.x,   s.To.y);
                var pos   = Vector2.Lerp(fromP, toP, te);
                DrawPieceGlyph(s.Piece, (int)pos.X, (int)pos.Y);
            }
            foreach (var f in _transitionFades)
            {
                byte a = f.FadeIn ? fadeInAlpha : fadeOutAlpha;
                if (a == 0) continue;
                var pos = SquareForOrigin(bx, by, f.Pos.x, f.Pos.y);
                DrawPieceGlyph(f.Piece, (int)pos.X, (int)pos.Y, a);
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
    /// Render a single piece as its Unicode chess glyph, centred in a
    /// _cell-sized square. Draws directly with the user-picked piece font (see
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
        // Values below are tuned for the legacy 36 px cell; they get
        // scaled by (_cell / 36f) below so pieces grow with the
        // cell when the window is resized.
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
        // Scale the legacy-tuned font size up/down with the current
        // cell so pieces stay proportional when the user resizes
        // the window. Multiplied THEN integer-divided so the math
        // stays in ints (Raylib MeasureTextEx wants an int size).
        fontSize = fontSize * _cell / 36;
        // Light colours bloom against the gray board (the font atlas's
        // soft glyph edges sample to non-zero alpha pixels that read as
        // fringe), so a same-fontSize white piece looked visibly bigger
        // than the matching black piece. Shave 2 px off white to bring
        // the apparent silhouette in line.
        if (white) fontSize -= 2;
        ChessPieceFonts.PollExternalChange();
        var font = ChessPieceFonts.GetFont();
        int textW = (int)Raylib.MeasureTextEx(font, g, fontSize, 0).X;
        int x = cellX + (_cell - textW) / 2;
        int y = cellY + (_cell - fontSize) / 2;
        // No outline-stamp pass — both colours just render the solid
        // silhouette glyph in their own fill colour. The Point font
        // filter (set in ChessPieceFonts.Load) gives crisp edges so
        // each piece reads as a hard pixel-art shape against the board.
        Color col = white
            ? new Color((byte)250, (byte)238, (byte)200, alpha)   // cream / ivory
            : new Color((byte) 20, (byte) 20, (byte) 20, alpha);
        Raylib.DrawTextEx(font, g, new Vector2(x, y), fontSize, 0, col);
    }

    private void DrawSidePanel(Vector2 panelOffset, float bx, float by)
    {
        float sx = bx + Side * _cell + Margin;
        var sidePanel = new Rectangle(sx, by, InfoWidth, Side * _cell);
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
            RetroSkin.DrawText("Moves", x, y, RetroSkin.BodyText, 14); y += 16;
            int moveNum = 1, mi = 0;
            int maxLines = (Side * _cell - (y - (int)by) - 38) / 16; // leave room for footer
            int linesDrawn = 0;
            if (!hist[0].white)
            {
                RetroSkin.DrawText($"{moveNum}...{hist[0].text}", x, y, RetroSkin.BodyText, 14);
                y += 16; mi = 1; moveNum = 2; linesDrawn++;
            }
            while (mi < hist.Count && linesDrawn < maxLines)
            {
                string line = $"{moveNum}.{hist[mi].text}";
                if (mi + 1 < hist.Count) line += $"  {hist[mi + 1].text}";
                RetroSkin.DrawText(line, x, y, RetroSkin.BodyText, 14);
                y += 16; mi += 2; moveNum++; linesDrawn++;
            }
        }

        // Footer: rating + id. The rating row is clickable to toggle
        // between masked ("Rating: ****") and revealed; we capture the
        // panel-local rect so the Update click-handler can hit-test it.
        int fy = (int)(by + Side * _cell) - 34;
        if (_offlineMode)
        {
            RetroSkin.DrawText("Offline", x, fy, RetroSkin.BodyText, 14);
            _ratingRowRect = default;     // not clickable in offline mode
        }
        else
        {
            string ratingText = _showRating
                ? (_rating > 0 ? $"Rating: {_rating}" : "Rating: ?")
                : "Rating: ****";
            RetroSkin.DrawText(ratingText, x, fy, RetroSkin.BodyText, 14);
            // Stash a panel-local rect (Update works in panel-local
            // coords). Width spans the side panel so the click target
            // is generous — the actual text is short.
            _ratingRowRect = new Rectangle(
                x - panelOffset.X, fy - panelOffset.Y, InfoWidth - 16, 16);
        }
        if (_puzzleId != "")
            RetroSkin.DrawText($"#{_puzzleId}", x, fy + 17, RetroSkin.BodyText, 14);
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
        float cellHalf = _cell / 2f;
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

    // ── Resolved + Next Puzzle ──────────────────────────────────────
    // A puzzle is "resolved" once the player has either solved it
    // correctly OR clicked Show Answer and the playback has reached
    // the end of the solution. While resolved we surface a Next
    // Puzzle button in the status bar's right slot + accept
    // Space / Enter as a keyboard shortcut.
    private bool IsResolved => _solved
        || (_showingAnswer && _movesMade >= _solution.Length);

    /// <summary>Panel-local rect for the Next Puzzle button —
    /// mirrors the right status-bar slot from DrawStatusBar. Used
    /// by both Update (hit test) and Draw (button render) so the
    /// click target and the visual button are pixel-aligned.</summary>
    private Rectangle NextPuzzleButtonLocal()
    {
        float bar_x = FrameInset;
        float bar_y = PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight;
        float bar_w = PanelSize.X - 2 * FrameInset;
        float rightX = FrameInset + 2 * Margin + Side * _cell - 35;
        return new Rectangle(rightX, bar_y + 2,
            bar_x + bar_w - rightX - 2, RetroWidgets.StatusBarHeight - 4);
    }

    /// <summary>Panel-local rect for the LEFT status-bar slot —
    /// the theme tag strip's display area. Click-to-toggle-themes
    /// uses this; same geometry as DrawStatusBar's leftSlot so the
    /// hit target matches the painted slot.</summary>
    private Rectangle StatusLeftSlotLocal()
    {
        float bar_y = PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight;
        float rightX = FrameInset + 2 * Margin + Side * _cell - 35;
        return new Rectangle(FrameInset + 2, bar_y + 2,
            rightX - FrameInset - 4, RetroWidgets.StatusBarHeight - 4);
    }

    private void DrawStatusBar(Vector2 panelOffset)
    {
        var bar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);

        Raylib.DrawRectangleRec(bar, RetroSkin.Face);

        // Custom split. Originally aligned the right slot's left
        // edge with the side info panel above; widened by 70 px
        // left to fit "Hint: move the knight on e5". With the
        // "Themes:" prefix gone the right slot doesn't need to be
        // that wide, so dial the shift back to 35 px — right slot
        // sits at ~215 px (still fits the longest hint), left
        // pane gets ~35 px back for theme-tag chains.
        float rightX = panelOffset.X + FrameInset + 2 * Margin + Side * _cell - 35;
        int fontSize = RetroWidgets.StatusFontSize;

        var leftSlot = new Rectangle(bar.X + 2, bar.Y + 2,
                                     rightX - bar.X - 4, bar.Height - 4);
        RetroSkin.DrawSunken(leftSlot, RetroSkin.Face);
        DrawStatusText(StatusLeft(), leftSlot, fontSize);

        var rightSlot = new Rectangle(rightX, bar.Y + 2,
                                      bar.X + bar.Width - rightX - 2, bar.Height - 4);
        if (IsResolved && _netplay == null)
        {
            DrawNextPuzzleButton(rightSlot);
        }
        else
        {
            RetroSkin.DrawSunken(rightSlot, RetroSkin.Face);
            DrawStatusText(StatusRight(), rightSlot, fontSize);
        }
    }

    /// <summary>Raised button replacing the sunken status text in
    /// the right slot when a puzzle is resolved. Uses DrawPressed
    /// while the user is holding the click for tactile feedback.
    /// The arrow is a literal "→" glyph in the same font as the
    /// rest of the UI — keeps the retro reading at a glance.</summary>
    private void DrawNextPuzzleButton(Rectangle slot)
    {
        var mouse = Raylib.GetMousePosition();
        bool hover = mouse.X >= slot.X && mouse.X < slot.X + slot.Width
                  && mouse.Y >= slot.Y && mouse.Y < slot.Y + slot.Height;
        bool pressed = hover && Raylib.IsMouseButtonDown(MouseButton.Left);

        if (pressed) RetroSkin.DrawPressed(slot);
        else         RetroSkin.DrawRaised(slot);

        const string label = "Next Puzzle →";
        int fontSize = RetroWidgets.StatusFontSize;
        int tw = RetroSkin.MeasureText(label, fontSize);
        int tx = (int)(slot.X + (slot.Width - tw) / 2) + (pressed ? 1 : 0);
        int ty = (int)(slot.Y + (slot.Height - fontSize) / 2) + (pressed ? 1 : 0);
        RetroSkin.DrawText(label, tx, ty, RetroSkin.BodyText, fontSize);
    }

    private static void DrawStatusText(string text, Rectangle slot, int fontSize)
    {
        int textArea = (int)slot.Width - 8;
        string truncated = RetroWidgets.TruncateToWidth(text, textArea, fontSize);
        int ty = (int)(slot.Y + (slot.Height - fontSize) / 2);
        RetroSkin.DrawText(truncated, (int)slot.X + 4, ty, RetroSkin.BodyText, fontSize);
    }

    private string StatusLeft()
    {
        if (_loading) return "Loading puzzle from lichess.org...";
        // Theme strip only when the user has toggled "Show Theme". Themes
        // can spoil the solution category so the default is hidden.
        if (_showThemes && _themes.Length > 0)
        {
            string themes = LichessClient.FormatThemes(_themes, max: 3);
            // Tags read fine on their own as a list of words — the
            // "Themes:" prefix mostly just consumed left-pane width
            // and pushed long tag chains into truncation.
            if (!string.IsNullOrEmpty(themes)) return themes;
        }
        if (_offlineMode && string.IsNullOrEmpty(_statusMsg))
        {
            string body = !string.IsNullOrEmpty(_title) ? _title : "Offline puzzle";
            return $"Offline: {body}";
        }
        return _statusMsg;
    }

    /// <summary>
    /// The right-hand status panel. Surfaces transient state (loading /
    /// solved / wrong / answer / hint) and otherwise shows the puzzle's
    /// rating + ID — the most useful at-a-glance puzzle metadata. The
    /// theme name used to live here but it's settable from the menu
    /// (and you can see the theme on the board), so the slot is better
    /// spent on info the user can't otherwise get.
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
        // Format: "Rating · ID" with a middle-dot separator. If rating
        // is unknown (offline puzzle) just show the ID; if both are
        // missing show the offline marker.
        bool hasRating = _rating > 0 && !_offlineMode;
        bool hasId = !string.IsNullOrEmpty(_puzzleId);
        if (hasRating && hasId) return $"{_rating} · {_puzzleId}";
        if (hasRating)           return $"Rating: {_rating}";
        if (hasId)               return $"#{_puzzleId}";
        return _offlineMode ? "Offline" : "";
    }
}
