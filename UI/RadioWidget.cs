using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Data;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.UI;

/// <summary>
/// Free-floating retro radio widget rendered directly on the pet's transparent
/// always-on-top overlay. Body drags around the screen; tuning knob cycles
/// through stations (left = next, right = previous); volume knob raises/lowers
/// the level (top half = up, bottom = down); the red round button is power.
/// The LCD-style display shows the station name + genre, or a setup hint if
/// neither ffplay nor mpv is on PATH.
/// </summary>
public class RadioWidget
{
    public const int W = 232;
    public const int H = 116;
    private const int KnobR = 18;

    public bool Visible;
    public Vector2 Position = new(80, 80);
    public Action? StateChanged;     // fired when something the host wants to persist changed

    private readonly RadioPlayer _player;
    private bool _power;
    private int _stationIdx;
    private float _volume = 0.6f;

    private bool _dragging;
    private Vector2 _dragGrab;

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

    private static Vector2 TuneKnobLocal => new(46, 80);
    private static Vector2 VolKnobLocal => new(110, 80);
    private static Rectangle PowerBtnLocal => new(168, 66, 32, 32);

    /// <summary>Returns true if the click was consumed by the widget.</summary>
    public bool Update(Vector2 mouse, bool leftPressed, bool leftReleased, bool rightPressed)
    {
        if (!Visible) return false;

        if (_dragging)
        {
            Position = mouse - _dragGrab;
            if (leftReleased) { _dragging = false; StateChanged?.Invoke(); }
            return true;
        }

        var local = mouse - Position;
        bool inside = local.X >= 0 && local.X < W && local.Y >= 0 && local.Y < H;
        if (!inside) return false;

        bool onTune = Vector2.Distance(local, TuneKnobLocal) < KnobR + 2;
        bool onVol = Vector2.Distance(local, VolKnobLocal) < KnobR + 2;
        bool onPower = local.X >= PowerBtnLocal.X && local.X < PowerBtnLocal.X + PowerBtnLocal.Width
                    && local.Y >= PowerBtnLocal.Y && local.Y < PowerBtnLocal.Y + PowerBtnLocal.Height;

        if (leftPressed)
        {
            if (onPower) TogglePower();
            else if (onTune) ChangeStation(+1);
            else if (onVol) AdjustVolume(local.Y < VolKnobLocal.Y ? +0.1f : -0.1f);
            else { _dragging = true; _dragGrab = local; }
            return true;
        }
        if (rightPressed)
        {
            if (onTune) ChangeStation(-1);
            else if (onVol) AdjustVolume(-0.1f);
            return true;
        }
        return false;
    }

    private void TogglePower()
    {
        _power = !_power;
        if (_power) PlayCurrent();
        else _player.Stop();
        StateChanged?.Invoke();
    }

    private void ChangeStation(int dir)
    {
        if (RadioStations.All.Count == 0) return;
        int n = RadioStations.All.Count;
        _stationIdx = ((_stationIdx + dir) % n + n) % n;
        if (_power) PlayCurrent();
        StateChanged?.Invoke();
    }

    private void AdjustVolume(float dv)
    {
        _volume = Math.Clamp(_volume + dv, 0, 1);
        if (_power) PlayCurrent();
        StateChanged?.Invoke();
    }

    private void PlayCurrent()
    {
        if (RadioStations.All.Count == 0) return;
        var s = RadioStations.All[_stationIdx];
        _player.Play(s.Url, s.Name, _volume);
    }

    public void Draw()
    {
        if (!Visible) return;

        int x = (int)Position.X, y = (int)Position.Y;

        // Drop shadow
        Raylib.DrawRectangle(x + 4, y + 4, W, H, new Color((byte)0, (byte)0, (byte)0, (byte)100));

        // Wood-grain body with bevel
        Raylib.DrawRectangle(x, y, W, H, new Color((byte)96, (byte)56, (byte)32, (byte)255));
        Raylib.DrawRectangle(x + 4, y + 4, W - 8, H - 8, new Color((byte)156, (byte)100, (byte)56, (byte)255));
        // Highlights
        Raylib.DrawRectangle(x + 4, y + 4, W - 8, 2, new Color((byte)200, (byte)148, (byte)96, (byte)255));
        Raylib.DrawRectangle(x + 4, y + 4, 2, H - 8, new Color((byte)200, (byte)148, (byte)96, (byte)255));
        // Shadows
        Raylib.DrawRectangle(x + 4, y + H - 6, W - 8, 2, new Color((byte)56, (byte)28, (byte)12, (byte)255));
        Raylib.DrawRectangle(x + W - 6, y + 4, 2, H - 8, new Color((byte)56, (byte)28, (byte)12, (byte)255));
        // Outer dark frame
        Raylib.DrawRectangleLines(x, y, W, H, new Color((byte)32, (byte)16, (byte)8, (byte)255));

        // LCD display
        int dispX = x + 12, dispY = y + 10, dispW = W - 24, dispH = 38;
        Raylib.DrawRectangle(dispX - 2, dispY - 2, dispW + 4, dispH + 4, new Color((byte)0, (byte)0, (byte)0, (byte)255));
        Raylib.DrawRectangle(dispX, dispY, dispW, dispH, new Color((byte)20, (byte)40, (byte)16, (byte)255));

        var lcdOn = new Color((byte)80, (byte)240, (byte)80, (byte)255);
        var lcdDim = new Color((byte)56, (byte)96, (byte)56, (byte)255);
        var lcdCol = _power ? lcdOn : lcdDim;

        string mainText, subText;
        if (!_player.BackendAvailable)
        {
            mainText = "no audio backend";
            subText = "install ffmpeg or mpv";
        }
        else if (RadioStations.All.Count == 0)
        {
            mainText = "no stations";
            subText = "";
        }
        else
        {
            var s = RadioStations.All[_stationIdx];
            mainText = s.Name;
            subText = _power ? $"{s.Genre}  ●  ON AIR" : $"{s.Genre}  ●  OFF";
        }

        RetroSkin.DrawText(mainText, dispX + 8, dispY + 4, lcdCol, 16);
        RetroSkin.DrawText(subText, dispX + 8, dispY + 22, lcdCol, 12);

        // Power LED on the display
        if (_power)
        {
            Raylib.DrawCircle(dispX + dispW - 12, dispY + 8, 4, new Color((byte)220, (byte)60, (byte)60, (byte)255));
            Raylib.DrawCircle(dispX + dispW - 13, dispY + 7, 1, new Color((byte)255, (byte)200, (byte)200, (byte)255));
        }

        // Tuning knob
        DrawKnob(x + (int)TuneKnobLocal.X, y + (int)TuneKnobLocal.Y,
            RadioStations.All.Count == 0 ? 0 : _stationIdx / Math.Max(1f, RadioStations.All.Count - 1),
            "TUNE");
        // Volume knob
        DrawKnob(x + (int)VolKnobLocal.X, y + (int)VolKnobLocal.Y, _volume, "VOL");

        // Power button
        var pwrCenter = new Vector2(x + PowerBtnLocal.X + PowerBtnLocal.Width / 2,
                                    y + PowerBtnLocal.Y + PowerBtnLocal.Height / 2);
        Raylib.DrawCircle((int)pwrCenter.X, (int)pwrCenter.Y, 14, new Color((byte)32, (byte)16, (byte)8, (byte)255));
        var pwrBody = _power ? new Color((byte)220, (byte)64, (byte)48, (byte)255)
                             : new Color((byte)96, (byte)32, (byte)24, (byte)255);
        Raylib.DrawCircle((int)pwrCenter.X, (int)pwrCenter.Y, 12, pwrBody);
        Raylib.DrawCircle((int)pwrCenter.X - 2, (int)pwrCenter.Y - 3, 4,
            _power ? new Color((byte)255, (byte)160, (byte)128, (byte)255)
                   : new Color((byte)136, (byte)72, (byte)56, (byte)255));
        // Power glyph
        Raylib.DrawCircleLines((int)pwrCenter.X, (int)pwrCenter.Y, 6,
            _power ? new Color((byte)255, (byte)220, (byte)200, (byte)200)
                   : new Color((byte)40, (byte)16, (byte)8, (byte)200));
        Raylib.DrawRectangle((int)pwrCenter.X - 1, (int)pwrCenter.Y - 8, 2, 6,
            _power ? new Color((byte)255, (byte)220, (byte)200, (byte)255)
                   : new Color((byte)40, (byte)16, (byte)8, (byte)255));

        RetroSkin.DrawText("PWR", (int)pwrCenter.X - 11, (int)pwrCenter.Y + 16,
            new Color((byte)255, (byte)240, (byte)200, (byte)255), 10);
    }

    private static void DrawKnob(int cx, int cy, float t, string label)
    {
        // Outer ring (dark)
        Raylib.DrawCircle(cx, cy, KnobR + 3, new Color((byte)40, (byte)20, (byte)8, (byte)255));
        Raylib.DrawCircle(cx, cy, KnobR + 1, new Color((byte)24, (byte)12, (byte)4, (byte)255));
        // Knob face (warm metal-ish)
        Raylib.DrawCircle(cx, cy, KnobR, new Color((byte)80, (byte)52, (byte)32, (byte)255));
        Raylib.DrawCircle(cx - 3, cy - 4, KnobR - 4, new Color((byte)148, (byte)100, (byte)56, (byte)255));
        // Knurled edge (12 little dimples)
        for (int i = 0; i < 12; i++)
        {
            float a = i / 12f * MathF.PI * 2;
            int dx = (int)(MathF.Cos(a) * (KnobR - 2));
            int dy = (int)(MathF.Sin(a) * (KnobR - 2));
            Raylib.DrawCircle(cx + dx, cy + dy, 1, new Color((byte)20, (byte)10, (byte)4, (byte)255));
        }
        // Indicator notch — sweeps 270° clockwise via top.
        // angDeg = 135 (lower-left) + t * 270 → ends at 405 = 45 (upper-right).
        float angDeg = 135 + Math.Clamp(t, 0, 1) * 270;
        float ang = angDeg * MathF.PI / 180f;
        int nx = cx + (int)(MathF.Cos(ang) * (KnobR - 4));
        int ny = cy + (int)(MathF.Sin(ang) * (KnobR - 4));
        Raylib.DrawLineEx(new Vector2(cx, cy), new Vector2(nx, ny), 2,
            new Color((byte)255, (byte)220, (byte)140, (byte)255));
        Raylib.DrawCircle(nx, ny, 2, new Color((byte)255, (byte)220, (byte)140, (byte)255));

        int lw = RetroSkin.MeasureText(label, 10);
        RetroSkin.DrawText(label, cx - lw / 2, cy + KnobR + 6,
            new Color((byte)255, (byte)240, (byte)200, (byte)255), 10);
    }
}
