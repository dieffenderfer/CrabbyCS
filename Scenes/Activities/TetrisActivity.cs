using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Falling-block puzzle. Standard 10×20 well, seven tetromino shapes drawn
/// from a 7-bag randomizer, simple rotation (no SRS kicks — the 1990 Windows
/// version predated SRS), level-based gravity, line-clear scoring.
///
/// Controls: Left / Right / Down arrows, Up rotates, Space hard-drops, P pauses.
/// </summary>
public class TetrisActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Cols = 10;
    private const int Rows = 20;
    private const int Cell = 16;
    private const int SidePanelW = 96;
    private const int Margin = 12;

    /// <summary>
    /// Panel width grows in netplay mode to fit the opponent's
    /// mini-board (Cols × 6 px per cell + frame + a gap). The pet
    /// re-queries PanelSize each frame so the activity can resize
    /// itself once ConfigureNetplay has run; solo play keeps the
    /// original tight footprint.
    /// </summary>
    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + Cols * Cell + Margin + SidePanelW
            + (_netplay != null ? Cols * 6 + 16 : 0),
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + 2 * Margin + Rows * Cell + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private readonly RetroHelp _help = new()
    {
        Title = "Tetris — How to play",
        Lines = new[]
        {
            "Stack falling shapes to fill complete rows.",
            "Filled rows clear and award points.",
            "Left / Right move the piece sideways.",
            "Up rotates, Down drops faster, Space hard-drops.",
            "P pauses. Speed ramps up every 10 lines cleared.",
        },
        DiagramHeight = 60,
        Diagram = r =>
        {
            // Show the seven tetrominoes as little 4x4 swatches.
            const int dCell = 8;
            int totalW = 7 * (4 * dCell) + 6 * 8;
            int x0 = (int)(r.X + (r.Width - totalW) / 2);
            int y0 = (int)(r.Y + 4);
            for (int t = 0; t < Shapes.Length; t++)
            {
                var s = Shapes[t];
                int baseX = x0 + t * (4 * dCell + 8);
                for (int i = 0; i < 4; i++)
                {
                    int rx = s[i * 2];
                    int ry = s[i * 2 + 1];
                    Raylib.DrawRectangle(baseX + rx * dCell, y0 + ry * dCell,
                        dCell - 1, dCell - 1, Colors[t]);
                }
            }
        },
    };

    private static readonly Color[] Colors =
    {
        new(  0, 192, 192, 255), // I cyan
        new(224, 224,   0, 255), // O yellow
        new(176,   0, 192, 255), // T magenta
        new(  0, 192,   0, 255), // S green
        new(192,   0,   0, 255), // Z red
        new(224, 128,   0, 255), // L orange
        new(  0,   0, 192, 255), // J blue
    };

    private int[,] _well = new int[Cols, Rows]; // 0 empty, 1..7 piece+1, 8 garbage
    private int _curType, _curRot, _curX, _curY;
    private int _nextType;
    private int[] _bag = Array.Empty<int>();
    private int _bagIdx;

    private float _gravityTimer;
    private int _level = 1;
    private int _score;
    private int _lines;
    private bool _gameOver;
    private bool _paused;
    private float _flashTimer;
    private List<int> _flashRows = new();

    // ── RNG ─────────────────────────────────────────────────────────
    // Two separate generators so the 7-bag piece sequence (shared
    // across both clients in netplay) doesn't interfere with the
    // garbage-hole sequence (per-side; the receiver picks its own
    // hole index when applying incoming attack). Solo play uses a
    // fresh time-seeded Random for both; netplay overrides via
    // ConfigureNetplay so the bag sequence is byte-identical
    // across both clients.
    private Random _bagRng = new();
    private Random _garbageRng = new();
    /// <summary>Cached for the title-bar HUD and the Reset that
    /// fires when the user clicks "New" — sticky across resets so a
    /// netplay seed survives the "New" menu click.</summary>
    private int _netplaySeed;
    private bool _seedFromNetplay;

    // ── T-spin / B2B / combo state ─────────────────────────────────
    /// <summary>True when the move that brought the current piece
    /// to its locked position was a rotation. T-spin detection
    /// requires a "spin into place" — so we clear this on Left /
    /// Right / Down / hard-drop, set it on Up.</summary>
    private bool _lastMoveWasRotation;
    /// <summary>0 = no T-spin, 1 = mini / regular T-spin. We treat
    /// all 3-corner T-spins as full (mini detection is finicky and
    /// the garbage-table reward is the same either way at this
    /// fidelity).</summary>
    private int _tspinLevel;
    /// <summary>"Back-to-back" applies when consecutive "hard" line
    /// clears chain — a hard clear is a Tetris (4 lines) or any
    /// T-spin (≥1 line). Singles / doubles / triples reset it.</summary>
    private bool _lastClearWasHard;
    /// <summary>How many consecutive piece-locks have produced
    /// at least one line clear. Bonus garbage is (combo - 1) so
    /// the first clear in a chain awards 0 combo bonus.</summary>
    private int _comboCount;

    // ── Garbage queue (netplay) ────────────────────────────────────
    /// <summary>Rows of incoming attack the activity hasn't applied
    /// to the well yet. Drained on each piece lock — first the
    /// queue absorbs any outgoing attack we generate ourselves
    /// (cancel pass in NetplayTetrisSession), then whatever's
    /// still pending rises from the bottom of our board.</summary>
    private int _pendingGarbageDisplay;

    // ── Netplay session ─────────────────────────────────────────────
    private INetplayTetrisSink? _netplay;
    public bool IsNetplay => _netplay != null;

    public void ConfigureNetplay(INetplayTetrisSink session)
    {
        _netplay = session;
        _netplaySeed = session.Seed;
        _seedFromNetplay = true;
        _level = session.StartingLevel;
    }

    public void Load() => Reset();

    private void Reset()
    {
        for (int x = 0; x < Cols; x++) for (int y = 0; y < Rows; y++) _well[x, y] = 0;
        // Re-seed the bag generator. In netplay, the seed is fixed
        // for the whole match (challenger picks it, both sides
        // share it via the accept envelope). In solo play, a fresh
        // time-based seed each Reset so consecutive plays differ.
        // _garbageRng uses a derived seed so a rematch doesn't
        // recycle hole columns from the previous round.
        int bagSeed = _seedFromNetplay ? _netplaySeed : new Random().Next();
        _bagRng = new Random(bagSeed);
        _garbageRng = new Random(unchecked(bagSeed * 1664525 + 1013904223));
        _bag = NewBag(); _bagIdx = 0;
        _nextType = NextFromBag();
        SpawnNext();
        _gravityTimer = 0;
        _score = 0; _lines = 0;
        // Preserve _level from ConfigureNetplay's starting-level;
        // solo Reset falls back to 1.
        if (!_seedFromNetplay) _level = 1;
        _gameOver = false; _paused = false;
        _flashTimer = 0; _flashRows.Clear();
        _lastMoveWasRotation = false;
        _tspinLevel = 0;
        _lastClearWasHard = false;
        _comboCount = 0;
        _pendingGarbageDisplay = 0;
    }

    private int[] NewBag()
    {
        var b = new[] { 0, 1, 2, 3, 4, 5, 6 };
        for (int i = b.Length - 1; i > 0; i--)
        {
            int j = _bagRng.Next(i + 1);
            (b[i], b[j]) = (b[j], b[i]);
        }
        return b;
    }

    private int NextFromBag()
    {
        if (_bagIdx >= _bag.Length) { _bag = NewBag(); _bagIdx = 0; }
        return _bag[_bagIdx++];
    }

    private void SpawnNext()
    {
        _curType = _nextType;
        _nextType = NextFromBag();
        _curRot = 0;
        _curX = Cols / 2 - 2;
        _curY = -1;
        if (Collides(_curType, _curRot, _curX, _curY + 1)) _gameOver = true;
    }

    private bool Collides(int type, int rot, int px, int py)
    {
        var s = Shapes[type];
        for (int i = 0; i < 4; i++)
        {
            int rx = s[rot * 8 + i * 2 + 0] + px;
            int ry = s[rot * 8 + i * 2 + 1] + py;
            if (rx < 0 || rx >= Cols || ry >= Rows) return true;
            if (ry < 0) continue;
            if (_well[rx, ry] != 0) return true;
        }
        return false;
    }

    private void Lock()
    {
        var s = Shapes[_curType];
        for (int i = 0; i < 4; i++)
        {
            int rx = s[_curRot * 8 + i * 2 + 0] + _curX;
            int ry = s[_curRot * 8 + i * 2 + 1] + _curY;
            if (ry >= 0 && ry < Rows && rx >= 0 && rx < Cols) _well[rx, ry] = _curType + 1;
        }
        // T-spin detection (3-corner rule). Only valid when the
        // current piece is a T, the last move was a rotation, and
        // ≥3 of the four diagonal corners of the T's 3×3 bounding
        // box are blocked (occupied or out of bounds). We treat
        // mini vs full as the same — distinguishing them needs
        // SRS kick-table awareness this rotation system doesn't
        // have, and the 2/4/6 garbage-row table only branches on
        // the line-clear count anyway.
        _tspinLevel = 0;
        if (_curType == 2 && _lastMoveWasRotation)
        {
            int filled = CountFilledCorners(_curX, _curY);
            if (filled >= 3) _tspinLevel = 1;
        }

        ClearLines();
        ApplyPendingGarbage();
        SpawnNext();
    }

    /// <summary>Count how many of the four diagonal corners of a
    /// T-piece's 3×3 bounding box are occupied — either by a stack
    /// block or off-grid. Center is the T's connecting cell at
    /// (px+1, py+1).</summary>
    private int CountFilledCorners(int px, int py)
    {
        int filled = 0;
        ReadOnlySpan<(int dx, int dy)> corners = stackalloc (int, int)[]
        {
            (0, 0), (2, 0), (0, 2), (2, 2),
        };
        foreach (var (dx, dy) in corners)
        {
            int x = px + dx, y = py + dy;
            if (x < 0 || x >= Cols || y >= Rows) { filled++; continue; }
            if (y < 0) continue;            // above the well counts as empty
            if (_well[x, y] != 0) filled++;
        }
        return filled;
    }

    private void ClearLines()
    {
        _flashRows.Clear();
        for (int y = Rows - 1; y >= 0; y--)
        {
            bool full = true;
            for (int x = 0; x < Cols; x++) if (_well[x, y] == 0) { full = false; break; }
            if (full) _flashRows.Add(y);
        }
        if (_flashRows.Count == 0)
        {
            // A piece that locks without clearing breaks the combo
            // chain. B2B persists across non-clear locks — that's
            // the standard rule.
            _comboCount = 0;
            return;
        }

        int n = _flashRows.Count;
        _score += n switch { 1 => 100, 2 => 300, 3 => 500, _ => 800 } * _level;
        _lines += n;
        _level = 1 + _lines / 10;

        // Compact: write rows that aren't cleared into a fresh stack from bottom up.
        var temp = new int[Cols, Rows];
        int writeY = Rows - 1;
        for (int y = Rows - 1; y >= 0; y--)
        {
            if (_flashRows.Contains(y)) continue;
            for (int x = 0; x < Cols; x++) temp[x, writeY] = _well[x, y];
            writeY--;
        }
        _well = temp;
        _flashTimer = 0.15f;

        // ── Netplay attack ──
        // Compute the clear kind for the wire envelope; combo and
        // back-to-back bonuses ride along as extra GarbageSent on
        // top of the base-table attack so NetplayTetrisSession's
        // table stays flat.
        bool perfectClear = IsWellEmpty();
        string clearKind = ClassifyClear(n, _tspinLevel);
        bool thisClearWasHard = clearKind == "tetris"
            || clearKind == "tspin1" || clearKind == "tspin2" || clearKind == "tspin3";

        int baseAttack = BaseAttackFor(clearKind);
        int b2bBonus = _lastClearWasHard && thisClearWasHard ? 1 : 0;
        int comboBonus = _comboCount;     // 0 on the first clear of a chain
        _comboCount++;
        _lastClearWasHard = thisClearWasHard;

        if (_netplay != null)
        {
            // OnLocalLinesCleared computes attack from the base
            // table; we sum-up the bonuses here and ship the whole
            // thing as an extra Sub="lines_cleared" with the per-
            // kind base + sender-side bonus already folded in.
            int totalAttack = baseAttack + b2bBonus + comboBonus + (perfectClear ? 10 : 0);
            int sent = Math.Max(0, totalAttack - _netplay.LocalPendingGarbage);
            // Note: the session's OnLocalLinesCleared also runs a
            // cancel pass against LocalPendingGarbage; passing the
            // computed kind keeps the session's table aligned with
            // ours. We deliberately pass perfectClear here too so
            // the session adds the +10 bonus uniformly.
            _netplay.OnLocalLinesCleared(n, clearKind, perfectClear);
            // Visualisation: keep _pendingGarbageDisplay in step
            // with the post-cancel local pending count for the
            // sidebar indicator.
            _pendingGarbageDisplay = _netplay.LocalPendingGarbage;
            _ = sent;   // keeping the local computation for future telemetry
        }
    }

    private bool IsWellEmpty()
    {
        for (int x = 0; x < Cols; x++)
            for (int y = 0; y < Rows; y++)
                if (_well[x, y] != 0) return false;
        return true;
    }

    private static string ClassifyClear(int rows, int tspin)
    {
        if (tspin > 0)
        {
            return rows switch
            {
                1 => "tspin1",
                2 => "tspin2",
                3 => "tspin3",
                _ => "tspin_zero",
            };
        }
        return rows switch
        {
            1 => "single",
            2 => "double",
            3 => "triple",
            4 => "tetris",
            _ => "single",
        };
    }

    private static int BaseAttackFor(string kind)
        => kind switch
        {
            "double" => 1, "triple" => 2, "tetris" => 4,
            "tspin1" => 2, "tspin2" => 4, "tspin3" => 6,
            _ => 0,
        };

    /// <summary>Drain whatever rows of attack the session has
    /// queued for us and rise them from the bottom of the well.
    /// Each garbage row has exactly one hole, in a column chosen
    /// by our per-side _garbageRng. Called after ClearLines (so
    /// the cancel pass has already happened in the session).</summary>
    private void ApplyPendingGarbage()
    {
        if (_netplay == null) return;
        int rowsToAdd = _netplay.ConsumePendingGarbage();
        if (rowsToAdd <= 0)
        {
            _pendingGarbageDisplay = _netplay.LocalPendingGarbage;
            return;
        }
        for (int r = 0; r < rowsToAdd; r++)
        {
            int hole = _garbageRng.Next(Cols);
            // Shift every row up by one.
            for (int y = 0; y < Rows - 1; y++)
                for (int x = 0; x < Cols; x++)
                    _well[x, y] = _well[x, y + 1];
            // Bottom row = garbage (cell value 8) with one hole.
            for (int x = 0; x < Cols; x++)
                _well[x, Rows - 1] = x == hole ? 0 : 8;
        }
        _pendingGarbageDisplay = _netplay.LocalPendingGarbage;
    }

    private float GravityInterval()
    {
        // Roughly NES Tetris frame counts → seconds
        float[] table = { 0.80f, 0.72f, 0.63f, 0.55f, 0.47f, 0.38f, 0.30f, 0.22f, 0.13f, 0.10f, 0.08f };
        int i = Math.Min(_level - 1, table.Length - 1);
        return Math.Max(0.05f, table[i]);
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
        int m = RetroWidgets.MenuBarHitTest(menuBar, new[] { "New", _paused ? "Resume" : "Pause", "Help" }, local, leftPressed);
        if (m == 0) { Reset(); return; }
        if (m == 1) { _paused = !_paused; return; }
        if (m == 2) { _help.Visible = !_help.Visible; return; }
        if (_help.HandleInput(local, leftPressed, PanelSize)) return;

        if (_gameOver || _paused) return;

        if (_flashTimer > 0)
        {
            _flashTimer -= delta;
            return;
        }

        if (Raylib.IsKeyPressed(KeyboardKey.Left) && !Collides(_curType, _curRot, _curX - 1, _curY))
        { _curX--; _lastMoveWasRotation = false; }
        if (Raylib.IsKeyPressed(KeyboardKey.Right) && !Collides(_curType, _curRot, _curX + 1, _curY))
        { _curX++; _lastMoveWasRotation = false; }
        if (Raylib.IsKeyPressed(KeyboardKey.Up))
        {
            int nr = (_curRot + 1) % (Shapes[_curType].Length / 8);
            if (!Collides(_curType, nr, _curX, _curY)) { _curRot = nr; _lastMoveWasRotation = true; }
        }
        if (Raylib.IsKeyPressed(KeyboardKey.P)) _paused = true;
        if (Raylib.IsKeyPressed(KeyboardKey.Space))
        {
            while (!Collides(_curType, _curRot, _curX, _curY + 1)) { _curY++; _score += 2; }
            Lock();
            _lastMoveWasRotation = false;
            BroadcastPostLock();
            if (_gameOver) NotifyTopOut();
            return;
        }

        float interval = Raylib.IsKeyDown(KeyboardKey.Down) ? Math.Min(0.05f, GravityInterval()) : GravityInterval();
        _gravityTimer += delta;
        if (_gravityTimer >= interval)
        {
            _gravityTimer = 0;
            if (Collides(_curType, _curRot, _curX, _curY + 1))
            {
                Lock();
                _lastMoveWasRotation = false;
                BroadcastPostLock();
                if (_gameOver) NotifyTopOut();
            }
            else { _curY++; _lastMoveWasRotation = false; }
        }
    }

    /// <summary>Push the post-lock board snapshot to the peer. Also
    /// the natural rate-limit point — pieces lock 1-3× per second
    /// at competitive play, well below MQTT throughput limits and
    /// well above what the peer's mini-board needs to feel live.</summary>
    private void BroadcastPostLock()
    {
        if (_netplay == null) return;
        var snap = SnapshotWell();
        _netplay.PushBoardSnapshot(snap, _score, _lines, _level);
    }

    private byte[] SnapshotWell()
    {
        // Row-major top-down, 200 bytes. Cell values mirror _well:
        // 0 = empty, 1..7 = tetromino + 1, 8 = garbage.
        var snap = new byte[Cols * Rows];
        int idx = 0;
        for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Cols; x++)
                snap[idx++] = (byte)_well[x, y];
        return snap;
    }

    private void NotifyTopOut()
    {
        _netplay?.OnLocalTopOut();
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Tetris", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", _paused ? "Resume" : "Pause", "Help" }, -1);

        // Well
        float wx = panelOffset.X + FrameInset + Margin;
        float wy = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
        var wellRect = new Rectangle(wx - 2, wy - 2, Cols * Cell + 4, Rows * Cell + 4);
        RetroSkin.DrawSunken(wellRect, new Color(0, 0, 0, 255));

        for (int x = 0; x < Cols; x++)
            for (int y = 0; y < Rows; y++)
                if (_well[x, y] != 0)
                    DrawBlock((int)wx + x * Cell, (int)wy + y * Cell, Colors[_well[x, y] - 1]);

        if (!_gameOver && _flashTimer <= 0)
        {
            var s = Shapes[_curType];
            for (int i = 0; i < 4; i++)
            {
                int rx = s[_curRot * 8 + i * 2 + 0] + _curX;
                int ry = s[_curRot * 8 + i * 2 + 1] + _curY;
                if (ry < 0) continue;
                DrawBlock((int)wx + rx * Cell, (int)wy + ry * Cell, Colors[_curType]);
            }
        }

        // Side panel: NEXT, score, level, lines
        float sx = wx + Cols * Cell + Margin;
        var sideRect = new Rectangle(sx, wy, SidePanelW, Rows * Cell);
        RetroSkin.DrawSunken(sideRect, RetroSkin.Face);

        RetroSkin.DrawText("NEXT", (int)sx + 8, (int)wy + 8, RetroSkin.BodyText);
        // Center the next-piece preview in a 4×4 box
        var sNext = Shapes[_nextType];
        for (int i = 0; i < 4; i++)
        {
            int rx = sNext[i * 2 + 0];
            int ry = sNext[i * 2 + 1];
            DrawBlock((int)sx + 16 + rx * Cell, (int)wy + 28 + ry * Cell, Colors[_nextType]);
        }

        int infoY = (int)wy + 28 + 5 * Cell;
        RetroSkin.DrawText($"SCORE", (int)sx + 8, infoY, RetroSkin.BodyText);
        RetroSkin.DrawText($"{_score}", (int)sx + 8, infoY + 16, RetroSkin.BodyText);
        RetroSkin.DrawText($"LINES", (int)sx + 8, infoY + 40, RetroSkin.BodyText);
        RetroSkin.DrawText($"{_lines}", (int)sx + 8, infoY + 56, RetroSkin.BodyText);
        RetroSkin.DrawText($"LEVEL", (int)sx + 8, infoY + 80, RetroSkin.BodyText);
        RetroSkin.DrawText($"{_level}", (int)sx + 8, infoY + 96, RetroSkin.BodyText);

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string state = _gameOver ? "Game over" : _paused ? "Paused" : "← → ↓ move  ↑ rotate  Space drop  P pause";
        RetroWidgets.StatusBar(status, state, $"L{_level}");

        if (_netplay != null) DrawNetplayHud(panelOffset, wx, wy);

        _help.Draw(panelOffset, PanelSize);
    }

    private void DrawNetplayHud(Vector2 panelOffset, float wx, float wy)
    {
        var s = _netplay!;

        // ── Garbage indicator: red column on the LEFT side of our
        //    well, one square per pending row. Newest at the bottom.
        if (_pendingGarbageDisplay > 0)
        {
            int ix = (int)wx - 6;
            int iy = (int)wy + Rows * Cell - 4;
            int blink = (int)(Raylib.GetTime() * 3) % 2 == 0 ? 220 : 80;
            for (int i = 0; i < _pendingGarbageDisplay && i < Rows; i++)
            {
                Raylib.DrawRectangle(ix, iy - i * 4, 4, 3,
                    new Color((byte)blink, (byte)40, (byte)40, (byte)255));
            }
        }

        // ── Opponent mini-board: 6×12 px cells, right of our well.
        const int miniCell = 6;
        int miniW = Cols * miniCell;
        int miniH = Rows * miniCell;
        int mx = (int)(panelOffset.X + PanelSize.X - FrameInset - miniW - 8);
        int my = (int)wy;
        Raylib.DrawRectangle(mx - 2, my - 2, miniW + 4, miniH + 4,
            new Color((byte)0, (byte)0, (byte)0, (byte)255));
        var peer = s.PeerBoard;
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Cols; x++)
            {
                byte v = peer[y * Cols + x];
                if (v == 0) continue;
                Color c = v == 8
                    ? new Color((byte)128, (byte)128, (byte)128, (byte)255)
                    : Colors[Math.Min(v - 1, 6)];
                Raylib.DrawRectangle(mx + x * miniCell, my + y * miniCell,
                    miniCell - 1, miniCell - 1, c);
            }
        }
        // Frame outline.
        Raylib.DrawRectangleLines(mx - 2, my - 2, miniW + 4, miniH + 4,
            new Color((byte)244, (byte)200, (byte)80, (byte)200));

        // ── Header + scoreboard row above the mini-board (or
        //    overflowing into the menu-bar strip if needed).
        int hudY = my + miniH + 4;
        RetroSkin.DrawText(s.PeerName, mx, hudY, RetroSkin.BodyText,
            RetroSkin.BodyFontSize - 2);
        string peerLine = s.PeerToppedOut
            ? "topped out"
            : s.PeerDisconnected
                ? "left"
                : s.IsPeerStale
                    ? "…no signal"
                    : $"L{s.PeerLevel}  {s.PeerLines} lines";
        RetroSkin.DrawText(peerLine,
            mx, hudY + 14, RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);
        if (s.PeerPendingGarbage > 0)
        {
            RetroSkin.DrawText($"⚠ {s.PeerPendingGarbage} incoming",
                mx, hudY + 28,
                new Color((byte)240, (byte)80, (byte)80, (byte)255),
                RetroSkin.BodyFontSize - 2);
        }

        // ── Win / lose banner once the match resolves.
        if (s.PeerToppedOut || _gameOver || s.PeerDisconnected)
        {
            string banner;
            if (s.PeerToppedOut && !_gameOver) banner = "🏆 You win!";
            else if (_gameOver && !s.PeerToppedOut) banner = $"🏆 {s.PeerName} wins!";
            else if (s.PeerDisconnected && !_gameOver) banner = "Peer left — solo finish.";
            else banner = "Match ended.";
            int bw = RetroSkin.MeasureText(banner, RetroSkin.BodyFontSize);
            int bx = (int)(panelOffset.X + PanelSize.X / 2 - bw / 2);
            int by = (int)wy + 6;
            Raylib.DrawRectangle(bx - 6, by - 4, bw + 12,
                RetroSkin.BodyFontSize + 8,
                new Color((byte)0, (byte)0, (byte)0, (byte)200));
            RetroSkin.DrawText(banner, bx, by,
                new Color((byte)80, (byte)240, (byte)80, (byte)255),
                RetroSkin.BodyFontSize);
        }
    }

    private static void DrawBlock(int x, int y, Color col)
    {
        var rect = new Rectangle(x, y, Cell, Cell);
        Raylib.DrawRectangleRec(rect, col);
        // Bevel
        Raylib.DrawRectangle(x, y, Cell, 2, new Color(255, 255, 255, 100));
        Raylib.DrawRectangle(x, y, 2, Cell, new Color(255, 255, 255, 100));
        Raylib.DrawRectangle(x, y + Cell - 2, Cell, 2, new Color(0, 0, 0, 100));
        Raylib.DrawRectangle(x + Cell - 2, y, 2, Cell, new Color(0, 0, 0, 100));
    }

    // Per-piece rotation tables, packed as rotations × 8 ints (4 cells × (x, y)).
    private static readonly int[][] Shapes = new int[][]
    {
        // I — 2 rotations
        new[] { 0,1, 1,1, 2,1, 3,1,   2,0, 2,1, 2,2, 2,3 },
        // O — 1 rotation
        new[] { 1,0, 2,0, 1,1, 2,1 },
        // T — 4 rotations
        new[] { 0,1, 1,1, 2,1, 1,0,
                1,0, 1,1, 1,2, 2,1,
                0,1, 1,1, 2,1, 1,2,
                1,0, 1,1, 1,2, 0,1 },
        // S — 2 rotations
        new[] { 1,1, 2,1, 0,2, 1,2,
                1,0, 1,1, 2,1, 2,2 },
        // Z — 2 rotations
        new[] { 0,1, 1,1, 1,2, 2,2,
                2,0, 1,1, 2,1, 1,2 },
        // L — 4 rotations
        new[] { 0,1, 1,1, 2,1, 2,0,
                1,0, 1,1, 1,2, 2,2,
                0,1, 1,1, 2,1, 0,2,
                0,0, 1,0, 1,1, 1,2 },
        // J — 4 rotations
        new[] { 0,0, 0,1, 1,1, 2,1,
                1,0, 2,0, 1,1, 1,2,
                0,1, 1,1, 2,1, 2,2,
                1,0, 1,1, 0,2, 1,2 },
    };

    public void Close()
    {
        if (_netplay != null)
        {
            if (!_gameOver) _netplay.OnLocalQuit();
            _netplay.RecordAndUnregister();
            _netplay = null;
        }
    }
}
