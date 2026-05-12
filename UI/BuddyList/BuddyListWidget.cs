using System.Collections.Concurrent;
using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Net.Buddies;
using MouseHouse.Scenes.Activities;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.UI.BuddyList;

/// <summary>
/// AIM-flavoured buddy list panel. Shows the user's own friend code
/// at the top (so it's easy to copy/share), then a scrollable list
/// of friends with status dots, then a status-control row at the
/// bottom (Available / Away / Busy / Invisible + away-message
/// field).
///
/// Modeled after RadioWidget — same Position / Visible / Update /
/// Draw shape so it can sit on the pet's transparent overlay
/// alongside the radio.
/// </summary>
public sealed class BuddyListWidget
{
    public const int W = 240;
    public const int H = 360;

    public bool Visible;
    public Vector2 Position = new(120, 80);
    public Action? StateChanged;

    private readonly BuddyService _svc;

    // Modals layered on top of the main panel.
    private bool _addOpen;
    private string _addInput = "";
    private string _addStatus = "";   // "Invalid code" / "Request sent" / etc.

    private PendingRequest? _activeRequest;
    private PendingChallenge? _activeChallenge;
    private DateTime _challengeAwaitingUntil;
    private string _challengeAwaitingLine = "";

    // Golf-race picker. Open when the local user clicks ▶ on a friend
    // row; closes either when they pick + Send (which sends the
    // challenge envelope and switches to "Awaiting response…") or
    // when they Cancel.
    private bool _golfPickerOpen;
    private Friend? _golfPickerFriend;
    private int _golfPickerRegionIdx;
    private int _golfPickerDifficulty = 1;          // default Medium
    private Rectangle _golfPickerSendBtn;
    private Rectangle _golfPickerCancelBtn;
    private Rectangle[] _golfPickerRegionRects = Array.Empty<Rectangle>();
    private Rectangle[] _golfPickerDifficultyRects = Array.Empty<Rectangle>();

    // Incoming golf-race challenge modal (separate from the generic
    // _activeChallenge for `kind=challenge` envelopes — golf races
    // ride on the new `golf_race` kind with seed/region/difficulty).
    private GolfRacePayload? _activeGolfChallenge;
    private string _activeGolfChallengeFromCode = "";
    private string _activeGolfChallengeFromName = "";
    private Rectangle _golfChAcceptBtn;
    private Rectangle _golfChDeclineBtn;

    // Pending outbound challenge — what we sent + are awaiting an
    // accept for. Used to construct the session on acceptance.
    private string _pendingOutgoingPeerCode = "";
    private int _pendingOutgoingSeed;
    private string _pendingOutgoingRegion = "";
    private int _pendingOutgoingDifficulty;

    // Region menu for the picker — mirrors GlobePicker.Regions for
    // the names the engine knows about. We list a curated subset so
    // the picker fits in a modal; the activity falls back to North
    // America if the peer sent a name we don't recognise.
    private static readonly string[] PickerRegions =
    {
        "North America", "Europe", "Asia", "South America",
        "Australia", "Africa", "New York City", "Ohio",
    };
    private static readonly string[] PickerDifficulties =
    {
        "Easy", "Medium", "Hard", "Expert", "Master", "Legendary", "Ohio",
    };

    // ── Chess-race picker state (mirrors golf shape) ───────────────────
    private bool _chessPickerOpen;
    private Friend? _chessPickerFriend;
    private int _chessPickerTimeIdx = 2;   // default 3 minutes
    private int _chessPickerBandIdx;       // default Beginner
    private bool _chessPickerFetching;
    private string _chessPickerStatus = "";
    private Rectangle _chessPickerSendBtn;
    private Rectangle _chessPickerCancelBtn;
    private Rectangle[] _chessPickerTimeRects = Array.Empty<Rectangle>();
    private Rectangle[] _chessPickerBandRects = Array.Empty<Rectangle>();
    private static readonly (string Label, int Seconds)[] PickerTimes =
    {
        ("1 min", 60), ("2 min", 120), ("3 min", 180),
        ("5 min", 300), ("10 min", 600),
    };
    /// <summary>Difficulty bands — name + (lichess-style) target
    /// rating-window center. Beginner pulls from the easier end of
    /// /api/puzzle/next; Advanced from the harder end.</summary>
    private static readonly (string Name, int RatingFloor, int RatingCeiling)[] PickerBands =
    {
        ("Beginner",     800, 1300),
        ("Easy",        1200, 1600),
        ("Intermediate",1500, 1900),
        ("Advanced",    1800, 2400),
    };

    // Inbound chess-race challenge modal.
    private ChessRacePayload? _activeChessChallenge;
    private string _activeChessChallengeFromCode = "";
    private string _activeChessChallengeFromName = "";
    private Rectangle _chessChAcceptBtn;
    private Rectangle _chessChDeclineBtn;

    // Outbound chess-race pending state — for matching the peer's
    // accept reply back to the queue we just sent.
    private string _pendingOutgoingChessPeerCode = "";
    private int _pendingOutgoingChessTimeSeconds;
    private string _pendingOutgoingChessBand = "";
    private List<ChessRacePuzzle>? _pendingOutgoingChessPuzzles;

    /// <summary>Actions queued from background tasks (e.g. the chess
    /// pre-fetch worker) for the UI thread to execute on its next
    /// tick. Modal state mutates only from the main thread — anything
    /// else races with the renderer/input handler.</summary>
    private readonly ConcurrentQueue<Action> _uiThreadWork = new();

    // ── Tetris-race picker state ───────────────────────────────────
    private bool _tetrisPickerOpen;
    private Friend? _tetrisPickerFriend;
    private int _tetrisPickerLevelIdx;   // 0 = Level 1
    private Rectangle _tetrisPickerSendBtn;
    private Rectangle _tetrisPickerCancelBtn;
    private Rectangle[] _tetrisPickerLevelRects = Array.Empty<Rectangle>();

    private TetrisRacePayload? _activeTetrisChallenge;
    private string _activeTetrisChallengeFromCode = "";
    private string _activeTetrisChallengeFromName = "";
    private Rectangle _tetrisChAcceptBtn;
    private Rectangle _tetrisChDeclineBtn;

    private string _pendingOutgoingTetrisPeerCode = "";
    private int _pendingOutgoingTetrisSeed;
    private int _pendingOutgoingTetrisLevel;

    // ── Hearts (4-way) picker state ─────────────────────────────────
    // Host-side lobby: 3 invite slots (each can hold a Friend or
    // stay null = AI seat) + an AI difficulty pick. After Send,
    // the picker stays open showing each invited friend's accept
    // status so the host can see the lobby fill before clicking
    // Start.
    private bool _heartsPickerOpen;
    private Friend?[] _heartsInvites = new Friend?[3];
    private int _heartsDifficultyIdx;        // 0 = Beginner, 1 = Standard, 2 = Expert
    private int _heartsSeed;
    private bool _heartsInvitesSent;
    /// <summary>Friend code → status. null = pending, true = accepted,
    /// false = declined.</summary>
    private readonly Dictionary<string, bool?> _heartsAcceptStatus = new();
    /// <summary>Inner "pick a friend" dropdown — which slot is being
    /// filled (-1 = closed).</summary>
    private int _heartsAddSlot = -1;
    private Rectangle _heartsSendBtn;
    private Rectangle _heartsStartBtn;
    private Rectangle _heartsCancelBtn;
    private Rectangle _heartsDiffCycleBtn;
    private Rectangle[] _heartsSlotRects = Array.Empty<Rectangle>();
    private Rectangle[] _heartsDropdownRowRects = Array.Empty<Rectangle>();
    private List<Friend> _heartsDropdownChoices = new();
    private static readonly string[] HeartsDifficulties = { "Beginner", "Standard", "Expert" };

    // Incoming Hearts challenge modal.
    private HeartsPayload? _activeHeartsChallenge;
    private string _activeHeartsChallengeFromCode = "";
    private string _activeHeartsChallengeFromName = "";
    private Rectangle _heartsChAcceptBtn;
    private Rectangle _heartsChDeclineBtn;

    // After a recipient accepts, we keep the proposed seat composition
    // around so when the host's start_match envelope arrives we can
    // build the right session.
    private HeartsPayload? _heartsAcceptedChallenge;

    // Status-control popover (small dropdown when you click your own
    // status pill). Closed by clicking outside.
    private bool _statusPopOpen;
    private string _awayMessageDraft = "";
    private bool _awayMessageFocused;
    private float _caretBlink;

    private bool _dragging;
    private Vector2 _dragGrab;
    private int _scroll;

    // Per-friend hover targets — recomputed each Draw, read in Update
    // for hit testing. Both run sequentially each frame so this is safe.
    private readonly List<(Friend Friend, Rectangle Row, Rectangle GolfBtn, Rectangle ChessBtn, Rectangle TetrisBtn, Rectangle HeartsBtn)> _rows = new();
    private Rectangle _closeBtn;
    private Rectangle _addBtn;
    private Rectangle _myCodeRect;
    private Rectangle _statusPill;
    private Rectangle[] _statusOptions = Array.Empty<Rectangle>();
    private Rectangle _awayMessageField;

    public BuddyListWidget(BuddyService svc)
    {
        _svc = svc;
        // Auto-show the incoming-request modal on event so the user
        // doesn't have to be staring at the buddy panel to notice.
        _svc.IncomingRequestReceived += req =>
        {
            if (_activeRequest == null) _activeRequest = req;
            // Force panel open on first incoming so the modal is visible.
            Visible = true;
        };
        _svc.IncomingChallengeReceived += ch =>
        {
            if (_activeChallenge == null) _activeChallenge = ch;
            Visible = true;
        };
        _svc.GolfRaceMessageReceived += OnGolfRaceMessage;
        _svc.ChessRaceMessageReceived += OnChessRaceMessage;
        _svc.TetrisRaceMessageReceived += OnTetrisRaceMessage;
        _svc.HeartsMessageReceived += OnHeartsMessage;
    }

    private void OnHeartsMessage(string fromCode, HeartsPayload p)
    {
        switch (p.Sub)
        {
            case "challenge":
                // Incoming invite. Pop the accept modal if we're
                // not already showing one.
                if (_activeHeartsChallenge != null) return;
                _activeHeartsChallenge = p;
                _activeHeartsChallengeFromCode = fromCode;
                var inviter = _svc.Friends.Find(fromCode);
                _activeHeartsChallengeFromName = inviter?.Nickname ?? fromCode;
                Visible = true;
                break;
            case "accept":
                // We're the host; one of our invitees accepted.
                if (_heartsAcceptStatus.ContainsKey(fromCode))
                    _heartsAcceptStatus[fromCode] = true;
                // seat_update fanout is intentionally deferred to the
                // host-loop commit; the proposed composition in the
                // sent challenge envelope is what each peer sees
                // until start_match.
                break;
            case "decline":
                if (_heartsAcceptStatus.ContainsKey(fromCode))
                    _heartsAcceptStatus[fromCode] = false;
                break;
            case "start_match":
                // Recipient side: build a shadow session from the
                // challenge composition + open the activity.
                OpenShadowFromStart(fromCode, p);
                break;
        }
    }

    private void OpenHeartsPicker(Friend f)
    {
        _heartsPickerOpen = true;
        _heartsInvites[0] = f;
        _heartsInvites[1] = null;
        _heartsInvites[2] = null;
        _heartsInvitesSent = false;
        _heartsAcceptStatus.Clear();
        _heartsAddSlot = -1;
        _heartsDifficultyIdx = 1;        // Standard
        Span<byte> seedBytes = stackalloc byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(seedBytes);
        _heartsSeed = BitConverter.ToInt32(seedBytes);
    }

    private void SendHeartsInvites()
    {
        // Build the seat composition. Host takes seat 0; the 3
        // invite slots fill seats 1/2/3 in order. Empty slots
        // become AI seats with a synthesised name.
        var seats = new List<HeartsSeat>
        {
            new HeartsSeat
            {
                Kind = "host",
                Name = _svc.Identity.DisplayName,
                FriendCode = _svc.Identity.Code,
            },
        };
        for (int i = 0; i < 3; i++)
        {
            var f = _heartsInvites[i];
            if (f != null)
            {
                seats.Add(new HeartsSeat
                {
                    Kind = "pending",
                    Name = f.Nickname,
                    FriendCode = f.Code,
                });
                _heartsAcceptStatus[f.Code] = null;
            }
            else
            {
                seats.Add(new HeartsSeat
                {
                    Kind = "ai",
                    Name = $"Computer {i + 1}",
                });
            }
        }
        // Ship the challenge envelope to each pending friend.
        var difficulty = HeartsDifficulties[_heartsDifficultyIdx];
        foreach (var seat in seats)
        {
            if (seat.Kind != "pending") continue;
            _ = _svc.Client.SendHearts(seat.FriendCode, new HeartsPayload
            {
                Sub = "challenge",
                Seed = _heartsSeed,
                Difficulty = difficulty,
                Seats = seats,
            });
        }
        _heartsInvitesSent = true;
    }

    private void StartHeartsMatch()
    {
        // Pending → AI conversion: any invitee who hasn't accepted
        // by the time the host clicks Start drops out and an AI
        // takes their seat.
        var seats = new List<NetplayHeartsSeat>
        {
            new NetplayHeartsSeat
            {
                Kind = "host",
                Name = _svc.Identity.DisplayName,
                FriendCode = _svc.Identity.Code,
            },
        };
        for (int i = 0; i < 3; i++)
        {
            var f = _heartsInvites[i];
            if (f != null && _heartsAcceptStatus.TryGetValue(f.Code, out var st)
                && st == true)
            {
                seats.Add(new NetplayHeartsSeat
                {
                    Kind = "friend",
                    Name = f.Nickname,
                    FriendCode = f.Code,
                });
            }
            else
            {
                seats.Add(new NetplayHeartsSeat
                {
                    Kind = "ai",
                    Name = $"Computer {i + 1}",
                });
            }
        }
        var difficulty = HeartsDifficulties[_heartsDifficultyIdx];
        var session = new NetplayHeartsSession(_svc, isHost: true,
            _heartsSeed, difficulty, seats, localSeat: 0);
        _svc.RegisterHeartsSession(session);

        // Ship start_match to every accepted friend with the
        // finalized seat composition.
        var wireSeats = seats.Select(s => new HeartsSeat
        {
            Kind = s.Kind, Name = s.Name, FriendCode = s.FriendCode,
        }).ToList();
        foreach (var seat in seats)
        {
            if (seat.Kind != "friend") continue;
            _ = _svc.Client.SendHearts(seat.FriendCode, new HeartsPayload
            {
                Sub = "start_match",
                Seed = _heartsSeed,
                Difficulty = difficulty,
                Seats = wireSeats,
            });
        }
        _svc.RaiseOpenNetplayHearts(session);
        _heartsPickerOpen = false;
    }

    private void OpenShadowFromStart(string fromCode, HeartsPayload p)
    {
        if (p.Seats == null) return;
        // Map our friend code to the seat we occupy.
        int localSeat = -1;
        for (int i = 0; i < p.Seats.Count; i++)
        {
            if (p.Seats[i].FriendCode == _svc.Identity.Code) { localSeat = i; break; }
        }
        if (localSeat < 0) return;
        var seats = p.Seats.Select(s => new NetplayHeartsSeat
        {
            Kind = s.Kind, Name = s.Name, FriendCode = s.FriendCode,
        }).ToList();
        var session = new NetplayHeartsSession(_svc, isHost: false,
            p.Seed, p.Difficulty, seats, localSeat);
        _svc.RegisterHeartsSession(session);
        _svc.RaiseOpenNetplayHearts(session);
        _activeHeartsChallenge = null;
    }

    private void OnTetrisRaceMessage(string fromCode, TetrisRacePayload p)
    {
        switch (p.Sub)
        {
            case "challenge":
                if (_activeTetrisChallenge != null) return;
                _activeTetrisChallenge = p;
                _activeTetrisChallengeFromCode = fromCode;
                var f = _svc.Friends.Find(fromCode);
                _activeTetrisChallengeFromName = f?.Nickname ?? fromCode;
                Visible = true;
                break;
            case "accept":
                if (_pendingOutgoingTetrisPeerCode != fromCode) return;
                var peer = _svc.Friends.Find(fromCode);
                if (peer == null) return;
                var session = new NetplayTetrisSession(_svc, peer, isHost: true,
                    _pendingOutgoingTetrisSeed, _pendingOutgoingTetrisLevel);
                _svc.RegisterTetrisSession(session);
                _svc.RaiseOpenNetplayTetris(session);
                _pendingOutgoingTetrisPeerCode = "";
                _challengeAwaitingLine = "";
                break;
            case "decline":
                if (_pendingOutgoingTetrisPeerCode == fromCode)
                {
                    _challengeAwaitingLine = "Challenge declined.";
                    _challengeAwaitingUntil = DateTime.UtcNow.AddSeconds(4);
                    _pendingOutgoingTetrisPeerCode = "";
                }
                break;
        }
    }

    private void OnChessRaceMessage(string fromCode, ChessRacePayload p)
    {
        switch (p.Sub)
        {
            case "challenge":
                if (_activeChessChallenge != null) return;
                _activeChessChallenge = p;
                _activeChessChallengeFromCode = fromCode;
                var f = _svc.Friends.Find(fromCode);
                _activeChessChallengeFromName = f?.Nickname ?? fromCode;
                Visible = true;
                break;
            case "accept":
                if (_pendingOutgoingChessPeerCode != fromCode) return;
                var peer = _svc.Friends.Find(fromCode);
                if (peer == null || _pendingOutgoingChessPuzzles == null) return;
                // Translate the wire-typed list into the sink-typed list
                // so the activity (which sees INetplayChessSink) doesn't
                // know about ChessRacePuzzle.
                var sinkList = new List<NetplayChessPuzzle>(_pendingOutgoingChessPuzzles.Count);
                foreach (var w in _pendingOutgoingChessPuzzles)
                {
                    sinkList.Add(new NetplayChessPuzzle
                    {
                        Id = w.Id, Pgn = w.Pgn, Solution = w.Solution,
                        Rating = w.Rating, Fen = w.Fen,
                        ExpectedUci = w.ExpectedUci, Title = w.Title,
                        WhiteToMove = w.WhiteToMove,
                    });
                }
                var session = new NetplayChessSession(_svc, peer, isHost: true,
                    _pendingOutgoingChessTimeSeconds, _pendingOutgoingChessBand,
                    sinkList);
                _svc.RegisterChessSession(session);
                _svc.RaiseOpenNetplayChess(session);
                _pendingOutgoingChessPeerCode = "";
                _pendingOutgoingChessPuzzles = null;
                _challengeAwaitingLine = "";
                break;
            case "decline":
                if (_pendingOutgoingChessPeerCode == fromCode)
                {
                    _challengeAwaitingLine = "Challenge declined.";
                    _challengeAwaitingUntil = DateTime.UtcNow.AddSeconds(4);
                    _pendingOutgoingChessPeerCode = "";
                    _pendingOutgoingChessPuzzles = null;
                }
                break;
        }
    }

    private void OnGolfRaceMessage(string fromCode, GolfRacePayload p)
    {
        switch (p.Sub)
        {
            case "challenge":
                // Don't queue more than one challenge at a time — if
                // we're staring at an Accept/Decline dialog already,
                // drop the new one. The sender's broker retry will
                // re-deliver once the user dismisses the current.
                if (_activeGolfChallenge != null) return;
                _activeGolfChallenge = p;
                _activeGolfChallengeFromCode = fromCode;
                var f = _svc.Friends.Find(fromCode);
                _activeGolfChallengeFromName = f?.Nickname ?? fromCode;
                Visible = true;
                break;
            case "accept":
                // The challenge we sent has been accepted — open the
                // activity locally with the params we remembered.
                if (_pendingOutgoingPeerCode != fromCode) return;
                var peer = _svc.Friends.Find(fromCode);
                if (peer == null) return;
                var session = new NetplayGolfSession(_svc, peer, isHost: true,
                    _pendingOutgoingSeed, _pendingOutgoingRegion, _pendingOutgoingDifficulty);
                _svc.RegisterGolfSession(session);
                _svc.RaiseOpenNetplayGolf(session);
                _pendingOutgoingPeerCode = "";
                _challengeAwaitingLine = "";
                break;
            case "decline":
                if (_pendingOutgoingPeerCode == fromCode)
                {
                    _challengeAwaitingLine = "Challenge declined.";
                    _challengeAwaitingUntil = DateTime.UtcNow.AddSeconds(4);
                    _pendingOutgoingPeerCode = "";
                }
                break;
        }
    }

    public bool ContainsPoint(Vector2 p)
    {
        if (!Visible) return false;
        if (_addOpen || _activeRequest != null || _activeChallenge != null
            || _statusPopOpen) return true;
        return p.X >= Position.X && p.X < Position.X + W
            && p.Y >= Position.Y && p.Y < Position.Y + H;
    }

    /// <summary>Returns true if input was consumed this frame.</summary>
    public bool Update(float delta, Vector2 mouse,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        if (!Visible) return false;
        _caretBlink += delta;

        // Drain UI-thread work queued by background tasks (chess
        // pre-fetch completion, etc.) before any modal logic runs
        // so the closing-modal flip below sees the latest state.
        while (_uiThreadWork.TryDequeue(out var work))
        {
            try { work(); } catch { /* don't let a stray callback kill input */ }
        }

        // Modals first — exclusive focus.
        if (_golfPickerOpen) { UpdateGolfPicker(mouse, leftPressed); return true; }
        if (_chessPickerOpen) { UpdateChessPicker(mouse, leftPressed); return true; }
        if (_tetrisPickerOpen) { UpdateTetrisPicker(mouse, leftPressed); return true; }
        if (_heartsPickerOpen) { UpdateHeartsPicker(mouse, leftPressed); return true; }
        if (_activeGolfChallenge != null) { UpdateGolfChallengeModal(mouse, leftPressed); return true; }
        if (_activeChessChallenge != null) { UpdateChessChallengeModal(mouse, leftPressed); return true; }
        if (_activeTetrisChallenge != null) { UpdateTetrisChallengeModal(mouse, leftPressed); return true; }
        if (_activeHeartsChallenge != null) { UpdateHeartsChallengeModal(mouse, leftPressed); return true; }
        if (_addOpen) { UpdateAddDialog(mouse, leftPressed); return true; }
        if (_activeRequest != null) { UpdateRequestModal(mouse, leftPressed); return true; }
        if (_activeChallenge != null) { UpdateChallengeModal(mouse, leftPressed); return true; }
        if (_statusPopOpen) { UpdateStatusPopover(mouse, leftPressed); return true; }

        var local = mouse - Position;
        bool inside = local.X >= 0 && local.X < W && local.Y >= 0 && local.Y < H;
        if (!inside)
        {
            if (_dragging && leftReleased) _dragging = false;
            if (_dragging) Position = mouse - _dragGrab;
            return false;
        }

        // Drag (title bar minus close X)
        if (_dragging)
        {
            Position = mouse - _dragGrab;
            if (leftReleased) { _dragging = false; StateChanged?.Invoke(); }
            return true;
        }
        if (leftPressed && local.Y < RetroWidgets.TitleBarHeight + 4)
        {
            if (RetroSkin.PointInRect(local, _closeBtn))
            {
                Visible = false;
                StateChanged?.Invoke();
                return true;
            }
            _dragging = true;
            _dragGrab = local;
            return true;
        }

        // Click on the friend-code line copies to clipboard.
        if (leftPressed && RetroSkin.PointInRect(local, _myCodeRect))
        {
            try { Raylib.SetClipboardText(_svc.Identity.Code); } catch { }
            return true;
        }

        // Click on Add button → open Add dialog.
        if (leftPressed && RetroSkin.PointInRect(local, _addBtn))
        {
            _addOpen = true;
            _addInput = "";
            _addStatus = "";
            return true;
        }

        // Click on a friend row — Golf ▶ opens the golf picker;
        // Chess ♛ opens the chess picker.
        if (leftPressed)
        {
            foreach (var r in _rows)
            {
                if (RetroSkin.PointInRect(local, r.GolfBtn))
                {
                    _golfPickerOpen = true;
                    _golfPickerFriend = r.Friend;
                    return true;
                }
                if (RetroSkin.PointInRect(local, r.ChessBtn))
                {
                    _chessPickerOpen = true;
                    _chessPickerFriend = r.Friend;
                    _chessPickerStatus = "";
                    _chessPickerFetching = false;
                    return true;
                }
                if (RetroSkin.PointInRect(local, r.TetrisBtn))
                {
                    _tetrisPickerOpen = true;
                    _tetrisPickerFriend = r.Friend;
                    return true;
                }
                if (RetroSkin.PointInRect(local, r.HeartsBtn))
                {
                    OpenHeartsPicker(r.Friend);
                    return true;
                }
            }
        }

        // Click on the status pill (bottom) → open the status popover.
        if (leftPressed && RetroSkin.PointInRect(local, _statusPill))
        {
            _statusPopOpen = true;
            _awayMessageDraft = _svc.SelfAwayMessage;
            return true;
        }

        // Scroll wheel
        float wheel = Raylib.GetMouseWheelMove();
        if (wheel != 0)
        {
            _scroll = Math.Max(0, _scroll - (int)(wheel * 22));
        }

        return inside;
    }

    private void UpdateAddDialog(Vector2 mouse, bool leftPressed)
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            _addOpen = false;
            return;
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Backspace) || Raylib.IsKeyPressedRepeat(KeyboardKey.Backspace))
            if (_addInput.Length > 0) _addInput = _addInput[..^1];
        int ch;
        while ((ch = Raylib.GetCharPressed()) > 0)
        {
            if (_addInput.Length < 24 && ch >= 32 && ch <= 126) _addInput += (char)ch;
        }

        if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter))
            TrySend();

        // Hit-test the two buttons drawn at the bottom of the dialog.
        // Layout matches the Draw side — cached _addOkBtn / _addCancelBtn.
        if (leftPressed && RetroSkin.PointInRect(mouse, _addOkBtn)) TrySend();
        if (leftPressed && RetroSkin.PointInRect(mouse, _addCancelBtn)) _addOpen = false;

        void TrySend()
        {
            var norm = FriendCode.Normalise(_addInput);
            if (!FriendCode.IsValid(norm))
            {
                _addStatus = "Code must be 12 letters/digits.";
                return;
            }
            if (norm == _svc.Identity.Code)
            {
                _addStatus = "That's your own code.";
                return;
            }
            _svc.SendFriendRequest(norm);
            _addStatus = $"Request sent to {FriendCode.Format(norm)}.";
            _addInput = "";
        }
    }

    private Rectangle _addOkBtn;
    private Rectangle _addCancelBtn;

    private void UpdateGolfPicker(Vector2 mouse, bool leftPressed)
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { _golfPickerOpen = false; return; }
        if (!leftPressed) return;
        for (int i = 0; i < _golfPickerRegionRects.Length; i++)
            if (RetroSkin.PointInRect(mouse, _golfPickerRegionRects[i])) _golfPickerRegionIdx = i;
        for (int i = 0; i < _golfPickerDifficultyRects.Length; i++)
            if (RetroSkin.PointInRect(mouse, _golfPickerDifficultyRects[i])) _golfPickerDifficulty = i;
        if (RetroSkin.PointInRect(mouse, _golfPickerCancelBtn)) { _golfPickerOpen = false; return; }
        if (RetroSkin.PointInRect(mouse, _golfPickerSendBtn) && _golfPickerFriend != null)
        {
            // Cosmetic guard: if the friend is offline by our last
            // presence read, the broker publish still succeeds but
            // no one's there to consume the inbox. Surface a toast
            // and keep the picker open so the user can retry
            // against someone else.
            var status = _golfPickerFriend.LastStatus;
            bool stale = DateTime.UtcNow - _golfPickerFriend.LastSeenUtc
                > NetConfig.PresenceStaleAfter;
            if (status == BuddyStatus.Offline || stale)
            {
                _challengeAwaitingLine =
                    $"{_golfPickerFriend.Nickname} is offline.";
                _challengeAwaitingUntil = DateTime.UtcNow.AddSeconds(4);
                _golfPickerOpen = false;
                return;
            }
            // Generate a fresh seed and stash everything the
            // OnGolfRaceMessage("accept") handler needs to build the
            // session when the peer replies. The seed is a 32-bit
            // random int so its on-wire JSON encoding is compact.
            Span<byte> seedBytes = stackalloc byte[4];
            System.Security.Cryptography.RandomNumberGenerator.Fill(seedBytes);
            int seed = BitConverter.ToInt32(seedBytes);
            string region = PickerRegions[_golfPickerRegionIdx];
            int difficulty = _golfPickerDifficulty;

            _pendingOutgoingPeerCode = _golfPickerFriend.Code;
            _pendingOutgoingSeed = seed;
            _pendingOutgoingRegion = region;
            _pendingOutgoingDifficulty = difficulty;

            _ = _svc.Client.SendGolfRace(_golfPickerFriend.Code,
                new GolfRacePayload
                {
                    Sub = "challenge",
                    Seed = seed,
                    Region = region,
                    Difficulty = difficulty,
                });
            _challengeAwaitingLine = $"Awaiting response from {_golfPickerFriend.Nickname}…";
            _challengeAwaitingUntil = DateTime.UtcNow.AddSeconds(60);
            _golfPickerOpen = false;
        }
    }

    private void UpdateGolfChallengeModal(Vector2 mouse, bool leftPressed)
    {
        if (_activeGolfChallenge == null) return;
        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            DeclineGolfChallenge();
            return;
        }
        if (leftPressed && RetroSkin.PointInRect(mouse, _golfChDeclineBtn))
        {
            DeclineGolfChallenge();
            return;
        }
        if (leftPressed && RetroSkin.PointInRect(mouse, _golfChAcceptBtn))
        {
            AcceptGolfChallenge();
        }
    }

    private void DeclineGolfChallenge()
    {
        if (_activeGolfChallenge == null) return;
        _ = _svc.Client.SendGolfRace(_activeGolfChallengeFromCode,
            new GolfRacePayload { Sub = "decline" });
        _activeGolfChallenge = null;
    }

    private void AcceptGolfChallenge()
    {
        if (_activeGolfChallenge == null) return;
        var peer = _svc.Friends.Find(_activeGolfChallengeFromCode);
        if (peer == null) { _activeGolfChallenge = null; return; }
        // Build the session with the SENDER's params — both sides
        // hash to the same seed so the heightmap matches.
        var session = new NetplayGolfSession(_svc, peer, isHost: false,
            _activeGolfChallenge.Seed,
            _activeGolfChallenge.Region,
            _activeGolfChallenge.Difficulty);
        _svc.RegisterGolfSession(session);
        // Tell the host we accepted — they'll open their activity
        // when this lands on their side.
        _ = _svc.Client.SendGolfRace(_activeGolfChallengeFromCode,
            new GolfRacePayload { Sub = "accept" });
        _svc.RaiseOpenNetplayGolf(session);
        _activeGolfChallenge = null;
    }

    private void UpdateRequestModal(Vector2 mouse, bool leftPressed)
    {
        if (_activeRequest == null) return;
        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            _svc.RejectRequest(_activeRequest);
            _activeRequest = null;
            return;
        }
        if (leftPressed && RetroSkin.PointInRect(mouse, _reqAcceptBtn))
        {
            _svc.AcceptRequest(_activeRequest);
            _activeRequest = null;
        }
        else if (leftPressed && RetroSkin.PointInRect(mouse, _reqRejectBtn))
        {
            _svc.RejectRequest(_activeRequest);
            _activeRequest = null;
        }
    }
    private Rectangle _reqAcceptBtn;
    private Rectangle _reqRejectBtn;

    private void UpdateChallengeModal(Vector2 mouse, bool leftPressed)
    {
        if (_activeChallenge == null) return;
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { _activeChallenge = null; return; }
        if (leftPressed && RetroSkin.PointInRect(mouse, _chAcceptBtn))
        {
            // Stub: in v1 we open the "Awaiting opponent…" placeholder
            // line in the panel. Full netplay golf/chess wiring is a
            // separate follow-up.
            _challengeAwaitingLine = $"Match with {_activeChallenge.FromName} — netplay coming soon.";
            _challengeAwaitingUntil = DateTime.UtcNow.AddSeconds(8);
            _activeChallenge = null;
        }
        else if (leftPressed && RetroSkin.PointInRect(mouse, _chDeclineBtn))
        {
            _activeChallenge = null;
        }
    }
    private Rectangle _chAcceptBtn;
    private Rectangle _chDeclineBtn;

    private void UpdateStatusPopover(Vector2 mouse, bool leftPressed)
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { CommitStatusPopover(); return; }

        // Click outside closes (and commits the away-message draft).
        if (leftPressed && !RetroSkin.PointInRect(mouse - Position, _statusPopRect))
        {
            CommitStatusPopover();
            return;
        }
        // Click one of the 4 status options.
        for (int i = 0; i < _statusOptions.Length; i++)
        {
            if (leftPressed && RetroSkin.PointInRect(mouse - Position, _statusOptions[i]))
            {
                var s = (BuddyStatus)(i + 1);   // skip Offline (index 0)
                _svc.SetSelfStatus(s, _awayMessageDraft);
                return;
            }
        }
        // Click into the away-message field.
        if (leftPressed)
            _awayMessageFocused = RetroSkin.PointInRect(mouse - Position, _awayMessageField);

        if (_awayMessageFocused)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Backspace) || Raylib.IsKeyPressedRepeat(KeyboardKey.Backspace))
                if (_awayMessageDraft.Length > 0) _awayMessageDraft = _awayMessageDraft[..^1];
            int ch;
            while ((ch = Raylib.GetCharPressed()) > 0)
                if (_awayMessageDraft.Length < 255 && ch >= 32 && ch <= 126)
                    _awayMessageDraft += (char)ch;
        }
    }

    private Rectangle _statusPopRect;

    private void CommitStatusPopover()
    {
        _svc.SetSelfStatus(_svc.SelfStatus, _awayMessageDraft);
        _statusPopOpen = false;
        _awayMessageFocused = false;
    }

    // ── Draw ─────────────────────────────────────────────────────────────
    public void Draw()
    {
        if (!Visible) return;

        int x = (int)Position.X, y = (int)Position.Y;

        // Drop shadow.
        Raylib.DrawRectangle(x + 4, y + 4, W, H, new Color((byte)0, (byte)0, (byte)0, (byte)110));

        var panel = new Rectangle(x, y, W, H);
        RetroWidgets.DrawWindowFrame(panel);

        // Title bar — yellow tint to nod at AIM's buddy-list panel.
        var titleBar = new Rectangle(x + 2, y + 2, W - 4, RetroWidgets.TitleBarHeight);
        Raylib.DrawRectangleGradientH((int)titleBar.X, (int)titleBar.Y,
            (int)titleBar.Width, (int)titleBar.Height,
            new Color((byte)200, (byte)156, (byte)20, (byte)255),
            new Color((byte)244, (byte)200, (byte)80, (byte)255));
        RetroSkin.DrawText("Buddies", (int)titleBar.X + 6, (int)titleBar.Y + 1,
            new Color((byte)40, (byte)24, (byte)8, (byte)255), RetroSkin.TitleFontSize);

        _closeBtn = new Rectangle(titleBar.X + titleBar.Width - 18, titleBar.Y + 2, 16, 14);
        RetroSkin.DrawRaised(_closeBtn);
        DrawX(_closeBtn);

        // My code strip.
        int yCur = (int)titleBar.Y + (int)titleBar.Height + 6;
        _myCodeRect = new Rectangle(x + 6 - x, yCur - y, W - 12, 18);
        var codeAbs = new Rectangle(x + 6, yCur, W - 12, 18);
        RetroSkin.DrawSunken(codeAbs);
        RetroSkin.DrawText("Your code: " + FriendCode.Format(_svc.Identity.Code),
            (int)codeAbs.X + 4, (int)codeAbs.Y + 2,
            RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
        yCur += 22;

        // Buddy list.
        int listTop = yCur;
        int listH = H - (listTop - y) - 60;     // leaves room for status pill + Add button
        var listRect = new Rectangle(x + 6, listTop, W - 12, listH);
        RetroSkin.DrawSunken(listRect, fill: RetroSkin.SunkenBg);

        _rows.Clear();
        int rowH = 28;
        int ry = (int)listRect.Y + 4 - _scroll;
        foreach (var f in _svc.Friends.Friends)
        {
            DrawFriendRow(f, listRect, ry, rowH);
            ry += rowH;
        }
        // Empty state.
        if (_svc.Friends.Friends.Count == 0)
        {
            RetroSkin.DrawText("No friends yet.",
                (int)listRect.X + 8, (int)listRect.Y + 10,
                RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);
            RetroSkin.DrawText("Click Add Friend below.",
                (int)listRect.X + 8, (int)listRect.Y + 26,
                RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);
        }

        // Bottom row: status pill (left) + Add Friend button (right).
        int btnY = y + H - 30;
        _statusPill = new Rectangle(6, H - 30 - 2, 120, 22);
        var statusAbs = new Rectangle(x + _statusPill.X, y + _statusPill.Y,
            _statusPill.Width, _statusPill.Height);
        DrawStatusPill(statusAbs);

        _addBtn = new Rectangle(W - 6 - 90, H - 30 - 2, 90, 22);
        var addAbs = new Rectangle(x + _addBtn.X, y + _addBtn.Y, _addBtn.Width, _addBtn.Height);
        RetroWidgets.ButtonVisual(addAbs, "+ Add Friend", false);

        // One-shot "Sent challenge..." line, just above the bottom row.
        if (DateTime.UtcNow < _challengeAwaitingUntil
            && !string.IsNullOrEmpty(_challengeAwaitingLine))
        {
            RetroSkin.DrawText(_challengeAwaitingLine, x + 8, btnY - 18,
                RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);
        }

        // Network status hint (small, below code) so the user sees
        // if the broker isn't reachable.
        if (!_svc.Client.Connected)
        {
            string note = _svc.Client.Connecting
                ? "Connecting to buddy server…"
                : (_svc.Client.LastError != null
                    ? "Offline (broker unreachable)"
                    : "Offline");
            RetroSkin.DrawText(note,
                x + 8, (int)(listRect.Y + listRect.Height + 2),
                RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);
        }

        // Modal overlays — always last. Picker / golf-challenge
        // sit *above* the friend-request modal so they win z-order
        // when a request lands mid-challenge.
        if (_addOpen) DrawAddDialog();
        if (_activeRequest != null) DrawRequestModal();
        if (_activeChallenge != null) DrawChallengeModal();
        if (_activeGolfChallenge != null) DrawGolfChallengeModal();
        if (_activeChessChallenge != null) DrawChessChallengeModal();
        if (_activeTetrisChallenge != null) DrawTetrisChallengeModal();
        if (_activeHeartsChallenge != null) DrawHeartsChallengeModal();
        if (_golfPickerOpen) DrawGolfPicker();
        if (_chessPickerOpen) DrawChessPicker();
        if (_tetrisPickerOpen) DrawTetrisPicker();
        if (_heartsPickerOpen) DrawHeartsPicker();
        if (_statusPopOpen) DrawStatusPopover();
    }

    private void DrawFriendRow(Friend f, Rectangle listRect, int ry, int rowH)
    {
        // Clip vertically.
        int topCut = Math.Max(ry, (int)listRect.Y);
        int botCut = Math.Min(ry + rowH, (int)listRect.Y + (int)listRect.Height);
        if (botCut <= topCut) return;

        var status = EffectiveStatus(f);

        // Status dot.
        int dotX = (int)listRect.X + 12;
        int dotY = ry + 8;
        Raylib.DrawCircle(dotX, dotY, 5, StatusColor(status));
        Raylib.DrawCircleLines(dotX, dotY, 5, new Color((byte)0, (byte)0, (byte)0, (byte)80));

        // Name (bold-looking via shadow).
        string name = string.IsNullOrEmpty(f.Nickname) ? f.Code : f.Nickname;
        RetroSkin.DrawText(name, dotX + 12, ry + 2, RetroSkin.BodyText, RetroSkin.BodyFontSize - 1);

        // Away message (italic-ish — render in disabled colour as a stand-in).
        if (!string.IsNullOrEmpty(f.LastStatusMessage)
            && status != BuddyStatus.Offline)
        {
            string m = f.LastStatusMessage;
            if (m.Length > 28) m = m[..28] + "…";
            RetroSkin.DrawText(m, dotX + 12, ry + 16,
                RetroSkin.DisabledText, RetroSkin.BodyFontSize - 3);
        }

        // Challenge buttons on the right — golf ▶, chess ♛, tetris ▦, hearts ♥.
        // Four buttons + 2 px gaps eats ~80 px of the row; the
        // remaining ~150 px holds the friend name (truncated via
        // RetroWidgets.TruncateToWidth on long nicknames).
        var hearts = new Rectangle(listRect.X + listRect.Width - 22, ry + 4, 20, rowH - 8);
        var tetris = new Rectangle(hearts.X - 22, ry + 4, 20, rowH - 8);
        var chess = new Rectangle(tetris.X - 22, ry + 4, 20, rowH - 8);
        var golf = new Rectangle(chess.X - 22, ry + 4, 20, rowH - 8);
        bool canChallenge = status == BuddyStatus.Online
            || status == BuddyStatus.Idle
            || status == BuddyStatus.Away;
        RetroWidgets.ButtonVisual(golf, "▶", !canChallenge);
        RetroWidgets.ButtonVisual(chess, "♛", !canChallenge);
        RetroWidgets.ButtonVisual(tetris, "▦", !canChallenge);
        RetroWidgets.ButtonVisual(hearts, "♥", !canChallenge);

        _rows.Add((f,
            new Rectangle(listRect.X - Position.X, ry - Position.Y, listRect.Width, rowH),
            new Rectangle(golf.X - Position.X, golf.Y - Position.Y, golf.Width, golf.Height),
            new Rectangle(chess.X - Position.X, chess.Y - Position.Y, chess.Width, chess.Height),
            new Rectangle(tetris.X - Position.X, tetris.Y - Position.Y, tetris.Width, tetris.Height),
            new Rectangle(hearts.X - Position.X, hearts.Y - Position.Y, hearts.Width, hearts.Height)));
    }

    private BuddyStatus EffectiveStatus(Friend f)
    {
        if (f.LastStatus == BuddyStatus.Offline) return BuddyStatus.Offline;
        if (DateTime.UtcNow - f.LastSeenUtc > NetConfig.PresenceStaleAfter)
            return BuddyStatus.Offline;
        return f.LastStatus;
    }

    private static Color StatusColor(BuddyStatus s) => s switch
    {
        BuddyStatus.Online => new Color((byte)80, (byte)200, (byte)80, (byte)255),
        BuddyStatus.Idle => new Color((byte)232, (byte)200, (byte)56, (byte)255),
        BuddyStatus.Away => new Color((byte)232, (byte)200, (byte)56, (byte)255),
        BuddyStatus.Busy => new Color((byte)220, (byte)60, (byte)60, (byte)255),
        BuddyStatus.Invisible => new Color((byte)128, (byte)128, (byte)128, (byte)255),
        _ => new Color((byte)128, (byte)128, (byte)128, (byte)255),
    };

    private void DrawStatusPill(Rectangle abs)
    {
        RetroSkin.DrawRaised(abs);
        var s = _svc.SelfStatus;
        Raylib.DrawCircle((int)abs.X + 10, (int)abs.Y + (int)abs.Height / 2, 5, StatusColor(s));
        RetroSkin.DrawText(StatusLabel(s),
            (int)abs.X + 22, (int)abs.Y + 4,
            RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
        // Down-chevron hint.
        RetroSkin.DrawText("▾",
            (int)abs.X + (int)abs.Width - 14, (int)abs.Y + 3,
            RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
    }

    private static string StatusLabel(BuddyStatus s) => s switch
    {
        BuddyStatus.Online => "Available",
        BuddyStatus.Idle => "Idle",
        BuddyStatus.Away => "Away",
        BuddyStatus.Busy => "Busy / DND",
        BuddyStatus.Invisible => "Invisible",
        _ => "Offline",
    };

    private void DrawStatusPopover()
    {
        // Sits above the status pill, anchored to its top-left.
        int popW = 200;
        int popH = 150;
        int popX = (int)_statusPill.X;
        int popY = (int)_statusPill.Y - popH - 4;
        _statusPopRect = new Rectangle(popX, popY, popW, popH);

        var abs = new Rectangle(Position.X + popX, Position.Y + popY, popW, popH);
        Raylib.DrawRectangle(0, 0,
            Raylib.GetRenderWidth(), Raylib.GetRenderHeight(),
            new Color((byte)0, (byte)0, (byte)0, (byte)60));
        RetroSkin.DrawRaised(abs);

        // Four options (Online / Away / Busy / Invisible).
        var labels = new[] { "Available", "Away", "Busy / DND", "Invisible" };
        _statusOptions = new Rectangle[labels.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            int oy = popY + 4 + i * 22;
            _statusOptions[i] = new Rectangle(popX + 4, oy, popW - 8, 20);
            var absR = new Rectangle(Position.X + _statusOptions[i].X,
                Position.Y + _statusOptions[i].Y,
                _statusOptions[i].Width, _statusOptions[i].Height);
            bool selected = (int)_svc.SelfStatus == i + 1;
            if (selected) RetroSkin.DrawPressed(absR);
            Raylib.DrawCircle((int)absR.X + 10, (int)absR.Y + (int)absR.Height / 2,
                5, StatusColor((BuddyStatus)(i + 1)));
            RetroSkin.DrawText(labels[i],
                (int)absR.X + 22, (int)absR.Y + 3,
                RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
        }

        // Away-message field.
        int fyRel = popY + 4 + labels.Length * 22 + 4;
        _awayMessageField = new Rectangle(popX + 4, fyRel, popW - 8, 32);
        var fyAbs = new Rectangle(Position.X + _awayMessageField.X,
            Position.Y + _awayMessageField.Y,
            _awayMessageField.Width, _awayMessageField.Height);
        RetroSkin.DrawSunken(fyAbs);
        string text = _awayMessageDraft;
        if (string.IsNullOrEmpty(text) && !_awayMessageFocused)
        {
            RetroSkin.DrawText("(away message)",
                (int)fyAbs.X + 4, (int)fyAbs.Y + 4,
                RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);
        }
        else
        {
            RetroSkin.DrawText(text,
                (int)fyAbs.X + 4, (int)fyAbs.Y + 4,
                RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
            if (_awayMessageFocused && ((int)(_caretBlink * 2) & 1) == 0)
            {
                int tw = RetroSkin.MeasureText(text, RetroSkin.BodyFontSize - 2);
                Raylib.DrawRectangle((int)fyAbs.X + 4 + tw + 1, (int)fyAbs.Y + 4,
                    1, RetroSkin.BodyFontSize - 2, RetroSkin.BodyText);
            }
        }
    }

    private void DrawAddDialog()
    {
        int dw = 280, dh = 130;
        int dx = (Raylib.GetRenderWidth() - dw) / 2;
        int dy = (Raylib.GetRenderHeight() - dh) / 2;
        Raylib.DrawRectangle(0, 0,
            Raylib.GetRenderWidth(), Raylib.GetRenderHeight(),
            new Color((byte)0, (byte)0, (byte)0, (byte)110));
        var panel = new Rectangle(dx, dy, dw, dh);
        RetroSkin.DrawRaised(panel);
        var bar = new Rectangle(dx + 2, dy + 2, dw - 4, RetroWidgets.TitleBarHeight);
        Raylib.DrawRectangleGradientH((int)bar.X, (int)bar.Y, (int)bar.Width, (int)bar.Height,
            RetroSkin.TitleActive, RetroSkin.TitleGradEnd);
        RetroSkin.DrawText("Add Friend", (int)bar.X + 4, (int)bar.Y + 1,
            RetroSkin.TitleText, RetroSkin.TitleFontSize);

        RetroSkin.DrawText("Enter your friend's 12-char code:",
            dx + 12, dy + 26, RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
        var field = new Rectangle(dx + 12, dy + 44, dw - 24, 22);
        RetroSkin.DrawSunken(field);
        RetroSkin.DrawText(_addInput,
            (int)field.X + 6, (int)field.Y + 4, RetroSkin.BodyText,
            RetroSkin.BodyFontSize - 2);
        if (((int)(_caretBlink * 2) & 1) == 0)
        {
            int tw = RetroSkin.MeasureText(_addInput, RetroSkin.BodyFontSize - 2);
            Raylib.DrawRectangle((int)field.X + 6 + tw + 1, (int)field.Y + 4,
                1, RetroSkin.BodyFontSize - 2, RetroSkin.BodyText);
        }

        if (!string.IsNullOrEmpty(_addStatus))
            RetroSkin.DrawText(_addStatus,
                dx + 12, dy + 70, RetroSkin.DisabledText,
                RetroSkin.BodyFontSize - 2);

        _addCancelBtn = new Rectangle(dx + dw - 12 - 70 - 6 - 70, dy + dh - 30, 70, 22);
        _addOkBtn = new Rectangle(dx + dw - 12 - 70, dy + dh - 30, 70, 22);
        RetroWidgets.ButtonVisual(_addCancelBtn, "Cancel", false);
        RetroWidgets.ButtonVisual(_addOkBtn, "Send", false);
    }

    private void DrawRequestModal()
    {
        var r = _activeRequest!;
        int dw = 320, dh = 150;
        int dx = (Raylib.GetRenderWidth() - dw) / 2;
        int dy = (Raylib.GetRenderHeight() - dh) / 2;
        Raylib.DrawRectangle(0, 0,
            Raylib.GetRenderWidth(), Raylib.GetRenderHeight(),
            new Color((byte)0, (byte)0, (byte)0, (byte)110));
        var panel = new Rectangle(dx, dy, dw, dh);
        RetroSkin.DrawRaised(panel);
        var bar = new Rectangle(dx + 2, dy + 2, dw - 4, RetroWidgets.TitleBarHeight);
        Raylib.DrawRectangleGradientH((int)bar.X, (int)bar.Y, (int)bar.Width, (int)bar.Height,
            new Color((byte)200, (byte)156, (byte)20, (byte)255),
            new Color((byte)244, (byte)200, (byte)80, (byte)255));
        RetroSkin.DrawText("Friend Request",
            (int)bar.X + 4, (int)bar.Y + 1,
            new Color((byte)40, (byte)24, (byte)8, (byte)255),
            RetroSkin.TitleFontSize);

        RetroSkin.DrawText(r.FromName + " wants to add you.",
            dx + 12, dy + 30, RetroSkin.BodyText, RetroSkin.BodyFontSize - 1);
        RetroSkin.DrawText("Friend code: " + FriendCode.Format(r.FromCode),
            dx + 12, dy + 54, RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);
        RetroSkin.DrawText("Accept = both add each other to friends.",
            dx + 12, dy + 76, RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);

        _reqRejectBtn = new Rectangle(dx + dw - 12 - 70 - 6 - 70, dy + dh - 30, 70, 22);
        _reqAcceptBtn = new Rectangle(dx + dw - 12 - 70, dy + dh - 30, 70, 22);
        RetroWidgets.ButtonVisual(_reqRejectBtn, "Reject", false);
        RetroWidgets.ButtonVisual(_reqAcceptBtn, "Accept", false);
    }

    private void DrawChallengeModal()
    {
        var ch = _activeChallenge!;
        int dw = 320, dh = 140;
        int dx = (Raylib.GetRenderWidth() - dw) / 2;
        int dy = (Raylib.GetRenderHeight() - dh) / 2;
        Raylib.DrawRectangle(0, 0,
            Raylib.GetRenderWidth(), Raylib.GetRenderHeight(),
            new Color((byte)0, (byte)0, (byte)0, (byte)110));
        var panel = new Rectangle(dx, dy, dw, dh);
        RetroSkin.DrawRaised(panel);
        var bar = new Rectangle(dx + 2, dy + 2, dw - 4, RetroWidgets.TitleBarHeight);
        Raylib.DrawRectangleGradientH((int)bar.X, (int)bar.Y, (int)bar.Width, (int)bar.Height,
            RetroSkin.TitleActive, RetroSkin.TitleGradEnd);
        RetroSkin.DrawText("Incoming challenge",
            (int)bar.X + 4, (int)bar.Y + 1, RetroSkin.TitleText, RetroSkin.TitleFontSize);

        string game = ch.Game switch
        {
            "golf" => "Ohio Golf",
            "chess" => "Chess Puzzles",
            _ => ch.Game,
        };
        RetroSkin.DrawText($"{ch.FromName} wants to play {game}.",
            dx + 12, dy + 30, RetroSkin.BodyText, RetroSkin.BodyFontSize - 1);
        RetroSkin.DrawText("(Netplay matches are stubbed in v1 —",
            dx + 12, dy + 60, RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);
        RetroSkin.DrawText(" accept just opens the lobby panel.)",
            dx + 12, dy + 74, RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);

        _chDeclineBtn = new Rectangle(dx + dw - 12 - 70 - 6 - 70, dy + dh - 30, 70, 22);
        _chAcceptBtn = new Rectangle(dx + dw - 12 - 70, dy + dh - 30, 70, 22);
        RetroWidgets.ButtonVisual(_chDeclineBtn, "Decline", false);
        RetroWidgets.ButtonVisual(_chAcceptBtn, "Accept", false);
    }

    private void DrawGolfPicker()
    {
        int dw = 380, dh = 300;
        int dx = (Raylib.GetRenderWidth() - dw) / 2;
        int dy = (Raylib.GetRenderHeight() - dh) / 2;
        Raylib.DrawRectangle(0, 0,
            Raylib.GetRenderWidth(), Raylib.GetRenderHeight(),
            new Color((byte)0, (byte)0, (byte)0, (byte)110));
        var panel = new Rectangle(dx, dy, dw, dh);
        RetroSkin.DrawRaised(panel);
        var bar = new Rectangle(dx + 2, dy + 2, dw - 4, RetroWidgets.TitleBarHeight);
        Raylib.DrawRectangleGradientH((int)bar.X, (int)bar.Y, (int)bar.Width, (int)bar.Height,
            RetroSkin.TitleActive, RetroSkin.TitleGradEnd);
        RetroSkin.DrawText("Challenge to Ohio Golf",
            (int)bar.X + 4, (int)bar.Y + 1,
            RetroSkin.TitleText, RetroSkin.TitleFontSize);
        if (_golfPickerFriend != null)
        {
            RetroSkin.DrawText("vs " + _golfPickerFriend.Nickname,
                dx + 12, dy + 26, RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
        }

        // Region grid: 2 columns × 4 rows.
        RetroSkin.DrawText("Region:",
            dx + 12, dy + 48, RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
        _golfPickerRegionRects = new Rectangle[PickerRegions.Length];
        int gx = dx + 12;
        int gy = dy + 66;
        int cellW = (dw - 24) / 2 - 4;
        int cellH = 20;
        for (int i = 0; i < PickerRegions.Length; i++)
        {
            int col = i % 2;
            int row = i / 2;
            _golfPickerRegionRects[i] = new Rectangle(
                gx + col * (cellW + 8), gy + row * (cellH + 4), cellW, cellH);
            bool selected = _golfPickerRegionIdx == i;
            if (selected) RetroSkin.DrawPressed(_golfPickerRegionRects[i]);
            else RetroSkin.DrawRaised(_golfPickerRegionRects[i]);
            RetroSkin.DrawText(PickerRegions[i],
                (int)_golfPickerRegionRects[i].X + 6,
                (int)_golfPickerRegionRects[i].Y + 3,
                RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
        }

        // Difficulty row.
        int diffY = gy + 4 * (cellH + 4) + 8;
        RetroSkin.DrawText("Difficulty:",
            dx + 12, diffY, RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
        _golfPickerDifficultyRects = new Rectangle[PickerDifficulties.Length];
        int dCellW = (dw - 24) / PickerDifficulties.Length - 2;
        for (int i = 0; i < PickerDifficulties.Length; i++)
        {
            _golfPickerDifficultyRects[i] = new Rectangle(
                dx + 12 + i * (dCellW + 2), diffY + 18, dCellW, cellH);
            bool selected = _golfPickerDifficulty == i;
            if (selected) RetroSkin.DrawPressed(_golfPickerDifficultyRects[i]);
            else RetroSkin.DrawRaised(_golfPickerDifficultyRects[i]);
            int tw = RetroSkin.MeasureText(PickerDifficulties[i], RetroSkin.BodyFontSize - 3);
            RetroSkin.DrawText(PickerDifficulties[i],
                (int)_golfPickerDifficultyRects[i].X
                + ((int)_golfPickerDifficultyRects[i].Width - tw) / 2,
                (int)_golfPickerDifficultyRects[i].Y + 4,
                RetroSkin.BodyText, RetroSkin.BodyFontSize - 3);
        }

        _golfPickerCancelBtn = new Rectangle(
            dx + dw - 12 - 80 - 6 - 80, dy + dh - 30, 80, 22);
        _golfPickerSendBtn = new Rectangle(dx + dw - 12 - 80, dy + dh - 30, 80, 22);
        RetroWidgets.ButtonVisual(_golfPickerCancelBtn, "Cancel", false);
        RetroWidgets.ButtonVisual(_golfPickerSendBtn, "Send →", false);
    }

    private void DrawGolfChallengeModal()
    {
        var p = _activeGolfChallenge!;
        int dw = 340, dh = 160;
        int dx = (Raylib.GetRenderWidth() - dw) / 2;
        int dy = (Raylib.GetRenderHeight() - dh) / 2;
        Raylib.DrawRectangle(0, 0,
            Raylib.GetRenderWidth(), Raylib.GetRenderHeight(),
            new Color((byte)0, (byte)0, (byte)0, (byte)110));
        var panel = new Rectangle(dx, dy, dw, dh);
        RetroSkin.DrawRaised(panel);
        var bar = new Rectangle(dx + 2, dy + 2, dw - 4, RetroWidgets.TitleBarHeight);
        Raylib.DrawRectangleGradientH((int)bar.X, (int)bar.Y, (int)bar.Width, (int)bar.Height,
            new Color((byte)200, (byte)156, (byte)20, (byte)255),
            new Color((byte)244, (byte)200, (byte)80, (byte)255));
        RetroSkin.DrawText("Incoming Race",
            (int)bar.X + 4, (int)bar.Y + 1,
            new Color((byte)40, (byte)24, (byte)8, (byte)255),
            RetroSkin.TitleFontSize);

        RetroSkin.DrawText(
            $"{_activeGolfChallengeFromName} wants to race in Ohio Golf:",
            dx + 12, dy + 30, RetroSkin.BodyText, RetroSkin.BodyFontSize - 1);
        string diff = (p.Difficulty >= 0 && p.Difficulty < PickerDifficulties.Length)
            ? PickerDifficulties[p.Difficulty]
            : p.Difficulty.ToString();
        RetroSkin.DrawText(
            $"Region: {p.Region}    Difficulty: {diff}",
            dx + 12, dy + 56, RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);
        RetroSkin.DrawText("Same heightmap, tee, cup, and hazards on both sides.",
            dx + 12, dy + 76, RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);
        RetroSkin.DrawText("First to sink the ball wins.",
            dx + 12, dy + 92, RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);

        _golfChDeclineBtn = new Rectangle(dx + dw - 12 - 80 - 6 - 80, dy + dh - 30, 80, 22);
        _golfChAcceptBtn = new Rectangle(dx + dw - 12 - 80, dy + dh - 30, 80, 22);
        RetroWidgets.ButtonVisual(_golfChDeclineBtn, "Decline", false);
        RetroWidgets.ButtonVisual(_golfChAcceptBtn, "Race!", false);
    }

    private void UpdateChessPicker(Vector2 mouse, bool leftPressed)
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape) && !_chessPickerFetching)
        { _chessPickerOpen = false; return; }
        // Block all input while a pre-fetch is running — the user
        // can't cancel mid-fetch (the HTTP calls are sequential and
        // bounded by Lichess's per-request timeout); the status
        // line reads "Fetching…" so the freeze is visible.
        if (_chessPickerFetching) return;
        if (!leftPressed) return;
        for (int i = 0; i < _chessPickerTimeRects.Length; i++)
            if (RetroSkin.PointInRect(mouse, _chessPickerTimeRects[i])) _chessPickerTimeIdx = i;
        for (int i = 0; i < _chessPickerBandRects.Length; i++)
            if (RetroSkin.PointInRect(mouse, _chessPickerBandRects[i])) _chessPickerBandIdx = i;
        if (RetroSkin.PointInRect(mouse, _chessPickerCancelBtn)) { _chessPickerOpen = false; return; }
        if (RetroSkin.PointInRect(mouse, _chessPickerSendBtn) && _chessPickerFriend != null)
        {
            // Same offline guard as golf — broker publish would
            // succeed silently into a void otherwise.
            var status = _chessPickerFriend.LastStatus;
            bool stale = DateTime.UtcNow - _chessPickerFriend.LastSeenUtc
                > NetConfig.PresenceStaleAfter;
            if (status == BuddyStatus.Offline || stale)
            {
                _challengeAwaitingLine =
                    $"{_chessPickerFriend.Nickname} is offline.";
                _challengeAwaitingUntil = DateTime.UtcNow.AddSeconds(4);
                _chessPickerOpen = false;
                return;
            }
            // Kick off the pre-fetch on a background task. The UI
            // stays open showing "Fetching..." until the task
            // returns; we poll its completion in the picker's
            // Update tick via the next OnChessRaceMessage frame.
            _chessPickerFetching = true;
            _chessPickerStatus = "Fetching puzzles…";
            var band = PickerBands[_chessPickerBandIdx];
            int timeSec = PickerTimes[_chessPickerTimeIdx].Seconds;
            var target = _chessPickerFriend;
            _ = Task.Run(async () =>
            {
                // Network + JSON happen off-thread; everything that
                // touches widget state (pending fields, modal flags,
                // status strings) is marshalled back to the UI tick
                // via _uiThreadWork so the renderer never races with
                // a half-applied update.
                List<ChessRacePuzzle> puzzles;
                try { puzzles = await PreFetchPuzzlesAsync(band, count: 15); }
                catch
                {
                    _uiThreadWork.Enqueue(() =>
                    {
                        _chessPickerFetching = false;
                        _chessPickerStatus = "Puzzle fetch failed.";
                    });
                    return;
                }
                try
                {
                    await _svc.Client.SendChessRace(target.Code,
                        new ChessRacePayload
                        {
                            Sub = "challenge",
                            TimeLimitSeconds = timeSec,
                            StartingBand = band.Name,
                            Puzzles = puzzles,
                        });
                }
                catch
                {
                    _uiThreadWork.Enqueue(() =>
                    {
                        _chessPickerFetching = false;
                        _chessPickerStatus = "Send failed.";
                    });
                    return;
                }
                _uiThreadWork.Enqueue(() =>
                {
                    _pendingOutgoingChessPeerCode = target.Code;
                    _pendingOutgoingChessTimeSeconds = timeSec;
                    _pendingOutgoingChessBand = band.Name;
                    _pendingOutgoingChessPuzzles = puzzles;
                    _chessPickerFetching = false;
                    _challengeAwaitingLine = $"Awaiting response from {target.Nickname}…";
                    _challengeAwaitingUntil = DateTime.UtcNow.AddSeconds(60);
                    _chessPickerOpen = false;
                });
            });
        }
    }

    private void UpdateChessChallengeModal(Vector2 mouse, bool leftPressed)
    {
        if (_activeChessChallenge == null) return;
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { DeclineChessChallenge(); return; }
        if (leftPressed && RetroSkin.PointInRect(mouse, _chessChDeclineBtn)) { DeclineChessChallenge(); return; }
        if (leftPressed && RetroSkin.PointInRect(mouse, _chessChAcceptBtn)) { AcceptChessChallenge(); }
    }

    private void DeclineChessChallenge()
    {
        if (_activeChessChallenge == null) return;
        _ = _svc.Client.SendChessRace(_activeChessChallengeFromCode,
            new ChessRacePayload { Sub = "decline" });
        _activeChessChallenge = null;
    }

    private void AcceptChessChallenge()
    {
        if (_activeChessChallenge == null) return;
        var peer = _svc.Friends.Find(_activeChessChallengeFromCode);
        if (peer == null) { _activeChessChallenge = null; return; }
        // Build sink list directly from the sender's payload (same
        // shape, different namespace) and open the activity. Sender
        // mirror-opens via OnChessRaceMessage("accept").
        var srcList = _activeChessChallenge.Puzzles ?? new List<ChessRacePuzzle>();
        var sinkList = new List<NetplayChessPuzzle>(srcList.Count);
        foreach (var w in srcList)
        {
            sinkList.Add(new NetplayChessPuzzle
            {
                Id = w.Id, Pgn = w.Pgn, Solution = w.Solution,
                Rating = w.Rating, Fen = w.Fen,
                ExpectedUci = w.ExpectedUci, Title = w.Title,
                WhiteToMove = w.WhiteToMove,
            });
        }
        var session = new NetplayChessSession(_svc, peer, isHost: false,
            _activeChessChallenge.TimeLimitSeconds,
            _activeChessChallenge.StartingBand,
            sinkList);
        _svc.RegisterChessSession(session);
        _ = _svc.Client.SendChessRace(_activeChessChallengeFromCode,
            new ChessRacePayload { Sub = "accept" });
        _svc.RaiseOpenNetplayChess(session);
        _activeChessChallenge = null;
    }

    /// <summary>
    /// Pre-fetch a puzzle queue from Lichess, filtered to the band's
    /// rating window, sorted ascending so the race ramps. Falls back
    /// to the bundled 7-puzzle set if Lichess is unreachable.
    /// Sequential calls (one HTTP per puzzle) so we don't burst-load
    /// the public API — count is intentionally modest (15, not 25)
    /// to keep the picker's freeze time tolerable.
    /// </summary>
    private static async Task<List<ChessRacePuzzle>> PreFetchPuzzlesAsync(
        (string Name, int RatingFloor, int RatingCeiling) band, int count)
    {
        var collected = new List<ChessRacePuzzle>();
        int attempts = 0;
        while (collected.Count < count && attempts < count * 3)
        {
            attempts++;
            try
            {
                var r = await MouseHouse.Scenes.Activities.Chess.LichessClient.FetchNextAsync();
                if (r.Ok && r.Puzzle != null)
                {
                    // Filter to the band; rating-out-of-window
                    // puzzles get dropped (with a retry budget so
                    // an unlucky stretch doesn't loop forever).
                    var p = r.Puzzle;
                    if (p.Rating >= band.RatingFloor && p.Rating <= band.RatingCeiling)
                    {
                        collected.Add(new ChessRacePuzzle
                        {
                            Id = p.Id, Pgn = p.Pgn, Solution = p.Solution,
                            Rating = p.Rating, WhiteToMove = true,
                        });
                    }
                }
                else
                {
                    // Hard fail — bail to fallback.
                    break;
                }
            }
            catch { break; }
        }
        // Sort by rating ascending so the race ramps. If we got
        // fewer than count from Lichess, top up from the bundled
        // set so the queue always has at least *something* even
        // when both sides are partially offline.
        if (collected.Count == 0) return BundledFallbackQueue();
        collected.Sort((a, b) => a.Rating.CompareTo(b.Rating));
        return collected;
    }

    private static List<ChessRacePuzzle> BundledFallbackQueue()
    {
        // Mirrors RetroChessPuzzlesActivity.OfflinePuzzles. We can't
        // reach into that array from this assembly without an awkward
        // public surface; duplicating the 7 puzzles here is the
        // smaller evil. Order matches the activity's so a future
        // sync becomes a copy-paste.
        return new List<ChessRacePuzzle>
        {
            new() { Id = "offline-1", Title = "White to mate in 1 - back rank",
                Fen = "6k1/5ppp/8/8/8/8/8/R6K w - - 0 1",
                ExpectedUci = "a1a8", WhiteToMove = true, Rating = 700 },
            new() { Id = "offline-2", Title = "White to mate in 1 - queen ladder",
                Fen = "7k/8/6Q1/8/8/8/8/7K w - - 0 1",
                ExpectedUci = "g6g7", WhiteToMove = true, Rating = 720 },
            new() { Id = "offline-3", Title = "White to mate in 1 - supported queen",
                Fen = "5rk1/5ppp/8/8/8/8/3Q4/3R3K w - - 0 1",
                ExpectedUci = "d2d8", WhiteToMove = true, Rating = 780 },
            new() { Id = "offline-4", Title = "White to mate in 1 - knight + bishop",
                Fen = "6k1/6pp/8/6N1/8/8/4B3/7K w - - 0 1",
                ExpectedUci = "e2c4", WhiteToMove = true, Rating = 820 },
            new() { Id = "offline-5", Title = "White to win the queen - fork",
                Fen = "4k3/8/4q3/8/3N4/8/8/4K3 w - - 0 1",
                ExpectedUci = "d4f5", WhiteToMove = true, Rating = 900 },
            new() { Id = "offline-6", Title = "White to mate in 1 - rook lift",
                Fen = "k7/2R5/1K6/8/8/8/8/8 w - - 0 1",
                ExpectedUci = "c7c8", WhiteToMove = true, Rating = 940 },
            new() { Id = "offline-7", Title = "White to win material - pin",
                Fen = "4k3/8/8/3q4/8/8/3R4/3K4 w - - 0 1",
                ExpectedUci = "d2d5", WhiteToMove = true, Rating = 1020 },
        };
    }

    private void DrawChessPicker()
    {
        int dw = 380, dh = 240;
        int dx = (Raylib.GetRenderWidth() - dw) / 2;
        int dy = (Raylib.GetRenderHeight() - dh) / 2;
        Raylib.DrawRectangle(0, 0,
            Raylib.GetRenderWidth(), Raylib.GetRenderHeight(),
            new Color((byte)0, (byte)0, (byte)0, (byte)110));
        var panel = new Rectangle(dx, dy, dw, dh);
        RetroSkin.DrawRaised(panel);
        var bar = new Rectangle(dx + 2, dy + 2, dw - 4, RetroWidgets.TitleBarHeight);
        Raylib.DrawRectangleGradientH((int)bar.X, (int)bar.Y, (int)bar.Width, (int)bar.Height,
            RetroSkin.TitleActive, RetroSkin.TitleGradEnd);
        RetroSkin.DrawText("Challenge to Chess Puzzles",
            (int)bar.X + 4, (int)bar.Y + 1,
            RetroSkin.TitleText, RetroSkin.TitleFontSize);
        if (_chessPickerFriend != null)
        {
            RetroSkin.DrawText("vs " + _chessPickerFriend.Nickname,
                dx + 12, dy + 26, RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
        }

        // Time limit row.
        RetroSkin.DrawText("Time limit:",
            dx + 12, dy + 48, RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
        _chessPickerTimeRects = new Rectangle[PickerTimes.Length];
        int cellH = 22;
        int cellW = (dw - 24) / PickerTimes.Length - 2;
        for (int i = 0; i < PickerTimes.Length; i++)
        {
            _chessPickerTimeRects[i] = new Rectangle(
                dx + 12 + i * (cellW + 2), dy + 66, cellW, cellH);
            bool selected = _chessPickerTimeIdx == i;
            if (selected) RetroSkin.DrawPressed(_chessPickerTimeRects[i]);
            else RetroSkin.DrawRaised(_chessPickerTimeRects[i]);
            int tw = RetroSkin.MeasureText(PickerTimes[i].Label, RetroSkin.BodyFontSize - 2);
            RetroSkin.DrawText(PickerTimes[i].Label,
                (int)_chessPickerTimeRects[i].X
                + ((int)_chessPickerTimeRects[i].Width - tw) / 2,
                (int)_chessPickerTimeRects[i].Y + 3,
                RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
        }

        // Band row.
        RetroSkin.DrawText("Starting band:",
            dx + 12, dy + 100, RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
        _chessPickerBandRects = new Rectangle[PickerBands.Length];
        int bCellW = (dw - 24) / PickerBands.Length - 2;
        for (int i = 0; i < PickerBands.Length; i++)
        {
            _chessPickerBandRects[i] = new Rectangle(
                dx + 12 + i * (bCellW + 2), dy + 118, bCellW, cellH);
            bool selected = _chessPickerBandIdx == i;
            if (selected) RetroSkin.DrawPressed(_chessPickerBandRects[i]);
            else RetroSkin.DrawRaised(_chessPickerBandRects[i]);
            int tw = RetroSkin.MeasureText(PickerBands[i].Name, RetroSkin.BodyFontSize - 2);
            RetroSkin.DrawText(PickerBands[i].Name,
                (int)_chessPickerBandRects[i].X
                + ((int)_chessPickerBandRects[i].Width - tw) / 2,
                (int)_chessPickerBandRects[i].Y + 3,
                RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
        }

        if (!string.IsNullOrEmpty(_chessPickerStatus))
        {
            RetroSkin.DrawText(_chessPickerStatus,
                dx + 12, dy + 155,
                RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);
        }

        _chessPickerCancelBtn = new Rectangle(
            dx + dw - 12 - 80 - 6 - 80, dy + dh - 30, 80, 22);
        _chessPickerSendBtn = new Rectangle(dx + dw - 12 - 80, dy + dh - 30, 80, 22);
        bool sendDisabled = _chessPickerFetching;
        if (sendDisabled) RetroSkin.DrawPressed(_chessPickerSendBtn);
        else RetroWidgets.ButtonVisual(_chessPickerSendBtn, "Send →", false);
        RetroWidgets.ButtonVisual(_chessPickerCancelBtn,
            sendDisabled ? "..." : "Cancel", sendDisabled);
        if (sendDisabled)
        {
            int sw = RetroSkin.MeasureText("Fetching…", RetroSkin.BodyFontSize - 2);
            RetroSkin.DrawText("Fetching…",
                (int)_chessPickerSendBtn.X
                + ((int)_chessPickerSendBtn.Width - sw) / 2,
                (int)_chessPickerSendBtn.Y + 4,
                RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);
        }
    }

    private void DrawChessChallengeModal()
    {
        var p = _activeChessChallenge!;
        int dw = 360, dh = 170;
        int dx = (Raylib.GetRenderWidth() - dw) / 2;
        int dy = (Raylib.GetRenderHeight() - dh) / 2;
        Raylib.DrawRectangle(0, 0,
            Raylib.GetRenderWidth(), Raylib.GetRenderHeight(),
            new Color((byte)0, (byte)0, (byte)0, (byte)110));
        var panel = new Rectangle(dx, dy, dw, dh);
        RetroSkin.DrawRaised(panel);
        var bar = new Rectangle(dx + 2, dy + 2, dw - 4, RetroWidgets.TitleBarHeight);
        Raylib.DrawRectangleGradientH((int)bar.X, (int)bar.Y, (int)bar.Width, (int)bar.Height,
            new Color((byte)200, (byte)156, (byte)20, (byte)255),
            new Color((byte)244, (byte)200, (byte)80, (byte)255));
        RetroSkin.DrawText("Incoming Chess Race",
            (int)bar.X + 4, (int)bar.Y + 1,
            new Color((byte)40, (byte)24, (byte)8, (byte)255),
            RetroSkin.TitleFontSize);

        RetroSkin.DrawText(
            $"{_activeChessChallengeFromName} wants to race chess puzzles:",
            dx + 12, dy + 30, RetroSkin.BodyText, RetroSkin.BodyFontSize - 1);
        int mm = p.TimeLimitSeconds / 60;
        int ss = p.TimeLimitSeconds % 60;
        string timeStr = ss == 0 ? $"{mm} min" : $"{mm}:{ss:D2}";
        RetroSkin.DrawText($"Time: {timeStr}    Band: {p.StartingBand}",
            dx + 12, dy + 56, RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);
        RetroSkin.DrawText(
            $"{p.Puzzles?.Count ?? 0} ramping puzzles, identical on both sides.",
            dx + 12, dy + 76, RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);
        RetroSkin.DrawText("Most solved when time runs out wins.",
            dx + 12, dy + 92, RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);

        _chessChDeclineBtn = new Rectangle(dx + dw - 12 - 80 - 6 - 80, dy + dh - 30, 80, 22);
        _chessChAcceptBtn = new Rectangle(dx + dw - 12 - 80, dy + dh - 30, 80, 22);
        RetroWidgets.ButtonVisual(_chessChDeclineBtn, "Decline", false);
        RetroWidgets.ButtonVisual(_chessChAcceptBtn, "Race!", false);
    }

    private void UpdateTetrisPicker(Vector2 mouse, bool leftPressed)
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { _tetrisPickerOpen = false; return; }
        if (!leftPressed) return;
        for (int i = 0; i < _tetrisPickerLevelRects.Length; i++)
            if (RetroSkin.PointInRect(mouse, _tetrisPickerLevelRects[i])) _tetrisPickerLevelIdx = i;
        if (RetroSkin.PointInRect(mouse, _tetrisPickerCancelBtn)) { _tetrisPickerOpen = false; return; }
        if (RetroSkin.PointInRect(mouse, _tetrisPickerSendBtn) && _tetrisPickerFriend != null)
        {
            // Offline guard — mirror golf and chess pickers.
            var status = _tetrisPickerFriend.LastStatus;
            bool stale = DateTime.UtcNow - _tetrisPickerFriend.LastSeenUtc
                > NetConfig.PresenceStaleAfter;
            if (status == BuddyStatus.Offline || stale)
            {
                _challengeAwaitingLine = $"{_tetrisPickerFriend.Nickname} is offline.";
                _challengeAwaitingUntil = DateTime.UtcNow.AddSeconds(4);
                _tetrisPickerOpen = false;
                return;
            }
            // Roll a fresh 32-bit course seed; remember it for the
            // accept reply so we build the session with the same
            // bag sequence the peer will see.
            Span<byte> seedBytes = stackalloc byte[4];
            System.Security.Cryptography.RandomNumberGenerator.Fill(seedBytes);
            int seed = BitConverter.ToInt32(seedBytes);
            int level = _tetrisPickerLevelIdx + 1;
            _pendingOutgoingTetrisPeerCode = _tetrisPickerFriend.Code;
            _pendingOutgoingTetrisSeed = seed;
            _pendingOutgoingTetrisLevel = level;
            _ = _svc.Client.SendTetrisRace(_tetrisPickerFriend.Code,
                new TetrisRacePayload
                {
                    Sub = "challenge",
                    Seed = seed,
                    StartingLevel = level,
                });
            _challengeAwaitingLine = $"Awaiting response from {_tetrisPickerFriend.Nickname}…";
            _challengeAwaitingUntil = DateTime.UtcNow.AddSeconds(60);
            _tetrisPickerOpen = false;
        }
    }

    private void UpdateTetrisChallengeModal(Vector2 mouse, bool leftPressed)
    {
        if (_activeTetrisChallenge == null) return;
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { DeclineTetrisChallenge(); return; }
        if (leftPressed && RetroSkin.PointInRect(mouse, _tetrisChDeclineBtn)) { DeclineTetrisChallenge(); return; }
        if (leftPressed && RetroSkin.PointInRect(mouse, _tetrisChAcceptBtn)) { AcceptTetrisChallenge(); }
    }

    private void DeclineTetrisChallenge()
    {
        if (_activeTetrisChallenge == null) return;
        _ = _svc.Client.SendTetrisRace(_activeTetrisChallengeFromCode,
            new TetrisRacePayload { Sub = "decline" });
        _activeTetrisChallenge = null;
    }

    private void AcceptTetrisChallenge()
    {
        if (_activeTetrisChallenge == null) return;
        var peer = _svc.Friends.Find(_activeTetrisChallengeFromCode);
        if (peer == null) { _activeTetrisChallenge = null; return; }
        // Recipient builds the session with the SENDER's seed +
        // level — same 7-bag sequence, same starting gravity.
        var session = new NetplayTetrisSession(_svc, peer, isHost: false,
            _activeTetrisChallenge.Seed, _activeTetrisChallenge.StartingLevel);
        _svc.RegisterTetrisSession(session);
        _ = _svc.Client.SendTetrisRace(_activeTetrisChallengeFromCode,
            new TetrisRacePayload { Sub = "accept" });
        _svc.RaiseOpenNetplayTetris(session);
        _activeTetrisChallenge = null;
    }

    private void DrawTetrisPicker()
    {
        int dw = 340, dh = 180;
        int dx = (Raylib.GetRenderWidth() - dw) / 2;
        int dy = (Raylib.GetRenderHeight() - dh) / 2;
        Raylib.DrawRectangle(0, 0,
            Raylib.GetRenderWidth(), Raylib.GetRenderHeight(),
            new Color((byte)0, (byte)0, (byte)0, (byte)110));
        var panel = new Rectangle(dx, dy, dw, dh);
        RetroSkin.DrawRaised(panel);
        var bar = new Rectangle(dx + 2, dy + 2, dw - 4, RetroWidgets.TitleBarHeight);
        Raylib.DrawRectangleGradientH((int)bar.X, (int)bar.Y, (int)bar.Width, (int)bar.Height,
            RetroSkin.TitleActive, RetroSkin.TitleGradEnd);
        RetroSkin.DrawText("Challenge to Tetris",
            (int)bar.X + 4, (int)bar.Y + 1,
            RetroSkin.TitleText, RetroSkin.TitleFontSize);
        if (_tetrisPickerFriend != null)
        {
            RetroSkin.DrawText("vs " + _tetrisPickerFriend.Nickname,
                dx + 12, dy + 26, RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
        }

        RetroSkin.DrawText("Starting level (faster gravity = harder):",
            dx + 12, dy + 50, RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
        const int levels = 10;
        _tetrisPickerLevelRects = new Rectangle[levels];
        int cellW = (dw - 24) / levels - 2;
        int cellH = 22;
        for (int i = 0; i < levels; i++)
        {
            _tetrisPickerLevelRects[i] = new Rectangle(
                dx + 12 + i * (cellW + 2), dy + 70, cellW, cellH);
            bool selected = _tetrisPickerLevelIdx == i;
            if (selected) RetroSkin.DrawPressed(_tetrisPickerLevelRects[i]);
            else RetroSkin.DrawRaised(_tetrisPickerLevelRects[i]);
            string lbl = (i + 1).ToString();
            int tw = RetroSkin.MeasureText(lbl, RetroSkin.BodyFontSize - 2);
            RetroSkin.DrawText(lbl,
                (int)_tetrisPickerLevelRects[i].X
                + ((int)_tetrisPickerLevelRects[i].Width - tw) / 2,
                (int)_tetrisPickerLevelRects[i].Y + 3,
                RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
        }

        RetroSkin.DrawText(
            "7-bag piece sequence is shared via the challenge seed.",
            dx + 12, dy + 110, RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);
        RetroSkin.DrawText(
            "First to top out loses.",
            dx + 12, dy + 126, RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);

        _tetrisPickerCancelBtn = new Rectangle(
            dx + dw - 12 - 80 - 6 - 80, dy + dh - 30, 80, 22);
        _tetrisPickerSendBtn = new Rectangle(dx + dw - 12 - 80, dy + dh - 30, 80, 22);
        RetroWidgets.ButtonVisual(_tetrisPickerCancelBtn, "Cancel", false);
        RetroWidgets.ButtonVisual(_tetrisPickerSendBtn, "Send →", false);
    }

    private void DrawTetrisChallengeModal()
    {
        var p = _activeTetrisChallenge!;
        int dw = 340, dh = 160;
        int dx = (Raylib.GetRenderWidth() - dw) / 2;
        int dy = (Raylib.GetRenderHeight() - dh) / 2;
        Raylib.DrawRectangle(0, 0,
            Raylib.GetRenderWidth(), Raylib.GetRenderHeight(),
            new Color((byte)0, (byte)0, (byte)0, (byte)110));
        var panel = new Rectangle(dx, dy, dw, dh);
        RetroSkin.DrawRaised(panel);
        var bar = new Rectangle(dx + 2, dy + 2, dw - 4, RetroWidgets.TitleBarHeight);
        Raylib.DrawRectangleGradientH((int)bar.X, (int)bar.Y, (int)bar.Width, (int)bar.Height,
            new Color((byte)200, (byte)156, (byte)20, (byte)255),
            new Color((byte)244, (byte)200, (byte)80, (byte)255));
        RetroSkin.DrawText("▦ Incoming Tetris Race",
            (int)bar.X + 4, (int)bar.Y + 1,
            new Color((byte)40, (byte)24, (byte)8, (byte)255),
            RetroSkin.TitleFontSize);

        RetroSkin.DrawText(
            $"{_activeTetrisChallengeFromName} wants to play 1v1 Tetris:",
            dx + 12, dy + 30, RetroSkin.BodyText, RetroSkin.BodyFontSize - 1);
        RetroSkin.DrawText($"Starting level: {p.StartingLevel}",
            dx + 12, dy + 56, RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);
        RetroSkin.DrawText("Same piece sequence on both sides.",
            dx + 12, dy + 76, RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);
        RetroSkin.DrawText("Line clears send garbage to the opponent.",
            dx + 12, dy + 92, RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);

        _tetrisChDeclineBtn = new Rectangle(dx + dw - 12 - 80 - 6 - 80, dy + dh - 30, 80, 22);
        _tetrisChAcceptBtn = new Rectangle(dx + dw - 12 - 80, dy + dh - 30, 80, 22);
        RetroWidgets.ButtonVisual(_tetrisChDeclineBtn, "Decline", false);
        RetroWidgets.ButtonVisual(_tetrisChAcceptBtn, "Race!", false);
    }

    private void UpdateHeartsPicker(Vector2 mouse, bool leftPressed)
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape) && !_heartsInvitesSent)
        {
            _heartsPickerOpen = false;
            return;
        }
        if (!leftPressed) return;

        // Inner "pick a friend" dropdown overlay — when open it
        // intercepts every click outside the row list to close.
        if (_heartsAddSlot >= 0)
        {
            for (int i = 0; i < _heartsDropdownRowRects.Length; i++)
            {
                if (RetroSkin.PointInRect(mouse, _heartsDropdownRowRects[i]))
                {
                    _heartsInvites[_heartsAddSlot] = _heartsDropdownChoices[i];
                    _heartsAddSlot = -1;
                    return;
                }
            }
            _heartsAddSlot = -1;     // click outside closes the dropdown
            return;
        }

        // Picker buttons.
        if (RetroSkin.PointInRect(mouse, _heartsCancelBtn))
        {
            _heartsPickerOpen = false; return;
        }
        if (!_heartsInvitesSent
            && RetroSkin.PointInRect(mouse, _heartsDiffCycleBtn))
        {
            _heartsDifficultyIdx = (_heartsDifficultyIdx + 1) % HeartsDifficulties.Length;
            return;
        }
        // Slot click → open the inner "pick a friend" dropdown if not
        // yet sent; ignored after sending (composition is fixed).
        if (!_heartsInvitesSent)
        {
            for (int i = 0; i < _heartsSlotRects.Length; i++)
            {
                if (RetroSkin.PointInRect(mouse, _heartsSlotRects[i]))
                {
                    _heartsAddSlot = i;
                    BuildHeartsDropdownChoices();
                    return;
                }
            }
        }

        if (!_heartsInvitesSent
            && RetroSkin.PointInRect(mouse, _heartsSendBtn))
        {
            // At least one invitee required — empty slots become AI
            // at Start time, but the picker enforces ≥1 human invite
            // since a 0-human game is just the solo Hearts activity.
            if (_heartsInvites.Any(f => f != null)) SendHeartsInvites();
            return;
        }
        if (_heartsInvitesSent
            && RetroSkin.PointInRect(mouse, _heartsStartBtn))
        {
            StartHeartsMatch();
            return;
        }
    }

    private void BuildHeartsDropdownChoices()
    {
        _heartsDropdownChoices = _svc.Friends.Friends
            .Where(f =>
            {
                if (_heartsInvites.Contains(f)) return false;
                var s = f.LastStatus;
                bool stale = DateTime.UtcNow - f.LastSeenUtc
                    > NetConfig.PresenceStaleAfter;
                return !stale && s != BuddyStatus.Offline;
            })
            .OrderBy(f => f.Nickname)
            .ToList();
    }

    private void UpdateHeartsChallengeModal(Vector2 mouse, bool leftPressed)
    {
        if (_activeHeartsChallenge == null) return;
        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        { DeclineHearts(); return; }
        if (leftPressed && RetroSkin.PointInRect(mouse, _heartsChDeclineBtn))
        { DeclineHearts(); return; }
        if (leftPressed && RetroSkin.PointInRect(mouse, _heartsChAcceptBtn))
        { AcceptHearts(); return; }
    }

    private void DeclineHearts()
    {
        if (_activeHeartsChallenge == null) return;
        _ = _svc.Client.SendHearts(_activeHeartsChallengeFromCode,
            new HeartsPayload { Sub = "decline" });
        _activeHeartsChallenge = null;
    }

    private void AcceptHearts()
    {
        if (_activeHeartsChallenge == null) return;
        // Just ship the accept; the recipient waits for the host's
        // start_match envelope (which delivers the finalised seat
        // composition) before opening the activity. _heartsAcceptedChallenge
        // keeps the proposed composition around so an early start_match
        // doesn't catch us empty-handed if seat_update broadcasts
        // arrive in between.
        _heartsAcceptedChallenge = _activeHeartsChallenge;
        _ = _svc.Client.SendHearts(_activeHeartsChallengeFromCode,
            new HeartsPayload { Sub = "accept" });
        // Modal closes; the buddy widget shows "Waiting for host
        // to start..." in the awaiting-line as a hint.
        _challengeAwaitingLine = "Hearts: waiting for host to start…";
        _challengeAwaitingUntil = DateTime.UtcNow.AddSeconds(180);
        _activeHeartsChallenge = null;
    }

    private void DrawHeartsPicker()
    {
        int dw = 420, dh = 280;
        int dx = (Raylib.GetRenderWidth() - dw) / 2;
        int dy = (Raylib.GetRenderHeight() - dh) / 2;
        Raylib.DrawRectangle(0, 0,
            Raylib.GetRenderWidth(), Raylib.GetRenderHeight(),
            new Color((byte)0, (byte)0, (byte)0, (byte)110));
        var panel = new Rectangle(dx, dy, dw, dh);
        RetroSkin.DrawRaised(panel);
        var bar = new Rectangle(dx + 2, dy + 2, dw - 4, RetroWidgets.TitleBarHeight);
        Raylib.DrawRectangleGradientH((int)bar.X, (int)bar.Y, (int)bar.Width, (int)bar.Height,
            RetroSkin.TitleActive, RetroSkin.TitleGradEnd);
        RetroSkin.DrawText("♥ Challenge to Hearts",
            (int)bar.X + 4, (int)bar.Y + 1,
            RetroSkin.TitleText, RetroSkin.TitleFontSize);

        RetroSkin.DrawText(
            _heartsInvitesSent
                ? "Waiting for friends to accept. Start when ready."
                : "Invite up to 3 friends. Empty seats become AI.",
            dx + 12, dy + 26, RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);

        // Three invite slots.
        _heartsSlotRects = new Rectangle[3];
        for (int i = 0; i < 3; i++)
        {
            var rect = new Rectangle(dx + 12, dy + 48 + i * 28, dw - 24, 24);
            _heartsSlotRects[i] = rect;
            var fill = _heartsInvitesSent ? RetroSkin.Face : RetroSkin.SunkenBg;
            RetroSkin.DrawSunken(rect, fill);
            string label;
            string statusSuffix = "";
            var inv = _heartsInvites[i];
            if (inv == null) label = $"Seat {i + 2}: (AI computer)";
            else
            {
                label = $"Seat {i + 2}: {inv.Nickname}";
                if (_heartsInvitesSent)
                {
                    var st = _heartsAcceptStatus.TryGetValue(inv.Code, out var s) ? s : null;
                    statusSuffix = st switch
                    {
                        true => "  ✓ accepted",
                        false => "  ✗ declined",
                        _ => "  …waiting",
                    };
                }
            }
            var col = inv == null ? RetroSkin.DisabledText : RetroSkin.BodyText;
            RetroSkin.DrawText(label + statusSuffix,
                (int)rect.X + 6, (int)rect.Y + 5,
                col, RetroSkin.BodyFontSize - 2);
        }

        // AI difficulty pill.
        _heartsDiffCycleBtn = new Rectangle(dx + 12, dy + 48 + 3 * 28 + 4, dw - 24, 22);
        if (_heartsInvitesSent) RetroSkin.DrawSunken(_heartsDiffCycleBtn, RetroSkin.Face);
        else RetroSkin.DrawRaised(_heartsDiffCycleBtn);
        string diffLabel = $"AI difficulty: {HeartsDifficulties[_heartsDifficultyIdx]}"
            + (_heartsInvitesSent ? "" : "   (click to cycle)");
        RetroSkin.DrawText(diffLabel,
            (int)_heartsDiffCycleBtn.X + 6, (int)_heartsDiffCycleBtn.Y + 4,
            _heartsInvitesSent ? RetroSkin.DisabledText : RetroSkin.BodyText,
            RetroSkin.BodyFontSize - 2);

        // Bottom button row.
        _heartsCancelBtn = new Rectangle(dx + 12, dy + dh - 30, 80, 22);
        if (_heartsInvitesSent)
        {
            _heartsStartBtn = new Rectangle(dx + dw - 12 - 100, dy + dh - 30, 100, 22);
            bool canStart = _heartsAcceptStatus.Values.Any(v => v == true);
            if (canStart) RetroWidgets.ButtonVisual(_heartsStartBtn, "Start match", false);
            else RetroSkin.DrawSunken(_heartsStartBtn, RetroSkin.Face);
            if (!canStart)
            {
                int tw = RetroSkin.MeasureText("Start match", RetroSkin.BodyFontSize - 1);
                RetroSkin.DrawText("Start match",
                    (int)_heartsStartBtn.X + ((int)_heartsStartBtn.Width - tw) / 2,
                    (int)_heartsStartBtn.Y + 4,
                    RetroSkin.DisabledText, RetroSkin.BodyFontSize - 1);
            }
        }
        else
        {
            _heartsSendBtn = new Rectangle(dx + dw - 12 - 100, dy + dh - 30, 100, 22);
            bool canSend = _heartsInvites.Any(f => f != null);
            if (canSend) RetroWidgets.ButtonVisual(_heartsSendBtn, "Send invites", false);
            else RetroSkin.DrawSunken(_heartsSendBtn, RetroSkin.Face);
            if (!canSend)
            {
                int tw = RetroSkin.MeasureText("Send invites", RetroSkin.BodyFontSize - 1);
                RetroSkin.DrawText("Send invites",
                    (int)_heartsSendBtn.X + ((int)_heartsSendBtn.Width - tw) / 2,
                    (int)_heartsSendBtn.Y + 4,
                    RetroSkin.DisabledText, RetroSkin.BodyFontSize - 1);
            }
        }
        RetroWidgets.ButtonVisual(_heartsCancelBtn, "Cancel", false);

        if (_heartsAddSlot >= 0) DrawHeartsDropdown(dx, dy);
    }

    private void DrawHeartsDropdown(int pickerX, int pickerY)
    {
        // Anchor under the selected slot row.
        var slot = _heartsSlotRects[_heartsAddSlot];
        int dropW = (int)slot.Width;
        int rowH = 18;
        int dropH = Math.Min(160, 4 + _heartsDropdownChoices.Count * rowH + 4);
        if (_heartsDropdownChoices.Count == 0) dropH = 28;
        var dropRect = new Rectangle(slot.X, slot.Y + slot.Height + 2, dropW, dropH);
        RetroSkin.DrawRaised(dropRect);
        if (_heartsDropdownChoices.Count == 0)
        {
            RetroSkin.DrawText("(no friends online)",
                (int)dropRect.X + 6, (int)dropRect.Y + 7,
                RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);
            _heartsDropdownRowRects = Array.Empty<Rectangle>();
            return;
        }
        _heartsDropdownRowRects = new Rectangle[_heartsDropdownChoices.Count];
        for (int i = 0; i < _heartsDropdownChoices.Count; i++)
        {
            var rect = new Rectangle(dropRect.X + 2, dropRect.Y + 4 + i * rowH,
                dropRect.Width - 4, rowH - 2);
            _heartsDropdownRowRects[i] = rect;
            RetroSkin.DrawText(_heartsDropdownChoices[i].Nickname,
                (int)rect.X + 4, (int)rect.Y + 1,
                RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
        }
    }

    private void DrawHeartsChallengeModal()
    {
        var p = _activeHeartsChallenge!;
        int dw = 380, dh = 220;
        int dx = (Raylib.GetRenderWidth() - dw) / 2;
        int dy = (Raylib.GetRenderHeight() - dh) / 2;
        Raylib.DrawRectangle(0, 0,
            Raylib.GetRenderWidth(), Raylib.GetRenderHeight(),
            new Color((byte)0, (byte)0, (byte)0, (byte)110));
        var panel = new Rectangle(dx, dy, dw, dh);
        RetroSkin.DrawRaised(panel);
        var bar = new Rectangle(dx + 2, dy + 2, dw - 4, RetroWidgets.TitleBarHeight);
        Raylib.DrawRectangleGradientH((int)bar.X, (int)bar.Y, (int)bar.Width, (int)bar.Height,
            new Color((byte)200, (byte)156, (byte)20, (byte)255),
            new Color((byte)244, (byte)200, (byte)80, (byte)255));
        RetroSkin.DrawText("♥ Incoming Hearts game",
            (int)bar.X + 4, (int)bar.Y + 1,
            new Color((byte)40, (byte)24, (byte)8, (byte)255),
            RetroSkin.TitleFontSize);

        RetroSkin.DrawText(
            $"{_activeHeartsChallengeFromName} invited you to play Hearts:",
            dx + 12, dy + 30, RetroSkin.BodyText, RetroSkin.BodyFontSize - 1);
        RetroSkin.DrawText($"AI difficulty: {p.Difficulty}",
            dx + 12, dy + 50, RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);

        // Show the proposed 4-seat composition.
        if (p.Seats != null)
        {
            int rowY = 0;
            foreach (var seat in p.Seats)
            {
                string label = seat.Kind switch
                {
                    "host" => $"  • {seat.Name} (host)",
                    "pending" => seat.FriendCode == _svc.Identity.Code
                        ? $"  • {seat.Name} (you)"
                        : $"  • {seat.Name}",
                    "ai" => $"  • {seat.Name} (AI)",
                    _ => $"  • {seat.Name}",
                };
                RetroSkin.DrawText(label,
                    dx + 12, dy + 72 + rowY,
                    RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
                rowY += 16;
            }
        }

        _heartsChDeclineBtn = new Rectangle(dx + dw - 12 - 80 - 6 - 80, dy + dh - 30, 80, 22);
        _heartsChAcceptBtn = new Rectangle(dx + dw - 12 - 80, dy + dh - 30, 80, 22);
        RetroWidgets.ButtonVisual(_heartsChDeclineBtn, "Decline", false);
        RetroWidgets.ButtonVisual(_heartsChAcceptBtn, "Accept", false);
    }

    private static void DrawX(Rectangle r)
    {
        int cx = (int)r.X + 7;
        int cy = (int)r.Y + 6;
        for (int i = -3; i <= 3; i++)
        {
            Raylib.DrawPixel(cx + i, cy + i, RetroSkin.BodyText);
            Raylib.DrawPixel(cx + i, cy - i, RetroSkin.BodyText);
        }
    }
}
