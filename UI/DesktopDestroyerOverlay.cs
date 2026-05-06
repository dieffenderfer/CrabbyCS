using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.UI;

/// <summary>
/// Cathartic vandalism toy that paints damage effects directly onto the
/// pet's full-screen always-on-top overlay — i.e. on top of whatever else
/// is on the user's actual desktop. While Active, every click outside the
/// floating tool palette applies the current tool at that screen pixel and
/// the result persists until Reset (or until the overlay is closed). The
/// palette is itself a small draggable widget.
/// </summary>
public class DesktopDestroyerOverlay
{
    public bool Active;

    public Vector2 PaletteOrigin = new(40, 80);
    private bool _paletteDragging;
    private Vector2 _paletteDragGrab;

    private const int PaletteW = 72;
    private const int TitleH = 20;
    private const int ToolBtn = 52;
    private const int BtnGap = 4;
    private const int FooterBtnH = 26;
    private const int FooterPad = 8;
    private const int NumTools = 6;
    private static int PaletteH =>
        TitleH + 6 + NumTools * (ToolBtn + BtnGap) + FooterPad
        + FooterBtnH * 2 + BtnGap + 6;

    public enum Tool { Pistol, MachineGun, Chainsaw, Flame, Mallet, Acid }
    private Tool _tool = Tool.Pistol;

    private record struct BulletHole(Vector2 Pos, float Size, int Seed);
    private record struct Crater(Vector2 Pos, float Size, int Seed);
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

    private readonly List<BulletHole> _holes = new();
    private readonly List<Crater> _craters = new();
    private readonly List<FireParticle> _fire = new();
    private readonly List<AcidDrip> _drips = new();
    private readonly List<Slash> _slashes = new();
    private Slash? _activeSlash;
    private float _autoFireCool;
    private readonly Random _rng = new();

    public bool ContainsPoint(Vector2 p)
    {
        if (!Active) return false;
        return p.X >= PaletteOrigin.X && p.X < PaletteOrigin.X + PaletteW
            && p.Y >= PaletteOrigin.Y && p.Y < PaletteOrigin.Y + PaletteH;
    }

    /// <summary>True if the overlay should force the host window to capture the mouse globally.</summary>
    public bool ShouldCaptureMouse => Active;

    public void Toggle()
    {
        Active = !Active;
        if (!Active) ClearAll();
    }

    public void ClearAll()
    {
        _holes.Clear(); _craters.Clear();
        _fire.Clear(); _drips.Clear();
        _slashes.Clear(); _activeSlash = null;
    }

    public bool Update(float delta, Vector2 mouse, bool leftPressed, bool leftReleased, bool rightPressed)
    {
        if (!Active) return false;

        // Tick effects regardless of input
        for (int i = _fire.Count - 1; i >= 0; i--)
        {
            _fire[i].Age += delta;
            if (_fire[i].Age >= _fire[i].Life) _fire.RemoveAt(i);
        }
        for (int i = _drips.Count - 1; i >= 0; i--)
        {
            var d = _drips[i];
            d.Age += delta;
            d.Pos = new Vector2(d.Pos.X, d.Pos.Y + d.Vy * delta);
            d.Length += delta * 18;
        }
        if (_autoFireCool > 0) _autoFireCool -= delta;

        // Palette drag
        if (_paletteDragging)
        {
            PaletteOrigin = mouse - _paletteDragGrab;
            if (leftReleased) _paletteDragging = false;
            return true;
        }

        bool inPalette = ContainsPoint(mouse);
        if (inPalette)
        {
            if (leftPressed) HandlePaletteClick(mouse);
            return true;     // consume any click on the palette
        }

        // Tool application anywhere outside the palette
        ApplyTool(mouse, leftPressed, leftReleased);
        return true;          // capture all mouse traffic while Active
    }

    private void HandlePaletteClick(Vector2 mouse)
    {
        var local = mouse - PaletteOrigin;

        // Title bar = drag handle
        if (local.Y < TitleH)
        {
            _paletteDragging = true;
            _paletteDragGrab = local;
            return;
        }

        // Tool buttons
        for (int i = 0; i < NumTools; i++)
        {
            var rect = ToolRect(i);
            if (RetroSkin.PointInRect(mouse, rect)) { _tool = (Tool)i; return; }
        }

        // Footer buttons
        if (RetroSkin.PointInRect(mouse, ResetRect())) { ClearAll(); return; }
        if (RetroSkin.PointInRect(mouse, StopRect())) { Active = false; ClearAll(); return; }
    }

    private Rectangle ToolRect(int i) => new(
        PaletteOrigin.X + (PaletteW - ToolBtn) / 2,
        PaletteOrigin.Y + TitleH + 6 + i * (ToolBtn + BtnGap),
        ToolBtn, ToolBtn);

    private float FooterY => PaletteOrigin.Y + TitleH + 6 + NumTools * (ToolBtn + BtnGap) + FooterPad;
    private Rectangle ResetRect() => new(PaletteOrigin.X + 8, FooterY, PaletteW - 16, FooterBtnH);
    private Rectangle StopRect() => new(PaletteOrigin.X + 8, FooterY + FooterBtnH + BtnGap, PaletteW - 16, FooterBtnH);

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
        }
    }

    public void Draw()
    {
        if (!Active) return;

        // Damage layers — paint on the full transparent overlay.
        foreach (var s in _slashes) DrawSlash(s);
        foreach (var c in _craters) DrawCrater(c);
        foreach (var h in _holes) DrawHole(h);
        foreach (var d in _drips) DrawDrip(d);
        foreach (var f in _fire) DrawFire(f);

        DrawPalette();
    }

    private void DrawPalette()
    {
        int x = (int)PaletteOrigin.X;
        int y = (int)PaletteOrigin.Y;
        int w = PaletteW, h = PaletteH;

        // Drop shadow
        Raylib.DrawRectangle(x + 4, y + 4, w, h, new Color((byte)0, (byte)0, (byte)0, (byte)100));
        // Body
        var panel = new Rectangle(x, y, w, h);
        RetroSkin.DrawRaised(panel);

        // Title bar
        var title = new Rectangle(x + 3, y + 3, w - 6, TitleH - 4);
        Raylib.DrawRectangleGradientH((int)title.X, (int)title.Y, (int)title.Width, (int)title.Height,
            new Color((byte)96, (byte)24, (byte)24, (byte)255),
            new Color((byte)200, (byte)64, (byte)48, (byte)255));
        RetroSkin.DrawText("DESTROY", (int)title.X + 4, (int)title.Y, new Color((byte)255, (byte)240, (byte)200, (byte)255), 12);

        // Tool buttons
        for (int i = 0; i < NumTools; i++)
        {
            var r = ToolRect(i);
            bool selected = (Tool)i == _tool;
            if (selected) RetroSkin.DrawPressed(r);
            else RetroSkin.DrawRaised(r);
            DrawToolIcon(i, r);
        }

        // Footer buttons
        var resetR = ResetRect();
        RetroSkin.DrawRaised(resetR);
        int rw = RetroSkin.MeasureText("Reset", 13);
        RetroSkin.DrawText("Reset",
            (int)(resetR.X + (resetR.Width - rw) / 2),
            (int)(resetR.Y + (resetR.Height - 13) / 2),
            RetroSkin.BodyText, 13);

        var stopR = StopRect();
        RetroSkin.DrawRaised(stopR);
        int sw = RetroSkin.MeasureText("Stop", 13);
        RetroSkin.DrawText("Stop",
            (int)(stopR.X + (stopR.Width - sw) / 2),
            (int)(stopR.Y + (stopR.Height - 13) / 2),
            new Color((byte)180, (byte)40, (byte)40, (byte)255), 13);
    }

    private static void DrawToolIcon(int i, Rectangle r)
    {
        int cx = (int)(r.X + r.Width / 2);
        int cy = (int)(r.Y + r.Height / 2);
        var ink = new Color((byte)24, (byte)24, (byte)24, (byte)255);
        switch (i)
        {
            case 0: // Pistol
                Raylib.DrawRectangle(cx - 14, cy - 4, 18, 7, ink);
                Raylib.DrawRectangle(cx - 5, cy + 3, 7, 10, ink);
                Raylib.DrawRectangle(cx + 4, cy - 5, 4, 3, new Color((byte)96, (byte)96, (byte)96, (byte)255));
                break;
            case 1: // Machine gun
                Raylib.DrawRectangle(cx - 16, cy - 3, 22, 6, ink);
                Raylib.DrawCircle(cx - 4, cy + 6, 6, ink);
                Raylib.DrawRectangle(cx - 10, cy + 3, 12, 9, ink);
                Raylib.DrawCircle(cx - 4, cy + 6, 3, new Color((byte)96, (byte)96, (byte)96, (byte)255));
                break;
            case 2: // Chainsaw
                Raylib.DrawRectangle(cx - 18, cy - 6, 14, 14, new Color((byte)220, (byte)96, (byte)32, (byte)255));
                Raylib.DrawRectangle(cx - 6, cy - 2, 22, 6, new Color((byte)140, (byte)140, (byte)150, (byte)255));
                for (int t = 0; t < 6; t++)
                    Raylib.DrawTriangle(
                        new Vector2(cx - 6 + t * 4, cy + 4),
                        new Vector2(cx - 4 + t * 4, cy + 8),
                        new Vector2(cx - 2 + t * 4, cy + 4),
                        ink);
                break;
            case 3: // Flame
                Raylib.DrawTriangle(
                    new Vector2(cx, cy - 16),
                    new Vector2(cx - 12, cy + 12),
                    new Vector2(cx + 12, cy + 12),
                    new Color((byte)232, (byte)80, (byte)32, (byte)255));
                Raylib.DrawTriangle(
                    new Vector2(cx, cy - 8),
                    new Vector2(cx - 7, cy + 10),
                    new Vector2(cx + 7, cy + 10),
                    new Color((byte)255, (byte)200, (byte)64, (byte)255));
                break;
            case 4: // Mallet
                Raylib.DrawRectangle(cx - 14, cy - 10, 28, 12, new Color((byte)120, (byte)80, (byte)40, (byte)255));
                Raylib.DrawRectangle(cx - 12, cy - 8, 24, 8, new Color((byte)180, (byte)130, (byte)70, (byte)255));
                Raylib.DrawRectangle(cx - 2, cy + 2, 4, 16, new Color((byte)80, (byte)56, (byte)24, (byte)255));
                break;
            case 5: // Acid drop
                Raylib.DrawCircle(cx, cy + 4, 8, new Color((byte)80, (byte)200, (byte)80, (byte)255));
                Raylib.DrawTriangle(
                    new Vector2(cx, cy - 14),
                    new Vector2(cx - 6, cy + 2),
                    new Vector2(cx + 6, cy + 2),
                    new Color((byte)80, (byte)200, (byte)80, (byte)255));
                Raylib.DrawCircle(cx - 2, cy + 2, 2, new Color((byte)220, (byte)255, (byte)200, (byte)255));
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
                Raylib.DrawPixel((int)p.X + dx, (int)p.Y + dy, new Color((byte)220, (byte)180, (byte)100, (byte)220));
            }
        }
    }

    private static void DrawFire(FireParticle f)
    {
        int cx = (int)f.Pos.X;
        int cy = (int)(f.Pos.Y - f.Age * 18);
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
}
