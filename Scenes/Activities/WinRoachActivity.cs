using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// WinRoach — port of FUNGAMES/ROACH (New Generation Software, 1991,
/// WinRoach 1.0). Mechanics drawn from WINROACH.DOC:
///
///   • Roaches "scurry across the screen and hide under windows."
///   • Closing or moving a window may expose roaches; they scramble
///     to find a new window to hide under.
///   • "Use the mouse to 'squash' and 'extermidate' the pesky roaches."
///   • Configurable initial count and speed via command-line args:
///         winroach 8 /s 50
///
/// Rather than infest the user's whole desktop (which would fight with
/// the rest of the pet UI), we put the kitchen inside an activity panel.
/// "Hiding under windows" is reinterpreted as roaches dimming when they
/// walk behind the drawn-in fridge / cabinets / boxes. Click-to-squash
/// leaves a splat decal for a few seconds before fading out.
/// </summary>
public class WinRoachActivity : IActivity
{
    public Vector2 PanelSize => new(600, 420);
    public bool IsFinished { get; private set; }

    private const int FrameInset = 3;
    private const int MaxRoaches = 24;
    private const float SplatLifetime = 4f;

    private class Roach
    {
        public Vector2 Pos;
        public float HeadingDeg;
        public float Speed;
        public float Size;
        public float ChangeDirIn;
        public bool Hidden;        // currently overlapping furniture
    }

    private record Splat(Vector2 Pos, float Born);

    private readonly List<Roach> _roaches = new();
    private readonly List<Splat> _splats = new();
    private readonly List<Rectangle> _furniture = new();
    private readonly Random _rng = new();
    private int _targetCount = 8;
    private float _speedMul = 1f;
    private int _squashed;
    private float _now;
    private string _status = "Click roaches to squash them!";

    public void Load()
    {
        // Furniture rectangles in panel-local coords. Roaches hide (dim)
        // when their position falls inside one of these so the original's
        // "hide under windows" behaviour reads visually even though we're
        // not actually layering over OS windows.
        BuildFurniture();
        for (int i = 0; i < _targetCount; i++) _roaches.Add(SpawnRoach(initial: true));
    }

    public void Close() => IsFinished = true;

    private void BuildFurniture()
    {
        _furniture.Clear();
        // Cabinets along the top
        _furniture.Add(new Rectangle(20, 50, 140, 60));
        _furniture.Add(new Rectangle(170, 50, 90, 60));
        _furniture.Add(new Rectangle(270, 50, 160, 60));
        // Fridge — tall on the right
        _furniture.Add(new Rectangle(450, 50, 100, 200));
        // A box on the floor
        _furniture.Add(new Rectangle(80, 280, 90, 60));
        // Sink basin
        _furniture.Add(new Rectangle(220, 290, 140, 50));
    }

    private Rectangle FloorRect()
    {
        float top = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 4;
        float bottom = PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight - 4;
        return new Rectangle(FrameInset + 6, top,
            PanelSize.X - 2 * FrameInset - 12, bottom - top);
    }

    private Roach SpawnRoach(bool initial)
    {
        var floor = FloorRect();
        Vector2 pos;
        if (initial)
        {
            pos = new Vector2(
                floor.X + (float)_rng.NextDouble() * floor.Width,
                floor.Y + (float)_rng.NextDouble() * floor.Height);
        }
        else
        {
            // Spawn from a random edge so it reads as "another roach
            // crawled in from somewhere" rather than popping into existence.
            int edge = _rng.Next(4);
            pos = edge switch
            {
                0 => new Vector2(floor.X - 8, floor.Y + (float)_rng.NextDouble() * floor.Height),
                1 => new Vector2(floor.X + floor.Width + 8, floor.Y + (float)_rng.NextDouble() * floor.Height),
                2 => new Vector2(floor.X + (float)_rng.NextDouble() * floor.Width, floor.Y - 8),
                _ => new Vector2(floor.X + (float)_rng.NextDouble() * floor.Width, floor.Y + floor.Height + 8),
            };
        }
        return new Roach
        {
            Pos = pos,
            HeadingDeg = (float)_rng.NextDouble() * 360f,
            Speed = 36 + (float)_rng.NextDouble() * 48,
            Size = 10 + (float)_rng.NextDouble() * 4,
            ChangeDirIn = 0.6f + (float)_rng.NextDouble() * 1.6f,
        };
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        _now += delta;

        var local = mousePos - panelOffset;
        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        { IsFinished = true; return; }

        var menuBar = new Rectangle(FrameInset, FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        var items = new[]
        {
            "+ Roach",
            "- Roach",
            _speedMul < 1.5f ? "Speed: Slow" : _speedMul < 2.5f ? "Speed: Med" : "Speed: Fast",
            "Sweep All",
        };
        switch (RetroWidgets.MenuBarHitTest(menuBar, items, local, leftPressed))
        {
            case 0:
                _targetCount = Math.Min(MaxRoaches, _targetCount + 2);
                _status = $"Releasing roaches... {_targetCount} target.";
                return;
            case 1:
                _targetCount = Math.Max(0, _targetCount - 2);
                _status = $"Roach budget cut to {_targetCount}.";
                return;
            case 2:
                _speedMul = _speedMul < 1.5f ? 2f : _speedMul < 2.5f ? 3f : 1f;
                _status = "Speed " + (_speedMul < 1.5f ? "slow" : _speedMul < 2.5f ? "medium" : "fast") + ".";
                return;
            case 3:
                int swept = _roaches.Count;
                foreach (var r in _roaches) _splats.Add(new Splat(r.Pos, _now));
                _roaches.Clear();
                _squashed += swept;
                _status = $"Sweep! {swept} squashed.";
                return;
        }

        // Tick roaches
        var floor = FloorRect();
        foreach (var r in _roaches)
        {
            r.ChangeDirIn -= delta;
            if (r.ChangeDirIn <= 0)
            {
                r.HeadingDeg += (float)(_rng.NextDouble() - 0.5) * 90f;
                r.ChangeDirIn = 0.4f + (float)_rng.NextDouble() * 1.2f;
            }
            float rad = r.HeadingDeg * MathF.PI / 180f;
            r.Pos += new Vector2(MathF.Cos(rad), MathF.Sin(rad)) * r.Speed * _speedMul * delta;

            // Bounce off the floor rect edges so they don't wander off.
            if (r.Pos.X < floor.X + 4) { r.Pos.X = floor.X + 4; r.HeadingDeg = 180 - r.HeadingDeg; }
            if (r.Pos.X > floor.X + floor.Width - 4) { r.Pos.X = floor.X + floor.Width - 4; r.HeadingDeg = 180 - r.HeadingDeg; }
            if (r.Pos.Y < floor.Y + 4) { r.Pos.Y = floor.Y + 4; r.HeadingDeg = -r.HeadingDeg; }
            if (r.Pos.Y > floor.Y + floor.Height - 4) { r.Pos.Y = floor.Y + floor.Height - 4; r.HeadingDeg = -r.HeadingDeg; }

            r.Hidden = false;
            foreach (var f in _furniture)
                if (RetroSkin.PointInRect(r.Pos, f)) { r.Hidden = true; break; }
        }

        // Spawn back up to target count gradually.
        if (_roaches.Count < _targetCount && _rng.NextDouble() < delta * 0.7)
            _roaches.Add(SpawnRoach(initial: false));

        // Squash on click.
        if (leftPressed)
        {
            for (int i = _roaches.Count - 1; i >= 0; i--)
            {
                var r = _roaches[i];
                if (r.Hidden) continue;
                if (Vector2.Distance(local, r.Pos) <= r.Size * 1.2f)
                {
                    _splats.Add(new Splat(r.Pos, _now));
                    _roaches.RemoveAt(i);
                    _squashed++;
                    _status = $"Got one. Total squashed: {_squashed}.";
                    break;
                }
            }
        }

        // Reap stale splats.
        _splats.RemoveAll(s => _now - s.Born > SplatLifetime);
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "WinRoach", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        var items = new[]
        {
            "+ Roach",
            "- Roach",
            _speedMul < 1.5f ? "Speed: Slow" : _speedMul < 2.5f ? "Speed: Med" : "Speed: Fast",
            "Sweep All",
        };
        RetroWidgets.MenuBarVisual(menuBar, items, -1);

        var floor = FloorRect();
        var floorAbs = new Rectangle(panelOffset.X + floor.X, panelOffset.Y + floor.Y,
            floor.Width, floor.Height);
        // Linoleum floor — pale yellow with a tile grid so it reads as a
        // kitchen / utility room rather than a generic empty panel.
        Raylib.DrawRectangleRec(floorAbs, new Color((byte)244, (byte)232, (byte)196, (byte)255));
        Raylib.DrawRectangleLinesEx(floorAbs, 1, RetroSkin.Shadow);
        int tile = 32;
        for (int x = (int)floorAbs.X; x < floorAbs.X + floorAbs.Width; x += tile)
            Raylib.DrawLine(x, (int)floorAbs.Y, x, (int)(floorAbs.Y + floorAbs.Height),
                new Color((byte)220, (byte)208, (byte)172, (byte)180));
        for (int y = (int)floorAbs.Y; y < floorAbs.Y + floorAbs.Height; y += tile)
            Raylib.DrawLine((int)floorAbs.X, y, (int)(floorAbs.X + floorAbs.Width), y,
                new Color((byte)220, (byte)208, (byte)172, (byte)180));

        // Furniture (drawn beneath the roaches; roaches dim when they
        // overlap so they appear to be hiding under them).
        foreach (var f in _furniture)
        {
            var abs = new Rectangle(panelOffset.X + f.X, panelOffset.Y + f.Y, f.Width, f.Height);
            RetroSkin.DrawRaised(abs);
        }

        // Splats — older splats fade out.
        foreach (var s in _splats)
        {
            float age = _now - s.Born;
            byte a = (byte)(255 * Math.Clamp(1f - age / SplatLifetime, 0f, 1f));
            var col = new Color((byte)80, (byte)40, (byte)40, a);
            // Splat = irregular cluster of small dark blobs.
            Raylib.DrawCircle((int)(panelOffset.X + s.Pos.X), (int)(panelOffset.Y + s.Pos.Y), 8, col);
            Raylib.DrawCircle((int)(panelOffset.X + s.Pos.X - 4), (int)(panelOffset.Y + s.Pos.Y - 2), 4, col);
            Raylib.DrawCircle((int)(panelOffset.X + s.Pos.X + 5), (int)(panelOffset.Y + s.Pos.Y + 3), 3, col);
            Raylib.DrawCircle((int)(panelOffset.X + s.Pos.X + 2), (int)(panelOffset.Y + s.Pos.Y - 6), 2, col);
        }

        // Roaches
        foreach (var r in _roaches)
        {
            DrawRoach(panelOffset, r);
        }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(status, _status,
            $"Loose {_roaches.Count}/{_targetCount}  Squashed {_squashed}");
    }

    private static void DrawRoach(Vector2 panelOffset, Roach r)
    {
        float cx = panelOffset.X + r.Pos.X;
        float cy = panelOffset.Y + r.Pos.Y;
        byte a = r.Hidden ? (byte)96 : (byte)255;
        var body = new Color((byte)84, (byte)56, (byte)32, a);
        var dark = new Color((byte)32, (byte)16, (byte)8, a);

        // Slight wobble between two leg-poses based on time so they look
        // like they're scurrying even when standing still.
        float wob = MathF.Sin((float)Raylib.GetTime() * 18f + r.Pos.X * 0.1f);
        float rad = r.HeadingDeg * MathF.PI / 180f;
        Vector2 fwd = new(MathF.Cos(rad), MathF.Sin(rad));
        Vector2 side = new(-fwd.Y, fwd.X);

        // Body (elongated)
        Raylib.DrawEllipse((int)cx, (int)cy, r.Size, r.Size * 0.5f, body);
        // Head
        Vector2 head = new(cx + fwd.X * r.Size * 0.7f, cy + fwd.Y * r.Size * 0.7f);
        Raylib.DrawCircle((int)head.X, (int)head.Y, r.Size * 0.35f, body);
        // Antennae
        Vector2 antL = head + fwd * r.Size * 0.8f + side * r.Size * 0.4f * (wob > 0 ? 1 : 0.6f);
        Vector2 antR = head + fwd * r.Size * 0.8f - side * r.Size * 0.4f * (wob > 0 ? 0.6f : 1);
        Raylib.DrawLineEx(head, antL, 1f, dark);
        Raylib.DrawLineEx(head, antR, 1f, dark);
        // Legs (3 per side, offset along body)
        for (int i = -1; i <= 1; i++)
        {
            float t = i * r.Size * 0.5f;
            Vector2 origin = new(cx + fwd.X * t, cy + fwd.Y * t);
            float legPhase = wob * (i == 0 ? 1 : -1);
            Vector2 legL = origin + side * (r.Size * 0.6f + legPhase * 1.5f);
            Vector2 legR = origin - side * (r.Size * 0.6f - legPhase * 1.5f);
            Raylib.DrawLineEx(origin, legL, 1f, dark);
            Raylib.DrawLineEx(origin, legR, 1f, dark);
        }
    }
}
