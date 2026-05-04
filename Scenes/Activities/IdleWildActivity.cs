using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Screensaver collection. Modules render full-panel and auto-cycle on a
/// timer. Each module is a tiny class with Reset / Update / Draw — adding a
/// new one is a few dozen lines.
/// </summary>
public class IdleWildActivity : IActivity
{
    private const int FrameInset = 3;
    private const int CanvasW = 360;
    private const int CanvasH = 240;
    private const float CycleSeconds = 18f;

    public Vector2 PanelSize => new(
        2 * FrameInset + CanvasW,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + CanvasH + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private readonly List<IScreensaver> _modules;
    private int _idx;
    private float _cycleTimer;
    private bool _autoCycle = true;
    private readonly Random _rng = new();

    public IdleWildActivity()
    {
        _modules = new List<IScreensaver>
        {
            new MystifyModule(),
            new StarfieldModule(),
            new BezierModule(),
            new MarqueeModule(),
            new PipesModule(),
            new MazeModule(),
        };
        _modules[0].Reset(_rng, CanvasW, CanvasH);
    }

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
        int m = RetroWidgets.MenuBarHitTest(menuBar,
            new[] { "Next", _autoCycle ? "Hold" : "Cycle" }, local, leftPressed);
        if (m == 0) NextModule();
        else if (m == 1) _autoCycle = !_autoCycle;

        if (_autoCycle)
        {
            _cycleTimer += delta;
            if (_cycleTimer >= CycleSeconds) NextModule();
        }

        _modules[_idx].Update(delta, _rng, CanvasW, CanvasH);
    }

    private void NextModule()
    {
        _idx = (_idx + 1) % _modules.Count;
        _modules[_idx].Reset(_rng, CanvasW, CanvasH);
        _cycleTimer = 0;
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, $"IdleWild — {_modules[_idx].Name}", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "Next", _autoCycle ? "Hold" : "Cycle" }, -1);

        // Black canvas
        var canvas = new Rectangle(
            panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight,
            CanvasW, CanvasH);
        Raylib.DrawRectangleRec(canvas, Color.Black);

        // Scissor so module drawing stays inside canvas
        Raylib.BeginScissorMode((int)canvas.X, (int)canvas.Y, (int)canvas.Width, (int)canvas.Height);
        _modules[_idx].Draw(new Vector2(canvas.X, canvas.Y), CanvasW, CanvasH);
        Raylib.EndScissorMode();

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(status,
            $"{_idx + 1}/{_modules.Count}  {_modules[_idx].Name}",
            _autoCycle ? $"auto {Math.Max(0, (int)(CycleSeconds - _cycleTimer))}s" : "held");
    }

    public void Close() { }
}

interface IScreensaver
{
    string Name { get; }
    void Reset(Random rng, int w, int h);
    void Update(float dt, Random rng, int w, int h);
    void Draw(Vector2 origin, int w, int h);
}

class MystifyModule : IScreensaver
{
    public string Name => "Mystify";
    private const int Verts = 4;
    private const int Trail = 12;
    private Vector2[][] _trail = Array.Empty<Vector2[]>();
    private Vector2[] _vel = new Vector2[Verts];
    private Color[] _colors = new Color[2];

    public void Reset(Random rng, int w, int h)
    {
        _trail = new Vector2[Trail][];
        var first = new Vector2[Verts];
        for (int i = 0; i < Verts; i++)
        {
            first[i] = new Vector2(rng.Next(w), rng.Next(h));
            _vel[i] = new Vector2((rng.NextSingle() - 0.5f) * 200, (rng.NextSingle() - 0.5f) * 200);
        }
        for (int t = 0; t < Trail; t++) _trail[t] = (Vector2[])first.Clone();
        _colors[0] = HsvColor(rng.NextSingle(), 0.9f, 1f);
        _colors[1] = HsvColor(rng.NextSingle(), 0.9f, 1f);
    }

    public void Update(float dt, Random rng, int w, int h)
    {
        for (int t = Trail - 1; t > 0; t--) _trail[t] = (Vector2[])_trail[t - 1].Clone();
        var head = _trail[0];
        for (int i = 0; i < Verts; i++)
        {
            head[i] += _vel[i] * dt;
            if (head[i].X < 0 || head[i].X > w) _vel[i].X = -_vel[i].X;
            if (head[i].Y < 0 || head[i].Y > h) _vel[i].Y = -_vel[i].Y;
            head[i].X = Math.Clamp(head[i].X, 0, w);
            head[i].Y = Math.Clamp(head[i].Y, 0, h);
        }
    }

    public void Draw(Vector2 origin, int w, int h)
    {
        for (int t = 0; t < Trail; t++)
        {
            float u = 1f - t / (float)Trail;
            var c = LerpColor(_colors[0], _colors[1], (float)t / Trail);
            c.A = (byte)(255 * u);
            for (int i = 0; i < Verts; i++)
            {
                var a = origin + _trail[t][i];
                var b = origin + _trail[t][(i + 1) % Verts];
                Raylib.DrawLineV(a, b, c);
            }
        }
    }

    private static Color LerpColor(Color a, Color b, float t)
        => new(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t),
            (byte)255);

    public static Color HsvColor(float h, float s, float v)
    {
        int hi = (int)(h * 6) % 6;
        float f = h * 6 - (int)(h * 6);
        float p = v * (1 - s), q = v * (1 - f * s), tt = v * (1 - (1 - f) * s);
        float r, g, b;
        switch (hi)
        {
            case 0: r = v; g = tt; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = tt; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = tt; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
        return new Color((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), (byte)255);
    }
}

class StarfieldModule : IScreensaver
{
    public string Name => "Starfield";
    private (float x, float y, float z)[] _stars = Array.Empty<(float, float, float)>();

    public void Reset(Random rng, int w, int h)
    {
        _stars = new (float, float, float)[180];
        for (int i = 0; i < _stars.Length; i++)
            _stars[i] = ((rng.NextSingle() - 0.5f) * w * 4,
                         (rng.NextSingle() - 0.5f) * h * 4,
                          rng.NextSingle() * w);
    }

    public void Update(float dt, Random rng, int w, int h)
    {
        for (int i = 0; i < _stars.Length; i++)
        {
            var s = _stars[i];
            s.z -= dt * 120f;
            if (s.z <= 1)
            {
                s.x = (rng.NextSingle() - 0.5f) * w * 4;
                s.y = (rng.NextSingle() - 0.5f) * h * 4;
                s.z = w;
            }
            _stars[i] = s;
        }
    }

    public void Draw(Vector2 origin, int w, int h)
    {
        float cx = w / 2f, cy = h / 2f;
        foreach (var s in _stars)
        {
            float k = 100f / s.z;
            float sx = cx + s.x * k;
            float sy = cy + s.y * k;
            byte b = (byte)Math.Clamp(255 - s.z, 80, 255);
            int sz = s.z < 20 ? 2 : 1;
            if (sx >= 0 && sx < w && sy >= 0 && sy < h)
                Raylib.DrawRectangle((int)(origin.X + sx), (int)(origin.Y + sy), sz, sz, new Color(b, b, b, (byte)255));
        }
    }
}

class BezierModule : IScreensaver
{
    public string Name => "Bezier";
    private Vector2[] _pts = new Vector2[4];
    private Vector2[] _vel = new Vector2[4];
    private List<(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color col)> _trail = new();
    private float _hue;

    public void Reset(Random rng, int w, int h)
    {
        for (int i = 0; i < 4; i++)
        {
            _pts[i] = new Vector2(rng.Next(w), rng.Next(h));
            _vel[i] = new Vector2((rng.NextSingle() - 0.5f) * 160, (rng.NextSingle() - 0.5f) * 160);
        }
        _trail.Clear();
        _hue = rng.NextSingle();
    }

    public void Update(float dt, Random rng, int w, int h)
    {
        for (int i = 0; i < 4; i++)
        {
            _pts[i] += _vel[i] * dt;
            if (_pts[i].X < 0 || _pts[i].X > w) _vel[i].X = -_vel[i].X;
            if (_pts[i].Y < 0 || _pts[i].Y > h) _vel[i].Y = -_vel[i].Y;
        }
        _hue = (_hue + dt * 0.15f) % 1f;
        _trail.Add((_pts[0], _pts[1], _pts[2], _pts[3], MystifyModule.HsvColor(_hue, 0.85f, 1f)));
        if (_trail.Count > 60) _trail.RemoveAt(0);
    }

    public void Draw(Vector2 origin, int w, int h)
    {
        for (int i = 0; i < _trail.Count; i++)
        {
            var t = _trail[i];
            byte a = (byte)(255 * (i + 1) / _trail.Count);
            var col = new Color(t.col.R, t.col.G, t.col.B, a);
            Raylib.DrawSplineSegmentBezierCubic(origin + t.a, origin + t.b, origin + t.c, origin + t.d, 1.5f, col);
        }
    }
}

class MarqueeModule : IScreensaver
{
    public string Name => "Marquee";
    private string _text = "";
    private float _x;
    private static readonly string[] Phrases =
    {
        "MOUSEHOUSE",
        "Press any key to wake up the mouse",
        "Hello from 1992",
        "WINDOWS FOR WORKGROUPS",
        "Tetris  Minesweeper  SkiFree  Cruel",
    };

    public void Reset(Random rng, int w, int h)
    {
        _text = Phrases[rng.Next(Phrases.Length)];
        _x = w;
    }

    public void Update(float dt, Random rng, int w, int h)
    {
        _x -= dt * 80;
        int tw = RetroSkin.MeasureText(_text, 32);
        if (_x < -tw) _x = w;
    }

    public void Draw(Vector2 origin, int w, int h)
    {
        int y = h / 2 - 20;
        RetroSkin.DrawText(_text, (int)(origin.X + _x), (int)(origin.Y + y),
            new Color(0, 220, 0, 255), 32);
    }
}

class PipesModule : IScreensaver
{
    public string Name => "Pipes";
    private record struct Cursor(int X, int Y, int Dir, Color Col);
    private List<Cursor> _cursors = new();
    private bool[,] _occ = new bool[1, 1];
    private const int Cell = 12;
    private float _stepTimer;

    public void Reset(Random rng, int w, int h)
    {
        int cw = w / Cell, ch = h / Cell;
        _occ = new bool[cw, ch];
        _cursors.Clear();
        for (int i = 0; i < 3; i++)
            _cursors.Add(new Cursor(rng.Next(cw), rng.Next(ch), rng.Next(4),
                MystifyModule.HsvColor(rng.NextSingle(), 0.8f, 1f)));
        _stepTimer = 0;
    }

    public void Update(float dt, Random rng, int w, int h)
    {
        _stepTimer += dt;
        if (_stepTimer < 0.04f) return;
        _stepTimer = 0;
        int cw = w / Cell, ch = h / Cell;
        for (int i = 0; i < _cursors.Count; i++)
        {
            var c = _cursors[i];
            _occ[c.X, c.Y] = true;
            if (rng.Next(8) == 0) c.Dir = (c.Dir + (rng.Next(2) == 0 ? 1 : 3)) % 4;
            int nx = c.X + (c.Dir == 0 ? 1 : c.Dir == 2 ? -1 : 0);
            int ny = c.Y + (c.Dir == 1 ? 1 : c.Dir == 3 ? -1 : 0);
            if (nx < 0 || nx >= cw || ny < 0 || ny >= ch || _occ[nx, ny])
            {
                int tries = 0;
                while (tries++ < 4)
                {
                    c.Dir = (c.Dir + 1) % 4;
                    nx = c.X + (c.Dir == 0 ? 1 : c.Dir == 2 ? -1 : 0);
                    ny = c.Y + (c.Dir == 1 ? 1 : c.Dir == 3 ? -1 : 0);
                    if (nx >= 0 && nx < cw && ny >= 0 && ny < ch && !_occ[nx, ny]) break;
                }
                if (tries > 4)
                {
                    nx = rng.Next(cw); ny = rng.Next(ch);
                    c.Col = MystifyModule.HsvColor(rng.NextSingle(), 0.8f, 1f);
                }
            }
            c.X = nx; c.Y = ny;
            _cursors[i] = c;
        }
    }

    public void Draw(Vector2 origin, int w, int h)
    {
        int cw = w / Cell, ch = h / Cell;
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
                if (_occ[x, y])
                    Raylib.DrawRectangle((int)origin.X + x * Cell + 2, (int)origin.Y + y * Cell + 2,
                        Cell - 4, Cell - 4, new Color(140, 140, 200, 255));
        foreach (var c in _cursors)
            Raylib.DrawRectangle((int)origin.X + c.X * Cell, (int)origin.Y + c.Y * Cell, Cell, Cell, c.Col);
    }
}

class MazeModule : IScreensaver
{
    public string Name => "Maze";
    private bool[,] _wall = new bool[1, 1];
    private int _gx, _gy;
    private float _stepTimer;
    private List<(int x, int y)> _stack = new();
    private int _cw, _ch;

    public void Reset(Random rng, int w, int h)
    {
        const int Cell = 8;
        _cw = w / Cell; _ch = h / Cell;
        _wall = new bool[_cw, _ch];
        for (int x = 0; x < _cw; x++) for (int y = 0; y < _ch; y++) _wall[x, y] = true;
        _gx = 0; _gy = 0; _wall[0, 0] = false;
        _stack.Clear(); _stack.Add((0, 0));
        _stepTimer = 0;
    }

    public void Update(float dt, Random rng, int w, int h)
    {
        _stepTimer += dt;
        if (_stepTimer < 0.01f || _stack.Count == 0) return;
        _stepTimer = 0;
        var (cx, cy) = _stack[^1];
        var dirs = new[] { (2, 0), (-2, 0), (0, 2), (0, -2) }.OrderBy(_ => rng.Next()).ToArray();
        bool moved = false;
        foreach (var (dx, dy) in dirs)
        {
            int nx = cx + dx, ny = cy + dy;
            if (nx < 0 || nx >= _cw || ny < 0 || ny >= _ch) continue;
            if (!_wall[nx, ny]) continue;
            _wall[nx, ny] = false;
            _wall[cx + dx / 2, cy + dy / 2] = false;
            _stack.Add((nx, ny));
            _gx = nx; _gy = ny;
            moved = true;
            break;
        }
        if (!moved) _stack.RemoveAt(_stack.Count - 1);
    }

    public void Draw(Vector2 origin, int w, int h)
    {
        const int Cell = 8;
        for (int x = 0; x < _cw; x++)
            for (int y = 0; y < _ch; y++)
                if (!_wall[x, y])
                    Raylib.DrawRectangle((int)origin.X + x * Cell, (int)origin.Y + y * Cell,
                        Cell, Cell, new Color(0, 100, 180, 255));
        Raylib.DrawRectangle((int)origin.X + _gx * Cell, (int)origin.Y + _gy * Cell, Cell, Cell,
            new Color(255, 220, 0, 255));
    }
}
