using MouseHouse.Scenes.Activities;

namespace MouseHouse.Net.Buddies;

/// <summary>
/// One in-progress netplay-golf race against a single peer. Owns
/// the wire-protocol state (last-known peer hole / stroke count /
/// finish status) and the per-side stopwatch. The activity
/// (<c>WorldTeeClassicActivity</c>) reads from this session to draw
/// the corner scoreboard, and pushes stroke / hole-complete /
/// finish events into it as the local player plays. Inbound peer
/// events arrive via <see cref="HandleInbound"/>, called from the
/// main-thread BuddyService drain.
///
/// One session per match; reuse is intentionally not supported —
/// a rematch creates a new session with a new seed.
/// </summary>
public sealed class NetplayGolfSession : INetplayGolfSink
{
    string INetplayGolfSink.PeerName => Peer.Nickname;
    /// <summary>Wire-protocol version this session understands. A
    /// peer with a mismatched version triggers <see cref="OnFatal"/>.</summary>
    public const string Protocol = "golf_race_v1";

    public BuddyService Svc { get; }
    public Friend Peer { get; }

    /// <summary>True on the side that issued the challenge.
    /// Cosmetic for the scoreboard ("you challenged X"); both sides
    /// run identical logic. Determined by who-clicked-Challenge.</summary>
    public bool IsHost { get; }

    /// <summary>Shared course seed — both sides generate the same
    /// terrain by feeding this into <c>WorldTeeClassicActivity</c>.</summary>
    public int Seed { get; }

    /// <summary>Region name (matches Globe.GlobePicker.Region.Name).</summary>
    public string Region { get; }

    /// <summary>Difficulty enum value, serialised as int across the wire.</summary>
    public int Difficulty { get; }

    public DateTime StartedAtUtc { get; private set; } = DateTime.UtcNow;

    // Local-side progress, mirrored from the activity as it plays.
    public int LocalHole { get; private set; }
    public int LocalStrokesThisHole { get; private set; }
    public int LocalTotalStrokes { get; private set; }
    public bool LocalFinished { get; private set; }
    public long LocalFinishMs { get; private set; }

    // Peer-side progress, mirrored from inbound events.
    public int PeerHole { get; private set; }
    public int PeerStrokesThisHole { get; private set; }
    public int PeerTotalStrokes { get; private set; }
    public bool PeerFinished { get; private set; }
    public long PeerFinishMs { get; private set; }
    public bool PeerDisconnected { get; private set; }

    public DateTime LastPeerMessageUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>
    /// 30 s after the last peer message we treat them as
    /// disconnected. The activity polls <see cref="IsPeerStale"/>
    /// to surface the toast and let the player finish solo.
    /// </summary>
    public static readonly TimeSpan PeerStaleAfter = TimeSpan.FromSeconds(30);

    public bool IsPeerStale =>
        !PeerFinished
        && !PeerDisconnected
        && DateTime.UtcNow - LastPeerMessageUtc > PeerStaleAfter;

    /// <summary>True if the local player wins by wall-clock. Tied
    /// finishes resolve as false (the scoreboard renders the tie
    /// separately).</summary>
    public bool LocalWon()
        => LocalFinished
        && (!PeerFinished || LocalFinishMs < PeerFinishMs);

    public event Action? StateChanged;
    public event Action<string>? Toast;

    public NetplayGolfSession(BuddyService svc, Friend peer, bool isHost,
        int seed, string region, int difficulty)
    {
        Svc = svc;
        Peer = peer;
        IsHost = isHost;
        Seed = seed;
        Region = region;
        Difficulty = difficulty;
    }

    // ── Outbound: the activity calls these as the local player plays ──

    public void OnLocalStroke(int hole, int strokesThisHole, int totalStrokes)
    {
        LocalHole = hole;
        LocalStrokesThisHole = strokesThisHole;
        LocalTotalStrokes = totalStrokes;
        SendPayload(new GolfRacePayload
        {
            Sub = "stroke",
            Hole = hole,
            Stroke = strokesThisHole,
            Score = totalStrokes,
            ElapsedMs = (long)(DateTime.UtcNow - StartedAtUtc).TotalMilliseconds,
        });
        StateChanged?.Invoke();
    }

    public void OnLocalHoleComplete(int hole, int strokesThisHole, int totalStrokes)
    {
        LocalHole = hole;
        LocalStrokesThisHole = strokesThisHole;
        LocalTotalStrokes = totalStrokes;
        SendPayload(new GolfRacePayload
        {
            Sub = "hole_complete",
            Hole = hole,
            Stroke = strokesThisHole,
            Score = totalStrokes,
            ElapsedMs = (long)(DateTime.UtcNow - StartedAtUtc).TotalMilliseconds,
        });
        StateChanged?.Invoke();
    }

    public void OnLocalFinish(int totalStrokes)
    {
        if (LocalFinished) return;
        LocalFinished = true;
        LocalTotalStrokes = totalStrokes;
        LocalFinishMs = (long)(DateTime.UtcNow - StartedAtUtc).TotalMilliseconds;
        SendPayload(new GolfRacePayload
        {
            Sub = "finish",
            Score = totalStrokes,
            ElapsedMs = LocalFinishMs,
        });
        StateChanged?.Invoke();
    }

    public void OnLocalQuit()
    {
        // Best-effort: peer sees an immediate "left" toast instead
        // of waiting out PeerStaleAfter.
        SendPayload(new GolfRacePayload { Sub = "disconnect" });
    }

    // ── Inbound: BuddyService routes here on golf_race envelopes ──

    public void HandleInbound(GolfRacePayload p)
    {
        // Reject anything that doesn't match our protocol version.
        // This is what makes the on-wire versioning load-bearing —
        // a future golf_race_v2 client talking to us would land
        // here and not silently corrupt our scoreboard.
        if (p.Protocol != Protocol) return;
        LastPeerMessageUtc = DateTime.UtcNow;

        switch (p.Sub)
        {
            case "stroke":
                PeerHole = p.Hole;
                PeerStrokesThisHole = p.Stroke;
                PeerTotalStrokes = p.Score;
                StateChanged?.Invoke();
                break;
            case "hole_complete":
                PeerHole = p.Hole;
                PeerStrokesThisHole = p.Stroke;
                PeerTotalStrokes = p.Score;
                StateChanged?.Invoke();
                break;
            case "finish":
                PeerFinished = true;
                PeerTotalStrokes = p.Score;
                PeerFinishMs = p.ElapsedMs;
                Toast?.Invoke(
                    LocalFinished
                        ? "Both done."
                        : $"🏆 {Peer.Nickname} finished first!");
                StateChanged?.Invoke();
                break;
            case "disconnect":
                PeerDisconnected = true;
                Toast?.Invoke($"{Peer.Nickname} disconnected.");
                StateChanged?.Invoke();
                break;
        }
    }

    private bool _recorded;

    public void RecordAndUnregister()
    {
        if (_recorded) return;
        _recorded = true;
        MatchHistory.Append(ToRecord());
        Svc.UnregisterGolfSession(this);
    }

    private void SendPayload(GolfRacePayload payload)
    {
        // Fire-and-forget; the await would block the activity's
        // main-thread Update otherwise. Failures are logged in
        // NetClient and the next stroke retries.
        _ = Svc.Client.SendGolfRace(Peer.Code, payload);
    }

    /// <summary>
    /// Produce the match-recap snapshot persisted to matches.json
    /// after the session ends. Called once both sides have finished
    /// (or the peer has disconnected / stalled).
    /// </summary>
    public MatchRecord ToRecord()
    {
        return new MatchRecord
        {
            EndedAtUtc = DateTime.UtcNow,
            PeerCode = Peer.Code,
            PeerName = Peer.Nickname,
            Region = Region,
            Difficulty = Difficulty,
            Seed = Seed,
            LocalStrokes = LocalTotalStrokes,
            LocalFinishMs = LocalFinishMs,
            LocalFinished = LocalFinished,
            PeerStrokes = PeerTotalStrokes,
            PeerFinishMs = PeerFinishMs,
            PeerFinished = PeerFinished,
            PeerDisconnected = PeerDisconnected,
        };
    }
}
