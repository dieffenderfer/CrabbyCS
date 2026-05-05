using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Pipe Dream — connect the source to the drain by placing pipe pieces from
/// the queue onto the grid before water arrives. Water flows step-by-step
/// through any contiguous run of compatible connections; reach the drain to
/// win the round, otherwise survive as long as you can. Help and Fonts
/// menu items open in-window overlays.
/// </summary>
public class PipeDreamActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Cols = 9;
    private const int Rows = 7;
    private const int Cell = 36;
    private const int Margin = 16;
    private const int QueueSize = 5;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + Cols * Cell + Cell + Margin,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + Rows * Cell + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private enum PieceType { H, V, NE, NW, SE, SW, Cross, Source, Drain }
    // bit flags for connections (N=1, E=2, S=4, W=8)
    private static int Connections(PieceType t) => t switch
    {
        PieceType.H => 2 | 8,
        PieceType.V => 1 | 4,
        PieceType.NE => 1 | 2,
        PieceType.NW => 1 | 8,
        PieceType.SE => 4 | 2,
        PieceType.SW => 4 | 8,
        PieceType.Cross => 1 | 2 | 4 | 8,
        PieceType.Source => 4,         // emits south
        PieceType.Drain => 1 | 2 | 4 | 8, // accepts from any side
        _ => 0,
    };

    private PieceType?[,] _board = new PieceType?[Cols, Rows];
    private bool[,] _filled = new bool[Cols, Rows];
    private List<PieceType> _queue = new();
    private (int x, int y) _source;
    private (int x, int y) _drain;
    private (int x, int y) _flowCell;
    private int _flowFrom;
    private float _flowTimer;
    private float _initialDelay;
    private int _score;
    private bool _flowing;
    private bool _gameOver;
    private bool _won;
    private readonly Random _rng = new();

    // Overlays
    private readonly RetroHelp _help = new()
    {
        Title = "Pipe Dream — How to play",
        Lines = new[]
        {
            "Connect the source to the drain (target reticle).",
            "Click an empty cell to drop the next pipe from the queue.",
            "Pieces can't be moved or removed once placed.",
            "Water starts flowing after the countdown — pick the queue",
            "carefully so its connections line up with the next cell.",
            "Each filled tile scores a point. Reach the drain to win.",
        },
        DiagramHeight = 44,
        Diagram = r =>
        {
            // Source, two corner pieces, a straight, and the drain — connected.
            const int sz = 36;
            int gap = 4;
            var pieces = new[] {
                PieceType.Source, PieceType.SE, PieceType.H, PieceType.SW, PieceType.Drain
            };
            int x0 = (int)(r.X + (r.Width - pieces.Length * (sz + gap) + gap) / 2);
            int y = (int)(r.Y + 4);
            for (int i = 0; i < pieces.Length; i++)
            {
                int px = x0 + i * (sz + gap);
                Raylib.DrawRectangle(px, y, sz, sz, new Color(64, 64, 64, 255));
                Raylib.DrawRectangleLines(px, y, sz, sz, new Color(48, 48, 48, 255));
                DrawPipe(px, y, pieces[i], filled: true);
            }
        },
    };
    private bool _showFonts;
    private int _draggingSlider = -1;  // 0=body, 1=title, 2=status

    public void Load() => Reset();

    private void Reset()
    {
        _board = new PieceType?[Cols, Rows];
        _filled = new bool[Cols, Rows];
        _queue.Clear();
        for (int i = 0; i < QueueSize; i++) _queue.Add(RandomPipe());
        _source = (_rng.Next(2, Cols - 2), 0);
        _board[_source.x, _source.y] = PieceType.Source;
        // Drain placed somewhere in the lower half, at least 3 cols away
        // from the source so the puzzle isn't trivial.
        while (true)
        {
            int dx = _rng.Next(0, Cols);
            int dy = _rng.Next(Rows / 2 + 1, Rows);
            if (Math.Abs(dx - _source.x) >= 3) { _drain = (dx, dy); break; }
        }
        _board[_drain.x, _drain.y] = PieceType.Drain;
        _flowing = false;
        _gameOver = false;
        _won = false;
        _score = 0;
        _initialDelay = 12f;
        _flowTimer = 0;
    }

    private PieceType RandomPipe()
    {
        var pool = new[] { PieceType.H, PieceType.V, PieceType.NE, PieceType.NW, PieceType.SE, PieceType.SW };
        if (_rng.Next(12) == 0) return PieceType.Cross;
        return pool[_rng.Next(pool.Length)];
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
        switch (RetroWidgets.MenuBarHitTest(menuBar, new[] { "New", "Help", "Fonts" }, local, leftPressed))
        {
            case 0: Reset(); _help.Visible = false; _showFonts = false; return;
            case 1: _help.Visible = !_help.Visible; _showFonts = false; return;
            case 2: _showFonts = !_showFonts; _help.Visible = false; return;
        }

        if (_help.HandleInput(local, leftPressed, PanelSize)) return;
        if (_showFonts)
        {
            UpdateFontPanel(local, leftPressed, leftReleased);
            return;
        }

        if (!_flowing && !_won && !_gameOver)
        {
            _initialDelay -= delta;
            if (_initialDelay <= 0) StartFlow();
        }
        else if (_flowing && !_won && !_gameOver)
        {
            _flowTimer += delta;
            float interval = Math.Max(0.25f, 1.5f - _score * 0.04f);
            if (_flowTimer >= interval)
            {
                _flowTimer = 0;
                AdvanceFlow();
            }
        }

        if (leftPressed && !_gameOver && !_won)
        {
            float bx = FrameInset + Margin;
            float by = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
            int gx = (int)((local.X - bx) / Cell);
            int gy = (int)((local.Y - by) / Cell);
            if (gx >= 0 && gx < Cols && gy >= 0 && gy < Rows
                && _board[gx, gy] == null
                && !(gx == _source.x && gy == _source.y)
                && !(gx == _drain.x && gy == _drain.y))
            {
                _board[gx, gy] = _queue[0];
                _queue.RemoveAt(0);
                _queue.Add(RandomPipe());
            }
        }
    }

    private void StartFlow()
    {
        _flowing = true;
        _filled[_source.x, _source.y] = true;
        _flowCell = _source;
        _flowFrom = 1;
    }

    private void AdvanceFlow()
    {
        if (_board[_flowCell.x, _flowCell.y] is not PieceType cur) { _gameOver = true; return; }
        int conns = Connections(cur);
        int exitDir;
        if (cur == PieceType.Source) exitDir = 4;
        else
        {
            int incoming = _flowFrom;
            int remaining = conns & ~incoming;
            if (remaining == 0) { _gameOver = true; return; }
            exitDir = remaining;
            if (cur == PieceType.Cross) exitDir = 15 & ~incoming & OppositeOf(incoming);
        }

        var (nx, ny) = exitDir switch
        {
            1 => (_flowCell.x, _flowCell.y - 1),
            2 => (_flowCell.x + 1, _flowCell.y),
            4 => (_flowCell.x, _flowCell.y + 1),
            8 => (_flowCell.x - 1, _flowCell.y),
            _ => (-1, -1),
        };
        if (nx < 0 || nx >= Cols || ny < 0 || ny >= Rows) { _gameOver = true; return; }
        if (_board[nx, ny] is not PieceType next) { _gameOver = true; return; }
        int nextConns = Connections(next);
        int entryDir = OppositeOf(exitDir);
        if ((nextConns & entryDir) == 0) { _gameOver = true; return; }
        _flowCell = (nx, ny);
        _filled[nx, ny] = true;
        _flowFrom = entryDir;
        _score++;
        if (next == PieceType.Drain) { _won = true; _flowing = false; }
    }

    private static int OppositeOf(int d) => d switch { 1 => 4, 2 => 8, 4 => 1, 8 => 2, _ => 0 };

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Pipe Dream", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", "Help", "Fonts" }, -1);

        float bx = panelOffset.X + FrameInset + Margin;
        float by = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
        var board = new Rectangle(bx - 3, by - 3, Cols * Cell + 6, Rows * Cell + 6);
        RetroSkin.DrawSunken(board, new Color(80, 80, 80, 255));

        for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Cols; x++)
            {
                int px = (int)(bx + x * Cell), py = (int)(by + y * Cell);
                Raylib.DrawRectangle(px, py, Cell, Cell, new Color(64, 64, 64, 255));
                Raylib.DrawRectangleLines(px, py, Cell, Cell, new Color(48, 48, 48, 255));
                if (_board[x, y] is PieceType t)
                    DrawPipe(px, py, t, _filled[x, y]);
            }

        // Queue
        float qx = panelOffset.X + FrameInset + Margin + Cols * Cell + Margin;
        float qy = by;
        RetroSkin.DrawText("NEXT", (int)qx, (int)(qy - 16), RetroSkin.BodyText, 16);
        for (int i = 0; i < _queue.Count; i++)
        {
            int px = (int)qx, py = (int)(qy + i * (Cell + 4));
            Raylib.DrawRectangle(px, py, Cell, Cell, new Color(64, 64, 64, 255));
            Raylib.DrawRectangleLines(px, py, Cell, Cell, RetroSkin.Shadow);
            DrawPipe(px, py, _queue[i], false);
        }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string state = _won ? "Drain reached!"
            : _gameOver ? "Pipe broke"
            : _flowing ? "Flowing..."
            : $"Flow in {(int)Math.Ceiling(_initialDelay)}s";
        RetroWidgets.StatusBar(status, state, $"Filled: {_score}");

        _help.Draw(panelOffset, PanelSize);
        if (_showFonts) DrawFontPanel(panelOffset);
    }

    private static void DrawPipe(int px, int py, PieceType t, bool filled)
    {
        var bg = filled ? new Color(80, 160, 240, 255) : new Color(180, 180, 180, 255);
        int cx = px + Cell / 2, cy = py + Cell / 2;
        int conns = Connections(t);
        int thick = 8;

        if ((conns & 1) != 0) Raylib.DrawRectangle(cx - thick / 2, py, thick, cy - py, bg);
        if ((conns & 4) != 0) Raylib.DrawRectangle(cx - thick / 2, cy, thick, py + Cell - cy, bg);
        if ((conns & 8) != 0) Raylib.DrawRectangle(px, cy - thick / 2, cx - px, thick, bg);
        if ((conns & 2) != 0) Raylib.DrawRectangle(cx, cy - thick / 2, px + Cell - cx, thick, bg);

        if (t == PieceType.Source)
            Raylib.DrawCircle(cx, cy, 8, new Color(40, 100, 200, 255));
        else if (t == PieceType.Drain)
        {
            // Draw a target reticle so the drain is unambiguous.
            Raylib.DrawCircleLines(cx, cy, 12, new Color(220, 80, 60, 255));
            Raylib.DrawCircleLines(cx, cy, 7, new Color(220, 80, 60, 255));
            Raylib.DrawCircle(cx, cy, 3, new Color(220, 80, 60, 255));
        }
    }

    // ── Font debug panel ────────────────────────────────────────────────
    private Rectangle FontPanelRectLocal()
    {
        float w = PanelSize.X - 60;
        float h = 220;
        return new Rectangle((PanelSize.X - w) / 2, (PanelSize.Y - h) / 2, w, h);
    }

    private record struct SliderSpec(string Label, int Min, int Max, Func<int> Get, Action<int> Set);

    private static SliderSpec[] FontSliders() => new[]
    {
        new SliderSpec("Body / menu", 8, 36, () => RetroSkin.BodyFontSize, v => RetroSkin.BodyFontSize = v),
        new SliderSpec("Title bar", 8, 36, () => RetroSkin.TitleFontSize, v => RetroSkin.TitleFontSize = v),
        new SliderSpec("Status bar", 8, 28, () => RetroWidgets.StatusFontSize, v => RetroWidgets.StatusFontSize = v),
    };

    private void UpdateFontPanel(Vector2 local, bool leftPressed, bool leftReleased)
    {
        var r = FontPanelRectLocal();
        // Click outside dismisses
        if (leftPressed && !RetroSkin.PointInRect(local, r))
        { _showFonts = false; _draggingSlider = -1; return; }

        var sliders = FontSliders();
        for (int i = 0; i < sliders.Length; i++)
        {
            var (track, _) = SliderRects(r, i, sliders[i]);
            if (leftPressed && (RetroSkin.PointInRect(local, ExpandRect(track, 0, 8))))
                _draggingSlider = i;
        }
        if (leftReleased) _draggingSlider = -1;

        if (_draggingSlider >= 0 && _draggingSlider < sliders.Length)
        {
            var s = sliders[_draggingSlider];
            var (track, _) = SliderRects(r, _draggingSlider, s);
            float t = Math.Clamp((local.X - track.X) / track.Width, 0, 1);
            int v = (int)MathF.Round(s.Min + t * (s.Max - s.Min));
            if (v != s.Get()) s.Set(v);
        }
    }

    private static Rectangle ExpandRect(Rectangle r, float dx, float dy)
        => new(r.X - dx, r.Y - dy, r.Width + 2 * dx, r.Height + 2 * dy);

    private (Rectangle track, Rectangle knob) SliderRects(Rectangle panel, int idx, SliderSpec s)
    {
        float rowY = panel.Y + 50 + idx * 50;
        var track = new Rectangle(panel.X + 130, rowY + 8, panel.Width - 180, 6);
        float t = (s.Get() - s.Min) / (float)(s.Max - s.Min);
        float kx = track.X + t * track.Width;
        var knob = new Rectangle(kx - 6, track.Y - 5, 12, 16);
        return (track, knob);
    }

    private void DrawFontPanel(Vector2 panelOffset)
    {
        var r = FontPanelRectLocal();
        var abs = new Rectangle(panelOffset.X + r.X, panelOffset.Y + r.Y, r.Width, r.Height);
        Raylib.DrawRectangle((int)abs.X + 4, (int)abs.Y + 4, (int)abs.Width, (int)abs.Height,
            new Color(0, 0, 0, 100));
        RetroSkin.DrawRaised(abs);

        var titleBar = new Rectangle(abs.X + 3, abs.Y + 3, abs.Width - 6, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Font sizes (debug)", true);

        var sliders = FontSliders();
        for (int i = 0; i < sliders.Length; i++)
        {
            var s = sliders[i];
            float rowY = abs.Y + 50 + i * 50;
            // Label
            RetroSkin.DrawText(s.Label, (int)abs.X + 16, (int)rowY, RetroSkin.BodyText, 14);
            // Value
            int v = s.Get();
            RetroSkin.DrawText($"{v}px", (int)(abs.X + abs.Width - 50), (int)rowY, RetroSkin.BodyText, 14);

            var (track, knob) = SliderRects(new Rectangle(r.X, r.Y, r.Width, r.Height), i, s);
            var trackAbs = new Rectangle(panelOffset.X + track.X, panelOffset.Y + track.Y, track.Width, track.Height);
            var knobAbs = new Rectangle(panelOffset.X + knob.X, panelOffset.Y + knob.Y, knob.Width, knob.Height);
            RetroSkin.DrawSunken(trackAbs, RetroSkin.Face);
            RetroSkin.DrawRaised(knobAbs);
        }

        RetroSkin.DrawText("Click outside to dismiss",
            (int)(abs.X + 16), (int)(abs.Y + abs.Height - 24),
            RetroSkin.DisabledText, 12);
    }

    public void Close() { }
}
