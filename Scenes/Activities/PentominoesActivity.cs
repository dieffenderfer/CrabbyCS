using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Pentominoes — port of FUNGAMES/PEN2MINO (Manny Juan, 1987, PEN2PLAY).
/// Mechanics drawn directly from PEN2PLAY.DOC:
///
///   • 12 pieces (F I L N P T U V W X Y Z), each five squares, named
///     after the letters they roughly resemble.
///   • All 12 pieces together cover 60 squares. The standard boards are
///     6×10, 5×12, 4×15, 3×20, and 8×8 with a 2×2 hole in the centre.
///   • Pick a piece, position it, rotate / flip with arrow keys, click
///     to place. ESC pops the most recently placed piece.
///   • A "GO" option asks the computer to keep trying solutions from
///     the current board state until it finds one or the user aborts.
///
/// The solver is a straightforward backtracking search: at each step,
/// find the first empty cell, try every unplaced piece in every
/// orientation that covers that cell, recurse, undo on failure. Fast
/// enough for the 6×10 board in well under a second; can stall on the
/// tighter 3×20 layout (we cap the search budget).
/// </summary>
public class PentominoesActivity : IActivity
{
    public Vector2 PanelSize => new(720, 540);
    public bool IsFinished { get; private set; }

    private const int FrameInset = 3;
    private const int SidebarW = 200;

    // 12 pentomino shapes as (x, y) offsets from the canonical anchor.
    // Anchor convention: the cell list for each piece. Rotations/flips
    // are computed on the fly.
    private static readonly Dictionary<char, (int x, int y)[]> Pieces = new()
    {
        ['F'] = new[] { (1, 0), (2, 0), (0, 1), (1, 1), (1, 2) },
        ['I'] = new[] { (0, 0), (1, 0), (2, 0), (3, 0), (4, 0) },
        ['L'] = new[] { (0, 0), (0, 1), (0, 2), (0, 3), (1, 3) },
        ['N'] = new[] { (1, 0), (1, 1), (0, 1), (0, 2), (0, 3) },
        ['P'] = new[] { (0, 0), (1, 0), (0, 1), (1, 1), (0, 2) },
        ['T'] = new[] { (0, 0), (1, 0), (2, 0), (1, 1), (1, 2) },
        ['U'] = new[] { (0, 0), (2, 0), (0, 1), (1, 1), (2, 1) },
        ['V'] = new[] { (0, 0), (0, 1), (0, 2), (1, 2), (2, 2) },
        ['W'] = new[] { (0, 0), (0, 1), (1, 1), (1, 2), (2, 2) },
        ['X'] = new[] { (1, 0), (0, 1), (1, 1), (2, 1), (1, 2) },
        ['Y'] = new[] { (1, 0), (0, 1), (1, 1), (1, 2), (1, 3) },
        ['Z'] = new[] { (0, 0), (1, 0), (1, 1), (1, 2), (2, 2) },
    };

    private static readonly Color[] PieceColors =
    {
        new(232, 100,  92, 255),
        new( 80, 152, 232, 255),
        new(112, 196, 116, 255),
        new(232, 192,  60, 255),
        new(196, 116, 200, 255),
        new( 96, 200, 200, 255),
        new(240, 156,  88, 255),
        new(168, 168, 224, 255),
        new(120, 200, 152, 255),
        new(216, 152, 120, 255),
        new(140, 188, 232, 255),
        new(220, 180,  68, 255),
    };

    private record BoardDef(string Name, int W, int H, int[]? Holes);

    private static readonly BoardDef[] Boards =
    {
        new("6 × 10",          6, 10, null),
        new("5 × 12",          5, 12, null),
        new("4 × 15",          4, 15, null),
        new("3 × 20",          3, 20, null),
        // 8×8 with the middle 2×2 missing → (3,3),(4,3),(3,4),(4,4).
        new("8 × 8 (hole)",    8,  8, new[] { 3 + 3 * 8, 4 + 3 * 8, 3 + 4 * 8, 4 + 4 * 8 }),
    };

    private int _boardIdx;
    private int _bw, _bh;
    private char[,] _board = new char[1, 1];   // ' ' = empty, '#' = hole, else piece letter
    private readonly HashSet<char> _placed = new();
    private readonly Stack<(char letter, List<(int x, int y)> cells)> _undo = new();
    private char? _heldPiece;
    private int _rotation;                     // 0..3
    private bool _flipped;
    private string _status = "Pick a piece from the right, drop it on the board.";

    private bool _solving;
    private int _solveBudget;
    private const int SolverMaxNodes = 4_000_000;

    public void Load() => NewGame(0);
    public void Close() => IsFinished = true;

    private void NewGame(int boardIdx)
    {
        _boardIdx = boardIdx;
        var b = Boards[boardIdx];
        _bw = b.W;
        _bh = b.H;
        _board = new char[_bw, _bh];
        for (int y = 0; y < _bh; y++)
            for (int x = 0; x < _bw; x++)
                _board[x, y] = ' ';
        if (b.Holes != null)
            foreach (var idx in b.Holes)
                _board[idx % b.W, idx / b.W] = '#';
        _placed.Clear();
        _undo.Clear();
        _heldPiece = null;
        _rotation = 0;
        _flipped = false;
        _solving = false;
        _status = $"Board: {b.Name}. {Boards[boardIdx].W * Boards[boardIdx].H - (Boards[boardIdx].Holes?.Length ?? 0)} squares, 12 pieces (60 squares total).";
    }

    private static (int x, int y)[] Transform((int x, int y)[] piece, int rot, bool flip)
    {
        var cells = new (int x, int y)[piece.Length];
        for (int i = 0; i < piece.Length; i++)
        {
            int x = piece[i].x;
            int y = piece[i].y;
            if (flip) x = -x;
            for (int r = 0; r < rot; r++)
            {
                int nx = -y;
                int ny = x;
                x = nx; y = ny;
            }
            cells[i] = (x, y);
        }
        // Normalize so min-x and min-y are 0.
        int minX = int.MaxValue, minY = int.MaxValue;
        foreach (var (cx, cy) in cells) { if (cx < minX) minX = cx; if (cy < minY) minY = cy; }
        for (int i = 0; i < cells.Length; i++)
            cells[i] = (cells[i].x - minX, cells[i].y - minY);
        return cells;
    }

    // Distinct orientations of a piece, for the solver.
    private static List<(int x, int y)[]> DistinctOrientations(char letter)
    {
        var seen = new HashSet<string>();
        var list = new List<(int x, int y)[]>();
        foreach (bool flip in new[] { false, true })
            for (int rot = 0; rot < 4; rot++)
            {
                var cells = Transform(Pieces[letter], rot, flip);
                Array.Sort(cells, (a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
                var key = string.Join(",", cells.Select(p => $"{p.x},{p.y}"));
                if (seen.Add(key)) list.Add(cells);
            }
        return list;
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
        var items = new[] { "Board ▾", "Undo", "Clear", "GO" };
        switch (RetroWidgets.MenuBarHitTest(menuBar, items, local, leftPressed))
        {
            case 0: NewGame((_boardIdx + 1) % Boards.Length); return;
            case 1: PopOne(); return;
            case 2: NewGame(_boardIdx); return;
            case 3: TrySolve(); return;
        }

        // Held-piece transforms via keys.
        if (Raylib.IsKeyPressed(KeyboardKey.Up) || Raylib.IsKeyPressed(KeyboardKey.R))
            _rotation = (_rotation + 1) % 4;
        if (Raylib.IsKeyPressed(KeyboardKey.Down))
            _rotation = (_rotation + 3) % 4;
        if (Raylib.IsKeyPressed(KeyboardKey.F) || Raylib.IsKeyPressed(KeyboardKey.Right))
            _flipped = !_flipped;
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { _heldPiece = null; }
        if (Raylib.IsKeyPressed(KeyboardKey.U)) PopOne();

        // Sidebar piece pick.
        var bag = SidebarRect();
        if (leftPressed && RetroSkin.PointInRect(local, bag))
        {
            char? pick = PickPieceFromBag(local, bag);
            if (pick != null && !_placed.Contains(pick.Value))
            {
                _heldPiece = pick;
                _rotation = 0;
                _flipped = false;
                _status = $"Selected {pick}. R / arrows rotate, F flips, click board to place.";
            }
        }

        // Click board to place held piece.
        var brd = BoardRect();
        if (leftPressed && _heldPiece != null && RetroSkin.PointInRect(local, brd))
        {
            int cellSize = CellSize();
            int bx = (int)((local.X - brd.X) / cellSize);
            int by = (int)((local.Y - brd.Y) / cellSize);
            if (TryPlace(_heldPiece.Value, bx, by, _rotation, _flipped))
            {
                if (_placed.Count == 12) _status = "All twelve placed — board filled!";
                else _status = $"Placed {_heldPiece.Value}. {12 - _placed.Count} pieces left.";
                _heldPiece = null;
            }
            else
            {
                _status = "That doesn't fit there.";
            }
        }
    }

    private char? PickPieceFromBag(Vector2 local, Rectangle bag)
    {
        var letters = Pieces.Keys.ToArray();
        int rows = (letters.Length + 1) / 2;
        float rowH = (bag.Height - 24) / rows;
        float colW = bag.Width / 2;
        int row = (int)((local.Y - bag.Y - 12) / rowH);
        int col = (int)((local.X - bag.X) / colW);
        if (row < 0 || row >= rows || col < 0 || col > 1) return null;
        int idx = row * 2 + col;
        if (idx >= letters.Length) return null;
        return letters[idx];
    }

    private bool TryPlace(char letter, int anchorX, int anchorY, int rot, bool flip)
    {
        var cells = Transform(Pieces[letter], rot, flip);
        var placed = new List<(int x, int y)>();
        // We want the cursor-clicked cell to be the top-left of the bounding
        // box of the transformed piece; cells are already normalised so
        // (0,0) is that corner.
        foreach (var (dx, dy) in cells)
        {
            int x = anchorX + dx;
            int y = anchorY + dy;
            if (x < 0 || y < 0 || x >= _bw || y >= _bh) return false;
            if (_board[x, y] != ' ') return false;
            placed.Add((x, y));
        }
        foreach (var (x, y) in placed) _board[x, y] = letter;
        _placed.Add(letter);
        _undo.Push((letter, placed));
        return true;
    }

    private void PopOne()
    {
        if (_undo.Count == 0) return;
        var (letter, cells) = _undo.Pop();
        foreach (var (x, y) in cells) _board[x, y] = ' ';
        _placed.Remove(letter);
        _status = $"Removed {letter}.";
    }

    // ── Solver ────────────────────────────────────────────────────────────
    private void TrySolve()
    {
        _solving = true;
        _solveBudget = SolverMaxNodes;
        bool ok = SolveStep();
        _solving = false;
        _status = ok ? "Solver finished the board." : "Solver gave up (budget exhausted).";
    }

    private bool SolveStep()
    {
        if (_solveBudget-- <= 0) return false;
        // Find the first empty cell.
        int fx = -1, fy = -1;
        for (int y = 0; y < _bh && fx < 0; y++)
            for (int x = 0; x < _bw && fx < 0; x++)
                if (_board[x, y] == ' ') { fx = x; fy = y; }
        if (fx < 0) return true;   // board full

        foreach (var letter in Pieces.Keys)
        {
            if (_placed.Contains(letter)) continue;
            foreach (var orient in DistinctOrientations(letter))
            {
                // Try placing this orientation so that one of its cells
                // lands exactly on (fx,fy) — this prunes huge swaths.
                foreach (var (dx, dy) in orient)
                {
                    int ax = fx - dx;
                    int ay = fy - dy;
                    var placed = new List<(int x, int y)>(orient.Length);
                    bool ok = true;
                    foreach (var (ox, oy) in orient)
                    {
                        int nx = ax + ox, ny = ay + oy;
                        if (nx < 0 || ny < 0 || nx >= _bw || ny >= _bh) { ok = false; break; }
                        if (_board[nx, ny] != ' ') { ok = false; break; }
                        placed.Add((nx, ny));
                    }
                    if (!ok) continue;
                    foreach (var (px, py) in placed) _board[px, py] = letter;
                    _placed.Add(letter);
                    _undo.Push((letter, placed));
                    if (SolveStep()) return true;
                    var pop = _undo.Pop();
                    foreach (var (px, py) in pop.cells) _board[px, py] = ' ';
                    _placed.Remove(letter);
                }
            }
        }
        return false;
    }

    // ── Layout ────────────────────────────────────────────────────────────
    private int CellSize()
    {
        var brd = BoardRect();
        return (int)Math.Floor(Math.Min(brd.Width / _bw, brd.Height / _bh));
    }

    private Rectangle BoardRect()
    {
        float top = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 12;
        float bottom = PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight - 12;
        float right = PanelSize.X - SidebarW - 12;
        // Board occupies the bigger of width/height in a 1:1 cell ratio.
        float w = right - (FrameInset + 12);
        float h = bottom - top;
        return new Rectangle(FrameInset + 12, top, w, h);
    }

    private Rectangle SidebarRect()
    {
        float top = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 12;
        float bottom = PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight - 12;
        return new Rectangle(PanelSize.X - SidebarW - 4, top, SidebarW, bottom - top);
    }

    // ── Drawing ───────────────────────────────────────────────────────────
    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Pentominoes", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        var items = new[] { "Board ▾", "Undo", "Clear", "GO" };
        RetroWidgets.MenuBarVisual(menuBar, items, -1);

        DrawBoard(panelOffset);
        DrawSidebar(panelOffset);

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(status, _status,
            $"Board {Boards[_boardIdx].Name}  Placed {_placed.Count}/12");
    }

    private void DrawBoard(Vector2 panelOffset)
    {
        var brd = BoardRect();
        var brdAbs = new Rectangle(panelOffset.X + brd.X, panelOffset.Y + brd.Y,
            brd.Width, brd.Height);
        int cell = CellSize();
        float gridW = cell * _bw;
        float gridH = cell * _bh;
        float ox = brdAbs.X + (brd.Width - gridW) / 2f;
        float oy = brdAbs.Y + (brd.Height - gridH) / 2f;

        // Cells
        for (int x = 0; x < _bw; x++)
        {
            for (int y = 0; y < _bh; y++)
            {
                float px = ox + x * cell;
                float py = oy + y * cell;
                char c = _board[x, y];
                Color col;
                if (c == '#')
                    col = new Color((byte)40, (byte)40, (byte)48, (byte)255);
                else if (c == ' ')
                    col = new Color((byte)252, (byte)244, (byte)216, (byte)255);
                else
                    col = ColorForLetter(c);
                Raylib.DrawRectangle((int)px, (int)py, cell, cell, col);
                Raylib.DrawRectangleLines((int)px, (int)py, cell, cell, new Color((byte)0, (byte)0, (byte)0, (byte)80));
                if (c != ' ' && c != '#')
                {
                    int tw = FontManager.MeasureText(c.ToString(), 12);
                    FontManager.DrawText(c.ToString(),
                        (int)(px + (cell - tw) / 2),
                        (int)(py + (cell - 12) / 2),
                        12, new Color((byte)20, (byte)20, (byte)30, (byte)160));
                }
            }
        }

        // Held piece preview tracking the mouse.
        if (_heldPiece != null)
        {
            var local = Raylib.GetMousePosition() / UIScaling.Factor - panelOffset;
            var cells = Transform(Pieces[_heldPiece.Value], _rotation, _flipped);
            int bx = (int)((local.X - brd.X - (brd.Width - gridW) / 2f) / cell);
            int by = (int)((local.Y - brd.Y - (brd.Height - gridH) / 2f) / cell);
            var col = ColorForLetter(_heldPiece.Value);
            // Lighter ghost color
            col.A = 160;
            bool fits = true;
            foreach (var (dx, dy) in cells)
            {
                int nx = bx + dx, ny = by + dy;
                if (nx < 0 || ny < 0 || nx >= _bw || ny >= _bh) { fits = false; break; }
                if (_board[nx, ny] != ' ') { fits = false; break; }
            }
            foreach (var (dx, dy) in cells)
            {
                int nx = bx + dx, ny = by + dy;
                float px = ox + nx * cell;
                float py = oy + ny * cell;
                if (!fits)
                {
                    Raylib.DrawRectangleLinesEx(new Rectangle(px, py, cell, cell), 2,
                        new Color((byte)200, (byte)80, (byte)80, (byte)200));
                }
                else
                {
                    Raylib.DrawRectangle((int)px, (int)py, cell, cell, col);
                    Raylib.DrawRectangleLines((int)px, (int)py, cell, cell, new Color((byte)0, (byte)0, (byte)0, (byte)80));
                }
            }
        }
    }

    private void DrawSidebar(Vector2 panelOffset)
    {
        var bag = SidebarRect();
        var bagAbs = new Rectangle(panelOffset.X + bag.X, panelOffset.Y + bag.Y, bag.Width, bag.Height);
        RetroSkin.DrawSunken(bagAbs, RetroSkin.SunkenBg);

        // Title strip
        FontManager.DrawText("PIECES",
            (int)bagAbs.X + 10, (int)bagAbs.Y + 6, 14, RetroSkin.BodyText);
        FontManager.DrawText("click to pick",
            (int)bagAbs.X + 10, (int)bagAbs.Y + 22, 11, RetroSkin.DisabledText);

        var letters = Pieces.Keys.ToArray();
        int rows = (letters.Length + 1) / 2;
        float rowH = (bag.Height - 60) / rows;
        float colW = bag.Width / 2;
        for (int i = 0; i < letters.Length; i++)
        {
            int row = i / 2;
            int col = i % 2;
            float cellX = bagAbs.X + col * colW + 6;
            float cellY = bagAbs.Y + 42 + row * rowH;
            float cellW = colW - 10;
            float cellH = rowH - 6;
            char letter = letters[i];
            bool placed = _placed.Contains(letter);
            bool held = _heldPiece == letter;
            Raylib.DrawRectangleRec(new Rectangle(cellX, cellY, cellW, cellH),
                placed ? RetroSkin.Face
                       : held ? RetroSkin.Highlight
                              : new Color((byte)252, (byte)244, (byte)216, (byte)255));
            Raylib.DrawRectangleLinesEx(new Rectangle(cellX, cellY, cellW, cellH), 1, RetroSkin.Shadow);

            // Draw the piece glyph small inside the cell.
            var cells = Transform(Pieces[letter], 0, false);
            int maxX = 0, maxY = 0;
            foreach (var (px, py) in cells) { if (px > maxX) maxX = px; if (py > maxY) maxY = py; }
            int glyphSize = (int)Math.Min((cellW - 24) / (maxX + 1), (cellH - 16) / (maxY + 1));
            glyphSize = Math.Max(4, Math.Min(glyphSize, 10));
            float gx0 = cellX + (cellW - glyphSize * (maxX + 1)) / 2f - 8;
            float gy0 = cellY + (cellH - glyphSize * (maxY + 1)) / 2f;
            var color = placed ? RetroSkin.DisabledText : ColorForLetter(letter);
            foreach (var (px, py) in cells)
            {
                Raylib.DrawRectangle((int)(gx0 + px * glyphSize), (int)(gy0 + py * glyphSize),
                    glyphSize, glyphSize, color);
                Raylib.DrawRectangleLines((int)(gx0 + px * glyphSize), (int)(gy0 + py * glyphSize),
                    glyphSize, glyphSize, new Color((byte)0, (byte)0, (byte)0, (byte)60));
            }

            // Letter label
            FontManager.DrawText(letter.ToString(),
                (int)(cellX + cellW - 14), (int)(cellY + cellH - 16),
                14, placed ? RetroSkin.DisabledText : RetroSkin.BodyText);
        }
    }

    private static Color ColorForLetter(char letter)
    {
        var letters = "FILNPTUVWXYZ";
        int i = letters.IndexOf(letter);
        return i >= 0 ? PieceColors[i] : RetroSkin.Face;
    }
}
