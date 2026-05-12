namespace MouseHouse.Scenes.Activities;

/// <summary>
/// One seat in a netplay-Hearts match. Carried over the wire +
/// mirrored into the activity so the table draws each player's
/// label correctly without dragging the BuddyService friend list
/// into the activity-sibling build.
/// </summary>
public sealed class NetplayHeartsSeat
{
    /// <summary>"host" / "friend" / "ai".</summary>
    public string Kind { get; set; } = "ai";
    public string Name { get; set; } = "";
    /// <summary>Friend code for kind == "friend"; empty otherwise.</summary>
    public string FriendCode { get; set; } = "";
}

/// <summary>
/// One canonical event the host broadcasts (or a peer submits to
/// the host). Same field set as the wire-level HeartsPayload but
/// in the Activities namespace so the sibling build doesn't drag
/// the Net.Buddies stack along.
/// </summary>
public sealed class NetplayHeartsEvent
{
    public string Sub { get; set; } = "";
    public int Seat { get; set; }
    public int CardKey { get; set; }
    public int[]? PassKeys { get; set; }
    public int TrickWinner { get; set; }
    public int[]? HandScores { get; set; }
    public int[]? TotalScores { get; set; }
    public int MoonSeat { get; set; } = -1;
    public int WinnerSeat { get; set; } = -1;
}

/// <summary>
/// Netplay-agnostic surface the Hearts activity calls when running
/// a 4-way race. Activity behaves identically on host and shadow
/// sides — both call IsHost (true for the challenger), but only
/// the host runs the canonical deal / validation logic.
/// </summary>
public interface INetplayHeartsSink
{
    bool IsHost { get; }
    int Seed { get; }
    int LocalSeat { get; }
    IReadOnlyList<NetplayHeartsSeat> Seats { get; }
    string Difficulty { get; }
    DateTime StartedAtUtc { get; }
    bool IsPeerStale { get; }

    /// <summary>Fired when the host broadcasts a canonical event
    /// (or, on the host side, when the host validates an incoming
    /// shadow play and is about to broadcast). Activity subscribes
    /// once in ConfigureNetplay and drives state transitions
    /// off this stream.</summary>
    event Action<NetplayHeartsEvent>? CanonicalEvent;

    void SubmitLocalPlay(int cardKey);
    void SubmitLocalPass(int[] cardKeys);
    void OnLocalQuit();
    void RecordAndUnregister();

    /// <summary>Host only — broadcast a canonical event to every
    /// non-self friend. Wire-level the host serializes one sealed
    /// envelope per recipient. No-op on shadows.</summary>
    void BroadcastCanonical(NetplayHeartsEvent ev);

    /// <summary>Host only — record the final stats for matches.json
    /// before RecordAndUnregister fires. Activity calls this after
    /// computing winner / moon-shot counts. No-op on shadows since
    /// the host's record is authoritative.</summary>
    void SetFinalStats(int[] finalScores, int[] moonShots, int winnerSeat);
}
