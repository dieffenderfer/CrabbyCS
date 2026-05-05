using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Pyramid solitaire — 28-card pyramid (rows of 1..7), remaining 24 in stock.
/// Match any two exposed cards whose ranks sum to 13; Kings remove alone.
/// (A=1, J=11, Q=12, K=13.) Win when the pyramid is cleared.
/// </summary>
public class TutsTombActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Margin = 18;
    private const int Rows = 7;
    private const int RowGap = 22;       // vertical step between pyramid rows
    private const int ColStep = 30;      // horizontal step between sibling cards in a row

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + Rows * (CardKit.CardW + 2),
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + Rows * RowGap + CardKit.CardH
            + Margin + CardKit.CardH + Margin
            + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private readonly RetroHelp _help = new()
    {
        Title = "Tut's Tomb — How to play",
        Lines = new[]
        {
            "Clear the 28-card pyramid to win.",
            "Tap any pair of exposed cards whose ranks add to 13.",
            "(Ace=1, Jack=11, Queen=12.) Kings remove alone.",
            "A card is exposed only if no card overlaps below it.",
            "Click stock to deal a new card to the waste pile,",
            "which can pair with any pyramid card.",
        },
    };

    private record class Slot(int Row, int Col) { public Card? Card; }
    private List<Slot> _pyramid = new();
    private List<Card> _stock = new();
    private List<Card> _waste = new();
    private bool _won;
    private Slot? _selectedSlot;
    private bool _selectedWaste;
    private readonly Random _rng = new();

    public void Load() => Deal();

    private void Deal()
    {
        var deck = CardKit.NewDeck();
        CardKit.Shuffle(deck, _rng);
        foreach (var c in deck) c.FaceUp = true;

        _pyramid.Clear();
        _stock.Clear();
        _waste.Clear();
        _selectedSlot = null;
        _selectedWaste = false;
        _won = false;

        int idx = 0;
        for (int r = 0; r < Rows; r++)
            for (int col = 0; col <= r; col++)
                _pyramid.Add(new Slot(r, col) { Card = deck[idx++] });

        for (; idx < deck.Count; idx++) _stock.Add(deck[idx]);
    }

    private bool IsExposed(Slot s)
    {
        if (s.Card == null) return false;
        if (s.Row == Rows - 1) return true;
        var lo = _pyramid.Find(x => x.Row == s.Row + 1 && x.Col == s.Col);
        var hi = _pyramid.Find(x => x.Row == s.Row + 1 && x.Col == s.Col + 1);
        return (lo?.Card == null) && (hi?.Card == null);
    }

    private Vector2 PyramidPos(Slot s)
    {
        float topY = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
        float rowWidth = (s.Row + 1) * CardKit.CardW + s.Row * (ColStep - CardKit.CardW);
        // Center each row within the body
        float bodyX = FrameInset + (PanelSize.X - 2 * FrameInset) / 2f;
        float startX = bodyX - (s.Row + 1) * (CardKit.CardW + 2) / 2f + 1;
        return new Vector2(startX + s.Col * (CardKit.CardW + 2), topY + s.Row * RowGap);
    }

    private Vector2 StockPos()
    {
        float y = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + Rows * RowGap + CardKit.CardH + Margin;
        return new Vector2(FrameInset + Margin, y);
    }

    private Vector2 WastePos()
    {
        var s = StockPos();
        return new Vector2(s.X + CardKit.CardW + 16, s.Y);
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
        switch (RetroWidgets.MenuBarHitTest(menuBar, new[] { "New", "Help" }, local, leftPressed))
        {
            case 0: Deal(); return;
            case 1: _help.Visible = !_help.Visible; return;
        }
        if (_help.HandleInput(local, leftPressed, PanelSize)) return;

        if (!leftPressed || _won) return;

        // Stock click
        if (RetroSkin.PointInRect(local, new Rectangle(StockPos().X, StockPos().Y, CardKit.CardW, CardKit.CardH)))
        {
            if (_stock.Count > 0) { var c = _stock[^1]; _stock.RemoveAt(_stock.Count - 1); _waste.Add(c); }
            else { _stock = new List<Card>(_waste); _stock.Reverse(); _waste.Clear(); }
            _selectedSlot = null; _selectedWaste = false;
            return;
        }

        // Waste click
        if (_waste.Count > 0 &&
            RetroSkin.PointInRect(local, new Rectangle(WastePos().X, WastePos().Y, CardKit.CardW, CardKit.CardH)))
        {
            HandleSelect(null, true);
            return;
        }

        // Pyramid click — top-down so deeper rows hit first when overlapping
        for (int r = Rows - 1; r >= 0; r--)
        {
            foreach (var s in _pyramid)
            {
                if (s.Row != r || s.Card == null) continue;
                var p = PyramidPos(s);
                if (RetroSkin.PointInRect(local, new Rectangle(p.X, p.Y, CardKit.CardW, CardKit.CardH)))
                {
                    if (!IsExposed(s)) return;
                    HandleSelect(s, false);
                    return;
                }
            }
        }
    }

    private void HandleSelect(Slot? slot, bool isWaste)
    {
        Card? card = isWaste ? _waste[^1] : slot?.Card;
        if (card == null) return;

        // King removes immediately
        if (card.Rank == 13)
        {
            if (isWaste) _waste.RemoveAt(_waste.Count - 1);
            else slot!.Card = null;
            CheckWin();
            _selectedSlot = null; _selectedWaste = false;
            return;
        }

        if (_selectedSlot == null && !_selectedWaste)
        {
            _selectedSlot = slot; _selectedWaste = isWaste;
            return;
        }

        Card? other = _selectedWaste ? _waste[^1] : _selectedSlot?.Card;
        if (other == null) { _selectedSlot = slot; _selectedWaste = isWaste; return; }
        if (other == card) { _selectedSlot = null; _selectedWaste = false; return; }

        if (other.Rank + card.Rank == 13)
        {
            if (_selectedWaste) _waste.RemoveAt(_waste.Count - 1);
            else _selectedSlot!.Card = null;
            if (isWaste) _waste.RemoveAt(_waste.Count - 1);
            else slot!.Card = null;
            CheckWin();
        }
        _selectedSlot = null; _selectedWaste = false;
    }

    private void CheckWin()
    {
        foreach (var s in _pyramid) if (s.Card != null) return;
        _won = true;
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Tut's Tomb", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", "Help" }, -1);

        float bodyY = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight;
        float bodyH = PanelSize.Y - bodyY - FrameInset - RetroWidgets.StatusBarHeight;
        Raylib.DrawRectangleRec(new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + bodyY,
            PanelSize.X - 2 * FrameInset, bodyH), new Color(168, 132, 64, 255));

        // Pyramid (back rows first, but they overlap so we still draw row-major)
        foreach (var s in _pyramid)
        {
            if (s.Card == null) continue;
            var p = PyramidPos(s);
            var abs = new Vector2(panelOffset.X + p.X, panelOffset.Y + p.Y);
            CardKit.DrawCard(s.Card, abs);
            if (!IsExposed(s))
                Raylib.DrawRectangle((int)abs.X, (int)abs.Y, CardKit.CardW, CardKit.CardH, new Color(0, 0, 0, 80));
            if (_selectedSlot == s)
                Raylib.DrawRectangleLinesEx(new Rectangle(abs.X - 2, abs.Y - 2, CardKit.CardW + 4, CardKit.CardH + 4), 2, new Color(255, 220, 0, 255));
        }

        // Stock + waste
        var stockAbs = new Vector2(panelOffset.X + StockPos().X, panelOffset.Y + StockPos().Y);
        if (_stock.Count > 0) CardKit.DrawCardBack(stockAbs);
        else CardKit.DrawEmptySlot(stockAbs);

        var wasteAbs = new Vector2(panelOffset.X + WastePos().X, panelOffset.Y + WastePos().Y);
        if (_waste.Count > 0)
        {
            CardKit.DrawCard(_waste[^1], wasteAbs);
            if (_selectedWaste)
                Raylib.DrawRectangleLinesEx(new Rectangle(wasteAbs.X - 2, wasteAbs.Y - 2, CardKit.CardW + 4, CardKit.CardH + 4), 2, new Color(255, 220, 0, 255));
        }
        else CardKit.DrawEmptySlot(wasteAbs);

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        int left = _pyramid.Count(s => s.Card != null);
        string state = _won ? "Pyramid cleared!" : "Pair to 13   |   Kings alone";
        RetroWidgets.StatusBar(status, state, $"Pyramid: {left}   Stock: {_stock.Count}");

        _help.Draw(panelOffset, PanelSize);
    }

    public void Close() { }
}
