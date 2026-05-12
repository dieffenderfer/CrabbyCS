using System.Text.Json.Serialization;

namespace MouseHouse.Net.Buddies;

/// <summary>
/// AIM-style buddy status. <see cref="Offline"/> means "presence
/// channel is empty or stale" (we haven't seen them recently);
/// <see cref="Invisible"/> is a self-only state — friends always see
/// us as Offline when we're Invisible. Idle is set automatically
/// after some minutes of input inactivity; the other three are
/// user-picked.
/// </summary>
public enum BuddyStatus
{
    Offline = 0,
    Online = 1,
    Idle = 2,
    Away = 3,
    Busy = 4,         // "Do Not Disturb" / Busy — shown red.
    Invisible = 5,    // self-only; broadcast as Offline.
}

/// <summary>
/// One row in the user's buddy list. Persisted in <c>friends.json</c>;
/// also gets in-memory presence fields updated as their presence
/// retained-MQTT message changes.
/// </summary>
public sealed class Friend
{
    /// <summary>Friend's 12-char Crockford-base32 code.</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    /// <summary>32-byte Curve25519 public key (base64-url), known
    /// from the friend-request handshake. Without this, sealed-box
    /// encryption to the friend isn't possible — a Friend with an
    /// empty PublicKey means the request hasn't completed yet.</summary>
    [JsonPropertyName("public_key_b64")]
    public string PublicKeyB64 { get; set; } = "";

    /// <summary>User-editable nickname (what we call them in our
    /// buddy list — independent of whatever they call themselves).
    /// Defaults to whatever DisplayName they sent in the request.</summary>
    [JsonPropertyName("nickname")]
    public string Nickname { get; set; } = "";

    /// <summary>Optional free-text notes the user keeps about this
    /// buddy. Visible only to us. Not sent over the wire.</summary>
    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "";

    /// <summary>UTC timestamp of when the friend request was
    /// accepted. Useful for "oldest friend" sorting or troubleshooting.</summary>
    [JsonPropertyName("added_at")]
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;

    // --- In-memory presence state. Cached to disk so the buddy list
    //     can be rendered before the broker connects on launch; the
    //     authoritative value gets refreshed once we subscribe to the
    //     friend's presence topic. ---

    [JsonPropertyName("last_status")]
    public BuddyStatus LastStatus { get; set; } = BuddyStatus.Offline;

    [JsonPropertyName("last_status_message")]
    public string LastStatusMessage { get; set; } = "";

    [JsonPropertyName("last_seen")]
    public DateTime LastSeenUtc { get; set; } = DateTime.MinValue;
}
