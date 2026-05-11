using System.Text.Json.Serialization;

namespace MouseHouse.Net.Buddies;

/// <summary>
/// Wire-format envelope for messages sent to a friend's inbox topic.
/// Always sealed with <see cref="BuddyCrypto.Seal"/> before
/// publishing — the broker only ever sees the ciphertext.
///
/// Kinds:
///   request — initial friend request (sender's pubkey + name).
///   accept  — recipient's reply to a request (their pubkey + name).
///   challenge — invite to play a game (generic stub; golf uses golf_race).
///   golf_race — netplay-golf race envelope; <see cref="GolfRace"/>
///     carries the per-lifecycle-stage payload (challenge / accept /
///     decline / stroke / hole_complete / finish / disconnect).
///   chat — chat message body in <see cref="Text"/>.
/// </summary>
public sealed class InboxEnvelope
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("from_code")] public string FromCode { get; set; } = "";
    [JsonPropertyName("from_pubkey_b64")] public string FromPublicKeyB64 { get; set; } = "";
    [JsonPropertyName("from_name")] public string FromName { get; set; } = "";
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("game")] public string? Game { get; set; }
    [JsonPropertyName("nonce")] public string? Nonce { get; set; }
    [JsonPropertyName("sent_at")] public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Populated when <see cref="Kind"/> == "golf_race".</summary>
    [JsonPropertyName("golf_race")] public GolfRacePayload? GolfRace { get; set; }
}

/// <summary>
/// Per-stage netplay-golf payload carried inside an inbox envelope.
/// Versioned via <see cref="Protocol"/> ("golf_race_v1") so future
/// protocol changes can be rejected by old clients without
/// silently misinterpreting fields.
///
/// Subkinds (single string field rather than separate envelope kinds
/// so the outer envelope schema stays small):
/// <list type="bullet">
///   <item><c>challenge</c> — sender proposes a race
///     (Seed/Region/Difficulty populated).</item>
///   <item><c>accept</c> — recipient agreed; both clients open the
///     activity simultaneously.</item>
///   <item><c>decline</c> — recipient declined (or timed out).</item>
///   <item><c>stroke</c> — every hit, after the stroke counter
///     ticks; carries Hole + Stroke + ElapsedMs.</item>
///   <item><c>hole_complete</c> — ball-in-cup; carries Hole + Score
///     (running total of strokes so far in the round).</item>
///   <item><c>finish</c> — round complete; carries Score (total
///     strokes) + ElapsedMs (total ms since match start).</item>
///   <item><c>disconnect</c> — explicit "I'm leaving" signal so
///     the peer doesn't have to wait out the broker stale window.</item>
/// </list>
/// </summary>
public sealed class GolfRacePayload
{
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "golf_race_v1";
    [JsonPropertyName("sub")] public string Sub { get; set; } = "";

    // Populated on `challenge`. Recipient mirrors these into its
    // local NetplayGolfSession so the course generates identically.
    [JsonPropertyName("seed")] public int Seed { get; set; }
    [JsonPropertyName("region")] public string Region { get; set; } = "";
    [JsonPropertyName("difficulty")] public int Difficulty { get; set; }

    // Populated on `stroke`, `hole_complete`, `finish`.
    [JsonPropertyName("hole")] public int Hole { get; set; }
    [JsonPropertyName("stroke")] public int Stroke { get; set; }
    [JsonPropertyName("score")] public int Score { get; set; }
    [JsonPropertyName("elapsed_ms")] public long ElapsedMs { get; set; }
}

/// <summary>
/// Presence payload on the per-user retained topic. Not encrypted —
/// anyone who knows the user's friend code (and therefore the topic
/// id) can subscribe and see status. The away message field is
/// shown verbatim to friends; the user is responsible for not
/// putting secrets in it.
/// </summary>
public sealed class PresencePayload
{
    [JsonPropertyName("status")] public BuddyStatus Status { get; set; } = BuddyStatus.Offline;
    [JsonPropertyName("away_message")] public string AwayMessage { get; set; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("last_seen")] public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}
