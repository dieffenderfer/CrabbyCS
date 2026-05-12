namespace MouseHouse.Scenes.Activities;

/// <summary>
/// One puzzle, as the chess activity sees it after the netplay
/// envelope is unpacked. Same shape as the
/// <c>MouseHouse.Net.Buddies.ChessRacePuzzle</c> wire type — the
/// session copies fields across so the activity doesn't have to
/// take a dependency on Net.Buddies (would drag MQTT/NSec into the
/// sibling/standalone activity host builds, same problem golf
/// already solved with INetplayGolfSink).
/// </summary>
public sealed class NetplayChessPuzzle
{
    public string Id { get; set; } = "";
    public string Pgn { get; set; } = "";
    public string[] Solution { get; set; } = Array.Empty<string>();
    public int Rating { get; set; }
    public string? Fen { get; set; }
    public string? ExpectedUci { get; set; }
    public string? Title { get; set; }
    public bool WhiteToMove { get; set; } = true;
}

/// <summary>
/// Netplay-agnostic surface the chess activity calls when running
/// a head-to-head race. Mirrors <see cref="INetplayGolfSink"/> in
/// spirit — read-only state for the corner scoreboard, plus push
/// hooks the activity fires as the local player progresses.
/// </summary>
public interface INetplayChessSink
{
    int TimeLimitSeconds { get; }
    string StartingBand { get; }
    string PeerName { get; }
    DateTime StartedAtUtc { get; }

    /// <summary>The full shared puzzle queue the host pre-fetched.</summary>
    IReadOnlyList<NetplayChessPuzzle> Puzzles { get; }

    // Peer state, mirrored from inbound envelopes.
    int PeerSolved { get; }
    int PeerPuzzleIndex { get; }
    long PeerLastSolveMs { get; }
    bool PeerFinished { get; }
    bool PeerDisconnected { get; }
    bool IsPeerStale { get; }

    // Local state, mirrored so the scoreboard can read it without
    // the activity having to pass it back through every method.
    int LocalSolved { get; }
    long LocalLastSolveMs { get; }
    bool LocalFinished { get; }

    /// <summary>True if local wins by score + tiebreak rules.</summary>
    bool LocalWon();

    void OnLocalSolved(int puzzleIndex, int solvedTotal);
    void OnLocalFailed(int puzzleIndex, int solvedTotal);
    void OnLocalFinish(int solvedTotal);
    void OnLocalQuit();

    /// <summary>Persist + unregister, idempotent. Called from
    /// activity Close.</summary>
    void RecordAndUnregister();
}
