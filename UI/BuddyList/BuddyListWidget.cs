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
        // (the right-end square next to each name). Plain row click
        // is a no-op for now — future netplay UI hangs off here.
        if (leftPressed)
        {
            foreach (var r in _rows)
            {
                if (RetroSkin.PointInRect(local, r.ChallengeBtn))
                {
                    // Stub: only support Ohio Golf challenges in v1.
                    _svc.Challenge(r.Friend, "golf");
                    _challengeAwaitingLine = $"Sent challenge to {r.Friend.Nickname}…";
                    _challengeAwaitingUntil = DateTime.UtcNow.AddSeconds(8);
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

        // Modal overlays — always last.
        if (_addOpen) DrawAddDialog();
        if (_activeRequest != null) DrawRequestModal();
        if (_activeChallenge != null) DrawChallengeModal();
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
