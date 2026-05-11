namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Netplay-agnostic surface the Tetris activity calls when running
/// a head-to-head race. Same shape as the golf and chess sinks —
/// activity has read-only access to the peer's snapshot state for
/// the mini-board, plus push hooks for every local event the wire
/// cares about.
/// </summary>
public interface INetplayTetrisSink
{
    int Seed { get; }
    int StartingLevel { get; }
    string PeerName { get; }
    DateTime StartedAtUtc { get; }

    // Peer state, mirrored from inbound envelopes.
    /// <summary>Latest 200-cell board state we received from the
    /// peer (10×20, row-major, top row first). 0 = empty,
    /// 1..7 = piece type, 8 = garbage block. Array is replaced
    /// whole on each snapshot so iterators stay safe; never
    /// mutated in place.</summary>
    byte[] PeerBoard { get; }
    int PeerScore { get; }
    int PeerLines { get; }
    int PeerLevel { get; }
    int PeerPendingGarbage { get; }
    bool PeerToppedOut { get; }
    bool PeerDisconnected { get; }
    bool IsPeerStale { get; }

    /// <summary>Garbage rows the peer has sent us that we haven't
    /// applied yet. Drained when the activity calls
    /// <see cref="ConsumePendingGarbage"/> after the local piece
    /// locks. The activity's own counter mirrors this so the UI
    /// can show a warning indicator on the side of the board.</summary>
    int LocalPendingGarbage { get; }

    /// <summary>
    /// Drain the local-pending-garbage counter and return how many
    /// rows to rise from below on the next piece-lock. Called once
    /// per lock; idempotent within a lock cycle in the sense that
    /// the counter is zeroed atomically — a re-entrant call returns 0.
    /// </summary>
    int ConsumePendingGarbage();

    // Outbound events fired by the activity.
    /// <summary>Local lines just cleared. The activity reports the
    /// raw clear count + a kind tag; the session applies the
    /// garbage table, runs the cancel pass against
    /// <see cref="LocalPendingGarbage"/>, and sends the remainder
    /// over the wire.</summary>
    void OnLocalLinesCleared(int rows, string clearKind, bool perfectClear);

    /// <summary>Push the current 200-cell board snapshot to the peer.
    /// Activity calls this on every piece lock — the per-frame
    /// snapshot rate is bounded by the lock rate (~1-3/sec) so
    /// the channel doesn't flood.</summary>
    void PushBoardSnapshot(byte[] board, int score, int lines, int level);

    /// <summary>Local player topped out — piece couldn't spawn.
    /// Fires once; subsequent calls are no-ops on the session.</summary>
    void OnLocalTopOut();

    void OnLocalQuit();

    /// <summary>Persist + unregister, idempotent. Called from
    /// activity Close.</summary>
    void RecordAndUnregister();
}
