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
    // Comet trail buffer (persists across frames so the trail is real)
    private readonly Vector2[] _cometTrail = new Vector2[60];
    private int _cometTrailIdx;
    private bool _cometTrailInit;

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
    private static Rectangle TapeLcdLocal
    {
        get
        {
            var row = TapeRowLocal;
            int x = (int)(RecBtnLocal.X + RecBtnLocal.Width + 6);
            int rgt = (int)WheelLocal.X - 6;
            int y = (int)(row.Y + (row.Height - 18) / 2);
            return new Rectangle(x, y, Math.Max(8, rgt - x), 18);
        }
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

    public bool Update(float delta, Vector2 mouse, bool leftPressed, bool leftReleased, bool rightPressed)
    {
        if (!Visible) return false;
        if (_power) _meta.Tick();
        bool playing = _power && _player.IsPlaying;
        bool haveLive = playing && _player.CopyRecentMono(_spectrumSamples);
        _spectrum.Update(delta,
            haveLive ? _spectrumSamples : null,
            haveLive ? _player.SampleRate : 0,
            playing);
        _vizTime += delta;

        // Detect now-playing changes for the slide/flash animation.
        string currentTrack = NowPlayingLine();
        if (currentTrack != _displayedTrack)
        {
            // Don't animate the very first paint (when we go from "" to something).
            if (!string.IsNullOrEmpty(_displayedTrack)) _songChangeAnim = 1.0f;
            _prevDisplayedTrack = _displayedTrack;
            _displayedTrack = currentTrack;
        }
        if (_songChangeAnim > 0)
            _songChangeAnim = Math.Max(0, _songChangeAnim - delta * 1.7f);

        // Wheel pointer drift back to neutral once released.
        if (!_wheelDragging)
        {
            // Decay velocity towards 1.0 (live-ish) over ~0.4 s; snap once we're
            // close so the playhead reads at integer steps and we stop doing
            // unnecessary fractional resampling (which adds audible quant noise).
            double v = _player.Velocity;
            double target = 1.0;
            double k = 1.0 - MathF.Exp(-delta / 0.18f);
            double next = v + (target - v) * k;
            if (Math.Abs(next - 1.0) < 0.005) next = 1.0;
            _player.Velocity = next;
            // Pointer naturally spins with current velocity as a slow tick.
            _wheelAngle += (float)_player.Velocity * delta * 2.4f;
        }

        // Continue active wheel drag even if the cursor leaves the widget.
        if (_wheelDragging)
        {
            UpdateWheelDrag(mouse);
            if (leftReleased) _wheelDragging = false;
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
                Visible = false;
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
        }

        if (rightPressed && RetroSkin.PointInRect(local, StationLcdLocal))
        {
            ChangeStation(-1);
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

        // Tape readout LCD: shows TAPE -1.4s / LIVE / REC + duration / FF / RW
        var lcd = new Rectangle(x + TapeLcdLocal.X, y + TapeLcdLocal.Y, TapeLcdLocal.Width, TapeLcdLocal.Height);
        RetroSkin.DrawSunken(lcd, fill: new Color((byte)8, (byte)20, (byte)28, (byte)255));
        string tapeText = TapeStatusText();
        const int tapeFont = 13;
        int tw = MeasureRadioText(tapeText, tapeFont);
        int tx = (int)(lcd.X + (lcd.Width - tw) / 2);
        int ty = (int)(lcd.Y + (lcd.Height - tapeFont) / 2 - 1);
        var tcol = tapeAvailable ? lcdCol : new Color((byte)56, (byte)96, (byte)56, (byte)180);
        DrawRadioText(tapeText, tx, ty, tcol, tapeFont);

        // Wheel
        var wheelRect = new Rectangle(x + WheelLocal.X, y + WheelLocal.Y, WheelD, WheelD);
        DrawWheel(wheelRect, tapeActive);
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

    private string TapeStatusText()
    {
        if (!_player.SupportsTape) return "no tape — install ffmpeg";
        if (!_power || !_player.IsPlaying) return "TAPE READY";
        if (_player.IsRecording) return "● REC";
        double behind = _player.PlayheadSecondsAgo;
        if (_player.IsLive || behind < 0.2) return "LIVE";
        double v = _player.Velocity;
        if (Math.Abs(v - 1.0) > 0.05)
            return $"TAPE -{behind:0.0}s  {v:+0.0;-0.0}×";
        return $"TAPE -{behind:0.0}s";
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
        if (_songChangeAnim <= 0 || string.IsNullOrEmpty(_prevDisplayedTrack))
        {
            DrawNowPlayingText(r, _displayedTrack, baseCol, 0, font);
            return;
        }

        // Slide + colour-flash transition. prog 0→1; smoothstep eased.
        float t = _songChangeAnim;
        float prog = 1f - t;
        float ease = prog * prog * (3f - 2f * prog);
        int slideH = (int)r.Height + 4;
        int oldOff = -(int)(ease * slideH);             // old text slides up & out
        int newOff = (int)((1f - ease) * slideH);       // new text slides up into place
        byte boost = (byte)((1f - ease) * 130);
        var hot = new Color(
            (byte)Math.Min(255, baseCol.R + boost),
            (byte)Math.Min(255, baseCol.G + boost),
            (byte)Math.Min(255, baseCol.B + boost),
            baseCol.A);

        // Clip to LCD interior so glyphs don't bleed onto the chrome.
        var dpi = Raylib.GetWindowScaleDPI();
        Raylib.BeginScissorMode(
            (int)((r.X + 2) * dpi.X),
            (int)((r.Y + 2) * dpi.Y),
            (int)((r.Width - 4) * dpi.X),
            (int)((r.Height - 4) * dpi.Y));
        DrawNowPlayingText(r, _prevDisplayedTrack, baseCol, oldOff, font);
        DrawNowPlayingText(r, _displayedTrack, hot, newOff, font);
        Raylib.EndScissorMode();
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
        // Receding rotating polygons converging to a vanishing point.
        var c = new Vector2(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        int rings = 16;
        float t = _vizTime;
        float beat = AvgBeat();
        float maxR = MathF.Min(r.Width, r.Height) * 0.55f;
        const int sides = 8;
        Span<Vector2> pts = stackalloc Vector2[sides + 1];
        for (int i = rings - 1; i >= 0; i--)
        {
            float depth = ((i + t * 1.6f) % rings) / rings;
            float radius = depth * depth * maxR * (1f + beat * 0.35f);
            float angle = depth * 7f + t * (0.4f + beat * 0.6f);
            for (int k = 0; k <= sides; k++)
            {
                float a = angle + k * (MathF.PI * 2f / sides);
                pts[k] = c + new Vector2(MathF.Cos(a) * radius, MathF.Sin(a) * radius * 0.72f);
            }
            byte br = (byte)(60 + (1f - depth) * 195);
            var col = new Color((byte)(br / 2 + 30), br, (byte)(255 - br / 2), (byte)255);
            float thick = 1f + (1f - depth) * 1.6f;
            for (int k = 0; k < sides; k++)
                Raylib.DrawLineEx(pts[k], pts[k + 1], thick, col);
        }
        // Bright pinprick at the vanishing point
        Raylib.DrawCircleV(c, 2f + beat * 4f, new Color((byte)255, (byte)240, (byte)200, (byte)255));
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

    private void DrawBezier(Rectangle r)
    {
        const int CP = 5;
        Span<Vector2> pts = stackalloc Vector2[CP];
        int n = _spectrum.BandCount;
        float midY = r.Y + r.Height / 2;
        float amp = r.Height / 2 - 6;
        for (int i = 0; i < CP; i++)
        {
            float u = (CP == 1) ? 0.5f : (float)i / (CP - 1);
            int band = (int)(u * (n - 1));
            float wob = MathF.Sin(_vizTime * (1.4f + i * 0.6f) + i * 1.3f) * 0.4f;
            float v = (_spectrum.Bar(band) - 0.5f) * 1.6f + wob * _spectrum.Bar(band);
            pts[i] = new Vector2(r.X + 6 + u * (r.Width - 12),
                                 midY + v * amp);
        }
        var glow = new Color((byte)128, (byte)80, (byte)200, (byte)90);
        DrawCatmullRom(pts, 32, glow, 6);
        var main = new Color((byte)200, (byte)160, (byte)255, (byte)255);
        DrawCatmullRom(pts, 32, main, 2);
        for (int i = 0; i < CP; i++)
            Raylib.DrawCircleV(pts[i], 3, new Color((byte)255, (byte)220, (byte)255, (byte)255));
    }

    private static void DrawCatmullRom(ReadOnlySpan<Vector2> pts, int segs, Color col, float thick)
    {
        if (pts.Length < 2) return;
        Vector2 prev = pts[0];
        for (int i = 0; i < pts.Length - 1; i++)
        {
            Vector2 p0 = i == 0 ? pts[0] : pts[i - 1];
            Vector2 p1 = pts[i];
            Vector2 p2 = pts[i + 1];
            Vector2 p3 = i + 2 < pts.Length ? pts[i + 2] : pts[^1];
            for (int s = 1; s <= segs; s++)
            {
                float t = s / (float)segs;
                float t2 = t * t;
                float t3 = t2 * t;
                Vector2 q = 0.5f * (
                    (2f * p1)
                    + (-p0 + p2) * t
                    + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                    + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
                Raylib.DrawLineEx(prev, q, thick, col);
                prev = q;
            }
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
