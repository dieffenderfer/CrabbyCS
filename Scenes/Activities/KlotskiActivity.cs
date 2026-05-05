using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Klotski — sliding-block puzzle on a 4×5 board. Slide the 2×2 block to the
/// bottom-center exit. Click a piece, then click an adjacent empty cell to
/// slide it one step in that direction.
/// </summary>
public class KlotskiActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Cols = 4;
    private const int Rows = 5;
    private const int Cell = 56;
    private const int Margin = 24;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + Cols * Cell,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + Rows * Cell + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private readonly RetroHelp _help = new()
    {
        Title = "Klotski — How to play",
        Lines = new[]
        {
            "Slide the big red 2x2 block out the bottom exit.",
            "Click any block to select, then click toward an",
            "empty cell to slide it one step that direction.",
            "Smaller blocks shuffle to make room.",
            "Solve by parking the big block over the exit.",
        },
        DiagramHeight = 56,
        Diagram = r =>
        {
            // Show one of each piece type.
            int u = 22;
            int gap = 10;
            int totalW = 2 * u + gap + u + gap + 2 * u + gap + u;
            int x0 = (int)(r.X + (r.Width - totalW) / 2);
            int y0 = (int)(r.Y + 4);
            DrawSamplePiece(x0, y0, 2 * u, 2 * u, new Color(180, 32, 32, 255), "★");
            DrawSamplePiece(x0 + 2 * u + gap, y0, u, 2 * u, new Color(64, 96, 160, 255), "");
            DrawSamplePiece(x0 + 3 * u + 2 * gap, y0, 2 * u, u, new Color(96, 144, 64, 255), "");
            DrawSamplePiece(x0 + 5 * u + 3 * gap, y0, u, u, new Color(192, 144, 32, 255), "");
        },
    };

    private static void DrawSamplePiece(int x, int y, int w, int h, Color col, string label)
    {
        var rect = new Rectangle(x, y, w, h);
        Raylib.DrawRectangleRec(rect, col);
        Raylib.DrawRectangle(x, y, w, 2, new Color(255, 255, 255, 120));
        Raylib.DrawRectangle(x, y, 2, h, new Color(255, 255, 255, 120));
        Raylib.DrawRectangle(x, y + h - 2, w, 2, new Color(0, 0, 0, 120));
        Raylib.DrawRectangle(x + w - 2, y, 2, h, new Color(0, 0, 0, 120));
        if (label.Length > 0)
            RetroSkin.DrawText(label, x + w / 2 - 6, y + h / 2 - 8, new Color(255, 255, 200, 255), 16);
    }

    private record class Piece(int W, int H, Color Col, bool IsGoal) { public int X; public int Y; }
    private List<Piece> _pieces = new();
    private int _selected = -1;
    private int _moves;
    private bool _won;

    public void Load() => Reset();

    private void Reset()
    {
        _pieces.Clear();
        // Classic Hua Rong Dao layout
        Piece big = new(2, 2, new Color(180, 32, 32, 255), true) { X = 1, Y = 0 };
        _pieces.Add(big);
        // 4 vertical soldiers
        _pieces.Add(new(1, 2, new Color(64, 96, 160, 255), false) { X = 0, Y = 0 });
        _pieces.Add(new(1, 2, new Color(64, 96, 160, 255), false) { X = 3, Y = 0 });
        _pieces.Add(new(1, 2, new Color(64, 96, 160, 255), false) { X = 0, Y = 2 });
        _pieces.Add(new(1, 2, new Color(64, 96, 160, 255), false) { X = 3, Y = 2 });
        // Horizontal beam
        _pieces.Add(new(2, 1, new Color(96, 144, 64, 255), false) { X = 1, Y = 2 });
        // 4 single soldiers
        _pieces.Add(new(1, 1, new Color(192, 144, 32, 255), false) { X = 1, Y = 3 });
        _pieces.Add(new(1, 1, new Color(192, 144, 32, 255), false) { X = 2, Y = 3 });
        _pieces.Add(new(1, 1, new Color(192, 144, 32, 255), false) { X = 0, Y = 4 });
        _pieces.Add(new(1, 1, new Color(192, 144, 32, 255), false) { X = 3, Y = 4 });

        _selected = -1;
        _moves = 0;
        _won = false;
    }

    private bool IsOccupied(int x, int y, int ignore = -1)
    {
        for (int i = 0; i < _pieces.Count; i++)
        {
            if (i == ignore) continue;
            var p = _pieces[i];
            if (x >= p.X && x < p.X + p.W && y >= p.Y && y < p.Y + p.H) return true;
        }
        return false;
    }

    private bool TrySlide(int idx, int dx, int dy)
    {
        var p = _pieces[idx];
        int nx = p.X + dx, ny = p.Y + dy;
        if (nx < 0 || ny < 0 || nx + p.W > Cols || ny + p.H > Rows) return false;
        for (int xx = 0; xx < p.W; xx++)
            for (int yy = 0; yy < p.H; yy++)
                if (IsOccupied(nx + xx, ny + yy, idx)) return false;
        p.X = nx; p.Y = ny;
        _moves++;
        if (p.IsGoal && p.X == 1 && p.Y == 3) _won = true;
        return true;
    }

    private Vector2 BoardOrigin() => new(
        FrameInset + Margin,
        FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin);

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
        switch (RetroWidgets.MenuBarHitTest(menuBar, new[] { "New", "Help" }, local, leftPressed))
        {
            case 0: Reset(); return;
            case 1: _help.Visible = !_help.Visible; return;
        }
        if (_help.HandleInput(local, leftPressed, PanelSize)) return;

        if (!leftPressed || _won) return;

        var origin = BoardOrigin();
        int gx = (int)((local.X - origin.X) / Cell);
        int gy = (int)((local.Y - origin.Y) / Cell);
        if (local.X < origin.X || local.Y < origin.Y) return;
        if (gx < 0 || gx >= Cols || gy < 0 || gy >= Rows) { _selected = -1; return; }

        // Find piece at (gx, gy)
        int hit = -1;
        for (int i = 0; i < _pieces.Count; i++)
        {
            var p = _pieces[i];
            if (gx >= p.X && gx < p.X + p.W && gy >= p.Y && gy < p.Y + p.H) { hit = i; break; }
        }

        if (_selected == -1)
        {
            if (hit >= 0) _selected = hit;
            return;
        }

        // Selected piece — clicking the piece itself just re-selects;
        // clicking an empty cell tries to slide toward it.
        if (hit == _selected) return;
        if (hit >= 0) { _selected = hit; return; }

        // Determine slide direction from selected to (gx, gy)
        var sel = _pieces[_selected];
        int sx = sel.X + sel.W / 2;
        int sy = sel.Y + sel.H / 2;
        int ddx = Math.Sign(gx - sx);
        int ddy = Math.Sign(gy - sy);
        if (Math.Abs(gx - sx) >= Math.Abs(gy - sy)) ddy = 0; else ddx = 0;
        if (ddx == 0 && ddy == 0) return;
        TrySlide(_selected, ddx, ddy);
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Klotski", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", "Help" }, -1);

        var origin = BoardOrigin();
        var bx = panelOffset.X + origin.X;
        var by = panelOffset.Y + origin.Y;

        var board = new Rectangle(bx - 4, by - 4, Cols * Cell + 8, Rows * Cell + 8);
        RetroSkin.DrawSunken(board, new Color(72, 56, 40, 255));

        // Exit indicator (bottom row 4 cols 1-2)
        Raylib.DrawRectangle((int)(bx + Cell), (int)(by + 4 * Cell),
            2 * Cell, Cell, new Color(0, 0, 0, 80));
        RetroSkin.DrawText("EXIT",
            (int)(bx + Cell + 12), (int)(by + 4 * Cell + Cell / 2 - 8),
            new Color(220, 220, 220, 200), 14);

        for (int i = 0; i < _pieces.Count; i++)
        {
            var p = _pieces[i];
            var rect = new Rectangle(bx + p.X * Cell + 2, by + p.Y * Cell + 2,
                p.W * Cell - 4, p.H * Cell - 4);
            Raylib.DrawRectangleRec(rect, p.Col);
            // Bevel
            Raylib.DrawRectangle((int)rect.X, (int)rect.Y, (int)rect.Width, 2, new Color(255, 255, 255, 120));
            Raylib.DrawRectangle((int)rect.X, (int)rect.Y, 2, (int)rect.Height, new Color(255, 255, 255, 120));
            Raylib.DrawRectangle((int)rect.X, (int)(rect.Y + rect.Height - 2), (int)rect.Width, 2, new Color(0, 0, 0, 120));
            Raylib.DrawRectangle((int)(rect.X + rect.Width - 2), (int)rect.Y, 2, (int)rect.Height, new Color(0, 0, 0, 120));

            if (p.IsGoal)
            {
                int cx = (int)(rect.X + rect.Width / 2);
                int cy = (int)(rect.Y + rect.Height / 2);
                RetroSkin.DrawText("★", cx - 8, cy - 12, new Color(255, 255, 200, 255), 24);
            }

            if (i == _selected)
                Raylib.DrawRectangleLinesEx(rect, 3, new Color(255, 220, 0, 255));
        }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string state = _won ? "Solved!" : "Click a piece, click toward an empty cell";
        RetroWidgets.StatusBar(status, state, $"Moves: {_moves}");

        _help.Draw(panelOffset, PanelSize);
    }

    public void Close() { }
}
