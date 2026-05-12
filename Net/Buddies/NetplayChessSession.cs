using MouseHouse.Scenes.Activities;

namespace MouseHouse.Net.Buddies;

/// <summary>
/// One in-progress netplay-chess race against a single peer. Owns
/// the wire-protocol state for the chess-race lifecycle (puzzle
/// queue, both sides' solve counts, who-finished-when) and the
/// per-side stopwatch. Mirrors <see cref="NetplayGolfSession"/> —
/// same registration model on <see cref="BuddyService"/>, same
/// peer-stale window, same RecordAndUnregister teardown.
///
/// One session per race; rematch creates a fresh session with a
/// freshly-fetched puzzle queue.
/// </summary>
public sealed class NetplayChessSession : INetplayChessSink
{
    public const string Protocol = "chess_race_v1";

    public BuddyService Svc { get; }
    public Friend Peer { get; }
    public bool IsHost { get; }
    public int TimeLimitSeconds { get; }
    public string StartingBand { get; }

    /// <summary>Shared puzzle queue — both sides hold byte-identical
    /// copies. Built on the challenger by pre-fetching from Lichess
    /// (or the bundled fallback if offline), packed into the
    /// challenge envelope, and reconstructed verbatim on accept.</summary>
    public IReadOnlyList<NetplayChessPuzzle> Puzzles { get; }

    public DateTime StartedAtUtc { get; private set; } = DateTime.UtcNow;
    string INetplayChessSink.PeerName => Peer.Nickname;

    // Local-side progress, set by the activity as it plays.
    public int LocalSolved { get; private set; }
    public int LocalPuzzleIndex { get; private set; }
    public long LocalLastSolveMs { get; private set; }
    public bool LocalFinished { get; private set; }

    // Peer-side progress, mirrored from inbound envelopes.
    public int PeerSolved { get; private set; }
    public int PeerPuzzleIndex { get; private set; }
    public long PeerLastSolveMs { get; private set; }
    public bool PeerFinished { get; private set; }
    public bool PeerDisconnected { get; private set; }
    public DateTime LastPeerMessageUtc { get; private set; } = DateTime.UtcNow;

    public static readonly TimeSpan PeerStaleAfter = TimeSpan.FromSeconds(30);

    public bool IsPeerStale =>
        !PeerFinished
        && !PeerDisconnected
        && DateTime.UtcNow - LastPeerMessageUtc > PeerStaleAfter;

    public event Action? StateChanged;
    public event Action<string>? Toast;

    public NetplayChessSession(BuddyService svc, Friend peer, bool isHost,
        int timeLimitSeconds, string startingBand,
        IReadOnlyList<NetplayChessPuzzle> puzzles)
    {
        Svc = svc;
        Peer = peer;
        IsHost = isHost;
        TimeLimitSeconds = timeLimitSeconds;
        StartingBand = startingBand;
        Puzzles = puzzles;
    }

    public bool LocalWon()
    {
        if (LocalSolved != PeerSolved) return LocalSolved > PeerSolved;
        // Faster last solve breaks ties. Never-solved means losing
        // the tiebreak — matches the MatchRecord rule.
        if (LocalLastSolveMs == 0) return false;
        if (PeerLastSolveMs == 0) return true;
        return LocalLastSolveMs < PeerLastSolveMs;
    }

    // ── Outbound: activity → wire ─────────────────────────────────────

    public void OnLocalSolved(int puzzleIndex, int solvedTotal)
    {
        LocalPuzzleIndex = puzzleIndex;
        LocalSolved = solvedTotal;
        LocalLastSolveMs = (long)(DateTime.UtcNow - StartedAtUtc).TotalMilliseconds;
        SendPayload(new ChessRacePayload
        {
            Sub = "puzzle_solved",
            PuzzleIndex = puzzleIndex,
            Score = solvedTotal,
            ElapsedMs = LocalLastSolveMs,
        });
        StateChanged?.Invoke();
    }

    public void OnLocalFailed(int puzzleIndex, int solvedTotal)
    {
        LocalPuzzleIndex = puzzleIndex;
        LocalSolved = solvedTotal;
        SendPayload(new ChessRacePayload
        {
            Sub = "puzzle_failed",
            PuzzleIndex = puzzleIndex,
            Score = solvedTotal,
            ElapsedMs = (long)(DateTime.UtcNow - StartedAtUtc).TotalMilliseconds,
        });
        StateChanged?.Invoke();
    }

    public void OnLocalFinish(int solvedTotal)
    {
        if (LocalFinished) return;
        LocalFinished = true;
        LocalSolved = solvedTotal;
        SendPayload(new ChessRacePayload
        {
            Sub = "finish",
            Score = solvedTotal,
            ElapsedMs = (long)(DateTime.UtcNow - StartedAtUtc).TotalMilliseconds,
        });
        StateChanged?.Invoke();
    }

    public void OnLocalQuit()
    {
        SendPayload(new ChessRacePayload { Sub = "disconnect" });
    }

    // ── Inbound: BuddyService → session ───────────────────────────────

    public void HandleInbound(ChessRacePayload p)
    {
        if (p.Protocol != Protocol) return;
        LastPeerMessageUtc = DateTime.UtcNow;
        switch (p.Sub)
        {
            case "puzzle_solved":
                PeerPuzzleIndex = p.PuzzleIndex;
                PeerSolved = p.Score;
                PeerLastSolveMs = p.ElapsedMs;
                StateChanged?.Invoke();
                break;
            case "puzzle_failed":
                PeerPuzzleIndex = p.PuzzleIndex;
                PeerSolved = p.Score;
                StateChanged?.Invoke();
                break;
            case "finish":
                PeerFinished = true;
                PeerSolved = p.Score;
                Toast?.Invoke(
                    LocalFinished
                        ? "Both done."
                        : (PeerSolved > LocalSolved
                            ? $"🏆 {Peer.Nickname} finished first!"
                            : $"{Peer.Nickname} ran out of time."));
                StateChanged?.Invoke();
                break;
            case "disconnect":
                PeerDisconnected = true;
                Toast?.Invoke($"{Peer.Nickname} disconnected.");
                StateChanged?.Invoke();
                break;
        }
    }

    private void SendPayload(ChessRacePayload payload)
        => _ = Svc.Client.SendChessRace(Peer.Code, payload);

    public MatchRecord ToRecord() => new MatchRecord
    {
        Kind = "chess",
        EndedAtUtc = DateTime.UtcNow,
        PeerCode = Peer.Code,
        PeerName = Peer.Nickname,
        TimeLimitSeconds = TimeLimitSeconds,
        StartingBand = StartingBand,
        PuzzleCount = Puzzles.Count,
        LocalSolved = LocalSolved,
        PeerSolved = PeerSolved,
        LocalLastSolveMs = LocalLastSolveMs,
        PeerLastSolveMs = PeerLastSolveMs,
        LocalFinished = LocalFinished,
        PeerFinished = PeerFinished,
        PeerDisconnected = PeerDisconnected,
    };

    private bool _recorded;
    public void RecordAndUnregister()
    {
        if (_recorded) return;
        _recorded = true;
        MatchHistory.Append(ToRecord());
        Svc.UnregisterChessSession(this);
    }
}
