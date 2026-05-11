namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Tiny netplay-agnostic surface the activity calls to push race
/// events into its <see cref="MouseHouse.Net.Buddies.NetplayGolfSession"/>.
/// Lives in the same folder as the activity so the
/// MouseHouse.Activities sibling project (which compiles
/// WorldTeeClassicActivity.cs but doesn't pull in MQTT / NSec) can
/// build without dragging the whole netplay stack along — the
/// sibling process never runs netplay golf, so it just leaves the
/// sink null.
///
/// Read-only fields the activity needs to build the course
/// identically on both sides:
/// <list type="bullet">
///   <item><see cref="Seed"/> — course generation seed.</item>
///   <item><see cref="Region"/> — region name to match against
///     <c>Globe.GlobePicker.Regions</c>.</item>
///   <item><see cref="Difficulty"/> — Difficulty enum value.</item>
///   <item><see cref="PeerName"/> — display name used in scoreboard / toasts.</item>
/// </list>
/// </summary>
public interface INetplayGolfSink
{
    int Seed { get; }
    string Region { get; }
    int Difficulty { get; }
    string PeerName { get; }

    // Local-side progress mirrors (for scoreboard render).
    int PeerHole { get; }
    int PeerTotalStrokes { get; }
    bool PeerFinished { get; }
    long PeerFinishMs { get; }
    bool PeerDisconnected { get; }
    bool IsPeerStale { get; }
    DateTime StartedAtUtc { get; }
    long LocalFinishMs { get; }
    bool LocalFinished { get; }

    /// <summary>Race winner by wall-clock; ties resolve as false.</summary>
    bool LocalWon();

    // Outbound events fired by the activity at each game-state change.
    void OnLocalStroke(int hole, int strokesThisHole, int totalStrokes);
    void OnLocalHoleComplete(int hole, int strokesThisHole, int totalStrokes);
    void OnLocalFinish(int totalStrokes);
    void OnLocalQuit();

    /// <summary>Persist the final match record to matches.json and
    /// remove this session from the routing table. Called on Close.
    /// Idempotent — repeat calls are no-ops.</summary>
    void RecordAndUnregister();
}
