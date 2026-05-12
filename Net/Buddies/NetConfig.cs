namespace MouseHouse.Net.Buddies;

/// <summary>
/// Network configuration constants for the buddy-list system. Pinned
/// in source — NOT loaded from a config file — so a malicious or
/// hand-edited config can't redirect traffic through an attacker's
/// broker. To change brokers, change this file and rebuild.
/// </summary>
public static class NetConfig
{
    /// <summary>
    /// Hostname of the MQTT broker. HiveMQ's free public broker has
    /// been running for years; mosquitto.org and the Eclipse
    /// project's broker are both viable fallbacks.
    /// </summary>
    public const string BrokerHost = "broker.hivemq.com";

    /// <summary>TLS MQTT port (8883). Always TLS — we never enable
    /// the plain 1883 port even for testing.</summary>
    public const int BrokerPort = 8883;

    /// <summary>Topic prefix. Versioned so we can change the wire
    /// format without colliding with old clients still around.</summary>
    public const string TopicPrefix = "mhouse/v1";

    /// <summary>How often we re-publish our presence retained
    /// message. The broker keeps the last retained value forever,
    /// but a refresh every minute is a cheap heartbeat and lets us
    /// update LastSeen on the receiver side.</summary>
    public static readonly TimeSpan PresenceRepublishInterval = TimeSpan.FromMinutes(1);

    /// <summary>Friend's status is considered Offline (stale) if we
    /// haven't received a presence update within this window.
    /// Tuned generously so a brief network blip doesn't blink a
    /// friend off and on.</summary>
    public static readonly TimeSpan PresenceStaleAfter = TimeSpan.FromMinutes(4);

    /// <summary>How long client→broker connect can take before we
    /// give up and surface "Couldn't reach buddy server" in the UI.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Build a fully-qualified topic from a topic id and a
    /// suffix ("inbox" / "presence"). Keeping all topic-string
    /// construction here means there's exactly one place that
    /// matters for "does our wire format match the docs."</summary>
    public static string Topic(string topicId, string suffix)
        => $"{TopicPrefix}/{topicId}/{suffix}";

    public static string InboxTopic(string topicId) => Topic(topicId, "inbox");
    public static string PresenceTopic(string topicId) => Topic(topicId, "presence");
}
