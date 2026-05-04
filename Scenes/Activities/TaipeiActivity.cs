using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Tile-matching solitaire on a layered board. Two unblocked tiles of the
/// same type can be matched and removed; a tile is blocked if anything sits
/// above it or if both its left and right neighbors on the same layer are
/// occupied. Cleared board wins.
/// </summary>
public class TaipeiActivity : IActivity
{
    private const int FrameInset = 3;
    private const int TileW = 36;
    private const int TileH = 48;
    private const int LayerOffsetX = 4;
    private const int LayerOffsetY = 4;
    private const int Margin = 18;
    // Pyramid layout dims (cells per layer)
    private static readonly (int w, int h)[] LayerSize =
        { (10, 6), (8, 4), (6, 2), (4, 2), (4, 2) };
    private const int TotalTiles = 60 + 32 + 12 + 8 + 8; // = 120

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + LayerSize[0].w * TileW + LayerSize.Length * LayerOffsetX,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + LayerSize[0].h * TileH + LayerSize.Length * LayerOffsetY
            + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private record class Tile(int Layer, int GridX, int GridY, int TypeId);
    private List<Tile> _tiles = new();
    private Tile? _selected;
    private bool _won;
    private string _status = "";
    private readonly Random _rng = new();

    public void Load() => Deal();

    private static int TypeCount => 30; // 9 C + 9 B + 9 W + 3 dragons

    private void Deal()
    {
        _tiles.Clear();
        _selected = null;
        _won = false;
        _status = "";

        // Build pyramid positions
        var positions = new List<(int layer, int x, int y)>();
        int baseW = LayerSize[0].w, baseH = LayerSize[0].h;
        for (int L = 0; L < LayerSize.Length; L++)
        {
            var (w, h) = LayerSize[L];
            int xOff = (baseW - w) / 2;
            int yOff = (baseH - h) / 2;
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    positions.Add((L, xOff + x, yOff + y));
        }

        // Build tile-type bag: 4 of each of 30 types = 120
        var bag = new List<int>(TotalTiles);
        for (int t = 0; t < TypeCount; t++)
            for (int k = 0; k < 4; k++) bag.Add(t);

        // Place pairs in solvable order: pre-sort positions so deeper/blocked
        // tiles get filled first. Simple heuristic: sort by layer desc, then
        // by distance from the open edges. (Not a guaranteed-solvable shuffle —
        // some boards may dead-end. Redeal handles it.)
        positions.Sort((a, b) =>
        {
            int byLayer = b.layer.CompareTo(a.layer);
            if (byLayer != 0) return byLayer;
            return _rng.Next(-1, 2);
        });

        // Shuffle bag, assign to positions
        for (int i = bag.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (bag[i], bag[j]) = (bag[j], bag[i]);
        }

        for (int i = 0; i < positions.Count && i < bag.Count; i++)
        {
            var (L, x, y) = positions[i];
            _tiles.Add(new Tile(L, x, y, bag[i]));
        }
    }

    private bool IsCovered(Tile t)
    {
        foreach (var u in _tiles)
            if (u.Layer == t.Layer + 1 && u.GridX == t.GridX && u.GridY == t.GridY)
                return true;
        return false;
    }

    private bool IsBlockedSides(Tile t)
    {
        bool left = false, right = false;
        foreach (var u in _tiles)
        {
            if (u.Layer != t.Layer || u.GridY != t.GridY) continue;
            if (u.GridX == t.GridX - 1) left = true;
            else if (u.GridX == t.GridX + 1) right = true;
        }
        return left && right;
    }

    private bool IsSelectable(Tile t) => !IsCovered(t) && !IsBlockedSides(t);

    private Vector2 TileScreenPos(Tile t)
    {
        float x = FrameInset + Margin + t.GridX * TileW + t.Layer * LayerOffsetX;
        float y = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + t.GridY * TileH - t.Layer * LayerOffsetY
            + LayerSize.Length * LayerOffsetY;
        return new Vector2(x, y);
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
        int menu = RetroWidgets.MenuBarHitTest(menuBar, new[] { "New", "Hint" }, local, leftPressed);
        if (menu == 0) { Deal(); return; }
        if (menu == 1) { ShowHint(); return; }

        if (!leftPressed || _won) return;

        // Hit-test top-most layer first
        for (int L = LayerSize.Length - 1; L >= 0; L--)
        {
            for (int i = _tiles.Count - 1; i >= 0; i--)
            {
                var t = _tiles[i];
                if (t.Layer != L) continue;
                var p = TileScreenPos(t);
                var rect = new Rectangle(p.X, p.Y, TileW, TileH);
                if (!RetroSkin.PointInRect(local, rect)) continue;

                if (!IsSelectable(t)) { _status = "That tile is blocked."; return; }

                if (_selected == null)
                {
                    _selected = t;
                    _status = "";
                    return;
                }
                if (_selected == t)
                {
                    _selected = null;
                    return;
                }
                if (_selected.TypeId == t.TypeId)
                {
                    _tiles.Remove(_selected);
                    _tiles.Remove(t);
                    _selected = null;
                    if (_tiles.Count == 0) _won = true;
                    return;
                }
                _selected = t;
                _status = "";
                return;
            }
        }
    }

    private void ShowHint()
    {
        var pool = _tiles.Where(IsSelectable).ToList();
        for (int i = 0; i < pool.Count; i++)
            for (int j = i + 1; j < pool.Count; j++)
                if (pool[i].TypeId == pool[j].TypeId)
                {
                    _selected = pool[i];
                    _status = $"Pair available: {pool.Count} free tiles";
                    return;
                }
        _status = "No pairs left — try Redeal";
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Taipei", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", "Hint" }, -1);

        // Felt
        float bodyY = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight;
        float bodyH = PanelSize.Y - bodyY - FrameInset - RetroWidgets.StatusBarHeight;
        Raylib.DrawRectangleRec(new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + bodyY,
            PanelSize.X - 2 * FrameInset, bodyH), new Color(20, 80, 50, 255));

        // Tiles back-to-front
        var ordered = _tiles.OrderBy(t => t.Layer)
                            .ThenBy(t => t.GridY)
                            .ThenBy(t => t.GridX);
        foreach (var t in ordered)
        {
            var p = TileScreenPos(t);
            var abs = new Vector2(panelOffset.X + p.X, panelOffset.Y + p.Y);
            DrawTile(abs, t);
        }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string stateMsg = _won ? "Cleared!" : !string.IsNullOrEmpty(_status) ? _status : "Pick two free matching tiles";
        RetroWidgets.StatusBar(status, stateMsg, $"{_tiles.Count} tiles left");
    }

    private void DrawTile(Vector2 pos, Tile t)
    {
        var rect = new Rectangle(pos.X, pos.Y, TileW, TileH);
        // Drop shadow under floating tile
        Raylib.DrawRectangle((int)pos.X + 2, (int)pos.Y + 3, TileW, TileH, new Color(0, 0, 0, 80));
        Raylib.DrawRectangleRec(rect, new Color(245, 230, 200, 255));
        // Bevel: light top/left, dark bottom/right
        int x = (int)pos.X, y = (int)pos.Y;
        Raylib.DrawRectangle(x, y, TileW, 2, new Color(255, 245, 220, 255));
        Raylib.DrawRectangle(x, y, 2, TileH, new Color(255, 245, 220, 255));
        Raylib.DrawRectangle(x, y + TileH - 2, TileW, 2, new Color(160, 140, 100, 255));
        Raylib.DrawRectangle(x + TileW - 2, y, 2, TileH, new Color(160, 140, 100, 255));
        Raylib.DrawRectangleLines(x, y, TileW, TileH, RetroSkin.BodyText);

        // Highlight if selectable
        if (!IsSelectable(t))
        {
            Raylib.DrawRectangleRec(rect, new Color(0, 0, 0, 60));
        }
        if (_selected == t)
        {
            Raylib.DrawRectangleLinesEx(new Rectangle(x, y, TileW, TileH), 2, new Color(255, 200, 0, 255));
        }

        DrawTileGlyph(t.TypeId, x + TileW / 2, y + TileH / 2);
    }

    private static void DrawTileGlyph(int typeId, int cx, int cy)
    {
        // 0..8 = circles 1..9 (red), 9..17 = bamboos 1..9 (green),
        // 18..26 = numbers 1..9 (blue), 27 R-dragon, 28 G-dragon, 29 W-dragon
        if (typeId < 9)
        {
            int n = typeId + 1;
            DrawSymbolStack(cx, cy, n, "C", new Color(192, 32, 32, 255));
        }
        else if (typeId < 18)
        {
            int n = typeId - 9 + 1;
            DrawSymbolStack(cx, cy, n, "B", new Color(32, 144, 64, 255));
        }
        else if (typeId < 27)
        {
            int n = typeId - 18 + 1;
            DrawSymbolStack(cx, cy, n, "W", new Color(32, 64, 192, 255));
        }
        else
        {
            string label = typeId == 27 ? "R" : typeId == 28 ? "G" : "W";
            var col = typeId == 27 ? new Color(208, 0, 0, 255)
                    : typeId == 28 ? new Color(0, 144, 0, 255)
                    : new Color(0, 0, 0, 255);
            int w = RetroSkin.MeasureText(label, 24);
            RetroSkin.DrawText(label, cx - w / 2, cy - 14, col, 24);
        }
    }

    private static void DrawSymbolStack(int cx, int cy, int n, string suit, Color col)
    {
        // Number on top, suit letter below
        string num = n.ToString();
        int w = RetroSkin.MeasureText(num, 18);
        RetroSkin.DrawText(num, cx - w / 2, cy - 18, col, 18);
        int sw = RetroSkin.MeasureText(suit, 14);
        RetroSkin.DrawText(suit, cx - sw / 2, cy + 2, col, 14);
    }

    public void Close() { }
}
