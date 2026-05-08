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
    // Slope force below this can't kick the ball back into motion at rest —
    // models static friction. Anything gentler than ~280 px/s² of pull is
    // shrugged off, which kills the micro-rolling that wouldn't otherwise
    // settle on shallow slopes.
    private const float StaticFrictionAccel = 280f;
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
    private int[] _strokes = new int[Holes];
    private int _holeIdx;
    private Vector2 _ball;       // .X = worldX, .Y = worldZ (depth)
    private Vector2 _vel;
    private bool _aiming;
    private Vector2 _aimEnd;     // canvas screen coords
    private bool _holeComplete;
    private bool _roundComplete;
    private float _holeFlashTimer;
    // Per-hole-complete celebration. Set when the ball drops in the cup
    // and consumed by the wave-text overlay; reset on hole reset/start.
    private string _celebrationText = "";
    private float _celebrationTime;
    // Trail puffs carry the ball's speed at the moment they were dropped so
    // we can render slow rolls as a few fat puffs and fast shots as a thin,
    // strung-out streak. Life decays in real time (independent of how fast
    // new puffs are being added) so dust dissipates even when the ball is
    // crawling and not laying down many new puffs.
    private List<(Vector2 Pos, float Speed, float Life)> _trail = new();
    private float _trailDistAccum;
    private const float TrailLifetimeSeconds = 0.55f;
    private readonly Random _rng = new();

    // Camera tilt (pitch). 0 = top-down. 35° default. Right-click+drag in
    // the scene rotates it live.
    private float _tiltDeg = 35f;
    private float _cosT = MathF.Cos(35f * MathF.PI / 180f);
    private float _sinT = MathF.Sin(35f * MathF.PI / 180f);
    private bool _tiltDragging;
    private float _tiltDragLastY;

    // Aim mode: Combo (default) draws both arc + line; Line is just the line
    // and arrow; Arc is the dotted trajectory + pulsing landing target.
    private enum AimStyle { Combo, Line, Arc }
    private AimStyle _aimStyle = AimStyle.Combo;

    // Single cached mesh texture for the current (hole, tilt). Invalidated
    // on hole / tilt change. While the user is right-dragging tilt we skip
    // the cache and re-render the mesh live (sparser) each frame.
    private Texture2D _activeTex;
    private bool _activeTexBuilt;
    private int _activeTexHole = -1;

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

    public void Load()
    {
        // Restore the persisted palette choice; clamp in case the user's
        // saved index is stale and out of range for the current preset
        // list.
        var s = MouseHouse.Core.SaveManager.LoadOrDefault<FujiGolfPrefs>("fuji_golf.json");
        _meshPaletteIdx = Math.Clamp(s.PaletteIdx, 0, MeshPalettes.Length - 1);
        _skyPaletteIdx = Math.Clamp(s.SkyIdx, 0, SkyPalettes.Length - 1);
        _bgPaletteIdx = Math.Clamp(s.BgIdx, 0, BgPalettes.Length - 1);
        TryLoadTreeSprite();
        StartRound();
    }

    // User-replaceable tree sprite. If `assets/golf/tree.png` exists, it
    // replaces the procedural triangle/circle tree at draw time. Drawn
    // anchored at the base center, scaled to fit a 28×40 box so any
    // reasonable pixel-art image will look right.
    private Texture2D _treeTex;
    private bool _treeTexLoaded;

    private void TryLoadTreeSprite()
    {
        if (_treeTexLoaded) return;
        var path = ResolveAssetPath("golf/tree.png");
        if (path == null) return;
        var tex = Raylib.LoadTexture(path);
        if (tex.Width == 0 || tex.Height == 0) return;
        Raylib.SetTextureFilter(tex, TextureFilter.Point);
        _treeTex = tex;
        _treeTexLoaded = true;
    }

    /// <summary>
    /// Walk up from the executable directory looking for an `assets`
    /// folder so the same code works under `dotnet run` (assets in repo
    /// root) and the published binary (assets next to the exe).
    /// </summary>
    private static string? ResolveAssetPath(string relative)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "assets", relative);
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        return null;
    }

    private class FujiGolfPrefs
    {
        public int PaletteIdx { get; set; }
        public int SkyIdx { get; set; }
        public int BgIdx { get; set; }
    }

    private string[] MenuLabels() => new[]
    {
        "New Round", "Replay Hole", "Skip",
        $"Aim: {_aimStyle}",
        $"Palette: {MeshPalettes[_meshPaletteIdx].Name}",
        $"Sky: {SkyPalettes[_skyPaletteIdx].Name}",
        $"Background: {BgPalettes[_bgPaletteIdx].Name}",
        "Help",
    };

    private void SaveFujiPrefs() =>
        MouseHouse.Core.SaveManager.Save("fuji_golf.json",
            new FujiGolfPrefs
            {
                PaletteIdx = _meshPaletteIdx,
                SkyIdx = _skyPaletteIdx,
                BgIdx = _bgPaletteIdx,
            });

    public void Close()
    {
        UnloadTerrainTextures();
        if (_treeTexLoaded)
        {
            Raylib.UnloadTexture(_treeTex);
            _treeTexLoaded = false;
        }
    }

    private void UnloadTerrainTextures()
    {
        if (_activeTexBuilt)
        {
            Raylib.UnloadTexture(_activeTex);
            _activeTexBuilt = false;
            _activeTexHole = -1;
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
        _celebrationText = "";
        _celebrationTime = 0f;
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

    // ── Mesh palette + dithering ────────────────────────────────────────
    // Rich 16-band ramp running cool-low → warm-high. Each adjacent pair of
    // bands gets dithered together with an 8×8 Bayer matrix so the dot
    // cloud reads as a continuous gradient instead of obvious colour bands.
    /// <summary>
    /// Named heightfield palettes the user can cycle through via the
    /// Palette menu item. Each is a 16-entry low→high gradient that
    /// MeshDitheredColor band-quantises. Add new entries here to expose
    /// them in the cycle.
    /// </summary>
    private static readonly (string Name, Color[] Stops)[] MeshPalettes =
    {
        ("Rainbow", new Color[]
        {
            new((byte) 28, (byte) 56, (byte) 96, (byte)255),
            new((byte) 36, (byte) 84, (byte)116, (byte)255),
            new((byte) 48, (byte)116, (byte)128, (byte)255),
            new((byte) 64, (byte)148, (byte)128, (byte)255),
            new((byte) 84, (byte)170, (byte)108, (byte)255),
            new((byte)108, (byte)188, (byte) 92, (byte)255),
            new((byte)140, (byte)200, (byte) 80, (byte)255),
            new((byte)180, (byte)204, (byte) 72, (byte)255),
            new((byte)212, (byte)196, (byte) 72, (byte)255),
            new((byte)228, (byte)168, (byte) 64, (byte)255),
            new((byte)232, (byte)128, (byte) 60, (byte)255),
            new((byte)224, (byte) 96, (byte) 76, (byte)255),
            new((byte)212, (byte)104, (byte)128, (byte)255),
            new((byte)220, (byte)152, (byte)180, (byte)255),
            new((byte)232, (byte)196, (byte)216, (byte)255),
            new((byte)244, (byte)228, (byte)236, (byte)255),
        }),
        ("Greens", new Color[]
        {
            new((byte) 12, (byte) 36, (byte) 18, (byte)255),
            new((byte) 18, (byte) 50, (byte) 24, (byte)255),
            new((byte) 26, (byte) 66, (byte) 32, (byte)255),
            new((byte) 36, (byte) 84, (byte) 42, (byte)255),
            new((byte) 48, (byte)104, (byte) 54, (byte)255),
            new((byte) 60, (byte)122, (byte) 64, (byte)255),
            new((byte) 76, (byte)142, (byte) 76, (byte)255),
            new((byte) 92, (byte)160, (byte) 88, (byte)255),
            new((byte)108, (byte)178, (byte)100, (byte)255),
            new((byte)128, (byte)196, (byte)112, (byte)255),
            new((byte)148, (byte)212, (byte)128, (byte)255),
            new((byte)170, (byte)224, (byte)148, (byte)255),
            new((byte)200, (byte)226, (byte)168, (byte)255),
            new((byte)218, (byte)234, (byte)182, (byte)255),
            new((byte)232, (byte)234, (byte)196, (byte)255),
            new((byte)240, (byte)230, (byte)204, (byte)255),
        }),
        ("Sunset", new Color[]
        {
            new((byte) 28, (byte)  6, (byte) 38, (byte)255),
            new((byte) 48, (byte) 14, (byte) 54, (byte)255),
            new((byte) 72, (byte) 22, (byte) 72, (byte)255),
            new((byte)100, (byte) 30, (byte) 84, (byte)255),
            new((byte)132, (byte) 38, (byte) 88, (byte)255),
            new((byte)164, (byte) 50, (byte) 80, (byte)255),
            new((byte)192, (byte) 64, (byte) 70, (byte)255),
            new((byte)214, (byte) 84, (byte) 60, (byte)255),
            new((byte)228, (byte)108, (byte) 52, (byte)255),
            new((byte)238, (byte)136, (byte) 50, (byte)255),
            new((byte)244, (byte)164, (byte) 56, (byte)255),
            new((byte)248, (byte)188, (byte) 70, (byte)255),
            new((byte)250, (byte)208, (byte) 96, (byte)255),
            new((byte)252, (byte)224, (byte)132, (byte)255),
            new((byte)254, (byte)238, (byte)176, (byte)255),
            new((byte)254, (byte)248, (byte)218, (byte)255),
        }),
        ("Ocean", new Color[]
        {
            new((byte)  4, (byte) 12, (byte) 36, (byte)255),
            new((byte)  8, (byte) 22, (byte) 56, (byte)255),
            new((byte) 14, (byte) 36, (byte) 78, (byte)255),
            new((byte) 22, (byte) 54, (byte)100, (byte)255),
            new((byte) 30, (byte) 72, (byte)122, (byte)255),
            new((byte) 38, (byte) 92, (byte)142, (byte)255),
            new((byte) 48, (byte)112, (byte)160, (byte)255),
            new((byte) 60, (byte)134, (byte)176, (byte)255),
            new((byte) 76, (byte)156, (byte)190, (byte)255),
            new((byte) 96, (byte)178, (byte)204, (byte)255),
            new((byte)122, (byte)198, (byte)216, (byte)255),
            new((byte)152, (byte)216, (byte)226, (byte)255),
            new((byte)180, (byte)230, (byte)234, (byte)255),
            new((byte)208, (byte)240, (byte)240, (byte)255),
            new((byte)230, (byte)248, (byte)246, (byte)255),
            new((byte)246, (byte)252, (byte)250, (byte)255),
        }),
    };

    /// <summary>
    /// Sky presets — used as the TOP of the canvas gradient (the strip
    /// above the playfield horizon). Cycle via the "Sky" menu item.
    /// </summary>
    private static readonly (string Name, Color Color)[] SkyPalettes =
    {
        ("Night",  new Color((byte) 28, (byte) 30, (byte) 46, (byte)255)),
        ("Dawn",   new Color((byte)244, (byte)188, (byte)170, (byte)255)),
        ("Day",    new Color((byte)164, (byte)200, (byte)232, (byte)255)),
        ("Dusk",   new Color((byte)200, (byte)112, (byte) 96, (byte)255)),
        ("White",  new Color((byte)248, (byte)246, (byte)240, (byte)255)),
        ("Storm",  new Color((byte) 84, (byte) 90, (byte)100, (byte)255)),
    };

    /// <summary>
    /// Background presets — used as the BOTTOM of the canvas gradient
    /// (the ground showing through behind/under the terrain mesh). Cycle
    /// via the "Background" menu item.
    /// </summary>
    private static readonly (string Name, Color Color)[] BgPalettes =
    {
        ("Black",  new Color((byte) 10, (byte) 14, (byte) 28, (byte)255)),
        ("Forest", new Color((byte) 18, (byte) 48, (byte) 28, (byte)255)),
        ("Sage",   new Color((byte)126, (byte)160, (byte)116, (byte)255)),
        ("Earth",  new Color((byte)112, (byte) 80, (byte) 52, (byte)255)),
        ("Sand",   new Color((byte)204, (byte)178, (byte)128, (byte)255)),
        ("Slate",  new Color((byte) 96, (byte)104, (byte)112, (byte)255)),
        ("Cream",  new Color((byte)220, (byte)200, (byte)170, (byte)255)),
    };

    private int _meshPaletteIdx;
    private int _skyPaletteIdx;
    private int _bgPaletteIdx;
    private Color[] MeshPalette => MeshPalettes[_meshPaletteIdx].Stops;

    // 8×8 Bayer ordered-dithering matrix. 64 thresholds give finer band
    // transitions than the 4×4 we used before.
    private static readonly int[,] Bayer8 =
    {
        {  0, 32,  8, 40,  2, 34, 10, 42 },
        { 48, 16, 56, 24, 50, 18, 58, 26 },
        { 12, 44,  4, 36, 14, 46,  6, 38 },
        { 60, 28, 52, 20, 62, 30, 54, 22 },
        {  3, 35, 11, 43,  1, 33,  9, 41 },
        { 51, 19, 59, 27, 49, 17, 57, 25 },
        { 15, 47,  7, 39, 13, 45,  5, 37 },
        { 63, 31, 55, 23, 61, 29, 53, 21 },
    };

    private void EnsureActiveTexture()
    {
        if (_activeTexBuilt && _activeTexHole == _holeIdx) return;
        if (_activeTexBuilt) Raylib.UnloadTexture(_activeTex);
        var hf = _course[_holeIdx].Heightmap;
        var img = BuildMeshImage(hf, stepX: 2, stepZ: 2);
        _activeTex = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);
        _activeTexBuilt = true;
        _activeTexHole = _holeIdx;
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
        switch (RetroWidgets.MenuBarHitTest(menuBar, MenuLabels(), local, leftPressed))
        {
            case 0: StartRound(); return;
            case 1: ResetBall(); return;
            case 2: AdvanceHole(); return;
            case 3:
                _aimStyle = (AimStyle)(((int)_aimStyle + 1) % 3);
                return;
            case 4:
                _meshPaletteIdx = (_meshPaletteIdx + 1) % MeshPalettes.Length;
                // Force the per-hole mesh texture to rebuild with the
                // new palette next frame, and persist the choice.
                UnloadTerrainTextures();
                SaveFujiPrefs();
                return;
            case 5:
                _skyPaletteIdx = (_skyPaletteIdx + 1) % SkyPalettes.Length;
                UnloadTerrainTextures();
                SaveFujiPrefs();
                return;
            case 6:
                _bgPaletteIdx = (_bgPaletteIdx + 1) % BgPalettes.Length;
                UnloadTerrainTextures();
                SaveFujiPrefs();
                return;
            case 7: _help.Visible = !_help.Visible; return;
        }
        if (_help.HandleInput(local, leftPressed, PanelSize)) return;

        if (_roundComplete) return;

        var canvasOrigin = new Vector2(FrameInset,
            FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight);
        var canvasMouse = local - canvasOrigin;

        if (_holeComplete)
        {
            _holeFlashTimer += delta;
            _celebrationTime += delta;
            // Hold the celebration for ~2 s so the wave text has time to
            // do a few cycles before the next hole loads.
            if (_holeFlashTimer > 2.0f) AdvanceHole();
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
            // Static friction: kill velocity when both speed and slope force
            // are small. Bigger threshold than before so the ball actually
            // settles instead of micro-rolling forever on gentle grades.
            if (_vel.LengthSquared() < 9f
                && slopeAccel.LengthSquared() < StaticFrictionAccel * StaticFrictionAccel)
            {
                if (_vel != Vector2.Zero)
                {
                    _vel = Vector2.Zero;
                    _trail.Clear();          // ball is parked → clean up the trail
                }
            }

            if (_vel.LengthSquared() > 0.01f)
            {
                var step = _vel * delta;
                _ball += step;
                // Drop a puff every ~6 world units traveled — slow rolls
                // produce few puffs, fast shots produce many. Speed is
                // captured per-puff so the renderer can size them.
                _trailDistAccum += step.Length();
                if (_trailDistAccum > 6f)
                {
                    _trailDistAccum = 0;
                    // Scatter puffs around the ball when it's rolling slowly,
                    // so the trail reads as a dust cloud surrounding the
                    // ball rather than a tight bead-on-a-string. Fast shots
                    // get zero scatter so the streak still looks linear.
                    float spd = _vel.Length();
                    float scatter = (1f - Math.Clamp(spd / 180f, 0f, 1f)) * 4f;
                    float ang = (float)(_rng.NextDouble() * Math.PI * 2);
                    float dist = (float)_rng.NextDouble() * scatter;
                    var puff = _ball + new Vector2(MathF.Cos(ang) * dist, MathF.Sin(ang) * dist);
                    _trail.Add((puff, spd, 1f));
                    if (_trail.Count > 16) _trail.RemoveAt(0);
                }
            }

            // Age existing puffs in real time and reap dead ones — gives a
            // consistent "puff fades in ~0.55 s" feel regardless of speed.
            for (int i = _trail.Count - 1; i >= 0; i--)
            {
                var (p, s, l) = _trail[i];
                l -= delta / TrailLifetimeSeconds;
                if (l <= 0f) _trail.RemoveAt(i);
                else _trail[i] = (p, s, l);
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
                int strokes = _strokes[_holeIdx];
                int par = _course[_holeIdx].Par;
                _celebrationText = strokes == 1 ? "HOLE IN ONE!"
                    : strokes == par - 2 ? "EAGLE!"
                    : strokes == par - 1 ? "BIRDIE!"
                    : strokes == par     ? "PAR!"
                    : "";
                _celebrationTime = 0f;
            }
        }

        // Right-click drag in the canvas rotates the camera tilt live. The
        // wireframe preview kicks in for the duration so we don't pay the
        // ~150ms texture rebuild on every mouse-move; on release the cached
        // texture is invalidated so the next draw rebuilds at the new angle.
        bool inCanvas = canvasMouse.X >= 0 && canvasMouse.X < CanvasW
                     && canvasMouse.Y >= 0 && canvasMouse.Y < CanvasH;
        if (rightPressed && inCanvas)
        {
            _tiltDragging = true;
            _tiltDragLastY = canvasMouse.Y;
        }
        if (_tiltDragging)
        {
            if (Raylib.IsMouseButtonDown(MouseButton.Right))
            {
                float dy = canvasMouse.Y - _tiltDragLastY;
                _tiltDragLastY = canvasMouse.Y;
                _tiltDeg = Math.Clamp(_tiltDeg - dy * 0.45f, 0, 60);
                _cosT = MathF.Cos(_tiltDeg * MathF.PI / 180f);
                _sinT = MathF.Sin(_tiltDeg * MathF.PI / 180f);
            }
            else
            {
                _tiltDragging = false;
                UnloadTerrainTextures();    // rebuild at the new angle on next draw
            }
        }

        // Aiming uses the projected ball position so the user clicks where
        // they see it. Re-hit is allowed only when the ball is at most slowly
        // rolling so a panicky click mid-flight doesn't cancel a real shot.
        var ballScreen = ProjectToScreen(_ball, hf);
        const float MaxRehitSpeed = 60f;
        bool ballSlow = _vel.Length() < MaxRehitSpeed;

        // Generous hitbox — clicking anywhere within ~28 px of the ball
        // starts an aim, since the ball sprite itself is tiny and a tight
        // hit-test feels finicky.
        const float BallHitRadius = 28f;
        if (ballSlow && leftPressed
            && (canvasMouse - ballScreen).LengthSquared() < BallHitRadius * BallHitRadius)
            _aiming = true;
        if (_aiming) _aimEnd = canvasMouse;
        if (leftReleased && _aiming)
        {
            _aiming = false;
            // Slingshot launch direction = ball − cursor in screen space.
            // The projection inverts Y (positive worldZ projects toward the
            // top of screen, smaller screenY), so we negate Y when going
            // back from screen to world. Without this, the line and the ball
            // ended up moving in opposite directions on screen.
            var screenDir = ballScreen - _aimEnd;
            var worldDir = new Vector2(screenDir.X, -screenDir.Y / Math.Max(0.05f, _cosT));
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
        _celebrationText = "";
        _celebrationTime = 0f;
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
        RetroWidgets.MenuBarVisual(menuBar, MenuLabels(), -1);

        var canvasOrigin = new Vector2(
            panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight);
        var canvas = new Rectangle(canvasOrigin.X, canvasOrigin.Y, CanvasW, CanvasH);

        DrawCourse(canvasOrigin, canvas);
        DrawScorecard(panelOffset);
        DrawPaletteSwatch(panelOffset);

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
        var hole = _course[_holeIdx];
        var hf = hole.Heightmap;

        // While right-dragging the tilt, skip the cached texture and render
        // a sparser mesh live each frame so the user always sees the mesh
        // they're tilting (no wireframe substitute). On release the cache
        // gets rebuilt at full density.
        if (_tiltDragging)
        {
            DrawMeshLive(canvasOrigin, canvas, hf);
        }
        else
        {
            EnsureActiveTexture();
            Raylib.DrawTexture(_activeTex,
                (int)canvasOrigin.X, (int)canvasOrigin.Y, Color.White);
        }

        // Hazards: project center, draw a two-tone dithered-fill
        // ellipse with a dithered rim. The interior alternates between
        // a primary and a slightly darker secondary on a Bayer 4×4
        // pattern so the fill reads as a texture instead of a flat
        // disc, and the rim drops off through the same matrix for a
        // crisp pixel-art edge.
        foreach (var (c, rx, ry, kind) in hole.Hazards)
        {
            var sp = ProjectToScreen(c, hf);
            int cx = (int)(canvasOrigin.X + sp.X);
            int cy = (int)(canvasOrigin.Y + sp.Y);
            int yRad = Math.Max(2, (int)(ry * _cosT));
            Color primary, secondary;
            if (kind == 0)
            {
                // Sand: warm tans
                primary   = new Color((byte)236, (byte)212, (byte)148, (byte)255);
                secondary = new Color((byte)204, (byte)178, (byte)112, (byte)255);
            }
            else
            {
                // Water: deeper / lighter blue stipple
                primary   = new Color((byte) 64, (byte)112, (byte)208, (byte)255);
                secondary = new Color((byte) 28, (byte) 72, (byte)160, (byte)255);
            }
            DrawDitheredEllipse(cx, cy, (int)rx, yRad, primary, secondary);
        }

        // Green: lighter circle around cup, two-tone dither so it
        // reads as turf, not a flat disc. Lighter / darker grass.
        var cupScreen = ProjectToScreen(hole.Cup, hf);
        DrawDitheredEllipse(
            (int)(canvasOrigin.X + cupScreen.X),
            (int)(canvasOrigin.Y + cupScreen.Y),
            36, Math.Max(8, (int)(36 * _cosT)),
            new Color((byte)128, (byte)212, (byte)104, (byte)255),
            new Color((byte) 88, (byte)178, (byte) 80, (byte)255));

        // Flagpole + pendant flag at the cup. Pole leans with the camera
        // tilt so it stays "vertical in world" — top is always above the
        // cup. The pendant's color/pattern cycles per hole.
        DrawFlag(canvasOrigin, cupScreen);

        // Trail — fade older points by dithering rather than alpha so the
        // trail keeps its hard pixel-art look instead of going translucent.
        for (int i = 0; i < _trail.Count; i++)
        {
            var (pos, speed, life) = _trail[i];
            var sp = ProjectToScreen(pos, hf);
            int cx = (int)(canvasOrigin.X + sp.X);
            int cy = (int)(canvasOrigin.Y + sp.Y);
            // Slow ball → fat puff, fast ball → thin streak. Map speed
            // (~0..220 in practice) to radius 5 down to 1.
            float speedFrac = Math.Clamp(speed / 180f, 0f, 1f);
            int radius = (int)MathF.Round(5f - 4f * speedFrac);
            // Fat puffs render thinner (more dithered) so they read as a
            // sparse dust cloud, not a solid disc. Combined with the
            // real-time life decay this also makes the trail dissipate
            // visibly faster.
            float densityCap = 0.45f + 0.55f * speedFrac;
            float coverage = life * densityCap;
            // Warm dust: faint brown-beige-gray, not pure neutral.
            DrawDitheredDot(cx, cy, radius,
                new Color((byte)178, (byte)168, (byte)148, (byte)255),
                coverage);
        }

        // Trees: sort by world Z descending so back trees draw first and near
        // ones over them.
        var sortedTrees = hole.Trees.OrderByDescending(t => t.Y);
        foreach (var t in sortedTrees)
        {
            var sp = ProjectToScreen(t, hf);
            int sx = (int)(canvasOrigin.X + sp.X);
            int sy = (int)(canvasOrigin.Y + sp.Y);
            // Shadow on the ground at the tree base — used by both the
            // sprite and the procedural fallback.
            Raylib.DrawEllipse(sx + 2, sy + 2, 11, Math.Max(3, (int)(11 * _cosT)),
                new Color((byte)0, (byte)0, (byte)0, (byte)80));

            if (_treeTexLoaded)
            {
                // Fit user-supplied PNG into a 28×40 box, anchored at the
                // base center. Scaled with nearest-neighbor for pixel art.
                const float boxW = 28f;
                const float boxH = 40f;
                float scale = MathF.Min(boxW / _treeTex.Width, boxH / _treeTex.Height);
                float dw = _treeTex.Width * scale;
                float dh = _treeTex.Height * scale;
                Raylib.DrawTexturePro(_treeTex,
                    new Rectangle(0, 0, _treeTex.Width, _treeTex.Height),
                    new Rectangle(sx - dw / 2f, sy - dh, dw, dh),
                    Vector2.Zero, 0f, Color.White);
                continue;
            }

            int foliageOffset = (int)(14 * _sinT + 6);
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

        // Aim — Line or Arc style + power bar
        var ballScreen = ProjectToScreen(_ball, hf);
        if (_aiming)
        {
            var ballAbs = canvasOrigin + ballScreen;
            var dir = ballScreen - _aimEnd;
            // Negate Y because positive worldZ projects to smaller screenY.
            // The arc preview must use the same world velocity the live shot
            // will use on release — otherwise the prediction wouldn't match.
            var worldDir = new Vector2(dir.X, -dir.Y / Math.Max(0.05f, _cosT));
            float pwr = MathF.Min(worldDir.Length() * 4f, 380f);
            float pwrFrac = pwr / 380f;

            if (dir.LengthSquared() > 0.1f && pwr > 12)
            {
                bool drawArc = _aimStyle != AimStyle.Line;
                bool drawLine = _aimStyle != AimStyle.Arc;
                bool drawNaive = _aimStyle == AimStyle.Combo;
                // Naive (obstacle-free) ghost arc first so the accurate gold
                // arc and the line + arrow paint on top.
                if (drawNaive) DrawNaiveArc(canvasOrigin, worldDir, pwr, hf);
                if (drawArc)   DrawAimArc(canvasOrigin, worldDir, pwr, hf);
                if (drawLine)  DrawAimLine(ballAbs, dir);
            }

            // Power bar (top-left of canvas)
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
            // Top-line score readout, plus a wave-animated celebration
            // word ("PAR!" / "BIRDIE!" / "EAGLE!" / "HOLE IN ONE!") below.
            string msg = $"Holed in {_strokes[_holeIdx]} (par {_course[_holeIdx].Par})";
            int w = RetroSkin.MeasureText(msg, 22);
            int x = (int)(canvas.X + (canvas.Width - w) / 2);
            int y = (int)(canvas.Y + canvas.Height / 2 - 12);
            int boxH = string.IsNullOrEmpty(_celebrationText) ? 36 : 80;
            int boxW = string.IsNullOrEmpty(_celebrationText)
                ? w + 24
                : Math.Max(w + 24, RetroSkin.MeasureText(_celebrationText, 32) + 40);
            Raylib.DrawRectangle((int)(canvas.X + (canvas.Width - boxW) / 2), y - 6,
                boxW, boxH, new Color((byte)0, (byte)0, (byte)0, (byte)180));
            RetroSkin.DrawText(msg, x, y, new Color((byte)255, (byte)240, (byte)140, (byte)255), 22);
            if (!string.IsNullOrEmpty(_celebrationText))
                DrawWaveText(_celebrationText, (int)canvas.X + (int)canvas.Width / 2,
                    y + 36, 32, _celebrationTime);
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
    /// <summary>
    /// Build a dot-cloud image: one coloured dot per heightmap sample at
    /// step 2 in both axes, projected through the current camera. Renders
    /// over a dark sky-to-ground gradient so the cloud reads as floating
    /// in 3D space.
    /// </summary>
    private Image BuildMeshImage(HeightField hf, int stepX = 2, int stepZ = 2)
    {
        var sky = SkyPalettes[_skyPaletteIdx].Color;
        var ground = BgPalettes[_bgPaletteIdx].Color;
        var img = Raylib.GenImageColor(CanvasW, CanvasH, ground);

        // Sky-to-ground gradient backdrop. Top of canvas = sky color,
        // bottom of canvas = background/ground color; lerped per scanline.
        for (int y = 0; y < CanvasH; y++)
        {
            float t = y / (float)CanvasH;
            byte r = (byte)(sky.R + (ground.R - sky.R) * t);
            byte g = (byte)(sky.G + (ground.G - sky.G) * t);
            byte b = (byte)(sky.B + (ground.B - sky.B) * t);
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

        for (int wz = 0; wz < CanvasH; wz += stepZ)
        {
            float depth = wz / (float)CanvasH;
            float fade = 1f - depth * 0.45f;
            for (int wx = 0; wx < CanvasW; wx += stepX)
            {
                float h = hf.Sample(wx, wz);
                int sy = (int)MathF.Round(baseY - wz * _cosT - h * _sinT);
                if (sy < 0 || sy >= CanvasH) continue;

                var col = MeshDitheredColor(h, min, range, wx, sy, fade);
                Raylib.ImageDrawPixel(ref img, wx, sy, col);
            }
        }
        return img;
    }

    /// <summary>
    /// Pick a band colour for the elevation, dithered between adjacent bands
    /// using the 8×8 Bayer matrix at (x,y), and faded for atmospheric depth.
    /// Higher band count + finer Bayer = visible gradient with subtle noise.
    /// </summary>
    private Color MeshDitheredColor(float h, float min, float range, int x, int y, float fade)
    {
        float t = (h - min) / range;
        float bandFloat = t * (MeshPalette.Length - 1);
        int b = Math.Clamp((int)bandFloat, 0, MeshPalette.Length - 2);
        float frac = Math.Clamp(bandFloat - b, 0, 1);
        int bayer = Bayer8[((x % 8) + 8) % 8, ((y % 8) + 8) % 8];
        float thresh = (bayer + 0.5f) / 64f;
        var c = frac > thresh ? MeshPalette[b + 1] : MeshPalette[b];
        // Light noise fleck once every 24 pixels for grit.
        bool fleck = (((x * 7) ^ (y * 13)) & 0x1F) == 0;
        float jitter = fleck ? 0.85f : 1f;
        return new Color(
            (byte)Math.Clamp(c.R * fade * jitter, 0, 255),
            (byte)Math.Clamp(c.G * fade * jitter, 0, 255),
            (byte)Math.Clamp(c.B * fade * jitter, 0, 255),
            (byte)255);
    }

    /// <summary>
    /// Live, immediate-mode mesh draw — used while the user is right-click-
    /// dragging the camera tilt so they get continuous feedback without
    /// paying the ~150 ms cost to rebuild the cached texture every frame.
    /// Sparser step keeps it ~30 fps.
    /// </summary>
    private void DrawMeshLive(Vector2 canvasOrigin, Rectangle canvas, HeightField hf)
    {
        // Background — flat fill of the sky/ground midpoint while the user
        // drags the camera; cached path paints the full gradient.
        var skyL = SkyPalettes[_skyPaletteIdx].Color;
        var groundL = BgPalettes[_bgPaletteIdx].Color;
        var bgMid = new Color(
            (byte)((skyL.R + groundL.R) / 2),
            (byte)((skyL.G + groundL.G) / 2),
            (byte)((skyL.B + groundL.B) / 2),
            (byte)255);
        Raylib.DrawRectangle((int)canvas.X, (int)canvas.Y, (int)canvas.Width, (int)canvas.Height, bgMid);

        float min = float.MaxValue, max = float.MinValue;
        for (int y = 0; y < CanvasH; y += 6)
            for (int x = 0; x < CanvasW; x += 6)
            {
                float h = hf.Sample(x, y);
                if (h < min) min = h;
                if (h > max) max = h;
            }
        float range = MathF.Max(0.6f, max - min);
        float baseY = CanvasH - 1;
        const int step = 4;

        for (int wz = 0; wz < CanvasH; wz += step)
        {
            float depth = wz / (float)CanvasH;
            float fade = 1f - depth * 0.45f;
            for (int wx = 0; wx < CanvasW; wx += step)
            {
                float h = hf.Sample(wx, wz);
                int sy = (int)MathF.Round(baseY - wz * _cosT - h * _sinT);
                if (sy < 0 || sy >= CanvasH) continue;
                var col = MeshDitheredColor(h, min, range, wx, sy, fade);
                Raylib.DrawRectangle((int)canvasOrigin.X + wx, (int)canvasOrigin.Y + sy,
                    step, step, col);
            }
        }
    }

    // ── Ball physics — shared between live Update and aim simulation ────
    private enum BallStep { Moving, Settled, Holed, Drowned }

    /// <summary>
    /// One physics tick on the ball. Same maths the live Update runs, used
    /// in the aim Arc preview so the predicted path matches what the ball
    /// will actually do (including tree bounces, wall bounces, water drops,
    /// hole detection, and per-terrain friction).
    /// </summary>
    private BallStep StepBall(ref Vector2 pos, ref Vector2 vel, float dt, HeightField hf)
    {
        var grad = hf.Gradient(pos.X, pos.Y);
        var slopeAccel = -grad * GravStrength;
        vel += slopeAccel * dt;

        int t = TerrainAtFor(pos);
        float visc = t switch { 2 => 5.5f, 1 => 2.6f, 4 => 0.8f, _ => 1.4f };
        vel *= MathF.Max(0, 1f - visc * dt);
        float speed = vel.Length();
        if (speed > 0.01f)
        {
            float decel = KineticFriction * dt;
            if (decel >= speed) vel = Vector2.Zero;
            else vel -= Vector2.Normalize(vel) * decel;
        }
        if (vel.LengthSquared() < 9f
            && slopeAccel.LengthSquared() < StaticFrictionAccel * StaticFrictionAccel)
        {
            vel = Vector2.Zero;
            return BallStep.Settled;
        }

        pos += vel * dt;

        // Wall bounces
        if (pos.X < 6 || pos.X > CanvasW - 6) { vel.X = -vel.X * 0.55f; pos.X = Math.Clamp(pos.X, 6, CanvasW - 6); }
        if (pos.Y < 6 || pos.Y > CanvasH - 6) { vel.Y = -vel.Y * 0.55f; pos.Y = Math.Clamp(pos.Y, 6, CanvasH - 6); }

        // Tree bounces
        foreach (var tree in _course[_holeIdx].Trees)
        {
            var diff = pos - tree;
            const float treeR = 12, ballR = 5;
            if (diff.LengthSquared() < (treeR + ballR) * (treeR + ballR))
            {
                var n = Vector2.Normalize(diff);
                vel = Vector2.Reflect(vel, n) * 0.7f;
                pos = tree + n * (treeR + ballR + 0.5f);
            }
        }

        // Water hazard
        if (TerrainAtFor(pos) == 3) return BallStep.Drowned;

        // Holed
        if (Vector2.Distance(pos, _course[_holeIdx].Cup) < 8 && vel.Length() < 90f)
            return BallStep.Holed;

        return BallStep.Moving;
    }

    /// <summary>Terrain at a given position — same as the existing Terrain() but pure (no _ball reference).</summary>
    private int TerrainAtFor(Vector2 p)
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

    /// <summary>
    /// "Naive" path — slope + friction only, no obstacles. Drawn next to the
    /// accurate path in Combo mode as a ghost reference so the player can see
    /// how much the trees / water / cup actually change the outcome. Always
    /// assumes fairway friction.
    /// </summary>
    private List<Vector2> SimulatePathNaive(Vector2 startPos, Vector2 startVel, int maxSteps = 240)
    {
        var path = new List<Vector2>(maxSteps);
        var pos = startPos;
        var vel = startVel;
        const float dt = 1f / 60f;
        var hf = _course[_holeIdx].Heightmap;

        for (int step = 0; step < maxSteps; step++)
        {
            path.Add(pos);
            var grad = hf.Gradient(pos.X, pos.Y);
            var slopeAccel = -grad * GravStrength;
            vel += slopeAccel * dt;
            vel *= MathF.Max(0, 1f - 1.4f * dt);          // fairway friction
            float speed = vel.Length();
            if (speed > 0.01f)
            {
                float decel = KineticFriction * dt;
                if (decel >= speed) vel = Vector2.Zero;
                else vel -= Vector2.Normalize(vel) * decel;
            }
            if (vel.LengthSquared() < 9f
                && slopeAccel.LengthSquared() < StaticFrictionAccel * StaticFrictionAccel)
                break;
            pos += vel * dt;
            if (pos.X < 0 || pos.X >= CanvasW || pos.Y < 0 || pos.Y >= CanvasH) break;
        }
        return path;
    }

    /// <summary>
    /// Forward-simulate the entire shot using the live ball physics, so the
    /// arc preview matches reality including tree bounces, wall bounces, water
    /// drops, and the cup. Returns the path and a flag for the end state.
    /// </summary>
    private (List<Vector2> path, BallStep end) SimulatePath(Vector2 startPos, Vector2 startVel, int maxSteps = 240)
    {
        var path = new List<Vector2>(maxSteps);
        var pos = startPos;
        var vel = startVel;
        const float dt = 1f / 60f;
        var hf = _course[_holeIdx].Heightmap;

        for (int step = 0; step < maxSteps; step++)
        {
            path.Add(pos);
            var result = StepBall(ref pos, ref vel, dt, hf);
            if (result != BallStep.Moving)
            {
                path.Add(pos);
                return (path, result);
            }
        }
        return (path, BallStep.Moving);
    }

    // Standard 4×4 ordered-dithering threshold matrix.
    private static readonly int[,] Bayer4 =
    {
        { 0,  8,  2, 10},
        {12,  4, 14,  6},
        { 3, 11,  1,  9},
        {15,  7, 13,  5},
    };

    /// <summary>
    /// Filled ellipse with a hard, pixel-art rim AND a two-tone Bayer
    /// dither across the interior. Pixels in the outer band feather
    /// out via the same matrix; pixels inside alternate between
    /// <paramref name="primary"/> and <paramref name="secondary"/> in a
    /// checkerboard-like pattern so the fill reads as a texture, not a
    /// flat solid.
    /// </summary>
    private static void DrawDitheredEllipse(int cx, int cy, int rx, int ry, Color primary, Color secondary)
    {
        if (rx <= 0 || ry <= 0) return;
        // Even the centre is a sparse stipple — coverage starts at
        // ~62% in the middle and falls smoothly to 0% at the rim, so
        // the underlying terrain shows through the gaps everywhere
        // (heaviest at the centre, gone at the edge).
        const float centerCoverage = 0.62f;
        for (int dy = -ry; dy <= ry; dy++)
        {
            int yy = cy + dy;
            int by = ((yy % 4) + 4) % 4;
            float ny = (float)dy / ry;
            for (int dx = -rx; dx <= rx; dx++)
            {
                float nx = (float)dx / rx;
                float t = nx * nx + ny * ny;
                if (t > 1f) continue;                                  // outside ellipse
                int xx = cx + dx;
                int bx = ((xx % 4) + 4) % 4;
                int b = Bayer4[by, bx];
                // Coverage: linear in t, peaks at centre, zero at rim.
                float coverage = (1f - t) * centerCoverage;
                int threshold = (int)Math.Clamp(coverage * 16f, 0f, 16f);
                if (b >= threshold) continue;
                // Color picked on a screen-space checkerboard so painted
                // pixels alternate primary/secondary independently of
                // the Bayer pattern that shapes coverage.
                bool useSecondary = (((xx + yy) & 1) == 0);
                Raylib.DrawPixel(xx, yy, useSecondary ? secondary : primary);
            }
        }
    }

    /// <summary>
    /// Draws a small filled disc at (cx, cy) of <paramref name="radius"/>
    /// pixels, using ordered dithering against a 4×4 Bayer matrix to fade
    /// the dot. <paramref name="coverage"/> is in [0..1] — at 1.0 every
    /// pixel inside the disc is drawn solid; at 0.5 about half are drawn
    /// in a checkerboard-ish pattern; at 0 nothing is drawn. The opaque
    /// pixels are always at full alpha so the dot keeps its hard
    /// pixel-art edge instead of going translucent.
    /// </summary>
    private static void DrawDitheredDot(int cx, int cy, int radius, Color col, float coverage)
    {
        int threshold = (int)MathF.Round(Math.Clamp(coverage, 0f, 1f) * 16f);
        if (threshold <= 0) return;
        int r2 = radius * radius;
        for (int dy = -radius; dy <= radius; dy++)
        {
            int yy = cy + dy;
            int by = ((yy % 4) + 4) % 4;
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > r2) continue;
                int xx = cx + dx;
                int bx = ((xx % 4) + 4) % 4;
                if (Bayer4[by, bx] < threshold)
                    Raylib.DrawPixel(xx, yy, col);
            }
        }
    }

    /// <summary>
    /// Pendant flag and pole at the cup. Color/pattern cycles per hole
    /// so each level reads as a distinct location.
    /// </summary>
    private void DrawFlag(Vector2 canvasOrigin, Vector2 cupScreen)
    {
        int cx = (int)(canvasOrigin.X + cupScreen.X);
        int cy = (int)(canvasOrigin.Y + cupScreen.Y);
        const int poleH = 26;
        int poleTopY = cy - poleH;

        // Pole — dark wood / metal grey rectangle.
        Raylib.DrawRectangle(cx - 1, poleTopY, 2, poleH,
            new Color((byte)40, (byte)32, (byte)24, (byte)255));

        // Pendant flag colors keyed off the hole index. Six distinct
        // pairs so most rounds get a fresh look without repeating.
        var flagPalette = new (Color body, Color stripe)[]
        {
            (new((byte)220, (byte) 60, (byte) 60, (byte)255), new((byte)255, (byte)220, (byte)200, (byte)255)),
            (new((byte) 60, (byte)136, (byte)220, (byte)255), new((byte)220, (byte)240, (byte)255, (byte)255)),
            (new((byte)244, (byte)196, (byte) 60, (byte)255), new((byte) 90, (byte) 60, (byte) 24, (byte)255)),
            (new((byte) 96, (byte)180, (byte)100, (byte)255), new((byte)244, (byte)252, (byte)220, (byte)255)),
            (new((byte)180, (byte) 80, (byte)200, (byte)255), new((byte)252, (byte)232, (byte)252, (byte)255)),
            (new((byte)244, (byte)128, (byte) 56, (byte)255), new((byte) 80, (byte) 32, (byte)  8, (byte)255)),
        };
        var (body, stripe) = flagPalette[Math.Abs(_holeIdx) % flagPalette.Length];

        // Triangular pendant: base attached to pole, point trailing to
        // the right. Width 16, height 10, with a horizontal stripe in
        // the middle that varies pattern by hole (solid / stripe /
        // checker) for a bit more variety.
        int fx = cx + 1;
        int fyTop = poleTopY;
        int fyBot = poleTopY + 10;
        int fxTip = fx + 16;
        int midY = (fyTop + fyBot) / 2;

        // Body fill: scanline pixel rows so pattern logic can poke
        // through.
        int patternKind = Math.Abs(_holeIdx) % 3;   // 0 solid, 1 stripe, 2 checker
        for (int yy = fyTop; yy <= fyBot; yy++)
        {
            float u = (yy - fyTop) / (float)(fyBot - fyTop);
            int triEdge = fx + (int)(16 * (1f - MathF.Abs(2f * u - 1f)));
            for (int xx = fx; xx <= triEdge; xx++)
            {
                Color px;
                if (patternKind == 1 && (yy == midY || yy == midY - 1)) px = stripe;
                else if (patternKind == 2 && (((xx + yy) >> 1) & 1) == 0) px = stripe;
                else px = body;
                Raylib.DrawPixel(xx, yy, px);
            }
        }
        // Pole top finial.
        Raylib.DrawPixel(cx, poleTopY - 1, new Color((byte)244, (byte)220, (byte)92, (byte)255));
        Raylib.DrawPixel(cx + 1, poleTopY - 1, new Color((byte)244, (byte)220, (byte)92, (byte)255));
        // Tip dot to define the flag's outermost point.
        _ = fxTip;
    }

    /// <summary>
    /// Renders <paramref name="text"/> centered at (cx, baselineY) with
    /// each character bobbing on a sine wave staggered by index — like
    /// "doing the wave". Letters cycle through warm celebration colors.
    /// </summary>
    private static void DrawWaveText(string text, int cx, int baselineY, int size, float time)
    {
        int totalW = RetroSkin.MeasureText(text, size);
        int x = cx - totalW / 2;
        var palette = new[]
        {
            new Color((byte)255, (byte)220, (byte) 80, (byte)255),
            new Color((byte)255, (byte)180, (byte) 80, (byte)255),
            new Color((byte)255, (byte)140, (byte) 80, (byte)255),
            new Color((byte)255, (byte)200, (byte)120, (byte)255),
            new Color((byte)255, (byte)240, (byte)140, (byte)255),
        };
        for (int i = 0; i < text.Length; i++)
        {
            string ch = text[i].ToString();
            int chW = RetroSkin.MeasureText(ch, size);
            float bob = MathF.Sin(time * 6f + i * 0.55f) * 8f;
            var col = palette[i % palette.Length];
            // Drop shadow for readability against any terrain.
            RetroSkin.DrawText(ch, x + 1, baselineY + (int)bob + 1,
                new Color((byte)0, (byte)0, (byte)0, (byte)200), size);
            RetroSkin.DrawText(ch, x, baselineY + (int)bob, col, size);
            x += chW;
        }
    }

    private static void DrawAimLine(Vector2 ballAbs, Vector2 dir)
    {
        // Aim "line" is actually a gentle arc: rises a touch above the
        // straight path, then droops a hair at the far end. Subtle —
        // the arrow at the tip still reads as a directional indicator.
        var nrm = Vector2.Normalize(dir);
        var perp = new Vector2(-nrm.Y, nrm.X);
        float length = MathF.Min(dir.Length(), 110f);

        // Arc shape: f(t) lifts above the baseline (negative Y is up on
        // screen), peaks ~mid, eases back, then droops slightly past the
        // end. Peak rise ≈ 6 px, droop ≈ 3 px.
        const int segs = 24;
        const float peakRise = 1.5f;
        const float endDroop = 1f;
        Vector2 prev = ballAbs;
        Vector2 last = ballAbs;
        Vector2 lastDir = nrm;
        var col = new Color((byte)255, (byte)255, (byte)255, (byte)220);
        for (int i = 1; i <= segs; i++)
        {
            float t = i / (float)segs;
            // Rise: parabola peaking at t=0.5; Droop: cubic kicking in
            // only in the last quarter.
            float rise = -peakRise * 4f * t * (1f - t);
            float droopT = MathF.Max(0f, (t - 0.65f) / 0.35f);
            float droop = endDroop * droopT * droopT;
            float along = length * t;
            Vector2 p = ballAbs + nrm * along + perp * (rise + droop);
            Raylib.DrawLineEx(prev, p, 2f, col);
            lastDir = Vector2.Normalize(p - prev);
            last = p;
            prev = p;
        }

        // Arrow head: two stroked diagonals forming a > chevron at the
        // tip. Built from DrawLineEx (not DrawTriangle, which has been
        // silently invisible at this winding for some reason on macOS).
        var aPerp = new Vector2(-lastDir.Y, lastDir.X);
        Vector2 tip = last + lastDir * 6f;
        Vector2 leftBack  = last - lastDir * 3f + aPerp * 7f;
        Vector2 rightBack = last - lastDir * 3f - aPerp * 7f;
        Raylib.DrawLineEx(tip, leftBack,  2.5f, col);
        Raylib.DrawLineEx(tip, rightBack, 2.5f, col);
    }

    private void DrawNaiveArc(Vector2 canvasOrigin, Vector2 worldDir, float power, HeightField hf)
    {
        // Ghost path that ignores trees / walls / water / cup — just slope +
        // friction. Drawn in cool cyan so it reads as a "what if there were
        // nothing in the way" reference next to the accurate gold arc.
        var startVel = Vector2.Normalize(worldDir) * power;
        var path = SimulatePathNaive(_ball, startVel);
        if (path.Count < 2) return;

        var ghostBody = new Color((byte)120, (byte)200, (byte)255, (byte)255);
        Vector2? prev = null;
        for (int i = 0; i < path.Count; i++)
        {
            var sp = canvasOrigin + ProjectToScreen(path[i], hf);
            float t = i / (float)Math.Max(1, path.Count - 1);
            byte alpha = (byte)Math.Clamp(170 - t * 80, 50, 170);
            var col = new Color(ghostBody.R, ghostBody.G, ghostBody.B, alpha);
            if (prev.HasValue && Vector2.Distance(prev.Value, sp) > 1.4f)
                Raylib.DrawLineEx(prev.Value, sp, 1.2f, col);
            Raylib.DrawCircleV(sp, 1.3f, col);
            prev = sp;
        }

        // Smaller, slower-pulsing ring at the naive landing spot.
        var endScreen = canvasOrigin + ProjectToScreen(path[^1], hf);
        float t2 = (float)Raylib.GetTime();
        float pulse = 1f + 0.22f * MathF.Sin(t2 * 3f);
        Raylib.DrawCircleLines((int)endScreen.X, (int)endScreen.Y, 6f * pulse,
            new Color(ghostBody.R, ghostBody.G, ghostBody.B, (byte)200));
    }

    private void DrawAimArc(Vector2 canvasOrigin, Vector2 worldDir, float power, HeightField hf)
    {
        // Run the *actual* ball physics forward (trees, walls, water, hole)
        // so the predicted dotted trail tracks exactly where the ball will
        // go if released right now. End-state colour reflects the outcome:
        // gold = settled, blue = holed, red = drowned.
        var startVel = Vector2.Normalize(worldDir) * power;
        var (path, end) = SimulatePath(_ball, startVel);
        if (path.Count < 2) return;

        // Trail tracer
        Vector2? prev = null;
        for (int i = 0; i < path.Count; i++)
        {
            var sp = canvasOrigin + ProjectToScreen(path[i], hf);
            float t = i / (float)Math.Max(1, path.Count - 1);
            byte alpha = (byte)Math.Clamp(255 - t * 100, 60, 255);
            if (prev.HasValue && Vector2.Distance(prev.Value, sp) > 1.4f)
                Raylib.DrawLineEx(prev.Value, sp, 1.5f,
                    new Color((byte)255, (byte)255, (byte)200, alpha));
            Raylib.DrawCircleV(sp, 1.6f, new Color((byte)255, (byte)240, (byte)160, alpha));
            prev = sp;
        }

        // Pulsing target ring at the landing spot, colored by outcome.
        var endScreen = canvasOrigin + ProjectToScreen(path[^1], hf);
        float t2 = (float)Raylib.GetTime();
        float pulse = 1f + 0.35f * MathF.Sin(t2 * 4f);
        float baseR = 8f;
        Color ringCol = end switch
        {
            BallStep.Holed   => new Color((byte) 80, (byte)220, (byte)120, (byte)230),
            BallStep.Drowned => new Color((byte)220, (byte) 80, (byte) 80, (byte)230),
            _                => new Color((byte)255, (byte)200, (byte) 80, (byte)220),
        };
        Color innerCol = new(ringCol.R, ringCol.G, ringCol.B, (byte)180);
        Raylib.DrawCircleLines((int)endScreen.X, (int)endScreen.Y, baseR * pulse, ringCol);
        Raylib.DrawCircleLines((int)endScreen.X, (int)endScreen.Y, (baseR * 0.55f) * pulse, innerCol);
        Raylib.DrawCircleV(endScreen, 2f, new Color((byte)255, (byte)240, (byte)180, (byte)255));
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

    /// <summary>
    /// Below the scorecard, paint the current MeshPalette as a strip of
    /// 16 swatches with their hex codes underneath, so the user can
    /// screenshot the current colors and reference them when picking.
    /// </summary>
    private void DrawPaletteSwatch(Vector2 panelOffset)
    {
        var palette = MeshPalette;
        string paletteName = MeshPalettes[_meshPaletteIdx].Name;
        float sx = panelOffset.X + FrameInset + Margin;
        // Sit just under the canvas, above the status bar.
        float sy = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight
                 + RetroWidgets.MenuBarHeight + CanvasH + 4;
        int width = CanvasW;
        int rowH = 26;

        var rect = new Rectangle(sx, sy, width, rowH + 14);
        RetroSkin.DrawSunken(rect, RetroSkin.Face);

        // Label.
        RetroSkin.DrawText($"Palette: {paletteName}",
            (int)sx + 4, (int)sy + 1, RetroSkin.BodyText, 11);

        // Swatches across the strip, each labeled with its hex code.
        int n = palette.Length;
        int innerX = (int)sx + 4;
        int innerY = (int)sy + 14;
        int swatchW = (width - 8) / n;
        for (int i = 0; i < n; i++)
        {
            int x = innerX + i * swatchW;
            Raylib.DrawRectangle(x, innerY, swatchW - 1, 12, palette[i]);
            string hex = $"{palette[i].R:X2}{palette[i].G:X2}{palette[i].B:X2}";
            RetroSkin.DrawText(hex, x, innerY + 12,
                RetroSkin.BodyText, 9);
        }
    }
}
