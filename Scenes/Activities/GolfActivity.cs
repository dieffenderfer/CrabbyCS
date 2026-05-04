using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Golf solitaire — 7 tableau columns of 5 cards, stock dealt one at a time
/// to a waste pile, move any tableau top to waste if one rank above or below.
/// No wrap (A and K do not connect). Win by clearing all tableau columns.
/// </summary>
public class GolfActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Cols = 7;
    private const int Rows = 5;
    private const int ColSpacing = 8;
    private const int Margin = 20;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + Cols * CardKit.CardW + (Cols - 1) * ColSpacing,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + Rows * CardKit.CascadeY + CardKit.CardH
            + Margin + CardKit.CardH + Margin
            + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }
    public bool UiScaled => true;

    private List<Card>[] _columns = new List<Card>[Cols];
    private List<Card> _stock = new();
    private List<Card> _waste = new();
    private bool _won, _gameOver;
    private readonly Random _rng = new();

    public void Load() => Deal();

    private void Deal()
    {
        var deck = CardKit.NewDeck();
        CardKit.Shuffle(deck, _rng);

        for (int c = 0; c < Cols; c++) _columns[c] = new List<Card>();
        _stock.Clear(); _waste.Clear();
        _won = false; _gameOver = false;

        int idx = 0;
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                var card = deck[idx++];
                card.FaceUp = true;
                _columns[c].Add(card);
            }
        for (; idx < deck.Count; idx++)
        {
            deck[idx].FaceUp = false;
            _stock.Add(deck[idx]);
        }
        // Seed waste with first stock card so first tableau click has a target
        if (_stock.Count > 0)
        {
            var top = _stock[^1]; _stock.RemoveAt(_stock.Count - 1);
            top.FaceUp = true; _waste.Add(top);
        }
    }

    private Vector2 ColumnPos(int col)
    {
        float x = FrameInset + Margin + col * (CardKit.CardW + ColSpacing);
        float y = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
        return new Vector2(x, y);
    }

    private Vector2 StockPos()
    {
        float x = FrameInset + Margin;
        float y = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + Rows * CardKit.CascadeY + CardKit.CardH + Margin;
        return new Vector2(x, y);
    }

    private Vector2 WastePos()
    {
        var s = StockPos();
        return new Vector2(s.X + CardKit.CardW + ColSpacing * 2, s.Y);
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
        int menu = RetroWidgets.MenuBarHitTest(menuBar, new[] { "New" }, local, leftPressed);
        if (menu == 0) Deal();

        if (!leftPressed) return;
        if (_won || _gameOver) return;

        // Stock click: deal one to waste
        if (CardKit.HitTest(local, StockPos()))
        {
            if (_stock.Count > 0)
            {
                var c = _stock[^1]; _stock.RemoveAt(_stock.Count - 1);
                c.FaceUp = true; _waste.Add(c);
                EvaluateEnd();
            }
            return;
        }

        // Tableau click: move top card to waste if adjacent rank
        for (int col = 0; col < Cols; col++)
        {
            if (_columns[col].Count == 0) continue;
            int top = _columns[col].Count - 1;
            var pos = ColumnPos(col) + new Vector2(0, top * CardKit.CascadeY);
            if (!CardKit.HitTest(local, pos)) continue;

            if (_waste.Count == 0) return;
            int wRank = _waste[^1].Rank;
            int cRank = _columns[col][top].Rank;
            if (Math.Abs(wRank - cRank) == 1)
            {
                var moved = _columns[col][top];
                _columns[col].RemoveAt(top);
                _waste.Add(moved);
                EvaluateEnd();
            }
            return;
        }
    }

    private void EvaluateEnd()
    {
        bool empty = true;
        foreach (var col in _columns) if (col.Count > 0) { empty = false; break; }
        if (empty) { _won = true; return; }

        if (_stock.Count > 0) return;
        if (_waste.Count == 0) { _gameOver = true; return; }
        int wRank = _waste[^1].Rank;
        foreach (var col in _columns)
        {
            if (col.Count == 0) continue;
            if (Math.Abs(col[^1].Rank - wRank) == 1) return;
        }
        _gameOver = true;
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Golf", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New" }, -1);

        // Felt background under play area
        float bodyY = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight;
        float bodyH = PanelSize.Y - bodyY - FrameInset - RetroWidgets.StatusBarHeight;
        var felt = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + bodyY,
            PanelSize.X - 2 * FrameInset, bodyH);
        Raylib.DrawRectangleRec(felt, new Color(8, 96, 56, 255));

        // Tableau
        for (int c = 0; c < Cols; c++)
        {
            var basePos = ColumnPos(c);
            var abs = new Vector2(panelOffset.X + basePos.X, panelOffset.Y + basePos.Y);
            if (_columns[c].Count == 0)
            {
                CardKit.DrawEmptySlot(abs);
                continue;
            }
            for (int i = 0; i < _columns[c].Count; i++)
            {
                var p = abs + new Vector2(0, i * CardKit.CascadeY);
                CardKit.DrawCard(_columns[c][i], p);
            }
        }

        // Stock
        var stockAbs = new Vector2(panelOffset.X + StockPos().X, panelOffset.Y + StockPos().Y);
        if (_stock.Count > 0) CardKit.DrawCardBack(stockAbs);
        else CardKit.DrawEmptySlot(stockAbs);

        // Waste
        var wasteAbs = new Vector2(panelOffset.X + WastePos().X, panelOffset.Y + WastePos().Y);
        if (_waste.Count > 0) CardKit.DrawCard(_waste[^1], wasteAbs);
        else CardKit.DrawEmptySlot(wasteAbs);

        // Stock count label
        RetroSkin.DrawText($"Stock: {_stock.Count}",
            (int)(panelOffset.X + StockPos().X),
            (int)(panelOffset.Y + StockPos().Y - 18),
            new Color(255, 255, 255, 220), 16);

        // Status
        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string state = _won ? "You win!" : _gameOver ? "No more moves" : "Playing...";
        RetroWidgets.StatusBar(status, state, $"Stock {_stock.Count}  |  Waste {_waste.Count}");
    }

    public void Close() { }
}
