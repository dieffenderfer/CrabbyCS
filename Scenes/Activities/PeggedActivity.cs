using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Peg solitaire on the standard English (33-hole cross) board. Click a peg
/// to select; click an empty hole two cells away in a cardinal direction with
/// a peg between to jump (the jumped peg is removed). Win: one peg remaining
/// in the center.
/// </summary>
public class PeggedActivity : IActivity
{
    private const int FrameInset = 3;
    private const int CellSize = 36;
    private const int Grid = 7;
    private const int Margin = 24;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + Grid * CellSize,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + Grid * CellSize + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    // -1 = no hole, 0 = empty hole, 1 = peg
    private int[,] _board = new int[Grid, Grid];
    private (int x, int y)? _selected;
    private bool _won;
    private int _moves;
    private int _pegsLeft;

    public void Load() => Reset();

    private void Reset()
    {
        // English board: 3x3 plus arms
        bool Hole(int x, int y) =>
            (x >= 2 && x <= 4) || (y >= 2 && y <= 4);
        for (int y = 0; y < Grid; y++)
            for (int x = 0; x < Grid; x++)
                _board[x, y] = Hole(x, y) ? 1 : -1;
        _board[3, 3] = 0; // center empty
        _selected = null;
        _won = false;
        _moves = 0;
        _pegsLeft = CountPegs();
    }

    private int CountPegs()
    {
        int n = 0;
        for (int y = 0; y < Grid; y++)
            for (int x = 0; x < Grid; x++)
                if (_board[x, y] == 1) n++;
        return n;
    }

    private Vector2 CellTopLeft(int x, int y)
    {
        float ox = FrameInset + Margin + x * CellSize;
        float oy = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin
            + y * CellSize;
        return new Vector2(ox, oy);
    }

    private (int x, int y) HitCell(Vector2 local)
    {
        var origin = CellTopLeft(0, 0);
        int x = (int)((local.X - origin.X) / CellSize);
        int y = (int)((local.Y - origin.Y) / CellSize);
        if (local.X < origin.X || local.Y < origin.Y) return (-1, -1);
        if (x < 0 || x >= Grid || y < 0 || y >= Grid) return (-1, -1);
        return (x, y);
    }

    private bool TryJump(int sx, int sy, int dx, int dy)
    {
        int mx = (sx + dx) / 2, my = (sy + dy) / 2;
        if (_board[dx, dy] != 0) return false;
        if (_board[mx, my] != 1) return false;
        _board[sx, sy] = 0;
        _board[mx, my] = 0;
        _board[dx, dy] = 1;
        return true;
    }

    private bool HasMoves()
    {
        for (int y = 0; y < Grid; y++)
            for (int x = 0; x < Grid; x++)
            {
                if (_board[x, y] != 1) continue;
                foreach (var (dx, dy) in new[] { (-2, 0), (2, 0), (0, -2), (0, 2) })
                {
                    int tx = x + dx, ty = y + dy;
                    if (tx < 0 || tx >= Grid || ty < 0 || ty >= Grid) continue;
                    if (_board[tx, ty] != 0) continue;
                    int mx = x + dx / 2, my = y + dy / 2;
                    if (_board[mx, my] == 1) return true;
                }
            }
        return false;
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
        if (RetroWidgets.MenuBarHitTest(menuBar, new[] { "New" }, local, leftPressed) == 0)
        { Reset(); return; }

        if (!leftPressed || _won) return;

        var (cx, cy) = HitCell(local);
        if (cx < 0 || _board[cx, cy] == -1) return;

        if (_selected == null)
        {
            if (_board[cx, cy] == 1) _selected = (cx, cy);
            return;
        }

        var (sx, sy) = _selected.Value;
        if (cx == sx && cy == sy) { _selected = null; return; }
        if (_board[cx, cy] == 1) { _selected = (cx, cy); return; }

        // Must be 2-step cardinal move
        int ddx = cx - sx, ddy = cy - sy;
        bool valid = (Math.Abs(ddx) == 2 && ddy == 0) || (Math.Abs(ddy) == 2 && ddx == 0);
        if (!valid) { _selected = null; return; }

        if (TryJump(sx, sy, cx, cy))
        {
            _moves++;
            _pegsLeft = CountPegs();
            if (_pegsLeft == 1 && _board[3, 3] == 1) _won = true;
            else if (!HasMoves()) _won = false; // game ends, status bar reports
        }
        _selected = null;
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Pegged", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New" }, -1);

        // Wood-board background
        float bodyY = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight;
        float bodyH = PanelSize.Y - bodyY - FrameInset - RetroWidgets.StatusBarHeight;
        Raylib.DrawRectangleRec(new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + bodyY,
            PanelSize.X - 2 * FrameInset, bodyH), new Color(120, 80, 40, 255));

        // Holes + pegs
        for (int y = 0; y < Grid; y++)
            for (int x = 0; x < Grid; x++)
            {
                if (_board[x, y] == -1) continue;
                var p = CellTopLeft(x, y);
                int cx = (int)(panelOffset.X + p.X + CellSize / 2);
                int cy = (int)(panelOffset.Y + p.Y + CellSize / 2);

                Raylib.DrawCircle(cx, cy, CellSize / 2 - 4, new Color(60, 32, 16, 255));
                Raylib.DrawCircle(cx, cy, CellSize / 2 - 6, new Color(36, 18, 8, 255));

                if (_board[x, y] == 1)
                {
                    bool sel = _selected.HasValue && _selected.Value.x == x && _selected.Value.y == y;
                    var col = sel ? new Color(255, 220, 80, 255) : new Color(220, 230, 240, 255);
                    Raylib.DrawCircle(cx, cy, CellSize / 2 - 7, col);
                    Raylib.DrawCircle(cx - 2, cy - 2, 3, new Color(255, 255, 255, 200));
                    Raylib.DrawCircleLines(cx, cy, CellSize / 2 - 7, new Color(0, 0, 0, 180));
                }
            }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string state;
        if (_won) state = "Solved!";
        else if (!HasMoves() && _moves > 0) state = $"No moves — {_pegsLeft} pegs left";
        else state = "Click a peg, then an empty hole 2 over";
        RetroWidgets.StatusBar(status, state, $"Pegs: {_pegsLeft}   Moves: {_moves}");
    }

    public void Close() { }
}
