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
    private bool _power;
    private int _stationIdx;
    private float _volume = 0.6f;
    private int _vizMode;          // 0..5
    private const int VizModeCount = 6;

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
    private const int StationRowH = 30;
    private const int NowPlayingH = 22;
    private const int TapeRowH = 50;
    private const int BottomRowH = 26;
    private const int BtnW = 30;
    private const int PowerW = 56;
    private const int LabelW = 32;
    private const int WheelD = 44;       // wheel diameter
    private const int RecW = 36;

    private static Rectangle TitleBarLocal   => new(0, 0, W, TitleH);
    private static Rectangle VizLocal        => new(Pad, TitleH + 4, W - Pad * 2, VizH);
    private static Rectangle StationRowLocal => new(Pad, VizLocal.Y + VizLocal.Height + Pad, W - Pad * 2, StationRowH);
    private static Rectangle PrevBtnLocal    => new(StationRowLocal.X, StationRowLocal.Y, BtnW, StationRowH);
    private static Rectangle NextBtnLocal    => new(StationRowLocal.X + StationRowLocal.Width - BtnW, StationRowLocal.Y, BtnW, StationRowH);
    private static Rectangle StationLcdLocal
        => new(PrevBtnLocal.X + PrevBtnLocal.Width + 4, StationRowLocal.Y,
               StationRowLocal.Width - BtnW * 2 - 8, StationRowH);
    private static Rectangle NowPlayingLocal => new(Pad, StationRowLocal.Y + StationRowH + 4, W - Pad * 2, NowPlayingH);
    private static Rectangle TapeRowLocal    => new(Pad, NowPlayingLocal.Y + NowPlayingH + Pad, W - Pad * 2, TapeRowH);

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
        _spectrum.Tick(delta, _power && _player.IsPlaying);
        _vizTime += delta;

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

        // Title bar: X first, then drag-anywhere-else.
        if (RetroSkin.PointInRect(local, TitleBarLocal))
        {
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

        // Visualizer pane (sunken)
        var viz = new Rectangle(x + VizLocal.X, y + VizLocal.Y, VizLocal.Width, VizLocal.Height);
        RetroSkin.DrawSunken(viz, fill: new Color((byte)6, (byte)8, (byte)12, (byte)255));
        DrawVisualizer(viz);
        DrawVizModeBadge((int)viz.X + (int)viz.Width - 32, (int)viz.Y + 4);

        var lcdCol = _power ? new Color((byte)80, (byte)240, (byte)80, (byte)255)
                            : new Color((byte)56, (byte)96, (byte)56, (byte)255);

        // Station row
        var prev = new Rectangle(x + PrevBtnLocal.X, y + PrevBtnLocal.Y, PrevBtnLocal.Width, PrevBtnLocal.Height);
        var next = new Rectangle(x + NextBtnLocal.X, y + NextBtnLocal.Y, NextBtnLocal.Width, NextBtnLocal.Height);
        var slcd = new Rectangle(x + StationLcdLocal.X, y + StationLcdLocal.Y, StationLcdLocal.Width, StationLcdLocal.Height);
        RetroWidgets.ButtonVisual(prev, "<<", _prevArmed);
        RetroWidgets.ButtonVisual(next, ">>", _nextArmed);
        RetroSkin.DrawSunken(slcd, fill: new Color((byte)20, (byte)40, (byte)16, (byte)255));
        string stationName = StationLine();
        const int stationFont = 20;
        string stationFitted = RetroWidgets.TruncateToWidth(stationName, (int)slcd.Width - 12, stationFont);
        int sw = RetroSkin.MeasureText(stationFitted, stationFont);
        int sx = (int)(slcd.X + (slcd.Width - sw) / 2);
        int sy = (int)(slcd.Y + (slcd.Height - stationFont) / 2 - 1);
        RetroSkin.DrawText(stationFitted, sx, sy, lcdCol, stationFont);

        // Now-playing strip (static): artist — title when known, else genre.
        var nowR = new Rectangle(x + NowPlayingLocal.X, y + NowPlayingLocal.Y, NowPlayingLocal.Width, NowPlayingLocal.Height);
        RetroSkin.DrawSunken(nowR, fill: new Color((byte)8, (byte)20, (byte)28, (byte)255));
        string nowText = NowPlayingLine();
        const int nowFont = 14;
        string nowFitted = RetroWidgets.TruncateToWidth(nowText, (int)nowR.Width - 12, nowFont);
        int nw = RetroSkin.MeasureText(nowFitted, nowFont);
        int nx = (int)(nowR.X + (nowR.Width - nw) / 2);
        int ny = (int)(nowR.Y + (nowR.Height - nowFont) / 2);
        RetroSkin.DrawText(nowFitted, nx, ny, lcdCol, nowFont);

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
        const int tapeFont = 12;
        int tw = RetroSkin.MeasureText(tapeText, tapeFont);
        int tx = (int)(lcd.X + (lcd.Width - tw) / 2);
        int ty = (int)(lcd.Y + (lcd.Height - tapeFont) / 2 - 1);
        var tcol = tapeAvailable ? lcdCol : new Color((byte)56, (byte)96, (byte)56, (byte)180);
        RetroSkin.DrawText(tapeText, tx, ty, tcol, tapeFont);

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
        var s = RadioStations.All[_stationIdx];
        if (_power && _meta.HasTrack)
        {
            string track = string.IsNullOrEmpty(_meta.CurrentArtist)
                ? _meta.CurrentTitle
                : $"{_meta.CurrentArtist} — {_meta.CurrentTitle}";
            if (!string.IsNullOrWhiteSpace(track))
                return "♪ " + track;
        }
        return s.Genre.ToUpperInvariant();
    }

    private string StationLine()
    {
        if (!_player.BackendAvailable) return "no audio backend";
        if (RadioStations.All.Count == 0) return "no stations";
        return RadioStations.All[_stationIdx].Name;
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
            _ => "STARS",
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
            default: DrawStars(r); break;
        }
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
        int barW = Math.Max(1, half / (n + 1));
        int midX = (int)r.X + (int)r.Width / 2;
        int midY = (int)r.Y + (int)r.Height / 2;
        int maxLen = (int)r.Height / 2 - 2;

        for (int i = 0; i < n; i++)
        {
            float bar = _spectrum.Bar(i);
            int len = (int)(bar * maxLen);
            byte br = (byte)(80 + (int)(bar * 175));
            var c = new Color((byte)(80 + i * 6), br, (byte)(220 - i * 6), (byte)255);
            int rx = midX + i * (barW + 1) + 1;
            int lx = midX - (i + 1) * (barW + 1) + 1;
            Raylib.DrawRectangle(rx, midY - len, barW, len * 2, c);
            Raylib.DrawRectangle(lx, midY - len, barW, len * 2, c);
        }
        Raylib.DrawRectangle((int)r.X + 2, midY, (int)r.Width - 4, 1,
            new Color((byte)40, (byte)60, (byte)80, (byte)180));
    }

    private void DrawWave(Rectangle r)
    {
        int samples = (int)r.Width - 4;
        if (samples < 4) return;
        int midY = (int)r.Y + (int)r.Height / 2;
        int amp = (int)r.Height / 2 - 4;
        var col = new Color((byte)80, (byte)240, (byte)160, (byte)255);

        Vector2 prev = new Vector2((int)r.X + 2, midY);
        for (int i = 1; i < samples; i++)
        {
            float u = (float)i / samples;
            float v = MathF.Sin(u * MathF.PI * 4 + _vizTime * 6f) * _spectrum.Bar(2)
                    + MathF.Sin(u * MathF.PI * 9 + _vizTime * 4f) * _spectrum.Bar(8) * 0.7f
                    + MathF.Sin(u * MathF.PI * 16 + _vizTime * 9f) * _spectrum.Bar(14) * 0.4f;
            var p = new Vector2((int)r.X + 2 + i, midY + (int)(v * amp * 0.8f));
            Raylib.DrawLineEx(prev, p, 2, col);
            prev = p;
        }
        Raylib.DrawRectangle((int)r.X + 2, midY, (int)r.Width - 4, 1,
            new Color((byte)40, (byte)80, (byte)60, (byte)120));
    }

    private void DrawRadial(Rectangle r)
    {
        int n = _spectrum.BandCount;
        float cx = r.X + r.Width / 2;
        float cy = r.Y + r.Height / 2;
        float baseR = MathF.Min(r.Width, r.Height) * 0.18f;
        float maxLen = MathF.Min(r.Width, r.Height) * 0.32f;
        float spin = _vizTime * 0.8f;

        for (int i = 0; i < n; i++)
        {
            float a = i / (float)n * MathF.PI * 2 + spin;
            float bar = _spectrum.Bar(i);
            float len = baseR + bar * maxLen;
            var p1 = new Vector2(cx + MathF.Cos(a) * baseR, cy + MathF.Sin(a) * baseR);
            var p2 = new Vector2(cx + MathF.Cos(a) * len,   cy + MathF.Sin(a) * len);
            byte g = (byte)(80 + (int)(bar * 175));
            var c = new Color((byte)(60 + i * 8), g, (byte)(220 - i * 6), (byte)255);
            Raylib.DrawLineEx(p1, p2, 2, c);
        }
        Raylib.DrawCircleLines((int)cx, (int)cy, baseR, new Color((byte)80, (byte)120, (byte)160, (byte)160));
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
        int n = 60;
        float cx = r.X + r.Width / 2;
        float cy = r.Y + r.Height / 2;
        float maxR = MathF.Min(r.Width, r.Height) * 0.45f;
        float beat = 0;
        for (int b = 0; b < _spectrum.BandCount; b++) beat += _spectrum.Bar(b);
        beat /= _spectrum.BandCount;
        for (int i = 0; i < n; i++)
        {
            float fi = i;
            float a = fi * 0.7853f + _vizTime * 0.6f;
            float rr = (0.2f + 0.8f * Frac(MathF.Sin(fi * 12.9898f) * 43758.55f))
                       * maxR
                       * (0.6f + 0.6f * beat);
            float px = cx + MathF.Cos(a) * rr;
            float py = cy + MathF.Sin(a) * rr * 0.7f;
            float bright = 0.4f + 0.6f * _spectrum.Bar(i % _spectrum.BandCount);
            byte v = (byte)Math.Clamp((int)(bright * 255), 0, 255);
            var c = new Color(v, (byte)(v * 0.9f), (byte)Math.Min(255, v + 40), (byte)255);
            Raylib.DrawCircle((int)px, (int)py, 1.5f + bright * 1.5f, c);
        }
    }

    private static float Frac(float v) { v -= MathF.Floor(v); return v < 0 ? v + 1 : v; }
}
