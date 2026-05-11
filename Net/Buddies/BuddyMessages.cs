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
///   challenge — invite to play a game.
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
