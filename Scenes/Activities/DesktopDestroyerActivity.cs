using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Desktop Destroyer-style toy: a cartoon "desktop" you can vandalize with a
/// rotating set of weapons. Click the canvas to apply the active tool; damage
/// persists until you hit Reset. All effects are drawn procedurally — bullet
/// holes get jagged crack patterns, fire spreads and fades into smoke, the
/// chainsaw carves a polyline, mallets crater, acid drips downward over time.
/// </summary>
public class DesktopDestroyerActivity : IActivity
{
    private const int FrameInset = 3;
    private const int CanvasW = 600;
    private const int CanvasH = 360;
    private const int PaletteW = 90;
    private const int PalettePad = 8;
    private const int ToolBtn = 64;

    public Vector2 PanelSize => new(
        2 * FrameInset + PaletteW + CanvasW,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + CanvasH + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private enum Tool { Pistol, MachineGun, Chainsaw, Flame, Mallet, Acid }
    private Tool _tool = Tool.Pistol;

    private record struct BulletHole(Vector2 Pos, float Size, int Seed);
    private record struct Crater(Vector2 Pos, float Size, int Seed);
    private record class FireParticle(Vector2 Pos, float Life)
    { public float Age; public float Radius = 4 + (float)(new Random().NextDouble() * 4); public int Seed = new Random().Next(); }
    private class AcidDrip
    {
        public Vector2 Pos;
        public float Age;
        public float Length = 6;
        public float Vy = 14;
        public AcidDrip(Vector2 pos) { Pos = pos; }
    }
    private record class Slash(List<Vector2> Points);

    private readonly List<BulletHole> _holes = new();
    private readonly List<Crater> _craters = new();
    private readonly List<FireParticle> _fire = new();
    private readonly List<AcidDrip> _drips = new();
    private readonly List<Slash> _slashes = new();
    private Slash? _activeSlash;
    private float _autoFireCool;
    private readonly Random _rng = new();
    private int _hits;

    private readonly RetroHelp _help = new()
    {
        Title = "Desktop Destroyer — How to play",
        Lines = new[]
        {
            "Pick a tool from the side palette, then go to town on the canvas.",
            "Pistol: one bullet hole per click.",
            "Machine gun: holes spray while you hold the mouse.",
            "Chainsaw: click and drag to carve a polyline gash.",
            "Flame: fire spreads, ages into smoke.",
            "Mallet: a crater with radiating cracks.",
            "Acid: green drips that ooze downward over time.",
            "Reset wipes the canvas clean.",
        },
    };

    public void Load() { }

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
        switch (RetroWidgets.MenuBarHitTest(menuBar, new[] { "Reset", "Help" }, local, leftPressed))
        {
            case 0: ClearAll(); return;
            case 1: _help.Visible = !_help.Visible; return;
        }
        if (_help.HandleInput(local, leftPressed, PanelSize)) return;

        // Tool palette
        for (int i = 0; i < 7; i++)
        {
            var rect = ToolRectLocal(i);
            if (leftPressed && RetroSkin.PointInRect(local, rect))
            {
                if (i == 6) ClearAll();
                else _tool = (Tool)i;
                return;
            }
        }

        // Canvas interaction
        var (cx, cy) = CanvasOriginLocal();
        var canvasRect = new Rectangle(cx, cy, CanvasW, CanvasH);
        var canvasMouse = new Vector2(local.X - cx, local.Y - cy);
        bool inCanvas = RetroSkin.PointInRect(local, canvasRect);

        // Tick effects
        for (int i = _fire.Count - 1; i >= 0; i--)
        {
            var f = _fire[i];
            f.Age += delta;
            if (f.Age >= f.Life) _fire.RemoveAt(i);
        }
        for (int i = _drips.Count - 1; i >= 0; i--)
        {
            var d = _drips[i];
            d.Age += delta;
            d.Pos = new Vector2(d.Pos.X, d.Pos.Y + d.Vy * delta);
            d.Length += delta * 18;
            if (d.Pos.Y > CanvasH + 20) _drips.RemoveAt(i);
        }

        if (_autoFireCool > 0) _autoFireCool -= delta;

        // Tool actions
        if (inCanvas)
        {
            switch (_tool)
            {
                case Tool.Pistol:
                    if (leftPressed)
                    { _holes.Add(new BulletHole(canvasMouse, 8, _rng.Next())); _hits++; }
                    break;
                case Tool.MachineGun:
                    if (Raylib.IsMouseButtonDown(MouseButton.Left) && _autoFireCool <= 0)
                    {
                        var jitter = new Vector2((float)(_rng.NextDouble() - 0.5) * 18,
                                                 (float)(_rng.NextDouble() - 0.5) * 18);
                        _holes.Add(new BulletHole(canvasMouse + jitter, 6, _rng.Next()));
                        _autoFireCool = 0.04f;
                        _hits++;
                    }
                    break;
                case Tool.Chainsaw:
                    if (leftPressed)
                    {
                        _activeSlash = new Slash(new List<Vector2> { canvasMouse });
                        _slashes.Add(_activeSlash);
                    }
                    if (_activeSlash != null && Raylib.IsMouseButtonDown(MouseButton.Left))
                    {
                        var last = _activeSlash.Points[^1];
                        if (Vector2.Distance(canvasMouse, last) > 4)
                        { _activeSlash.Points.Add(canvasMouse); _hits++; }
                    }
                    if (leftReleased) _activeSlash = null;
                    break;
                case Tool.Flame:
                    if (Raylib.IsMouseButtonDown(MouseButton.Left) && _autoFireCool <= 0)
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            var jitter = new Vector2((float)(_rng.NextDouble() - 0.5) * 22,
                                                     (float)(_rng.NextDouble() - 0.5) * 22);
                            _fire.Add(new FireParticle(canvasMouse + jitter, 1.4f + (float)_rng.NextDouble()));
                        }
                        _autoFireCool = 0.05f;
                        _hits++;
                    }
                    break;
                case Tool.Mallet:
                    if (leftPressed)
                    { _craters.Add(new Crater(canvasMouse, 22, _rng.Next())); _hits += 3; }
                    break;
                case Tool.Acid:
                    if (leftPressed)
                    { _drips.Add(new AcidDrip(canvasMouse)); _hits++; }
                    break;
            }
        }
    }

    private void ClearAll()
    {
        _holes.Clear();
        _craters.Clear();
        _fire.Clear();
        _drips.Clear();
        _slashes.Clear();
        _activeSlash = null;
        _hits = 0;
    }

    private (float x, float y) CanvasOriginLocal() => (
        FrameInset + PaletteW,
        FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight);

    private Rectangle ToolRectLocal(int idx)
    {
        float x = FrameInset + PalettePad;
        float y = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + PalettePad
            + idx * (ToolBtn + 2);
        return new Rectangle(x, y, ToolBtn, ToolBtn);
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Desktop Destroyer", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "Reset", "Help" }, -1);

        // Palette strip
        var palette = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight,
            PaletteW, CanvasH);
        RetroSkin.DrawSunken(palette, RetroSkin.Face);

        for (int i = 0; i < 7; i++)
        {
            var local = ToolRectLocal(i);
            var rect = new Rectangle(panelOffset.X + local.X, panelOffset.Y + local.Y,
                local.Width, local.Height);
            bool selected = i < 6 && (Tool)i == _tool;
            if (selected) RetroSkin.DrawPressed(rect);
            else RetroSkin.DrawRaised(rect);
            DrawToolIcon(i, rect);
        }

        // Canvas (the "desktop")
        var (cx, cy) = CanvasOriginLocal();
        var canvasAbs = new Rectangle(panelOffset.X + cx, panelOffset.Y + cy, CanvasW, CanvasH);
        DrawDesktop(canvasAbs);

        // Damage layers
        foreach (var s in _slashes) DrawSlash(canvasAbs, s);
        foreach (var c in _craters) DrawCrater(canvasAbs, c);
        foreach (var h in _holes) DrawHole(canvasAbs, h);
        foreach (var d in _drips) DrawDrip(canvasAbs, d);
        foreach (var f in _fire) DrawFire(canvasAbs, f);

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(status,
            $"Tool: {_tool}", $"Hits: {_hits}");

        _help.Draw(panelOffset, PanelSize);
    }

    private void DrawToolIcon(int i, Rectangle r)
    {
        int cx = (int)(r.X + r.Width / 2);
        int cy = (int)(r.Y + r.Height / 2);
        switch (i)
        {
            case 0: // Pistol — chunky L shape
                Raylib.DrawRectangle(cx - 16, cy - 4, 22, 8, RetroSkin.BodyText);
                Raylib.DrawRectangle(cx - 6, cy + 4, 8, 12, RetroSkin.BodyText);
                Raylib.DrawRectangle(cx + 6, cy - 6, 4, 4, new Color(96, 96, 96, 255));
                break;
            case 1: // Machine gun — longer barrel + drum
                Raylib.DrawRectangle(cx - 18, cy - 3, 28, 6, RetroSkin.BodyText);
                Raylib.DrawCircle(cx - 4, cy + 6, 6, RetroSkin.BodyText);
                Raylib.DrawRectangle(cx - 10, cy + 3, 12, 10, RetroSkin.BodyText);
                Raylib.DrawCircle(cx - 4, cy + 6, 3, new Color(96, 96, 96, 255));
                break;
            case 2: // Chainsaw — body + bar with teeth
                Raylib.DrawRectangle(cx - 20, cy - 6, 14, 14, new Color(220, 96, 32, 255));
                Raylib.DrawRectangle(cx - 6, cy - 2, 22, 6, new Color(140, 140, 150, 255));
                for (int t = 0; t < 6; t++)
                    Raylib.DrawTriangle(
                        new Vector2(cx - 6 + t * 4, cy + 4),
                        new Vector2(cx - 4 + t * 4, cy + 8),
                        new Vector2(cx - 2 + t * 4, cy + 4),
                        RetroSkin.BodyText);
                break;
            case 3: // Flame — triangular flame body
                Raylib.DrawTriangle(
                    new Vector2(cx, cy - 16),
                    new Vector2(cx - 12, cy + 12),
                    new Vector2(cx + 12, cy + 12),
                    new Color(232, 80, 32, 255));
                Raylib.DrawTriangle(
                    new Vector2(cx, cy - 8),
                    new Vector2(cx - 7, cy + 10),
                    new Vector2(cx + 7, cy + 10),
                    new Color(255, 200, 64, 255));
                break;
            case 4: // Mallet
                Raylib.DrawRectangle(cx - 14, cy - 10, 28, 12, new Color(120, 80, 40, 255));
                Raylib.DrawRectangle(cx - 12, cy - 8, 24, 8, new Color(180, 130, 70, 255));
                Raylib.DrawRectangle(cx - 2, cy + 2, 4, 16, new Color(80, 56, 24, 255));
                break;
            case 5: // Acid — drop
                Raylib.DrawCircle(cx, cy + 4, 8, new Color(80, 200, 80, 255));
                Raylib.DrawTriangle(
                    new Vector2(cx, cy - 14),
                    new Vector2(cx - 6, cy + 2),
                    new Vector2(cx + 6, cy + 2),
                    new Color(80, 200, 80, 255));
                Raylib.DrawCircle(cx - 2, cy + 2, 2, new Color(220, 255, 200, 255));
                break;
            case 6: // Reset
                Raylib.DrawCircleLines(cx, cy, 14, RetroSkin.BodyText);
                Raylib.DrawCircleLines(cx, cy, 13, RetroSkin.BodyText);
                // Arrow tip
                Raylib.DrawTriangle(
                    new Vector2(cx + 14, cy),
                    new Vector2(cx + 8, cy - 6),
                    new Vector2(cx + 8, cy + 6),
                    RetroSkin.BodyText);
                RetroSkin.DrawText("RESET", cx - 18, cy + 18, RetroSkin.BodyText, 11);
                break;
        }
    }

    private static void DrawDesktop(Rectangle r)
    {
        // Teal Win9x backdrop
        Raylib.DrawRectangleRec(r, new Color(0, 128, 128, 255));

        // Fake icons in a column on the left
        int iconX = (int)r.X + 16;
        int iconY = (int)r.Y + 16;
        for (int i = 0; i < 4; i++)
        {
            int y = iconY + i * 56;
            Raylib.DrawRectangle(iconX, y, 30, 26, new Color(192, 192, 192, 255));
            Raylib.DrawRectangle(iconX + 6, y + 4, 18, 4, new Color(80, 80, 80, 255));
            Raylib.DrawRectangleLines(iconX, y, 30, 26, new Color(80, 80, 80, 255));
            string lbl = i switch { 0 => "PC", 1 => "Bin", 2 => "Net", _ => "Doc" };
            int lw = RetroSkin.MeasureText(lbl, 12);
            RetroSkin.DrawText(lbl, iconX + (30 - lw) / 2, y + 30,
                new Color(255, 255, 255, 255), 12);
        }

        // A "window" floating on the desktop
        var winRect = new Rectangle(r.X + 130, r.Y + 60, 220, 160);
        RetroSkin.DrawRaised(winRect);
        var winTitle = new Rectangle(winRect.X + 3, winRect.Y + 3, winRect.Width - 6, 16);
        Raylib.DrawRectangleGradientH((int)winTitle.X, (int)winTitle.Y,
            (int)winTitle.Width, (int)winTitle.Height,
            new Color(0, 0, 128, 255), new Color(16, 132, 208, 255));
        RetroSkin.DrawText("Untitled - Document",
            (int)winTitle.X + 4, (int)winTitle.Y + 1, new Color(255, 255, 255, 255), 12);
        // Window inner sunken text area
        var inner = new Rectangle(winRect.X + 8, winRect.Y + 28,
            winRect.Width - 16, winRect.Height - 36);
        RetroSkin.DrawSunken(inner, new Color(255, 255, 255, 255));

        // Bottom taskbar
        var bar = new Rectangle(r.X, r.Y + r.Height - 22, r.Width, 22);
        RetroSkin.DrawRaised(bar);
        var startBtn = new Rectangle(bar.X + 4, bar.Y + 3, 60, 16);
        RetroSkin.DrawRaised(startBtn);
        RetroSkin.DrawText("Start", (int)startBtn.X + 8, (int)startBtn.Y, RetroSkin.BodyText, 12);
    }

    private static void DrawHole(Rectangle canvas, BulletHole h)
    {
        int cx = (int)(canvas.X + h.Pos.X);
        int cy = (int)(canvas.Y + h.Pos.Y);
        Raylib.DrawCircle(cx, cy, h.Size, new Color(0, 0, 0, 255));
        // Cracks — short jagged lines radiating out, deterministic by Seed
        var rng = new Random(h.Seed);
        int cracks = 6 + rng.Next(4);
        for (int i = 0; i < cracks; i++)
        {
            float ang = (float)(rng.NextDouble() * Math.PI * 2);
            float len = h.Size + 4 + (float)rng.NextDouble() * 12;
            int ex = cx + (int)(MathF.Cos(ang) * len);
            int ey = cy + (int)(MathF.Sin(ang) * len);
            Raylib.DrawLine(cx, cy, ex, ey, RetroSkin.BodyText);
            // Tiny side branch
            float ang2 = ang + (float)((rng.NextDouble() - 0.5) * 0.6);
            float len2 = len * 0.5f;
            int mx = cx + (int)(MathF.Cos(ang) * len2);
            int my = cy + (int)(MathF.Sin(ang) * len2);
            int bx = mx + (int)(MathF.Cos(ang2) * 4);
            int by = my + (int)(MathF.Sin(ang2) * 4);
            Raylib.DrawLine(mx, my, bx, by, RetroSkin.BodyText);
        }
    }

    private static void DrawCrater(Rectangle canvas, Crater c)
    {
        int cx = (int)(canvas.X + c.Pos.X);
        int cy = (int)(canvas.Y + c.Pos.Y);
        Raylib.DrawCircle(cx, cy, c.Size, new Color(40, 40, 40, 255));
        Raylib.DrawCircle(cx, cy, c.Size - 4, new Color(20, 20, 20, 255));
        var rng = new Random(c.Seed);
        int cracks = 10;
        for (int i = 0; i < cracks; i++)
        {
            float ang = i * MathF.PI * 2 / cracks + (float)rng.NextDouble() * 0.3f;
            float len = c.Size + 8 + (float)rng.NextDouble() * 18;
            // Two-segment zigzag
            float midLen = len * 0.6f;
            int mx = cx + (int)(MathF.Cos(ang) * midLen);
            int my = cy + (int)(MathF.Sin(ang) * midLen);
            float ang2 = ang + (float)((rng.NextDouble() - 0.5) * 0.7);
            int ex = mx + (int)(MathF.Cos(ang2) * (len - midLen));
            int ey = my + (int)(MathF.Sin(ang2) * (len - midLen));
            Raylib.DrawLine(cx, cy, mx, my, RetroSkin.BodyText);
            Raylib.DrawLine(mx, my, ex, ey, RetroSkin.BodyText);
        }
    }

    private static void DrawSlash(Rectangle canvas, Slash s)
    {
        if (s.Points.Count < 2) return;
        for (int i = 1; i < s.Points.Count; i++)
        {
            var a = canvas.Position() + s.Points[i - 1];
            var b = canvas.Position() + s.Points[i];
            // Wide gash
            Raylib.DrawLineEx(a, b, 6f, RetroSkin.BodyText);
            Raylib.DrawLineEx(a, b, 3f, new Color(48, 24, 16, 255));
        }
        // Sawdust dots near the cuts
        var rng = new Random(s.Points.Count);
        for (int i = 0; i < s.Points.Count; i++)
        {
            var p = canvas.Position() + s.Points[i];
            for (int k = 0; k < 3; k++)
            {
                int dx = rng.Next(-8, 9), dy = rng.Next(-8, 9);
                Raylib.DrawPixel((int)p.X + dx, (int)p.Y + dy, new Color(220, 180, 100, 220));
            }
        }
    }

    private static void DrawFire(Rectangle canvas, FireParticle f)
    {
        int cx = (int)(canvas.X + f.Pos.X);
        int cy = (int)(canvas.Y + f.Pos.Y - f.Age * 18);
        float t = f.Age / f.Life;
        Color col;
        if (t < 0.4f) col = new Color((byte)255, (byte)200, (byte)40, (byte)(255 - t * 100));
        else if (t < 0.75f) col = new Color((byte)220, (byte)80, (byte)32, (byte)(255 - (t - 0.4f) * 200));
        else col = new Color((byte)96, (byte)96, (byte)96, (byte)Math.Max(0, 200 - (t - 0.75f) * 800));
        float radius = f.Radius * (t < 0.5f ? 1 + t : 1.5f - (t - 0.5f) * 0.4f);
        Raylib.DrawCircle(cx, cy, radius, col);
        // Soft inner highlight
        if (t < 0.5f)
            Raylib.DrawCircle(cx, cy, radius * 0.5f,
                new Color((byte)255, (byte)240, (byte)160, (byte)180));
    }

    private static void DrawDrip(Rectangle canvas, AcidDrip d)
    {
        int cx = (int)(canvas.X + d.Pos.X);
        int cy = (int)(canvas.Y + d.Pos.Y);
        // Drip body — vertical streak
        Raylib.DrawRectangle(cx - 2, cy - (int)d.Length, 4, (int)d.Length,
            new Color(96, 200, 96, 255));
        // Bead at the bottom
        Raylib.DrawCircle(cx, cy, 4, new Color(80, 220, 80, 255));
        Raylib.DrawCircle(cx - 1, cy - 1, 1, new Color(220, 255, 200, 220));
    }

    public void Close() { }
}

internal static class RectangleExt
{
    public static Vector2 Position(this Rectangle r) => new(r.X, r.Y);
}
