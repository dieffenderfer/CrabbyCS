using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.UI;

/// <summary>
/// Cathartic vandalism toy that paints damage effects directly onto the
/// pet's full-screen always-on-top overlay — i.e. on top of whatever else
/// is on the user's actual desktop. Tools have rich interactions: fire ages
/// into persistent char and incinerates ants; wash sprays away damage and
/// drowns ants; lightning leaves a crater; tornadoes swirl every nearby
/// piece of damage around their vortex; ants wander and gnaw nibble trails
/// across the screen.
/// </summary>
public class DesktopDestroyerOverlay
{
    public bool Active;

    public Vector2 PaletteOrigin = new(40, 80);
    private bool _paletteDragging;
    private Vector2 _paletteDragGrab;

    // Two-column palette: tools laid out 2 wide by 5 tall.
    private const int ToolCols = 2;
    private const int ToolRows = 5;
    private const int NumTools = 10;
    private const int ToolBtn = 40;
    private const int BtnGap = 4;
    private const int TitleH = 20;
    private const int FooterBtnH = 24;
    private const int FooterPad = 8;
    private const int PaletteSidePad = 6;
    private const int PaletteW = ToolCols * ToolBtn + (ToolCols - 1) * BtnGap + PaletteSidePad * 2;
    private static int PaletteH =>
        TitleH + 6 + ToolRows * (ToolBtn + BtnGap) + FooterPad
        + FooterBtnH * 2 + BtnGap + 6;

    public enum Tool
    {
        Pistol, MachineGun, Chainsaw, Flame, Mallet,
        Acid, Ants, Wash, Lightning, Tornado,
    }
    private Tool _tool = Tool.Pistol;

    // ── Effect data ─────────────────────────────────────────────────────
    private record struct BulletHole(Vector2 Pos, float Size, int Seed);
    private record struct Crater(Vector2 Pos, float Size, int Seed);
    private record struct CharBlot(Vector2 Pos, float Size, int Seed);
    private class FireParticle
    {
        public Vector2 Pos;
        public float Age;
        public float Life;
        public float Radius;
    }
    private class AcidDrip
    {
        public Vector2 Pos;
        public float Age;
        public float Length = 6;
        public float Vy = 14;
    }
    private class Slash { public List<Vector2> Points = new(); }
    private class Ant
    {
        public Vector2 Pos;
        public float HeadingDeg;
        public float Speed = 26f;
        public float NibbleAccum;
    }
    private class WashSplash
    {
        public Vector2 Pos;
        public float Age;
        public float Life = 0.7f;
    }
    private class LightningBolt
    {
        public float Age;
        public float Life = 0.32f;
        public List<Vector2> Path = new();
        public List<List<Vector2>> Branches = new();
    }
    private class TornadoFx
    {
        public Vector2 Pos;
        public float Age;
        public float Life = 3.2f;
        public float Radius = 90f;
        public float SpinSpeed = 5.5f;
    }

    private readonly List<BulletHole> _holes = new();
    private readonly List<Crater> _craters = new();
    private readonly List<CharBlot> _char = new();
    private readonly List<FireParticle> _fire = new();
    private readonly List<AcidDrip> _drips = new();
    private readonly List<Slash> _slashes = new();
    private readonly List<Ant> _ants = new();
    private readonly List<Vector2> _nibbles = new();
    private readonly List<WashSplash> _washes = new();
    private readonly List<LightningBolt> _bolts = new();
    private readonly List<TornadoFx> _tornadoes = new();
    private Slash? _activeSlash;
    private float _autoFireCool;
    private readonly Random _rng = new();

    // ── Public API ──────────────────────────────────────────────────────
    public bool ContainsPoint(Vector2 p)
    {
        if (!Active) return false;
        return p.X >= PaletteOrigin.X && p.X < PaletteOrigin.X + PaletteW
            && p.Y >= PaletteOrigin.Y && p.Y < PaletteOrigin.Y + PaletteH;
    }

    public bool ShouldCaptureMouse => Active;

    public void Toggle()
    {
        Active = !Active;
        if (!Active) ClearAll();
    }

    public void ClearAll()
    {
        _holes.Clear(); _craters.Clear(); _char.Clear();
        _fire.Clear(); _drips.Clear(); _slashes.Clear();
        _ants.Clear(); _nibbles.Clear();
        _washes.Clear(); _bolts.Clear(); _tornadoes.Clear();
        _activeSlash = null;
    }

    // ── Update ──────────────────────────────────────────────────────────
    public bool Update(float delta, Vector2 mouse, bool leftPressed, bool leftReleased, bool rightPressed)
    {
        if (!Active) return false;

        TickEffects(delta);

        if (_paletteDragging)
        {
            PaletteOrigin = mouse - _paletteDragGrab;
            if (leftReleased) _paletteDragging = false;
            return true;
        }

        if (ContainsPoint(mouse))
        {
            if (leftPressed) HandlePaletteClick(mouse);
            return true;
        }

        ApplyTool(mouse, leftPressed, leftReleased);
        return true;
    }

    private void TickEffects(float delta)
    {
        // Fire: age, then char on burnout.
        for (int i = _fire.Count - 1; i >= 0; i--)
        {
            var f = _fire[i];
            f.Age += delta;
            if (f.Age >= f.Life)
            {
                // Fire rises ~18px/s; bake the final visual position into the char.
                var charPos = f.Pos - new Vector2(0, f.Age * 12f);
                _char.Add(new CharBlot(charPos, 8 + f.Radius * 0.4f, _rng.Next()));
                _fire.RemoveAt(i);
            }
        }

        // Drips
        for (int i = _drips.Count - 1; i >= 0; i--)
        {
            var d = _drips[i];
            d.Age += delta;
            d.Pos = new Vector2(d.Pos.X, d.Pos.Y + d.Vy * delta);
            d.Length += delta * 18;
        }

        // Ants wander, drop nibbles, bounce off screen edges.
        int sw = Math.Max(800, Raylib.GetScreenWidth());
        int sh = Math.Max(600, Raylib.GetScreenHeight());
        foreach (var a in _ants)
        {
            a.HeadingDeg += (float)(_rng.NextDouble() - 0.5) * 240f * delta;
            float rad = a.HeadingDeg * MathF.PI / 180f;
            var dir = new Vector2(MathF.Cos(rad), MathF.Sin(rad));
            var next = a.Pos + dir * a.Speed * delta;
            a.NibbleAccum += Vector2.Distance(next, a.Pos);
            a.Pos = next;
            if (a.NibbleAccum > 5f)
            {
                _nibbles.Add(a.Pos + new Vector2((float)(_rng.NextDouble() - 0.5) * 2,
                                                 (float)(_rng.NextDouble() - 0.5) * 2));
                a.NibbleAccum = 0;
            }
            if (a.Pos.X < 4)        { a.HeadingDeg = 180 - a.HeadingDeg; a.Pos.X = 4; }
            if (a.Pos.X > sw - 4)   { a.HeadingDeg = 180 - a.HeadingDeg; a.Pos.X = sw - 4; }
            if (a.Pos.Y < 4)        { a.HeadingDeg = -a.HeadingDeg;       a.Pos.Y = 4; }
            if (a.Pos.Y > sh - 4)   { a.HeadingDeg = -a.HeadingDeg;       a.Pos.Y = sh - 4; }
        }

        // Fire incinerates nearby ants → leave char behind.
        if (_fire.Count > 0 && _ants.Count > 0)
        {
            for (int i = _ants.Count - 1; i >= 0; i--)
            {
                foreach (var f in _fire)
                {
                    if (Vector2.DistanceSquared(_ants[i].Pos, f.Pos) < (f.Radius + 12) * (f.Radius + 12))
                    {
                        _char.Add(new CharBlot(_ants[i].Pos, 6, _rng.Next()));
                        _ants.RemoveAt(i);
                        break;
                    }
                }
            }
        }

        // Wash splashes age out.
        for (int i = _washes.Count - 1; i >= 0; i--)
        {
            _washes[i].Age += delta;
            if (_washes[i].Age >= _washes[i].Life) _washes.RemoveAt(i);
        }

        // Lightning bolts: very short flash.
        for (int i = _bolts.Count - 1; i >= 0; i--)
        {
            _bolts[i].Age += delta;
            if (_bolts[i].Age >= _bolts[i].Life) _bolts.RemoveAt(i);
        }

        // Tornadoes: animate + swirl every nearby damage record around the
        // vortex center. Effect strength grows toward the tornado's centre.
        if (_autoFireCool > 0) _autoFireCool -= delta;
        for (int i = _tornadoes.Count - 1; i >= 0; i--)
        {
            var t = _tornadoes[i];
            t.Age += delta;
            if (t.Age >= t.Life) { _tornadoes.RemoveAt(i); continue; }
            ApplyTornadoSwirl(t, delta);
        }
    }

    private void ApplyTornadoSwirl(TornadoFx t, float delta)
    {
        float radSq = t.Radius * t.Radius;
        Vector2 Swirl(Vector2 p)
        {
            var d = p - t.Pos;
            float dSq = d.LengthSquared();
            if (dSq > radSq || dSq < 0.001f) return p;
            float dist = MathF.Sqrt(dSq);
            float strength = 1f - dist / t.Radius;          // 0..1, peak at center
            float angle = t.SpinSpeed * delta * (0.4f + strength);
            // Slight inward pull while swirling, so debris funnels in.
            float inward = 24f * strength * delta;
            float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
            var rotated = new Vector2(d.X * cos - d.Y * sin, d.X * sin + d.Y * cos);
            var pulled = rotated * (1f - inward / dist);
            return t.Pos + pulled;
        }

        for (int i = 0; i < _holes.Count; i++)
            _holes[i] = _holes[i] with { Pos = Swirl(_holes[i].Pos) };
        for (int i = 0; i < _craters.Count; i++)
            _craters[i] = _craters[i] with { Pos = Swirl(_craters[i].Pos) };
        for (int i = 0; i < _char.Count; i++)
            _char[i] = _char[i] with { Pos = Swirl(_char[i].Pos) };
        foreach (var f in _fire) f.Pos = Swirl(f.Pos);
        foreach (var d in _drips) d.Pos = Swirl(d.Pos);
        foreach (var a in _ants) a.Pos = Swirl(a.Pos);
        for (int i = 0; i < _nibbles.Count; i++) _nibbles[i] = Swirl(_nibbles[i]);
        foreach (var s in _slashes)
            for (int i = 0; i < s.Points.Count; i++) s.Points[i] = Swirl(s.Points[i]);
    }

    private void HandlePaletteClick(Vector2 mouse)
    {
        var local = mouse - PaletteOrigin;

        if (local.Y < TitleH)
        {
            _paletteDragging = true;
            _paletteDragGrab = local;
            return;
        }

        for (int i = 0; i < NumTools; i++)
        {
            if (RetroSkin.PointInRect(mouse, ToolRect(i))) { _tool = (Tool)i; return; }
        }

        if (RetroSkin.PointInRect(mouse, ResetRect())) { ClearAll(); return; }
        if (RetroSkin.PointInRect(mouse, StopRect())) { Active = false; ClearAll(); return; }
    }

    private Rectangle ToolRect(int i)
    {
        int col = i % ToolCols;
        int row = i / ToolCols;
        float x = PaletteOrigin.X + PaletteSidePad + col * (ToolBtn + BtnGap);
        float y = PaletteOrigin.Y + TitleH + 6 + row * (ToolBtn + BtnGap);
        return new Rectangle(x, y, ToolBtn, ToolBtn);
    }

    private float FooterY => PaletteOrigin.Y + TitleH + 6 + ToolRows * (ToolBtn + BtnGap) + FooterPad;
    private Rectangle ResetRect() => new(PaletteOrigin.X + 6, FooterY, PaletteW - 12, FooterBtnH);
    private Rectangle StopRect() => new(PaletteOrigin.X + 6, FooterY + FooterBtnH + BtnGap, PaletteW - 12, FooterBtnH);

    private void ApplyTool(Vector2 mouse, bool leftPressed, bool leftReleased)
    {
        switch (_tool)
        {
            case Tool.Pistol:
                if (leftPressed)
                    _holes.Add(new BulletHole(mouse, 8, _rng.Next()));
                break;

            case Tool.MachineGun:
                if (Raylib.IsMouseButtonDown(MouseButton.Left) && _autoFireCool <= 0)
                {
                    var jit = new Vector2((float)(_rng.NextDouble() - 0.5) * 18,
                                          (float)(_rng.NextDouble() - 0.5) * 18);
                    _holes.Add(new BulletHole(mouse + jit, 6, _rng.Next()));
                    _autoFireCool = 0.04f;
                }
                break;

            case Tool.Chainsaw:
                if (leftPressed)
                {
                    _activeSlash = new Slash();
                    _activeSlash.Points.Add(mouse);
                    _slashes.Add(_activeSlash);
                }
                if (_activeSlash != null && Raylib.IsMouseButtonDown(MouseButton.Left))
                {
                    var last = _activeSlash.Points[^1];
                    if (Vector2.Distance(mouse, last) > 4) _activeSlash.Points.Add(mouse);
                }
                if (leftReleased) _activeSlash = null;
                break;

            case Tool.Flame:
                if (Raylib.IsMouseButtonDown(MouseButton.Left) && _autoFireCool <= 0)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        var jit = new Vector2((float)(_rng.NextDouble() - 0.5) * 22,
                                              (float)(_rng.NextDouble() - 0.5) * 22);
                        _fire.Add(new FireParticle
                        {
                            Pos = mouse + jit,
                            Life = 1.4f + (float)_rng.NextDouble(),
                            Radius = 4 + (float)_rng.NextDouble() * 4,
                        });
                    }
                    _autoFireCool = 0.05f;
                }
                break;

            case Tool.Mallet:
                if (leftPressed)
                    _craters.Add(new Crater(mouse, 22, _rng.Next()));
                break;

            case Tool.Acid:
                if (leftPressed) _drips.Add(new AcidDrip { Pos = mouse });
                break;

            case Tool.Ants:
                // Initial click drops a burst of 6 ants. Holding the button
                // and dragging keeps spawning more at a rate-limited cadence
                // so the screen fills as the cursor sweeps across it without
                // becoming a swarm in a single frame.
                if (leftPressed)
                {
                    for (int k = 0; k < 6; k++)
                    {
                        var jit = new Vector2((float)(_rng.NextDouble() - 0.5) * 24,
                                              (float)(_rng.NextDouble() - 0.5) * 24);
                        _ants.Add(new Ant
                        {
                            Pos = mouse + jit,
                            HeadingDeg = (float)_rng.NextDouble() * 360f,
                            Speed = 22f + (float)_rng.NextDouble() * 16f,
                        });
                    }
                    _autoFireCool = 0.06f;
                }
                else if (Raylib.IsMouseButtonDown(MouseButton.Left) && _autoFireCool <= 0)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        var jit = new Vector2((float)(_rng.NextDouble() - 0.5) * 24,
                                              (float)(_rng.NextDouble() - 0.5) * 24);
                        _ants.Add(new Ant
                        {
                            Pos = mouse + jit,
                            HeadingDeg = (float)_rng.NextDouble() * 360f,
                            Speed = 22f + (float)_rng.NextDouble() * 16f,
                        });
                    }
                    _autoFireCool = 0.06f;
                }
                break;

            case Tool.Wash:
                if (Raylib.IsMouseButtonDown(MouseButton.Left) && _autoFireCool <= 0)
                {
                    _washes.Add(new WashSplash { Pos = mouse });
                    ClearInRadius(mouse, 55f);
                    _autoFireCool = 0.12f;
                }
                break;

            case Tool.Lightning:
                if (leftPressed)
                {
                    var bolt = new LightningBolt();
                    var start = new Vector2(mouse.X + (float)(_rng.NextDouble() - 0.5) * 80, 0);
                    bolt.Path = GenerateBoltPath(start, mouse);
                    bolt.Branches = GenerateBranches(bolt.Path);
                    _bolts.Add(bolt);
                    _craters.Add(new Crater(mouse, 16, _rng.Next()));
                    _char.Add(new CharBlot(mouse, 12, _rng.Next()));
                }
                break;

            case Tool.Tornado:
                if (leftPressed)
                    _tornadoes.Add(new TornadoFx { Pos = mouse });
                break;
        }
    }

    private void ClearInRadius(Vector2 center, float radius)
    {
        float r2 = radius * radius;
        _holes.RemoveAll(h => (h.Pos - center).LengthSquared() < r2);
        _craters.RemoveAll(c => (c.Pos - center).LengthSquared() < r2);
        _char.RemoveAll(c => (c.Pos - center).LengthSquared() < r2);
        _fire.RemoveAll(f => (f.Pos - center).LengthSquared() < r2);
        _drips.RemoveAll(d => (d.Pos - center).LengthSquared() < r2);
        _ants.RemoveAll(a => (a.Pos - center).LengthSquared() < r2);
        _nibbles.RemoveAll(n => (n - center).LengthSquared() < r2);
        _slashes.RemoveAll(s => s.Points.Exists(p => (p - center).LengthSquared() < r2));
    }

    private List<Vector2> GenerateBoltPath(Vector2 start, Vector2 end)
    {
        var path = new List<Vector2> { start };
        const int segments = 14;
        var step = (end - start) / segments;
        for (int i = 1; i < segments; i++)
        {
            var perp = new Vector2(-step.Y, step.X);
            if (perp.LengthSquared() > 0) perp = Vector2.Normalize(perp);
            float jitter = (float)(_rng.NextDouble() - 0.5) * 36f;
            path.Add(start + step * i + perp * jitter);
        }
        path.Add(end);
        return path;
    }

    private List<List<Vector2>> GenerateBranches(List<Vector2> mainPath)
    {
        var branches = new List<List<Vector2>>();
        for (int i = 2; i < mainPath.Count - 2; i++)
        {
            if (_rng.Next(3) > 0) continue;
            var p = mainPath[i];
            var dir = mainPath[i + 1] - mainPath[i];
            if (dir.LengthSquared() < 0.01f) continue;
            var perp = Vector2.Normalize(new Vector2(-dir.Y, dir.X));
            var endpoint = p + perp * (float)((_rng.NextDouble() - 0.5) * 90)
                             + Vector2.Normalize(dir) * (float)(_rng.NextDouble() * 30);
            branches.Add(new List<Vector2> { p, endpoint });
        }
        return branches;
    }

    // ── Draw ────────────────────────────────────────────────────────────
    public void Draw()
    {
        if (!Active) return;

        foreach (var s in _slashes) DrawSlash(s);
        foreach (var c in _craters) DrawCrater(c);
        foreach (var c in _char) DrawCharBlot(c);
        foreach (var n in _nibbles)
            Raylib.DrawCircle((int)n.X, (int)n.Y, 1,
                new Color((byte)40, (byte)28, (byte)16, (byte)220));
        foreach (var h in _holes) DrawHole(h);
        foreach (var d in _drips) DrawDrip(d);
        foreach (var a in _ants) DrawAnt(a);
        foreach (var f in _fire) DrawFire(f);
        foreach (var w in _washes) DrawWashSplash(w);
        foreach (var b in _bolts) DrawLightningBolt(b);
        foreach (var t in _tornadoes) DrawTornado(t);

        DrawPalette();
    }

    private void DrawPalette()
    {
        int x = (int)PaletteOrigin.X;
        int y = (int)PaletteOrigin.Y;
        int w = PaletteW, h = PaletteH;

        Raylib.DrawRectangle(x + 4, y + 4, w, h, new Color((byte)0, (byte)0, (byte)0, (byte)100));
        var panel = new Rectangle(x, y, w, h);
        RetroSkin.DrawRaised(panel);

        var title = new Rectangle(x + 3, y + 3, w - 6, TitleH - 4);
        Raylib.DrawRectangleGradientH((int)title.X, (int)title.Y, (int)title.Width, (int)title.Height,
            new Color((byte)96, (byte)24, (byte)24, (byte)255),
            new Color((byte)200, (byte)64, (byte)48, (byte)255));
        RetroSkin.DrawText("DESTROY", (int)title.X + 4, (int)title.Y, new Color((byte)255, (byte)240, (byte)200, (byte)255), 12);

        for (int i = 0; i < NumTools; i++)
        {
            var r = ToolRect(i);
            bool selected = (Tool)i == _tool;
            if (selected) RetroSkin.DrawPressed(r);
            else RetroSkin.DrawRaised(r);
            DrawToolIcon(i, r);
        }

        var resetR = ResetRect();
        RetroSkin.DrawRaised(resetR);
        int rw = RetroSkin.MeasureText("Reset", 12);
        RetroSkin.DrawText("Reset",
            (int)(resetR.X + (resetR.Width - rw) / 2),
            (int)(resetR.Y + (resetR.Height - 12) / 2),
            RetroSkin.BodyText, 12);

        var stopR = StopRect();
        RetroSkin.DrawRaised(stopR);
        int sw = RetroSkin.MeasureText("Stop", 12);
        RetroSkin.DrawText("Stop",
            (int)(stopR.X + (stopR.Width - sw) / 2),
            (int)(stopR.Y + (stopR.Height - 12) / 2),
            new Color((byte)180, (byte)40, (byte)40, (byte)255), 12);
    }

    private static void DrawToolIcon(int i, Rectangle r)
    {
        int cx = (int)(r.X + r.Width / 2);
        int cy = (int)(r.Y + r.Height / 2);
        var ink = new Color((byte)24, (byte)24, (byte)24, (byte)255);
        switch (i)
        {
            case 0: // Pistol
                Raylib.DrawRectangle(cx - 12, cy - 3, 16, 6, ink);
                Raylib.DrawRectangle(cx - 4, cy + 3, 6, 9, ink);
                break;
            case 1: // Machine gun
                Raylib.DrawRectangle(cx - 14, cy - 2, 18, 5, ink);
                Raylib.DrawCircle(cx - 4, cy + 5, 5, ink);
                Raylib.DrawRectangle(cx - 8, cy + 2, 10, 8, ink);
                break;
            case 2: // Chainsaw
                Raylib.DrawRectangle(cx - 14, cy - 5, 12, 12, new Color((byte)220, (byte)96, (byte)32, (byte)255));
                Raylib.DrawRectangle(cx - 4, cy - 2, 18, 5, new Color((byte)140, (byte)140, (byte)150, (byte)255));
                for (int t = 0; t < 5; t++)
                    Raylib.DrawTriangle(
                        new Vector2(cx - 4 + t * 4, cy + 3),
                        new Vector2(cx - 2 + t * 4, cy + 7),
                        new Vector2(cx + t * 4,     cy + 3),
                        ink);
                break;
            case 3: // Flame
                Raylib.DrawTriangle(
                    new Vector2(cx, cy - 13),
                    new Vector2(cx - 10, cy + 10),
                    new Vector2(cx + 10, cy + 10),
                    new Color((byte)232, (byte)80, (byte)32, (byte)255));
                Raylib.DrawTriangle(
                    new Vector2(cx, cy - 6),
                    new Vector2(cx - 6, cy + 9),
                    new Vector2(cx + 6, cy + 9),
                    new Color((byte)255, (byte)200, (byte)64, (byte)255));
                break;
            case 4: // Mallet
                Raylib.DrawRectangle(cx - 12, cy - 8, 22, 10, new Color((byte)120, (byte)80, (byte)40, (byte)255));
                Raylib.DrawRectangle(cx - 10, cy - 6, 18, 6, new Color((byte)180, (byte)130, (byte)70, (byte)255));
                Raylib.DrawRectangle(cx - 1, cy + 2, 3, 12, new Color((byte)80, (byte)56, (byte)24, (byte)255));
                break;
            case 5: // Acid drop
                Raylib.DrawCircle(cx, cy + 4, 7, new Color((byte)80, (byte)200, (byte)80, (byte)255));
                Raylib.DrawTriangle(
                    new Vector2(cx, cy - 12),
                    new Vector2(cx - 5, cy + 2),
                    new Vector2(cx + 5, cy + 2),
                    new Color((byte)80, (byte)200, (byte)80, (byte)255));
                break;
            case 6: // Ants — three little oval bugs
                for (int n = 0; n < 3; n++)
                {
                    int ax = cx - 8 + n * 8;
                    int ay = cy + (n - 1) * 4;
                    Raylib.DrawCircle(ax, ay, 2, ink);
                    Raylib.DrawCircle(ax - 2, ay, 1, ink);
                    Raylib.DrawCircle(ax + 2, ay, 1, ink);
                    Raylib.DrawLine(ax - 1, ay, ax - 4, ay - 2, ink);
                    Raylib.DrawLine(ax + 1, ay, ax + 4, ay + 2, ink);
                }
                break;
            case 7: // Wash — water droplets falling
                Raylib.DrawTriangle(
                    new Vector2(cx - 7, cy - 8),
                    new Vector2(cx - 11, cy + 2),
                    new Vector2(cx - 3, cy + 2),
                    new Color((byte)80, (byte)160, (byte)220, (byte)255));
                Raylib.DrawTriangle(
                    new Vector2(cx + 6, cy - 4),
                    new Vector2(cx + 2, cy + 6),
                    new Vector2(cx + 10, cy + 6),
                    new Color((byte)100, (byte)180, (byte)240, (byte)255));
                Raylib.DrawCircle(cx, cy + 8, 4, new Color((byte)120, (byte)200, (byte)240, (byte)255));
                break;
            case 8: // Lightning bolt
                Raylib.DrawTriangle(
                    new Vector2(cx + 4, cy - 12),
                    new Vector2(cx - 6, cy),
                    new Vector2(cx + 1, cy),
                    new Color((byte)255, (byte)220, (byte)80, (byte)255));
                Raylib.DrawTriangle(
                    new Vector2(cx + 1, cy),
                    new Vector2(cx - 4, cy + 12),
                    new Vector2(cx + 5, cy - 1),
                    new Color((byte)255, (byte)220, (byte)80, (byte)255));
                break;
            case 9: // Tornado — funnel + arcs
                Raylib.DrawTriangle(
                    new Vector2(cx - 12, cy - 10),
                    new Vector2(cx + 12, cy - 10),
                    new Vector2(cx, cy + 12),
                    new Color((byte)160, (byte)160, (byte)180, (byte)255));
                for (int k = 0; k < 3; k++)
                {
                    int ay = cy - 8 + k * 6;
                    int rad = 11 - k * 3;
                    Raylib.DrawEllipseLines(cx, ay, rad, 3, new Color((byte)90, (byte)90, (byte)110, (byte)255));
                }
                break;
        }
    }

    // ── Effect drawing (absolute screen coords) ─────────────────────────
    private static void DrawHole(BulletHole h)
    {
        int cx = (int)h.Pos.X, cy = (int)h.Pos.Y;
        Raylib.DrawCircle(cx, cy, h.Size, new Color((byte)0, (byte)0, (byte)0, (byte)255));
        var rng = new Random(h.Seed);
        int cracks = 6 + rng.Next(4);
        for (int i = 0; i < cracks; i++)
        {
            float ang = (float)(rng.NextDouble() * Math.PI * 2);
            float len = h.Size + 4 + (float)rng.NextDouble() * 12;
            int ex = cx + (int)(MathF.Cos(ang) * len);
            int ey = cy + (int)(MathF.Sin(ang) * len);
            Raylib.DrawLine(cx, cy, ex, ey, new Color((byte)0, (byte)0, (byte)0, (byte)255));
            float ang2 = ang + (float)((rng.NextDouble() - 0.5) * 0.6);
            float midLen = len * 0.5f;
            int mx = cx + (int)(MathF.Cos(ang) * midLen);
            int my = cy + (int)(MathF.Sin(ang) * midLen);
            int bx = mx + (int)(MathF.Cos(ang2) * 4);
            int by = my + (int)(MathF.Sin(ang2) * 4);
            Raylib.DrawLine(mx, my, bx, by, new Color((byte)0, (byte)0, (byte)0, (byte)255));
        }
    }

    private static void DrawCrater(Crater c)
    {
        int cx = (int)c.Pos.X, cy = (int)c.Pos.Y;
        Raylib.DrawCircle(cx, cy, c.Size, new Color((byte)40, (byte)40, (byte)40, (byte)255));
        Raylib.DrawCircle(cx, cy, c.Size - 4, new Color((byte)20, (byte)20, (byte)20, (byte)255));
        var rng = new Random(c.Seed);
        for (int i = 0; i < 10; i++)
        {
            float ang = i * MathF.PI * 2 / 10 + (float)rng.NextDouble() * 0.3f;
            float len = c.Size + 8 + (float)rng.NextDouble() * 18;
            float midLen = len * 0.6f;
            int mx = cx + (int)(MathF.Cos(ang) * midLen);
            int my = cy + (int)(MathF.Sin(ang) * midLen);
            float ang2 = ang + (float)((rng.NextDouble() - 0.5) * 0.7);
            int ex = mx + (int)(MathF.Cos(ang2) * (len - midLen));
            int ey = my + (int)(MathF.Sin(ang2) * (len - midLen));
            Raylib.DrawLine(cx, cy, mx, my, new Color((byte)0, (byte)0, (byte)0, (byte)255));
            Raylib.DrawLine(mx, my, ex, ey, new Color((byte)0, (byte)0, (byte)0, (byte)255));
        }
    }

    private static void DrawCharBlot(CharBlot c)
    {
        var rng = new Random(c.Seed);
        int cx = (int)c.Pos.X, cy = (int)c.Pos.Y;
        // Irregular blob: 6-9 overlapping dark circles of varying radius.
        int blobs = 6 + rng.Next(4);
        for (int i = 0; i < blobs; i++)
        {
            int dx = rng.Next(-(int)c.Size, (int)c.Size + 1);
            int dy = rng.Next(-(int)c.Size, (int)c.Size + 1);
            int rad = 2 + rng.Next((int)(c.Size * 0.5f) + 1);
            Raylib.DrawCircle(cx + dx, cy + dy, rad,
                new Color((byte)16, (byte)10, (byte)6, (byte)225));
        }
        // Grey ash flecks for texture
        for (int i = 0; i < blobs; i++)
        {
            int dx = rng.Next(-(int)c.Size, (int)c.Size + 1);
            int dy = rng.Next(-(int)c.Size, (int)c.Size + 1);
            Raylib.DrawPixel(cx + dx, cy + dy, new Color((byte)160, (byte)140, (byte)120, (byte)200));
        }
    }

    private static void DrawSlash(Slash s)
    {
        if (s.Points.Count < 2) return;
        for (int i = 1; i < s.Points.Count; i++)
        {
            var a = s.Points[i - 1];
            var b = s.Points[i];
            Raylib.DrawLineEx(a, b, 6f, new Color((byte)0, (byte)0, (byte)0, (byte)255));
            Raylib.DrawLineEx(a, b, 3f, new Color((byte)48, (byte)24, (byte)16, (byte)255));
        }
        var rng = new Random(s.Points.Count);
        for (int i = 0; i < s.Points.Count; i++)
        {
            var p = s.Points[i];
            for (int k = 0; k < 3; k++)
            {
                int dx = rng.Next(-8, 9), dy = rng.Next(-8, 9);
                Raylib.DrawPixel((int)p.X + dx, (int)p.Y + dy,
                    new Color((byte)220, (byte)180, (byte)100, (byte)220));
            }
        }
    }

    private static void DrawFire(FireParticle f)
    {
        int cx = (int)f.Pos.X;
        int cy = (int)(f.Pos.Y - f.Age * 12);
        float t = f.Age / f.Life;
        Color col;
        if (t < 0.4f) col = new Color((byte)255, (byte)200, (byte)40, (byte)(255 - t * 100));
        else if (t < 0.75f) col = new Color((byte)220, (byte)80, (byte)32, (byte)(255 - (t - 0.4f) * 200));
        else col = new Color((byte)96, (byte)96, (byte)96, (byte)Math.Max(0, 200 - (t - 0.75f) * 800));
        float radius = f.Radius * (t < 0.5f ? 1 + t : 1.5f - (t - 0.5f) * 0.4f);
        Raylib.DrawCircle(cx, cy, radius, col);
        if (t < 0.5f)
            Raylib.DrawCircle(cx, cy, radius * 0.5f,
                new Color((byte)255, (byte)240, (byte)160, (byte)180));
    }

    private static void DrawDrip(AcidDrip d)
    {
        int cx = (int)d.Pos.X, cy = (int)d.Pos.Y;
        Raylib.DrawRectangle(cx - 2, cy - (int)d.Length, 4, (int)d.Length,
            new Color((byte)96, (byte)200, (byte)96, (byte)255));
        Raylib.DrawCircle(cx, cy, 4, new Color((byte)80, (byte)220, (byte)80, (byte)255));
        Raylib.DrawCircle(cx - 1, cy - 1, 1, new Color((byte)220, (byte)255, (byte)200, (byte)220));
    }

    private static void DrawAnt(Ant a)
    {
        int x = (int)a.Pos.X, y = (int)a.Pos.Y;
        var ink = new Color((byte)20, (byte)16, (byte)10, (byte)255);
        // Heading vector for orienting body parts.
        float rad = a.HeadingDeg * MathF.PI / 180f;
        var fwd = new Vector2(MathF.Cos(rad), MathF.Sin(rad));
        var perp = new Vector2(-fwd.Y, fwd.X);
        // Body: head + thorax + abdomen
        var head = a.Pos + fwd * 3;
        var abd = a.Pos - fwd * 3;
        Raylib.DrawCircle((int)head.X, (int)head.Y, 1.6f, ink);
        Raylib.DrawCircle(x, y, 1.4f, ink);
        Raylib.DrawCircle((int)abd.X, (int)abd.Y, 1.8f, ink);
        // Legs (3 per side)
        for (int i = -1; i <= 1; i++)
        {
            var legBase = a.Pos + fwd * (i * 1.4f);
            var tipL = legBase + perp * 3.2f + fwd * (i * 0.4f);
            var tipR = legBase - perp * 3.2f - fwd * (i * 0.4f);
            Raylib.DrawLine((int)legBase.X, (int)legBase.Y, (int)tipL.X, (int)tipL.Y, ink);
            Raylib.DrawLine((int)legBase.X, (int)legBase.Y, (int)tipR.X, (int)tipR.Y, ink);
        }
    }

    private static void DrawWashSplash(WashSplash w)
    {
        float t = w.Age / w.Life;
        float r = 8 + 50 * t;
        byte alpha = (byte)Math.Max(0, 255 - t * 255);
        Raylib.DrawCircleLines((int)w.Pos.X, (int)w.Pos.Y, r,
            new Color((byte)120, (byte)200, (byte)240, alpha));
        Raylib.DrawCircleLines((int)w.Pos.X, (int)w.Pos.Y, r * 0.7f,
            new Color((byte)180, (byte)220, (byte)255, alpha));
        // Droplet specks expanding outward
        var rng = new Random((int)(w.Pos.X * 31 + w.Pos.Y * 17));
        for (int i = 0; i < 8; i++)
        {
            float ang = i * MathF.PI / 4 + (float)rng.NextDouble() * 0.3f;
            int dx = (int)(MathF.Cos(ang) * r * 0.9f);
            int dy = (int)(MathF.Sin(ang) * r * 0.9f);
            Raylib.DrawCircle((int)w.Pos.X + dx, (int)w.Pos.Y + dy, 2,
                new Color((byte)180, (byte)220, (byte)255, alpha));
        }
    }

    private static void DrawLightningBolt(LightningBolt b)
    {
        float t = b.Age / b.Life;
        // Strobe: bright early, fades fast
        byte alpha = (byte)Math.Max(0, 255 - t * 280);
        if (alpha == 0) return;
        var glow = new Color((byte)255, (byte)240, (byte)160, alpha);
        var core = new Color((byte)255, (byte)255, (byte)255, alpha);
        for (int i = 1; i < b.Path.Count; i++)
        {
            Raylib.DrawLineEx(b.Path[i - 1], b.Path[i], 5, glow);
            Raylib.DrawLineEx(b.Path[i - 1], b.Path[i], 1.6f, core);
        }
        foreach (var br in b.Branches)
            for (int i = 1; i < br.Count; i++)
                Raylib.DrawLineEx(br[i - 1], br[i], 2, glow);
    }

    private static void DrawTornado(TornadoFx t)
    {
        float age = t.Age;
        // Funnel silhouette
        float w = t.Radius * 0.5f;
        float h = t.Radius * 1.4f;
        Raylib.DrawTriangle(
            new Vector2(t.Pos.X - w, t.Pos.Y - h * 0.4f),
            new Vector2(t.Pos.X + w, t.Pos.Y - h * 0.4f),
            new Vector2(t.Pos.X, t.Pos.Y + h * 0.6f),
            new Color((byte)160, (byte)160, (byte)180, (byte)80));

        // Rotating concentric rings (squashed Y for funnel feel)
        for (int i = 0; i < 6; i++)
        {
            float ringR = (i + 1) * t.Radius / 7f;
            float baseAng = age * t.SpinSpeed + i * 0.6f;
            byte alpha = (byte)Math.Max(0, 220 - i * 30);
            var col = new Color((byte)200, (byte)200, (byte)220, alpha);
            const int segments = 36;
            Vector2 prev = default;
            for (int s = 0; s <= segments; s++)
            {
                float a = baseAng + s * MathF.PI * 2 / segments;
                var p = new Vector2(
                    t.Pos.X + ringR * MathF.Cos(a),
                    t.Pos.Y + ringR * MathF.Sin(a) * 0.55f);
                if (s > 0) Raylib.DrawLineV(prev, p, col);
                prev = p;
            }
        }

        // Debris specks orbiting the vortex
        var rng = new Random((int)(t.Pos.X * 7 + t.Pos.Y * 13));
        for (int i = 0; i < 14; i++)
        {
            float baseAng = age * t.SpinSpeed * 1.5f + i * 0.45f;
            float r = (float)rng.NextDouble() * t.Radius;
            var p = new Vector2(
                t.Pos.X + r * MathF.Cos(baseAng),
                t.Pos.Y + r * MathF.Sin(baseAng) * 0.55f);
            Raylib.DrawCircle((int)p.X, (int)p.Y, 1.4f,
                new Color((byte)80, (byte)64, (byte)48, (byte)220));
        }

        // Thin dark center column hinting at the vortex's axis
        Raylib.DrawLine((int)t.Pos.X, (int)(t.Pos.Y - h * 0.4f),
                        (int)t.Pos.X, (int)(t.Pos.Y + h * 0.6f),
                        new Color((byte)40, (byte)40, (byte)56, (byte)180));
    }
}
