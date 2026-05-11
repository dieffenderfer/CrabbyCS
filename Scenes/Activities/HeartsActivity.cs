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
/// AI plays at three difficulty tiers — Beginner (legal-random),
/// Standard (duck + Q♠ dump heuristics), Expert (card counting +
/// moon-shot defense + smarter pass that voids short suits and
/// protects Q♠). Choice persists in hearts_difficulty.txt next to
/// the chess settings. Card-play and trick-clear are animated; the
/// last completed trick is peekable via the menu bar's "Last Trick"
/// entry or a right-click on the trick area. Moon shots play a
/// full-screen reveal with a glowing moon + sparkles + the score-
/// reversal explainer before the end-of-hand modal appears.
///
/// Netplay layer (host-mediated 4-way) lands in follow-up commits;
/// the protocol fields + INetplayHeartsSink hook into the same
/// pattern golf / chess / Tetris race use.
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
    private enum Phase { Passing, Playing, MoonReveal, HandEnd, GameEnd }
    private Phase _phase = Phase.Passing;

    public enum Difficulty { Beginner, Standard, Expert }
    private Difficulty _difficulty = Difficulty.Standard;

    // ── Card-play / clear animations ────────────────────────────────
    // Cards in flight (sliding from one position to another). The
    // renderer checks this list for any card the static draw path
    // would otherwise paint and uses the interpolated position
    // instead. Input + AI are blocked while anims are active, so
    // the game's state machine stays in lockstep with what the
    // user can see.
    private sealed class CardAnim
    {
        public Card Card = null!;
        public Vector2 From;
        public Vector2 To;
        public float Duration;
        public float Elapsed;
        public Action? OnComplete;
        public float T => Math.Clamp(Elapsed / Duration, 0f, 1f);
        public Vector2 Position
        {
            get
            {
                // easeOutQuad: snappy on entry, gentle on landing.
                float t = T;
                float e = 1f - (1f - t) * (1f - t);
                return Vector2.Lerp(From, To, e);
            }
        }
    }
    private readonly List<CardAnim> _anims = new();
    private const float CardPlayDuration = 0.22f;
    private const float CardClearDuration = 0.28f;

    // ── Last-trick peek ─────────────────────────────────────────────
    /// <summary>Snapshot of the most-recently-resolved trick. Right-
    /// click on the trick area (when no current trick is in flight)
    /// pops this open as a face-up dimmed overlay; click anywhere to
    /// dismiss. Disabled when no trick has been resolved yet this
    /// hand (e.g. immediately after a deal).</summary>
    private readonly Card?[] _lastTrick = new Card?[Seats];
    private int _lastTrickWinner = -1;
    private bool _lastTrickAvailable;
    private bool _peekActive;

    // ── Per-game stats (for end-of-game flavor) ────────────────────
    private readonly int[] _moonShots = new int[Seats];
    private readonly int[] _tricksWon = new int[Seats];

    // ── Card counter (Expert AI) ────────────────────────────────────
    /// <summary>Set of every card seen this hand — populated as cards
    /// are played. Expert AI uses this + per-seat void inference
    /// to count outs and detect moon attempts.</summary>
    private readonly HashSet<int> _played = new();      // key = SuitIdx*16 + Rank
    /// <summary>_voids[seat, suit] = true once we've seen `seat`
    /// discard off-suit (i.e., confirmed they hold no card of that
    /// suit). Rebuilt fresh each hand.</summary>
    private readonly bool[,] _voids = new bool[Seats, 4];

    private static int CardKey(Card c) => (int)c.Suit * 16 + c.Rank;

    // ── Moon-shot reveal ────────────────────────────────────────────
    private int _moonSeat = -1;
    private float _moonAnimTimer;
    private const float MoonAnimDuration = 2.5f;

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
        LoadDifficulty();
        StartNewMatch();
    }

    public void Close() { }

    private void StartNewMatch()
    {
        Array.Clear(_scoresTotal, 0, Seats);
        Array.Clear(_moonShots, 0, Seats);
        Array.Clear(_tricksWon, 0, Seats);
        _passDir = 0;
        DealHand();
    }

    // ── Difficulty persistence ──────────────────────────────────────
    // Same one-line-text-file pattern the chess theme / piece-font /
    // window-size all use; lives in the SaveManager folder so the
    // pet's main exe and the activity sibling process share it.
    private static string DifficultyPath
        => Path.Combine(MouseHouse.Core.SaveManager.SaveDirectory,
            "hearts_difficulty.txt");

    private void LoadDifficulty()
    {
        try
        {
            if (!File.Exists(DifficultyPath)) return;
            var s = File.ReadAllText(DifficultyPath).Trim();
            if (Enum.TryParse<Difficulty>(s, ignoreCase: true, out var d))
                _difficulty = d;
        }
        catch { /* fall back to Standard */ }
    }

    private void SaveDifficulty()
    {
        try
        {
            Directory.CreateDirectory(MouseHouse.Core.SaveManager.SaveDirectory);
            File.WriteAllText(DifficultyPath, _difficulty.ToString());
        }
        catch { /* best-effort */ }
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
        _anims.Clear();
        Array.Clear(_lastTrick, 0, Seats);
        _lastTrickWinner = -1;
        _lastTrickAvailable = false;
        _peekActive = false;
        _played.Clear();
        Array.Clear(_voids, 0, _voids.Length);
        _moonSeat = -1;
        _moonAnimTimer = 0f;

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
        var h = _hands[seat];
        return _difficulty switch
        {
            Difficulty.Beginner => PickPassBeginner(h),
            Difficulty.Expert => PickPassExpert(h, seat),
            _ => PickPassStandard(h),
        };
    }

    /// <summary>Beginner: just dump the three highest by rank.</summary>
    private List<Card> PickPassBeginner(List<Card> h)
    {
        return h.OrderByDescending(RankOrder).Take(3).ToList();
    }

    /// <summary>Standard: highest 3 with spade-Q+ / heart bias.</summary>
    private List<Card> PickPassStandard(List<Card> h)
    {
        var ordered = h
            .OrderByDescending(c =>
            {
                int score = RankOrder(c);
                if (c.Suit == Suit.Spades && c.Rank >= 12) score += 30;
                if (c.Suit == Suit.Hearts) score += 5;
                return score;
            })
            .ToList();
        return new List<Card> { ordered[0], ordered[1], ordered[2] };
    }

    /// <summary>Expert pass: try to void a short suit; protect Q♠
    /// (never pass it unless across); pass low spades when we're
    /// long in spades so Q♠ has cover; pass low clubs to dodge
    /// the 2♣ lead. Picks 3 cards heuristically.</summary>
    private List<Card> PickPassExpert(List<Card> h, int seat)
    {
        // Bucket by suit.
        var bySuit = new Dictionary<Suit, List<Card>>();
        foreach (Suit s in Enum.GetValues(typeof(Suit))) bySuit[s] = new();
        foreach (var c in h) bySuit[c.Suit].Add(c);
        foreach (var list in bySuit.Values) list.Sort((a, b) => RankOrder(a).CompareTo(RankOrder(b)));

        var picks = new List<Card>(3);
        bool acrossPass = _passDir == 2;

        // 1. Try to void a 1-2-card suit (not hearts — discarding all
        //    hearts kills our moon-defense and our offload options).
        foreach (var (suit, cards) in bySuit.OrderBy(kv => kv.Value.Count))
        {
            if (suit == Suit.Hearts) continue;
            if (cards.Count == 0 || cards.Count > 2) continue;
            foreach (var c in cards)
            {
                if (picks.Count == 3) break;
                // Never pass Q♠ / A♠ / K♠ unless across; they're
                // dangerous in opponents' hands but extra-dangerous
                // in the hand directly to our left (who plays right
                // after us on a typical clubs lead).
                if (!acrossPass && c.Suit == Suit.Spades && c.Rank >= 12) continue;
                picks.Add(c);
            }
            if (picks.Count == 3) return picks;
        }

        // 2. Dump high hearts (J/Q/K/A) to reduce moon-shot risk.
        foreach (var c in bySuit[Suit.Hearts].OrderByDescending(RankOrder))
        {
            if (picks.Count == 3) break;
            if (RankOrder(c) >= 11 && !picks.Contains(c)) picks.Add(c);
        }
        if (picks.Count == 3) return picks;

        // 3. Pass 2♣ if we have it AND we're not the natural lead
        //    candidate this hand (we'll be forced to lead it).
        var twoClubs = bySuit[Suit.Clubs].FirstOrDefault(IsTwoOfClubs);
        if (twoClubs != null && !picks.Contains(twoClubs)) picks.Add(twoClubs);
        if (picks.Count == 3) return picks;

        // 4. Fill remaining with high cards (anything ≥ J, prefer
        //    non-spade non-heart to keep our suit structure).
        foreach (var c in h
            .Where(c => !picks.Contains(c))
            .OrderByDescending(c => RankOrder(c) + (c.Suit == Suit.Diamonds ? 2 : 0)))
        {
            if (picks.Count == 3) break;
            picks.Add(c);
        }
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
        // Compute the source position BEFORE removing the card so
        // the slide-to-trick animation starts where the user saw it.
        Vector2 from = HandSlotPosition(seat, c);
        _hands[seat].Remove(c);
        c.FaceUp = true;
        _trick[seat] = c;
        _trickOrder.Add(seat);
        if (c.Suit == Suit.Hearts) _heartsBroken = true;
        if (IsQueenOfSpades(c)) _heartsBroken = true;       // Q♠ also frees hearts

        // Card-counter book-keeping. Both the "played this hand" set
        // and the "this seat voided that suit" inference are used by
        // the Expert AI; cheap to maintain even when the player's
        // running Beginner / Standard.
        _played.Add(CardKey(c));
        if (_trickOrder.Count > 1)
        {
            // Following: a discard off the led suit reveals a void.
            var ledSuit = _trick[_trickLeader]!.Suit;
            if (c.Suit != ledSuit) _voids[seat, (int)ledSuit] = true;
        }

        // Queue the slide-to-trick animation. Input + AI are
        // blocked while any anim is in flight (see UpdatePlaying),
        // so the state machine stays in step with the visuals.
        Vector2 to = TrickCardPosition(seat);
        QueueAnim(c, from, to, CardPlayDuration);

        // Advance turn or queue the post-trick pause.
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

    /// <summary>Snapshot the about-to-resolve trick into the peek
    /// buffer, identify the winner, and start the slide-to-pile
    /// animation. FinalizeTrick fires when those four slides
    /// complete and is where the actual scoring lands.</summary>
    private void BeginTrickClear()
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

        // Snapshot for peek BEFORE the cards leave the trick area.
        for (int s = 0; s < Seats; s++) _lastTrick[s] = _trick[s];
        _lastTrickWinner = winner;
        _lastTrickAvailable = true;

        var pilePos = TakePilePosition(winner);
        for (int s = 0; s < Seats; s++)
        {
            if (_trick[s] == null) continue;
            QueueAnim(_trick[s]!, TrickCardPosition(s), pilePos, CardClearDuration,
                onComplete: null);
        }
        // FinalizeTrick fires when the last clear-anim completes
        // (we hook the very last animation's OnComplete since they
        // all share the same Duration).
        if (_anims.Count > 0)
        {
            _anims[^1].OnComplete = () => FinalizeTrick(winner);
        }
        else
        {
            FinalizeTrick(winner);
        }
    }

    private void FinalizeTrick(int winner)
    {
        for (int s = 0; s < Seats; s++)
            if (_trick[s] != null) _taken[winner].Add(_trick[s]!);
        Array.Clear(_trick, 0, Seats);
        _trickOrder.Clear();
        _firstTrick = false;
        _trickLeader = winner;
        _tricksWon[winner]++;
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

    // ── Animation helpers ───────────────────────────────────────────
    private void QueueAnim(Card c, Vector2 from, Vector2 to, float duration,
        Action? onComplete = null)
    {
        _anims.Add(new CardAnim
        {
            Card = c, From = from, To = to,
            Duration = duration, Elapsed = 0f,
            OnComplete = onComplete,
        });
    }

    /// <summary>Advance every active animation by <paramref name="delta"/>
    /// seconds. Completed anims fire their OnComplete and are removed
    /// from the list. Returns true while any anim is still active —
    /// input + AI gate on this so the state machine stays in step
    /// with the visuals.</summary>
    private bool TickAnims(float delta)
    {
        for (int i = _anims.Count - 1; i >= 0; i--)
        {
            var a = _anims[i];
            a.Elapsed += delta;
            if (a.Elapsed >= a.Duration)
            {
                _anims.RemoveAt(i);
                a.OnComplete?.Invoke();
            }
        }
        return _anims.Count > 0;
    }

    /// <summary>Lookup: if <paramref name="c"/> is animating, return
    /// its interpolated screen position (panel-local). Otherwise
    /// null — callers fall back to the static render path.</summary>
    private Vector2? AnimPositionFor(Card c)
    {
        foreach (var a in _anims) if (a.Card == c) return a.Position;
        return null;
    }

    /// <summary>Compute the panel-local position a specific card
    /// occupies in <paramref name="seat"/>'s hand, BEFORE it's
    /// removed for play. For AI seats this is approximate (the
    /// face-down stack origin); for the human it's the exact fan
    /// slot the card sits in.</summary>
    private Vector2 HandSlotPosition(int seat, Card c)
    {
        if (seat == Human)
        {
            var hand = _hands[seat];
            int idx = hand.IndexOf(c);
            if (idx < 0) idx = 0;
            int fanW = (hand.Count - 1) * HandFanStep + CardKit.CardW;
            int x0 = PanelW / 2 - fanW / 2;
            int y0 = PanelH - CardKit.CardH - 36;
            return new Vector2(x0 + idx * HandFanStep, y0);
        }
        // AI seats: just use the anchor; the user can't see the
        // exact card position anyway since the hand renders face-down.
        return SeatHandAnchor(seat);
    }

    /// <summary>Where each seat's won-cards pile renders — also the
    /// destination for the slide-to-pile clear animation. Anchored
    /// next to the seat's name label so the user sees the cards
    /// "going to" the right player.</summary>
    private static Vector2 TakePilePosition(int seat)
    {
        var anchor = SeatNamePosition(seat);
        return seat switch
        {
            0 => new Vector2(anchor.X + 80, anchor.Y - 20),
            1 => new Vector2(anchor.X + 80, anchor.Y + 24),
            2 => new Vector2(anchor.X + 80, anchor.Y + 24),
            3 => new Vector2(anchor.X - 70, anchor.Y + 24),
            _ => anchor,
        };
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
        _moonSeat = -1;
        for (int s = 0; s < Seats; s++)
            if (_scoresHand[s] == 26) _moonSeat = s;
        if (_moonSeat >= 0)
        {
            for (int s = 0; s < Seats; s++)
                _scoresHand[s] = s == _moonSeat ? 0 : 26;
            _moonShots[_moonSeat]++;
            // Run the moon reveal animation BEFORE settling totals
            // + transitioning to HandEnd — visually the reveal
            // happens, then the totals tick up in the end-of-hand
            // modal that follows.
            _phase = Phase.MoonReveal;
            _moonAnimTimer = MoonAnimDuration;
            return;
        }
        FinishHandScoring();
    }

    /// <summary>Settle hand scores into running totals and pick the
    /// next phase (HandEnd modal or GameEnd if someone hit 100).
    /// Split out so the moon-reveal path can defer this until its
    /// animation finishes.</summary>
    private void FinishHandScoring()
    {
        for (int s = 0; s < Seats; s++) _scoresTotal[s] += _scoresHand[s];
        _phase = Phase.HandEnd;
        if (_scoresTotal.Any(p => p >= 100)) _phase = Phase.GameEnd;
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

        Card pick = _difficulty switch
        {
            Difficulty.Beginner => PickPlayBeginner(legal),
            Difficulty.Expert => PickPlayExpert(_waitingFor, legal),
            _ => PickPlayStandard(_waitingFor, legal),
        };
        PlayCard(_waitingFor, pick);
    }

    private Card PickPlayBeginner(List<Card> legal)
    {
        // Beginner: random legal card. No Q♠ dump heuristic, no
        // ducking — they'll occasionally win tricks they shouldn't,
        // which makes them play "loose" the way a casual human would.
        return legal[_rng.Next(legal.Count)];
    }

    private Card PickPlayStandard(int seat, List<Card> legal)
    {
        bool following = _trickOrder.Count > 0;
        if (following && legal.Any(IsQueenOfSpades)
            && _trick[_trickLeader]!.Suit != Suit.Spades)
        {
            // Dump Q♠ on a non-spade trick (someone else takes it).
            return legal.First(IsQueenOfSpades);
        }
        return legal.OrderBy(RankOrder).First();
    }

    /// <summary>
    /// Expert play. Branches on situation: leading vs following,
    /// safe vs risky to take, moon-shot defense if one opponent is
    /// hoarding hearts. Not perfect Hearts AI by any stretch — just
    /// notably stronger than Standard.
    /// </summary>
    private Card PickPlayExpert(int seat, List<Card> legal)
    {
        bool following = _trickOrder.Count > 0;

        // Moon-shot defense: is any opponent threatening to take all
        // 26? We say yes when one non-self seat has ≥ 5 hearts taken
        // AND no other seat has taken any hearts or Q♠ yet.
        int threatSeat = DetectMoonThreat(seat);

        if (following)
        {
            var lead = _trick[_trickLeader]!.Suit;

            // Q♠ dump on non-spade follow.
            if (lead != Suit.Spades && legal.Any(IsQueenOfSpades))
                return legal.First(IsQueenOfSpades);

            // Moon defense: take this trick if it has hearts in it
            // and we have a card that beats the current high. We
            // grant the threat their hearts unless WE can break
            // it cheaply.
            if (threatSeat >= 0 && threatSeat != seat
                && _trick.Any(c => c != null && c.Suit == Suit.Hearts))
            {
                var follow = legal.Where(c => c.Suit == lead).ToList();
                if (follow.Count > 0)
                {
                    int currentHigh = _trick
                        .Where(c => c != null && c.Suit == lead)
                        .Select(c => RankOrder(c!))
                        .Max();
                    var winning = follow.Where(c => RankOrder(c) > currentHigh)
                        .OrderBy(RankOrder).FirstOrDefault();
                    if (winning != null) return winning;
                }
            }

            // Following the lead suit: duck (highest card below the
            // current high), unless we can safely take the trick
            // because no points are in it AND we're leading the
            // hand anyway.
            var followSuit = legal.Where(c => c.Suit == lead).ToList();
            if (followSuit.Count > 0)
            {
                int currentHigh = _trick
                    .Where(c => c != null && c.Suit == lead)
                    .Select(c => RankOrder(c!))
                    .Max();
                var duck = followSuit.Where(c => RankOrder(c) < currentHigh)
                    .OrderByDescending(RankOrder).FirstOrDefault();
                if (duck != null) return duck;
                // Forced to take the trick — play smallest of the suit.
                return followSuit.OrderBy(RankOrder).First();
            }

            // Off-suit: dump the most dangerous card we can.
            // Priority: Q♠ → K♠ / A♠ (if Q♠ hasn't fallen) → high hearts.
            if (legal.Any(IsQueenOfSpades)) return legal.First(IsQueenOfSpades);
            bool qsStillOut = !_played.Contains((int)Suit.Spades * 16 + 12);
            if (qsStillOut)
            {
                var bigSpades = legal.Where(c => c.Suit == Suit.Spades && c.Rank >= 13)
                    .OrderByDescending(RankOrder).FirstOrDefault();
                if (bigSpades != null) return bigSpades;
            }
            var highHeart = legal.Where(c => c.Suit == Suit.Hearts)
                .OrderByDescending(RankOrder).FirstOrDefault();
            if (highHeart != null) return highHeart;
            return legal.OrderByDescending(RankOrder).First();
        }

        // Leading. Default to a safe low card; prefer clubs and
        // diamonds; lead hearts only when forced.
        var byPriority = legal
            .OrderBy(c => c.Suit == Suit.Hearts ? 100 : (int)c.Suit)
            .ThenBy(RankOrder)
            .ToList();
        return byPriority.First();
    }

    /// <summary>Returns the seat we suspect of shooting the moon,
    /// or -1 if no current threat. Heuristic: one seat has ≥ 5
    /// hearts taken and no other seat has taken any heart or Q♠.</summary>
    private int DetectMoonThreat(int self)
    {
        int candidate = -1;
        for (int s = 0; s < Seats; s++)
        {
            int heartsTaken = _taken[s].Count(c => c.Suit == Suit.Hearts);
            bool tookQs = _taken[s].Any(IsQueenOfSpades);
            if (heartsTaken >= 5 || tookQs)
            {
                if (candidate >= 0) return -1;   // more than one taker — no moon threat
                candidate = s;
            }
        }
        return candidate == self ? -1 : candidate;
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
        var menuItems = new[] { "New Game", $"Difficulty: {_difficulty}", "Last Trick", "Help" };
        int m = RetroWidgets.MenuBarHitTest(menuBar, menuItems, local, leftPressed);
        if (m == 0) { StartNewMatch(); return; }
        if (m == 1) { CycleDifficulty(); return; }
        if (m == 2)
        {
            // Open the peek if we have a trick to peek at AND we're
            // not in the middle of one.
            if (_lastTrickAvailable && _trickOrder.Count == 0
                && _phase == Phase.Playing && _anims.Count == 0)
            {
                _peekActive = true;
            }
            return;
        }
        if (m == 3) { _help.Visible = !_help.Visible; return; }
        if (_help.HandleInput(local, leftPressed, PanelSize)) return;

        switch (_phase)
        {
            case Phase.Passing: UpdatePassing(local, leftPressed); break;
            case Phase.Playing: UpdatePlaying(delta, local, leftPressed, rightPressed); break;
            case Phase.MoonReveal: UpdateMoonReveal(delta); break;
            case Phase.HandEnd: UpdateHandEnd(local, leftPressed); break;
            case Phase.GameEnd: UpdateGameEnd(local, leftPressed); break;
        }
    }

    private void UpdateMoonReveal(float delta)
    {
        _moonAnimTimer -= delta;
        if (_moonAnimTimer <= 0) FinishHandScoring();
    }

    private void CycleDifficulty()
    {
        _difficulty = _difficulty switch
        {
            Difficulty.Beginner => Difficulty.Standard,
            Difficulty.Standard => Difficulty.Expert,
            _ => Difficulty.Beginner,
        };
        SaveDifficulty();
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

    private void UpdatePlaying(float delta, Vector2 local, bool leftPressed, bool rightPressed)
    {
        // Pump animations first — they gate every other action so
        // the visible card-slide finishes before the next player
        // acts (or before the trick clears to the winner's pile).
        bool animActive = TickAnims(delta);

        // Peek toggle. Right-click anywhere inside the trick area
        // pops the last completed trick face-up; click anywhere
        // dismisses. Only available between tricks (no active anim,
        // no current play in progress).
        if (_peekActive)
        {
            if (leftPressed || rightPressed) _peekActive = false;
            return;
        }
        if (rightPressed && _lastTrickAvailable && _trickOrder.Count == 0
            && !animActive)
        {
            var peekArea = new Rectangle(
                PanelW / 2 - 100, PanelH / 2 - 70, 200, 140);
            if (RetroSkin.PointInRect(local, peekArea))
            {
                _peekActive = true;
                return;
            }
        }

        if (animActive) return;

        // Pause to let the user see a completed trick. After the
        // pause, queue the slide-to-pile clear animation; that
        // anim's OnComplete fires FinalizeTrick.
        if (_trickResolveTimer > 0)
        {
            _trickResolveTimer -= delta;
            if (_trickResolveTimer <= 0) BeginTrickClear();
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
        RetroWidgets.MenuBarVisual(menuBar,
            new[] { "New Game", $"Difficulty: {_difficulty}", "Last Trick", "Help" }, -1);

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
        if (_phase == Phase.MoonReveal) DrawMoonReveal(panelOffset);
        if (_peekActive) DrawPeek(panelOffset);
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
        // Cards in the trick slot — using animation position when
        // one's in flight, otherwise the static slot position.
        for (int s = 0; s < Seats; s++)
        {
            if (_trick[s] == null) continue;
            var anim = AnimPositionFor(_trick[s]!);
            var pos = (anim ?? TrickCardPosition(s)) + panelOffset;
            CardKit.DrawCard(_trick[s]!, pos);
        }
        // Animations may also be in flight for cards that are no
        // longer in _trick (slide-to-pile after trick clear). Draw
        // those too — they're not in any other render path while
        // mid-air.
        foreach (var a in _anims)
        {
            bool inTrick = false;
            for (int s = 0; s < Seats; s++)
                if (_trick[s] == a.Card) { inTrick = true; break; }
            if (inTrick) continue;
            CardKit.DrawCard(a.Card, a.Position + panelOffset);
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

        // Sort by score ascending so the winner's at the top of the
        // standings.
        var standings = Enumerable.Range(0, Seats)
            .OrderBy(s => _scoresTotal[s])
            .ThenBy(s => s)
            .ToList();

        // Flavor stats: who shot the moon (and how many times), who
        // took the most tricks.
        var flavorLines = new List<string>();
        int totalMoons = _moonShots.Sum();
        if (totalMoons > 0)
        {
            for (int s = 0; s < Seats; s++)
            {
                if (_moonShots[s] > 0)
                {
                    flavorLines.Add(_moonShots[s] == 1
                        ? $"🌙 {_names[s]} shot the moon."
                        : $"🌙 {_names[s]} shot the moon ×{_moonShots[s]}.");
                }
            }
        }
        int mostTricks = _tricksWon.Max();
        if (mostTricks > 0)
        {
            int taker = Array.IndexOf(_tricksWon, mostTricks);
            flavorLines.Add($"Most tricks: {_names[taker]} ({mostTricks}).");
        }

        DrawCenteredCard(panelOffset, 400, 280,
            $"Game over — {_names[winner]} wins!", () =>
        {
            int rowY = 0;
            for (int rank = 0; rank < standings.Count; rank++)
            {
                int s = standings[rank];
                string medal = rank == 0 ? "🏆 " : $"{rank + 1}. ";
                string row = $"{medal}{_names[s],-13}  {_scoresTotal[s],3} pts";
                RetroSkin.DrawText(row,
                    (int)panelOffset.X + PanelW / 2 - 110,
                    (int)panelOffset.Y + PanelH / 2 - 90 + rowY,
                    s == winner
                        ? new Color((byte)80, (byte)240, (byte)80, (byte)255)
                        : RetroSkin.BodyText,
                    RetroSkin.BodyFontSize - 1);
                rowY += 18;
            }
            // Flavor lines below the standings.
            int fy = rowY + 8;
            foreach (var line in flavorLines)
            {
                RetroSkin.DrawText(line,
                    (int)panelOffset.X + PanelW / 2 - 110,
                    (int)panelOffset.Y + PanelH / 2 - 90 + fy,
                    RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);
                fy += 16;
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
        // Lightweight yellow banner (used for one-shot status lines).
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

    /// <summary>
    /// Full-screen moon-shot reveal. Holds for MoonAnimDuration
    /// before the end-of-hand modal fires, with three stages:
    /// (1) dark dimmer fades in, (2) a glowing moon graphic + the
    /// big "🌙 X shot the moon!" header eases in, (3) the score-
    /// reversal explainer rolls in below. Stars expand outward
    /// from the moon for the full duration.
    /// </summary>
    private void DrawMoonReveal(Vector2 panelOffset)
    {
        if (_moonSeat < 0) return;
        float t = 1f - (_moonAnimTimer / MoonAnimDuration);   // 0 → 1
        // Stage 1: dimmer.
        byte dim = (byte)(Math.Clamp(t * 2f, 0f, 1f) * 200);
        Raylib.DrawRectangle((int)panelOffset.X, (int)panelOffset.Y,
            PanelW, PanelH, new Color((byte)0, (byte)0, (byte)16, dim));

        // Moon body — a softly-bevelled cream disc with a couple of
        // crater dots. Centred, easing up from y+40 to y.
        int cx = (int)panelOffset.X + PanelW / 2;
        int cy = (int)panelOffset.Y + PanelH / 2 - 30
                  - (int)(Math.Clamp(t * 1.5f, 0f, 1f) * 0);
        float entryT = Math.Clamp(t * 1.4f, 0f, 1f);
        int moonR = (int)(48 * entryT);
        if (moonR > 6)
        {
            // Outer glow rings.
            for (int g = 4; g > 0; g--)
            {
                Raylib.DrawCircle(cx, cy, moonR + g * 6,
                    new Color((byte)244, (byte)232, (byte)180,
                        (byte)(60 / g)));
            }
            Raylib.DrawCircle(cx, cy, moonR,
                new Color((byte)252, (byte)244, (byte)208, (byte)255));
            Raylib.DrawCircle(cx, cy, moonR - 2,
                new Color((byte)240, (byte)232, (byte)196, (byte)255));
            // Craters.
            Raylib.DrawCircle(cx - 16, cy - 12, 6,
                new Color((byte)210, (byte)200, (byte)164, (byte)255));
            Raylib.DrawCircle(cx + 18, cy + 8, 4,
                new Color((byte)210, (byte)200, (byte)164, (byte)255));
            Raylib.DrawCircle(cx - 4, cy + 18, 3,
                new Color((byte)210, (byte)200, (byte)164, (byte)255));
        }

        // Sparkles — 8 little stars expanding outward.
        if (t > 0.15f)
        {
            float sparkT = Math.Clamp((t - 0.15f) * 1.4f, 0f, 1f);
            for (int i = 0; i < 8; i++)
            {
                float ang = i * MathF.PI / 4f + t * 0.6f;
                int r = (int)(70 + sparkT * 90);
                int sx = cx + (int)(MathF.Cos(ang) * r);
                int sy = cy + (int)(MathF.Sin(ang) * r);
                byte alpha = (byte)((1f - sparkT) * 255);
                Raylib.DrawRectangle(sx - 1, sy - 1, 2, 2,
                    new Color((byte)255, (byte)244, (byte)180, alpha));
                Raylib.DrawRectangle(sx - 2, sy, 4, 1,
                    new Color((byte)255, (byte)244, (byte)180, (byte)(alpha / 2)));
                Raylib.DrawRectangle(sx, sy - 2, 1, 4,
                    new Color((byte)255, (byte)244, (byte)180, (byte)(alpha / 2)));
            }
        }

        // Header text — eases in below the moon.
        if (t > 0.25f)
        {
            float headerT = Math.Clamp((t - 0.25f) * 1.6f, 0f, 1f);
            byte a = (byte)(headerT * 255);
            string header = $"🌙 {_names[_moonSeat]} SHOT THE MOON!";
            int hw = RetroSkin.MeasureText(header, RetroSkin.TitleFontSize + 4);
            RetroSkin.DrawText(header, cx - hw / 2, cy + 64,
                new Color((byte)252, (byte)244, (byte)208, a),
                RetroSkin.TitleFontSize + 4);
        }
        // Explainer line.
        if (t > 0.45f)
        {
            byte a = (byte)(Math.Clamp((t - 0.45f) * 2f, 0f, 1f) * 255);
            string explain = $"+26 to everyone else, 0 to {_names[_moonSeat]}";
            int ew = RetroSkin.MeasureText(explain, RetroSkin.BodyFontSize);
            RetroSkin.DrawText(explain, cx - ew / 2, cy + 100,
                new Color((byte)232, (byte)216, (byte)180, a),
                RetroSkin.BodyFontSize);
        }
    }

    /// <summary>Last-trick peek: dimmed backdrop + the 4 cards from
    /// the most recently-resolved trick face-up in the centre, with
    /// the winner's slot outlined in gold. Click anywhere to
    /// dismiss (handled in Update).</summary>
    private void DrawPeek(Vector2 panelOffset)
    {
        Raylib.DrawRectangle((int)panelOffset.X, (int)panelOffset.Y,
            PanelW, PanelH, new Color((byte)0, (byte)0, (byte)0, (byte)170));

        // Frame around the centre area where peek cards land.
        int cx = (int)panelOffset.X + PanelW / 2;
        int cy = (int)panelOffset.Y + PanelH / 2;

        RetroSkin.DrawText("Last trick", cx - 30, cy - 100,
            new Color((byte)244, (byte)200, (byte)80, (byte)255),
            RetroSkin.BodyFontSize - 1);
        for (int s = 0; s < Seats; s++)
        {
            if (_lastTrick[s] == null) continue;
            // Centre the 4 cards in a cardinal-cross layout, same
            // arrangement as during play.
            int dx = s switch { 0 => 0, 1 => -60, 2 => 0, 3 => 60, _ => 0 };
            int dy = s switch { 0 => 40, 1 => 0, 2 => -40, 3 => 0, _ => 0 };
            var pos = new Vector2(cx - CardKit.CardW / 2 + dx,
                                  cy - CardKit.CardH / 2 + dy);
            CardKit.DrawCard(_lastTrick[s]!, pos);
            if (s == _lastTrickWinner)
            {
                Raylib.DrawRectangleLines((int)pos.X - 3, (int)pos.Y - 3,
                    CardKit.CardW + 6, CardKit.CardH + 6,
                    new Color((byte)244, (byte)200, (byte)80, (byte)255));
                RetroSkin.DrawText("winner",
                    (int)pos.X + (CardKit.CardW - RetroSkin.MeasureText("winner",
                        RetroSkin.BodyFontSize - 3)) / 2,
                    (int)pos.Y + CardKit.CardH + 4,
                    new Color((byte)244, (byte)200, (byte)80, (byte)255),
                    RetroSkin.BodyFontSize - 3);
            }
        }
        RetroSkin.DrawText("click to dismiss",
            cx - 50, cy + 100,
            new Color((byte)200, (byte)200, (byte)200, (byte)200),
            RetroSkin.BodyFontSize - 2);
    }
}
