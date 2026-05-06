using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Fuji Golf — top-down 9-hole course over procedurally-rolling terrain. Each
/// hole has a heightmap (sum of Gaussian bumps, amplitude scaled by hole
/// index), pre-rendered to a texture using normal·light shading for the
/// classic Win9x topo-shaded look. The ball rolls in response to the
/// heightmap gradient, so slopes really matter — putts hook around hills,
/// stray shots roll down into bunkers, and the green is flattened so the cup
/// is reachable.
/// </summary>
public class FujiGolfActivity : IActivity
{
    private const int FrameInset = 3;
    private const int CanvasW = 540;
    private const int CanvasH = 360;
    private const int ScoreW = 160;
    private const int Margin = 12;
    private const int Holes = 9;

    // Terrain tuning. GravStrength controls how strongly gradient pushes the
    // ball; KineticFriction is a constant decel that lets the ball settle on
    // small slopes; Viscosity scales with speed.
    private const float GravStrength = 1400f;
    private const float KineticFriction = 12f;
    private const int HeightCellSize = 4;

    public Vector2 PanelSize => new(
        2 * FrameInset + CanvasW + ScoreW + Margin,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + CanvasH + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    // ── Heightmap ────────────────────────────────────────────────────────
    private class HeightField
    {
        public readonly int Cols, Rows;
        public readonly float CellSize;
        public readonly float[,] H;

        public HeightField(int cols, int rows, float cellSize)
        {
            Cols = cols; Rows = rows; CellSize = cellSize;
            H = new float[cols, rows];
        }

        /// <summary>Bilinear-sample the height at canvas pixel (x, y).</summary>
        public float Sample(float x, float y)
        {
            float gx = x / CellSize, gy = y / CellSize;
            int ix = Math.Clamp((int)MathF.Floor(gx), 0, Cols - 2);
            int iy = Math.Clamp((int)MathF.Floor(gy), 0, Rows - 2);
            float fx = Math.Clamp(gx - ix, 0, 1);
            float fy = Math.Clamp(gy - iy, 0, 1);
            float h00 = H[ix, iy], h10 = H[ix + 1, iy];
            float h01 = H[ix, iy + 1], h11 = H[ix + 1, iy + 1];
            float a = h00 + (h10 - h00) * fx;
            float b = h01 + (h11 - h01) * fx;
            return a + (b - a) * fy;
        }

        /// <summary>Numerical gradient (∂h/∂x, ∂h/∂y) at canvas pixel (x, y).</summary>
        public Vector2 Gradient(float x, float y)
        {
            const float eps = 1.5f;
            float dx = (Sample(x + eps, y) - Sample(x - eps, y)) / (2 * eps);
            float dy = (Sample(x, y + eps) - Sample(x, y - eps)) / (2 * eps);
            return new Vector2(dx, dy);
        }

        /// <summary>Add a Gaussian bump centered at grid (cx, cy).</summary>
        public void AddBump(float cx, float cy, float amp, float radius)
        {
            float r2inv = 1f / (2 * radius * radius);
            // Bound the loop to where the bump has any effect (3σ).
            int xMin = Math.Max(0, (int)(cx - radius * 3));
            int xMax = Math.Min(Cols - 1, (int)(cx + radius * 3));
            int yMin = Math.Max(0, (int)(cy - radius * 3));
            int yMax = Math.Min(Rows - 1, (int)(cy + radius * 3));
            for (int x = xMin; x <= xMax; x++)
                for (int y = yMin; y <= yMax; y++)
                {
                    float ddx = x - cx, ddy = y - cy;
                    H[x, y] += amp * MathF.Exp(-(ddx * ddx + ddy * ddy) * r2inv);
                }
        }

        /// <summary>Smoothly pull the height in a region toward zero (used to flatten tee/green).</summary>
        public void Flatten(float cxPx, float cyPx, float radiusPx, float blend = 1f)
        {
            float cx = cxPx / CellSize;
            float cy = cyPx / CellSize;
            float radius = radiusPx / CellSize;
            float r2inv = 1f / (radius * radius);
            int xMin = Math.Max(0, (int)(cx - radius * 2));
            int xMax = Math.Min(Cols - 1, (int)(cx + radius * 2));
            int yMin = Math.Max(0, (int)(cy - radius * 2));
            int yMax = Math.Min(Rows - 1, (int)(cy + radius * 2));
            for (int x = xMin; x <= xMax; x++)
                for (int y = yMin; y <= yMax; y++)
                {
                    float ddx = x - cx, ddy = y - cy;
                    float w = MathF.Exp(-(ddx * ddx + ddy * ddy) * r2inv) * blend;
                    H[x, y] *= 1f - w;
                }
        }
    }

    // ── State ────────────────────────────────────────────────────────────
    private record class HoleLayout(
        Vector2 Tee,
        Vector2 Cup,
        int Par,
        List<Vector2> Trees,
        List<(Vector2 Center, float Rx, float Ry, int Kind)> Hazards,
        HeightField Heightmap);

    private List<HoleLayout> _course = new();
    private Texture2D[] _terrainTex = Array.Empty<Texture2D>();
    private bool[] _terrainBuilt = Array.Empty<bool>();
    private int[] _strokes = new int[Holes];
    private int _holeIdx;
    private Vector2 _ball;
    private Vector2 _vel;
    private bool _aiming;
    private Vector2 _aimEnd;
    private bool _holeComplete;
    private bool _roundComplete;
    private float _holeFlashTimer;
    private List<Vector2> _trail = new();
    private float _trailDropTimer;
    private readonly Random _rng = new();

    private readonly RetroHelp _help = new()
    {
        Title = "Fuji Golf — How to play",
        Lines = new[]
        {
            "Sink the ball in nine holes with as few strokes as possible.",
            "Drag from the ball to aim, longer drag = more power, release to hit.",
            "Each hole has rolling terrain: bright slopes face up, dark slopes face",
            "into the ground. The ball rolls downhill — read the shading.",
            "Trees bounce, water costs a penalty stroke, sand drags you down.",
            "Later holes have bigger hills and steeper slopes.",
        },
        DiagramHeight = 64,
        Diagram = r =>
        {
            // Tiny shaded heightmap preview: a single hill on the left, a basin
            // on the right, with the ball rolling between them.
            int w = (int)r.Width, h = (int)r.Height;
            for (int x = 0; x < w; x += 2)
                for (int y = 0; y < h; y += 2)
                {
                    float u = (x - w * 0.3f) / 14f;
                    float v = (y - h * 0.5f) / 10f;
                    float hill = MathF.Exp(-(u * u + v * v));
                    float u2 = (x - w * 0.7f) / 14f;
                    float v2 = (y - h * 0.5f) / 10f;
                    float basin = -MathF.Exp(-(u2 * u2 + v2 * v2));
                    float height = hill + basin;
                    // Cheap shading: derivative in x ≈ slope facing right.
                    float u3 = (x + 1 - w * 0.3f) / 14f;
                    float hill2 = MathF.Exp(-(u3 * u3 + v * v));
                    float u4 = (x + 1 - w * 0.7f) / 14f;
                    float basin2 = -MathF.Exp(-(u4 * u4 + v2 * v2));
                    float dh = (hill2 + basin2) - height;
                    float shade = Math.Clamp(0.5f - dh * 4f, 0.3f, 1.0f);
                    byte g = (byte)(64 + 80 * shade);
                    byte rr = (byte)(40 + 40 * shade);
                    byte bb = (byte)(40 + 30 * shade);
                    Raylib.DrawRectangle((int)r.X + x, (int)r.Y + y, 2, 2, new Color(rr, g, bb, (byte)255));
                }
            // Ball
            Raylib.DrawCircle((int)r.X + w / 2, (int)(r.Y + h * 0.45f), 3,
                new Color((byte)255, (byte)255, (byte)255, (byte)255));
        },
    };

    public void Load() => StartRound();

    public void Close()
    {
        UnloadTerrainTextures();
    }

    private void UnloadTerrainTextures()
    {
        for (int i = 0; i < _terrainBuilt.Length; i++)
        {
            if (_terrainBuilt[i])
            {
                Raylib.UnloadTexture(_terrainTex[i]);
                _terrainBuilt[i] = false;
            }
        }
    }

    private void StartRound()
    {
        UnloadTerrainTextures();

        _course.Clear();
        for (int i = 0; i < Holes; i++) _course.Add(GenerateHole(i));
        _terrainTex = new Texture2D[Holes];
        _terrainBuilt = new bool[Holes];
        _strokes = new int[Holes];
        _holeIdx = 0;
        _holeComplete = false;
        _roundComplete = false;
        _trail.Clear();
        ResetBall();
    }

    private void ResetBall()
    {
        _ball = _course[_holeIdx].Tee;
        _vel = Vector2.Zero;
        _aiming = false;
        _holeFlashTimer = 0;
        _trail.Clear();
    }

    private HoleLayout GenerateHole(int idx)
    {
        int par = idx switch { 0 => 3, 1 => 3, 2 => 4, 3 => 4, 4 => 4, 5 => 5, 6 => 4, 7 => 5, _ => 5 };

        var tee = new Vector2(40, 60 + _rng.Next(CanvasH - 120));
        var cup = new Vector2(CanvasW - 40, 60 + _rng.Next(CanvasH - 120));

        var trees = new List<Vector2>();
        int treeCount = 2 + _rng.Next(par);
        int safety = 0;
        while (trees.Count < treeCount && safety++ < 200)
        {
            var t = new Vector2(
                100 + _rng.Next(CanvasW - 200),
                30 + _rng.Next(CanvasH - 60));
            if (Vector2.Distance(t, tee) < 50) continue;
            if (Vector2.Distance(t, cup) < 50) continue;
            bool overlap = false;
            foreach (var u in trees) if (Vector2.Distance(t, u) < 28) { overlap = true; break; }
            if (overlap) continue;
            trees.Add(t);
        }

        var hazards = new List<(Vector2, float, float, int)>();
        int hzCount = par == 3 ? 1 : 2;
        for (int h = 0; h < hzCount; h++)
        {
            int kind = _rng.Next(2);
            var center = new Vector2(
                CanvasW / 4f + _rng.Next(CanvasW / 2),
                40 + _rng.Next(CanvasH - 80));
            if (Vector2.Distance(center, tee) < 60) center.X += 80;
            if (Vector2.Distance(center, cup) < 60) center.X -= 80;
            float rx = 24 + _rng.Next(20);
            float ry = 14 + _rng.Next(14);
            hazards.Add((center, rx, ry, kind));
        }

        // Heightmap. Difficulty scales: more bumps, taller, with steeper
        // slopes. Cup and tee regions get flattened so they're playable.
        int cols = CanvasW / HeightCellSize + 1;
        int rows = CanvasH / HeightCellSize + 1;
        var hf = new HeightField(cols, rows, HeightCellSize);

        int bumps = 4 + idx;
        float maxAmp = 1.5f + idx * 0.7f;        // hole 0: ~1.5, hole 8: ~7
        float minRadius = Math.Max(6f, 14f - idx * 0.5f);
        float maxRadius = Math.Max(minRadius + 4, 22f - idx * 0.6f);
        for (int b = 0; b < bumps; b++)
        {
            float cx = (float)_rng.NextDouble() * cols;
            float cy = (float)_rng.NextDouble() * rows;
            float amp = ((float)_rng.NextDouble() * 2 - 1) * maxAmp;
            float radius = minRadius + (float)_rng.NextDouble() * (maxRadius - minRadius);
            hf.AddBump(cx, cy, amp, radius);
        }

        // Flatten the green (the 36px circle around the cup gets smoothed).
        // Tee gets a small flat patch too.
        hf.Flatten(cup.X, cup.Y, 50f);
        hf.Flatten(tee.X, tee.Y, 28f);

        return new HoleLayout(tee, cup, par, trees, hazards, hf);
    }

    /// <summary>
    /// Build a topographic-map texture for the given hole: discrete elevation
    /// bands (green lowlands → tan peaks), thin black contour lines at every
    /// band boundary, and a normal·light shading multiplier on top to give
    /// the bands a sense of depth. Heavy upfront work but baked once per hole.
    /// </summary>
    private void BuildTerrainTexture(int idx)
    {
        if (_terrainBuilt[idx]) return;
        var hf = _course[idx].Heightmap;
        var img = Raylib.GenImageColor(CanvasW, CanvasH, new Color((byte)64, (byte)144, (byte)64, (byte)255));

        // First pass: find the height range so we can normalize into bands.
        float min = float.MaxValue, max = float.MinValue;
        for (int y = 0; y < CanvasH; y += 3)
            for (int x = 0; x < CanvasW; x += 3)
            {
                float h = hf.Sample(x, y);
                if (h < min) min = h;
                if (h > max) max = h;
            }
        float range = MathF.Max(0.6f, max - min);

        // Topo color ramp (low → high).
        var bands = new[]
        {
            new Color((byte) 48, (byte) 92, (byte) 48, (byte)255),   // shadow lowland
            new Color((byte) 60, (byte)124, (byte) 60, (byte)255),   // fairway
            new Color((byte) 88, (byte)160, (byte) 72, (byte)255),   // grassy mid
            new Color((byte)136, (byte)180, (byte) 80, (byte)255),   // light hill
            new Color((byte)188, (byte)184, (byte)104, (byte)255),   // upper slope
            new Color((byte)216, (byte)196, (byte)144, (byte)255),   // ridge
            new Color((byte)232, (byte)216, (byte)176, (byte)255),   // peak
        };
        int nBands = bands.Length;

        // Strong top-left light, slightly elevated. Gradient gets a 6× boost
        // for shading purposes only — physics still uses the raw gradient.
        var lightDir = Vector3.Normalize(new Vector3(-1, -1, 1.0f));
        const float gradAmp = 6f;
        const float shadeMin = 0.35f;
        const float shadeMax = 1.15f;

        // Per-pixel band index lookup so the contour pass can detect band
        // boundaries in O(1) per pixel.
        var bandAt = new byte[CanvasW * CanvasH];

        for (int y = 0; y < CanvasH; y++)
            for (int x = 0; x < CanvasW; x++)
            {
                float h = hf.Sample(x, y);
                float t = (h - min) / range;
                int b = Math.Clamp((int)(t * nBands), 0, nBands - 1);
                bandAt[y * CanvasW + x] = (byte)b;

                var grad = hf.Gradient(x, y);
                var n = Vector3.Normalize(new Vector3(-grad.X * gradAmp, -grad.Y * gradAmp, 1));
                float shade = Math.Clamp(Vector3.Dot(n, lightDir), shadeMin, shadeMax);

                var c = bands[b];
                byte r = (byte)Math.Clamp(c.R * shade, 0, 255);
                byte g = (byte)Math.Clamp(c.G * shade, 0, 255);
                byte bb = (byte)Math.Clamp(c.B * shade, 0, 255);
                Raylib.ImageDrawPixel(ref img, x, y, new Color(r, g, bb, (byte)255));
            }

        // Contour-line pass: a pixel becomes near-black if either its right or
        // down neighbor sits in a different band. Reads as a topo map.
        var contourCol = new Color((byte)24, (byte)32, (byte)20, (byte)255);
        for (int y = 0; y < CanvasH - 1; y++)
            for (int x = 0; x < CanvasW - 1; x++)
            {
                int b0 = bandAt[y * CanvasW + x];
                int b1 = bandAt[y * CanvasW + x + 1];
                int b2 = bandAt[(y + 1) * CanvasW + x];
                if (b0 != b1 || b0 != b2)
                    Raylib.ImageDrawPixel(ref img, x, y, contourCol);
            }

        _terrainTex[idx] = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);
        _terrainBuilt[idx] = true;
    }

    private int Terrain(Vector2 p)
    {
        if (p.X < 0 || p.Y < 0 || p.X >= CanvasW || p.Y >= CanvasH) return 1;
        var hole = _course[_holeIdx];
        foreach (var (c, rx, ry, kind) in hole.Hazards)
        {
            float dx = p.X - c.X, dy = p.Y - c.Y;
            if ((dx * dx) / (rx * rx) + (dy * dy) / (ry * ry) < 1f)
                return kind == 0 ? 2 : 3;
        }
        if (Vector2.Distance(p, hole.Cup) < 40) return 4;
        if (p.X < 18 || p.Y < 18 || p.X > CanvasW - 18 || p.Y > CanvasH - 18) return 1;
        return 0;
    }

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
        switch (RetroWidgets.MenuBarHitTest(menuBar,
            new[] { "New Round", "Replay Hole", "Skip", "Help" }, local, leftPressed))
        {
            case 0: StartRound(); return;
            case 1: ResetBall(); return;
            case 2: AdvanceHole(); return;
            case 3: _help.Visible = !_help.Visible; return;
        }
        if (_help.HandleInput(local, leftPressed, PanelSize)) return;

        if (_roundComplete) return;

        var canvasOrigin = new Vector2(FrameInset, FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight);
        var canvasMouse = local - canvasOrigin;

        if (_holeComplete)
        {
            _holeFlashTimer += delta;
            if (_holeFlashTimer > 1.4f) AdvanceHole();
            return;
        }

        var hf = _course[_holeIdx].Heightmap;

        // Physics tick — always run unless aiming, so the ball can roll
        // downhill from rest on a slope.
        if (!_aiming)
        {
            // Slope acceleration from heightmap gradient.
            var grad = hf.Gradient(_ball.X, _ball.Y);
            var slopeAccel = -grad * GravStrength;

            // Apply slope force.
            _vel += slopeAccel * delta;

            int t = Terrain(_ball);
            // Per-terrain viscous friction (scales with speed) plus a constant
            // kinetic floor so the ball settles instead of trickling forever.
            float visc = t switch { 2 => 5.5f, 1 => 2.6f, 4 => 0.8f, _ => 1.4f };
            _vel *= MathF.Max(0, 1f - visc * delta);
            float speed = _vel.Length();
            if (speed > 0.01f)
            {
                float decel = KineticFriction * delta;
                if (decel >= speed) _vel = Vector2.Zero;
                else _vel -= Vector2.Normalize(_vel) * decel;
            }

            // Static-friction cutoff: if both speed and slope force are tiny,
            // park the ball.
            if (_vel.LengthSquared() < 0.5f && slopeAccel.LengthSquared() < 200f)
                _vel = Vector2.Zero;

            if (_vel.LengthSquared() > 0.01f)
            {
                _ball += _vel * delta;
                _trailDropTimer += delta;
                if (_trailDropTimer > 0.04f)
                {
                    _trailDropTimer = 0;
                    _trail.Add(_ball);
                    if (_trail.Count > 30) _trail.RemoveAt(0);
                }
            }

            if (_ball.X < 6 || _ball.X > CanvasW - 6) { _vel.X = -_vel.X * 0.55f; _ball.X = Math.Clamp(_ball.X, 6, CanvasW - 6); }
            if (_ball.Y < 6 || _ball.Y > CanvasH - 6) { _vel.Y = -_vel.Y * 0.55f; _ball.Y = Math.Clamp(_ball.Y, 6, CanvasH - 6); }

            foreach (var tree in _course[_holeIdx].Trees)
            {
                var diff = _ball - tree;
                float treeR = 12;
                float ballR = 5;
                if (diff.LengthSquared() < (treeR + ballR) * (treeR + ballR))
                {
                    var n = Vector2.Normalize(diff);
                    _vel = Vector2.Reflect(_vel, n) * 0.7f;
                    _ball = tree + n * (treeR + ballR + 0.5f);
                }
            }

            if (Terrain(_ball) == 3)
            {
                _ball = _course[_holeIdx].Tee;
                _vel = Vector2.Zero;
                _strokes[_holeIdx]++;
                _trail.Clear();
            }

            if (Vector2.Distance(_ball, _course[_holeIdx].Cup) < 8 && _vel.Length() < 90f)
            {
                _ball = _course[_holeIdx].Cup;
                _vel = Vector2.Zero;
                _holeComplete = true;
                _holeFlashTimer = 0;
            }
        }

        // Allow aiming any time the ball is essentially at rest.
        bool atRest = _vel.LengthSquared() < 1f;

        if (atRest && leftPressed && (canvasMouse - _ball).LengthSquared() < 14 * 14)
            _aiming = true;
        if (_aiming) _aimEnd = canvasMouse;
        if (leftReleased && _aiming)
        {
            _aiming = false;
            var dir = _ball - _aimEnd;
            float power = Math.Min(dir.Length() * 4f, 380f);
            if (power < 12) return;
            _vel = Vector2.Normalize(dir) * power;
            _strokes[_holeIdx]++;
            _trail.Clear();
        }
    }

    private void AdvanceHole()
    {
        _holeFlashTimer = 0;
        _holeComplete = false;
        _trail.Clear();
        if (_holeIdx >= Holes - 1)
        {
            _roundComplete = true;
            return;
        }
        _holeIdx++;
        ResetBall();
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, $"Fuji Golf — Hole {_holeIdx + 1} of {Holes}", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New Round", "Replay Hole", "Skip", "Help" }, -1);

        var canvasOrigin = new Vector2(
            panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight);
        var canvas = new Rectangle(canvasOrigin.X, canvasOrigin.Y, CanvasW, CanvasH);

        DrawCourse(canvasOrigin, canvas);
        DrawScorecard(panelOffset);

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);

        int total = 0; for (int i = 0; i < Holes; i++) total += _strokes[i];
        int parTotal = 0; for (int i = 0; i < Holes; i++) parTotal += _course[i].Par;

        string state;
        if (_roundComplete)
        {
            int rel = total - parTotal;
            state = rel == 0 ? $"Round done — even par ({total})"
                  : rel < 0 ? $"Round done — {rel} under par ({total})"
                            : $"Round done — +{rel} over par ({total})";
        }
        else if (_holeComplete) state = $"Holed in {_strokes[_holeIdx]}!";
        else if (_aiming) state = "Drag to aim, release to swing";
        else state = "Drag from ball to aim";

        RetroWidgets.StatusBar(status, state, $"H{_holeIdx + 1}  Par {_course[_holeIdx].Par}  Strokes {_strokes[_holeIdx]}");

        _help.Draw(panelOffset, PanelSize);
    }

    private void DrawCourse(Vector2 canvasOrigin, Rectangle canvas)
    {
        // Lazy-build the shaded terrain texture for this hole on first draw.
        BuildTerrainTexture(_holeIdx);
        Raylib.DrawTexture(_terrainTex[_holeIdx],
            (int)canvasOrigin.X, (int)canvasOrigin.Y, Color.White);

        // Rough strip border on top of terrain
        Raylib.DrawRectangleLinesEx(canvas, 12, new Color((byte)40, (byte)96, (byte)32, (byte)180));

        var hole = _course[_holeIdx];

        foreach (var (c, rx, ry, kind) in hole.Hazards)
        {
            int cx = (int)(canvasOrigin.X + c.X);
            int cy = (int)(canvasOrigin.Y + c.Y);
            var col = kind == 0 ? new Color((byte)232, (byte)208, (byte)144, (byte)255)
                                : new Color((byte)48, (byte)96, (byte)192, (byte)255);
            Raylib.DrawEllipse(cx, cy, (int)rx, (int)ry, col);
            if (kind == 1)
                Raylib.DrawEllipseLines(cx, cy, (int)rx, (int)ry, new Color((byte)80, (byte)144, (byte)232, (byte)255));
        }

        var cup = hole.Cup;
        // Green is a slightly lighter, flatter circle (the heightmap is
        // already smoothed here so we don't need to fight the shading).
        Raylib.DrawCircle((int)(canvasOrigin.X + cup.X), (int)(canvasOrigin.Y + cup.Y),
            36, new Color((byte)112, (byte)200, (byte)96, (byte)200));

        // Trail of the ball's roll
        for (int i = 0; i < _trail.Count; i++)
        {
            byte a = (byte)(180 * (i + 1) / _trail.Count);
            Raylib.DrawCircle(
                (int)(canvasOrigin.X + _trail[i].X),
                (int)(canvasOrigin.Y + _trail[i].Y),
                2, new Color((byte)255, (byte)255, (byte)255, a));
        }

        foreach (var t in hole.Trees)
        {
            int cx = (int)(canvasOrigin.X + t.X);
            int cy = (int)(canvasOrigin.Y + t.Y);
            Raylib.DrawCircle(cx + 1, cy + 2, 12, new Color((byte)0, (byte)0, (byte)0, (byte)60));
            Raylib.DrawCircle(cx, cy, 12, new Color((byte)40, (byte)120, (byte)60, (byte)255));
            Raylib.DrawCircle(cx - 3, cy - 3, 4, new Color((byte)80, (byte)168, (byte)96, (byte)255));
            Raylib.DrawRectangle(cx - 2, cy + 8, 4, 6, new Color((byte)80, (byte)56, (byte)32, (byte)255));
        }

        Raylib.DrawCircle((int)(canvasOrigin.X + cup.X), (int)(canvasOrigin.Y + cup.Y), 6,
            new Color((byte)0, (byte)0, (byte)0, (byte)255));
        int fx = (int)(canvasOrigin.X + cup.X);
        int fy = (int)(canvasOrigin.Y + cup.Y);
        Raylib.DrawRectangle(fx - 1, fy - 26, 2, 26, RetroSkin.BodyText);
        Raylib.DrawTriangle(
            new Vector2(fx + 1, fy - 26),
            new Vector2(fx + 14, fy - 22),
            new Vector2(fx + 1, fy - 18),
            new Color((byte)220, (byte)60, (byte)60, (byte)255));

        var tee = hole.Tee;
        Raylib.DrawCircleLines((int)(canvasOrigin.X + tee.X), (int)(canvasOrigin.Y + tee.Y),
            7, new Color((byte)255, (byte)255, (byte)255, (byte)220));

        if (_aiming)
        {
            var ballAbs = canvasOrigin + _ball;
            var dir = _ball - _aimEnd;
            float pwr = MathF.Min(dir.Length() * 4f, 380f);
            float pwrFrac = pwr / 380f;
            if (dir.LengthSquared() > 0.1f)
            {
                var endAbs = canvasOrigin + _ball + Vector2.Normalize(dir) * MathF.Min(dir.Length(), 110f);
                Raylib.DrawLineEx(ballAbs, endAbs, 2f, new Color((byte)255, (byte)255, (byte)255, (byte)220));
                var n = Vector2.Normalize(dir);
                var perp = new Vector2(-n.Y, n.X);
                Raylib.DrawTriangle(
                    endAbs,
                    endAbs - n * 8 + perp * 4,
                    endAbs - n * 8 - perp * 4,
                    new Color((byte)255, (byte)255, (byte)255, (byte)220));
            }
            int barX = (int)canvas.X + 12;
            int barY = (int)canvas.Y + 12;
            int barW = 120;
            Raylib.DrawRectangle(barX - 1, barY - 1, barW + 2, 12, RetroSkin.BodyText);
            Raylib.DrawRectangle(barX, barY, barW, 10, new Color((byte)40, (byte)40, (byte)40, (byte)200));
            var fillCol = pwrFrac < 0.5f ? new Color((byte)60, (byte)200, (byte)60, (byte)255)
                        : pwrFrac < 0.85f ? new Color((byte)232, (byte)200, (byte)64, (byte)255)
                                          : new Color((byte)220, (byte)60, (byte)60, (byte)255);
            Raylib.DrawRectangle(barX, barY, (int)(barW * pwrFrac), 10, fillCol);
            RetroSkin.DrawText($"{(int)pwr}", barX + barW + 6, barY - 2,
                new Color((byte)255, (byte)255, (byte)255, (byte)220), 14);
        }

        Raylib.DrawCircle((int)(canvasOrigin.X + _ball.X) + 1, (int)(canvasOrigin.Y + _ball.Y) + 1,
            5, new Color((byte)0, (byte)0, (byte)0, (byte)100));
        Raylib.DrawCircle((int)(canvasOrigin.X + _ball.X), (int)(canvasOrigin.Y + _ball.Y),
            5, new Color((byte)255, (byte)255, (byte)255, (byte)255));
        Raylib.DrawCircleLines((int)(canvasOrigin.X + _ball.X), (int)(canvasOrigin.Y + _ball.Y),
            5, RetroSkin.BodyText);

        if (_holeComplete)
        {
            string msg = $"Holed in {_strokes[_holeIdx]} (par {_course[_holeIdx].Par})";
            int w = RetroSkin.MeasureText(msg, 22);
            int x = (int)(canvas.X + (canvas.Width - w) / 2);
            int y = (int)(canvas.Y + canvas.Height / 2 - 12);
            Raylib.DrawRectangle(x - 12, y - 6, w + 24, 36, new Color((byte)0, (byte)0, (byte)0, (byte)180));
            RetroSkin.DrawText(msg, x, y, new Color((byte)255, (byte)240, (byte)140, (byte)255), 22);
        }

        if (_roundComplete)
        {
            int total = 0, parTotal = 0;
            for (int i = 0; i < Holes; i++) { total += _strokes[i]; parTotal += _course[i].Par; }
            int rel = total - parTotal;
            string headline = "Round complete";
            string score = rel == 0 ? $"{total} — even par"
                         : rel < 0 ? $"{total} — {rel} under par!"
                                   : $"{total} — +{rel} over par";
            int wHead = RetroSkin.MeasureText(headline, 24);
            int wScore = RetroSkin.MeasureText(score, 18);
            int x = (int)canvas.X + (CanvasW - Math.Max(wHead, wScore)) / 2;
            int y = (int)canvas.Y + CanvasH / 2 - 30;
            Raylib.DrawRectangle(x - 16, y - 12, Math.Max(wHead, wScore) + 32, 70,
                new Color((byte)0, (byte)0, (byte)0, (byte)200));
            RetroSkin.DrawText(headline,
                (int)canvas.X + (CanvasW - wHead) / 2, y, new Color((byte)255, (byte)240, (byte)140, (byte)255), 24);
            RetroSkin.DrawText(score,
                (int)canvas.X + (CanvasW - wScore) / 2, y + 28, new Color((byte)255, (byte)255, (byte)255, (byte)255), 18);
        }
    }

    private void DrawScorecard(Vector2 panelOffset)
    {
        float sx = panelOffset.X + FrameInset + CanvasW + Margin;
        float sy = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight;
        var rect = new Rectangle(sx, sy, ScoreW - Margin, CanvasH);
        RetroSkin.DrawSunken(rect, RetroSkin.Face);

        RetroSkin.DrawText("Scorecard", (int)sx + 8, (int)sy + 6, RetroSkin.BodyText, 16);
        int hx = (int)sx + 8;
        int hy = (int)sy + 28;
        RetroSkin.DrawText("Hole", hx, hy, RetroSkin.BodyText, 13);
        RetroSkin.DrawText("Par", hx + 50, hy, RetroSkin.BodyText, 13);
        RetroSkin.DrawText("You", hx + 90, hy, RetroSkin.BodyText, 13);
        Raylib.DrawLine(hx, hy + 16, hx + (int)rect.Width - 16, hy + 16, RetroSkin.Shadow);

        int rowY = hy + 22;
        int totalPar = 0, totalStrokes = 0;
        for (int i = 0; i < Holes; i++)
        {
            bool current = i == _holeIdx && !_roundComplete;
            var col = current ? new Color((byte)40, (byte)80, (byte)168, (byte)255) : RetroSkin.BodyText;
            RetroSkin.DrawText((i + 1).ToString(), hx, rowY, col, 13);
            RetroSkin.DrawText(_course[i].Par.ToString(), hx + 50, rowY, col, 13);
            string strokes = (i <= _holeIdx || _roundComplete) ? _strokes[i].ToString() : "—";
            RetroSkin.DrawText(strokes, hx + 90, rowY, col, 13);
            totalPar += _course[i].Par;
            totalStrokes += _strokes[i];
            rowY += 16;
        }
        Raylib.DrawLine(hx, rowY, hx + (int)rect.Width - 16, rowY, RetroSkin.Shadow);
        rowY += 4;
        RetroSkin.DrawText("Total", hx, rowY, RetroSkin.BodyText, 13);
        RetroSkin.DrawText(totalPar.ToString(), hx + 50, rowY, RetroSkin.BodyText, 13);
        RetroSkin.DrawText(totalStrokes.ToString(), hx + 90, rowY, RetroSkin.BodyText, 13);
    }
}
