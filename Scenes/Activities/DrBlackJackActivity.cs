using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Dr. Black Jack — single-deck blackjack vs the dealer. Hit / Stand / Double
/// on the player's turn. Dealer hits soft 17. Win 1.5x on natural blackjack.
/// Bankroll persists for the session.
/// </summary>
public class DrBlackJackActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Margin = 18;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + 6 * (CardKit.CardW + 8),
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + CardKit.CardH + 28
            + Margin + CardKit.CardH + 60
            + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private readonly List<Card> _shoe = new();
    private readonly List<Card> _player = new();
    private readonly List<Card> _dealer = new();
    private int _bankroll = 100;
    private int _bet = 10;
    private bool _inHand;
    private bool _dealerTurn;
    private bool _playerStood;
    private string _result = "";
    private float _dealerPause;
    private readonly Random _rng = new();

    public void Load() => StartHand();

    private void RebuildShoe()
    {
        _shoe.Clear();
        var deck = CardKit.NewDeck();
        CardKit.Shuffle(deck, _rng);
        foreach (var c in deck) { c.FaceUp = true; _shoe.Add(c); }
    }

    private Card Draw()
    {
        if (_shoe.Count == 0) RebuildShoe();
        var c = _shoe[^1];
        _shoe.RemoveAt(_shoe.Count - 1);
        return c;
    }

    private void StartHand()
    {
        if (_shoe.Count < 15) RebuildShoe();
        _player.Clear();
        _dealer.Clear();
        _player.Add(Draw());
        var dHole = Draw(); dHole.FaceUp = false;
        _dealer.Add(dHole);
        _player.Add(Draw());
        _dealer.Add(Draw());
        _inHand = true;
        _dealerTurn = false;
        _playerStood = false;
        _result = "";
        _bankroll -= _bet;

        if (HandValue(_player) == 21 && HandValue(_dealer) == 21) Resolve("Push (both blackjack)");
        else if (HandValue(_player) == 21) Resolve($"Blackjack! +{(int)(_bet * 2.5)}", _bet * 2.5f);
    }

    private static int HandValue(List<Card> hand)
    {
        int sum = 0, aces = 0;
        foreach (var c in hand)
        {
            int v = c.Rank switch { 1 => 11, >= 10 => 10, _ => c.Rank };
            if (c.Rank == 1) aces++;
            sum += v;
        }
        while (sum > 21 && aces > 0) { sum -= 10; aces--; }
        return sum;
    }

    private void Hit()
    {
        if (!_inHand || _dealerTurn) return;
        _player.Add(Draw());
        if (HandValue(_player) > 21) Resolve("Bust");
    }

    private void Stand()
    {
        if (!_inHand || _dealerTurn) return;
        _playerStood = true;
        _dealerTurn = true;
        _dealer[0].FaceUp = true;
        _dealerPause = 0.6f;
    }

    private void Double()
    {
        if (!_inHand || _dealerTurn || _player.Count != 2) return;
        if (_bankroll < _bet) return;
        _bankroll -= _bet;
        _bet *= 2;
        _player.Add(Draw());
        if (HandValue(_player) > 21) { Resolve("Bust"); _bet /= 2; return; }
        Stand();
    }

    private void Resolve(string msg, float multiplier = 0)
    {
        _result = msg;
        _inHand = false;
        if (multiplier > 0)
            _bankroll += (int)(_bet * multiplier);
    }

    private void DealerPlay(float delta)
    {
        if (_dealerPause > 0) { _dealerPause -= delta; return; }
        int dv = HandValue(_dealer);
        bool soft17 = dv == 17 && _dealer.Any(c => c.Rank == 1);
        if (dv < 17 || soft17)
        {
            _dealer.Add(Draw());
            _dealerPause = 0.6f;
            return;
        }
        // Resolve
        int pv = HandValue(_player);
        if (dv > 21) Resolve($"Dealer busts (+{_bet * 2})", 2);
        else if (pv > dv) Resolve($"You win (+{_bet * 2})", 2);
        else if (pv < dv) Resolve("Dealer wins");
        else Resolve($"Push (+{_bet})", 1);
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
        var items = _inHand
            ? new[] { "Hit", "Stand", "Double", "Bet -", "Bet +" }
            : new[] { "Deal", "Bet -", "Bet +", "Reset Bankroll" };
        switch (RetroWidgets.MenuBarHitTest(menuBar, items, local, leftPressed))
        {
            case 0:
                if (_inHand) Hit();
                else if (_bankroll >= _bet) StartHand();
                break;
            case 1:
                if (_inHand) Stand();
                else _bet = Math.Max(5, _bet - 5);
                break;
            case 2:
                if (_inHand) Double();
                else _bet = Math.Min(_bankroll, _bet + 5);
                break;
            case 3:
                if (_inHand) _bet = Math.Max(5, _bet - 5);
                else _bankroll = 100;
                break;
            case 4:
                if (_inHand) _bet = Math.Min(_bankroll, _bet + 5);
                break;
        }

        if (_dealerTurn && _inHand) DealerPlay(delta);
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Dr. Black Jack", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        var items = _inHand
            ? new[] { "Hit", "Stand", "Double", "Bet -", "Bet +" }
            : new[] { "Deal", "Bet -", "Bet +", "Reset Bankroll" };
        RetroWidgets.MenuBarVisual(menuBar, items, -1);

        float bodyY = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight;
        float bodyH = PanelSize.Y - bodyY - FrameInset - RetroWidgets.StatusBarHeight;
        Raylib.DrawRectangleRec(new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + bodyY,
            PanelSize.X - 2 * FrameInset, bodyH), new Color(8, 96, 56, 255));

        // Dealer row
        float drowY = panelOffset.Y + bodyY + Margin;
        RetroSkin.DrawText($"Dealer: {(_dealer.All(c => c.FaceUp) ? HandValue(_dealer).ToString() : "?")}",
            (int)(panelOffset.X + FrameInset + Margin), (int)(drowY - 18),
            new Color(255, 255, 255, 220), 16);
        for (int i = 0; i < _dealer.Count; i++)
            CardKit.DrawCard(_dealer[i],
                new Vector2(panelOffset.X + FrameInset + Margin + i * (CardKit.CardW + 8), drowY));

        // Player row
        float prowY = drowY + CardKit.CardH + 50;
        RetroSkin.DrawText($"You: {HandValue(_player)}    Bet: ${_bet}    Bankroll: ${_bankroll}",
            (int)(panelOffset.X + FrameInset + Margin), (int)(prowY - 20),
            new Color(255, 255, 255, 220), 16);
        for (int i = 0; i < _player.Count; i++)
            CardKit.DrawCard(_player[i],
                new Vector2(panelOffset.X + FrameInset + Margin + i * (CardKit.CardW + 8), prowY));

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string state = _inHand ? (_dealerTurn ? "Dealer plays..." : "Hit / Stand / Double") : (_result.Length > 0 ? _result : "Click Deal");
        RetroWidgets.StatusBar(status, state, $"Shoe: {_shoe.Count}");
    }

    public void Close() { }
}
