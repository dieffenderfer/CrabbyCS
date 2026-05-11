using MouseHouse.Scenes.Activities;

namespace MouseHouse.Net.Buddies;

/// <summary>
/// One in-progress netplay-Hearts match. Host-mediated: the
/// challenger is the host and owns the canonical state (deal,
/// validation, scoring). Three shadow clients mirror the host's
/// state and submit local plays for validation.
///
/// Session is registered with <see cref="BuddyService"/> for the
/// host (so it can route per-friend envelopes) and on each
/// shadow (so the inbound queue knows where to deliver canonical
/// updates). Same RecordAndUnregister teardown the other races use.
///
/// 4-way fanout: the host's <see cref="BroadcastToOthers"/> serializes
/// one sealed envelope per non-self friend in the seat list —
/// BuddyCrypto's sealed-box construction is per-recipient so each
/// friend gets a separately-encrypted blob.
/// </summary>
public sealed class NetplayHeartsSession : INetplayHeartsSink
{
    public const string Protocol = "hearts_v1";

    public BuddyService Svc { get; }
    public bool IsHost { get; }
    public int Seed { get; }
    public string Difficulty { get; }
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;

    /// <summary>Canonical seating, set once at match start.
    /// Ordered seat 0..3; seat 0 is the host on both host and
    /// shadow sides (the host is always first in the seat list).</summary>
    public IReadOnlyList<NetplayHeartsSeat> Seats { get; }

    /// <summary>This client's seat in the canonical seating.
    /// Host = 0; each shadow is the seat whose FriendCode matches
    /// our identity.</summary>
    public int LocalSeat { get; }

    public DateTime LastInboundUtc { get; private set; } = DateTime.UtcNow;
    public static readonly TimeSpan PeerStaleAfter = TimeSpan.FromSeconds(30);
    public bool IsPeerStale =>
        DateTime.UtcNow - LastInboundUtc > PeerStaleAfter;

    // ── Activity bridge ─────────────────────────────────────────────
    /// <summary>Activity-namespace event stream. The session
    /// translates wire-level HeartsPayload → NetplayHeartsEvent
    /// before invoking so the activity doesn't see Net.Buddies
    /// types.</summary>
    public event Action<NetplayHeartsEvent>? CanonicalEvent;

    public NetplayHeartsSession(BuddyService svc, bool isHost, int seed,
        string difficulty, IReadOnlyList<NetplayHeartsSeat> seats, int localSeat)
    {
        Svc = svc;
        IsHost = isHost;
        Seed = seed;
        Difficulty = difficulty;
        Seats = seats;
        LocalSeat = localSeat;
    }

    // ── Inbound routing ─────────────────────────────────────────────
    public void HandleInbound(HeartsPayload p)
    {
        if (p.Protocol != Protocol) return;
        LastInboundUtc = DateTime.UtcNow;
        CanonicalEvent?.Invoke(ToActivityEvent(p));
    }

    private static NetplayHeartsEvent ToActivityEvent(HeartsPayload p)
        => new NetplayHeartsEvent
        {
            Sub = p.Sub,
            Seat = p.Seat,
            CardKey = p.CardKey,
            PassKeys = p.PassKeys?.ToArray(),
            TrickWinner = p.TrickWinner,
            HandScores = p.HandScores?.ToArray(),
            TotalScores = p.TotalScores?.ToArray(),
            MoonSeat = p.MoonSeat,
            WinnerSeat = p.WinnerSeat,
        };

    // ── Outbound — host fanout ──────────────────────────────────────
    /// <summary>Host: send a canonical event to every human seat
    /// other than the host (the host's own activity already has
    /// the state and doesn't echo back to itself). One sealed
    /// envelope per recipient.</summary>
    public void BroadcastToOthers(HeartsPayload payload)
    {
        if (!IsHost) return;
        foreach (var seat in Seats)
        {
            if (seat.Kind != "friend") continue;
            if (string.IsNullOrEmpty(seat.FriendCode)) continue;
            _ = Svc.Client.SendHearts(seat.FriendCode, payload);
        }
    }

    /// <summary>Shadow: send a peer→host event (typically
    /// pass_submitted or card_played). The host validates and
    /// rebroadcasts the canonical version.</summary>
    public void SendToHost(HeartsPayload payload)
    {
        if (IsHost) return;
        var hostSeat = Seats.FirstOrDefault(s => s.Kind == "host");
        if (hostSeat == null || string.IsNullOrEmpty(hostSeat.FriendCode)) return;
        _ = Svc.Client.SendHearts(hostSeat.FriendCode, payload);
    }

    // ── Sink interface used by the activity ─────────────────────────
    public void SubmitLocalPlay(int cardKey)
    {
        var p = new HeartsPayload
        {
            Sub = "card_played",
            Seat = LocalSeat,
            CardKey = cardKey,
        };
        if (IsHost) CanonicalEvent?.Invoke(ToActivityEvent(p));
        else SendToHost(p);
    }

    public void SubmitLocalPass(int[] cardKeys)
    {
        var p = new HeartsPayload
        {
            Sub = "pass_submitted",
            Seat = LocalSeat,
            PassKeys = cardKeys.ToList(),
        };
        if (IsHost) CanonicalEvent?.Invoke(ToActivityEvent(p));
        else SendToHost(p);
    }

    public void SetFinalStats(int[] finalScores, int[] moonShots, int winnerSeat)
    {
        if (finalScores != null) FinalScores = finalScores.ToArray();
        if (moonShots != null) MoonShots = moonShots.ToArray();
        WinnerSeat = winnerSeat;
    }

    /// <summary>Host: ship a canonical event to every non-self
    /// friend in the seat list. Translates back to HeartsPayload
    /// for the wire.</summary>
    public void BroadcastCanonical(NetplayHeartsEvent ev)
    {
        if (!IsHost) return;
        BroadcastToOthers(new HeartsPayload
        {
            Sub = ev.Sub,
            Seat = ev.Seat,
            CardKey = ev.CardKey,
            PassKeys = ev.PassKeys?.ToList(),
            TrickWinner = ev.TrickWinner,
            HandScores = ev.HandScores?.ToList(),
            TotalScores = ev.TotalScores?.ToList(),
            MoonSeat = ev.MoonSeat,
            WinnerSeat = ev.WinnerSeat,
        });
    }

    public void OnLocalQuit()
    {
        var p = new HeartsPayload { Sub = "disconnect", Seat = LocalSeat };
        if (IsHost) BroadcastToOthers(p);
        else SendToHost(p);
    }

    // ── Stats roll-up for MatchRecord ───────────────────────────────
    public int[] FinalScores = new int[4];
    public int[] MoonShots = new int[4];
    public int WinnerSeat = -1;

    public MatchRecord ToRecord() => new MatchRecord
    {
        Kind = "hearts",
        EndedAtUtc = DateTime.UtcNow,
        PeerCode = Seats.FirstOrDefault(s => s.Kind == "host")?.FriendCode ?? "",
        PeerName = Seats.FirstOrDefault(s => s.Kind == "host")?.Name ?? "",
        HeartsSeats = Seats.Select(s => s.Name).ToList(),
        HeartsFinalScores = FinalScores.ToList(),
        HeartsMoonShots = MoonShots.ToList(),
        HeartsWinnerSeat = WinnerSeat,
        HeartsLocalSeat = LocalSeat,
        DurationMs = (long)(DateTime.UtcNow - StartedAtUtc).TotalMilliseconds,
    };

    private bool _recorded;
    public void RecordAndUnregister()
    {
        if (_recorded) return;
        _recorded = true;
        MatchHistory.Append(ToRecord());
        Svc.UnregisterHeartsSession(this);
    }
}
