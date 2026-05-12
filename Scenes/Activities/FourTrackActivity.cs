using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Tascam-style 4-track cassette recorder emulator. One unified
/// tape with 4 mono PCM tracks, animated reels at the top, big
/// chunky transport buttons in the middle, per-track strips +
/// master section across the bottom. Recording / mixing / varispeed
/// / bounce / save+load / polish all land in follow-up commits per
/// the build plan; this scaffold ships the chrome + transport state
/// machine + animated reels + tape counter + the resizable window
/// pattern copied from RetroChessPuzzlesActivity.
///
/// MVP scope this commit: empty 4 tracks, transport state machine
/// (PLAY / REC / STOP / REW / FF / PAUSE / ZERO + REC+PLAY combo),
/// playhead advancing in time with the transport, animated cassette
/// reels rotating at tape speed, 4-digit LCD counter ticking with
/// the playhead, dithered cassette body. Per-track strips render as
/// labeled placeholders; controls inert. Audio mixing + recording
/// land next commit.
/// </summary>
public sealed class FourTrackActivity : IActivity
{
    // ── Layout ──────────────────────────────────────────────────────
    private const int FrameInset = 3;
    private const int Margin = 12;
    private const int CassetteH = 180;     // top section: animated reels
    private const int TransportH = 70;     // middle row: big buttons + LCD
    private const int TrackStripH = 200;   // bottom section: 4 track columns + master
    private const int ResizeGripSize = 14;

    // Default + bounds. Default lands on a 760x520 footprint that
    // fits the front-panel layout cleanly; min keeps the four track
    // strips usable; max caps at a roomy desktop-fill size.
    private static readonly Vector2 PanelDefault = new(760, 520);
    private static readonly Vector2 PanelMin = new(640, 460);
    private static readonly Vector2 PanelMax = new(1280, 880);

    private Vector2 _panelSize = PanelDefault;
    public Vector2 PanelSize => _panelSize;
    public bool IsFinished { get; private set; }

    // Resize-grip state — mirrors RetroChessPuzzlesActivity's pattern.
    private bool _resizing;
    private Vector2 _resizeStartMouse;
    private Vector2 _resizeStartSize;

    // ── Tape model ──────────────────────────────────────────────────
    private const int Tracks = 4;
    private const int SampleRate = 44100;
    /// <summary>Default tape length in seconds. The Tape menu in a
    /// future commit will let the user pick 60 / 90; for the
    /// scaffold we just use 60.</summary>
    private const float TapeLengthSeconds = 60f;
    /// <summary>4 mono PCM buffers — one per track. Allocated to
    /// the full tape length so the playhead can scrub freely; all
    /// zeros until a recording lands here in the next commit.</summary>
    private float[][] _tracks = new float[Tracks][];

    /// <summary>Current playhead in seconds from the start of tape.
    /// Mutated by the transport state machine + the tape counter
    /// reads it for the LCD.</summary>
    private float _playheadSec;

    /// <summary>Live tape length — starts at TapeLengthSeconds, can
    /// be extended later when the Tape menu's "extend" lands.</summary>
    private float _tapeLengthSec = TapeLengthSeconds;

    private enum Transport { Stopped, Playing, Recording, Paused, Rewinding, FastForwarding }
    private Transport _transport = Transport.Stopped;

    /// <summary>Set when the user has armed REC by tapping the REC
    /// button without PLAY. The button blinks; if PLAY is then
    /// pressed, recording starts. Real Tascam-style behavior.</summary>
    private bool _recArmed;

    /// <summary>Per-track REC-arm. A track must be armed AND the
    /// transport must be Recording for that track to actually
    /// receive samples. (Recording itself lands next commit.)</summary>
    private bool[] _trackRecArmed = new bool[Tracks];

    /// <summary>Cosmetic reel rotation (radians). Advances at tape
    /// speed × varispeed; reads the same _playheadSec the LCD
    /// reads so they stay in lockstep.</summary>
    private float _reelAngle;

    // ── Transport buttons ───────────────────────────────────────────
    private enum Btn { Rew, Play, FF, Stop, Pause, Rec, Zero }
    private static readonly string[] BtnLabels = { "◀◀", "▶", "▶▶", "■", "⏸", "●", "↺" };
    private readonly Rectangle[] _btnRects = new Rectangle[Enum.GetValues(typeof(Btn)).Length];
    /// <summary>Press-down visual state per button. Set during the
    /// frame the button is depressed; the button renders shifted
    /// down 1 px. Cleared on next frame.</summary>
    private readonly bool[] _btnPressed = new bool[Enum.GetValues(typeof(Btn)).Length];
    /// <summary>Hold-tracking per button — needed for the REC+PLAY
    /// combo (real decks required holding REC, then pressing PLAY
    /// to start recording). We honor the combo if both buttons
    /// are clicked in the same frame; the click handler also
    /// covers the legacy "tap REC, then tap PLAY" flow.</summary>
    private bool _recPressedThisFrame;

    // ── Help overlay ────────────────────────────────────────────────
    private readonly RetroHelp _help = new()
    {
        Title = "4-Track — How to use",
        Lines = new[]
        {
            "Cassette-style 4-track recorder. One tape, four mono tracks,",
            "one playhead — they all roll together like a real Tascam.",
            "Transport: ◀◀ rewind, ▶ play, ▶▶ fast-fwd, ■ stop, ⏸ pause,",
            "● rec, ↺ zero (rewind to start).",
            "Press REC then PLAY (or REC + PLAY together) to start recording",
            "on whichever tracks are armed.",
            "",
            "Scaffold commit — recording / mixing / varispeed / save+load",
            "land in follow-up commits.",
        },
    };

    // ── Lifecycle ───────────────────────────────────────────────────
    public void Load()
    {
        for (int t = 0; t < Tracks; t++)
            _tracks[t] = new float[(int)(_tapeLengthSec * SampleRate)];
        LoadWindowSize();
    }

    public void Close()
    {
        SaveWindowSize();
    }

    // ── Window-size persistence ─────────────────────────────────────
    private static string WindowSizePath
        => Path.Combine(MouseHouse.Core.SaveManager.SaveDirectory,
            "fourtrack_window_size.txt");

    private void LoadWindowSize()
    {
        try
        {
            if (!File.Exists(WindowSizePath)) return;
            var parts = File.ReadAllText(WindowSizePath).Trim().Split('x');
            if (parts.Length != 2) return;
            if (!int.TryParse(parts[0], out int w)) return;
            if (!int.TryParse(parts[1], out int h)) return;
            _panelSize = new Vector2(
                Math.Clamp(w, (int)PanelMin.X, (int)PanelMax.X),
                Math.Clamp(h, (int)PanelMin.Y, (int)PanelMax.Y));
        }
        catch { /* fall back to PanelDefault */ }
    }

    private void SaveWindowSize()
    {
        try
        {
            Directory.CreateDirectory(MouseHouse.Core.SaveManager.SaveDirectory);
            File.WriteAllText(WindowSizePath,
                $"{(int)_panelSize.X}x{(int)_panelSize.Y}");
        }
        catch { /* best-effort */ }
    }

    // ── Resize grip ─────────────────────────────────────────────────
    private Rectangle ResizeGripLocal()
        => new(_panelSize.X - ResizeGripSize - FrameInset,
               _panelSize.Y - ResizeGripSize - FrameInset,
               ResizeGripSize, ResizeGripSize);

    private bool HandleResizeGrip(Vector2 local, bool leftPressed, bool leftReleased)
    {
        var grip = ResizeGripLocal();
        if (!_resizing && leftPressed && RetroSkin.PointInRect(local, grip))
        {
            _resizing = true;
            _resizeStartMouse = local;
            _resizeStartSize = _panelSize;
            return true;
        }
        if (_resizing)
        {
            var delta = local - _resizeStartMouse;
            float w = Math.Clamp(_resizeStartSize.X + delta.X, PanelMin.X, PanelMax.X);
            float h = Math.Clamp(_resizeStartSize.Y + delta.Y, PanelMin.Y, PanelMax.Y);
            _panelSize = new Vector2((int)w, (int)h);
            if (leftReleased)
            {
                _resizing = false;
                SaveWindowSize();
            }
            return true;
        }
        return false;
    }

    // ── Update ──────────────────────────────────────────────────────
    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;

        // Reset per-frame button-press latches.
        for (int i = 0; i < _btnPressed.Length; i++) _btnPressed[i] = false;
        _recPressedThisFrame = false;

        // Resize grip wins over chrome / transport input — mid-drag
        // it's the only interaction that matters.
        if (HandleResizeGrip(local, leftPressed, leftReleased)) return;

        // Title bar close.
        var titleBar = new Rectangle(FrameInset, FrameInset,
            _panelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        { IsFinished = true; return; }

        // Menu bar.
        var menuBar = new Rectangle(FrameInset, FrameInset + RetroWidgets.TitleBarHeight,
            _panelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        var menuItems = new[] { "File", "Edit", "Tape", "Help" };
        int m = RetroWidgets.MenuBarHitTest(menuBar, menuItems, local, leftPressed);
        if (m == 3) { _help.Visible = !_help.Visible; return; }
        if (_help.HandleInput(local, leftPressed, _panelSize)) return;

        // Tick the playhead by the current transport state.
        TickTransport(delta);

        // Hit-test transport buttons. Layout pass also fills _btnRects;
        // we rely on the previous frame's rects which is fine on a
        // resizable window because the layout is deterministic from
        // _panelSize and recomputed every render.
        if (leftPressed)
        {
            for (int i = 0; i < _btnRects.Length; i++)
            {
                if (!RetroSkin.PointInRect(local, _btnRects[i])) continue;
                _btnPressed[i] = true;
                if ((Btn)i == Btn.Rec) _recPressedThisFrame = true;
                OnButton((Btn)i);
                break;
            }
        }
    }

    private void TickTransport(float delta)
    {
        // Reels advance proportionally to playhead speed. Speed scale:
        // 1 second of play = ~6 radians of reel rotation (cosmetic).
        float speed = _transport switch
        {
            Transport.Playing => 1f,
            Transport.Recording => 1f,
            Transport.Rewinding => -3.5f,
            Transport.FastForwarding => 3.5f,
            _ => 0f,
        };
        if (speed != 0f)
        {
            _playheadSec += delta * speed;
            _reelAngle += delta * speed * 6f;
            if (_playheadSec < 0)
            {
                _playheadSec = 0;
                if (_transport == Transport.Rewinding) _transport = Transport.Stopped;
            }
            if (_playheadSec > _tapeLengthSec)
            {
                _playheadSec = _tapeLengthSec;
                if (_transport == Transport.FastForwarding
                    || _transport == Transport.Playing
                    || _transport == Transport.Recording)
                {
                    _transport = Transport.Stopped;
                }
            }
        }
    }

    private void OnButton(Btn b)
    {
        switch (b)
        {
            case Btn.Play:
                // PLAY alone enters Playing; PLAY pressed in the same
                // frame as REC (or after REC was tapped to arm) starts
                // Recording.
                if (_recArmed || _recPressedThisFrame) _transport = Transport.Recording;
                else _transport = Transport.Playing;
                _recArmed = false;
                break;
            case Btn.Rec:
                // First tap arms REC (button blinks, transport stays
                // wherever it was). Real Tascam-style: tape doesn't
                // move until PLAY follows. Tapping REC again disarms.
                if (_transport == Transport.Recording)
                {
                    // Punch out — drop back to Playing without
                    // stopping the tape.
                    _transport = Transport.Playing;
                    _recArmed = false;
                }
                else
                {
                    _recArmed = !_recArmed;
                }
                break;
            case Btn.Stop:
                _transport = Transport.Stopped;
                _recArmed = false;
                break;
            case Btn.Pause:
                if (_transport == Transport.Playing
                    || _transport == Transport.Recording)
                    _transport = Transport.Paused;
                else if (_transport == Transport.Paused)
                    _transport = Transport.Playing;
                break;
            case Btn.Rew:
                _transport = Transport.Rewinding;
                _recArmed = false;
                break;
            case Btn.FF:
                _transport = Transport.FastForwarding;
                _recArmed = false;
                break;
            case Btn.Zero:
                _playheadSec = 0;
                _transport = Transport.Stopped;
                _recArmed = false;
                break;
        }
    }

    // ── Render ──────────────────────────────────────────────────────
    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, _panelSize.X, _panelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            _panelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "4-Track Recorder", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            _panelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar,
            new[] { "File", "Edit", "Tape", "Help" }, -1);

        // Body — dark grey "front panel" plastic, dithered for the
        // Win9x look.
        float bodyTop = panelOffset.Y + FrameInset
            + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight;
        float bodyH = _panelSize.Y - bodyTop + panelOffset.Y - FrameInset
            - RetroWidgets.StatusBarHeight;
        var body = new Rectangle(panelOffset.X + FrameInset, bodyTop,
            _panelSize.X - 2 * FrameInset, bodyH);
        DrawDitheredVerticalGradient(body,
            new Color((byte)64, (byte)68, (byte)80, (byte)255),
            new Color((byte)40, (byte)44, (byte)56, (byte)255));

        // Three sections, vertically: cassette deck on top, transport
        // row in the middle, track strips at the bottom. Heights
        // scale with the panel: the track strip takes the leftover
        // space after the fixed cassette + transport heights.
        float sectionY = bodyTop + 6;
        var cassetteRect = new Rectangle(body.X + 8, sectionY,
            body.Width - 16, CassetteH);
        DrawCassetteDeck(cassetteRect);

        sectionY += CassetteH + 8;
        var transportRect = new Rectangle(body.X + 8, sectionY,
            body.Width - 16, TransportH);
        DrawTransportRow(transportRect);

        sectionY += TransportH + 8;
        float remaining = body.Y + body.Height - sectionY - 8;
        var stripsRect = new Rectangle(body.X + 8, sectionY,
            body.Width - 16, Math.Max(80, remaining));
        DrawTrackStrips(stripsRect);

        // Status bar at the bottom.
        var statusBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + _panelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            _panelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(statusBar, StatusLeft(), StatusRight());

        DrawResizeGrip(panelOffset);
        _help.Draw(panelOffset, _panelSize);
    }

    // ── Cassette deck visual ────────────────────────────────────────
    private void DrawCassetteDeck(Rectangle r)
    {
        // Cassette body — beige/cream rounded rectangle with the
        // Bayer-dithered gradient panels every Win9x audio app
        // had. Two reel windows show the spinning hubs.
        DrawDitheredVerticalGradient(r,
            new Color((byte)232, (byte)220, (byte)180, (byte)255),
            new Color((byte)200, (byte)188, (byte)148, (byte)255));
        Raylib.DrawRectangleLines((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height,
            new Color((byte)40, (byte)32, (byte)20, (byte)255));

        // Centre label strip running across the cassette.
        var label = new Rectangle(r.X + 12, r.Y + r.Height * 0.35f,
            r.Width - 24, r.Height * 0.30f);
        Raylib.DrawRectangleRec(label, new Color((byte)252, (byte)244, (byte)220, (byte)255));
        Raylib.DrawRectangleLines((int)label.X, (int)label.Y,
            (int)label.Width, (int)label.Height,
            new Color((byte)80, (byte)64, (byte)40, (byte)200));
        // Tape-counter style label text. Rendered in the cassette's
        // centre label so the cassette reads as a real piece of
        // physical media, not just abstract chrome.
        string brand = "C-" + (int)_tapeLengthSec + "  ·  4-TRACK";
        int brandW = RetroSkin.MeasureText(brand, RetroSkin.BodyFontSize);
        RetroSkin.DrawText(brand,
            (int)(label.X + (label.Width - brandW) / 2),
            (int)(label.Y + (label.Height - RetroSkin.BodyFontSize) / 2),
            new Color((byte)64, (byte)40, (byte)24, (byte)255), RetroSkin.BodyFontSize);

        // Two reel windows — supply (left) + take-up (right).
        float reelR = MathF.Min(r.Width * 0.18f, r.Height * 0.30f);
        float reelY = r.Y + r.Height * 0.20f;
        float reelLeftX = r.X + r.Width * 0.22f;
        float reelRightX = r.X + r.Width * 0.78f;
        float t = _tapeLengthSec > 0 ? _playheadSec / _tapeLengthSec : 0f;
        // "Fullness" — supply starts full + drains; take-up starts
        // empty + fills. Visualised as the inner radius of the tape
        // wrapping each hub.
        float supplyFill = 1f - t;
        float takeupFill = t;
        DrawReel(reelLeftX, reelY, reelR, _reelAngle, supplyFill);
        DrawReel(reelRightX, reelY, reelR, _reelAngle, takeupFill);

        // LCD counter strip just under the cassette label.
        int lcdY = (int)(label.Y + label.Height + 8);
        int lcdW = 130;
        int lcdH = 26;
        int lcdX = (int)(r.X + (r.Width - lcdW) / 2);
        DrawLcdCounter(new Rectangle(lcdX, lcdY, lcdW, lcdH));
    }

    /// <summary>Animated reel: outer ring + inner hub + 6 spokes
    /// rotating with the playhead. The "tape wrap" radius around
    /// the hub indicates fullness — full supply reel has wrap
    /// extending out to ~80% of the window radius; empty has only
    /// the hub showing.</summary>
    private void DrawReel(float cx, float cy, float windowR, float angle, float fullness)
    {
        // Reel window cutout (dark recess).
        Raylib.DrawCircle((int)cx, (int)cy, windowR + 2,
            new Color((byte)40, (byte)32, (byte)20, (byte)255));
        Raylib.DrawCircle((int)cx, (int)cy, windowR,
            new Color((byte)20, (byte)16, (byte)10, (byte)255));

        // Tape wrap — a darker disc around the hub whose radius
        // grows with `fullness`. Hub radius = 30% of window radius.
        float hubR = windowR * 0.30f;
        float wrapR = MathF.Max(hubR, hubR + (windowR - hubR) * 0.85f * fullness);
        Raylib.DrawCircle((int)cx, (int)cy, wrapR,
            new Color((byte)56, (byte)40, (byte)28, (byte)255));

        // Hub centre — beige plastic, with 6 spokes rotating with the
        // tape angle so the eye reads motion clearly.
        Raylib.DrawCircle((int)cx, (int)cy, hubR,
            new Color((byte)220, (byte)200, (byte)160, (byte)255));
        Raylib.DrawCircleLines((int)cx, (int)cy, hubR,
            new Color((byte)64, (byte)48, (byte)24, (byte)255));
        for (int i = 0; i < 6; i++)
        {
            float a = angle + i * MathF.PI / 3f;
            float ex = cx + MathF.Cos(a) * hubR * 0.85f;
            float ey = cy + MathF.Sin(a) * hubR * 0.85f;
            Raylib.DrawLineEx(new Vector2(cx, cy), new Vector2(ex, ey),
                2f, new Color((byte)64, (byte)48, (byte)24, (byte)255));
        }
        // Centre dot for the spindle.
        Raylib.DrawCircle((int)cx, (int)cy, 3f,
            new Color((byte)32, (byte)24, (byte)12, (byte)255));
    }

    private void DrawLcdCounter(Rectangle r)
    {
        // Sunken bezel + black LCD background.
        RetroSkin.DrawSunken(r, fill: new Color((byte)10, (byte)16, (byte)10, (byte)255));
        // Format: MM:SS:CC (mm:ss:centi). Compact, classic.
        int totalCs = (int)(_playheadSec * 100f);
        int mm = totalCs / 6000;
        int ss = (totalCs / 100) % 60;
        int cs = totalCs % 100;
        string text = $"{mm:D2}:{ss:D2}:{cs:D2}";
        // Use the radio's LCD font palette — soft green on black.
        var col = new Color((byte)80, (byte)240, (byte)80, (byte)255);
        // Tiny scanline tint to read as LCD.
        for (int y = (int)r.Y + 2; y < r.Y + r.Height - 2; y += 2)
            Raylib.DrawLine((int)r.X + 1, y, (int)(r.X + r.Width - 1), y,
                new Color((byte)0, (byte)32, (byte)0, (byte)40));
        int tw = RetroSkin.MeasureText(text, RetroSkin.BodyFontSize + 2);
        RetroSkin.DrawText(text,
            (int)(r.X + (r.Width - tw) / 2),
            (int)(r.Y + (r.Height - (RetroSkin.BodyFontSize + 2)) / 2),
            col, RetroSkin.BodyFontSize + 2);
    }

    // ── Transport row ───────────────────────────────────────────────
    private void DrawTransportRow(Rectangle r)
    {
        DrawDitheredVerticalGradient(r,
            new Color((byte)80, (byte)84, (byte)96, (byte)255),
            new Color((byte)56, (byte)60, (byte)72, (byte)255));
        Raylib.DrawRectangleLines((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height,
            new Color((byte)24, (byte)28, (byte)40, (byte)255));

        // Layout: 7 buttons spaced evenly across the row, ~50 px each.
        int btnCount = _btnRects.Length;
        int btnW = 52;
        int btnH = (int)(r.Height - 16);
        int gap = ((int)r.Width - btnCount * btnW) / (btnCount + 1);
        int x = (int)r.X + gap;
        int y = (int)(r.Y + (r.Height - btnH) / 2);
        for (int i = 0; i < btnCount; i++)
        {
            _btnRects[i] = new Rectangle(x, y, btnW, btnH);
            DrawTransportButton(_btnRects[i], (Btn)i);
            x += btnW + gap;
        }
    }

    private void DrawTransportButton(Rectangle r, Btn b)
    {
        bool pressed = _btnPressed[(int)b]
            || (b == Btn.Play && (_transport == Transport.Playing || _transport == Transport.Recording))
            || (b == Btn.Rec && _transport == Transport.Recording)
            || (b == Btn.Pause && _transport == Transport.Paused)
            || (b == Btn.Rew && _transport == Transport.Rewinding)
            || (b == Btn.FF && _transport == Transport.FastForwarding);

        // REC blinks when armed but not yet recording.
        bool blinkOn = b == Btn.Rec && _recArmed && !pressed
            && ((int)(Raylib.GetTime() * 2.5) % 2 == 0);

        var face = b switch
        {
            Btn.Rec when pressed || blinkOn => new Color((byte)220, (byte)40, (byte)40, (byte)255),
            Btn.Rec => new Color((byte)120, (byte)32, (byte)32, (byte)255),
            Btn.Play when pressed => new Color((byte)80, (byte)200, (byte)80, (byte)255),
            Btn.Play => new Color((byte)40, (byte)112, (byte)40, (byte)255),
            _ => new Color((byte)180, (byte)180, (byte)188, (byte)255),
        };

        // Shifted-down 2 px when pressed for the chunky-button feel.
        int yOff = pressed ? 2 : 0;
        var draw = new Rectangle(r.X, r.Y + yOff, r.Width, r.Height - yOff);
        // Drop shadow so the un-pressed state reads as a button
        // sticking up off the panel.
        if (!pressed)
        {
            Raylib.DrawRectangle((int)r.X + 1, (int)(r.Y + r.Height) - 1,
                (int)r.Width - 2, 2,
                new Color((byte)16, (byte)20, (byte)28, (byte)180));
        }
        Raylib.DrawRectangleRec(draw, face);
        Raylib.DrawRectangleLines((int)draw.X, (int)draw.Y,
            (int)draw.Width, (int)draw.Height,
            new Color((byte)16, (byte)20, (byte)28, (byte)255));
        // Top highlight bevel.
        Raylib.DrawRectangle((int)draw.X + 1, (int)draw.Y + 1, (int)draw.Width - 2, 2,
            new Color((byte)255, (byte)255, (byte)255, (byte)80));
        // Glyph centred.
        string label = BtnLabels[(int)b];
        int tw = RetroSkin.MeasureText(label, RetroSkin.BodyFontSize + 4);
        var labelCol = b == Btn.Rec
            ? new Color((byte)252, (byte)232, (byte)200, (byte)255)
            : new Color((byte)24, (byte)28, (byte)40, (byte)255);
        RetroSkin.DrawText(label,
            (int)(draw.X + (draw.Width - tw) / 2),
            (int)(draw.Y + (draw.Height - (RetroSkin.BodyFontSize + 4)) / 2),
            labelCol, RetroSkin.BodyFontSize + 4);
    }

    // ── Track strips ────────────────────────────────────────────────
    private void DrawTrackStrips(Rectangle r)
    {
        DrawDitheredVerticalGradient(r,
            new Color((byte)96, (byte)100, (byte)112, (byte)255),
            new Color((byte)64, (byte)68, (byte)80, (byte)255));
        Raylib.DrawRectangleLines((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height,
            new Color((byte)24, (byte)28, (byte)40, (byte)255));

        // 4 track columns + a small master section on the right.
        // Master takes ~110 px; tracks share the rest equally.
        const int masterW = 110;
        int tracksW = (int)r.Width - masterW - 12;
        int colW = tracksW / Tracks;
        int colH = (int)r.Height - 16;
        int colY = (int)r.Y + 8;
        for (int t = 0; t < Tracks; t++)
        {
            int colX = (int)r.X + 6 + t * colW;
            DrawTrackPlaceholder(new Rectangle(colX, colY, colW - 4, colH), t);
        }
        var masterRect = new Rectangle(r.X + r.Width - masterW - 6,
            colY, masterW, colH);
        DrawMasterPlaceholder(masterRect);
    }

    private void DrawTrackPlaceholder(Rectangle r, int trackIdx)
    {
        Raylib.DrawRectangleRec(r, new Color((byte)40, (byte)44, (byte)56, (byte)255));
        Raylib.DrawRectangleLines((int)r.X, (int)r.Y,
            (int)r.Width, (int)r.Height,
            new Color((byte)16, (byte)20, (byte)28, (byte)255));
        string label = $"TRK {trackIdx + 1}";
        int tw = RetroSkin.MeasureText(label, RetroSkin.BodyFontSize - 1);
        RetroSkin.DrawText(label,
            (int)(r.X + (r.Width - tw) / 2), (int)r.Y + 6,
            new Color((byte)200, (byte)200, (byte)200, (byte)255),
            RetroSkin.BodyFontSize - 1);
        RetroSkin.DrawText("(controls in next commit)",
            (int)r.X + 6, (int)(r.Y + r.Height - 18),
            new Color((byte)128, (byte)128, (byte)128, (byte)255),
            RetroSkin.BodyFontSize - 3);
    }

    private void DrawMasterPlaceholder(Rectangle r)
    {
        Raylib.DrawRectangleRec(r, new Color((byte)32, (byte)36, (byte)48, (byte)255));
        Raylib.DrawRectangleLines((int)r.X, (int)r.Y,
            (int)r.Width, (int)r.Height,
            new Color((byte)16, (byte)20, (byte)28, (byte)255));
        int tw = RetroSkin.MeasureText("MASTER", RetroSkin.BodyFontSize - 1);
        RetroSkin.DrawText("MASTER",
            (int)(r.X + (r.Width - tw) / 2), (int)r.Y + 6,
            new Color((byte)244, (byte)200, (byte)80, (byte)255),
            RetroSkin.BodyFontSize - 1);
    }

    // ── Status bar ──────────────────────────────────────────────────
    private string StatusLeft() => _transport switch
    {
        Transport.Playing => "▶ Playing",
        Transport.Recording => "● Recording",
        Transport.Paused => "⏸ Paused",
        Transport.Rewinding => "◀◀ Rewinding",
        Transport.FastForwarding => "▶▶ Fast-forwarding",
        _ => _recArmed ? "● REC armed — press PLAY" : "■ Stopped",
    };

    private string StatusRight()
    {
        int armedCount = _trackRecArmed.Count(b => b);
        return $"{armedCount}/{Tracks} armed   tape: {_tapeLengthSec:F0}s";
    }

    // ── Resize grip glyph ───────────────────────────────────────────
    private void DrawResizeGrip(Vector2 panelOffset)
    {
        var grip = ResizeGripLocal();
        int gx = (int)(grip.X + panelOffset.X);
        int gy = (int)(grip.Y + panelOffset.Y);
        for (int d = 2; d < ResizeGripSize; d += 4)
        {
            for (int t = 0; t < 2; t++)
            {
                Raylib.DrawLine(gx + ResizeGripSize - d - t, gy + ResizeGripSize - 2,
                                gx + ResizeGripSize - 2,    gy + ResizeGripSize - d - t,
                                t == 0 ? RetroSkin.DarkShadow : RetroSkin.Highlight);
            }
        }
    }

    // ── Bayer-dithered vertical gradient ────────────────────────────
    /// <summary>
    /// 8×8 Bayer dither on a vertical gradient between `top` and
    /// `bottom`. Same Win9x dither pattern WorldTeeClassic uses for
    /// the moon / globe; inlined here following the existing
    /// codebase convention (each activity defines its own Bayer
    /// matrix rather than sharing a helper).
    /// </summary>
    private static readonly int[,] Bayer8 = new int[8, 8]
    {
        {  0, 32,  8, 40,  2, 34, 10, 42 },
        { 48, 16, 56, 24, 50, 18, 58, 26 },
        { 12, 44,  4, 36, 14, 46,  6, 38 },
        { 60, 28, 52, 20, 62, 30, 54, 22 },
        {  3, 35, 11, 43,  1, 33,  9, 41 },
        { 51, 19, 59, 27, 49, 17, 57, 25 },
        { 15, 47,  7, 39, 13, 45,  5, 37 },
        { 63, 31, 55, 23, 61, 29, 53, 21 },
    };

    private static void DrawDitheredVerticalGradient(Rectangle r, Color top, Color bottom)
    {
        // Per-row interpolation; per-pixel dither against the Bayer
        // matrix to choose between the row's colour and the next
        // row's colour. Cheap and reads like a Win9x deep-color
        // gradient even on solid-color skins.
        int x0 = (int)r.X, y0 = (int)r.Y;
        int w = (int)r.Width, h = (int)r.Height;
        for (int dy = 0; dy < h; dy++)
        {
            float t = h <= 1 ? 0f : dy / (float)(h - 1);
            // Two reference colours either side of the row's nominal
            // gradient sample; Bayer threshold picks one per pixel.
            int r0 = (int)Math.Round(top.R + (bottom.R - top.R) * t);
            int g0 = (int)Math.Round(top.G + (bottom.G - top.G) * t);
            int b0 = (int)Math.Round(top.B + (bottom.B - top.B) * t);
            // Dither shift: add a small +/- to give the matrix
            // something to threshold against.
            int rShift = top.R == bottom.R ? 0 : 4;
            int gShift = top.G == bottom.G ? 0 : 4;
            int bShift = top.B == bottom.B ? 0 : 4;
            for (int dx = 0; dx < w; dx++)
            {
                int thr = Bayer8[dy & 7, dx & 7];
                bool up = thr < 32;
                byte rr = (byte)Math.Clamp(up ? r0 + rShift : r0 - rShift, 0, 255);
                byte gg = (byte)Math.Clamp(up ? g0 + gShift : g0 - gShift, 0, 255);
                byte bb = (byte)Math.Clamp(up ? b0 + bShift : b0 - bShift, 0, 255);
                Raylib.DrawPixel(x0 + dx, y0 + dy, new Color(rr, gg, bb, (byte)255));
            }
        }
    }
}
