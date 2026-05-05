using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Jigsaw puzzle. The image is generated procedurally (so it repaints under
/// any RetroTheme), cut into a 4×3 grid, and scrambled around the panel
/// edges. Click a piece to pick it up, click a slot to place it.
/// </summary>
public class JigsawedActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Cols = 4;
    private const int Rows = 3;
    private const int PieceSize = 64;
    private const int Margin = 16;
    private const int TrayPad = 8;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + Cols * PieceSize + 2 * (PieceSize + TrayPad),
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + Rows * PieceSize + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private record class Piece(int CorrectIdx)
    {
        public int Slot;        // -1 = in tray
        public int TrayIdx;     // when in tray
    }

    private List<Piece> _pieces = new();
    private int _picked = -1;
    private bool _won;
    private readonly Random _rng = new();

    public void Load() => Reset();

    private void Reset()
    {
        _pieces = new List<Piece>();
        int total = Cols * Rows;
        for (int i = 0; i < total; i++) _pieces.Add(new Piece(i) { Slot = -1, TrayIdx = i });
        // Shuffle tray positions
        for (int i = _pieces.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (_pieces[i].TrayIdx, _pieces[j].TrayIdx) = (_pieces[j].TrayIdx, _pieces[i].TrayIdx);
        }
        _picked = -1;
        _won = false;
    }

    private Vector2 SlotPos(int idx)
    {
        int x = idx % Cols, y = idx / Cols;
        return new Vector2(
            FrameInset + Margin + x * PieceSize,
            FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin + y * PieceSize);
    }

    private Vector2 TrayPos(int trayIdx)
    {
        // 12 tray slots: 6 down right side, 6 down left side wraps to right column
        int rightCol = Cols * PieceSize + Margin + TrayPad;
        int colN = trayIdx / 6;
        int rowN = trayIdx % 6;
        return new Vector2(
            FrameInset + Margin + rightCol + colN * (PieceSize + TrayPad),
            FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin + rowN * (PieceSize / 2));
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

        // Click on a piece (board first, then tray)
        for (int i = 0; i < _pieces.Count; i++)
        {
            var p = _pieces[i];
            if (p.Slot < 0) continue;
            var pos = SlotPos(p.Slot);
            if (RetroSkin.PointInRect(local, new Rectangle(pos.X, pos.Y, PieceSize, PieceSize)))
            { _picked = i; return; }
        }
        for (int i = 0; i < _pieces.Count; i++)
        {
            var p = _pieces[i];
            if (p.Slot >= 0) continue;
            var pos = TrayPos(p.TrayIdx);
            if (RetroSkin.PointInRect(local, new Rectangle(pos.X, pos.Y, PieceSize, PieceSize)))
            { _picked = i; return; }
        }

        // Click on board slot to drop
        if (_picked >= 0)
        {
            for (int s = 0; s < Cols * Rows; s++)
            {
                var pos = SlotPos(s);
                if (!RetroSkin.PointInRect(local, new Rectangle(pos.X, pos.Y, PieceSize, PieceSize))) continue;
                // Swap whatever's in s with picked
                var picked = _pieces[_picked];
                Piece? occupant = null;
                foreach (var q in _pieces) if (q.Slot == s) { occupant = q; break; }
                int prevSlot = picked.Slot;
                int prevTray = picked.TrayIdx;
                picked.Slot = s;
                if (occupant != null)
                {
                    occupant.Slot = prevSlot;
                    if (prevSlot < 0) occupant.TrayIdx = prevTray;
                }
                _picked = -1;
                CheckWin();
                return;
            }
        }
    }

    private void CheckWin()
    {
        foreach (var p in _pieces) if (p.Slot != p.CorrectIdx) return;
        _won = true;
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Jigsawed", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New" }, -1);

        // Board grid sunken
        var board = new Rectangle(
            panelOffset.X + FrameInset + Margin - 3,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin - 3,
            Cols * PieceSize + 6, Rows * PieceSize + 6);
        RetroSkin.DrawSunken(board, RetroSkin.Face);

        // Slots outline
        for (int s = 0; s < Cols * Rows; s++)
        {
            var pos = SlotPos(s);
            Raylib.DrawRectangleLines(
                (int)(panelOffset.X + pos.X), (int)(panelOffset.Y + pos.Y),
                PieceSize, PieceSize, RetroSkin.Shadow);
        }

        // Pieces
        foreach (var p in _pieces)
        {
            Vector2 pos;
            int drawSize;
            if (p.Slot >= 0) { pos = SlotPos(p.Slot); drawSize = PieceSize; }
            else { pos = TrayPos(p.TrayIdx); drawSize = PieceSize / 2; }

            var abs = new Vector2(panelOffset.X + pos.X, panelOffset.Y + pos.Y);
            DrawPieceArt(abs, drawSize, p.CorrectIdx);

            if (_pieces.IndexOf(p) == _picked)
                Raylib.DrawRectangleLinesEx(new Rectangle(abs.X - 2, abs.Y - 2, drawSize + 4, drawSize + 4),
                    2, new Color(255, 220, 0, 255));
        }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        int placed = _pieces.Count(p => p.Slot == p.CorrectIdx);
        string state = _won ? "Solved!" : "Click a piece, click a slot";
        RetroWidgets.StatusBar(status, state, $"{placed}/{_pieces.Count} placed");
    }

    /// <summary>Procedural piece art — a slice of a colorful gradient + circles.</summary>
    private static void DrawPieceArt(Vector2 pos, int size, int idx)
    {
        int cx = idx % Cols, cy = idx / Cols;
        // Each piece pulls a region from a virtual canvas
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x += 2)
            {
                float u = (cx * PieceSize + x) / (float)(Cols * PieceSize);
                float v = (cy * PieceSize + y) / (float)(Rows * PieceSize);
                byte r = (byte)(64 + 191 * u);
                byte g = (byte)(64 + 191 * v);
                byte b = (byte)(96 + 159 * (1 - u * v));
                Raylib.DrawRectangle((int)pos.X + x, (int)pos.Y + y, 2, 1, new Color(r, g, b, (byte)255));
            }
        // Decorative circles per region
        var dotCol = new Color((byte)(255 - cx * 60), (byte)(160 + cy * 30), (byte)(80 + (cx + cy) * 30), (byte)220);
        Raylib.DrawCircle((int)pos.X + size / 2, (int)pos.Y + size / 2, size / 4, dotCol);
        Raylib.DrawRectangleLines((int)pos.X, (int)pos.Y, size, size, RetroSkin.BodyText);
    }

    public void Close() { }
}
