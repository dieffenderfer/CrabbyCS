namespace MouseHouse.Net.Buddies;

/// <summary>
/// Single-instance facade the pet scene + UI talk to. Owns the
/// <see cref="Identity"/>, <see cref="FriendList"/>, and
/// <see cref="NetClient"/>. Game / UI code reads state and
/// subscribes to events here; never touches the lower layers
/// directly.
/// </summary>
public sealed class BuddyService : IDisposable
{
    public Identity Identity { get; }
    public FriendList Friends { get; }
    public NetClient Client { get; }

    /// <summary>Pending incoming friend requests, drained by the UI
    /// as it shows the modal. Removed from this list once the user
    /// accepts or rejects.</summary>
    public List<PendingRequest> IncomingRequests { get; } = new();

    /// <summary>Pending incoming game challenges, drained by the UI
    /// as it shows the modal. Removed when user accepts/declines.</summary>
    public List<PendingChallenge> IncomingChallenges { get; } = new();

    public BuddyStatus SelfStatus { get; private set; } = BuddyStatus.Online;
    public string SelfAwayMessage { get; private set; } = "";

    /// <summary>Fired when a new request shows up. UI uses this to
    /// trigger the popup modal + play a bell sound.</summary>
    public event Action<PendingRequest>? IncomingRequestReceived;
    public event Action<PendingChallenge>? IncomingChallengeReceived;
    public event Action? FriendsChanged;

    public BuddyService()
    {
        Identity = Identity.LoadOrCreate();
        Friends = FriendList.Load();
        Client = new NetClient(Identity, Friends);
        Friends.Changed += () => FriendsChanged?.Invoke();
    }

    /// <summary>Kick off the broker connection. Returns immediately;
    /// the connect is non-blocking.</summary>
    public void StartNetwork() => Client.StartAsync();

    /// <summary>Called once per frame from the pet's main loop.
    /// Drains incoming events onto the main thread.</summary>
    public void Update()
    {
        foreach (var ev in Client.PumpInbound())
        {
            switch (ev.Kind)
            {
                case "request": OnRequest(ev.Envelope!); break;
                case "accept": OnAccept(ev.Envelope!); break;
                case "challenge": OnChallenge(ev.Envelope!); break;
                case "presence": FriendsChanged?.Invoke(); break;
            }
        }
    }

    private void OnRequest(InboxEnvelope env)
    {
        // Sanity: drop requests from a code we already have
        // accepted — that's a re-add attempt, which we treat as
        // a no-op (or as a pubkey refresh if their pubkey changed).
        var existing = Friends.Find(env.FromCode);
        if (existing != null)
        {
            if (!string.IsNullOrEmpty(env.FromPublicKeyB64)
                && existing.PublicKeyB64 != env.FromPublicKeyB64)
            {
                // Their key changed (re-installed, reset identity).
                // Surface as a request so the user can re-trust.
            }
            else
            {
                return;
            }
        }
        // De-dupe pending list — if they re-send before we accept,
        // overwrite the existing entry with the latest envelope.
        IncomingRequests.RemoveAll(r => r.FromCode == env.FromCode);
        var req = new PendingRequest
        {
            FromCode = env.FromCode,
            FromName = env.FromName,
            FromPublicKeyB64 = env.FromPublicKeyB64,
            ReceivedAtUtc = DateTime.UtcNow,
        };
        IncomingRequests.Add(req);
        IncomingRequestReceived?.Invoke(req);
    }

    private void OnAccept(InboxEnvelope env)
    {
        // The other side accepted our request — record their pubkey
        // and add them to friends if they aren't already.
        Friends.AddOrUpdate(env.FromCode, env.FromPublicKeyB64, env.FromName);
        _ = Client.EnsureFriendPresenceSubscribedAsync(env.FromCode);
    }

    private void OnChallenge(InboxEnvelope env)
    {
        // Only honor challenges from existing friends; a challenge
        // from a stranger is treated like spam and dropped silently.
        var f = Friends.Find(env.FromCode);
        if (f == null) return;
        var c = new PendingChallenge
        {
            FromCode = env.FromCode,
            FromName = f.Nickname,
            Game = env.Game ?? "",
            ReceivedAtUtc = DateTime.UtcNow,
        };
        IncomingChallenges.Add(c);
        IncomingChallengeReceived?.Invoke(c);
    }

    /// <summary>User accepted an inbound request. Adds the friend
    /// to our list, sends an Accept back so they have our key, and
    /// subscribes to their presence.</summary>
    public void AcceptRequest(PendingRequest req)
    {
        Friends.AddOrUpdate(req.FromCode, req.FromPublicKeyB64, req.FromName);
        IncomingRequests.RemoveAll(r => r.FromCode == req.FromCode);
        _ = Client.SendAccept(req.FromCode);
        _ = Client.EnsureFriendPresenceSubscribedAsync(req.FromCode);
    }

    public void RejectRequest(PendingRequest req)
    {
        // No wire signal — reject is just "drop the modal and move
        // on." The sender will eventually realise we never accepted
        // and stop seeing us as a pending entry on their side.
        IncomingRequests.RemoveAll(r => r.FromCode == req.FromCode);
    }

    /// <summary>Send an outbound friend request to a typed-in code.</summary>
    public void SendFriendRequest(string code)
    {
        var norm = FriendCode.Normalise(code);
        if (!FriendCode.IsValid(norm)) return;
        if (norm == Identity.Code) return;       // can't friend yourself
        _ = Client.SendFriendRequest(norm);
    }

    public void Challenge(Friend f, string game)
    {
        _ = Client.SendChallenge(f.Code, game);
    }

    public void SetSelfStatus(BuddyStatus status, string awayMessage)
    {
        SelfStatus = status;
        SelfAwayMessage = awayMessage ?? "";
        _ = Client.PublishPresence(status, SelfAwayMessage);
    }

    /// <summary>
    /// Best-effort offline-on-quit. The MQTT will-message is the
    /// failsafe (fires when the broker times us out); this is the
    /// happy-path "user quit cleanly" signal so friends see us go
    /// offline immediately.
    /// </summary>
    public void Dispose()
    {
        try { SetSelfStatus(BuddyStatus.Offline, ""); } catch { }
        Client.Dispose();
    }
}

public sealed class PendingRequest
{
    public string FromCode { get; set; } = "";
    public string FromName { get; set; } = "";
    public string FromPublicKeyB64 { get; set; } = "";
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class PendingChallenge
{
    public string FromCode { get; set; } = "";
    public string FromName { get; set; } = "";
    public string Game { get; set; } = "";
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
}
