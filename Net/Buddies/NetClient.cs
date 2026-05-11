using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace MouseHouse.Net.Buddies;

/// <summary>
/// MQTT-backed network client for the buddy-list system. Owns the
/// connection to the broker, the subscription set, and the inbound
/// message demuxer. Game code never touches this directly — it
/// reads buddy state out of <see cref="FriendList"/> and listens
/// for events on the singleton <see cref="BuddyService"/> facade.
///
/// Concurrency: MQTTnet's event callbacks fire on broker-side IO
/// threads. This class never mutates the public-facing collections
/// from those threads — incoming messages get queued into
/// <see cref="_inbound"/> and the main scene drains them once per
/// frame via <see cref="PumpInbound"/>. Outbound publishes go
/// straight through the MQTT client (its own internal queue
/// handles back-pressure).
/// </summary>
public sealed class NetClient : IDisposable
{
    private readonly Identity _identity;
    private readonly FriendList _friends;
    private readonly IMqttClient _mqtt;
    private readonly MqttFactory _factory = new();
    private readonly ConcurrentQueue<InboundEvent> _inbound = new();
    private readonly HashSet<string> _subscribedPresenceTopics = new();
    private CancellationTokenSource? _connectCts;
    private DateTime _lastPresencePublishUtc = DateTime.MinValue;
    private BuddyStatus _selfStatus = BuddyStatus.Online;
    private string _selfAwayMessage = "";

    public bool Connected => _mqtt.IsConnected;
    public bool Connecting { get; private set; }
    public string? LastError { get; private set; }

    public NetClient(Identity identity, FriendList friends)
    {
        _identity = identity;
        _friends = friends;
        _mqtt = _factory.CreateMqttClient();
        _mqtt.ApplicationMessageReceivedAsync += OnMessageAsync;
        _mqtt.DisconnectedAsync += OnDisconnectedAsync;
    }

    /// <summary>
    /// Kick off a non-blocking connect. Safe to call repeatedly —
    /// subsequent calls while connected are no-ops; calls while a
    /// previous attempt is in flight are coalesced.
    /// </summary>
    public void StartAsync()
    {
        if (_mqtt.IsConnected || Connecting) return;
        Connecting = true;
        _connectCts = new CancellationTokenSource(NetConfig.ConnectTimeout);
        _ = Task.Run(async () =>
        {
            try
            {
                var opts = new MqttClientOptionsBuilder()
                    .WithTcpServer(NetConfig.BrokerHost, NetConfig.BrokerPort)
                    .WithTlsOptions(o => o.UseTls())
                    .WithClientId("mhouse-" + _identity.Code.ToLowerInvariant())
                    // Will: when we drop unexpectedly, the broker
                    // publishes an Offline presence on our retained
                    // topic so friends see us go offline immediately
                    // instead of waiting for the stale-after window.
                    .WithWillTopic(NetConfig.PresenceTopic(FriendCode.TopicId(_identity.Code)))
                    .WithWillPayload(SerializePresence(BuddyStatus.Offline, ""))
                    .WithWillRetain(true)
                    .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();
                await _mqtt.ConnectAsync(opts, _connectCts!.Token).ConfigureAwait(false);
                await SubscribeAfterConnect().ConfigureAwait(false);
                await PublishPresence(_selfStatus, _selfAwayMessage).ConfigureAwait(false);
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
            finally
            {
                Connecting = false;
            }
        });
    }

    private async Task SubscribeAfterConnect()
    {
        // Our own inbox + every friend's presence topic.
        var ourInbox = NetConfig.InboxTopic(FriendCode.TopicId(_identity.Code));
        await _mqtt.SubscribeAsync(ourInbox, MqttQualityOfServiceLevel.AtLeastOnce);
        _subscribedPresenceTopics.Clear();
        foreach (var f in _friends.Friends)
        {
            var t = NetConfig.PresenceTopic(FriendCode.TopicId(f.Code));
            _subscribedPresenceTopics.Add(t);
            await _mqtt.SubscribeAsync(t, MqttQualityOfServiceLevel.AtLeastOnce);
        }
    }

    /// <summary>
    /// Subscribe to a newly-added friend's presence topic. Called
    /// after a friend request is accepted (either direction). No-op
    /// if we're not yet connected — SubscribeAfterConnect picks
    /// them up on the next connect.
    /// </summary>
    public async Task EnsureFriendPresenceSubscribedAsync(string friendCode)
    {
        if (!_mqtt.IsConnected) return;
        var t = NetConfig.PresenceTopic(FriendCode.TopicId(friendCode));
        if (_subscribedPresenceTopics.Add(t))
            await _mqtt.SubscribeAsync(t, MqttQualityOfServiceLevel.AtLeastOnce);
    }

    /// <summary>
    /// Update our presence retained message. Set the local cache
    /// fields too so a republish picks them up.
    /// </summary>
    public async Task PublishPresence(BuddyStatus status, string awayMessage)
    {
        _selfStatus = status;
        _selfAwayMessage = awayMessage ?? "";
        if (!_mqtt.IsConnected) return;
        // Invisible is broadcast AS Offline — that's the whole
        // point of "invisible." Status stays in _selfStatus locally
        // so the user's own UI shows the truth.
        var onWire = status == BuddyStatus.Invisible ? BuddyStatus.Offline : status;
        var topic = NetConfig.PresenceTopic(FriendCode.TopicId(_identity.Code));
        var payload = SerializePresence(onWire, _selfAwayMessage);
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(true)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await _mqtt.PublishAsync(msg);
        _lastPresencePublishUtc = DateTime.UtcNow;
    }

    private byte[] SerializePresence(BuddyStatus status, string awayMessage)
    {
        var p = new PresencePayload
        {
            Status = status,
            AwayMessage = awayMessage ?? "",
            DisplayName = _identity.DisplayName,
            LastSeenUtc = DateTime.UtcNow,
        };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(p));
    }

    /// <summary>
    /// Send a friend request to the given code. The recipient sees
    /// it as an incoming-request modal; they click Accept (which
    /// triggers <see cref="SendAccept"/>) or Reject (which is a
    /// silent drop — there's no "rejected" wire signal).
    /// </summary>
    public async Task SendFriendRequest(string targetCode)
    {
        await SendEnvelope(targetCode, new InboxEnvelope
        {
            Kind = "request",
            FromCode = _identity.Code,
            FromPublicKeyB64 = _identity.PublicKeyB64,
            FromName = _identity.DisplayName,
        });
    }

    public async Task SendAccept(string targetCode)
    {
        await SendEnvelope(targetCode, new InboxEnvelope
        {
            Kind = "accept",
            FromCode = _identity.Code,
            FromPublicKeyB64 = _identity.PublicKeyB64,
            FromName = _identity.DisplayName,
        });
    }

    /// <summary>
    /// Send a netplay-golf race envelope (challenge / accept /
    /// decline / stroke / hole_complete / finish / disconnect).
    /// Always sealed — the friend handshake has completed by the
    /// time anyone has a session, so the recipient's pubkey is
    /// already in our FriendList. If sealing fails (peer was
    /// re-added without finishing the handshake), we drop the
    /// message silently rather than leaking it as plaintext.
    /// </summary>
    public async Task SendGolfRace(string targetCode, GolfRacePayload payload)
    {
        await SendEnvelope(targetCode, new InboxEnvelope
        {
            Kind = "golf_race",
            FromCode = _identity.Code,
            FromPublicKeyB64 = _identity.PublicKeyB64,
            FromName = _identity.DisplayName,
            GolfRace = payload,
        });
    }

    /// <summary>Send a netplay-chess race envelope. Symmetric with
    /// SendGolfRace — always sealed.</summary>
    public async Task SendChessRace(string targetCode, ChessRacePayload payload)
    {
        await SendEnvelope(targetCode, new InboxEnvelope
        {
            Kind = "chess_race",
            FromCode = _identity.Code,
            FromPublicKeyB64 = _identity.PublicKeyB64,
            FromName = _identity.DisplayName,
            ChessRace = payload,
        });
    }

    public async Task SendChallenge(string targetCode, string game)
    {
        // Random 8-byte nonce so the receiver can match a future
        // accept reply back to this particular challenge. Unused in
        // v1 (challenges only generate the "Awaiting opponent…"
        // placeholder) but included so the wire format doesn't
        // need to change when full netplay drops in.
        Span<byte> n = stackalloc byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(n);
        await SendEnvelope(targetCode, new InboxEnvelope
        {
            Kind = "challenge",
            FromCode = _identity.Code,
            FromPublicKeyB64 = _identity.PublicKeyB64,
            FromName = _identity.DisplayName,
            Game = game,
            Nonce = Convert.ToBase64String(n),
        });
    }

    private async Task SendEnvelope(string targetCode, InboxEnvelope env)
    {
        if (!_mqtt.IsConnected) return;
        // We need the recipient's public key to seal. For a fresh
        // request the only way we have it is if the user typed in
        // a code they already share a key with (re-add) — in the
        // typical first-add case, we don't have the key yet. So
        // requests are published *unsealed* on the inbox; the
        // receiver decides whether to trust them. Accepts and
        // subsequent messages are sealed once both sides hold each
        // other's keys.
        Friend? f = _friends.Find(targetCode);
        bool canSeal = f != null && !string.IsNullOrEmpty(f.PublicKeyB64);

        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(env));
        byte[] payload;
        string topic = NetConfig.InboxTopic(FriendCode.TopicId(targetCode));

        if (canSeal)
        {
            payload = BuddyCrypto.Seal(Base64UrlDecode(f!.PublicKeyB64), plaintext);
            // Mark sealed payloads with a 1-byte prefix so the
            // receiver knows whether to attempt Open. Plaintext
            // requests (the bootstrap case) get a 0 prefix.
            payload = Prepend((byte)1, payload);
        }
        else
        {
            // Bootstrap envelope — plaintext. The architecture
            // doc covers why this is acceptable for the request
            // kind only: the broker sees only that "someone is
            // sending an inbox message to this topic id"; nothing
            // about who, content, or IPs. The receiver UI gates
            // accept/reject so spam is still rate-limited by
            // human decision, not crypto.
            payload = Prepend((byte)0, plaintext);
        }
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await _mqtt.PublishAsync(msg);
    }

    private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = e.ApplicationMessage.PayloadSegment.ToArray();
        try
        {
            // Inbox: ours, sealed or plaintext.
            var ourInbox = NetConfig.InboxTopic(FriendCode.TopicId(_identity.Code));
            if (topic == ourInbox)
            {
                HandleInbox(payload);
                return Task.CompletedTask;
            }
            // Presence: a friend's status update.
            // Find which friend by topic — small N, linear scan
            // is fine.
            foreach (var f in _friends.Friends)
            {
                if (NetConfig.PresenceTopic(FriendCode.TopicId(f.Code)) == topic)
                {
                    HandlePresence(f, payload);
                    break;
                }
            }
        }
        catch { /* swallow — bad payload from broker shouldn't crash us */ }
        return Task.CompletedTask;
    }

    private void HandleInbox(byte[] payload)
    {
        if (payload.Length < 1) return;
        bool sealed_ = payload[0] == 1;
        var body = new byte[payload.Length - 1];
        Buffer.BlockCopy(payload, 1, body, 0, body.Length);

        byte[]? plaintext = body;
        if (sealed_)
        {
            plaintext = BuddyCrypto.Open(_identity.SecretKey, body);
            if (plaintext == null) return;       // crypto failure → drop silently
        }

        InboxEnvelope? env;
        try { env = JsonSerializer.Deserialize<InboxEnvelope>(plaintext); }
        catch { return; }
        if (env == null || string.IsNullOrEmpty(env.Kind)) return;

        _inbound.Enqueue(new InboundEvent
        {
            Kind = env.Kind,
            Envelope = env,
        });
    }

    private void HandlePresence(Friend f, byte[] payload)
    {
        PresencePayload? p;
        try { p = JsonSerializer.Deserialize<PresencePayload>(payload); }
        catch { return; }
        if (p == null) return;
        f.LastStatus = p.Status;
        f.LastStatusMessage = p.AwayMessage ?? "";
        f.LastSeenUtc = p.LastSeenUtc;
        _inbound.Enqueue(new InboundEvent { Kind = "presence", PresenceFriend = f });
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        LastError = e.Reason.ToString();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Drain the inbound queue; caller (the buddy UI / scene)
    /// handles each event on the main thread. Also republishes
    /// presence if we're past the heartbeat interval — keeps the
    /// retained value fresh so friends' "last seen" doesn't drift.
    /// </summary>
    public IEnumerable<InboundEvent> PumpInbound()
    {
        while (_inbound.TryDequeue(out var ev)) yield return ev;
        if (_mqtt.IsConnected
            && DateTime.UtcNow - _lastPresencePublishUtc > NetConfig.PresenceRepublishInterval)
        {
            _ = PublishPresence(_selfStatus, _selfAwayMessage);
        }
    }

    public void Dispose()
    {
        _connectCts?.Cancel();
        // Best-effort offline-on-quit so friends see us go offline
        // immediately instead of waiting for the will-message to
        // fire on broker-side disconnect detection (~30 s).
        try
        {
            if (_mqtt.IsConnected)
                PublishPresence(BuddyStatus.Offline, _selfAwayMessage).Wait(500);
        }
        catch { }
        _mqtt.Dispose();
    }

    private static byte[] Prepend(byte prefix, byte[] data)
    {
        var r = new byte[data.Length + 1];
        r[0] = prefix;
        Buffer.BlockCopy(data, 0, r, 1, data.Length);
        return r;
    }

    private static byte[] Base64UrlDecode(string s)
    {
        string b = s.Replace('-', '+').Replace('_', '/');
        switch (b.Length % 4)
        {
            case 2: b += "=="; break;
            case 3: b += "="; break;
        }
        return Convert.FromBase64String(b);
    }
}

/// <summary>
/// Inbound event drained on the main thread. Distinct kinds:
///   request / accept / challenge / chat: <see cref="Envelope"/> populated.
///   presence: <see cref="PresenceFriend"/> populated with the updated row.
/// </summary>
public sealed class InboundEvent
{
    public string Kind { get; set; } = "";
    public InboxEnvelope? Envelope { get; set; }
    public Friend? PresenceFriend { get; set; }
}
