using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// TriPeaks — three pyramid peaks of 28 cards. Remove an exposed card by
/// playing it onto the waste pile if its rank is one above or below the
/// current waste top (Ace and King wrap). Stock deals new waste cards.
/// </summary>
public class TriPeaksActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Margin = 18;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + 10 * (CardKit.CardW + 2),
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + 4 * (CardKit.CardH / 2 + 8) + CardKit.CardH
            + Margin + CardKit.CardH
            + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private readonly RetroHelp _help = new()
    {
        Title = "TriPeaks — How to play",
        Lines = new[]
        {
            "Clear the three peaks of cards.",
            "Tap any exposed peak card whose rank is exactly",
            "one above OR one below the waste-pile top.",
            "Ace and King wrap around (A↔K is a legal move).",
            "Click stock to deal a fresh waste card when stuck.",
            "Streaks compound your score per card cleared.",
        },
    };

    private record class Slot(int Row, int Col) { public Card? Card; public List<Slot> Covers = new(); }
    private List<Slot> _slots = new();
    private List<Card> _stock = new();
    private List<Card> _waste = new();
    private bool _won;
    private int _score;
    private int _streak;
    private readonly Random _rng = new();

    public void Load() => Deal();

    private void Deal()
    {
        _slots.Clear();
        _stock.Clear();
        _waste.Clear();
        _won = false;
        _score = 0;
        _streak = 0;

        var deck = CardKit.NewDeck();
        CardKit.Shuffle(deck, _rng);
        foreach (var c in deck) c.FaceUp = true;

        // Build TriPeaks layout: rows 0..2 have peak triplets (1, 2, 3 cards
        // per peak respectively), row 3 has 10 continuous cards across the bottom.
        // Simplified: place rows by (row, col) where col is in a virtual ladder.
        int idx = 0;
        // Row 0 (peak tops) — columns 1, 5, 9 in a 12-wide virtual grid
        foreach (int c in new[] { 1, 5, 9 }) _slots.Add(new Slot(0, c) { Card = deck[idx++] });
        // Row 1 — two cards under each peak (columns 0/2, 4/6, 8/10)
        foreach (int c in new[] { 0, 2, 4, 6, 8, 10 }) _slots.Add(new Slot(1, c) { Card = deck[idx++] });
        // Row 2 — three cards per peak
        foreach (int c in new[] { -1, 1, 3, 4, 6, 8, 9, 11, 13 }.Take(9))
            _slots.Add(new Slot(2, c) { Card = deck[idx++] });
        // Row 3 — base of 10 cards
        for (int c = 0; c < 10; c++) _slots.Add(new Slot(3, c) { Card = deck[idx++] });

        // Compute covers (cards that block this slot from being playable).
        foreach (var s in _slots)
        {
            // A slot is covered by the two cards immediately below it (row+1, col-1) and (row+1, col)
            foreach (var o in _slots)
                if (o.Row == s.Row + 1 && (o.Col == s.Col || o.Col == s.Col + 1))
                    s.Covers.Add(o);
        }

        for (; idx < deck.Count; idx++) _stock.Add(deck[idx]);
        if (_stock.Count > 0) { var t = _stock[^1]; _stock.RemoveAt(_stock.Count - 1); _waste.Add(t); }
    }

    private bool IsExposed(Slot s)
    {
        if (s.Card == null) return false;
        foreach (var c in s.Covers) if (c.Card != null) return false;
        return true;
    }

    private bool ValidPlay(Card on, Card top)
    {
        int diff = Math.Abs(on.Rank - top.Rank);
        return diff == 1 || diff == 12; // K(13) ↔ A(1) wrap
    }

    private Vector2 SlotPos(Slot s)
    {
        // Each row of cards centers within 12 virtual columns spanning the body
        float bx = FrameInset + Margin;
        float colW = (CardKit.CardW + 2) * 0.75f;
        float rowY = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin
            + s.Row * (CardKit.CardH / 2 + 6);
        return new Vector2(bx + s.Col * colW + 6, rowY);
    }

    private Vector2 StockPos()
    {
        float y = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + 4 * (CardKit.CardH / 2 + 8) + CardKit.CardH + Margin;
        return new Vector2(FrameInset + Margin + 40, y);
    }

    private Vector2 WastePos()
    {
        var s = StockPos();
        return new Vector2(s.X + CardKit.CardW + 24, s.Y);
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
            if (_stock.Count > 0)
            {
                var c = _stock[^1]; _stock.RemoveAt(_stock.Count - 1);
                _waste.Add(c);
                _streak = 0;
            }
            return;
        }

        if (_waste.Count == 0) return;
        var top = _waste[^1];

        // Click peak slots
        for (int r = 3; r >= 0; r--)
        {
            foreach (var s in _slots)
            {
                if (s.Row != r || s.Card == null || !IsExposed(s)) continue;
                var p = SlotPos(s);
                if (!RetroSkin.PointInRect(local, new Rectangle(p.X, p.Y, CardKit.CardW, CardKit.CardH))) continue;
                if (!ValidPlay(s.Card, top)) return;
                _waste.Add(s.Card);
                s.Card = null;
                _streak++;
                _score += 5 * _streak;
                if (_slots.All(x => x.Card == null)) { _won = true; _score += 100; }
                return;
            }
        }
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "TriPeaks", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", "Help" }, -1);

        float bodyY = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight;
        float bodyH = PanelSize.Y - bodyY - FrameInset - RetroWidgets.StatusBarHeight;
        Raylib.DrawRectangleRec(new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + bodyY,
            PanelSize.X - 2 * FrameInset, bodyH), new Color(8, 96, 56, 255));

        // Slots — back rows first
        for (int r = 0; r <= 3; r++)
        {
            foreach (var s in _slots)
            {
                if (s.Row != r) continue;
                var p = SlotPos(s);
                var abs = new Vector2(panelOffset.X + p.X, panelOffset.Y + p.Y);
                if (s.Card == null) continue;
                if (IsExposed(s)) CardKit.DrawCard(s.Card, abs);
                else CardKit.DrawCardBack(abs);
            }
        }

        // Stock + waste
        var stockAbs = new Vector2(panelOffset.X + StockPos().X, panelOffset.Y + StockPos().Y);
        if (_stock.Count > 0) CardKit.DrawCardBack(stockAbs);
        else CardKit.DrawEmptySlot(stockAbs);

        var wasteAbs = new Vector2(panelOffset.X + WastePos().X, panelOffset.Y + WastePos().Y);
        if (_waste.Count > 0) CardKit.DrawCard(_waste[^1], wasteAbs);
        else CardKit.DrawEmptySlot(wasteAbs);

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string state = _won ? "Cleared!" : "Play 1 above or below the waste top (A and K wrap)";
        RetroWidgets.StatusBar(status, state, $"Score: {_score}   Streak: {_streak}   Stock: {_stock.Count}");

        _help.Draw(panelOffset, PanelSize);
    }

    public void Close() { }
}
