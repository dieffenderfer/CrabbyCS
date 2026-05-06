using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Data;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.UI;

/// <summary>
/// Free-floating radio widget. Wears the active retro window chrome (title bar
/// drags it, X hides it) over a stripped-down utilitarian face: prev/next
/// buttons for the station, a horizontal volume slider, a power button, and a
/// big visualizer pane. Click the visualizer to cycle through six render
/// modes (bars, mirror bars, oscilloscope, radial, dancing bezier, starburst).
/// A round scrub wheel and REC button on the tape row let you rewind /
/// fast-forward the live stream and capture what you're hearing to MP3.
/// </summary>
public class RadioWidget
{
    public const int W = 256;
    public const int H = 244;

    public bool Visible;
    public Vector2 Position = new(80, 80);
    public Action? StateChanged;

    private readonly RadioPlayer _player;
    private readonly RadioMetadata _meta = new();
    private readonly SpectrumVisualizer _spectrum = new(20);
    // Sample buffer fed to the spectrum analyzer. 2048 samples at 48kHz gives
    // ~23 Hz frequency resolution, enough to separate kick-drum from hi-hat.
    private readonly float[] _spectrumSamples = new float[2048];
    private bool _power;
    private int _stationIdx;
    private float _volume = 0.6f;
    private int _vizMode;          // 0..5
    private const int VizModeCount = 9;
    // Song-change animation
    private string _displayedTrack = "";
    private string _prevDisplayedTrack = "";
    private float _songChangeAnim;
    private float _nowPlayingScrollT;
    // Varispeed: target playback speed the wheel decays back to (instead of 1.0×).
    private float _varispeed = 1.0f;
    private bool _varispeedDragging;
    // Comet trail buffer (persists across frames so the trail is real)
    private readonly Vector2[] _cometTrail = new Vector2[60];
    private int _cometTrailIdx;
    private bool _cometTrailInit;

    // Win98-style Bezier screensaver state: four control points bouncing
    // inside the panel, plus a ring buffer of recent (control-point set,
    // color index) snapshots for the afterimage trail.
    private readonly Vector2[] _bezPts = new Vector2[4];
    private readonly Vector2[] _bezVel = new Vector2[4];
    private bool _bezInit;
    private float _bezColorPhase;
    private const int BezTrailLen = 28;
    private readonly Vector2[,] _bezTrail = new Vector2[BezTrailLen, 4];
    private readonly int[] _bezTrailColor = new int[BezTrailLen];
    private int _bezTrailIdx;
    private bool _bezTrailFilled;

    private bool _dragging;
    private Vector2 _dragGrab;
    private bool _volDragging;
    private bool _powerArmed;
    private bool _prevArmed, _nextArmed;
    private bool _recArmed;
    private bool _ffArmed;
    private float _vizTime;

    // Wheel state
    private bool _wheelDragging;
    private float _wheelAngle;        // visual rotation of the wheel pointer
    private float _lastWheelAngle;    // angular position of the cursor last frame
    private double _wheelLastTime;
    private double _lastClickTime;

    public int StationIndex => _stationIdx;
    public float Volume => _volume;
    public bool Power => _power;

    public RadioWidget(RadioPlayer player) { _player = player; }

    public void Restore(int stationIdx, float volume)
    {
        _stationIdx = Math.Clamp(stationIdx, 0, Math.Max(0, RadioStations.All.Count - 1));
        _volume = Math.Clamp(volume, 0, 1);
    }

    public bool ContainsPoint(Vector2 p)
    {
        if (!Visible) return false;
        return p.X >= Position.X && p.X < Position.X + W
            && p.Y >= Position.Y && p.Y < Position.Y + H;
    }

    // ── Layout (in widget-local space) ──────────────────────────────────
    private const int TitleH = RetroWidgets.TitleBarHeight;
    private const int Pad = 6;
    private const int VizH = 64;
    private const int NowPlayingH = 28;     // big LCD with the song (primary)
    private const int StationRowH = 22;     // small strip with name · genre + prev/next
    private const int TapeRowH = 50;
    private const int BottomRowH = 26;
    private const int BtnW = 30;
    private const int PowerW = 56;
    private const int LabelW = 32;
    private const int WheelD = 44;       // wheel diameter
    private const int RecW = 36;

    private static Rectangle TitleBarLocal   => new(0, 0, W, TitleH);
    /// <summary>Small "Aa: font" cycle pill in the title bar, to the left of the X.</summary>
    private static Rectangle FontBadgeLocal  => new(W - 22 - 90, 3, 88, TitleH - 6);
    private static Rectangle VizLocal        => new(Pad, TitleH + 4, W - Pad * 2, VizH);
    private static Rectangle NowPlayingLocal => new(Pad, VizLocal.Y + VizLocal.Height + Pad, W - Pad * 2, NowPlayingH);
    private static Rectangle StationRowLocal => new(Pad, NowPlayingLocal.Y + NowPlayingH + 4, W - Pad * 2, StationRowH);
    private static Rectangle PrevBtnLocal    => new(StationRowLocal.X, StationRowLocal.Y, BtnW, StationRowH);
    private static Rectangle NextBtnLocal    => new(StationRowLocal.X + StationRowLocal.Width - BtnW, StationRowLocal.Y, BtnW, StationRowH);
    private static Rectangle StationLcdLocal
        => new(PrevBtnLocal.X + PrevBtnLocal.Width + 4, StationRowLocal.Y,
               StationRowLocal.Width - BtnW * 2 - 8, StationRowH);
    private static Rectangle TapeRowLocal    => new(Pad, StationRowLocal.Y + StationRowH + Pad, W - Pad * 2, TapeRowH);

    /// <summary>Wheel disc, centered vertically in the tape row, on the right.</summary>
    private static Rectangle WheelLocal
    {
        get
        {
            var row = TapeRowLocal;
            int wx = (int)(row.X + row.Width - WheelD - 4);
            int wy = (int)(row.Y + (row.Height - WheelD) / 2);
            return new Rectangle(wx, wy, WheelD, WheelD);
        }
    }

    /// <summary>REC button, centered vertically in the tape row, on the left.</summary>
    private static Rectangle RecBtnLocal
    {
        get
        {
            var row = TapeRowLocal;
            int rx = (int)row.X + 4;
            int ry = (int)(row.Y + (row.Height - 22) / 2);
            return new Rectangle(rx, ry, RecW, 22);
        }
    }

    /// <summary>Tape readout LCD between the REC button and the wheel.</summary>
    /// <summary>Varispeed strip between REC and the wheel: speed label + pitch slider.</summary>
    private static Rectangle VarispeedLocal
    {
        get
        {
            var row = TapeRowLocal;
            int x = (int)(RecBtnLocal.X + RecBtnLocal.Width + 6);
            int rgt = (int)WheelLocal.X - 6;
            int h = 32;
            int y = (int)(row.Y + (row.Height - h) / 2);
            return new Rectangle(x, y, Math.Max(8, rgt - x), h);
        }
    }

    /// <summary>Slider rail inside the varispeed strip.</summary>
    private static Rectangle VarispeedTrackLocal
    {
        get
        {
            var v = VarispeedLocal;
            return new Rectangle(v.X + 6, v.Y + v.Height - 12, v.Width - 12, 6);
        }
    }

    private const float VarispeedMin = -2f;
    private const float VarispeedMax = 4f;
    private static float VarispeedToT(float speed) =>
        Math.Clamp((speed - VarispeedMin) / (VarispeedMax - VarispeedMin), 0f, 1f);
    private static float TToVarispeed(float t)
    {
        float s = VarispeedMin + Math.Clamp(t, 0f, 1f) * (VarispeedMax - VarispeedMin);
        if (MathF.Abs(s - 1f) < 0.08f) s = 1f;     // detent at normal play
        return s;
    }

    /// <summary>"Get ffmpeg" button shown in the tape row when ffmpeg isn't on PATH.</summary>
    private static Rectangle FFmpegHintBtnLocal
    {
        get
        {
            var row = TapeRowLocal;
            int bw = 96, bh = 22;
            int bx = (int)(row.X + row.Width - bw - 6);
            int by = (int)(row.Y + (row.Height - bh) / 2);
            return new Rectangle(bx, by, bw, bh);
        }
    }

    private static Rectangle BottomRowLocal  => new(Pad, TapeRowLocal.Y + TapeRowH + Pad, W - Pad * 2, BottomRowH);
    private static Rectangle PowerBtnLocal   => new(BottomRowLocal.X + BottomRowLocal.Width - PowerW, BottomRowLocal.Y, PowerW, BottomRowH);

    /// <summary>Slider track (just the inset rail, not the handle).</summary>
    private static Rectangle VolTrackLocal
    {
        get
        {
            var row = BottomRowLocal;
            int trackX = (int)row.X + LabelW;
            int trackW = (int)row.Width - LabelW - PowerW - 6;
            int trackY = (int)row.Y + (BottomRowH - 8) / 2;
            return new Rectangle(trackX, trackY, trackW, 8);
        }
    }

    private bool _shadowsStarted;

    /// <summary>
    /// Test-flag: stations that shouldn't be touched by the wheel/varispeed
    /// pipeline. Classical's stream has no startup burst so the playhead
    /// rides the live edge — any tiny velocity drift from varispeed shows
    /// up as a stutter. Setting this for it locks velocity at 1.0×.
    /// </summary>
    private static bool IsLockedToOneX(string? slug, string? url) =>
        string.IsNullOrEmpty(slug);  // currently == every non-SomaFM station, i.e. WCPE

    public bool Update(float delta, Vector2 mouse, bool leftPressed, bool leftReleased, bool rightPressed)
    {
        if (!Visible) return false;
        // First time the widget shows up, kick off background ffmpeg
        // pre-loaders for non-SomaFM streams so a later switch is instant.
        if (!_shadowsStarted)
        {
            _shadowsStarted = true;
            foreach (var s in RadioStations.All)
                if (string.IsNullOrEmpty(s.Slug)) _player.StartShadow(s.Url);
        }
        if (_power) _meta.Tick();
        bool playing = _power && _player.IsPlaying;
        bool haveLive = playing && _player.CopyRecentMono(_spectrumSamples);
        _spectrum.Update(delta,
            haveLive ? _spectrumSamples : null,
            haveLive ? _player.SampleRate : 0,
            playing);
        _vizTime += delta;

        // Detect now-playing changes for the slide/flash + scroll reset.
        string currentTrack = NowPlayingLine();
        if (currentTrack != _displayedTrack)
        {
            // Don't animate the very first paint (when we go from "" to something).
            if (!string.IsNullOrEmpty(_displayedTrack)) _songChangeAnim = 1.0f;
            _prevDisplayedTrack = _displayedTrack;
            _displayedTrack = currentTrack;
            _nowPlayingScrollT = 0f;
        }
        if (_songChangeAnim > 0)
            _songChangeAnim = Math.Max(0, _songChangeAnim - delta * 1.7f);
        else
            _nowPlayingScrollT += delta;

        // After the wheel is released, velocity glides back to whatever the
        // varispeed slider is set to (instead of always returning to 1.0× —
        // this is what makes the slider sticky like a tape deck pitch fader).
        // Locked stations (WCPE) skip this entirely so velocity stays
        // exactly 1.0× and the playhead doesn't drift off the live edge.
        if (IsLockedToOneX(_player.CurrentStationSlug, _player.CurrentUrl))
        {
            _player.Velocity = 1.0;
            _wheelAngle += delta * 2.4f;
        }
        else if (!_wheelDragging)
        {
            double v = _player.Velocity;
            double target = _varispeed;
            double k = 1.0 - MathF.Exp(-delta / 0.18f);
            double next = v + (target - v) * k;
            if (Math.Abs(next - target) < 0.005) next = target;
            _player.Velocity = next;
            _wheelAngle += (float)_player.Velocity * delta * 2.4f;
        }

        // Continue active wheel drag even if the cursor leaves the widget.
        if (_wheelDragging)
        {
            UpdateWheelDrag(mouse);
            if (leftReleased) _wheelDragging = false;
            return true;
        }

        // Varispeed drag continues even if the cursor leaves the widget.
        if (_varispeedDragging)
        {
            float t = (mouse.X - (Position.X + VarispeedTrackLocal.X)) / VarispeedTrackLocal.Width;
            _varispeed = TToVarispeed(t);
            if (leftReleased) _varispeedDragging = false;
            return true;
        }

        // Volume drag continues even if the cursor leaves the widget.
        if (_volDragging)
        {
            float t = (mouse.X - (Position.X + VolTrackLocal.X)) / VolTrackLocal.Width;
            _volume = Math.Clamp(t, 0, 1);
            _player.SetVolume(_volume);
            if (leftReleased)
            {
                _volDragging = false;
                StateChanged?.Invoke();
            }
            return true;
        }

        if (_dragging)
        {
            Position = mouse - _dragGrab;
            if (leftReleased) { _dragging = false; StateChanged?.Invoke(); }
            return true;
        }

        var local = mouse - Position;
        bool inside = local.X >= 0 && local.X < W && local.Y >= 0 && local.Y < H;
        if (!inside)
        {
            if (leftReleased) { _powerArmed = _prevArmed = _nextArmed = _recArmed = false; }
            return false;
        }

        // Title bar: X / font pill first, then drag-anywhere-else.
        if (RetroSkin.PointInRect(local, TitleBarLocal))
        {
            if (leftPressed && RetroSkin.PointInRect(local, FontBadgeLocal))
            {
                CycleRadioFont();
                return true;
            }
            if (leftPressed && RetroWidgets.DrawTitleBarHitTest(TitleBarLocal, local, true))
            {
                // Closing the window also kills audio so the radio doesn't
                // keep playing invisibly until the user finds the menu toggle.
                Visible = false;
                if (_power) { _power = false; _player.Stop(); }
                StateChanged?.Invoke();
                return true;
            }
            if (leftPressed)
            {
                _dragging = true;
                _dragGrab = local;
                return true;
            }
            return true;
        }

        // Prev / next station buttons
        if (RetroWidgets.ButtonHitTest(PrevBtnLocal, local, leftPressed, leftReleased, ref _prevArmed))
            ChangeStation(-1);
        if (RetroWidgets.ButtonHitTest(NextBtnLocal, local, leftPressed, leftReleased, ref _nextArmed))
            ChangeStation(+1);

        // REC button (only if tape backend is available + power is on)
        bool tapeOn = _player.SupportsTape && _power && _player.IsPlaying;
        if (tapeOn && RetroWidgets.ButtonHitTest(RecBtnLocal, local, leftPressed, leftReleased, ref _recArmed))
            ToggleRecord();

        // "Get ffmpeg" hint button — only when ffmpeg backend is missing.
        if (!_player.SupportsTape
            && RetroWidgets.ButtonHitTest(FFmpegHintBtnLocal, local, leftPressed, leftReleased, ref _ffArmed))
        {
            OpenUrl("https://ffmpeg.org/download.html");
        }

        // Power button (also a retro pushbutton)
        if (RetroWidgets.ButtonHitTest(PowerBtnLocal, local, leftPressed, leftReleased, ref _powerArmed))
            TogglePower();

        // Wheel: must come before the visualizer/volume hits so it claims its disc.
        if (tapeOn && leftPressed && PointInWheel(local))
        {
            double now = Raylib.GetTime();
            if (now - _lastClickTime < 0.32)
            {
                _varispeed = 1.0f;
                _player.GoLive();
                _wheelDragging = false;
                _lastClickTime = 0;
                return true;
            }
            _lastClickTime = now;
            _wheelDragging = true;
            var center = new Vector2(WheelLocal.X + WheelD / 2f, WheelLocal.Y + WheelD / 2f);
            _lastWheelAngle = MathF.Atan2(local.Y - center.Y, local.X - center.X);
            _wheelLastTime = now;
            return true;
        }

        if (leftPressed)
        {
            // Visualizer click: cycle render modes
            if (RetroSkin.PointInRect(local, VizLocal))
            {
                _vizMode = (_vizMode + 1) % VizModeCount;
                StateChanged?.Invoke();
                return true;
            }
            // Station LCD click: also advances (handy big target)
            if (RetroSkin.PointInRect(local, StationLcdLocal))
            {
                ChangeStation(+1);
                return true;
            }
            // Volume track / handle: start drag
            var hitTrack = new Rectangle(VolTrackLocal.X - 2, VolTrackLocal.Y - 6,
                                          VolTrackLocal.Width + 4, VolTrackLocal.Height + 12);
            if (RetroSkin.PointInRect(local, hitTrack))
            {
                _volDragging = true;
                float t = (mouse.X - (Position.X + VolTrackLocal.X)) / VolTrackLocal.Width;
                _volume = Math.Clamp(t, 0, 1);
                _player.SetVolume(_volume);
                return true;
            }
            // Varispeed slider — only when tape backend is available.
            if (tapeOn)
            {
                var varHit = new Rectangle(VarispeedTrackLocal.X - 4, VarispeedTrackLocal.Y - 8,
                                            VarispeedTrackLocal.Width + 8, VarispeedTrackLocal.Height + 16);
                if (RetroSkin.PointInRect(local, varHit))
                {
                    _varispeedDragging = true;
                    float vt = (mouse.X - (Position.X + VarispeedTrackLocal.X)) / VarispeedTrackLocal.Width;
                    _varispeed = TToVarispeed(vt);
                    return true;
                }
            }
        }

        if (rightPressed && RetroSkin.PointInRect(local, StationLcdLocal))
        {
            ChangeStation(-1);
            return true;
        }
        // Right-click on the varispeed strip → snap to 1.0× normal play.
        if (rightPressed && _player.SupportsTape
            && RetroSkin.PointInRect(local, VarispeedLocal))
        {
            _varispeed = 1.0f;
            return true;
        }
        return inside;
    }

    private static bool PointInWheel(Vector2 local)
    {
        var c = new Vector2(WheelLocal.X + WheelD / 2f, WheelLocal.Y + WheelD / 2f);
        return Vector2.Distance(local, c) <= WheelD / 2f;
    }

    private void UpdateWheelDrag(Vector2 mouse)
    {
        // Locked stations (WCPE) ignore wheel scrubbing — keep velocity at 1×
        // so the playhead never leaves the live edge.
        if (IsLockedToOneX(_player.CurrentStationSlug, _player.CurrentUrl))
        {
            _player.Velocity = 1.0;
            return;
        }
        var local = mouse - Position;
        var center = new Vector2(WheelLocal.X + WheelD / 2f, WheelLocal.Y + WheelD / 2f);
        float angle = MathF.Atan2(local.Y - center.Y, local.X - center.X);

        // Shortest signed angular delta in radians.
        float d = angle - _lastWheelAngle;
        while (d > MathF.PI)  d -= MathF.PI * 2f;
        while (d < -MathF.PI) d += MathF.PI * 2f;
        _lastWheelAngle = angle;

        double now = Raylib.GetTime();
        double dt = Math.Max(1.0 / 240.0, now - _wheelLastTime);
        _wheelLastTime = now;

        // Map angular speed (rad/s) → playback velocity. One full turn (2π) per
        // second corresponds to 4× speed; sign follows rotation direction.
        double angularSpeed = d / dt;          // rad/s, clockwise positive on screen
        double vel = angularSpeed / (MathF.PI * 2.0) * 4.0;
        // Slight low-pass with the previous velocity to feel weighty.
        _player.Velocity = _player.Velocity * 0.5 + vel * 0.5;
        _wheelAngle += d;
    }

    private void TogglePower()
    {
        _power = !_power;
        if (_power) PlayCurrent();
        else _player.Stop();
        StateChanged?.Invoke();
    }

    private void ToggleRecord()
    {
        if (_player.IsRecording)
        {
            string? path = _player.StopRecording();
            if (!string.IsNullOrEmpty(path))
                Console.WriteLine("[RadioWidget] recorded → " + path);
        }
        else
        {
            string? path = _player.StartRecording();
            if (!string.IsNullOrEmpty(path))
                Console.WriteLine("[RadioWidget] REC → " + path);
            else
                Console.WriteLine("[RadioWidget] recording unavailable");
        }
    }

    private void ChangeStation(int dir)
    {
        if (RadioStations.All.Count == 0) return;
        int n = RadioStations.All.Count;
        _stationIdx = ((_stationIdx + dir) % n + n) % n;
        if (_power) PlayCurrent();
        StateChanged?.Invoke();
    }

    private void PlayCurrent()
    {
        if (RadioStations.All.Count == 0) return;
        var s = RadioStations.All[_stationIdx];
        _player.Play(s.Url, s.Name, _volume, s.Slug);
        _meta.SetChannel(s.Slug);
    }

    // ── Draw ─────────────────────────────────────────────────────────────
    public void Draw()
    {
        if (!Visible) return;

        int x = (int)Position.X, y = (int)Position.Y;
        var bodyRect = new Rectangle(x, y, W, H);

        // Drop shadow
        Raylib.DrawRectangle(x + 4, y + 4, W, H, new Color((byte)0, (byte)0, (byte)0, (byte)110));

        // Themed window frame
        RetroWidgets.DrawWindowFrame(bodyRect);

        // Title bar
        var titleBar = new Rectangle(x + 2, y + 2, W - 4, TitleH);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Radio", active: true);
        // Font cycle pill (sits to the left of the X close button)
        var fontBadge = new Rectangle(x + FontBadgeLocal.X, y + FontBadgeLocal.Y,
                                       FontBadgeLocal.Width, FontBadgeLocal.Height);
        Raylib.DrawRectangleRec(fontBadge, new Color((byte)0, (byte)0, (byte)0, (byte)110));
        Raylib.DrawRectangleLinesEx(fontBadge, 1, new Color((byte)180, (byte)200, (byte)220, (byte)160));
        string fontLabel = "Aa " + CurrentRadioFontName;
        // Use RetroSkin (chrome font) for the BADGE itself so it stays consistent
        // with the rest of the title bar — only the LCD content uses the radio font.
        int flw = RetroSkin.MeasureText(fontLabel, 11);
        if (flw > FontBadgeLocal.Width - 6)
            fontLabel = RetroWidgets.TruncateToWidth(fontLabel, (int)FontBadgeLocal.Width - 6, 11);
        flw = RetroSkin.MeasureText(fontLabel, 11);
        RetroSkin.DrawText(fontLabel,
            (int)(fontBadge.X + (fontBadge.Width - flw) / 2),
            (int)(fontBadge.Y + (fontBadge.Height - 11) / 2),
            new Color((byte)230, (byte)240, (byte)250, (byte)230), 11);

        // Visualizer pane (sunken)
        var viz = new Rectangle(x + VizLocal.X, y + VizLocal.Y, VizLocal.Width, VizLocal.Height);
        RetroSkin.DrawSunken(viz, fill: new Color((byte)6, (byte)8, (byte)12, (byte)255));
        DrawVisualizer(viz);
        DrawVizModeBadge((int)viz.X + (int)viz.Width - 32, (int)viz.Y + 4);

        var lcdCol = _power ? new Color((byte)80, (byte)240, (byte)80, (byte)255)
                            : new Color((byte)56, (byte)96, (byte)56, (byte)255);

        // Now-playing — primary, big LCD with song title (animates on change).
        var nowR = new Rectangle(x + NowPlayingLocal.X, y + NowPlayingLocal.Y, NowPlayingLocal.Width, NowPlayingLocal.Height);
        RetroSkin.DrawSunken(nowR, fill: new Color((byte)8, (byte)20, (byte)28, (byte)255));
        DrawNowPlaying(nowR, lcdCol);

        // Station row — secondary, small LCD with name · genre flanked by prev/next.
        var prev = new Rectangle(x + PrevBtnLocal.X, y + PrevBtnLocal.Y, PrevBtnLocal.Width, PrevBtnLocal.Height);
        var next = new Rectangle(x + NextBtnLocal.X, y + NextBtnLocal.Y, NextBtnLocal.Width, NextBtnLocal.Height);
        var slcd = new Rectangle(x + StationLcdLocal.X, y + StationLcdLocal.Y, StationLcdLocal.Width, StationLcdLocal.Height);
        RetroWidgets.ButtonVisual(prev, "<<", _prevArmed);
        RetroWidgets.ButtonVisual(next, ">>", _nextArmed);
        RetroSkin.DrawSunken(slcd, fill: new Color((byte)20, (byte)40, (byte)16, (byte)255));
        string stationLine = StationStripLine();
        const int stationFont = 13;
        string stationFitted = RetroWidgets.TruncateToWidth(stationLine, (int)slcd.Width - 10, stationFont);
        int sw = MeasureRadioText(stationFitted, stationFont);
        int sx = (int)(slcd.X + (slcd.Width - sw) / 2);
        int sy = (int)(slcd.Y + (slcd.Height - stationFont) / 2 - 1);
        DrawRadioText(stationFitted, sx, sy, lcdCol, stationFont);

        // Tape row: REC + tape readout LCD + scrub wheel
        DrawTapeRow(x, y, lcdCol);

        // Bottom row: VOL slider + power button
        var bottom = new Rectangle(x + BottomRowLocal.X, y + BottomRowLocal.Y, BottomRowLocal.Width, BottomRowLocal.Height);
        RetroSkin.DrawText("VOL", (int)bottom.X,
            (int)(bottom.Y + (bottom.Height - 14) / 2), RetroSkin.BodyText, 14);
        var track = new Rectangle(x + VolTrackLocal.X, y + VolTrackLocal.Y, VolTrackLocal.Width, VolTrackLocal.Height);
        RetroSkin.DrawSunken(track);
        int fillW = (int)(track.Width * _volume);
        if (fillW > 0)
        {
            Raylib.DrawRectangle((int)track.X + 1, (int)track.Y + 1, fillW - 1, (int)track.Height - 2,
                RetroSkin.TitleActive);
        }
        int handleX = (int)(track.X + _volume * track.Width);
        var handle = new Rectangle(handleX - 5, track.Y - 5, 10, track.Height + 10);
        RetroSkin.DrawRaised(handle);

        var pwr = new Rectangle(x + PowerBtnLocal.X, y + PowerBtnLocal.Y, PowerBtnLocal.Width, PowerBtnLocal.Height);
        if (_power) RetroSkin.DrawPressed(pwr); else RetroSkin.DrawRaised(pwr);
        int pyOff = _power ? 1 : 0;
        var pwrDotCol = _power ? new Color((byte)220, (byte)60, (byte)60, (byte)255)
                               : new Color((byte)100, (byte)100, (byte)100, (byte)255);
        Raylib.DrawCircle((int)pwr.X + 12 + pyOff, (int)pwr.Y + (int)pwr.Height / 2 + pyOff, 4, pwrDotCol);
        if (_power)
            Raylib.DrawCircle((int)pwr.X + 11 + pyOff, (int)pwr.Y + (int)pwr.Height / 2 - 1 + pyOff,
                1, new Color((byte)255, (byte)200, (byte)200, (byte)255));
        string pwrLabel = _power ? "ON" : "OFF";
        int pwrLabelW = RetroSkin.MeasureText(pwrLabel, 14);
        RetroSkin.DrawText(pwrLabel,
            (int)pwr.X + 22 + ((int)pwr.Width - 22 - pwrLabelW) / 2 + pyOff,
            (int)(pwr.Y + (pwr.Height - 14) / 2) + pyOff,
            RetroSkin.BodyText, 14);
    }

    private void DrawTapeRow(int x, int y, Color lcdCol)
    {
        bool tapeAvailable = _player.SupportsTape;
        bool tapeActive = tapeAvailable && _power && _player.IsPlaying;

        if (!tapeAvailable)
        {
            DrawFFmpegHint(x, y);
            return;
        }

        // REC button
        var rec = new Rectangle(x + RecBtnLocal.X, y + RecBtnLocal.Y, RecBtnLocal.Width, RecBtnLocal.Height);
        bool recOn = _player.IsRecording;
        if (recOn || _recArmed) RetroSkin.DrawPressed(rec); else RetroSkin.DrawRaised(rec);
        // Pulsing red dot when recording, dim when unavailable.
        float pulse = recOn ? 0.55f + 0.45f * MathF.Sin((float)Raylib.GetTime() * 6f) : 1f;
        byte alpha = (byte)(tapeActive ? 255 : 110);
        var dotBase = recOn
            ? new Color((byte)(220 * pulse + 35), (byte)40, (byte)40, alpha)
            : new Color((byte)200, (byte)40, (byte)40, alpha);
        int doff = (recOn || _recArmed) ? 1 : 0;
        Raylib.DrawCircle((int)rec.X + 10 + doff, (int)rec.Y + (int)rec.Height / 2 + doff, 4, dotBase);
        var labelCol = tapeActive ? RetroSkin.BodyText : RetroSkin.DisabledText;
        RetroSkin.DrawText("REC", (int)rec.X + 17 + doff, (int)(rec.Y + (rec.Height - 12) / 2) + doff, labelCol, 12);

        // Varispeed strip — pitch fader between REC and the wheel.
        var vs = new Rectangle(x + VarispeedLocal.X, y + VarispeedLocal.Y,
                               VarispeedLocal.Width, VarispeedLocal.Height);
        DrawVarispeedStrip(vs, lcdCol, tapeActive);

        // Wheel
        var wheelRect = new Rectangle(x + WheelLocal.X, y + WheelLocal.Y, WheelD, WheelD);
        DrawWheel(wheelRect, tapeActive);
    }

    private void DrawVarispeedStrip(Rectangle r, Color col, bool active)
    {
        RetroSkin.DrawSunken(r, fill: new Color((byte)8, (byte)20, (byte)28, (byte)255));

        // Speed label (top of the strip): "+1.50×" / "1.00×" / "-0.50×".
        // Highlights when scrubbing live differs from the slider's resting target.
        double live = _player.Velocity;
        bool scrubbing = Math.Abs(live - _varispeed) > 0.05;
        string label = scrubbing
            ? $"{(_varispeed >= 0 ? "+" : "")}{_varispeed:0.00}×  ({(live >= 0 ? "+" : "")}{live:0.00}×)"
            : $"{(_varispeed >= 0 ? "+" : "")}{_varispeed:0.00}×";
        const int labelFont = 12;
        int lw = MeasureRadioText(label, labelFont);
        int lx = (int)(r.X + (r.Width - lw) / 2);
        int ly = (int)r.Y + 3;
        var labelCol = active ? col : new Color((byte)56, (byte)96, (byte)56, (byte)180);
        DrawRadioText(label, lx, ly, labelCol, labelFont);

        // Track + ticks at -1, 0, +1, +2, +3
        // Track rect lives in widget-screen space already (caller passes screen r).
        int trackX = (int)r.X + 6;
        int trackW = (int)r.Width - 12;
        int trackY = (int)r.Y + (int)r.Height - 12;
        int trackH = 6;
        var track = new Rectangle(trackX, trackY, trackW, trackH);
        RetroSkin.DrawSunken(track);

        // Tick marks
        var tickCol = active
            ? new Color((byte)160, (byte)180, (byte)200, (byte)200)
            : new Color((byte)90, (byte)100, (byte)110, (byte)180);
        foreach (float speed in new[] { -1f, 0f, 1f, 2f, 3f })
        {
            float t = VarispeedToT(speed);
            int tx = trackX + (int)(t * trackW);
            int th = (speed == 1f) ? 5 : 3;     // taller tick at the 1× detent
            Raylib.DrawRectangle(tx, trackY - th, 1, th, tickCol);
        }

        // Filled portion from the 1× detent to the current position.
        float curT = VarispeedToT(_varispeed);
        float oneT = VarispeedToT(1f);
        int curX = trackX + (int)(curT * trackW);
        int oneX = trackX + (int)(oneT * trackW);
        int fillX = Math.Min(curX, oneX);
        int fillW = Math.Abs(curX - oneX);
        if (fillW > 0)
        {
            Raylib.DrawRectangle(fillX, trackY + 1, fillW, trackH - 2, RetroSkin.TitleActive);
        }

        // Handle
        var handle = new Rectangle(curX - 5, trackY - 4, 10, trackH + 8);
        if (active) RetroSkin.DrawRaised(handle);
        else RetroSkin.DrawSunken(handle);
    }

    private void DrawFFmpegHint(int x, int y)
    {
        var row = new Rectangle(x + TapeRowLocal.X, y + TapeRowLocal.Y, TapeRowLocal.Width, TapeRowLocal.Height);
        // Subtle sunken plate so the hint feels like part of the chassis, not a missing piece.
        RetroSkin.DrawSunken(row, fill: RetroSkin.Face);
        RetroSkin.DrawText("Rewind & record need ffmpeg.",
            (int)row.X + 8, (int)row.Y + 8, RetroSkin.BodyText, 13);
        RetroSkin.DrawText("Install it, then restart.",
            (int)row.X + 8, (int)row.Y + 24, RetroSkin.DisabledText, 12);

        var btn = new Rectangle(x + FFmpegHintBtnLocal.X, y + FFmpegHintBtnLocal.Y,
                                FFmpegHintBtnLocal.Width, FFmpegHintBtnLocal.Height);
        RetroWidgets.ButtonVisual(btn, "Get ffmpeg »", _ffArmed);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo("cmd", $"/c start \"\" \"{url}\"")
                    { CreateNoWindow = true, UseShellExecute = false }
                : new System.Diagnostics.ProcessStartInfo(
                    OperatingSystem.IsMacOS() ? "open" : "xdg-open", url)
                    { UseShellExecute = false };
            System.Diagnostics.Process.Start(psi);
        }
        catch { /* swallow — best effort */ }
    }

    private void DrawWheel(Rectangle r, bool active)
    {
        float cx = r.X + r.Width / 2f;
        float cy = r.Y + r.Height / 2f;
        float radius = r.Width / 2f;
        byte a = (byte)(active ? 255 : 110);

        // Dark recessed pad behind the wheel.
        Raylib.DrawCircle((int)cx + 1, (int)cy + 1, radius, new Color((byte)0, (byte)0, (byte)0, (byte)(110 * a / 255)));
        Raylib.DrawCircle((int)cx, (int)cy, radius, new Color((byte)40, (byte)44, (byte)52, a));

        // Beveled disc.
        var faceTop = new Color((byte)200, (byte)204, (byte)212, a);
        var faceBot = new Color((byte)120, (byte)128, (byte)138, a);
        Raylib.DrawCircleGradient((int)cx, (int)cy, radius - 3, faceTop, faceBot);

        // Tick marks around the rim.
        for (int i = 0; i < 12; i++)
        {
            float ang = i / 12f * MathF.PI * 2f;
            var p1 = new Vector2(cx + MathF.Cos(ang) * (radius - 5), cy + MathF.Sin(ang) * (radius - 5));
            var p2 = new Vector2(cx + MathF.Cos(ang) * (radius - 9), cy + MathF.Sin(ang) * (radius - 9));
            Raylib.DrawLineEx(p1, p2, 1.5f,
                new Color((byte)40, (byte)40, (byte)50, (byte)(180 * a / 255)));
        }

        // Center hub.
        Raylib.DrawCircle((int)cx, (int)cy, 4, new Color((byte)32, (byte)36, (byte)44, a));

        // Pointer / dimple — rotates with _wheelAngle.
        float pa = _wheelAngle - MathF.PI / 2f; // 12 o'clock at angle=0
        var tip = new Vector2(cx + MathF.Cos(pa) * (radius - 8), cy + MathF.Sin(pa) * (radius - 8));
        Raylib.DrawCircleV(tip, 3.5f, new Color((byte)230, (byte)40, (byte)40, a));
        Raylib.DrawCircleV(tip, 1.5f, new Color((byte)255, (byte)200, (byte)200, a));

        // Outer ring.
        Raylib.DrawCircleLines((int)cx, (int)cy, radius, new Color((byte)16, (byte)16, (byte)20, a));
    }

    private string NowPlayingLine()
    {
        if (!_player.BackendAvailable) return "no audio backend";
        if (RadioStations.All.Count == 0) return "no stations";
        if (_power && _meta.HasTrack)
        {
            string track = string.IsNullOrEmpty(_meta.CurrentArtist)
                ? _meta.CurrentTitle
                : $"{_meta.CurrentArtist} — {_meta.CurrentTitle}";
            if (!string.IsNullOrWhiteSpace(track)) return "♪ " + track;
        }
        return _power ? "tuning…" : "—";
    }

    private string StationStripLine()
    {
        if (!_player.BackendAvailable) return "no audio backend";
        if (RadioStations.All.Count == 0) return "no stations";
        var s = RadioStations.All[_stationIdx];
        return $"{s.Name}  ·  {s.Genre}";
    }

    private void DrawNowPlaying(Rectangle r, Color baseCol)
    {
        const int font = 18;

        // Slide animation takes priority — both old and new shown truncated.
        if (_songChangeAnim > 0 && !string.IsNullOrEmpty(_prevDisplayedTrack))
        {
            float t = _songChangeAnim;
            float prog = 1f - t;
            float ease = prog * prog * (3f - 2f * prog);
            int slideH = (int)r.Height + 4;
            int oldOff = -(int)(ease * slideH);
            int newOff = (int)((1f - ease) * slideH);
            byte boost = (byte)((1f - ease) * 130);
            var hot = new Color(
                (byte)Math.Min(255, baseCol.R + boost),
                (byte)Math.Min(255, baseCol.G + boost),
                (byte)Math.Min(255, baseCol.B + boost),
                baseCol.A);
            BeginNowPlayingScissor(r);
            DrawNowPlayingText(r, _prevDisplayedTrack, baseCol, oldOff, font);
            DrawNowPlayingText(r, _displayedTrack, hot, newOff, font);
            Raylib.EndScissorMode();
            return;
        }

        if (string.IsNullOrEmpty(_displayedTrack)) return;
        int maxW = (int)r.Width - 12;
        int fullW = MeasureRadioText(_displayedTrack, font);

        // Fits — center-align statically, no scrolling needed.
        if (fullW <= maxW)
        {
            DrawNowPlayingText(r, _displayedTrack, baseCol, 0, font);
            return;
        }

        // Doesn't fit — pause briefly, then scroll left at a slow walk and
        // loop with a gap so the full text is always eventually visible.
        const float pauseStart = 1.6f;
        const float scrollSpeed = 26f;
        const int gap = 48;
        int loopW = fullW + gap;
        float offset = _nowPlayingScrollT < pauseStart
            ? 0f
            : (_nowPlayingScrollT - pauseStart) * scrollSpeed;
        offset = ((offset % loopW) + loopW) % loopW;
        int startX = (int)r.X + 6;
        int ty = (int)(r.Y + (r.Height - font) / 2);
        int drawX = startX - (int)offset;

        BeginNowPlayingScissor(r);
        DrawRadioText(_displayedTrack, drawX, ty, baseCol, font);
        DrawRadioText(_displayedTrack, drawX + loopW, ty, baseCol, font);
        Raylib.EndScissorMode();
    }

    private static void BeginNowPlayingScissor(Rectangle r)
    {
        var dpi = Raylib.GetWindowScaleDPI();
        Raylib.BeginScissorMode(
            (int)((r.X + 2) * dpi.X),
            (int)((r.Y + 2) * dpi.Y),
            (int)((r.Width - 4) * dpi.X),
            (int)((r.Height - 4) * dpi.Y));
    }

    private void DrawNowPlayingText(Rectangle r, string text, Color col, int yOffset, int font)
    {
        if (string.IsNullOrEmpty(text)) return;
        string fitted = TruncateRadioText(text, (int)r.Width - 12, font);
        int tw = MeasureRadioText(fitted, font);
        int tx = (int)(r.X + (r.Width - tw) / 2);
        int ty = (int)(r.Y + (r.Height - font) / 2) + yOffset;
        DrawRadioText(fitted, tx, ty, col, font);
    }

    private void DrawVizModeBadge(int x, int y)
    {
        string label = _vizMode switch
        {
            0 => "BARS",
            1 => "MIRROR",
            2 => "WAVE",
            3 => "RADIAL",
            4 => "BEZIER",
            5 => "STARS",
            6 => "PLASMA",
            7 => "TUNNEL",
            _ => "COMET",
        };
        int w = RetroSkin.MeasureText(label, 10) + 6;
        Raylib.DrawRectangle(x - w + 28, y, w, 11, new Color((byte)0, (byte)0, (byte)0, (byte)160));
        RetroSkin.DrawText(label, x - w + 31, y, new Color((byte)180, (byte)220, (byte)200, (byte)220), 10);
    }

    // ── Visualizers ──────────────────────────────────────────────────────
    private void DrawVisualizer(Rectangle r)
    {
        switch (_vizMode)
        {
            case 0: DrawBars(r); break;
            case 1: DrawMirrorBars(r); break;
            case 2: DrawWave(r); break;
            case 3: DrawRadial(r); break;
            case 4: DrawBezier(r); break;
            case 5: DrawStars(r); break;
            case 6: DrawPlasma(r); break;
            case 7: DrawTunnel(r); break;
            default: DrawComet(r); break;
        }
    }

    private float AvgBeat()
    {
        float s = 0;
        for (int i = 0; i < _spectrum.BandCount; i++) s += _spectrum.Bar(i);
        return s / Math.Max(1, _spectrum.BandCount);
    }

    private void DrawPlasma(Rectangle r)
    {
        // Higher-res cells than before, with the source frequency mix tied to
        // bass/mid/treble so different parts of the song push different
        // wavefronts.
        const int gw = 42, gh = 22;
        float cw = r.Width / (float)gw;
        float ch = r.Height / (float)gh;
        float t = _vizTime;
        float bass = _spectrum.Bass;
        float mid = _spectrum.Mid;
        float treb = _spectrum.Treble;
        for (int gy = 0; gy < gh; gy++)
        {
            for (int gx = 0; gx < gw; gx++)
            {
                float u = (float)gx / gw;
                float v = (float)gy / gh;
                float dx = u - 0.5f, dy = v - 0.5f;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                float k = MathF.Sin(u * 5.5f + t * (0.7f + treb * 2f))
                       + MathF.Sin((u + v) * 7f + t * 1.3f)
                       + MathF.Sin(dist * (18f + treb * 24f) - t * 2.6f) * (1f + treb * 0.8f)
                       + MathF.Sin(v * 6f - t * 0.9f) * (0.7f + bass * 2.2f)
                       + MathF.Cos(dist * 8f + t * (1.2f + mid * 2f)) * (0.6f + mid * 1.2f);
                float n = (k + 5f) / 10f;
                float hue = (n * 340f + t * 50f + bass * 60f) % 360f;
                float sat = 0.8f + 0.2f * mid;
                float val = 0.45f + 0.55f * n;
                HsvToRgb(hue, sat, val, out var rr, out var gg, out var bb);
                int px = (int)(r.X + gx * cw);
                int py = (int)(r.Y + gy * ch);
                Raylib.DrawRectangle(px, py, (int)MathF.Ceiling(cw), (int)MathF.Ceiling(ch),
                    new Color(rr, gg, bb, (byte)255));
            }
        }
    }

    private void DrawTunnel(Rectangle r)
    {
        // Aspect-corrected receding tunnel: 32 polygon rings, 16 streamer
        // particles between them, and 8 radial spokes at the back. Hue
        // cycles around the rings; bass pumps the depth, treble strobes
        // the spokes and tips.
        var c = new Vector2(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        float t = _vizTime;
        float bass = _spectrum.Bass;
        float mid = _spectrum.Mid;
        float treb = _spectrum.Treble;
        float energy = _spectrum.Energy;

        // The panel is wide and short — stretch the tunnel along x so it
        // visually fills the rect rather than being a circle in the middle.
        float aspect = r.Width / Math.Max(1f, r.Height);
        float maxR = MathF.Max(r.Width, r.Height) * 0.7f;

        const int rings = 32;
        const int sides = 14;
        Span<Vector2> pts = stackalloc Vector2[sides + 1];

        // Outer aura first — a soft rectangle gradient from the rim inward,
        // colored by treble. Costs almost nothing and kills the dead corners.
        if (treb > 0.05f)
        {
            byte aa = (byte)(treb * 80);
            Raylib.DrawRectangleLines((int)r.X + 1, (int)r.Y + 1,
                (int)r.Width - 2, (int)r.Height - 2,
                new Color((byte)200, (byte)80, (byte)200, aa));
        }

        // Back-to-front so closer rings cover farther ones.
        for (int i = rings - 1; i >= 0; i--)
        {
            // Depth advances faster on bass, so kicks make the tunnel surge.
            float depth = ((i + t * (1.5f + bass * 1.6f)) % rings) / rings;
            float radius = depth * depth * maxR * (1f + bass * 0.5f);
            float angle = depth * 7f + t * (0.4f + mid * 0.9f);
            for (int k = 0; k <= sides; k++)
            {
                float a = angle + k * (MathF.PI * 2f / sides);
                pts[k] = c + new Vector2(
                    MathF.Cos(a) * radius * aspect * 0.55f,
                    MathF.Sin(a) * radius);
            }
            float lit = 1f - depth;
            HsvToRgb((depth * 720f + t * 60f + bass * 120f) % 360f, 0.85f,
                0.35f + 0.55f * lit, out var rc, out var gc, out var bc);
            byte alpha = (byte)Math.Clamp((int)(220 * lit + 35), 0, 255);
            var col = new Color(rc, gc, bc, alpha);
            float thick = 0.8f + lit * (2.4f + treb * 2.0f);
            for (int k = 0; k < sides; k++)
                Raylib.DrawLineEx(pts[k], pts[k + 1], thick, col);
        }

        // Streamer particles — points that traverse the tunnel along fixed
        // angles. Their depth phase is offset per particle so they don't all
        // group up. Colors riff on the ring hue palette but brighter.
        const int streamers = 22;
        for (int i = 0; i < streamers; i++)
        {
            float seed = i / (float)streamers;
            float ang = seed * MathF.PI * 2f + t * 0.3f;
            float depth = Frac(seed * 1.7f + t * (0.55f + bass * 0.8f));
            float radius = depth * depth * maxR * (1f + bass * 0.5f);
            var p = c + new Vector2(
                MathF.Cos(ang) * radius * aspect * 0.55f,
                MathF.Sin(ang) * radius);
            float lit = 1f - depth;
            HsvToRgb((seed * 360f + t * 80f) % 360f, 0.9f, 0.4f + 0.6f * lit,
                out var rc, out var gc, out var bc);
            float sz = 0.8f + lit * (1.4f + treb * 2.6f);
            Raylib.DrawCircleV(p, sz, new Color(rc, gc, bc, (byte)Math.Clamp((int)(lit * 255 + 30), 0, 255)));
        }

        // Radial spokes at the very back, strobing on treble. They give a
        // sense of "looking straight down a corridor" and make the dead zone
        // around the vanishing point feel alive at idle.
        const int spokes = 10;
        for (int i = 0; i < spokes; i++)
        {
            float ang = i / (float)spokes * MathF.PI * 2f + t * 0.18f;
            float lenInner = 4f + bass * 8f;
            float lenOuter = 10f + treb * 24f + energy * 30f;
            var p1 = c + new Vector2(MathF.Cos(ang) * lenInner * aspect * 0.55f,
                                     MathF.Sin(ang) * lenInner);
            var p2 = c + new Vector2(MathF.Cos(ang) * lenOuter * aspect * 0.55f,
                                     MathF.Sin(ang) * lenOuter);
            byte ra = (byte)Math.Clamp((int)(treb * 220 + 60), 0, 255);
            Raylib.DrawLineEx(p1, p2, 1.0f + treb * 1.5f,
                new Color((byte)(160 + treb * 95), (byte)200, (byte)(255 - treb * 60), ra));
        }

        // Pulsing core that turns chromatic-bright on big kicks.
        float coreR = 2f + bass * 8f + energy * 4f;
        if (bass > 0.4f)
            Raylib.DrawCircleV(c, coreR + 6, new Color((byte)255, (byte)180, (byte)180, (byte)90));
        Raylib.DrawCircleV(c, coreR, new Color((byte)255, (byte)240, (byte)200, (byte)255));
    }

    private void DrawComet(Rectangle r)
    {
        // Three lissajous-orbiting comets with persistent fading trails. Each
        // comet's lobe ratio comes from a different audio band so the curves
        // fold and unfold with the music instead of just scaling.
        var center = new Vector2(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        if (!_cometTrailInit)
        {
            for (int i = 0; i < _cometTrail.Length; i++) _cometTrail[i] = center;
            _cometTrailInit = true;
        }

        float bass = _spectrum.Bass;
        float mid = _spectrum.Mid;
        float treb = _spectrum.Treble;
        float maxR = MathF.Min(r.Width, r.Height) * 0.46f;
        float aspect = r.Width / Math.Max(1f, r.Height);

        float angle = _vizTime * 1.4f + bass * 6f;
        float lobe = 1.3f + mid * 2.2f;          // y/x frequency ratio
        float radius = (0.35f + 0.65f * mid) * maxR;
        var head = center + new Vector2(
            MathF.Cos(angle) * radius * aspect * 0.55f,
            MathF.Sin(angle * lobe + treb * 4f) * radius);
        _cometTrail[_cometTrailIdx] = head;
        _cometTrailIdx = (_cometTrailIdx + 1) % _cometTrail.Length;

        // Older lap remnants: every step up to N apart, fading exponentially.
        for (int i = 1; i < _cometTrail.Length; i++)
        {
            int a = (_cometTrailIdx + i - 1) % _cometTrail.Length;
            int b = (_cometTrailIdx + i) % _cometTrail.Length;
            float age = i / (float)_cometTrail.Length;
            byte alpha = (byte)(age * age * 255);
            byte rc = (byte)(60 + age * 195);
            byte gc = (byte)(180 + age * 75);
            Raylib.DrawLineEx(_cometTrail[a], _cometTrail[b],
                0.6f + age * 2.4f,
                new Color(rc, gc, (byte)255, alpha));
        }

        // Two ghost comets at fixed phase offsets behind the leader, drawn
        // dimmer. Adds visual density without doubling the trail buffer.
        for (int g = 1; g <= 2; g++)
        {
            float ga = angle - g * 0.9f;
            float gh = treb * 4f - g * 0.8f;
            var p = center + new Vector2(
                MathF.Cos(ga) * radius * aspect * 0.55f,
                MathF.Sin(ga * lobe + gh) * radius);
            byte alpha = (byte)(140 - g * 50);
            Raylib.DrawCircleV(p, 1.6f + bass * 1.8f,
                new Color((byte)160, (byte)200, (byte)255, alpha));
        }

        // Bright head with a quick chromatic ring on bass kicks.
        if (bass > 0.4f)
            Raylib.DrawCircleLines((int)head.X, (int)head.Y, 4 + bass * 6,
                new Color((byte)255, (byte)200, (byte)160, (byte)200));
        Raylib.DrawCircleV(head, 2.5f + bass * 1.5f,
            new Color((byte)255, (byte)240, (byte)200, (byte)255));
    }

    private static void HsvToRgb(float h, float s, float v,
        out byte r, out byte g, out byte b)
    {
        h = (h % 360f + 360f) % 360f;
        float c = v * s;
        float x = c * (1f - MathF.Abs((h / 60f) % 2f - 1f));
        float m = v - c;
        float rf, gf, bf;
        if      (h < 60)  { rf = c; gf = x; bf = 0; }
        else if (h < 120) { rf = x; gf = c; bf = 0; }
        else if (h < 180) { rf = 0; gf = c; bf = x; }
        else if (h < 240) { rf = 0; gf = x; bf = c; }
        else if (h < 300) { rf = x; gf = 0; bf = c; }
        else              { rf = c; gf = 0; bf = x; }
        r = (byte)Math.Clamp((int)((rf + m) * 255f), 0, 255);
        g = (byte)Math.Clamp((int)((gf + m) * 255f), 0, 255);
        b = (byte)Math.Clamp((int)((bf + m) * 255f), 0, 255);
    }

    private void DrawBars(Rectangle r)
    {
        int n = _spectrum.BandCount;
        int gap = 1;
        int barW = Math.Max(1, ((int)r.Width - (n + 1) * gap) / n);
        int innerH = (int)r.Height - 4;
        int rowH = 3;
        int rows = innerH / rowH;
        int baseY = (int)r.Y + (int)r.Height - 2;

        for (int i = 0; i < n; i++)
        {
            int bx = (int)r.X + gap + i * (barW + gap);
            int litRows = (int)MathF.Round(_spectrum.Bar(i) * rows);
            int peakRow = (int)MathF.Round(_spectrum.Peak(i) * rows);
            for (int rr = 0; rr < rows; rr++)
            {
                int sy = baseY - (rr + 1) * rowH + 1;
                Color c;
                if (rr < litRows)
                {
                    float t = (float)rr / Math.Max(1, rows - 1);
                    c = t < 0.55f ? new Color((byte)40, (byte)230, (byte)80, (byte)255)
                      : t < 0.80f ? new Color((byte)230, (byte)210, (byte)50, (byte)255)
                                  : new Color((byte)230, (byte)60, (byte)50, (byte)255);
                }
                else c = new Color((byte)18, (byte)24, (byte)20, (byte)255);
                Raylib.DrawRectangle(bx, sy, barW, 2, c);
            }
            if (peakRow > 0 && peakRow <= rows)
            {
                int sy = baseY - peakRow * rowH + 1;
                Raylib.DrawRectangle(bx, sy, barW, 2, new Color((byte)220, (byte)230, (byte)240, (byte)255));
            }
        }
    }

    private void DrawMirrorBars(Rectangle r)
    {
        int n = _spectrum.BandCount;
        int half = (int)r.Width / 2;
        int barW = Math.Max(1, (half - 2) / (n + 1));
        int gap = 1;
        int midX = (int)r.X + (int)r.Width / 2;
        int midY = (int)r.Y + (int)r.Height / 2;
        int maxLen = (int)r.Height / 2 - 1;

        // Bass-pumped centerline glow — fills dead space at idle and pulses
        // hard on kicks.
        float bass = _spectrum.Bass;
        int glowH = 1 + (int)(bass * 8);
        for (int g = 0; g < glowH; g++)
        {
            byte a = (byte)Math.Clamp((int)(180 - g * 24), 0, 200);
            Raylib.DrawRectangle((int)r.X + 2, midY - g, (int)r.Width - 4, 1,
                new Color((byte)80, (byte)200, (byte)255, a));
            Raylib.DrawRectangle((int)r.X + 2, midY + g, (int)r.Width - 4, 1,
                new Color((byte)80, (byte)200, (byte)255, a));
        }

        for (int i = 0; i < n; i++)
        {
            float bar = _spectrum.Bar(i);
            // Power-curve so bigger spikes feel bigger without saturating mids.
            float norm = MathF.Pow(bar, 0.85f);
            int len = (int)(norm * maxLen);
            byte br = (byte)(80 + (int)(norm * 175));
            // Shift hue from blue-green at bass to magenta at treble.
            HsvToRgb(180f + i * 9f, 0.85f, 0.55f + 0.45f * norm,
                out var rc, out var gc, out var bc);
            var c = new Color(rc, gc, bc, (byte)255);
            int rx = midX + i * (barW + gap) + 1;
            int lx = midX - (i + 1) * (barW + gap) + 1;
            Raylib.DrawRectangle(rx, midY - len, barW, len * 2 + 1, c);
            Raylib.DrawRectangle(lx, midY - len, barW, len * 2 + 1, c);
            // Tip cap matching peak hold.
            float peak = _spectrum.Peak(i);
            int peakLen = (int)(MathF.Pow(peak, 0.85f) * maxLen);
            if (peakLen > 0)
            {
                var cap = new Color((byte)230, (byte)240, (byte)250, (byte)220);
                Raylib.DrawRectangle(rx, midY - peakLen, barW, 1, cap);
                Raylib.DrawRectangle(rx, midY + peakLen, barW, 1, cap);
                Raylib.DrawRectangle(lx, midY - peakLen, barW, 1, cap);
                Raylib.DrawRectangle(lx, midY + peakLen, barW, 1, cap);
            }
        }
    }

    private void DrawWave(Rectangle r)
    {
        int samples = (int)r.Width - 4;
        if (samples < 4) return;
        int midY = (int)r.Y + (int)r.Height / 2;
        int amp = (int)r.Height / 2 - 2;

        // Faint baseline with bass pulse so the panel never goes dead.
        float bass = _spectrum.Bass;
        Raylib.DrawRectangle((int)r.X + 2, midY, (int)r.Width - 4, 1,
            new Color((byte)40, (byte)80, (byte)60, (byte)120));

        // Three superposed traces, each summing many bands at a different
        // harmonic count and time speed. Drawing them at slightly different
        // alphas/colors gives the lissajous-on-an-oscilloscope feel.
        DrawWaveLayer(r, samples, midY, amp,
            colorA: new Color((byte)40, (byte)180, (byte)120, (byte)90),
            phase: _vizTime * 5f, harm: 3, bandStride: 2, ampScale: 1.0f, thick: 2.5f);
        DrawWaveLayer(r, samples, midY, amp,
            colorA: new Color((byte)80, (byte)240, (byte)180, (byte)180),
            phase: _vizTime * 7f, harm: 6, bandStride: 1, ampScale: 0.9f, thick: 1.6f);
        DrawWaveLayer(r, samples, midY, amp,
            colorA: new Color((byte)200, (byte)255, (byte)220, (byte)240),
            phase: _vizTime * 9f, harm: 11, bandStride: 1, ampScale: 0.6f + bass * 0.6f, thick: 1f);
    }

    private void DrawWaveLayer(Rectangle r, int samples, int midY, int amp,
                               Color colorA, float phase, int harm, int bandStride,
                               float ampScale, float thick)
    {
        int n = _spectrum.BandCount;
        Vector2 prev = new((int)r.X + 2, midY);
        for (int i = 1; i < samples; i++)
        {
            float u = (float)i / samples;
            float v = 0f;
            // Sum sin/cos of the band's "frequency index" weighted by its bar
            // value, so peaks in any band thicken the wave at that point.
            for (int b = 0; b < harm; b++)
            {
                int band = (b * bandStride) % n;
                float bar = _spectrum.Bar(band);
                v += MathF.Sin(u * MathF.PI * (2 + b * 2.5f) + phase + b) * bar;
            }
            float vy = v * ampScale * 0.45f;
            var p = new Vector2((int)r.X + 2 + i, midY + (int)(vy * amp));
            Raylib.DrawLineEx(prev, p, thick, colorA);
            prev = p;
        }
    }

    private void DrawRadial(Rectangle r)
    {
        int n = _spectrum.BandCount;
        float cx = r.X + r.Width / 2;
        float cy = r.Y + r.Height / 2;
        float bass = _spectrum.Bass;
        float energy = _spectrum.Energy;
        float baseR = MathF.Min(r.Width, r.Height) * 0.16f * (1f + bass * 0.6f);
        float maxLen = MathF.Min(r.Width, r.Height) * 0.55f;
        float spin = _vizTime * 0.7f;

        // Hub: pulsing translucent circle plus crisp inner ring.
        Raylib.DrawCircle((int)cx, (int)cy, baseR + 2 + bass * 6,
            new Color((byte)40, (byte)80, (byte)160, (byte)50));
        Raylib.DrawCircleLines((int)cx, (int)cy, baseR,
            new Color((byte)100, (byte)160, (byte)220, (byte)200));

        // Forward rays — aspect-corrected so the panel's tall-narrow shape
        // doesn't squish them into the corners.
        float aspect = r.Width / Math.Max(1f, r.Height);
        for (int i = 0; i < n; i++)
        {
            float a = i / (float)n * MathF.PI * 2 + spin;
            float bar = _spectrum.Bar(i);
            float len = baseR + bar * maxLen;
            var p1 = new Vector2(cx + MathF.Cos(a) * baseR,
                                 cy + MathF.Sin(a) * baseR / aspect);
            var p2 = new Vector2(cx + MathF.Cos(a) * len,
                                 cy + MathF.Sin(a) * len / aspect);
            HsvToRgb(200f + i * 10f, 0.85f, 0.55f + 0.45f * bar,
                out var rc, out var gc, out var bc);
            Raylib.DrawLineEx(p1, p2, 1.6f + bar * 1.4f, new Color(rc, gc, bc, (byte)255));
            // Tip dot.
            Raylib.DrawCircleV(p2, 1.2f + bar * 1.6f, new Color((byte)255, (byte)240, (byte)200, (byte)220));
        }

        // Counter-rotating shorter inner rays — fills the dead zone between
        // hub and outer rays at idle.
        float spin2 = -_vizTime * 1.4f;
        for (int i = 0; i < n; i++)
        {
            float a = i / (float)n * MathF.PI * 2 + spin2;
            float bar = _spectrum.Bar((i + 5) % n);
            float len = baseR + bar * maxLen * 0.45f;
            var p1 = new Vector2(cx + MathF.Cos(a) * (baseR - 2),
                                 cy + MathF.Sin(a) * (baseR - 2) / aspect);
            var p2 = new Vector2(cx + MathF.Cos(a) * len,
                                 cy + MathF.Sin(a) * len / aspect);
            byte br = (byte)(60 + bar * 160);
            Raylib.DrawLineEx(p1, p2, 1f, new Color(br, (byte)(br * 0.7f), (byte)(255 - br / 2), (byte)180));
        }

        // Energy halo sweeping outward when audio gets loud.
        if (energy > 0.05f)
        {
            float haloR = baseR + energy * maxLen * 0.85f;
            byte ha = (byte)Math.Clamp((int)(energy * 220), 0, 220);
            Raylib.DrawCircleLines((int)cx, (int)cy, haloR,
                new Color((byte)180, (byte)220, (byte)255, ha));
        }
    }

    // Classic VGA 16-color palette (bright slots only) — the Win98 Bezier
    // screensaver cycled through exactly this set.
    private static readonly Color[] Win98BezPalette =
    {
        new(255,  85,  85, 255), // bright red
        new( 85, 255,  85, 255), // bright green
        new( 85, 255, 255, 255), // bright cyan
        new(255, 255,  85, 255), // bright yellow
        new(255,  85, 255, 255), // bright magenta
        new( 85,  85, 255, 255), // bright blue
        new(255, 255, 255, 255), // white
    };

    private void DrawBezier(Rectangle r)
    {
        // Win98 "Bezier" screensaver: four control points pinball around
        // the rect, the cubic curve through them is drawn with a slowly
        // changing palette color, and recent frames are kept as fading
        // afterimages so a trail of curves morphs across the panel.
        if (!_bezInit) InitBezier(r);

        // Audio drive: bass speeds the points and the palette cycle, treble
        // adds curve thickness sparkle.
        float bass = _spectrum.Bass;
        float treb = _spectrum.Treble;

        // Step control points each frame — they bounce off the inner rect.
        // dt isn't passed in but DrawVisualizer is called once per frame and
        // the rest of the widget updates vizTime by delta; we approximate
        // dt here from the delta stored on the spectrum's smoothing rate.
        // Practically: at 60fps a fixed 1/60 step looks identical, so:
        const float dt = 1f / 60f;
        float speedScale = 1f + bass * 1.8f;
        float minX = r.X + 4;
        float minY = r.Y + 4;
        float maxX = r.X + r.Width - 4;
        float maxY = r.Y + r.Height - 4;
        for (int i = 0; i < 4; i++)
        {
            _bezPts[i] += _bezVel[i] * dt * speedScale;
            if (_bezPts[i].X < minX) { _bezPts[i].X = minX; _bezVel[i].X = MathF.Abs(_bezVel[i].X); }
            else if (_bezPts[i].X > maxX) { _bezPts[i].X = maxX; _bezVel[i].X = -MathF.Abs(_bezVel[i].X); }
            if (_bezPts[i].Y < minY) { _bezPts[i].Y = minY; _bezVel[i].Y = MathF.Abs(_bezVel[i].Y); }
            else if (_bezPts[i].Y > maxY) { _bezPts[i].Y = maxY; _bezVel[i].Y = -MathF.Abs(_bezVel[i].Y); }
        }

        _bezColorPhase += dt * (0.9f + bass * 1.6f);
        int colorIdx = ((int)_bezColorPhase) % Win98BezPalette.Length;

        // Snapshot current curve into the trail ring.
        for (int i = 0; i < 4; i++) _bezTrail[_bezTrailIdx, i] = _bezPts[i];
        _bezTrailColor[_bezTrailIdx] = colorIdx;
        _bezTrailIdx = (_bezTrailIdx + 1) % BezTrailLen;
        if (_bezTrailIdx == 0) _bezTrailFilled = true;

        // Black backing fills the rect so older app pixels don't bleed
        // through the trail (the visualizer pane is sunken with face color
        // by default; the screensaver wants pitch black).
        Raylib.DrawRectangleRec(r, new Color((byte)0, (byte)0, (byte)0, (byte)255));

        // Walk the trail oldest → newest, drawing each saved curve at
        // increasing alpha. This is the afterimage.
        int count = _bezTrailFilled ? BezTrailLen : _bezTrailIdx;
        Span<Vector2> snap = stackalloc Vector2[4];
        for (int slot = 0; slot < count; slot++)
        {
            int trailPos = (_bezTrailIdx - count + slot + BezTrailLen) % BezTrailLen;
            float age = (count == 1) ? 1f : slot / (float)(count - 1);  // 0=oldest, 1=newest
            // Quadratic ease so the very newest few are clearly brightest.
            float vis = age * age;
            byte alpha = (byte)Math.Clamp((int)(vis * 230 + 12), 0, 240);
            var col = Win98BezPalette[_bezTrailColor[trailPos]];
            var faded = new Color(col.R, col.G, col.B, alpha);
            for (int i = 0; i < 4; i++) snap[i] = _bezTrail[trailPos, i];
            float thick = 1.0f + vis * (1.5f + treb * 1.5f);
            DrawCubicBezier(snap, 32, faded, thick);
        }

        // Highlight the live curve white-hot on top so the leading edge pops.
        var live = new Color((byte)255, (byte)255, (byte)255, (byte)200);
        DrawCubicBezier(_bezPts, 36, live, 1.4f);
    }

    private void InitBezier(Rectangle r)
    {
        var rng = new Random(1337);
        for (int i = 0; i < 4; i++)
        {
            _bezPts[i] = new Vector2(
                r.X + 8 + (float)rng.NextDouble() * (r.Width - 16),
                r.Y + 8 + (float)rng.NextDouble() * (r.Height - 16));
            // Velocity in pixels-per-second. ~60-110 px/sec at idle, plus
            // bass scaling at draw time. Random direction per point.
            float ang = (float)rng.NextDouble() * MathF.PI * 2f;
            float spd = 60f + (float)rng.NextDouble() * 50f;
            _bezVel[i] = new Vector2(MathF.Cos(ang) * spd, MathF.Sin(ang) * spd);
        }
        _bezInit = true;
        _bezTrailIdx = 0;
        _bezTrailFilled = false;
    }

    /// <summary>
    /// Render a cubic bezier through four control points using De Casteljau.
    /// The classic Win98 screensaver used a single cubic Bezier connecting
    /// the four bouncing points end-to-end — that's what we replicate here.
    /// </summary>
    private static void DrawCubicBezier(ReadOnlySpan<Vector2> pts, int segs, Color col, float thick)
    {
        if (pts.Length < 4) return;
        Vector2 p0 = pts[0], p1 = pts[1], p2 = pts[2], p3 = pts[3];
        Vector2 prev = p0;
        for (int s = 1; s <= segs; s++)
        {
            float t = s / (float)segs;
            float u = 1f - t;
            float b0 = u * u * u;
            float b1 = 3f * u * u * t;
            float b2 = 3f * u * t * t;
            float b3 = t * t * t;
            var q = p0 * b0 + p1 * b1 + p2 * b2 + p3 * b3;
            Raylib.DrawLineEx(prev, q, thick, col);
            prev = q;
        }
    }

    private void DrawStars(Rectangle r)
    {
        // Starfield streaming towards the camera. Each star has a stable
        // angle / aspect / radius-modulus, but z-depth advances every frame
        // (faster on bass), so we get the warp-speed look.
        const int n = 140;
        float cx = r.X + r.Width / 2;
        float cy = r.Y + r.Height / 2;
        float maxR = MathF.Min(r.Width, r.Height) * 0.95f;
        float bass = _spectrum.Bass;
        float treble = _spectrum.Treble;
        float speed = 0.18f + bass * 0.95f;

        // Each star's radius advances by `speed` and wraps; we then stretch
        // it into a streak so high speed reads as motion blur.
        float t = _vizTime * speed;
        for (int i = 0; i < n; i++)
        {
            float a = i * 0.318f;                           // pseudo-uniform angle
            float seed = Frac(MathF.Sin(i * 12.9898f) * 43758.55f);
            float depth = Frac(seed + t);                   // 0..1 along the trail
            // Quadratic accel toward the edge — looks like 3D perspective.
            float rr = depth * depth * maxR;
            float px = cx + MathF.Cos(a) * rr;
            float py = cy + MathF.Sin(a) * rr;
            // Streak length grows with speed and depth.
            float streak = 1.2f + speed * 8f * depth;
            float dx = MathF.Cos(a) * streak;
            float dy = MathF.Sin(a) * streak;
            float bright = 0.3f + 0.7f * (depth * depth);
            // Treble tints the leading edge cyan; bass tints magenta — a
            // little color drama that's clearly tied to what's playing.
            byte rcol = (byte)Math.Clamp((int)(bright * (180 + bass * 75)), 0, 255);
            byte gcol = (byte)Math.Clamp((int)(bright * (200 + treble * 55)), 0, 255);
            byte bcol = (byte)Math.Clamp((int)(bright * (220 + treble * 35)), 0, 255);
            var col = new Color(rcol, gcol, bcol, (byte)255);
            Raylib.DrawLineEx(new Vector2(px, py), new Vector2(px + dx, py + dy),
                0.8f + bright * 1.6f, col);
        }
        // Bright vanishing point.
        Raylib.DrawCircleV(new Vector2(cx, cy), 1.5f + bass * 3f,
            new Color((byte)255, (byte)240, (byte)220, (byte)255));
    }

    private static float Frac(float v) { v -= MathF.Floor(v); return v < 0 ? v + 1 : v; }

    // ── Radio-only font system (isolated from RetroSkin / FontManager) ───
    private static readonly string[] RadioFontFiles =
    {
        "VT323.ttf", "ShareTechMono.ttf", "Audiowide.ttf", "Orbitron.ttf",
        "Jersey10.ttf", "DotGothic16.ttf", "PressStart2P.ttf", "Silkscreen.ttf",
        "Tiny5.ttf", "PixelifySans.ttf", "RubikMonoOne.ttf", "W95F.otf",
    };
    private static readonly Dictionary<string, Font> RadioFontCache = new();
    private static int _radioFontIdx;

    private static string CurrentRadioFontName
    {
        get
        {
            string f = RadioFontFiles[_radioFontIdx];
            int dot = f.LastIndexOf('.');
            return dot > 0 ? f[..dot] : f;
        }
    }

    private static Font GetRadioFont()
    {
        string file = RadioFontFiles[_radioFontIdx];
        if (RadioFontCache.TryGetValue(file, out var f)) return f;
        var path = Path.Combine(AppContext.BaseDirectory, "assets/fonts", file);
        if (!File.Exists(path))
        {
            var def = Raylib.GetFontDefault();
            RadioFontCache[file] = def;
            return def;
        }
        var font = Raylib.LoadFontEx(path, 64, null, 0);
        Raylib.SetTextureFilter(font.Texture, TextureFilter.Bilinear);
        RadioFontCache[file] = font;
        return font;
    }

    private static void DrawRadioText(string text, int x, int y, Color col, int size)
    {
        var f = GetRadioFont();
        GlyphFallback.DrawText(f, text, new Vector2(x, y), size, 0, col);
    }

    private static int MeasureRadioText(string text, int size)
    {
        var f = GetRadioFont();
        return (int)GlyphFallback.MeasureText(f, text, size, 0).X;
    }

    private static string TruncateRadioText(string text, int maxWidth, int size)
    {
        if (MeasureRadioText(text, size) <= maxWidth) return text;
        const string ell = "…";
        int ellW = MeasureRadioText(ell, size);
        for (int len = text.Length - 1; len > 0; len--)
        {
            string c = text[..len];
            if (MeasureRadioText(c, size) + ellW <= maxWidth) return c + ell;
        }
        return ell;
    }

    private static void CycleRadioFont() => _radioFontIdx = (_radioFontIdx + 1) % RadioFontFiles.Length;
}
