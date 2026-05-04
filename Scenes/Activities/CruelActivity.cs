using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Cruel solitaire — Aces seed four foundations, the remaining 48 cards deal
/// into 12 piles of 4 face-up. Move top of any pile to a foundation (next rank
/// up by suit) or onto another pile's top (one rank lower, same suit).
/// "Redeal" picks up the piles in order and re-deals without shuffling.
/// </summary>
public class CruelActivity : IActivity
{
    private const int FrameInset = 3;
    private const int TableauCols = 12;
    private const int InitialPileSize = 4;
    private const int Margin = 18;
    private const int ColSpacing = 6;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + TableauCols * CardKit.CardW + (TableauCols - 1) * ColSpacing,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + CardKit.CardH                          // foundations row
            + Margin + InitialPileSize * CardKit.CascadeY + CardKit.CardH
            + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private readonly List<Card>[] _foundations = new List<Card>[4];
    private List<List<Card>> _piles = new();
    private bool _won;
    private int _redealCount;
    private readonly Random _rng = new();

    public void Load()
    {
        for (int i = 0; i < 4; i++) _foundations[i] = new List<Card>();
        Deal();
    }

    private void Deal()
    {
        var deck = CardKit.NewDeck();
        CardKit.Shuffle(deck, _rng);

        for (int i = 0; i < 4; i++) _foundations[i].Clear();
        _piles = new List<List<Card>>();
        for (int i = 0; i < TableauCols; i++) _piles.Add(new List<Card>());
        _won = false;
        _redealCount = 0;

        // Aces seed foundations
        var leftover = new List<Card>();
        foreach (var c in deck)
        {
            if (c.Rank == 1)
            {
                c.FaceUp = true;
                _foundations[(int)c.Suit].Add(c);
            }
            else
            {
                c.FaceUp = true;
                leftover.Add(c);
            }
        }

        DealIntoPiles(leftover);
    }

    private void DealIntoPiles(List<Card> source)
    {
        for (int i = 0; i < TableauCols; i++) _piles[i].Clear();
        for (int i = 0; i < source.Count; i++)
        {
            int col = i / InitialPileSize;
            if (col >= TableauCols) col = TableauCols - 1;
            _piles[col].Add(source[i]);
        }
    }

    /// <summary>Pick up piles left-to-right, bottom-to-top, redeal in order.</summary>
    private void Redeal()
    {
        var collected = new List<Card>();
        foreach (var p in _piles) collected.AddRange(p);
        _redealCount++;
        DealIntoPiles(collected);
    }

    private Vector2 FoundationPos(int i)
    {
        float x = FrameInset + Margin + i * (CardKit.CardW + ColSpacing);
        float y = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
        return new Vector2(x, y);
    }

    private Vector2 PilePos(int col)
    {
        float x = FrameInset + Margin + col * (CardKit.CardW + ColSpacing);
        float y = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + CardKit.CardH + Margin;
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
        int menu = RetroWidgets.MenuBarHitTest(menuBar, new[] { "New", "Redeal" }, local, leftPressed);
        if (menu == 0) Deal();
        else if (menu == 1 && !_won) Redeal();

        if (!leftPressed || _won) return;

        // Find clicked pile top
        for (int col = 0; col < TableauCols; col++)
        {
            if (_piles[col].Count == 0) continue;
            int top = _piles[col].Count - 1;
            var p = PilePos(col) + new Vector2(0, top * CardKit.CascadeY);
            if (!CardKit.HitTest(local, p)) continue;

            var card = _piles[col][top];
            // Try foundation first
            int f = (int)card.Suit;
            int needNext = _foundations[f].Count + 1;
            if (card.Rank == needNext)
            {
                _piles[col].RemoveAt(top);
                _foundations[f].Add(card);
                CheckWin();
                return;
            }
            // Try leftmost valid tableau
            for (int dest = 0; dest < TableauCols; dest++)
            {
                if (dest == col) continue;
                if (_piles[dest].Count == 0) continue;
                var destTop = _piles[dest][^1];
                if (destTop.Suit == card.Suit && destTop.Rank == card.Rank + 1)
                {
                    _piles[col].RemoveAt(top);
                    _piles[dest].Add(card);
                    return;
                }
            }
            return;
        }
    }

    private void CheckWin()
    {
        for (int i = 0; i < 4; i++)
            if (_foundations[i].Count != 13) return;
        _won = true;
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Cruel", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", "Redeal" }, -1);

        // Felt background
        float bodyY = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight;
        float bodyH = PanelSize.Y - bodyY - FrameInset - RetroWidgets.StatusBarHeight;
        Raylib.DrawRectangleRec(new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + bodyY,
            PanelSize.X - 2 * FrameInset, bodyH), new Color(8, 96, 56, 255));

        // Foundations (always 4 visible regardless of suit order)
        for (int i = 0; i < 4; i++)
        {
            var p = FoundationPos(i);
            var abs = new Vector2(panelOffset.X + p.X, panelOffset.Y + p.Y);
            if (_foundations[i].Count == 0) CardKit.DrawEmptySlot(abs);
            else CardKit.DrawCard(_foundations[i][^1], abs);
        }

        // Foundation labels (suit reminder)
        for (int i = 0; i < 4; i++)
        {
            var p = FoundationPos(i);
            CardKit.DrawSuitPip((Suit)i,
                (int)(panelOffset.X + p.X + CardKit.CardW + 14),
                (int)(panelOffset.Y + p.Y + CardKit.CardH / 2),
                6, i < 2 ? new Color(208, 0, 0, 255) : new Color(0, 0, 0, 255));
        }

        // Tableau
        for (int col = 0; col < TableauCols; col++)
        {
            var basePos = PilePos(col);
            var abs = new Vector2(panelOffset.X + basePos.X, panelOffset.Y + basePos.Y);
            if (_piles[col].Count == 0) { CardKit.DrawEmptySlot(abs); continue; }
            for (int i = 0; i < _piles[col].Count; i++)
            {
                var p = abs + new Vector2(0, i * CardKit.CascadeY);
                CardKit.DrawCard(_piles[col][i], p);
            }
        }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        int found = 0;
        for (int i = 0; i < 4; i++) found += _foundations[i].Count;
        string state = _won ? "You win!" : "Click a top card to play it (foundation, else leftmost legal pile)";
        RetroWidgets.StatusBar(status, state, $"Foundations: {found}/52   Redeals: {_redealCount}");
    }

    public void Close() { }
}
