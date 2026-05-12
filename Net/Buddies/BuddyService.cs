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

    // Codes we've sent outbound friend requests to but haven't seen
    // an accept for yet. Gates OnAccept: an unsolicited accept that
    // isn't in this set is dropped on the floor, which closes the
    // key-swap attack where any party knowing a friend code could
    // publish a forged accept to overwrite our stored pubkey for
    // that friend. In-memory only — if the user quits before the
    // accept lands they'll need to re-add. That's strictly safer
    // than persisting and risking a stale entry being abused.
    private readonly HashSet<string> _outboundRequests = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fired when a new request shows up. UI uses this to
    /// trigger the popup modal + play a bell sound.</summary>
    public event Action<PendingRequest>? IncomingRequestReceived;
    public event Action<PendingChallenge>? IncomingChallengeReceived;
    public event Action? FriendsChanged;

    /// <summary>
    /// Inbound netplay-golf race envelope arrived from a friend.
    /// The first event in a session is always sub == "challenge";
    /// the buddy widget pops a "X wants to race" modal in response.
    /// Subsequent events (accept / stroke / hole_complete / finish /
    /// disconnect) are forwarded to whichever <see cref="NetplayGolfSession"/>
    /// is currently registered for that peer via <see cref="RegisterGolfSession"/>.
    /// </summary>
    public event Action<string, GolfRacePayload>? GolfRaceMessageReceived;
    public event Action<string, ChessRacePayload>? ChessRaceMessageReceived;
    public event Action<string, TetrisRacePayload>? TetrisRaceMessageReceived;
    public event Action<string, HeartsPayload>? HeartsMessageReceived;

    /// <summary>
    /// Active golf sessions, keyed by peer friend code. Set on
    /// accept / when the local side opens the activity; cleared
    /// by the activity on close. The service routes inbound
    /// envelopes directly to the registered session so the
    /// activity doesn't have to spin up its own subscriber.
    /// </summary>
    private readonly Dictionary<string, NetplayGolfSession> _activeGolf = new();
    private readonly Dictionary<string, NetplayChessSession> _activeChess = new();

    public void RegisterGolfSession(NetplayGolfSession s)
        => _activeGolf[s.Peer.Code] = s;
    public void UnregisterGolfSession(NetplayGolfSession s)
        => _activeGolf.Remove(s.Peer.Code);
    public NetplayGolfSession? GetGolfSession(string peerCode)
        => _activeGolf.TryGetValue(peerCode, out var s) ? s : null;

    public void RegisterChessSession(NetplayChessSession s)
        => _activeChess[s.Peer.Code] = s;
    public void UnregisterChessSession(NetplayChessSession s)
        => _activeChess.Remove(s.Peer.Code);
    public NetplayChessSession? GetChessSession(string peerCode)
        => _activeChess.TryGetValue(peerCode, out var s) ? s : null;

    private readonly Dictionary<string, NetplayTetrisSession> _activeTetris = new();
    public void RegisterTetrisSession(NetplayTetrisSession s)
        => _activeTetris[s.Peer.Code] = s;
    public void UnregisterTetrisSession(NetplayTetrisSession s)
        => _activeTetris.Remove(s.Peer.Code);
    public NetplayTetrisSession? GetTetrisSession(string peerCode)
        => _activeTetris.TryGetValue(peerCode, out var s) ? s : null;

    // Hearts: at most one active session at a time per BuddyService
    // (a client is either hosting OR shadowing OR not playing). The
    // routing table is keyed by the canonical friend code of the
    // peer we're talking to: the host (for shadows) or any one of
    // the friend seats (for the host — same session, multiple keys).
    private readonly Dictionary<string, NetplayHeartsSession> _activeHearts = new();
    public void RegisterHeartsSession(NetplayHeartsSession s)
    {
        foreach (var seat in s.Seats)
        {
            if (seat.Kind == "friend" || seat.Kind == "host")
            {
                if (!string.IsNullOrEmpty(seat.FriendCode))
                    _activeHearts[seat.FriendCode] = s;
            }
        }
    }
    public void UnregisterHeartsSession(NetplayHeartsSession s)
    {
        var keys = _activeHearts
            .Where(kv => kv.Value == s)
            .Select(kv => kv.Key).ToList();
        foreach (var k in keys) _activeHearts.Remove(k);
    }
    public NetplayHeartsSession? GetHeartsSession(string fromCode)
        => _activeHearts.TryGetValue(fromCode, out var s) ? s : null;

    /// <summary>
    /// Fired when a netplay-golf session is ready to open as an
    /// in-pet activity (both sides have agreed on the seed/region/
    /// difficulty). DesktopPetScene handles this by calling
    /// OpenActivity(new WorldTeeClassicActivity()) with
    /// ConfigureNetplay(session) — the buddy widget can't open
    /// activities itself because it doesn't own the scene.
    /// </summary>
    public event Action<NetplayGolfSession>? OpenNetplayGolfRequested;
    public event Action<NetplayChessSession>? OpenNetplayChessRequested;
    public event Action<NetplayTetrisSession>? OpenNetplayTetrisRequested;
    public event Action<NetplayHeartsSession>? OpenNetplayHeartsRequested;

    public void RaiseOpenNetplayGolf(NetplayGolfSession s)
    {
        try { OpenNetplayGolfRequested?.Invoke(s); }
        catch { /* don't let a subscriber crash the inbound drain */ }
    }

    public void RaiseOpenNetplayChess(NetplayChessSession s)
    {
        try { OpenNetplayChessRequested?.Invoke(s); }
        catch { }
    }

    public void RaiseOpenNetplayTetris(NetplayTetrisSession s)
    {
        try { OpenNetplayTetrisRequested?.Invoke(s); }
        catch { }
    }

    public void RaiseOpenNetplayHearts(NetplayHeartsSession s)
    {
        try { OpenNetplayHeartsRequested?.Invoke(s); }
        catch { }
    }

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
                case "golf_race": OnGolfRace(ev.Envelope!); break;
                case "chess_race": OnChessRace(ev.Envelope!); break;
                case "tetris_race": OnTetrisRace(ev.Envelope!); break;
                case "hearts": OnHearts(ev.Envelope!); break;
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
        if (string.IsNullOrEmpty(env.FromCode)
            || string.IsNullOrEmpty(env.FromPublicKeyB64)) return;
        // Only honor an accept that matches an outbound request we
        // actually sent. Drops the forged-accept key-swap vector.
        if (!_outboundRequests.Contains(env.FromCode)) return;
        // If we already hold a pubkey for this code and the envelope
        // tries to install a different one, refuse — the user must
        // explicitly remove + re-add for a key change. (The normal
        // accept path will set a previously-empty key just fine.)
        var existing = Friends.Find(env.FromCode);
        if (existing != null
            && !string.IsNullOrEmpty(existing.PublicKeyB64)
            && existing.PublicKeyB64 != env.FromPublicKeyB64) return;
        _outboundRequests.Remove(env.FromCode);
        Friends.AddOrUpdate(env.FromCode, env.FromPublicKeyB64, env.FromName);
        _ = Client.EnsureFriendPresenceSubscribedAsync(env.FromCode);
    }

    /// <summary>
    /// Route an inbound golf_race envelope. Subkinds dispatched:
    ///   challenge → fire <see cref="GolfRaceMessageReceived"/>;
    ///       the buddy widget shows a confirm-modal.
    ///   accept    → fire <see cref="GolfRaceMessageReceived"/>;
    ///       the buddy widget unblocks "awaiting opponent…" and
    ///       opens the activity.
    ///   stroke / hole_complete / finish / disconnect → forward to
    ///       the active session for this peer (if any). Unmatched
    ///       events arrive when a peer keeps streaming after we've
    ///       quit; drop them silently.
    /// </summary>
    private void OnGolfRace(InboxEnvelope env)
    {
        if (env.GolfRace == null) return;
        // Mid-session events go straight to the session — no need
        // to round-trip through subscribers if we already know
        // who's playing whom.
        var session = GetGolfSession(env.FromCode);
        if (session != null && env.GolfRace.Sub != "challenge"
            && env.GolfRace.Sub != "accept" && env.GolfRace.Sub != "decline")
        {
            session.HandleInbound(env.GolfRace);
            return;
        }
        GolfRaceMessageReceived?.Invoke(env.FromCode, env.GolfRace);
    }

    /// <summary>Symmetric with <see cref="OnGolfRace"/>: in-flight
    /// per-puzzle events go straight to the registered session;
    /// challenge / accept / decline fan out to subscribers (the
    /// buddy widget shows confirm-modals).</summary>
    private void OnChessRace(InboxEnvelope env)
    {
        if (env.ChessRace == null) return;
        var session = GetChessSession(env.FromCode);
        if (session != null && env.ChessRace.Sub != "challenge"
            && env.ChessRace.Sub != "accept" && env.ChessRace.Sub != "decline")
        {
            session.HandleInbound(env.ChessRace);
            return;
        }
        ChessRaceMessageReceived?.Invoke(env.FromCode, env.ChessRace);
    }

    /// <summary>Route an inbound tetris_race envelope. Same shape
    /// as <see cref="OnGolfRace"/> / <see cref="OnChessRace"/> —
    /// mid-game events (lines_cleared / board_snapshot / top_out /
    /// disconnect) go straight to the registered session; lifecycle
    /// events (challenge / accept / decline) fan out to subscribers
    /// so the buddy widget can pop confirm-modals.</summary>
    private void OnTetrisRace(InboxEnvelope env)
    {
        if (env.TetrisRace == null) return;
        var session = GetTetrisSession(env.FromCode);
        if (session != null && env.TetrisRace.Sub != "challenge"
            && env.TetrisRace.Sub != "accept" && env.TetrisRace.Sub != "decline")
        {
            session.HandleInbound(env.TetrisRace);
            return;
        }
        TetrisRaceMessageReceived?.Invoke(env.FromCode, env.TetrisRace);
    }

    /// <summary>Route an inbound hearts envelope. Lifecycle events
    /// (challenge / accept / decline / seat_update / start_match)
    /// fan out to subscribers so the buddy widget can drive the
    /// picker + accept modal + seat-composition table; mid-game
    /// events route to the active session if one's registered for
    /// the sender.</summary>
    private void OnHearts(InboxEnvelope env)
    {
        if (env.Hearts == null) return;
        var lifecycle = env.Hearts.Sub is "challenge" or "accept"
            or "decline" or "seat_update" or "start_match";
        if (!lifecycle)
        {
            var session = GetHeartsSession(env.FromCode);
            if (session != null) { session.HandleInbound(env.Hearts); return; }
        }
        HeartsMessageReceived?.Invoke(env.FromCode, env.Hearts);
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
        _outboundRequests.Add(norm);
        _ = Client.SendFriendRequest(norm);
    }

    public void Challenge(Friend f, string game)
    {
        _ = Client.SendChallenge(f.Code, game);
    }

    /// <summary>
    /// Regenerate the user's friend code. Fire-and-forget — the UI
    /// reads <see cref="Identity.Code"/> directly, and the reconnect
    /// happens off-thread.
    /// </summary>
    public void RotateIdentityCode()
    {
        _ = Client.RotateIdentityCodeAsync();
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
