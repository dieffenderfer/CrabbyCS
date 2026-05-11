using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Windows 98–style Hearts. Four seats (you bottom, AI left/top/right),
/// standard 52-card deck, dealt 13 each. Pass-3 phase rotates
/// left → right → across → no-pass; whoever has 2♣ leads the first
/// trick. Must follow suit; hearts can't lead until broken; first
/// trick disallows hearts and Q♠. Each heart = 1 pt, Q♠ = 13 pts;
/// shooting the moon (all 13 hearts + Q♠ in one hand) flips the
/// 26 pts onto the other three players. Game ends when someone hits
/// 100; lowest score wins.
///
/// MVP scope: faithful Hearts mechanics + simple "play lowest legal
/// card" AI for the other 3 seats. Pass-phase AI just dumps the
/// three highest cards. End-of-hand and end-of-game banners drawn
/// in-line. Moon-shot detection wired and surfaced via a banner.
///
/// Polish (moon animation, smarter AI, last-trick peek, difficulty
/// levels) lands in follow-up commits per the build plan.
/// </summary>
public sealed class HeartsActivity : IActivity
{
    // ── Layout constants ────────────────────────────────────────────
    private const int FrameInset = 3;
    private const int Margin = 12;
    private const int PanelW = 720;
    private const int PanelH = 520;
    private const int HandFanStep = 26;         // visible width per card in player's fan
    private const int AiFanStep = 12;           // visible width per face-down card in AI fans

    public Vector2 PanelSize => new(PanelW, PanelH);
    public bool IsFinished { get; private set; }

    // ── Seats ───────────────────────────────────────────────────────
    // 0 = human (bottom), 1 = AI (left), 2 = AI (top), 3 = AI (right).
    // Seats rotate clockwise: 0 → 1 → 2 → 3 → 0.
    private const int Seats = 4;
    private const int Human = 0;
    private static int NextSeat(int s) => (s + 1) % Seats;

    private readonly string[] _names = { "You", "Computer 1", "Computer 2", "Computer 3" };
    private readonly List<Card>[] _hands = new List<Card>[Seats];
    private readonly List<Card>[] _taken = new List<Card>[Seats];
    private readonly int[] _scoresTotal = new int[Seats];
    private readonly int[] _scoresHand = new int[Seats];

    // ── Game-state machine ──────────────────────────────────────────
    private enum Phase { Passing, Playing, HandEnd, GameEnd }
    private Phase _phase = Phase.Passing;

    /// <summary>
    /// Pass direction: 0=left (CW), 1=right (CCW), 2=across, 3=no pass.
    /// Rotates each hand; on a no-pass hand, the activity skips
    /// straight to play.
    /// </summary>
    private int _passDir;

    /// <summary>Indexes into _hands[Human] selected for passing this hand.</summary>
    private readonly List<int> _passSel = new();

    /// <summary>Cards each seat is sending; null until they've submitted.</summary>
    private readonly List<Card>?[] _passOut = new List<Card>?[Seats];

    private int _trickLeader;
    private int _waitingFor;
    private bool _heartsBroken;
    private bool _firstTrick;

    /// <summary>Current trick — index by seat. null = haven't played yet.</summary>
    private readonly Card?[] _trick = new Card?[Seats];

    /// <summary>Seat order in which cards were played in the current
    /// trick — used for resolution + the slight cosmetic stagger so
    /// the user can see who played what.</summary>
    private readonly List<int> _trickOrder = new();

    // ── Timers ──────────────────────────────────────────────────────
    /// <summary>Delay between AI plays so the human can read the
    /// table. Re-armed after each AI play.</summary>
    private float _aiTimer;
    private const float AiThinkTime = 0.42f;

    /// <summary>Delay between the 4th card landing and the trick
    /// clearing. Lets the user see the completed trick.</summary>
    private float _trickResolveTimer;
    private const float TrickPauseTime = 1.1f;

    private readonly Random _rng = new();
    private readonly RetroHelp _help = new()
    {
        Title = "Hearts — How to play",
        Lines = new[]
        {
            "Trick-taking for four. You're at the bottom; AIs fill the rest.",
            "Each hand starts with a 3-card pass (left / right / across /",
            "no-pass rotates each hand). Whoever holds 2♣ leads the first",
            "trick with it.",
            "Follow suit if you can. Hearts can't lead until broken — when",
            "someone is forced to play a heart on a non-heart trick.",
            "Hearts and Q♠ can't be played on the first trick.",
            "Each heart = 1 pt; Q♠ = 13 pts. Lowest total after someone",
            "hits 100 wins. Take ALL the hearts + Q♠ in one hand and the",
            "26 points land on every other player instead.",
        },
    };

    private string _banner = "";
    private float _bannerTimer;

    // ── Lifecycle ───────────────────────────────────────────────────
    public void Load()
    {
        for (int i = 0; i < Seats; i++)
        {
            _hands[i] = new List<Card>();
            _taken[i] = new List<Card>();
        }
        StartNewMatch();
    }

    public void Close() { }

    private void StartNewMatch()
    {
        Array.Clear(_scoresTotal, 0, Seats);
        _passDir = 0;
        DealHand();
    }

    private void DealHand()
    {
        for (int i = 0; i < Seats; i++)
        {
            _hands[i].Clear();
            _taken[i].Clear();
            _scoresHand[i] = 0;
            _passOut[i] = null;
            _trick[i] = null;
        }
        _trickOrder.Clear();
        _passSel.Clear();
        _heartsBroken = false;
        _firstTrick = true;
        _trickResolveTimer = 0f;

        var deck = CardKit.NewDeck();
        CardKit.Shuffle(deck, _rng);
        for (int i = 0; i < deck.Count; i++)
        {
            int seat = i % Seats;
            var c = deck[i];
            c.FaceUp = seat == Human;
            _hands[seat].Add(c);
        }
        SortHand(_hands[Human]);

        if (_passDir == 3)
        {
            // No-pass round — jump straight to play.
            _phase = Phase.Playing;
            BeginFirstTrick();
        }
        else
        {
            _phase = Phase.Passing;
            // AI seats pick their pass picks immediately so submitting
            // human picks resolves the whole pass in one step.
            for (int s = 1; s < Seats; s++) _passOut[s] = AiPickPass(s);
        }
    }

    private void SortHand(List<Card> h)
    {
        h.Sort((a, b) =>
        {
            int c = ((int)a.Suit).CompareTo((int)b.Suit);
            return c != 0 ? c : RankOrder(a).CompareTo(RankOrder(b));
        });
    }

    /// <summary>Rank ordering for trick winning + sorting. Ace is high;
    /// 2..10 sort normally, J=11, Q=12, K=13, A=14.</summary>
    private static int RankOrder(Card c) => c.Rank == 1 ? 14 : c.Rank;

    private static bool IsTwoOfClubs(Card c) => c.Suit == Suit.Clubs && c.Rank == 2;
    private static bool IsQueenOfSpades(Card c) => c.Suit == Suit.Spades && c.Rank == 12;

    private void BeginFirstTrick()
    {
        // Whoever has 2♣ leads it.
        int leader = -1;
        for (int s = 0; s < Seats; s++)
            foreach (var c in _hands[s])
                if (IsTwoOfClubs(c)) { leader = s; break; }
        if (leader < 0) leader = 0;        // shouldn't happen with a real deck
        _trickLeader = leader;
        _waitingFor = leader;
        _aiTimer = _waitingFor == Human ? 0 : AiThinkTime;
    }

    // ── Pass phase ──────────────────────────────────────────────────
    private List<Card> AiPickPass(int seat)
    {
        // Beginner pass logic: dump the three highest by rank, with a
        // bias toward shedding spades ≥ Q to get rid of Q♠ exposure.
        // Smarter passing lands in the polish commit.
        var h = _hands[seat];
        var ordered = h
            .OrderByDescending(c =>
            {
                int score = RankOrder(c);
                if (c.Suit == Suit.Spades && c.Rank >= 12) score += 30;       // dump Q♠ / K♠ / A♠
                if (c.Suit == Suit.Hearts) score += 5;                       // bias against keeping high hearts
                return score;
            })
            .ToList();
        var picks = new List<Card> { ordered[0], ordered[1], ordered[2] };
        return picks;
    }

    private void TryResolvePasses()
    {
        if (_phase != Phase.Passing) return;
        for (int s = 0; s < Seats; s++) if (_passOut[s] == null) return;

        // All four sets are in. Compute the destination seat per
        // direction, remove from sender, add to receiver.
        int OffsetFor(int dir) => dir switch
        {
            0 => 1,        // pass left → next seat CW
            1 => 3,        // pass right → previous seat CW (i.e. +3 mod 4)
            2 => 2,        // pass across
            _ => 0,
        };
        int offset = OffsetFor(_passDir);
        for (int s = 0; s < Seats; s++)
        {
            int dst = (s + offset) % Seats;
            var sent = _passOut[s]!;
            foreach (var c in sent)
            {
                _hands[s].Remove(c);
                _hands[dst].Add(c);
            }
        }
        foreach (var c in _hands[Human]) c.FaceUp = true;
        SortHand(_hands[Human]);
        for (int s = 0; s < Seats; s++) _passOut[s] = null;

        _phase = Phase.Playing;
        BeginFirstTrick();
    }

    // ── Trick play ──────────────────────────────────────────────────
    private bool CanPlay(int seat, Card c)
    {
        var hand = _hands[seat];
        bool leading = _trick[_trickLeader] == null && _trickOrder.Count == 0;
        if (leading)
        {
            if (_firstTrick)
            {
                // First trick must be led with 2♣.
                return IsTwoOfClubs(c);
            }
            if (c.Suit == Suit.Hearts && !_heartsBroken)
            {
                // Can lead hearts only if hearts is the only suit left
                // in the hand.
                return hand.All(x => x.Suit == Suit.Hearts);
            }
            return true;
        }
        // Following: must match suit if able.
        var leadSuit = _trick[_trickLeader]!.Suit;
        if (c.Suit == leadSuit) return ValidFirstTrick(c);
        // Off-suit only if we have no card of the lead suit.
        if (hand.Any(x => x.Suit == leadSuit)) return false;
        return ValidFirstTrick(c);
    }

    private bool ValidFirstTrick(Card c)
    {
        if (!_firstTrick) return true;
        // No hearts / Q♠ on the first trick — unless the player has
        // literally nothing else, which we can detect after the rest
        // of the legal-play filter has run. Conservatively reject
        // here; the "all-hearts hand" exception is handled by the
        // legal-set fallback in CardsLegalToPlay below.
        if (c.Suit == Suit.Hearts) return false;
        if (IsQueenOfSpades(c)) return false;
        return true;
    }

    private List<Card> CardsLegalToPlay(int seat)
    {
        var legal = _hands[seat].Where(c => CanPlay(seat, c)).ToList();
        if (legal.Count == 0 && _firstTrick)
        {
            // Pathological: a hand that's all hearts + Q♠ on the first
            // trick. Allow the smallest heart so play can proceed.
            legal = _hands[seat]
                .Where(c => c.Suit == Suit.Hearts || IsQueenOfSpades(c))
                .OrderBy(RankOrder)
                .Take(1)
                .ToList();
        }
        return legal;
    }

    private void PlayCard(int seat, Card c)
    {
        _hands[seat].Remove(c);
        c.FaceUp = true;
        _trick[seat] = c;
        _trickOrder.Add(seat);
        if (c.Suit == Suit.Hearts) _heartsBroken = true;
        if (IsQueenOfSpades(c)) _heartsBroken = true;       // Q♠ also frees hearts

        // Advance turn or resolve.
        if (_trickOrder.Count >= Seats)
        {
            _trickResolveTimer = TrickPauseTime;
            _waitingFor = -1;
        }
        else
        {
            _waitingFor = NextSeat(seat);
            _aiTimer = _waitingFor == Human ? 0 : AiThinkTime;
        }
    }

    private void ResolveTrick()
    {
        var leadSuit = _trick[_trickLeader]!.Suit;
        int winner = _trickLeader;
        int best = RankOrder(_trick[_trickLeader]!);
        for (int s = 0; s < Seats; s++)
        {
            if (s == _trickLeader || _trick[s] == null) continue;
            if (_trick[s]!.Suit != leadSuit) continue;
            int r = RankOrder(_trick[s]!);
            if (r > best) { best = r; winner = s; }
        }
        for (int s = 0; s < Seats; s++)
            if (_trick[s] != null) _taken[winner].Add(_trick[s]!);
        Array.Clear(_trick, 0, Seats);
        _trickOrder.Clear();
        _firstTrick = false;
        _trickLeader = winner;
        if (_hands[winner].Count == 0)
        {
            EndHand();
        }
        else
        {
            _waitingFor = winner;
            _aiTimer = _waitingFor == Human ? 0 : AiThinkTime;
        }
    }

    // ── End of hand / match ─────────────────────────────────────────
    private void EndHand()
    {
        // Score the cards each seat took.
        for (int s = 0; s < Seats; s++)
        {
            int pts = 0;
            foreach (var c in _taken[s])
            {
                if (c.Suit == Suit.Hearts) pts++;
                if (IsQueenOfSpades(c)) pts += 13;
            }
            _scoresHand[s] = pts;
        }
        // Moon-shot: one seat took all 26 points.
        int moonSeat = -1;
        for (int s = 0; s < Seats; s++)
            if (_scoresHand[s] == 26) moonSeat = s;
        if (moonSeat >= 0)
        {
            for (int s = 0; s < Seats; s++)
                _scoresHand[s] = s == moonSeat ? 0 : 26;
            _banner = $"🌙 {_names[moonSeat]} SHOT THE MOON!";
            _bannerTimer = 4f;
        }
        for (int s = 0; s < Seats; s++) _scoresTotal[s] += _scoresHand[s];

        _phase = Phase.HandEnd;

        // Match-end check.
        if (_scoresTotal.Any(p => p >= 100))
        {
            _phase = Phase.GameEnd;
        }
    }

    private void DealNextHand()
    {
        _passDir = (_passDir + 1) % 4;
        DealHand();
    }

    // ── AI play ─────────────────────────────────────────────────────
    private void AiPlayTurn()
    {
        var legal = CardsLegalToPlay(_waitingFor);
        if (legal.Count == 0) return;        // shouldn't happen for legal hands

        // MVP: play lowest-ranked legal card, with two soft heuristics:
        //   1. If we're following and CAN dump Q♠, do it.
        //   2. If we're leading and have low clubs early, prefer clubs.
        // Smarter strategy + difficulty levels land in the polish commit.
        bool following = _trickOrder.Count > 0;
        Card pick;
        if (following && legal.Any(IsQueenOfSpades)
            && _trick[_trickLeader]!.Suit != Suit.Spades)
        {
            pick = legal.First(IsQueenOfSpades);
        }
        else
        {
            pick = legal.OrderBy(RankOrder).First();
        }
        PlayCard(_waitingFor, pick);
    }

    // ── Input ───────────────────────────────────────────────────────
    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;

        // Banner timer.
        if (_bannerTimer > 0)
        {
            _bannerTimer -= delta;
            if (_bannerTimer <= 0) _banner = "";
        }

        // Title bar close.
        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        { IsFinished = true; return; }

        // Menu bar.
        var menuBar = new Rectangle(FrameInset, FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        int m = RetroWidgets.MenuBarHitTest(menuBar, new[] { "New Game", "Help" }, local, leftPressed);
        if (m == 0) { StartNewMatch(); return; }
        if (m == 1) { _help.Visible = !_help.Visible; return; }
        if (_help.HandleInput(local, leftPressed, PanelSize)) return;

        switch (_phase)
        {
            case Phase.Passing: UpdatePassing(local, leftPressed); break;
            case Phase.Playing: UpdatePlaying(delta, local, leftPressed); break;
            case Phase.HandEnd: UpdateHandEnd(local, leftPressed); break;
            case Phase.GameEnd: UpdateGameEnd(local, leftPressed); break;
        }
    }

    private void UpdatePassing(Vector2 local, bool leftPressed)
    {
        // Click cards in the player's hand to toggle their selection.
        var humanHand = _hands[Human];
        var humanFan = HumanHandPositions();
        if (leftPressed)
        {
            // Top-down hit test (rightmost card is on top).
            for (int i = humanHand.Count - 1; i >= 0; i--)
            {
                if (CardKit.HitTest(local, humanFan[i]))
                {
                    if (_passSel.Contains(i)) _passSel.Remove(i);
                    else if (_passSel.Count < 3) _passSel.Add(i);
                    return;
                }
            }
            // Submit button hit-test.
            if (RetroSkin.PointInRect(local, PassSubmitRect()) && _passSel.Count == 3)
            {
                var sel = _passSel.Select(i => humanHand[i]).ToList();
                _passOut[Human] = sel;
                _passSel.Clear();
                TryResolvePasses();
            }
        }
    }

    private void UpdatePlaying(float delta, Vector2 local, bool leftPressed)
    {
        // Pause to let the user see a completed trick.
        if (_trickResolveTimer > 0)
        {
            _trickResolveTimer -= delta;
            if (_trickResolveTimer <= 0) ResolveTrick();
            return;
        }

        if (_waitingFor == Human && leftPressed)
        {
            var humanHand = _hands[Human];
            var humanFan = HumanHandPositions();
            for (int i = humanHand.Count - 1; i >= 0; i--)
            {
                if (!CardKit.HitTest(local, humanFan[i])) continue;
                var c = humanHand[i];
                if (CanPlay(Human, c)) PlayCard(Human, c);
                return;
            }
        }
        else if (_waitingFor >= 0 && _waitingFor != Human)
        {
            _aiTimer -= delta;
            if (_aiTimer <= 0) AiPlayTurn();
        }
    }

    private void UpdateHandEnd(Vector2 local, bool leftPressed)
    {
        if (leftPressed && RetroSkin.PointInRect(local, ContinueButtonRect()))
        {
            DealNextHand();
        }
    }

    private void UpdateGameEnd(Vector2 local, bool leftPressed)
    {
        if (leftPressed && RetroSkin.PointInRect(local, ContinueButtonRect()))
        {
            StartNewMatch();
        }
    }

    // ── Layout helpers ──────────────────────────────────────────────
    private static Rectangle PassSubmitRect()
        => new(PanelW - Margin - 100, PanelH - 30 - 60, 100, 24);

    private static Rectangle ContinueButtonRect()
        => new(PanelW / 2 - 60, PanelH - 30 - 60, 120, 26);

    private Vector2[] HumanHandPositions()
    {
        var h = _hands[Human];
        int n = h.Count;
        if (n == 0) return Array.Empty<Vector2>();
        int fanW = (n - 1) * HandFanStep + CardKit.CardW;
        int x0 = PanelW / 2 - fanW / 2;
        int y0 = PanelH - CardKit.CardH - 36;
        var positions = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            int y = y0;
            if (_phase == Phase.Passing && _passSel.Contains(i)) y -= 14;
            positions[i] = new Vector2(x0 + i * HandFanStep, y);
        }
        return positions;
    }

    private static Vector2 SeatNamePosition(int seat)
    {
        // Anchor for the seat's name label + score readout.
        return seat switch
        {
            0 => new Vector2(PanelW / 2 - 60, PanelH - CardKit.CardH - 60),
            1 => new Vector2(Margin + 4, PanelH / 2 - 8),
            2 => new Vector2(PanelW / 2 - 60, FrameInset + RetroWidgets.TitleBarHeight
                              + RetroWidgets.MenuBarHeight + Margin),
            3 => new Vector2(PanelW - Margin - 120, PanelH / 2 - 8),
            _ => Vector2.Zero,
        };
    }

    private static Vector2 SeatHandAnchor(int seat)
    {
        // Where the AI face-down stacks render.
        return seat switch
        {
            1 => new Vector2(Margin + 4, PanelH / 2 + 8),
            2 => new Vector2(PanelW / 2 - (CardKit.CardW + 12 * AiFanStep) / 2,
                             FrameInset + RetroWidgets.TitleBarHeight
                             + RetroWidgets.MenuBarHeight + Margin + 14),
            3 => new Vector2(PanelW - Margin - CardKit.CardW - 4, PanelH / 2 + 8),
            _ => Vector2.Zero,
        };
    }

    private static Vector2 TrickCardPosition(int seat)
    {
        // Center anchor + cardinal-direction offsets so each seat's
        // played card lands in the right slot relative to where its
        // player sits at the table.
        int cx = PanelW / 2 - CardKit.CardW / 2;
        int cy = PanelH / 2 - CardKit.CardH / 2;
        return seat switch
        {
            0 => new Vector2(cx, cy + 40),
            1 => new Vector2(cx - 60, cy),
            2 => new Vector2(cx, cy - 40),
            3 => new Vector2(cx + 60, cy),
            _ => new Vector2(cx, cy),
        };
    }

    // ── Render ──────────────────────────────────────────────────────
    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Hearts", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New Game", "Help" }, -1);

        // Green felt body.
        var felt = new Rectangle(
            panelOffset.X + FrameInset + 1,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 1,
            PanelSize.X - 2 * FrameInset - 2,
            PanelSize.Y - 2 * FrameInset - RetroWidgets.TitleBarHeight
                - RetroWidgets.MenuBarHeight - RetroWidgets.StatusBarHeight - 2);
        Raylib.DrawRectangleRec(felt,
            new Color((byte)20, (byte)96, (byte)52, (byte)255));

        DrawSeatLabels(panelOffset);
        DrawAiHands(panelOffset);
        DrawTrick(panelOffset);
        DrawHumanHand(panelOffset);
        DrawPassDirectionAndHeartsBroken(panelOffset);

        if (_phase == Phase.Passing) DrawPassChrome(panelOffset);
        if (_phase == Phase.HandEnd) DrawHandEndModal(panelOffset);
        if (_phase == Phase.GameEnd) DrawGameEndModal(panelOffset);
        if (_bannerTimer > 0) DrawBanner(panelOffset);

        // Status bar.
        var statusBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(statusBar, StatusLeft(), StatusRight());

        _help.Draw(panelOffset, PanelSize);
    }

    private string StatusLeft() => _phase switch
    {
        Phase.Passing => $"Select 3 cards to pass {PassDirName(_passDir)}.",
        Phase.Playing when _waitingFor == Human => "Your turn.",
        Phase.Playing => $"{_names[_waitingFor]} is playing…",
        Phase.HandEnd => "Hand complete.",
        Phase.GameEnd => "Game over.",
        _ => "",
    };

    private string StatusRight()
    {
        var totals = string.Join("  ",
            Enumerable.Range(0, Seats).Select(s => $"{ShortName(s)} {_scoresTotal[s]}"));
        return totals;
    }

    private string ShortName(int seat) => seat == 0 ? "You" : $"C{seat}";

    private static string PassDirName(int dir) => dir switch
    {
        0 => "LEFT", 1 => "RIGHT", 2 => "ACROSS", _ => "—",
    };

    private void DrawSeatLabels(Vector2 panelOffset)
    {
        for (int s = 0; s < Seats; s++)
        {
            var p = SeatNamePosition(s) + panelOffset;
            // Highlight whichever seat is on turn.
            bool active = _waitingFor == s && _phase == Phase.Playing;
            var col = active ? new Color((byte)255, (byte)232, (byte)80, (byte)255)
                             : new Color((byte)232, (byte)232, (byte)232, (byte)255);
            RetroSkin.DrawText(_names[s], (int)p.X, (int)p.Y, col,
                RetroSkin.BodyFontSize - 1);
            RetroSkin.DrawText($"{_scoresTotal[s]} pts ({_taken[s].Count} cards)",
                (int)p.X, (int)p.Y + 14,
                new Color((byte)200, (byte)200, (byte)200, (byte)255),
                RetroSkin.BodyFontSize - 3);
        }
    }

    private void DrawAiHands(Vector2 panelOffset)
    {
        for (int s = 1; s < Seats; s++)
        {
            var anchor = SeatHandAnchor(s) + panelOffset;
            int n = _hands[s].Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 pos = s switch
                {
                    1 => new Vector2(anchor.X, anchor.Y + i * AiFanStep),
                    3 => new Vector2(anchor.X, anchor.Y + i * AiFanStep),
                    _ => new Vector2(anchor.X + i * AiFanStep, anchor.Y),
                };
                CardKit.DrawCardBack(pos);
            }
        }
    }

    private void DrawHumanHand(Vector2 panelOffset)
    {
        var hand = _hands[Human];
        var positions = HumanHandPositions();
        for (int i = 0; i < hand.Count; i++)
        {
            CardKit.DrawCard(hand[i], positions[i] + panelOffset);
            // Highlight illegal cards in playing phase so the user
            // sees what they can actually play.
            if (_phase == Phase.Playing && _waitingFor == Human
                && !CanPlay(Human, hand[i]))
            {
                var pos = positions[i] + panelOffset;
                Raylib.DrawRectangleRec(
                    new Rectangle(pos.X, pos.Y, CardKit.CardW, CardKit.CardH),
                    new Color((byte)0, (byte)0, (byte)0, (byte)120));
            }
        }
    }

    private void DrawTrick(Vector2 panelOffset)
    {
        for (int s = 0; s < Seats; s++)
        {
            if (_trick[s] == null) continue;
            var pos = TrickCardPosition(s) + panelOffset;
            CardKit.DrawCard(_trick[s]!, pos);
        }
    }

    private void DrawPassDirectionAndHeartsBroken(Vector2 panelOffset)
    {
        // Heart-broken indicator (centre top, just below the menu bar).
        int hx = (int)panelOffset.X + PanelW - 60;
        int hy = (int)panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight
                 + RetroWidgets.MenuBarHeight + 6;
        var heartCol = _heartsBroken
            ? new Color((byte)240, (byte)80, (byte)80, (byte)255)
            : new Color((byte)64, (byte)64, (byte)64, (byte)255);
        CardKit.DrawSuitPip(Suit.Hearts, hx, hy + 8, 8, heartCol);
        RetroSkin.DrawText(_heartsBroken ? "broken" : "—",
            hx + 12, hy + 1,
            new Color((byte)200, (byte)200, (byte)200, (byte)255),
            RetroSkin.BodyFontSize - 3);

        // Pass-direction text in upper-left of the table.
        if (_phase == Phase.Passing)
        {
            int px = (int)panelOffset.X + Margin + 4;
            int py = (int)panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight
                     + RetroWidgets.MenuBarHeight + 6;
            RetroSkin.DrawText($"Pass: {PassDirName(_passDir)}",
                px, py,
                new Color((byte)255, (byte)232, (byte)80, (byte)255),
                RetroSkin.BodyFontSize - 1);
        }
    }

    private void DrawPassChrome(Vector2 panelOffset)
    {
        // Instructional line above the hand + Submit button at right.
        int cx = (int)panelOffset.X + PanelW / 2;
        int cy = (int)panelOffset.Y + PanelH - CardKit.CardH - 92;
        string line = _passSel.Count == 3
            ? "Click Submit to send your three cards."
            : $"Pick {3 - _passSel.Count} more card(s) to pass.";
        int tw = RetroSkin.MeasureText(line, RetroSkin.BodyFontSize - 1);
        RetroSkin.DrawText(line, cx - tw / 2, cy,
            new Color((byte)232, (byte)232, (byte)232, (byte)255),
            RetroSkin.BodyFontSize - 1);

        var btn = PassSubmitRect();
        btn.X += panelOffset.X;
        btn.Y += panelOffset.Y;
        bool enabled = _passSel.Count == 3;
        if (enabled) RetroSkin.DrawRaised(btn);
        else RetroSkin.DrawSunken(btn, RetroSkin.Face);
        int btw = RetroSkin.MeasureText("Submit", RetroSkin.BodyFontSize - 1);
        RetroSkin.DrawText("Submit",
            (int)btn.X + ((int)btn.Width - btw) / 2,
            (int)btn.Y + 5,
            enabled ? RetroSkin.BodyText : RetroSkin.DisabledText,
            RetroSkin.BodyFontSize - 1);
    }

    private void DrawHandEndModal(Vector2 panelOffset)
    {
        DrawCenteredCard(panelOffset, 360, 200, "Hand complete", () =>
        {
            int rowY = 0;
            for (int s = 0; s < Seats; s++)
            {
                string row = $"{_names[s],-14}  +{_scoresHand[s],3}   total {_scoresTotal[s],3}";
                RetroSkin.DrawText(row,
                    (int)panelOffset.X + PanelW / 2 - 130,
                    (int)panelOffset.Y + PanelH / 2 - 30 + rowY,
                    RetroSkin.BodyText, RetroSkin.BodyFontSize - 1);
                rowY += 18;
            }
        }, "Next hand");
    }

    private void DrawGameEndModal(Vector2 panelOffset)
    {
        int winner = 0;
        for (int s = 1; s < Seats; s++)
            if (_scoresTotal[s] < _scoresTotal[winner]) winner = s;
        DrawCenteredCard(panelOffset, 360, 200,
            $"Game over — {_names[winner]} wins!", () =>
        {
            int rowY = 0;
            for (int s = 0; s < Seats; s++)
            {
                string row = $"{_names[s],-14}  {_scoresTotal[s],3} pts";
                RetroSkin.DrawText(row,
                    (int)panelOffset.X + PanelW / 2 - 90,
                    (int)panelOffset.Y + PanelH / 2 - 20 + rowY,
                    s == winner
                        ? new Color((byte)80, (byte)240, (byte)80, (byte)255)
                        : RetroSkin.BodyText,
                    RetroSkin.BodyFontSize - 1);
                rowY += 18;
            }
        }, "Play again");
    }

    private void DrawCenteredCard(Vector2 panelOffset, int w, int h, string title,
        Action body, string buttonLabel)
    {
        int cx = (int)panelOffset.X + PanelW / 2 - w / 2;
        int cy = (int)panelOffset.Y + PanelH / 2 - h / 2;
        Raylib.DrawRectangle((int)panelOffset.X, (int)panelOffset.Y,
            PanelW, PanelH,
            new Color((byte)0, (byte)0, (byte)0, (byte)140));
        var panel = new Rectangle(cx, cy, w, h);
        RetroSkin.DrawRaised(panel);
        var bar = new Rectangle(cx + 2, cy + 2, w - 4, RetroWidgets.TitleBarHeight);
        Raylib.DrawRectangleGradientH((int)bar.X, (int)bar.Y, (int)bar.Width, (int)bar.Height,
            RetroSkin.TitleActive, RetroSkin.TitleGradEnd);
        RetroSkin.DrawText(title, (int)bar.X + 6, (int)bar.Y + 1,
            RetroSkin.TitleText, RetroSkin.TitleFontSize);

        body();

        var btn = ContinueButtonRect();
        btn.X += panelOffset.X;
        btn.Y += panelOffset.Y;
        RetroSkin.DrawRaised(btn);
        int btw = RetroSkin.MeasureText(buttonLabel, RetroSkin.BodyFontSize - 1);
        RetroSkin.DrawText(buttonLabel,
            (int)btn.X + ((int)btn.Width - btw) / 2,
            (int)btn.Y + 5,
            RetroSkin.BodyText, RetroSkin.BodyFontSize - 1);
    }

    private void DrawBanner(Vector2 panelOffset)
    {
        // Big yellow moon-shot banner in the centre. Drawn after the
        // table so it sits on top of the trick.
        int tw = RetroSkin.MeasureText(_banner, RetroSkin.TitleFontSize + 2);
        int bx = (int)panelOffset.X + PanelW / 2 - tw / 2 - 12;
        int by = (int)panelOffset.Y + PanelH / 2 - 60;
        var bg = new Rectangle(bx, by, tw + 24, 30);
        Raylib.DrawRectangleRec(bg,
            new Color((byte)244, (byte)200, (byte)60, (byte)230));
        Raylib.DrawRectangleLines((int)bg.X, (int)bg.Y, (int)bg.Width, (int)bg.Height,
            new Color((byte)40, (byte)24, (byte)8, (byte)255));
        RetroSkin.DrawText(_banner, bx + 12, by + 6,
            new Color((byte)40, (byte)24, (byte)8, (byte)255),
            RetroSkin.TitleFontSize + 2);
    }
}
