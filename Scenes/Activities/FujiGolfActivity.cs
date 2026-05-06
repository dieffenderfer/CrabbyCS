using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Fuji Golf — top-down… ish. The course is a 9-hole heightmap rendered as a
/// 2.5D oblique view: pixel columns are voxel-sampled far→near, projected
/// onto the screen via a configurable pitch (tilt) angle, painted in
/// Bayer-dithered elevation bands with normal·light shading on top. The ball
/// rolls on the actual heightmap, and every game object (tee, cup, trees,
/// hazards, trail, aim line) gets projected through the same camera so the
/// world stays consistent under the angled view.
/// </summary>
public class FujiGolfActivity : IActivity
{
    private const int FrameInset = 3;
    private const int CanvasW = 540;
    private const int CanvasH = 360;
    private const int ScoreW = 160;
    private const int Margin = 12;
    private const int Holes = 9;

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

        public Vector2 Gradient(float x, float y)
        {
            const float eps = 1.5f;
            float dx = (Sample(x + eps, y) - Sample(x - eps, y)) / (2 * eps);
            float dy = (Sample(x, y + eps) - Sample(x, y - eps)) / (2 * eps);
            return new Vector2(dx, dy);
        }

        public void AddBump(float cx, float cy, float amp, float radius)
        {
            float r2inv = 1f / (2 * radius * radius);
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

        public void Flatten(float cxPx, float cyPx, float radiusPx)
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
                    float w = MathF.Exp(-(ddx * ddx + ddy * ddy) * r2inv);
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
    private Texture2D[] _meshTex = Array.Empty<Texture2D>();
    private bool[] _meshBuilt = Array.Empty<bool>();
    private int[] _strokes = new int[Holes];
    private int _holeIdx;
    private Vector2 _ball;       // .X = worldX, .Y = worldZ (depth)
    private Vector2 _vel;
    private bool _aiming;
    private Vector2 _aimEnd;     // canvas screen coords
    private bool _holeComplete;
    private bool _roundComplete;
    private float _holeFlashTimer;
    private List<Vector2> _trail = new();
    private float _trailDropTimer;
    private readonly Random _rng = new();

    // Camera tilt (pitch). 0 = top-down. 35° default.
    private float _tiltDeg = 35f;
    private float _cosT = MathF.Cos(35f * MathF.PI / 180f);
    private float _sinT = MathF.Sin(35f * MathF.PI / 180f);
    private bool _showGrid;     // wireframe overlay so you can read the 3D mesh

    private readonly RetroHelp _help = new()
    {
        Title = "Fuji Golf — How to play",
        Lines = new[]
        {
            "Sink the ball in nine holes with as few strokes as possible.",
            "The course is rendered in 2.5D — banded elevation, dithered, with a",
            "configurable tilt (View menu) so you can read the slopes.",
            "Drag from the ball to aim (any tilt), longer drag = more power.",
            "The ball rolls on the actual terrain — putts hook around hills,",
            "stray drives roll into bunkers. Trees bounce, water resets to tee.",
            "Later holes have bigger hills.",
        },
        DiagramHeight = 64,
        Diagram = r =>
        {
            // Voxel preview: a small heightmap with two hills, rendered with
            // the same banded shading + dithering used in-game.
            int w = (int)r.Width, h = (int)r.Height;
            int[,] bayer = { { 0, 8, 2, 10 }, { 12, 4, 14, 6 }, { 3, 11, 1, 9 }, { 15, 7, 13, 5 } };
            float ct = MathF.Cos(35 * MathF.PI / 180);
            float st = MathF.Sin(35 * MathF.PI / 180);
            for (int x = 0; x < w; x++)
            {
                int prev = -1;
                for (int z = h - 1; z >= 0; z--)
                {
                    float u = (x - w * 0.30f) / 9f;
                    float v = (z - h * 0.55f) / 7f;
                    float u2 = (x - w * 0.70f) / 11f;
                    float v2 = (z - h * 0.55f) / 7f;
                    float height = 16f * MathF.Exp(-(u * u + v * v))
                                 + 12f * MathF.Exp(-(u2 * u2 + v2 * v2));
                    int sy = (int)((h - 1) - z * ct - height * st);
                    if (sy <= prev) continue;
                    float t = Math.Clamp(height / 18f, 0, 1);
                    var c = t < 0.2f ? new Color((byte)60, (byte)124, (byte)60, (byte)255)
                          : t < 0.4f ? new Color((byte)88, (byte)160, (byte)72, (byte)255)
                          : t < 0.65f ? new Color((byte)136, (byte)180, (byte)80, (byte)255)
                          : t < 0.85f ? new Color((byte)200, (byte)188, (byte)104, (byte)255)
                                      : new Color((byte)232, (byte)216, (byte)176, (byte)255);
                    for (int y = prev + 1; y <= sy; y++)
                    {
                        if (y < 0 || y >= h) continue;
                        int b = bayer[Math.Abs(x) % 4, Math.Abs(y) % 4];
                        var col = b > 8 ? new Color((byte)(c.R * 0.85f), (byte)(c.G * 0.85f), (byte)(c.B * 0.85f), (byte)255) : c;
                        Raylib.DrawPixel((int)r.X + x, (int)r.Y + y, col);
                    }
                    prev = sy;
                }
                // Sky above
                for (int y = 0; y <= prev; y++)
                {
                    byte sb = (byte)(180 + y * 50 / Math.Max(1, prev));
                    Raylib.DrawPixel((int)r.X + x, (int)r.Y + y,
                        new Color((byte)160, (byte)188, sb, (byte)255));
                }
            }
        },
    };

    public void Load() => StartRound();

    public void Close() => UnloadTerrainTextures();

    private void UnloadTerrainTextures()
    {
        for (int i = 0; i < _terrainBuilt.Length; i++)
            if (_terrainBuilt[i])
            {
                Raylib.UnloadTexture(_terrainTex[i]);
                _terrainBuilt[i] = false;
            }
        for (int i = 0; i < _meshBuilt.Length; i++)
            if (_meshBuilt[i])
            {
                Raylib.UnloadTexture(_meshTex[i]);
                _meshBuilt[i] = false;
            }
    }

    private void SetTilt(float deg)
    {
        deg = Math.Clamp(deg, 0, 60);
        if (MathF.Abs(deg - _tiltDeg) < 0.01f) return;
        _tiltDeg = deg;
        _cosT = MathF.Cos(_tiltDeg * MathF.PI / 180f);
        _sinT = MathF.Sin(_tiltDeg * MathF.PI / 180f);
        UnloadTerrainTextures();   // force re-render at new angle
    }

    private void StartRound()
    {
        UnloadTerrainTextures();
        _course.Clear();
        for (int i = 0; i < Holes; i++) _course.Add(GenerateHole(i));
        _terrainTex = new Texture2D[Holes];
        _terrainBuilt = new bool[Holes];
        _meshTex = new Texture2D[Holes];
        _meshBuilt = new bool[Holes];
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

        int cols = CanvasW / HeightCellSize + 1;
        int rows = CanvasH / HeightCellSize + 1;
        var hf = new HeightField(cols, rows, HeightCellSize);

        // Heights are in heightmap units; under tilt, screenY drops by
        // h * sinT, so an amp of ~16 with sinT=0.57 lifts a peak ~9 pixels.
        // That needs to be visible — earlier values were too tame.
        int bumps = 6 + idx;
        float maxAmp = 6f + idx * 1.6f;
        float minRadius = Math.Max(8f, 16f - idx * 0.4f);
        float maxRadius = Math.Max(minRadius + 6, 30f - idx * 0.6f);
        for (int b = 0; b < bumps; b++)
        {
            float cx = (float)_rng.NextDouble() * cols;
            float cy = (float)_rng.NextDouble() * rows;
            float amp = ((float)_rng.NextDouble() * 2 - 1) * maxAmp;
            float radius = minRadius + (float)_rng.NextDouble() * (maxRadius - minRadius);
            hf.AddBump(cx, cy, amp, radius);
        }

        hf.Flatten(cup.X, cup.Y, 50f);
        hf.Flatten(tee.X, tee.Y, 28f);

        return new HoleLayout(tee, cup, par, trees, hazards, hf);
    }

    // ── Camera / projection ──────────────────────────────────────────────
    private Vector2 ProjectToScreen(float wx, float wz, float h) =>
        new(wx, (CanvasH - 1) - wz * _cosT - h * _sinT);

    private Vector2 ProjectToScreen(Vector2 worldXZ, HeightField hf) =>
        ProjectToScreen(worldXZ.X, worldXZ.Y, hf.Sample(worldXZ.X, worldXZ.Y));

    /// <summary>Approximate inverse projection (assumes flat ground).</summary>
    private Vector2 ScreenToWorld(Vector2 canvasMouse)
    {
        float wz = ((CanvasH - 1) - canvasMouse.Y) / _cosT;
        return new Vector2(canvasMouse.X, wz);
    }

    // ── Terrain texture build (voxel oblique render) ─────────────────────
    private static readonly Color[] TerrainBands =
    {
        new((byte) 56, (byte)100, (byte) 56, (byte)255),
        new((byte) 64, (byte)128, (byte) 64, (byte)255),
        new((byte) 88, (byte)160, (byte) 72, (byte)255),
        new((byte)136, (byte)180, (byte) 80, (byte)255),
        new((byte)188, (byte)184, (byte)104, (byte)255),
        new((byte)216, (byte)196, (byte)144, (byte)255),
        new((byte)232, (byte)216, (byte)176, (byte)255),
    };

    private static readonly int[,] Bayer4 =
    {
        {  0,  8,  2, 10 },
        { 12,  4, 14,  6 },
        {  3, 11,  1,  9 },
        { 15,  7, 13,  5 },
    };

    private void BuildTerrainTexture(int idx)
    {
        if (_terrainBuilt[idx]) return;
        var hf = _course[idx].Heightmap;
        var img = Raylib.GenImageColor(CanvasW, CanvasH, new Color((byte)0, (byte)0, (byte)0, (byte)0));

        // Find height range so bands span the whole vertical extent.
        float min = float.MaxValue, max = float.MinValue;
        for (int y = 0; y < CanvasH; y += 3)
            for (int x = 0; x < CanvasW; x += 3)
            {
                float h = hf.Sample(x, y);
                if (h < min) min = h;
                if (h > max) max = h;
            }
        float range = MathF.Max(0.6f, max - min);

        var lightDir = Vector3.Normalize(new Vector3(-1, -1, 1.0f));
        const float gradAmp = 6f;
        const float shadeMin = 0.45f;
        const float shadeMax = 1.18f;
        float baseY = CanvasH - 1;

        // Sky gradient: pale blue at top to warmer near-horizon. Drawn first
        // so terrain paints over it.
        for (int y = 0; y < CanvasH; y++)
        {
            float t = y / (float)CanvasH;
            byte sr = (byte)(140 + 60 * t);
            byte sg = (byte)(170 + 50 * t);
            byte sb = (byte)(210 - 30 * t);
            for (int x = 0; x < CanvasW; x++)
                Raylib.ImageDrawPixel(ref img, x, y, new Color(sr, sg, sb, (byte)255));
        }

        // Canonical voxel-space algorithm: for each world X column, march
        // world Z FRONT to BACK (Z=0 is closest). Track floorY = the highest
        // (smallest-Y) screen pixel painted so far in this column. A new
        // slab whose projected screenY is ABOVE floorY (smaller Y) sticks up
        // into the sky and gets painted from screenY up to floorY-1; anything
        // that projects at or below floorY is occluded by closer terrain and
        // skipped. Sky pre-pass survives wherever the loop never reaches.
        for (int worldX = 0; worldX < CanvasW; worldX++)
        {
            int floorY = CanvasH;     // start with sky filling the whole column
            for (int worldZ = 0; worldZ < CanvasH; worldZ++)
            {
                float h = hf.Sample(worldX, worldZ);
                int screenY = (int)MathF.Round(baseY - worldZ * _cosT - h * _sinT);
                if (screenY >= floorY) continue;

                float bandT = (h - min) / range;
                float bandFloat = bandT * (TerrainBands.Length - 1);
                int b = Math.Clamp((int)bandFloat, 0, TerrainBands.Length - 2);
                float bandFrac = Math.Clamp(bandFloat - b, 0, 1);
                var c0 = TerrainBands[b];
                var c1 = TerrainBands[b + 1];

                var grad = hf.Gradient(worldX, worldZ);
                var n = Vector3.Normalize(new Vector3(-grad.X * gradAmp, -grad.Y * gradAmp, 1));
                float shade = Math.Clamp(Vector3.Dot(n, lightDir), shadeMin, shadeMax);

                // Atmospheric haze: blend distant terrain toward sky color.
                float depth = worldZ / (float)CanvasH;
                float haze = 0.35f * depth;

                for (int y = screenY; y < floorY; y++)
                {
                    if (y < 0 || y >= CanvasH) continue;
                    int bayer = Bayer4[((worldX % 4) + 4) % 4, ((y % 4) + 4) % 4];
                    float threshold = (bayer + 0.5f) / 16f;
                    var bandCol = bandFrac > threshold ? c1 : c0;
                    float r = bandCol.R * shade;
                    float g = bandCol.G * shade;
                    float bb = bandCol.B * shade;
                    r = r + (210 - r) * haze;
                    g = g + (220 - g) * haze;
                    bb = bb + (230 - bb) * haze;
                    Raylib.ImageDrawPixel(ref img, worldX, y,
                        new Color((byte)Math.Clamp(r, 0, 255),
                                  (byte)Math.Clamp(g, 0, 255),
                                  (byte)Math.Clamp(bb, 0, 255), (byte)255));
                }
                floorY = screenY;
            }
        }

        _terrainTex[idx] = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);
        _terrainBuilt[idx] = true;
    }

    // ── Terrain queries (gameplay) ───────────────────────────────────────
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
            new[] { "New Round", "Replay Hole", "Skip", $"View: {(int)_tiltDeg}°", _showGrid ? "Mesh: on" : "Mesh: off", "Help" }, local, leftPressed))
        {
            case 0: StartRound(); return;
            case 1: ResetBall(); return;
            case 2: AdvanceHole(); return;
            case 3:
                // Cycle through tilt presets.
                float[] presets = { 0, 20, 35, 50 };
                int curIdx = 0;
                for (int i = 0; i < presets.Length; i++)
                    if (MathF.Abs(presets[i] - _tiltDeg) < 0.5f) { curIdx = i; break; }
                SetTilt(presets[(curIdx + 1) % presets.Length]);
                return;
            case 4: _showGrid = !_showGrid; return;
            case 5: _help.Visible = !_help.Visible; return;
        }
        if (_help.HandleInput(local, leftPressed, PanelSize)) return;

        if (_roundComplete) return;

        var canvasOrigin = new Vector2(FrameInset,
            FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight);
        var canvasMouse = local - canvasOrigin;

        if (_holeComplete)
        {
            _holeFlashTimer += delta;
            if (_holeFlashTimer > 1.4f) AdvanceHole();
            return;
        }

        var hf = _course[_holeIdx].Heightmap;

        if (!_aiming)
        {
            var grad = hf.Gradient(_ball.X, _ball.Y);
            var slopeAccel = -grad * GravStrength;
            _vel += slopeAccel * delta;

            int t = Terrain(_ball);
            float visc = t switch { 2 => 5.5f, 1 => 2.6f, 4 => 0.8f, _ => 1.4f };
            _vel *= MathF.Max(0, 1f - visc * delta);
            float speed = _vel.Length();
            if (speed > 0.01f)
            {
                float decel = KineticFriction * delta;
                if (decel >= speed) _vel = Vector2.Zero;
                else _vel -= Vector2.Normalize(_vel) * decel;
            }
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
                    var nrm = Vector2.Normalize(diff);
                    _vel = Vector2.Reflect(_vel, nrm) * 0.7f;
                    _ball = tree + nrm * (treeR + ballR + 0.5f);
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

        // Aiming uses the projected ball position so the user clicks where
        // they see it, not where it lives in world coords.
        bool atRest = _vel.LengthSquared() < 1f;
        var ballScreen = ProjectToScreen(_ball, hf);

        if (atRest && leftPressed && (canvasMouse - ballScreen).LengthSquared() < 14 * 14)
            _aiming = true;
        if (_aiming) _aimEnd = canvasMouse;
        if (leftReleased && _aiming)
        {
            _aiming = false;
            // Convert the screen-space drag into a world-space direction by
            // un-stretching the Y axis (worldZ→screenY scales by cosT).
            var screenDir = ballScreen - _aimEnd;
            var worldDir = new Vector2(screenDir.X, screenDir.Y / Math.Max(0.05f, _cosT));
            float power = Math.Min(worldDir.Length() * 4f, 380f);
            if (power < 12) return;
            _vel = Vector2.Normalize(worldDir) * power;
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
        RetroWidgets.MenuBarVisual(menuBar,
            new[] { "New Round", "Replay Hole", "Skip", $"View: {(int)_tiltDeg}°", _showGrid ? "Mesh: on" : "Mesh: off", "Help" }, -1);

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
        if (_showGrid)
        {
            BuildMeshTexture(_holeIdx);
            Raylib.DrawTexture(_meshTex[_holeIdx],
                (int)canvasOrigin.X, (int)canvasOrigin.Y, Color.White);
        }
        else
        {
            BuildTerrainTexture(_holeIdx);
            Raylib.DrawTexture(_terrainTex[_holeIdx],
                (int)canvasOrigin.X, (int)canvasOrigin.Y, Color.White);
        }

        var hole = _course[_holeIdx];
        var hf = hole.Heightmap;

        // Hazards: project center, draw an ellipse with Y stretched by cosT.
        foreach (var (c, rx, ry, kind) in hole.Hazards)
        {
            var sp = ProjectToScreen(c, hf);
            int cx = (int)(canvasOrigin.X + sp.X);
            int cy = (int)(canvasOrigin.Y + sp.Y);
            int yRad = Math.Max(2, (int)(ry * _cosT));
            var col = kind == 0 ? new Color((byte)232, (byte)208, (byte)144, (byte)255)
                                : new Color((byte)48, (byte)96, (byte)192, (byte)255);
            Raylib.DrawEllipse(cx, cy, (int)rx, yRad, col);
            if (kind == 1)
                Raylib.DrawEllipseLines(cx, cy, (int)rx, yRad,
                    new Color((byte)80, (byte)144, (byte)232, (byte)255));
        }

        // Green: lighter circle around cup, projected.
        var cupScreen = ProjectToScreen(hole.Cup, hf);
        Raylib.DrawEllipse(
            (int)(canvasOrigin.X + cupScreen.X),
            (int)(canvasOrigin.Y + cupScreen.Y),
            36, Math.Max(8, (int)(36 * _cosT)),
            new Color((byte)112, (byte)200, (byte)96, (byte)200));

        // Trail
        for (int i = 0; i < _trail.Count; i++)
        {
            var sp = ProjectToScreen(_trail[i], hf);
            byte a = (byte)(180 * (i + 1) / _trail.Count);
            Raylib.DrawCircle(
                (int)(canvasOrigin.X + sp.X),
                (int)(canvasOrigin.Y + sp.Y),
                2, new Color((byte)255, (byte)255, (byte)255, a));
        }

        // Trees: sort by world Z descending so back trees draw first and near
        // ones over them.
        var sortedTrees = hole.Trees.OrderByDescending(t => t.Y);
        foreach (var t in sortedTrees)
        {
            var sp = ProjectToScreen(t, hf);
            int sx = (int)(canvasOrigin.X + sp.X);
            int sy = (int)(canvasOrigin.Y + sp.Y);
            int foliageOffset = (int)(14 * _sinT + 6);
            // Shadow on the ground at the tree base
            Raylib.DrawEllipse(sx + 2, sy + 2, 11, Math.Max(3, (int)(11 * _cosT)),
                new Color((byte)0, (byte)0, (byte)0, (byte)80));
            // Trunk: vertical rectangle from base going up
            Raylib.DrawRectangle(sx - 2, sy - foliageOffset, 4, foliageOffset + 1,
                new Color((byte)80, (byte)56, (byte)32, (byte)255));
            // Foliage
            Raylib.DrawCircle(sx, sy - foliageOffset, 12, new Color((byte)40, (byte)120, (byte)60, (byte)255));
            Raylib.DrawCircle(sx - 3, sy - foliageOffset - 3, 4, new Color((byte)80, (byte)168, (byte)96, (byte)255));
        }

        // Cup hole + flag
        int fx = (int)(canvasOrigin.X + cupScreen.X);
        int fy = (int)(canvasOrigin.Y + cupScreen.Y);
        Raylib.DrawCircle(fx, fy, 5, new Color((byte)0, (byte)0, (byte)0, (byte)255));
        // Flag pole rises straight up from the cup
        Raylib.DrawRectangle(fx - 1, fy - 26, 2, 26, RetroSkin.BodyText);
        Raylib.DrawTriangle(
            new Vector2(fx + 1, fy - 26),
            new Vector2(fx + 14, fy - 22),
            new Vector2(fx + 1, fy - 18),
            new Color((byte)220, (byte)60, (byte)60, (byte)255));

        // Tee marker
        var teeScreen = ProjectToScreen(hole.Tee, hf);
        Raylib.DrawEllipseLines(
            (int)(canvasOrigin.X + teeScreen.X),
            (int)(canvasOrigin.Y + teeScreen.Y),
            7, Math.Max(2, (int)(7 * _cosT)),
            new Color((byte)255, (byte)255, (byte)255, (byte)220));

        // Aim line + power bar
        var ballScreen = ProjectToScreen(_ball, hf);
        if (_aiming)
        {
            var ballAbs = canvasOrigin + ballScreen;
            var dir = ballScreen - _aimEnd;
            var worldDir = new Vector2(dir.X, dir.Y / Math.Max(0.05f, _cosT));
            float pwr = MathF.Min(worldDir.Length() * 4f, 380f);
            float pwrFrac = pwr / 380f;
            if (dir.LengthSquared() > 0.1f)
            {
                var endAbs = canvasOrigin + ballScreen + Vector2.Normalize(dir) * MathF.Min(dir.Length(), 110f);
                Raylib.DrawLineEx(ballAbs, endAbs, 2f, new Color((byte)255, (byte)255, (byte)255, (byte)220));
                var nrm = Vector2.Normalize(dir);
                var perp = new Vector2(-nrm.Y, nrm.X);
                Raylib.DrawTriangle(
                    endAbs,
                    endAbs - nrm * 8 + perp * 4,
                    endAbs - nrm * 8 - perp * 4,
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

        // Ball — drawn last so it sits on top.
        Raylib.DrawCircle((int)(canvasOrigin.X + ballScreen.X) + 1, (int)(canvasOrigin.Y + ballScreen.Y) + 1,
            5, new Color((byte)0, (byte)0, (byte)0, (byte)100));
        Raylib.DrawCircle((int)(canvasOrigin.X + ballScreen.X), (int)(canvasOrigin.Y + ballScreen.Y),
            5, new Color((byte)255, (byte)255, (byte)255, (byte)255));
        Raylib.DrawCircleLines((int)(canvasOrigin.X + ballScreen.X), (int)(canvasOrigin.Y + ballScreen.Y),
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

    /// <summary>
    /// Build a dot-mesh texture: a dense scatter of single-pixel dots, one
    /// per heightmap sample, projected through the same camera as the dithered
    /// terrain. Dots are coloured by elevation band on a near-black sky so
    /// the 3D shape reads as a point cloud floating in space. Used as an
    /// alternate Mesh view via the menu.
    /// </summary>
    private void BuildMeshTexture(int idx)
    {
        if (_meshBuilt[idx]) return;
        var hf = _course[idx].Heightmap;
        var img = Raylib.GenImageColor(CanvasW, CanvasH,
            new Color((byte)10, (byte)14, (byte)28, (byte)255));

        // Subtle vertical gradient on the background — slightly lighter near
        // the horizon, darker near the camera. Cheap because we just paint
        // every Nth row to skip work.
        for (int y = 0; y < CanvasH; y++)
        {
            float t = y / (float)CanvasH;
            byte r = (byte)(10 + 18 * (1 - t));
            byte g = (byte)(14 + 16 * (1 - t));
            byte b = (byte)(28 + 18 * (1 - t));
            for (int x = 0; x < CanvasW; x++)
                Raylib.ImageDrawPixel(ref img, x, y, new Color(r, g, b, (byte)255));
        }

        float min = float.MaxValue, max = float.MinValue;
        for (int y = 0; y < CanvasH; y += 3)
            for (int x = 0; x < CanvasW; x += 3)
            {
                float h = hf.Sample(x, y);
                if (h < min) min = h;
                if (h > max) max = h;
            }
        float range = MathF.Max(0.6f, max - min);
        float baseY = CanvasH - 1;

        // Dot mesh. Step 2 in worldX, ~2 in worldZ — yields roughly 540*360/4
        // ≈ 48k dots, plenty to read the 3D shape clearly.
        const int stepX = 2;
        const int stepZ = 2;
        for (int wz = 0; wz < CanvasH; wz += stepZ)
        {
            float depth = wz / (float)CanvasH;
            float fade = 1f - depth * 0.45f;        // far dots fade slightly
            for (int wx = 0; wx < CanvasW; wx += stepX)
            {
                float h = hf.Sample(wx, wz);
                int sy = (int)MathF.Round(baseY - wz * _cosT - h * _sinT);
                if (sy < 0 || sy >= CanvasH) continue;

                float t = (h - min) / range;
                int bandIdx = Math.Clamp(
                    (int)(t * (TerrainBands.Length - 1) + 0.5f),
                    0, TerrainBands.Length - 1);
                var col = TerrainBands[bandIdx];
                byte r = (byte)Math.Clamp(col.R * fade, 0, 255);
                byte g = (byte)Math.Clamp(col.G * fade, 0, 255);
                byte b = (byte)Math.Clamp(col.B * fade, 0, 255);
                Raylib.ImageDrawPixel(ref img, wx, sy, new Color(r, g, b, (byte)255));
            }
        }

        _meshTex[idx] = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);
        _meshBuilt[idx] = true;
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
