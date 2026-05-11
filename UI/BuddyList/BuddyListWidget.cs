using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Net.Buddies;
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
    private readonly List<(Friend Friend, Rectangle Row, Rectangle ChallengeBtn)> _rows = new();
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

        // Modals first — exclusive focus.
        if (_golfPickerOpen) { UpdateGolfPicker(mouse, leftPressed); return true; }
        if (_activeGolfChallenge != null) { UpdateGolfChallengeModal(mouse, leftPressed); return true; }
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

        // Click on a friend row → check for the Challenge button
        // (the right-end square next to each name). Opens the
        // region/difficulty picker; the picker handles the actual
        // send.
        if (leftPressed)
        {
            foreach (var r in _rows)
            {
                if (RetroSkin.PointInRect(local, r.ChallengeBtn))
                {
                    _golfPickerOpen = true;
                    _golfPickerFriend = r.Friend;
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
        if (_golfPickerOpen) DrawGolfPicker();
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

        // Challenge button on the right.
        var ch = new Rectangle(listRect.X + listRect.Width - 26, ry + 4, 22, rowH - 8);
        // Disable look if friend is offline.
        bool canChallenge = status == BuddyStatus.Online
            || status == BuddyStatus.Idle
            || status == BuddyStatus.Away;
        RetroWidgets.ButtonVisual(ch, "▶", !canChallenge);

        _rows.Add((f,
            new Rectangle(listRect.X - Position.X, ry - Position.Y, listRect.Width, rowH),
            new Rectangle(ch.X - Position.X, ch.Y - Position.Y, ch.Width, ch.Height)));
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
