using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Faithful port of Spiroplot v2.0 (Don MacFarlane, 1990) from the
/// FUNGAMES category — itself a computerized Spirograph (trademark
/// Kenner products). Mechanics drawn from SPIRO.DOC:
///
///   • A fixed wheel (radius R), an inside moving wheel, and an outside
///     moving wheel; each moving wheel has its own radius and "pencil"
///     offset (the radial distance from the wheel center where the pen
///     is mounted).
///   • Inside wheel rolls inside the fixed circle → hypotrochoid:
///         x = (R-r)cos(t) + d cos((R-r)/r · t)
///         y = (R-r)sin(t) − d sin((R-r)/r · t)
///   • Outside wheel rolls outside the fixed circle → epitrochoid:
///         x = (R+r)cos(t) − d cos((R+r)/r · t)
///         y = (R+r)sin(t) − d sin((R+r)/r · t)
///   • Plot animates over a max-time window; both wheels can run
///     simultaneously or in succession.
///   • Show Wheels toggle visualizes the rolling wheels alongside the
///     pen trace (the doc lists this as a separate per-wheel option).
///
/// We diverge from the original in that all parameters are mouse-driven
/// sliders rather than typed menu entries; the underlying curve math
/// matches the original 1:1.
/// </summary>
public class SpiroplotActivity : IActivity
{
    public Vector2 PanelSize => new(640, 480);
    public bool IsFinished { get; private set; }

    private const int FrameInset = 3;
    private const int SidebarW = 200;
    private const int Pad = 12;
    private const float MaxTime = 80f;          // total parametric duration
    private const float TimeStep = 0.012f;      // increment per draw tick
    private const int MaxPoints = 12000;

    private record Slider(string Label, float Min, float Max, Func<SpiroplotActivity, float> Get,
                          Action<SpiroplotActivity, float> Set);

    private static readonly Slider[] Sliders =
    {
        new("Fixed R",        20,  140, s => s._R,     (s, v) => s._R = v),
        new("Inside r",        4,  100, s => s._rIn,   (s, v) => s._rIn = v),
        new("Inside pen d",    1,  120, s => s._dIn,   (s, v) => s._dIn = v),
        new("Outside r",       4,  100, s => s._rOut,  (s, v) => s._rOut = v),
        new("Outside pen d",   1,  120, s => s._dOut,  (s, v) => s._dOut = v),
    };

    private float _R = 70, _rIn = 24, _dIn = 16, _rOut = 18, _dOut = 26;
    private bool _useInside = true;
    private bool _useOutside = false;
    private bool _showWheels = true;
    private bool _plotting;
    private float _plotTime;
    private readonly List<(Vector2 Pos, Color Col)> _trail = new();
    private int _draggingSlider = -1;
    private int _colorIndex;
    private string _status = "Adjust the wheels, then press Plot.";

    private static readonly Color[] Palette =
    {
        new( 80, 132, 232, 255),    // blue
        new(220,  88, 140, 255),    // pink
        new(108, 196, 116, 255),    // green
        new(228, 168,  72, 255),    // amber
        new(160, 120, 200, 255),    // violet
        new(240, 100,  88, 255),    // red
    };

    public void Load() { /* nothing to load */ }
    public void Close() => IsFinished = true;

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;
        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        { IsFinished = true; return; }

        var menuBar = new Rectangle(FrameInset, FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        var items = new[]
        {
            _plotting ? "Stop" : "Plot",
            "Clear",
            _useInside ? "Inside ✓" : "Inside",
            _useOutside ? "Outside ✓" : "Outside",
            _showWheels ? "Wheels ✓" : "Wheels",
            "Color",
        };
        switch (RetroWidgets.MenuBarHitTest(menuBar, items, local, leftPressed))
        {
            case 0:
                _plotting = !_plotting;
                if (_plotting) { _plotTime = 0; if (_trail.Count > MaxPoints / 2) _trail.Clear(); }
                _status = _plotting ? "Plotting..." : "Stopped.";
                return;
            case 1: _trail.Clear(); _plotTime = 0; _status = "Cleared."; return;
            case 2: _useInside = !_useInside; return;
            case 3: _useOutside = !_useOutside; return;
            case 4: _showWheels = !_showWheels; return;
            case 5:
                _colorIndex = (_colorIndex + 1) % Palette.Length;
                _status = "Pen color " + (_colorIndex + 1) + " of " + Palette.Length;
                return;
        }

        // Slider drag
        if (leftPressed)
        {
            for (int i = 0; i < Sliders.Length; i++)
            {
                if (RetroSkin.PointInRect(local, SliderHitRect(i)))
                {
                    _draggingSlider = i;
                    UpdateSliderFromMouse(i, local);
                    return;
                }
            }
        }
        if (_draggingSlider >= 0)
        {
            if (Raylib.IsMouseButtonDown(MouseButton.Left)) UpdateSliderFromMouse(_draggingSlider, local);
            else _draggingSlider = -1;
        }

        if (_plotting)
        {
            // Advance parametric time and append point(s) to the trail
            // each frame. Multiple steps per frame keep the trace dense
            // even at 60 fps so the curve looks continuous.
            int steps = 4;
            for (int i = 0; i < steps; i++)
            {
                _plotTime += TimeStep;
                AppendTrail();
                if (_plotTime >= MaxTime) { _plotting = false; _status = "Plot complete."; break; }
            }
        }
    }

    private void AppendTrail()
    {
        if (_useInside)
        {
            var p = Hypotrochoid(_R, _rIn, _dIn, _plotTime);
            if (_trail.Count < MaxPoints) _trail.Add((p, Palette[_colorIndex]));
        }
        if (_useOutside)
        {
            var p = Epitrochoid(_R, _rOut, _dOut, _plotTime);
            // Use the next palette color for the outside pen so the two
            // traces are distinguishable when both run simultaneously.
            if (_trail.Count < MaxPoints) _trail.Add((p, Palette[(_colorIndex + 2) % Palette.Length]));
        }
    }

    private static Vector2 Hypotrochoid(float R, float r, float d, float t)
    {
        if (r <= 0.01f) return Vector2.Zero;
        float k = (R - r) / r;
        float x = (R - r) * MathF.Cos(t) + d * MathF.Cos(k * t);
        float y = (R - r) * MathF.Sin(t) - d * MathF.Sin(k * t);
        return new Vector2(x, y);
    }

    private static Vector2 Epitrochoid(float R, float r, float d, float t)
    {
        if (r <= 0.01f) return Vector2.Zero;
        float k = (R + r) / r;
        float x = (R + r) * MathF.Cos(t) - d * MathF.Cos(k * t);
        float y = (R + r) * MathF.Sin(t) - d * MathF.Sin(k * t);
        return new Vector2(x, y);
    }

    private Rectangle SidebarRect()
    {
        float top = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 4;
        float bottom = PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight - 4;
        return new Rectangle(FrameInset + 6, top, SidebarW, bottom - top);
    }

    private Rectangle CanvasRect()
    {
        var sb = SidebarRect();
        return new Rectangle(sb.X + sb.Width + 8, sb.Y,
            PanelSize.X - FrameInset - 6 - (sb.X + sb.Width + 8),
            sb.Height);
    }

    private Rectangle SliderHitRect(int i)
    {
        var sb = SidebarRect();
        float y = sb.Y + 16 + i * 56 + 22;
        return new Rectangle(sb.X + 12, y - 6, sb.Width - 24, 18);
    }

    private void UpdateSliderFromMouse(int i, Vector2 local)
    {
        var r = SliderHitRect(i);
        float t = Math.Clamp((local.X - r.X) / r.Width, 0f, 1f);
        var s = Sliders[i];
        s.Set(this, s.Min + t * (s.Max - s.Min));
        _status = $"{s.Label} = {s.Get(this):F1}";
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Spiroplot", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        var items = new[]
        {
            _plotting ? "Stop" : "Plot",
            "Clear",
            _useInside ? "Inside ✓" : "Inside",
            _useOutside ? "Outside ✓" : "Outside",
            _showWheels ? "Wheels ✓" : "Wheels",
            "Color",
        };
        RetroWidgets.MenuBarVisual(menuBar, items, -1);

        DrawSidebar(panelOffset);
        DrawCanvas(panelOffset);

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(status, _status,
            $"t={_plotTime:F1}/{MaxTime:F0}  pts={_trail.Count}");
    }

    private void DrawSidebar(Vector2 panelOffset)
    {
        var sb = SidebarRect();
        var sbAbs = new Rectangle(panelOffset.X + sb.X, panelOffset.Y + sb.Y, sb.Width, sb.Height);
        RetroSkin.DrawSunken(sbAbs, RetroSkin.SunkenBg);

        for (int i = 0; i < Sliders.Length; i++)
        {
            var s = Sliders[i];
            float baseY = sbAbs.Y + 16 + i * 56;
            FontManager.DrawText($"{s.Label}: {s.Get(this):F1}",
                (int)(sbAbs.X + 12), (int)baseY, 14, RetroSkin.BodyText);
            var trk = new Rectangle(sbAbs.X + 12, baseY + 22, sb.Width - 24, 6);
            RetroSkin.DrawSunken(trk, RetroSkin.SunkenBg);
            float frac = (s.Get(this) - s.Min) / (s.Max - s.Min);
            var thumb = new Rectangle(trk.X + frac * (trk.Width - 10) - 2, trk.Y - 4, 10, 14);
            RetroSkin.DrawRaised(thumb);
        }

        // Tip below the sliders explaining the math model.
        const string tip = "Inside = hypotrochoid;\nOutside = epitrochoid";
        int y = (int)(sbAbs.Y + sb.Height - 44);
        foreach (var line in tip.Split('\n'))
        {
            FontManager.DrawText(line, (int)(sbAbs.X + 12), y, 12, RetroSkin.DisabledText);
            y += 16;
        }
    }

    private void DrawCanvas(Vector2 panelOffset)
    {
        var c = CanvasRect();
        var cAbs = new Rectangle(panelOffset.X + c.X, panelOffset.Y + c.Y, c.Width, c.Height);
        RetroSkin.DrawSunken(cAbs, new Color((byte)252, (byte)248, (byte)240, (byte)255));

        float cx = cAbs.X + cAbs.Width / 2f;
        float cy = cAbs.Y + cAbs.Height / 2f;

        // Trail. DrawLineEx between consecutive trail points keeps the
        // curve visible even when individual segments are sub-pixel.
        for (int i = 1; i < _trail.Count; i++)
        {
            var a = _trail[i - 1];
            var b = _trail[i];
            if (a.Col.R != b.Col.R || a.Col.G != b.Col.G || a.Col.B != b.Col.B) continue;
            Raylib.DrawLineEx(
                new Vector2(cx + a.Pos.X, cy + a.Pos.Y),
                new Vector2(cx + b.Pos.X, cy + b.Pos.Y),
                1.5f, b.Col);
        }

        // Wheels visualization — only while plotting and the toggle is on.
        if (_showWheels && _plotting)
        {
            // Fixed wheel
            Raylib.DrawCircleLines((int)cx, (int)cy, _R, new Color((byte)160, (byte)160, (byte)170, (byte)200));

            if (_useInside)
            {
                // Center of the rolling wheel sits on a circle of radius (R-r) from center.
                float wcx = cx + (_R - _rIn) * MathF.Cos(_plotTime);
                float wcy = cy + (_R - _rIn) * MathF.Sin(_plotTime);
                Raylib.DrawCircleLines((int)wcx, (int)wcy, _rIn, new Color((byte)80, (byte)132, (byte)232, (byte)220));
                var pen = Hypotrochoid(_R, _rIn, _dIn, _plotTime);
                Raylib.DrawLineEx(new Vector2(wcx, wcy),
                    new Vector2(cx + pen.X, cy + pen.Y), 1f,
                    new Color((byte)80, (byte)132, (byte)232, (byte)220));
            }
            if (_useOutside)
            {
                float wcx = cx + (_R + _rOut) * MathF.Cos(_plotTime);
                float wcy = cy + (_R + _rOut) * MathF.Sin(_plotTime);
                Raylib.DrawCircleLines((int)wcx, (int)wcy, _rOut, new Color((byte)108, (byte)196, (byte)116, (byte)220));
                var pen = Epitrochoid(_R, _rOut, _dOut, _plotTime);
                Raylib.DrawLineEx(new Vector2(wcx, wcy),
                    new Vector2(cx + pen.X, cy + pen.Y), 1f,
                    new Color((byte)108, (byte)196, (byte)116, (byte)220));
            }
        }
    }
}
