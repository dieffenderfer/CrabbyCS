using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// World Tee Classic - top-down… ish. The course is a 9-hole heightmap rendered as a
/// 2.5D oblique view: pixel columns are voxel-sampled far→near, projected
/// onto the screen via a configurable pitch (tilt) angle, painted in
/// Bayer-dithered elevation bands with normal·light shading on top. The ball
/// rolls on the actual heightmap, and every game object (tee, cup, trees,
/// hazards, trail, aim line) gets projected through the same camera so the
/// world stays consistent under the angled view.
/// </summary>
public class WorldTeeClassicActivity : IActivity
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
    // TeePlan is the planner's shortest stroke path from Tee to Cup,
    // computed once in MakePlayableHole and reused as the supercombo
    // arrows when the player first lines up the hole. null until the
    // planner has run for this layout.
    //
    // Reach is the per-cell R₁ table: Reach[i] = bitmap of grid cells
    // reachable in ONE stroke from cell i (treating cell i's center as
    // the launch point). null entries are uncomputed; the planner
    // populates lazily and the layout's reach cache is shared across
    // every plan call so mid-hole supercombo replans are bitmap unions
    // instead of full physics BFS. CanHole[i] flags cells from which
    // a single stroke can drop directly into the cup.
    private record class HoleLayout(
        Vector2 Tee,
        Vector2 Cup,
        int Par,
        List<Vector2> Trees,
        List<(Vector2 Center, float Rx, float Ry, int Kind)> Hazards,
        HeightField Heightmap,
        List<Vector2>? TeePlan = null)
    {
        // Synchronization for the lazy reach computation — a background
        // pre-planner thread and the main thread can both call PlanShots
        // for the same layout, so writes need to be serialized per-cell.
        public readonly object ReachLock = new();
        public readonly ulong[]?[] Reach = new ulong[ReachGw * ReachGh][];
        public readonly bool[] CanHole = new bool[ReachGw * ReachGh];
        public readonly bool[] ReachComputed = new bool[ReachGw * ReachGh];
    }

    private List<HoleLayout> _course = new();
    private int[] _strokes = new int[Holes];
    private int _holeIdx;
    private Vector2 _ball;       // .X = worldX, .Y = worldZ (depth)
    private Vector2 _vel;
    private bool _aiming;
    /// <summary>True when the ball is drifting slowly enough to be re-hit AND
    /// the cursor is hovering inside the ball's hit radius. Drives the
    /// pulsing "ready" ring drawn around the ball.</summary>
    private bool _ballReadyHover;
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

    // Splash ripples spawned when the ball lands in water. Each ripple is a
    // world-space position + age in seconds; they fade out as expanding
    // dithered rings independently of where the ball is now (the ball gets
    // teleported back to the tee on the same frame, so the ripple is the
    // only post-mortem visual cue of the splash location).
    private List<(Vector2 Pos, float Age)> _ripples = new();
    private const float RippleLife = 0.9f;
    private readonly Random _rng = new();

    // Camera tilt (pitch). 0 = top-down. 35° default. Right-click+drag in
    // the scene rotates it live.
    private float _tiltDeg = 35f;
    private float _cosT = MathF.Cos(35f * MathF.PI / 180f);
    private float _sinT = MathF.Sin(35f * MathF.PI / 180f);
    private bool _tiltDragging;
    private float _tiltDragLastY;

    // Aim mode: Combo draws both arc + line plus red planner arrows
    // (supercombo); Line (default) is just the line and arrow; Arc is the
    // dotted trajectory + pulsing landing target.
    private enum AimStyle { Combo, Line, Arc }
    private AimStyle _aimStyle = AimStyle.Line;

    // Single cached mesh texture for the current (hole, tilt). Invalidated
    // on hole / tilt change. While the user is right-dragging tilt we skip
    // the cache and re-render the mesh live (sparser) each frame.
    private Texture2D _activeTex;
    private bool _activeTexBuilt;
    private int _activeTexHole = -1;

    // Planner cache for "supercombo" mode: shortest stroke path from the
    // current ball position to the cup. Stops[0] is always the start
    // position, Stops[^1] is the cup, intermediate points are the ball's
    // settle position after each planned shot. Recomputed when the ball
    // settles in a new spot or the hole changes.
    private List<Vector2> _planStops = new();
    private Vector2 _planFromPos;
    private int _planForHole = -1;
    private bool _planDirty = true;

    private readonly RetroHelp _help = new()
    {
        Title = "World Tee Classic - How to play",
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
        // Restore the persisted palette + aim choice. The save file was
        // renamed from fuji_golf.json to world_tee_classic.json; if a user
        // is upgrading from the old game name, fall back to the legacy file
        // exactly once so they don't lose their preferences. Save() always
        // writes the new name from now on, and the legacy file is left in
        // place (harmless and recoverable).
        const string SaveFile = "world_tee_classic.json";
        const string LegacySaveFile = "fuji_golf.json";
        var newPath = Path.Combine(MouseHouse.Core.SaveManager.SaveDirectory, SaveFile);
        var legacyPath = Path.Combine(MouseHouse.Core.SaveManager.SaveDirectory, LegacySaveFile);
        var sourceFile = File.Exists(newPath) ? SaveFile
                       : File.Exists(legacyPath) ? LegacySaveFile
                       : SaveFile;
        var s = MouseHouse.Core.SaveManager.LoadOrDefault<WorldTeeClassicPrefs>(sourceFile);
        if (string.IsNullOrEmpty(s.AimStyle))
            s = new WorldTeeClassicPrefs { AimStyle = AimStyle.Line.ToString() };
        _meshPaletteIdx = Math.Clamp(s.PaletteIdx, 0, MeshPalettes.Length - 1);
        _skyPaletteIdx = Math.Clamp(s.SkyIdx, 0, SkyPalettes.Length - 1);
        _bgPaletteIdx = Math.Clamp(s.BgIdx, 0, BgPalettes.Length - 1);
        _aimStyle = Enum.TryParse<AimStyle>(s.AimStyle, out var st) ? st : AimStyle.Line;
        _difficulty = Enum.TryParse<Difficulty>(s.Difficulty, out var d) ? d : Difficulty.Medium;
        TryLoadTreeSprite();
        TryLoadSwingSound();
        // The user picks a region on the dithered globe before any course
        // is generated. StartRound runs only after they pick (or skip via
        // the menu later) — this is the spec'd 'start screen' for World
        // Tee Classic. Globe size leaves room for the title and the hint
        // line above/below.
        _picker = new Globe.GlobePicker(CanvasW, CanvasH);
        _state = AppState.Picking;
    }

    // User-replaceable tree sprite. If `assets/golf/tree.png` exists, it
    // replaces the procedural triangle/circle tree at draw time. Drawn
    // anchored at the base center, scaled to fit a 28×40 box so any
    // reasonable pixel-art image will look right.
    private Texture2D _treeTex;
    private bool _treeTexLoaded;

    private Sound _swingSound;
    private bool _swingSoundLoaded;
    private Sound _sinkSound;
    private bool _sinkSoundLoaded;
    private Sound _clapSound;
    private bool _clapSoundLoaded;

    private void TryLoadSwingSound()
    {
        if (!_swingSoundLoaded)
        {
            var path = ResolveAssetPath("golf/sounds/golfswing.wav");
            if (path != null)
            {
                _swingSound = Raylib.LoadSound(path);
                _swingSoundLoaded = true;
            }
        }
        if (!_sinkSoundLoaded)
        {
            var path = ResolveAssetPath("golf/sounds/sinking_golf_ball.wav");
            if (path != null)
            {
                _sinkSound = Raylib.LoadSound(path);
                _sinkSoundLoaded = true;
            }
        }
        if (!_clapSoundLoaded)
        {
            var path = ResolveAssetPath("golf/sounds/golf_clap.wav");
            if (path != null)
            {
                _clapSound = Raylib.LoadSound(path);
                _clapSoundLoaded = true;
            }
        }
    }

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

    public enum Difficulty { Easy, Medium, Hard, Expert, Master, Legendary, Ohio }
    private Difficulty _difficulty = Difficulty.Medium;

    /// <summary>Activity-level mode: the player picks a region on the dithered
    /// spinning globe, then transitions to the actual round. The picker maps
    /// each region to a difficulty (continent → exotic → harder courses).</summary>
    private enum AppState { Picking, Playing }
    private AppState _state = AppState.Picking;
    private Globe.GlobePicker? _picker;
    private Globe.Region? _activeRegion;
    /// <summary>Per-hole bear swarms, indexed parallel to _course. Empty
    /// for non-Ohio rounds. Populated lazily when the player advances to
    /// each Ohio hole so spawn positions match the (possibly re-planned)
    /// hole layout. Mama Bear shows up on the final Ohio hole.</summary>
    private readonly List<Globe.BearSwarm> _bearSwarms = new();
    private string _ohioToast = "";
    private float _ohioToastTimer;
    /// <summary>Difficulty queued by the menu for the *next* StartRound. Lets
    /// the user cycle Difficulty mid-round without yanking them onto a freshly
    /// generated hole 1 — the active round keeps its courses; the new
    /// difficulty takes effect next time a New Round is started.</summary>
    private Difficulty? _pendingDifficulty;

    /// <summary>
    /// Target planner-stroke range per difficulty. The hole generator
    /// keeps regenerating layouts until the planner's best path falls
    /// inside this range. Final par = strokes + ParSlack, so Easy's
    /// (1,1) yields par 3 across the whole course, Expert's (5,6)
    /// yields par 7-8 holes that take real planning to solve.
    /// </summary>
    private (int Min, int Max) CurrentStrokeRange() => _difficulty switch
    {
        Difficulty.Easy      => (1, 1),   // par 3
        Difficulty.Medium    => (2, 3),   // par 4-5
        Difficulty.Hard      => (4, 5),   // par 6-7
        Difficulty.Expert    => (5, 6),   // par 7-8
        Difficulty.Master    => (6, 7),   // par 8-9
        Difficulty.Legendary => (7, 8),   // par 9-10
        Difficulty.Ohio      => (8, 9),   // par 10-11 — bear-infested
        _ => (2, 3),
    };

    /// <summary>
    /// How many extra obstacles (trees / hazards) to seed into the raw
    /// hole layout to bias the planner toward longer stroke counts.
    /// Without this, Hard/Expert never finds high-stroke layouts on the
    /// first few holes (which generate as low-density "par 3" templates).
    /// </summary>
    private int CurrentDensityBoost() => _difficulty switch
    {
        Difficulty.Easy      => -1,
        Difficulty.Medium    => 0,
        Difficulty.Hard      => 2,
        Difficulty.Expert    => 3,
        Difficulty.Master    => 4,
        Difficulty.Legendary => 5,
        Difficulty.Ohio      => 6,
        _ => 0,
    };

    private class WorldTeeClassicPrefs
    {
        // Defaults match the in-game labels Greens / Dawn / Forest. AimStyle
        // is nullable so old saves (no field) parse to null and the loader
        // can detect "no preference yet" → apply current defaults.
        public int PaletteIdx { get; set; } = 1;
        public int SkyIdx { get; set; } = 1;
        public int BgIdx { get; set; } = 1;
        public string? AimStyle { get; set; }
        // Nullable so old saves (no Difficulty field) parse to null and
        // the loader can apply the current default rather than Easy.
        public string? Difficulty { get; set; }
    }

    private string[] MenuLabels() => new[]
    {
        "New Round", "Replay Hole", "Skip",
        _pendingDifficulty.HasValue && _pendingDifficulty.Value != _difficulty
            ? $"Difficulty: {_pendingDifficulty} (next round)"
            : $"Difficulty: {_difficulty}",
        $"Aim: {_aimStyle}",
        $"Palette: {MeshPalettes[_meshPaletteIdx].Name}",
        $"Sky: {SkyPalettes[_skyPaletteIdx].Name}",
        $"Background: {BgPalettes[_bgPaletteIdx].Name}",
        "Help",
    };

    private void SaveWorldTeePrefs() =>
        MouseHouse.Core.SaveManager.Save("world_tee_classic.json",
            new WorldTeeClassicPrefs
            {
                PaletteIdx = _meshPaletteIdx,
                SkyIdx = _skyPaletteIdx,
                BgIdx = _bgPaletteIdx,
                AimStyle = _aimStyle.ToString(),
                // Persist the queued difficulty if there is one so a
                // quit-and-reopen treats the pending pick as the real
                // choice for the next round. On reopen, _difficulty is
                // initialised from this value before the first StartRound.
                Difficulty = (_pendingDifficulty ?? _difficulty).ToString(),
            });

    /// <summary>
    /// Region → Difficulty mapping. The picker's region order doubles as
    /// 'how exotic / how hard'.
    /// </summary>
    private static Difficulty DifficultyForRegion(Globe.Region r) => r.Name switch
    {
        "North America" => Difficulty.Easy,
        "Europe"        => Difficulty.Medium,
        "Asia"          => Difficulty.Hard,
        "South America" => Difficulty.Expert,
        "Australia"     => Difficulty.Master,
        "Africa"        => Difficulty.Legendary,
        "Ohio"          => Difficulty.Ohio,
        _               => Difficulty.Medium,
    };

    public void Close()
    {
        UnloadTerrainTextures();
        _picker?.Unload();
        _picker = null;
        foreach (var bs in _bearSwarms) bs.Clear();
        _bearSwarms.Clear();
        if (_treeTexLoaded)
        {
            Raylib.UnloadTexture(_treeTex);
            _treeTexLoaded = false;
        }
        if (_swingSoundLoaded)
        {
            Raylib.UnloadSound(_swingSound);
            _swingSoundLoaded = false;
        }
        if (_sinkSoundLoaded)
        {
            Raylib.UnloadSound(_sinkSound);
            _sinkSoundLoaded = false;
        }
        if (_clapSoundLoaded)
        {
            Raylib.UnloadSound(_clapSound);
            _clapSoundLoaded = false;
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
        // Apply any difficulty change that was queued mid-round so the
        // new round's holes use the new par cap.
        if (_pendingDifficulty.HasValue)
        {
            _difficulty = _pendingDifficulty.Value;
            _pendingDifficulty = null;
        }
        UnloadTerrainTextures();
        // Bump the generation-version: any in-flight background planner
        // from a previous round bails as soon as it sees the mismatch,
        // so its writes never race with the new round's state.
        int planVersion = System.Threading.Interlocked.Increment(ref _genVersion);

        // Pre-fill the slots with raw (un-planned) layouts. Reads of
        // _course[i] from either thread are then atomic-reference-safe;
        // the background planner publishes refined layouts by replacing
        // entries in place rather than appending. Also gives the live
        // ball physics a real Heightmap/Tee/Cup for every hole even
        // before the planner has finished — so the player isn't gated
        // on planning to start playing.
        _course.Clear();
        // Ohio courses get bear swarms — one BearSwarm slot per hole, lazily
        // populated when the player reaches that hole so positions match
        // the post-planner layout. Wipe any from a previous round.
        foreach (var bs in _bearSwarms) bs.Clear();
        _bearSwarms.Clear();
        bool ohio = _difficulty == Difficulty.Ohio;
        var rng = new Random();
        int densityBoost = CurrentDensityBoost();
        var range = CurrentStrokeRange();
        var raw = new HoleLayout[Holes];
        for (int i = 0; i < Holes; i++)
        {
            raw[i] = GenerateHole(i, rng, densityBoost);
            _course.Add(raw[i]);
            _bearSwarms.Add(ohio ? new Globe.BearSwarm() : new Globe.BearSwarm());
        }
        Array.Clear(_holeReady, 0, _holeReady.Length);

        _strokes = new int[Holes];
        _holeIdx = 0;
        _holeComplete = false;
        _roundComplete = false;
        _trail.Clear();
        _planDirty = true;
        ResetBall();

        // Background pre-planner: ALL holes are planned async, including
        // hole 0. The activity panel opens immediately; arrows fade in
        // hole-by-hole as each plan publishes. Each hole is its own
        // Task.Run so they all run concurrently on the threadpool — on
        // an 8-core machine the entire 9-hole round plans in roughly the
        // wall time of a single hole, vs. 9× that wall time when run
        // serially in one task.
        //
        // Per-hole work is hole-parameterized and touches no shared
        // state across holes, so concurrent planning is safe. Reach-cache
        // writes within a single layout serialize via the layout's
        // ReachLock. _genVersion is checked twice (before publish AND
        // before the work) so a restarted round bails out promptly.
        for (int i = 0; i < Holes; i++)
        {
            int holeIdx = i;
            System.Threading.Tasks.Task.Run(() =>
            {
                if (System.Threading.Volatile.Read(ref _genVersion) != planVersion) return;
                var bgRng = new Random();
                var planned = MakePlayableHole(holeIdx, raw[holeIdx], bgRng, range, densityBoost);
                if (System.Threading.Volatile.Read(ref _genVersion) != planVersion) return;
                _course[holeIdx] = planned;
                System.Threading.Volatile.Write(ref _holeReady[holeIdx], true);
            });
        }
    }

    // Background-planner state.
    // _genVersion bumps once per StartRound; the worker checks it to
    // bail out if the round was restarted while it was running.
    // _holeReady[i] flips true when the worker has published the
    // planned layout for slot i — currently advisory (the player can
    // still play with the un-planned raw layout, just no supercombo
    // arrows until the plan lands).
    private int _genVersion;
    private readonly bool[] _holeReady = new bool[Holes];

    // Hard ceiling on planner search depth used for the mid-hole replan
    // in supercombo aim mode. Round-generation uses the difficulty-driven
    // cap from CurrentPlanCap() instead — this constant only governs the
    // depth of the plan-arrows BFS once the player is in motion.
    private const int PlanStrokesCap = 6;
    // Forgiveness buffer added on top of the planner's optimal stroke
    // count. The planner plays pixel-perfect; real players can't, so
    // par reads as "AI strokes + 2" to keep birdies achievable.
    private const int ParSlack = 2;
    // Hard ceiling on regeneration tries before we give up and accept
    // whatever we have (or fall back to a conservative par).
    private const int MaxHoleAttempts = 14;

    /// <summary>
    /// Generate a hole and verify a path to the cup actually exists in a
    /// reasonable number of strokes. Regenerates up to <see cref="MaxHoleAttempts"/>
    /// times if the planner can't beat <see cref="PlanStrokesCap"/>; the
    /// returned par is the planner's stroke count + <see cref="ParSlack"/>
    /// since real players can't land pixel-perfect — par reflects "what an
    /// optimal AI does" plus a forgiveness buffer. Also caches the planner's
    /// path on the layout (TeePlan) so the supercombo arrows can be
    /// rendered instantly on hole entry without a fresh BFS.
    /// </summary>
    private static HoleLayout MakePlayableHole(int idx, HoleLayout firstCandidate, Random rng,
                                               (int Min, int Max) range, int densityBoost)
    {
        // The PLANNER always searches up to PlanStrokesCap so we have a
        // real TeePlan even when the difficulty range is narrow — the
        // supercombo arrows render off this regardless. We ACCEPT any
        // layout whose planner-stroke count falls inside `range`; if no
        // attempt lands in-range we ship the closest-by-distance fallback
        // so the round still has playable holes (and visible arrows).
        HoleLayout? best = null;
        int bestDist = int.MaxValue;
        for (int attempt = 0; attempt < MaxHoleAttempts; attempt++)
        {
            // attempt==0 gets the pre-built `firstCandidate` so we don't
            // throw away the work the caller already did. All later
            // attempts re-generate with the difficulty's density boost
            // baked in (more trees/hazards on Hard/Expert).
            var candidate = attempt == 0
                ? firstCandidate
                : GenerateHole(idx, rng, densityBoost);
            var planPath = PlanShots(candidate.Tee, PlanStrokesCap, candidate);
            if (planPath.Count < 2) continue;

            int strokes = planPath.Count - 1;
            int dist = strokes < range.Min ? range.Min - strokes
                     : strokes > range.Max ? strokes - range.Max
                     : 0;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = candidate with { Par = strokes + ParSlack, TeePlan = planPath };
                if (dist == 0) break;     // perfect hit — accept
            }
        }
        return best ?? (firstCandidate with { Par = PlanStrokesCap + ParSlack });
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
        _planDirty = true;
        _planStops.Clear();
    }

    private static HoleLayout GenerateHole(int idx, Random rng, int densityBoost = 0)
    {
        // Provisional par used only for hole-density tuning (number of
        // trees/hazards). The real Par is overwritten by MakePlayableHole
        // after the planner runs. densityBoost shifts the obstacle count
        // up/down per difficulty: Hard/Expert add trees+hazards so the
        // planner finds high-stroke layouts; Easy subtracts so the
        // planner finds 1-stroke layouts.
        int par = idx switch { 0 => 3, 1 => 3, 2 => 4, 3 => 4, 4 => 4, 5 => 5, 6 => 4, 7 => 5, _ => 5 };
        par = Math.Clamp(par + densityBoost, 2, 9);

        var tee = new Vector2(40, 60 + rng.Next(CanvasH - 120));
        var cup = new Vector2(CanvasW - 40, 60 + rng.Next(CanvasH - 120));

        var trees = new List<Vector2>();
        int treeCount = 2 + rng.Next(par);
        int safety = 0;
        while (trees.Count < treeCount && safety++ < 200)
        {
            var t = new Vector2(
                100 + rng.Next(CanvasW - 200),
                30 + rng.Next(CanvasH - 60));
            if (Vector2.Distance(t, tee) < 50) continue;
            if (Vector2.Distance(t, cup) < 50) continue;
            bool overlap = false;
            foreach (var u in trees) if (Vector2.Distance(t, u) < 28) { overlap = true; break; }
            if (overlap) continue;
            trees.Add(t);
        }

        var hazards = new List<(Vector2, float, float, int)>();
        int hzCount = par <= 3 ? 1 : par <= 5 ? 2 : 3;
        for (int h = 0; h < hzCount; h++)
        {
            int kind = rng.Next(2);
            var center = new Vector2(
                CanvasW / 4f + rng.Next(CanvasW / 2),
                40 + rng.Next(CanvasH - 80));
            if (Vector2.Distance(center, tee) < 60) center.X += 80;
            if (Vector2.Distance(center, cup) < 60) center.X -= 80;
            float rx = 24 + rng.Next(20);
            float ry = 14 + rng.Next(14);
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
            float cx = (float)rng.NextDouble() * cols;
            float cy = (float)rng.NextDouble() * rows;
            float amp = ((float)rng.NextDouble() * 2 - 1) * maxAmp;
            float radius = minRadius + (float)rng.NextDouble() * (maxRadius - minRadius);
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

        // Region picker mode: only the title-bar close X and the globe
        // itself are interactive. Once the picker fires Picked, map the
        // chosen region to a Difficulty and start the round.
        if (_state == AppState.Picking && _picker != null)
        {
            var titleBarPick = new Rectangle(FrameInset, FrameInset,
                PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
            if (RetroWidgets.DrawTitleBarHitTest(titleBarPick, local, leftPressed))
            { IsFinished = true; return; }

            var hostPick = new Rectangle(panelOffset.X + FrameInset,
                panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight,
                CanvasW, CanvasH);
            bool leftHeld = Raylib.IsMouseButtonDown(MouseButton.Left);
            _picker.Update(delta, mousePos, hostPick, leftPressed, leftReleased, leftHeld);

            if (_picker.Picked && _picker.PickedRegion != null)
            {
                _activeRegion = _picker.PickedRegion;
                _difficulty = DifficultyForRegion(_activeRegion);
                _state = AppState.Playing;
                StartRound();
            }
            return;
        }

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
                // Cycle difficulty for the *next* round. Critical: never
                // regenerate the active course mid-play — that would yank
                // the player onto a fresh hole 1 with new geometry,
                // erasing whatever shot they were lining up. The active
                // round keeps its difficulty until the user explicitly
                // chooses 'New Round' (case 0). _difficulty is only read
                // inside StartRound, so a deferred change is sufficient.
                var cur = _pendingDifficulty ?? _difficulty;
                var next = (Difficulty)(((int)cur + 1) % 4);
                _pendingDifficulty = next == _difficulty ? null : next;
                SaveWorldTeePrefs();
                return;
            case 4:
                _aimStyle = (AimStyle)(((int)_aimStyle + 1) % 3);
                _planDirty = true;
                SaveWorldTeePrefs();
                return;
            case 5:
                _meshPaletteIdx = (_meshPaletteIdx + 1) % MeshPalettes.Length;
                // Force the per-hole mesh texture to rebuild with the
                // new palette next frame, and persist the choice.
                UnloadTerrainTextures();
                SaveWorldTeePrefs();
                return;
            case 6:
                _skyPaletteIdx = (_skyPaletteIdx + 1) % SkyPalettes.Length;
                UnloadTerrainTextures();
                SaveWorldTeePrefs();
                return;
            case 7:
                _bgPaletteIdx = (_bgPaletteIdx + 1) % BgPalettes.Length;
                UnloadTerrainTextures();
                SaveWorldTeePrefs();
                return;
            case 8: _help.Visible = !_help.Visible; return;
        }
        if (_help.HandleInput(local, leftPressed, PanelSize)) return;

        if (_roundComplete) return;

        // Age water-splash ripples every frame regardless of aim/celebration
        // state so they keep expanding even while the player is lining up
        // the next shot from the tee.
        for (int i = _ripples.Count - 1; i >= 0; i--)
        {
            var (rp, ra) = _ripples[i];
            ra += delta;
            if (ra >= RippleLife) _ripples.RemoveAt(i);
            else _ripples[i] = (rp, ra);
        }

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
                _ripples.Add((_ball, 0f));
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
                // if (_sinkSoundLoaded) Raylib.PlaySound(_sinkSound);
                int strokes = _strokes[_holeIdx];
                int par = _course[_holeIdx].Par;
                int dpar = strokes - par;
                _celebrationText = strokes == 1 ? "HOLE IN ONE"
                    : dpar <= -4 ? "CONDOR"
                    : dpar == -3 ? "ALBATROSS"
                    : dpar == -2 ? "EAGLE"
                    : dpar == -1 ? "BIRDIE"
                    : dpar ==  0 ? "PAR"
                    : "";
                _celebrationTime = 0f;
                // Polite golf-crowd clap layered on top of the sink sound
                // when the player makes par or better. Bogey or worse:
                // the ball drop is its own punctuation.
                if (dpar <= 0 && _clapSoundLoaded) Raylib.PlaySound(_clapSound);
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
        // they see it. Re-hit is allowed only when the ball is drifting —
        // ≤ 10% of the player's max launch power. The previous 60-unit
        // threshold (~16% of max) let a clearly-still-rolling ball be
        // re-hit, which felt like a swing input was getting eaten.
        var ballScreen = ProjectToScreen(_ball, hf);
        const float RehitSpeedFrac = 0.10f;
        float maxRehitSpeed = PlayerMaxShotPower * RehitSpeedFrac;
        bool ballSlow = _vel.Length() < maxRehitSpeed;

        // Generous hitbox — clicking anywhere within ~28 px of the ball
        // starts an aim, since the ball sprite itself is tiny and a tight
        // hit-test feels finicky.
        const float BallHitRadius = 28f;
        bool cursorOverBall = (canvasMouse - ballScreen).LengthSquared() < BallHitRadius * BallHitRadius;
        _ballReadyHover = ballSlow && cursorOverBall && !_aiming && !_holeComplete && inCanvas;
        if (ballSlow && leftPressed && cursorOverBall)
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
            float power = Math.Min(worldDir.Length() * 4f, PlayerMaxShotPower);
            if (power < 12) return;
            _vel = Vector2.Normalize(worldDir) * power;
            _strokes[_holeIdx]++;
            _trail.Clear();
            if (_swingSoundLoaded) Raylib.PlaySound(_swingSound);
            // Plan is invalidated by the ball moving — clear arrows until
            // the ball settles and the planner replans from the new spot.
            _planStops.Clear();
            _planDirty = true;
        }

        UpdateBears(delta);
        if (_ohioToastTimer > 0) _ohioToastTimer = MathF.Max(0, _ohioToastTimer - delta);
    }

    /// <summary>
    /// Bear AI tick + ball-impact resolution. Only runs on Ohio rounds.
    /// First call per hole lazily populates the swarm so spawn positions
    /// match the planner-finalised tee/cup. Bears keep moving even while
    /// the player is aiming — that's the whole point.
    /// </summary>
    private void UpdateBears(float delta)
    {
        if (_difficulty != Difficulty.Ohio) return;
        if (_bearSwarms.Count <= _holeIdx) return;
        var swarm = _bearSwarms[_holeIdx];
        if (swarm.Bears.Count == 0)
        {
            var hole = _course[_holeIdx];
            swarm.PopulateOhioHole(_holeIdx, Holes, hole.Tee, hole.Cup,
                CanvasW, CanvasH, new Random(_holeIdx * 7919 + 17));
        }
        var hit = swarm.Update(delta, _ball, ref _vel, CanvasW, CanvasH);
        if (hit.Hit)
        {
            // Stroke penalty + on-screen toast. Don't apply during the
            // hole-complete celebration; bears can't grief a finished cup.
            if (!_holeComplete)
            {
                _strokes[_holeIdx] += hit.StrokePenalty;
                _ohioToast = hit.Message ?? "Bear!";
                _ohioToastTimer = 1.6f;
                _planStops.Clear();
                _planDirty = true;
            }
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

        // Picker mode draws its own self-contained chrome on top of the
        // window frame: title bar with close X, the dithered globe filling
        // the canvas area, and a status hint.
        if (_state == AppState.Picking && _picker != null)
        {
            var titleBarPick = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
                PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
            RetroWidgets.DrawTitleBarVisual(titleBarPick, "World Tee Classic", true);

            var hostPick = new Rectangle(panelOffset.X + FrameInset,
                panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight,
                CanvasW, CanvasH);
            // Dark mat behind the globe so the dithered ocean reads against
            // whatever the active retro theme's panel face is.
            Raylib.DrawRectangleRec(hostPick, new Color((byte)8, (byte)12, (byte)28, (byte)255));
            _picker.Draw(hostPick);

            var statusPick = new Rectangle(panelOffset.X + FrameInset,
                panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
                PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
            RetroWidgets.StatusBar(statusPick,
                "Each region maps to a difficulty - more exotic = harder courses",
                "Pick to begin");
            return;
        }

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        string regionTag = _activeRegion != null ? $" ({_activeRegion.Name})" : "";
        RetroWidgets.DrawTitleBarVisual(titleBar,
            $"World Tee Classic - Hole {_holeIdx + 1} of {Holes}{regionTag}", true);

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

    /// <summary>
    /// Refresh the cached supercombo plan if the ball is parked in a new
    /// spot. Skips when not in Combo aim mode (planning is expensive — a
    /// few hundred thousand physics ticks worst case) or while the ball
    /// is in flight, since any plan from a moving ball is stale.
    /// </summary>
    private void EnsurePlan()
    {
        if (_aimStyle != AimStyle.Combo
            || _holeComplete || _roundComplete
            || _vel.LengthSquared() > 0.01f)
        {
            _planStops.Clear();
            return;
        }
        bool needRecompute = _planDirty
            || _planForHole != _holeIdx
            || _planStops.Count == 0
            || Vector2.Distance(_planFromPos, _ball) > 6f;
        if (!needRecompute) return;
        var hole = _course[_holeIdx];
        bool atTee = Vector2.Distance(_ball, hole.Tee) <= 8f;
        if (atTee)
        {
            // Tee position: serve the precomputed plan if the background
            // pre-planner has finished this hole; otherwise hold off (no
            // arrows for a beat) so we don't pay for a synchronous BFS
            // on the render thread. The next frame will retry.
            if (hole.TeePlan != null && hole.TeePlan.Count >= 2)
            {
                _planStops = new List<Vector2>(hole.TeePlan);
                _planFromPos = _ball;
                _planForHole = _holeIdx;
                _planDirty = false;
            }
            else
            {
                _planStops.Clear();
            }
            return;
        }

        // Mid-hole replan from a non-tee position. The reach-region
        // planner reuses the per-cell R₁ cache built during round-load,
        // so this is a few bitmap unions plus (worst case) one R₁ build
        // for the cell the ball just settled in — typically sub-ms.
        _planStops = PlanShots(_ball, PlanStrokesCap, hole);
        _planFromPos = _ball;
        _planForHole = _holeIdx;
        _planDirty = false;
    }

    // Pixel radius of the dithered "safe landing zone" drawn at each
    // planned settle point. Sized so the cluster reads as a region the
    // player should aim into, not a precise pixel target — the planner
    // plays pixel-perfect, players don't.
    private const int PlanZoneRadius = 18;

    /// <summary>
    /// Draw the cached supercombo plan as red arrows pointing into
    /// dithered red "safe landing zones" — one zone per planned settle
    /// point (and a slightly larger one over the cup). Each arrow stops
    /// at the zone's edge, so the visual reads as "land somewhere in
    /// here" rather than "hit this exact pixel". Drawn under the ball
    /// so the ball stays visible.
    /// </summary>
    private void DrawPlanArrows(Vector2 canvasOrigin, HeightField hf)
    {
        // Arrows stay up while the player is aiming so the planned line
        // remains visible as a reference under their drag.
        if (_planStops.Count < 2) return;
        var arrowCol = new Color((byte)230, (byte)50, (byte)50, (byte)235);
        var zonePrimary   = new Color((byte)232, (byte) 64, (byte) 64, (byte)255);
        var zoneSecondary = new Color((byte)176, (byte) 28, (byte) 28, (byte)255);

        // Zones for every stop except the very first (the ball itself).
        // Cup gets a slightly larger zone since "landing near the cup"
        // is the win condition.
        for (int i = 1; i < _planStops.Count; i++)
        {
            var sp = canvasOrigin + ProjectToScreen(_planStops[i], hf);
            int rx = i == _planStops.Count - 1 ? PlanZoneRadius + 4 : PlanZoneRadius;
            int ry = Math.Max(5, (int)(rx * _cosT));
            DrawDitheredEllipse((int)sp.X, (int)sp.Y, rx, ry,
                zonePrimary, zoneSecondary);
        }

        // Arrows: from each stop's zone edge to the next stop's zone
        // edge. The shaft visually terminates *inside* the zone, which
        // is what sells the "aim somewhere in this region" read.
        for (int i = 0; i < _planStops.Count - 1; i++)
        {
            var a = canvasOrigin + ProjectToScreen(_planStops[i], hf);
            var b = canvasOrigin + ProjectToScreen(_planStops[i + 1], hf);
            var d = b - a;
            float len = d.Length();
            if (len < 8f) continue;
            var dir = d / len;
            // First stop is the ball — pull back by ball-radius. Later
            // stops are zones — pull back by zone-radius minus a few
            // pixels so the arrow head still penetrates the zone edge.
            float startInset = i == 0 ? 6f : (PlanZoneRadius - 4f);
            float endInset = (i == _planStops.Count - 2 ? PlanZoneRadius : PlanZoneRadius) - 4f;
            // Bail if the two zones overlap so much that there's no
            // useful arrow to draw between them.
            if (len <= startInset + endInset + 4f) continue;
            var start = a + dir * startInset;
            var end = b - dir * endInset;
            Raylib.DrawLineEx(start, end, 2.2f, arrowCol);
            // Chevron head — same diagonal-pair approach as DrawAimLine,
            // since DrawTriangle has been unreliable on macOS.
            var perp = new Vector2(-dir.Y, dir.X);
            Raylib.DrawLineEx(end, end - dir * 7f + perp * 5f, 2.5f, arrowCol);
            Raylib.DrawLineEx(end, end - dir * 7f - perp * 5f, 2.5f, arrowCol);
        }
    }

    private void DrawCourse(Vector2 canvasOrigin, Rectangle canvas)
    {
        var hole = _course[_holeIdx];
        var hf = hole.Heightmap;
        EnsurePlan();

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

        // Splash ripples: expanding two-tone dithered rings at the world
        // splash position, projected through the same camera so they squash
        // with the tilt and sit on the water surface. Two concentric rings
        // staggered in time read as a "ploop" instead of a single pop.
        foreach (var (pos, age) in _ripples)
        {
            var sp = ProjectToScreen(pos, hf);
            int rcx = (int)(canvasOrigin.X + sp.X);
            int rcy = (int)(canvasOrigin.Y + sp.Y);
            // Outer ring: grows 4 → 18 px over its lifetime.
            float t0 = age / RippleLife;
            // Inner ring: starts 0.25 s later so it lags behind the outer.
            float t1 = (age - 0.25f) / RippleLife;
            // Ring colors match the water hazard palette so the ripple
            // reads as displaced water, not a foreign overlay.
            var foam   = new Color((byte)200, (byte)224, (byte)248, (byte)255);
            var deeper = new Color((byte) 28, (byte) 72, (byte)160, (byte)255);
            if (t0 >= 0f && t0 < 1f)
            {
                int r = (int)(4f + 14f * t0);
                int yr = Math.Max(2, (int)(r * _cosT));
                float coverage = 1f - t0;                        // fade out
                DrawDitheredRing(rcx, rcy, r, yr, 2, foam, coverage);
            }
            if (t1 >= 0f && t1 < 1f)
            {
                int r = (int)(2f + 10f * t1);
                int yr = Math.Max(2, (int)(r * _cosT));
                float coverage = 1f - t1;
                DrawDitheredRing(rcx, rcy, r, yr, 1, deeper, coverage);
            }
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

        // Supercombo plan arrows — drawn after the aim preview so they
        // don't fight with it visually, but before the ball so the ball
        // sits on top. Hidden while aiming or mid-flight (EnsurePlan
        // clears _planStops in those cases).
        DrawPlanArrows(canvasOrigin, hf);

        // "Ready to hit" pulsing ring around the ball. Mirrors the same
        // gate as the click-to-aim handler so the visual cue can't
        // mislead the player into a swing the engine would reject.
        if (_ballReadyHover)
        {
            float t = (float)Raylib.GetTime();
            float pulse = 1f + 0.25f * MathF.Sin(t * 5f);
            int cx = (int)(canvasOrigin.X + ballScreen.X);
            int cy = (int)(canvasOrigin.Y + ballScreen.Y);
            Raylib.DrawCircleLines(cx, cy, 11f * pulse,
                new Color((byte)255, (byte)240, (byte)120, (byte)200));
            Raylib.DrawCircleLines(cx, cy, 14f * pulse,
                new Color((byte)255, (byte)240, (byte)120, (byte)110));
        }

        // Ohio bears — drawn before the ball so the ball stays visible
        // on top during a tackle. The swarm uses canvas-local coords.
        if (_difficulty == Difficulty.Ohio && _holeIdx < _bearSwarms.Count)
            _bearSwarms[_holeIdx].Draw(canvasOrigin);

        // Ball — drawn last so it sits on top.
        Raylib.DrawCircle((int)(canvasOrigin.X + ballScreen.X) + 1, (int)(canvasOrigin.Y + ballScreen.Y) + 1,
            5, new Color((byte)0, (byte)0, (byte)0, (byte)100));
        Raylib.DrawCircle((int)(canvasOrigin.X + ballScreen.X), (int)(canvasOrigin.Y + ballScreen.Y),
            5, new Color((byte)255, (byte)255, (byte)255, (byte)255));
        Raylib.DrawCircleLines((int)(canvasOrigin.X + ballScreen.X), (int)(canvasOrigin.Y + ballScreen.Y),
            5, RetroSkin.BodyText);

        // Ohio bear-attack toast — banner at top of canvas, fades over ~1.6s.
        if (_ohioToastTimer > 0 && !string.IsNullOrEmpty(_ohioToast))
        {
            byte alpha = (byte)(MathF.Min(1f, _ohioToastTimer / 0.4f) * 255);
            int tw = RetroSkin.MeasureText(_ohioToast, 18);
            int tx = (int)(canvas.X + (canvas.Width - tw) / 2);
            int ty = (int)(canvas.Y + 28);
            Raylib.DrawRectangle(tx - 8, ty - 4, tw + 16, 24,
                new Color((byte)80, (byte)20, (byte)20, alpha));
            RetroSkin.DrawText(_ohioToast, tx, ty,
                new Color((byte)255, (byte)220, (byte)80, alpha), 18);
        }

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
    /// hole detection, and per-terrain friction). Operates against the
    /// supplied <paramref name="hole"/> so background-thread planners can
    /// run without touching shared <c>_course[_holeIdx]</c> state.
    /// </summary>
    private static BallStep StepBall(ref Vector2 pos, ref Vector2 vel, float dt, HoleLayout hole)
    {
        var hf = hole.Heightmap;
        var grad = hf.Gradient(pos.X, pos.Y);
        var slopeAccel = -grad * GravStrength;
        vel += slopeAccel * dt;

        int t = TerrainAtFor(pos, hole);
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
        foreach (var tree in hole.Trees)
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
        if (TerrainAtFor(pos, hole) == 3) return BallStep.Drowned;

        // Holed
        if (Vector2.Distance(pos, hole.Cup) < 8 && vel.Length() < 90f)
            return BallStep.Holed;

        return BallStep.Moving;
    }

    /// <summary>Terrain at a given position against a specific hole — pure (no shared state).</summary>
    private static int TerrainAtFor(Vector2 p, HoleLayout hole)
    {
        if (p.X < 0 || p.Y < 0 || p.X >= CanvasW || p.Y >= CanvasH) return 1;
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
    private List<Vector2> SimulatePathNaive(Vector2 startPos, Vector2 startVel, HoleLayout hole, int maxSteps = 240)
    {
        var path = new List<Vector2>(maxSteps);
        var pos = startPos;
        var vel = startVel;
        const float dt = 1f / 60f;
        var hf = hole.Heightmap;

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

    // ── Stroke planner ──────────────────────────────────────────────────
    // Reach-region planner. For each cell on the planning grid we lazily
    // precompute R₁ — the bitmap of grid cells reachable from that cell's
    // center in ONE stroke — by sampling many (angle, power) shots and
    // recording each shot's settle cell. Once R₁ exists for a cell, the
    // question "where can I get from here in one stroke?" is a constant-
    // time bitmap lookup, and N-stroke planning is N successive bitmap
    // unions over the previous frontier. Mid-hole replans (every time the
    // ball settles in supercombo aim) thus stop being a multi-million-step
    // physics search and become microsecond bitmap math.
    //
    // The action set is much denser than what the old per-plan BFS could
    // afford (36 angles × 5 powers + 6 cup-aim vs. 24 × 5 + 5), because
    // the cost moves from "every plan call" to "once per cell, ever."
    // Player's hard cap on shot velocity at swing time (see launch site
    // in the aim-release branch). The planner MUST stay at-or-under this
    // — anything stronger and the red arrows would suggest shots the
    // player physically can't make.
    private const float PlayerMaxShotPower = 380f;
    private const int PlanCellSize = 14;
    // 24 × 4 grid samples + 5 cup-aim = 101 sims per cell. We tried denser
    // sampling (36 × 5 + 6 = 186) and the regenerate-and-test loop turned
    // a round-load into a multi-minute freeze. The reach cache amortizes
    // sufficient quality across mid-hole replans even at this density.
    private const int ReachAngleSteps = 24;
    private static readonly int ReachGw = (CanvasW + PlanCellSize - 1) / PlanCellSize;
    private static readonly int ReachGh = (CanvasH + PlanCellSize - 1) / PlanCellSize;
    private static readonly int ReachCellCount = ReachGw * ReachGh;
    private static readonly int ReachWords = (ReachCellCount + 63) / 64;
    private static readonly float[] ReachPowers = { 130f, 210f, 290f, PlayerMaxShotPower };
    private static readonly float[] ReachCupAimPowers = { 130f, 200f, 270f, 340f, PlayerMaxShotPower };

    private static int CellIdx(int gx, int gy) => gy * ReachGw + gx;
    private static (int gx, int gy) CellOf(Vector2 pos) =>
        (Math.Clamp((int)(pos.X / PlanCellSize), 0, ReachGw - 1),
         Math.Clamp((int)(pos.Y / PlanCellSize), 0, ReachGh - 1));
    private static Vector2 CellCenter(int gx, int gy) =>
        new(gx * PlanCellSize + PlanCellSize / 2f, gy * PlanCellSize + PlanCellSize / 2f);

    /// <summary>
    /// Compute (or fetch from cache) the R₁ bitmap and CanHole flag for
    /// a single grid cell. Idempotent and thread-safe — the layout's
    /// ReachLock serialises concurrent writers (the round-load pre-planner
    /// runs on a background task).
    /// </summary>
    private static void EnsureCellReach(HoleLayout hole, int gx, int gy)
    {
        int idx = CellIdx(gx, gy);
        if (System.Threading.Volatile.Read(ref hole.ReachComputed[idx])) return;
        lock (hole.ReachLock)
        {
            if (hole.ReachComputed[idx]) return;
            var bm = new ulong[ReachWords];
            bool canHole = false;
            var pos = CellCenter(gx, gy);

            // Cup-aimed shots — most likely to win the hole, so they go first.
            var cupOffset = hole.Cup - pos;
            float cupDist = cupOffset.Length();
            if (cupDist > 0.5f)
            {
                var cupDir = cupOffset / cupDist;
                foreach (var p in ReachCupAimPowers)
                    AccumulateShot(hole, pos, cupDir * p, bm, ref canHole);
            }

            // Uniform angle × power sweep covers everything else.
            for (int a = 0; a < ReachAngleSteps; a++)
            {
                float ang = a * (MathF.PI * 2f / ReachAngleSteps);
                var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
                foreach (var p in ReachPowers)
                    AccumulateShot(hole, pos, dir * p, bm, ref canHole);
            }

            hole.Reach[idx] = bm;
            hole.CanHole[idx] = canHole;
            System.Threading.Volatile.Write(ref hole.ReachComputed[idx], true);
        }
    }

    private static void AccumulateShot(HoleLayout hole, Vector2 pos, Vector2 vel,
                                       ulong[] bm, ref bool canHole)
    {
        var (endPos, end) = SimulatePathLite(pos, vel, hole, maxSteps: 200);
        if (end == BallStep.Holed) { canHole = true; return; }
        if (end != BallStep.Settled) return;
        var (egx, egy) = CellOf(endPos);
        int didx = CellIdx(egx, egy);
        bm[didx >> 6] |= 1UL << (didx & 63);
    }

    /// <summary>
    /// Reduced-allocation variant of <see cref="SimulatePath"/> for the
    /// planner — same physics, but skips the per-step path List so each
    /// shot only allocates what's needed for the (endPos, endState) tuple.
    /// </summary>
    private static (Vector2 endPos, BallStep end) SimulatePathLite(
        Vector2 startPos, Vector2 startVel, HoleLayout hole, int maxSteps)
    {
        var pos = startPos;
        var vel = startVel;
        const float dt = 1f / 60f;
        for (int step = 0; step < maxSteps; step++)
        {
            var result = StepBall(ref pos, ref vel, dt, hole);
            if (result != BallStep.Moving) return (pos, result);
        }
        return (pos, BallStep.Moving);
    }

    /// <summary>
    /// Plan the shortest stroke-path from <paramref name="start"/> to
    /// <paramref name="hole"/>'s cup. Returns [start, stop1, stop2, ...,
    /// cup] or an empty list if no path of length ≤ maxStrokes exists.
    /// Intermediate stops are reported at the cell-center granularity of
    /// the planning grid (~7 px slop), which is what R₁ resolves to.
    /// </summary>
    private static List<Vector2> PlanShots(Vector2 start, int maxStrokes, HoleLayout hole)
    {
        var (sgx, sgy) = CellOf(start);
        int sIdx = CellIdx(sgx, sgy);

        var visited = new ulong[ReachWords];
        var parent = new int[ReachCellCount];
        Array.Fill(parent, -1);
        SetBit(visited, sIdx);

        // Frontier-N as a list of cell indices reached at exactly stroke N-1.
        // Reusing two lists rather than scanning the whole bitmap each level
        // because the frontier is typically tiny relative to the grid.
        var current = new List<int> { sIdx };
        var next = new List<int>(64);

        EnsureCellReach(hole, sgx, sgy);

        int holeFromCell = -1;
        int holeStrokes = -1;

        for (int stroke = 1; stroke <= maxStrokes; stroke++)
        {
            // Holing-out edge: any cell on the current frontier whose
            // CanHole bit is set finishes the round at this stroke depth.
            foreach (int srcIdx in current)
            {
                if (hole.CanHole[srcIdx])
                {
                    holeStrokes = stroke;
                    holeFromCell = srcIdx;
                    break;
                }
            }
            if (holeFromCell >= 0) break;

            // Expand into the next frontier.
            next.Clear();
            foreach (int srcIdx in current)
            {
                var bm = hole.Reach[srcIdx];
                if (bm == null) continue;
                for (int w = 0; w < ReachWords; w++)
                {
                    ulong word = bm[w] & ~visited[w];
                    while (word != 0)
                    {
                        int b = System.Numerics.BitOperations.TrailingZeroCount(word);
                        word &= word - 1;
                        int dIdx = (w << 6) | b;
                        if (dIdx >= ReachCellCount) continue;
                        visited[w] |= 1UL << b;
                        parent[dIdx] = srcIdx;
                        next.Add(dIdx);
                    }
                }
            }
            if (next.Count == 0) break;

            // Compute R₁ for newly-reached cells before they enter
            // the next iteration — this is what makes the lazy cache
            // pay off (we skip cells the planner never visits).
            foreach (int idx in next)
                EnsureCellReach(hole, idx % ReachGw, idx / ReachGw);

            (current, next) = (next, current);
        }

        if (holeFromCell < 0) return new List<Vector2>();

        // Walk parent[] back from the holing cell to the start.
        var stops = new List<Vector2>();
        int cur = holeFromCell;
        while (cur != sIdx && cur >= 0)
        {
            stops.Add(CellCenter(cur % ReachGw, cur / ReachGw));
            cur = parent[cur];
        }
        stops.Reverse();
        stops.Insert(0, start);
        stops.Add(hole.Cup);
        return stops;
    }

    private static void SetBit(ulong[] bm, int idx) => bm[idx >> 6] |= 1UL << (idx & 63);

    private static (List<Vector2> path, BallStep end) SimulatePath(Vector2 startPos, Vector2 startVel, HoleLayout hole, int maxSteps = 240)
    {
        var path = new List<Vector2>(maxSteps);
        var pos = startPos;
        var vel = startVel;
        const float dt = 1f / 60f;

        for (int step = 0; step < maxSteps; step++)
        {
            path.Add(pos);
            var result = StepBall(ref pos, ref vel, dt, hole);
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
    /// Pixel-art dithered annulus (ring) for the water-splash ripple. Pixels
    /// inside an elliptical band of half-thickness <paramref name="thickness"/>
    /// around the (rx, ry) ellipse rim are emitted, gated by the 4×4 Bayer
    /// matrix scaled by <paramref name="coverage"/> so the ring fades out
    /// while keeping a hard pixel-art edge instead of going translucent.
    /// </summary>
    private static void DrawDitheredRing(int cx, int cy, int rx, int ry, int thickness, Color col, float coverage)
    {
        if (rx <= 0 || ry <= 0) return;
        int threshold = (int)MathF.Round(Math.Clamp(coverage, 0f, 1f) * 16f);
        if (threshold <= 0) return;
        int yMax = ry + thickness;
        int xMax = rx + thickness;
        for (int dy = -yMax; dy <= yMax; dy++)
        {
            int yy = cy + dy;
            int by = ((yy % 4) + 4) % 4;
            float ny = (float)dy / ry;
            for (int dx = -xMax; dx <= xMax; dx++)
            {
                float nx = (float)dx / rx;
                float r = MathF.Sqrt(nx * nx + ny * ny);
                // Distance from the rim, expressed in average-radius pixels.
                float meanR = 0.5f * (rx + ry);
                float distFromRim = MathF.Abs(r - 1f) * meanR;
                if (distFromRim > thickness) continue;
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
        // Slight tracking — at default font and 32px size the letters of
        // "EAGLE" / "BIRDIE" felt too tight. Scales with size so smaller
        // labels don't get visually disproportionate gaps.
        int tracking = Math.Max(2, size / 12);
        int charsW = 0;
        for (int i = 0; i < text.Length; i++)
            charsW += RetroSkin.MeasureText(text[i].ToString(), size);
        int totalW = charsW + Math.Max(0, text.Length - 1) * tracking;
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
            x += chW + tracking;
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
        var path = SimulatePathNaive(_ball, startVel, _course[_holeIdx]);
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
        var (path, end) = SimulatePath(_ball, startVel, _course[_holeIdx]);
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

}
