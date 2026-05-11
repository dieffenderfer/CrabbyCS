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
/// Netplay-agnostic surface the Hearts activity calls when running
/// a 4-way race. Mirrors the per-game sinks for golf / chess /
/// tetris in shape; richer because Hearts has both host and
/// shadow roles.
///
/// Activity behaves identically on host and shadow sides — both
/// call IsHost (true for the challenger; the canonical-state
/// owner), but only the host runs the deal / shuffle / validation
/// logic locally. Shadows submit local plays via
/// <see cref="SubmitLocalPlay"/> and update from the host's
/// canonical broadcasts (<see cref="ApplyCanonicalPlay"/> via the
/// activity's HandleInbound path) rather than from their own
/// trick simulation.
/// </summary>
public interface INetplayHeartsSink
{
    bool IsHost { get; }
    /// <summary>Shared deal seed — both sides feed this into the
    /// activity's RNG so the initial 13-card hands are identical.
    /// Host generated, broadcast in the start_match envelope.</summary>
    int Seed { get; }
    /// <summary>This client's seat index in the canonical seating
    /// (0..3). Host sets up the seating; shadows learn it from
    /// the start_match envelope.</summary>
    int LocalSeat { get; }
    /// <summary>All four seats in canonical order. AI seats are
    /// played by the host locally; their plays come through the
    /// same canonical broadcast path as remote human plays.</summary>
    IReadOnlyList<NetplayHeartsSeat> Seats { get; }
    string Difficulty { get; }
    DateTime StartedAtUtc { get; }
    bool IsPeerStale { get; }

    /// <summary>Submit a card play from the local human. Host-side
    /// this short-circuits straight to the validator; shadow-side
    /// it ships to the host and waits for the canonical broadcast.</summary>
    void SubmitLocalPlay(int cardKey);

    /// <summary>Submit pass picks (3 card keys) at the start of a
    /// hand. Same routing model as SubmitLocalPlay.</summary>
    void SubmitLocalPass(int[] cardKeys);

    /// <summary>Local player closed the window mid-match. Sends
    /// disconnect on the wire so the host can AI-substitute.</summary>
    void OnLocalQuit();

    /// <summary>Persist + unregister, idempotent. Called from
    /// activity Close.</summary>
    void RecordAndUnregister();
}
