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
    /// <summary>
    /// Single source of truth for the user-facing title of this game.
    /// Used by the title bar (in both Picking and Playing states), the
    /// help-overlay header, and the pet-menu launcher entry. Internal
    /// class name / save-file paths / JSON prefs keys are kept stable
    /// across renames so existing saved courses and prefs keep loading.
    /// Change this string and rebuild — that's the whole rename.
    /// </summary>
    public const string AppTitle = "Ohio Golf";

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

        /// <summary>
        /// Drop an impact-crater profile centred at (cx, cy) with the given
        /// radius (in cells). Heightmap gets a Gaussian dip in the middle
        /// (depth) and a thin Gaussian band at d ≈ 0.85·radius for the
        /// raised rim (rim). Falls off cleanly past the rim.
        /// </summary>
        public void AddCrater(float cx, float cy, float radius, float depth, float rim)
        {
            int xMin = Math.Max(0, (int)(cx - radius * 1.5f));
            int xMax = Math.Min(Cols - 1, (int)(cx + radius * 1.5f));
            int yMin = Math.Max(0, (int)(cy - radius * 1.5f));
            int yMax = Math.Min(Rows - 1, (int)(cy + radius * 1.5f));
            for (int x = xMin; x <= xMax; x++)
                for (int y = yMin; y <= yMax; y++)
                {
                    float dx = x - cx, dy = y - cy;
                    float d = MathF.Sqrt(dx * dx + dy * dy) / radius;
                    if (d > 1.3f) continue;
                    // Wide dip centred at d=0; narrow rim band at d≈0.85.
                    float dip = -depth * MathF.Exp(-(d * 2f) * (d * 2f));
                    float rimBand = rim * MathF.Exp(-MathF.Pow((d - 0.85f) * 6.7f, 2f));
                    H[x, y] += dip + rimBand;
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
    /// <summary>Last frame's cursor position in canvas coords — used by
    /// the swept hit-test for the ball press so a click-and-drag whose
    /// cursor crosses the ball mid-motion still registers (the cursor
    /// has typically moved past the ball by the press frame).</summary>
    private Vector2 _prevCanvasMouse;
    private bool _holeComplete;
    private bool _roundComplete;
    private float _holeFlashTimer;
    // Per-hole-complete celebration. Set when the ball drops in the cup
    // and consumed by the wave-text overlay; reset on hole reset/start.
    private string _celebrationText = "";
    private float _celebrationTime;
    // Countdown until the polite-clap SFX fires after a par-or-better hole-out.
    // Negative = no clap pending. Lets the sink/ball-in-hole sound finish its
    // attack before the applause kicks in so they don't smear together.
    private float _clapDelayTimer = -1f;
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

    // ── Course editor ───────────────────────────────────────────────────
    /// <summary>
    /// When true, right-click on the canvas sculpts terrain (drag up =
    /// raise, drag down = lower) and Shift+Right-Click handles the
    /// camera tilt. When false, right-click does the tilt as before.
    /// Off by default so a stray right-click during normal play can't
    /// accidentally deform the course.
    /// </summary>
    private bool _editorMode;
    private bool _editorOpen;             // popup visibility
    private bool _sculpting;
    private float _sculptLastY;
    private float _brushRadius = 14f;     // canvas pixels
    /// <summary>Heightmap snapshots for undo. Bounded to 20 entries; the
    /// oldest is dropped when full. A new snapshot is captured at the
    /// *start* of each sculpt drag so undo unwinds one stroke at a time.</summary>
    private readonly Stack<float[,]> _undoStack = new();
    private readonly Stack<float[,]> _redoStack = new();
    private const int UndoLimit = 20;
    /// <summary>The hole's heightmap state at generation time, captured
    /// once on Load and used by the Reset Hole button to revert all
    /// sculpting in a single click.</summary>
    private float[,]? _origHeightmap;
    private int _origHoleIdx = -1;
    /// <summary>One-shot status string shown in the editor popup right
    /// after a save / load — "Saved as foo.json", "Loaded foo.json", etc.</summary>
    private string _editorStatus = "";
    private float _editorStatusTimer;

    // Aim mode: Combo draws both arc + line plus red planner arrows
    // (supercombo); Line (default) is just the line and arrow; Arc is the
    // dotted trajectory + pulsing landing target.
    // Order matters: the display panel's button row indexes into this enum
    // via (AimStyle)i, and its labels are { "Line", "Arc", "Combo" }. Keep
    // the two lined up — earlier order { Combo, Line, Arc } meant clicking
    // "Line" actually selected Combo, so users got the gold-arc / planner
    // overlay even though the UI said Line.
    private enum AimStyle { Line, Arc, Combo }
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
        Title = AppTitle + " - How to play",
        Lines = new[]
        {
            "Sink the ball in nine holes with as few strokes as possible.",
            "The course is rendered in 2.5D — banded elevation, dithered, with a",
            "configurable tilt (View menu) so you can read the slopes.",
            "Drag from the ball to aim (any tilt), longer drag = more power.",
            "The ball rolls on the actual terrain — putts hook around hills,",
            "stray drives roll into bunkers. Trees bounce, water resets to tee.",
            "Later holes have bigger hills.",
            "",
            "(Tip: a right-click on the Earth might surprise you.)",
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
        _difficulty = Enum.TryParse<Difficulty>(s.Difficulty, out var d) ? d : Difficulty.Easy;
        _beatenRegions = new HashSet<string>(s.BeatenRegions ?? Array.Empty<string>());
        _moonUnlocked = s.MoonUnlocked;
        _pathArrow = s.PathArrow;
        _enableHolePlanner = s.EnableHolePlanner;
        TryLoadTreeSprite();
        TryLoadSwingSound();
        TryLoadSplash();
        // Default startup goes through the Splash title card. The Splash
        // tick auto-advances to a North America round after a short hold
        // (or sooner on a click) — the player can still hit the menu's
        // Region item from gameplay to switch flavors. Booting straight
        // into Playing remains a one-line change if the splash ever needs
        // to come out.
        _activeRegion = Globe.GlobePicker.Regions[0];   // North America
        _state = AppState.Splash;
        _splashTime = 0f;
    }

    private void TryLoadSplash()
    {
        if (_splashTexLoaded) return;
        // PNG only — Raylib's stb_image-based JPG path rendered the
        // splash as a black rect for some inputs; PNG always loads.
        var path = ResolveAssetPath("golf/splash.png");
        if (path == null) return;
        var tex = Raylib.LoadTexture(path);
        if (tex.Width == 0) return;
        // Bilinear so the 1080-wide art doesn't pixelate when squeezed
        // into the canvas — it's photographic, not the usual pixel-art.
        Raylib.SetTextureFilter(tex, TextureFilter.Bilinear);
        _splashTex = tex;
        _splashTexLoaded = true;
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
    private Sound _ballInHoleSound;
    private bool _ballInHoleSoundLoaded;
    private Sound _ballInWaterSound;
    private bool _ballInWaterSoundLoaded;
    private Sound _treeHitSound;
    private bool _treeHitSoundLoaded;
    /// <summary>Cooldown that gates the tree-hit SFX so a ball glancing
    /// or rubbing along a tree doesn't fire the sound on every frame
    /// of contact. Set to ~0.18 s on each play; counted down each tick
    /// during the physics loop. Only collisions seen with the timer at
    /// zero get to play.</summary>
    private float _treeHitDebounce;

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
        if (!_ballInHoleSoundLoaded)
        {
            var path = ResolveAssetPath("golf/sounds/ball_in_hole.wav");
            if (path != null)
            {
                _ballInHoleSound = Raylib.LoadSound(path);
                _ballInHoleSoundLoaded = true;
            }
        }
        if (!_ballInWaterSoundLoaded)
        {
            var path = ResolveAssetPath("golf/sounds/ball_in_water.wav");
            if (path != null)
            {
                _ballInWaterSound = Raylib.LoadSound(path);
                _ballInWaterSoundLoaded = true;
            }
        }
        if (!_treeHitSoundLoaded)
        {
            var path = ResolveAssetPath("golf/sounds/tree_hit.wav");
            if (path != null)
            {
                _treeHitSound = Raylib.LoadSound(path);
                _treeHitSoundLoaded = true;
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

    /// <summary>Activity-level mode. Splash is the title-card shown briefly
    /// on launch (auto-advances after a few seconds, or click-anywhere skip).
    /// Playing is the default round screen. Picking is opt-in — opened from
    /// the menu when the player wants to change regions, and used once
    /// during the Moon-unlock fanfare.</summary>
    private enum AppState { Splash, Picking, Playing }
    private AppState _state = AppState.Splash;
    private float _splashTime;
    private const float SplashHoldSeconds = 2.5f;
    private Texture2D _splashTex;
    private bool _splashTexLoaded;
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

        /// <summary>
        /// Region names the player has finished a full round of. Drives the
        /// Moon unlock — once this set covers every Earth region, the Moon
        /// becomes selectable on the picker. "Beaten" is the friendlier
        /// "completed at least once" definition rather than "par or better".
        /// </summary>
        public string[] BeatenRegions { get; set; } = Array.Empty<string>();
        public bool MoonUnlocked { get; set; } = false;
        /// <summary>Toggle for the red pathfinding-arrow overlay; was
        /// previously implicit in AimStyle.Combo. Defaults to false so
        /// existing players keep their current 'no arrows by default'
        /// experience until they opt in from the Display popup.</summary>
        public bool PathArrow { get; set; } = false;
        /// <summary>Debug-only: run the BFS reach-grid stroke planner
        /// when a round loads. The planner sets per-hole Par tightly
        /// matched to the difficulty and powers the supercombo path
        /// arrows — but it's expensive (multi-second freeze on heavy
        /// regions like Ohio). Off by default; flip on if you want
        /// the planner-quality Par + arrows. Without it: Par defaults
        /// to a hardcoded value per hole index (loose but instant)
        /// and the path-arrow overlay stays empty.</summary>
        public bool EnableHolePlanner { get; set; } = false;
    }

    private bool _enableHolePlanner;

    private string[] MenuLabels() => new[]
    {
        "New Round", "Replay Hole", "Skip",
        $"Region: {(_activeRegion?.Name ?? "-")}",
        _pendingDifficulty.HasValue && _pendingDifficulty.Value != _difficulty
            ? $"Difficulty: {_pendingDifficulty} (next round)"
            : $"Difficulty: {_difficulty}",
        "Display",
        _editorMode ? "Edit: ON" : "Edit",
        "Help",
    };

    /// <summary>
    /// In-memory mirror of the persisted region-beaten set. Loaded from
    /// disk in Load() and written back any time we save prefs.
    /// </summary>
    private HashSet<string> _beatenRegions = new();
    private bool _moonUnlocked;
    /// <summary>True for one return-to-picker transition after the player
    /// just beat the last needed region — drives the picker's unlock
    /// fanfare animation.</summary>
    private bool _justUnlockedMoon;
    private float _unlockReturnTimer;

    /// <summary>Modal Display popup — replaces the four separate menu items
    /// (Aim, Palette, Sky, Background) with one fine-grained dialog.</summary>
    private bool _displayOpen;

    private static readonly string[] EarthRegionNames =
    {
        "North America", "Europe", "Asia", "South America",
        "Australia", "Africa", "Ohio",
    };

    private void SaveWorldTeePrefs() =>
        MouseHouse.Core.SaveManager.Save("world_tee_classic.json",
            new WorldTeeClassicPrefs
            {
                BeatenRegions = _beatenRegions.ToArray(),
                MoonUnlocked = _moonUnlocked,
                PathArrow = _pathArrow,
                EnableHolePlanner = _enableHolePlanner,
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
    /// Region → Difficulty override. Regions are flavor only — difficulty
    /// is the player's separate menu knob and persists through region
    /// switches. The single exception is Ohio, which is a punchline region
    /// (bears!) and snaps to its own Brutal tier; that's the joke. The
    /// Moon victory-lap mode is also pinned (Easy + low gravity carry the
    /// celebratory feel; the player just earned it).
    /// </summary>
    private static Difficulty? DifficultyOverrideForRegion(Globe.Region r) => r.Name switch
    {
        "Ohio" => Difficulty.Ohio,
        "Moon" => Difficulty.Easy,
        _      => null,
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
        if (_splashTexLoaded)
        {
            Raylib.UnloadTexture(_splashTex);
            _splashTexLoaded = false;
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
        if (_ballInHoleSoundLoaded)
        {
            Raylib.UnloadSound(_ballInHoleSound);
            _ballInHoleSoundLoaded = false;
        }
        if (_ballInWaterSoundLoaded)
        {
            Raylib.UnloadSound(_ballInWaterSound);
            _ballInWaterSoundLoaded = false;
        }
        if (_treeHitSoundLoaded)
        {
            Raylib.UnloadSound(_treeHitSound);
            _treeHitSoundLoaded = false;
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
        _isMoonRound = _activeRegion?.Name == "Moon";
        var rng = new Random();
        int densityBoost = CurrentDensityBoost();
        var range = CurrentStrokeRange();
        var raw = new HoleLayout[Holes];
        for (int i = 0; i < Holes; i++)
        {
            raw[i] = GenerateHole(i, rng, densityBoost, _isMoonRound);
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

        // The reach-grid stroke planner is debug-only by default. Without
        // it: per-hole Par defaults to a hardcoded value (loose but
        // instant) and the path-arrow overlay stays empty. With it: tight
        // Par tuned to the difficulty + supercombo arrows, but a
        // multi-second freeze on heavy regions like Ohio while it runs.
        // Toggle via WorldTeeClassicPrefs.EnableHolePlanner (off by
        // default).
        if (_enableHolePlanner)
        {
            var sharedPlanRng = new Random();
            var plannedZero = MakePlayableHole(0, raw[0], sharedPlanRng, range, densityBoost, _isMoonRound);
            raw[0] = plannedZero;
            _course[0] = plannedZero;
        }
        else
        {
            // Hand-rolled Par fallback — close enough for celebration
            // text (Birdie / Eagle / etc) without paying the planner.
            int defaultPar = ParFallback(0);
            _course[0] = raw[0] with { Par = defaultPar };
        }
        System.Threading.Volatile.Write(ref _holeReady[0], true);

        ResetBall();

        // Background pre-planner — same gate as above. Skipped entirely
        // when the planner's off so the threadpool isn't churning behind
        // the user's back. When on, runs as ONE sequential task instead
        // of 8 parallel ones (concurrent Task.Runs starved the main
        // thread on heavy regions before this was made sequential).
        if (_enableHolePlanner)
        {
            bool moonSnap = _isMoonRound;
            System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 1; i < Holes; i++)
                {
                    if (System.Threading.Volatile.Read(ref _genVersion) != planVersion) return;
                    var bgRng = new Random();
                    var planned = MakePlayableHole(i, raw[i], bgRng, range, densityBoost, moonSnap);
                    if (System.Threading.Volatile.Read(ref _genVersion) != planVersion) return;
                    PublishPlannedHole(i, planned);
                }
            });
        }
        else
        {
            // Same fallback Par for holes 1..N-1 so the celebration text
            // at end-of-hole has something sensible to compute against.
            for (int i = 1; i < Holes; i++)
                _course[i] = raw[i] with { Par = ParFallback(i) };
        }
    }

    /// <summary>Hardcoded Par per hole index when the planner is off.
    /// Mirrors the difficulty-driven stroke range so easy regions still
    /// get easier pars and Ohio still gets longer ones.</summary>
    private int ParFallback(int idx)
    {
        var range = CurrentStrokeRange();
        // Average the difficulty's stroke range and add the standard
        // forgiveness buffer. Slight per-hole variation so par 4 / 5 /
        // 6 mix instead of every hole reading the same value.
        int basePar = (range.Min + range.Max) / 2 + ParSlack;
        int wobble = (idx % 3) - 1;     // -1, 0, +1 across the round
        return Math.Clamp(basePar + wobble, 2, 12);
    }

    /// <summary>
    /// Single source of truth for mutating <c>_course[holeIdx]</c> after
    /// StartRound's initial fill. Refuses to overwrite the active or
    /// already-completed hole — that's the level-swap-mid-play bug. The
    /// invariant is asserted in dev; in release builds an out-of-window
    /// publish is silently dropped (the layout the player started on
    /// stays).
    ///
    /// <para>Reading <c>_holeIdx</c> here without a lock is safe because
    /// it only ever monotonically increases within a round (set to 0 in
    /// StartRound, ++ in AdvanceHole, both on the main thread). A racy
    /// read at worst trails the true value by a frame, which still lands
    /// the publish on a future hole — never the active one.</para>
    /// </summary>
    private void PublishPlannedHole(int holeIdx, HoleLayout planned)
    {
        // The invariant: only holes the player hasn't reached yet are
        // mutable. holeIdx == _holeIdx + 1, == _holeIdx + 2, ... are
        // fine; holeIdx <= _holeIdx is forbidden.
        int activeHole = _holeIdx;
        System.Diagnostics.Debug.Assert(holeIdx > activeHole,
            $"Planner tried to overwrite hole {holeIdx} while player is on hole {activeHole}. " +
            "Active and past holes are frozen — only future holes can be re-published.");
        if (holeIdx <= activeHole) return;
        _course[holeIdx] = planned;
        System.Threading.Volatile.Write(ref _holeReady[holeIdx], true);
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
    // in supercombo aim mode. Round-generation derives a per-difficulty
    // cap from this floor + the difficulty's stroke-range max (see
    // MakePlayableHole) — Ohio's range tops out at 9 strokes, so the
    // generator needs to search deeper than 6 or every hole comes back
    // with no plan and we burn 14 attempts × full-grid BFS each, freezing
    // the main thread on hole 0's synchronous plan.
    private const int PlanStrokesCap = 6;
    // Forgiveness buffer added on top of the planner's optimal stroke
    // count. The planner plays pixel-perfect; real players can't, so
    // par reads as "AI strokes + 2" to keep birdies achievable.
    private const int ParSlack = 2;
    // Hard ceiling on regeneration tries before we give up and accept
    // whatever we have (or fall back to a conservative par).
    private const int MaxHoleAttempts = 3;

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
                                               (int Min, int Max) range, int densityBoost,
                                               bool isMoon = false)
    {
        // The PLANNER searches deep enough to find paths inside this
        // difficulty's stroke-range. With the old fixed PlanStrokesCap=6,
        // Ohio (range 8-9) was unreachable in every BFS — every attempt
        // came back empty, dist never updated, all 14 attempts ran a full
        // 6-level grid traversal for nothing, and hole 0's synchronous
        // plan froze the activity for ~10s.
        //
        // BFS terminates as soon as ANY frontier cell can hole, so a
        // higher cap doesn't cost more on holes whose cup is reachable
        // quickly — the cost only shows up when the cap would otherwise
        // have prevented the planner from finding a path at all.
        int planCap = Math.Max(PlanStrokesCap, range.Max + 2);

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
                : GenerateHole(idx, rng, densityBoost, isMoon);
            var planPath = PlanShots(candidate.Tee, planCap, candidate);
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
        return best ?? (firstCandidate with { Par = planCap + ParSlack });
    }

    private void ResetBall()
    {
        _ball = _course[_holeIdx].Tee;
        _vel = Vector2.Zero;
        _aiming = false;
        _holeFlashTimer = 0;
        _celebrationText = "";
        _celebrationTime = 0f;
        _clapDelayTimer = -1f;
        _trail.Clear();
        _planDirty = true;
        _planStops.Clear();
    }

    private static HoleLayout GenerateHole(int idx, Random rng, int densityBoost = 0, bool isMoon = false)
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

        // Trees: skipped on the moon (no atmosphere → no plants).
        var trees = new List<Vector2>();
        if (!isMoon)
        {
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

        if (isMoon)
        {
            // Moon: 8-20 craters of varied size, larger ones rarer. Each
            // carves a circular dip with a raised rim; combine to give
            // the pockmarked look. No bumps — moon terrain is purely
            // crater-defined (plus the cup/tee flatten passes below).
            int craterCount = 8 + rng.Next(13);
            for (int c = 0; c < craterCount; c++)
            {
                // Power distribution: larger craters rarer. Roll 0..1, square
                // to bias toward small. radius range 4..22 cells.
                float t = (float)rng.NextDouble();
                float radius = 4f + (t * t) * 18f;
                float depth = 4f + radius * 0.45f;
                float rim = depth * 0.45f;
                float cx = (float)rng.NextDouble() * cols;
                float cy = (float)rng.NextDouble() * rows;
                hf.AddCrater(cx, cy, radius, depth, rim);
            }
        }
        else
        {
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
    private Color[] MeshPalette => _isMoonRound ? MoonPalette : MeshPalettes[_meshPaletteIdx].Stops;

    /// <summary>Gray/cratered terrain palette used only on moon rounds —
    /// overrides the user's saved palette so the moon always reads as
    /// otherworldly. 10 stops, low-key shadow → bright crater rim.</summary>
    private static readonly Color[] MoonPalette = new Color[]
    {
        new((byte) 18, (byte) 18, (byte) 26, (byte)255),
        new((byte) 32, (byte) 34, (byte) 44, (byte)255),
        new((byte) 56, (byte) 58, (byte) 68, (byte)255),
        new((byte) 84, (byte) 86, (byte) 96, (byte)255),
        new((byte)112, (byte)116, (byte)126, (byte)255),
        new((byte)138, (byte)142, (byte)150, (byte)255),
        new((byte)168, (byte)170, (byte)174, (byte)255),
        new((byte)198, (byte)200, (byte)200, (byte)255),
        new((byte)226, (byte)226, (byte)222, (byte)255),
        new((byte)244, (byte)242, (byte)234, (byte)255),
    };

    private bool _isMoonRound;

    /// <summary>Independent toggle for the red 'safe landing zone' arrow
    /// pathfinding overlay. Used to be implicitly bound to AimStyle.Combo;
    /// now lives on its own so any aim style can show or hide path arrows.
    /// Persisted in WorldTeeClassicPrefs.</summary>
    private bool _pathArrow;

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

        // Splash title-card: holds the splash art for SplashHoldSeconds,
        // or skips on click. Either way exits to a default round on the
        // currently-selected region. The title-bar X still closes the
        // window so the user isn't trapped if they want out fast.
        if (_state == AppState.Splash)
        {
            var titleBarSplash = new Rectangle(FrameInset, FrameInset,
                PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
            if (RetroWidgets.DrawTitleBarHitTest(titleBarSplash, local, leftPressed))
            { IsFinished = true; return; }

            _splashTime += delta;
            bool clickedToSkip = leftPressed && Raylib.CheckCollisionPointRec(local,
                new Rectangle(0, 0, PanelSize.X, PanelSize.Y));
            if (_splashTime >= SplashHoldSeconds || clickedToSkip)
            {
                _state = AppState.Playing;
                StartRound();
            }
            return;
        }

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
            _picker.Update(delta, mousePos, hostPick, leftPressed, leftReleased, leftHeld, rightPressed);

            // Picker can flip MoonUnlocked itself (right-click-Earth cheat).
            // Mirror the change into our own state + persist so the flag
            // survives a restart, matching the natural-completion path.
            if (!_moonUnlocked && _picker.MoonUnlocked)
            {
                _moonUnlocked = true;
                SaveWorldTeePrefs();
            }

            if (_picker.Picked && _picker.PickedRegion != null)
            {
                _activeRegion = _picker.PickedRegion;
                // Apply the region's difficulty *only* when the region
                // pins one (Ohio joke tier, Moon victory lap). Otherwise
                // keep whatever difficulty the player has set on the menu.
                var ovrd = DifficultyOverrideForRegion(_activeRegion);
                if (ovrd.HasValue) _difficulty = ovrd.Value;
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
                // Re-open the globe picker so the player can switch regions
                // on demand. Picking a region from here starts a new round
                // (StartRound) — the only legitimate level-swap path, so
                // the no-mid-play-swap rule isn't violated.
                _picker?.Unload();
                _picker = new Globe.GlobePicker(CanvasW, CanvasH);
                _picker.SetMoonState(_moonUnlocked, justUnlocked: false);
                _state = AppState.Picking;
                return;
            case 4:
                // Cycle difficulty for the *next* round. Critical: never
                // regenerate the active course mid-play — that would yank
                // the player onto a fresh hole 1 with new geometry,
                // erasing whatever shot they were lining up. The active
                // round keeps its difficulty until the user explicitly
                // chooses 'New Round' (case 0). _difficulty is only read
                // inside StartRound, so a deferred change is sufficient.
                // Cycles Easy..Legendary; Ohio is region-pinned so we
                // skip it here (would be confusing without the bears).
                var cur = _pendingDifficulty ?? _difficulty;
                int curI = (int)cur;
                int nextI = (curI + 1) % (int)Difficulty.Ohio;     // 0..5
                var next = (Difficulty)nextI;
                _pendingDifficulty = next == _difficulty ? null : next;
                SaveWorldTeePrefs();
                return;
            case 5: _displayOpen = !_displayOpen; return;
            case 6: _editorOpen = !_editorOpen; return;
            case 7: _help.Visible = !_help.Visible; return;
        }
        if (_help.HandleInput(local, leftPressed, PanelSize)) return;
        if (_displayOpen)
        {
            UpdateDisplayPanel(local, leftPressed);
            return;
        }
        if (_editorOpen)
        {
            UpdateEditorPanel(local, leftPressed);
            return;
        }

        // After a Moon-unlocking round, briefly hold on the round-complete
        // screen so the player can read their score, then bounce them
        // back to the picker with the unlock fanfare so they actually see
        // the Moon appear in real time.
        if (_roundComplete && _justUnlockedMoon)
        {
            _unlockReturnTimer += delta;
            if (_unlockReturnTimer >= 2.2f)
            {
                _unlockReturnTimer = 0;
                _picker?.Unload();
                _picker = new Globe.GlobePicker(CanvasW, CanvasH);
                _picker.SetMoonState(true, justUnlocked: true);
                _justUnlockedMoon = false;
                _state = AppState.Picking;
                _roundComplete = false;
                _holeIdx = 0;
                return;
            }
        }
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

        // Pending applause — fire when the post-sink delay expires. For
        // par-or-better the ball-in-hole sample is cut short the moment
        // the clap kicks in so the crowd doesn't have to compete with
        // the long tail of the sink-tone (which runs ~2.2 s).
        if (_clapDelayTimer >= 0f)
        {
            _clapDelayTimer -= delta;
            if (_clapDelayTimer <= 0f)
            {
                if (_ballInHoleSoundLoaded) Raylib.StopSound(_ballInHoleSound);
                if (_clapSoundLoaded) Raylib.PlaySound(_clapSound);
                _clapDelayTimer = -1f;
            }
        }

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
            // Moon rounds run with reduced gravity + reduced friction so
            // the ball glides much further per stroke. Scales applied only
            // to the live physics; the planner's BFS still uses the Earth
            // constants because Moon courses use a different layout
            // generator anyway (no wind, simpler heightmap).
            // Moon: lower gravity (slope feel softened) plus aggressively
            // lower friction so the ball really glides. Tuning history:
            // 0.55 → 0.40 → 0.30 — at 0.40 the ball still slowed too fast
            // for the moon to feel unmistakable, so pulled to 0.30 per
            // user feedback ('balls travel forever'). Combined with the
            // 2.0x launch impulse below, a max-drag moon shot rolls ~4x
            // further than the same drag on Earth.
            float gravScale = _isMoonRound ? 0.32f : 1f;
            float frictionScale = _isMoonRound ? 0.30f : 1f;

            var grad = hf.Gradient(_ball.X, _ball.Y);
            var slopeAccel = -grad * GravStrength * gravScale;
            _vel += slopeAccel * delta;

            int t = Terrain(_ball);
            float visc = t switch { 2 => 5.5f, 1 => 2.6f, 4 => 0.8f, _ => 1.4f };
            visc *= frictionScale;
            _vel *= MathF.Max(0, 1f - visc * delta);
            float speed = _vel.Length();
            if (speed > 0.01f)
            {
                float decel = KineticFriction * frictionScale * delta;
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

            // Tick the tree-hit SFX debounce so the cooldown advances
            // even on physics frames with no collision. Same `delta`
            // the trail aging above uses.
            if (_treeHitDebounce > 0f) _treeHitDebounce -= delta;

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
                    // Play the thunk sound on real impacts only — the
                    // 0.18 s cooldown swallows the rapid-fire repeat
                    // hits that happen when the ball is rubbing along
                    // a tree's edge or the reflect bounces it back into
                    // the same tree on the next physics step.
                    if (_treeHitDebounce <= 0f && _treeHitSoundLoaded)
                    {
                        Raylib.PlaySound(_treeHitSound);
                        _treeHitDebounce = 0.18f;
                    }
                }
            }

            if (Terrain(_ball) == 3)
            {
                _ripples.Add((_ball, 0f));
                if (_ballInWaterSoundLoaded) Raylib.PlaySound(_ballInWaterSound);
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
                if (_ballInHoleSoundLoaded) Raylib.PlaySound(_ballInHoleSound);
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
                // Polite golf-crowd clap for par-or-better finishes. Fired
                // after a short delay so the ball-in-hole sample lands
                // first, then cuts short (Raylib.StopSound on the ball
                // sample when the clap fires) so the crowd reads cleanly
                // instead of getting buried under the sink-tone's tail.
                // Bogey or worse: no clap; the ball-in-hole plays out in
                // full as its own punctuation.
                _clapDelayTimer = (dpar <= 0 && _clapSoundLoaded) ? 0.7f : -1f;
            }
        }

        // Right-click drag in the canvas: tilts the camera (Shift held, or
        // any time editor mode is off) — or sculpts terrain (editor mode +
        // no Shift). The wireframe preview kicks in for either gesture so
        // we don't pay the ~150ms texture rebuild every mouse-move.
        bool inCanvas = canvasMouse.X >= 0 && canvasMouse.X < CanvasW
                     && canvasMouse.Y >= 0 && canvasMouse.Y < CanvasH;
        bool shift = Raylib.IsKeyDown(KeyboardKey.LeftShift)
                  || Raylib.IsKeyDown(KeyboardKey.RightShift);
        bool sculptGesture = _editorMode && !shift;

        // Brush-radius mouse-wheel adjust (only meaningful in editor
        // mode; harmless to leave on always).
        if (_editorMode && inCanvas)
        {
            float wheel = Raylib.GetMouseWheelMove();
            if (wheel != 0)
                _brushRadius = Math.Clamp(_brushRadius + wheel * 2f, 4f, 60f);
        }

        // Cmd/Ctrl+Z (undo) / Cmd/Ctrl+Shift+Z (redo) — gated on editor
        // mode so it can't fire by accident during normal play.
        bool ctrlOrCmd = Raylib.IsKeyDown(KeyboardKey.LeftSuper) || Raylib.IsKeyDown(KeyboardKey.RightSuper)
                      || Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl);
        if (_editorMode && ctrlOrCmd && Raylib.IsKeyPressed(KeyboardKey.Z))
        {
            if (shift) RedoSculpt();
            else       UndoSculpt();
        }

        if (rightPressed && inCanvas)
        {
            if (sculptGesture)
            {
                _sculpting = true;
                _sculptLastY = canvasMouse.Y;
                CaptureUndoSnapshot();
                _redoStack.Clear();    // a new edit invalidates redo history
            }
            else
            {
                _tiltDragging = true;
                _tiltDragLastY = canvasMouse.Y;
            }
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
        if (_sculpting)
        {
            if (Raylib.IsMouseButtonDown(MouseButton.Right))
            {
                float dy = canvasMouse.Y - _sculptLastY;
                _sculptLastY = canvasMouse.Y;
                // Drag up = raise (negative dy), drag down = lower (positive dy).
                // 0.6 chosen so a 100 px drag changes height by a strong but
                // not-saturating ~60 units.
                float amp = -dy * 0.6f;
                if (MathF.Abs(amp) > 0.05f)
                    ApplyBrush(canvasMouse, _brushRadius, amp);
            }
            else
            {
                _sculpting = false;
                UnloadTerrainTextures();
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

        // Swept hit-test for the press: did the cursor's path between
        // last frame and this frame pass within BallHitRadius of the
        // ball? Without this, clicking-and-immediately-dragging missed
        // — the cursor was over the ball mid-motion (between frames)
        // but had drifted past by the time the press frame ran. The
        // line-segment-vs-circle test catches the sub-frame intersection.
        bool sweptOverBall = false;
        if (leftPressed)
        {
            var ab = canvasMouse - _prevCanvasMouse;
            float lenSq = ab.LengthSquared();
            if (lenSq > 0.0001f)
            {
                float t = Vector2.Dot(ballScreen - _prevCanvasMouse, ab) / lenSq;
                t = Math.Clamp(t, 0f, 1f);
                var closest = _prevCanvasMouse + ab * t;
                sweptOverBall = (ballScreen - closest).LengthSquared()
                              < BallHitRadius * BallHitRadius;
            }
        }
        _prevCanvasMouse = canvasMouse;

        _ballReadyHover = ballSlow && cursorOverBall && !_aiming && !_holeComplete && inCanvas;
        if (ballSlow && leftPressed && (cursorOverBall || sweptOverBall))
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
            // Moon: same drag delivers 2x more impulse, and the cap lifts
            // to match. The pseudo-2D top-down model means lower gravity
            // (slope acceleration, not altitude) doesn't give the visible
            // 'long arc' you'd want — the lunar feel comes from per-swing
            // distance instead. Combined with the 0.30 friction scale in
            // the integration loop, a fully wound moon shot rolls ~4x
            // further than the same drag on Earth.
            float shotScale = _isMoonRound ? 2.0f : 1f;
            float maxP = PlayerMaxShotPower * shotScale;
            float power = Math.Min(worldDir.Length() * 4f * shotScale, maxP);
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
        if (_editorStatusTimer > 0) _editorStatusTimer = MathF.Max(0, _editorStatusTimer - delta);
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

    // ── Display popup ───────────────────────────────────────────────────

    /// <summary>Layout numbers for the Display popup. Centred over the
    /// canvas; sized for the four sections (aim row, three palette
    /// columns, presets row).</summary>
    private const int DisplayPanelW = 480;
    private const int DisplayPanelH = 380;

    private Rectangle DisplayPanelRectLocal()
    {
        int x = FrameInset + (CanvasW - DisplayPanelW) / 2;
        int y = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
              + (CanvasH - DisplayPanelH) / 2;
        return new Rectangle(x, y, DisplayPanelW, DisplayPanelH);
    }

    private void UpdateDisplayPanel(Vector2 local, bool leftPressed)
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { _displayOpen = false; return; }
        if (!leftPressed) return;
        var r = DisplayPanelRectLocal();
        // Title bar X
        var titleBar = new Rectangle(r.X + 3, r.Y + 3, r.Width - 6, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        { _displayOpen = false; return; }
        // Click-outside dismiss (so the user has two ways out).
        if (!RetroSkin.PointInRect(local, r)) { _displayOpen = false; return; }

        // Aim style row: 3 buttons.
        int contentX = (int)r.X + 16;
        int y = (int)r.Y + RetroWidgets.TitleBarHeight + 16 + 18;
        for (int i = 0; i < 3; i++)
        {
            var btn = new Rectangle(contentX + i * 90, y, 80, 24);
            if (RetroSkin.PointInRect(local, btn))
            {
                _aimStyle = (AimStyle)i;
                _planDirty = true;
                SaveWorldTeePrefs();
                return;
            }
        }
        // Path-arrow toggle on the right of the aim-style row — independent
        // of aim style now, so any combination is selectable.
        var pathBtn = new Rectangle(contentX + 3 * 90 + 16, y, 132, 24);
        if (RetroSkin.PointInRect(local, pathBtn))
        {
            _pathArrow = !_pathArrow;
            _planDirty = true;
            SaveWorldTeePrefs();
            return;
        }
        y += 24 + 18;

        // Three palette sections side-by-side. Each shows a vertical list
        // of named swatches; click anywhere on a row to select.
        int colW = (int)((r.Width - 32 - 16) / 3);
        int rowH = 18;
        int section0X = contentX;
        int section1X = contentX + colW + 8;
        int section2X = contentX + (colW + 8) * 2;

        for (int i = 0; i < MeshPalettes.Length; i++)
        {
            var row = new Rectangle(section0X, y + 14 + i * rowH, colW, rowH - 1);
            if (RetroSkin.PointInRect(local, row))
            {
                _meshPaletteIdx = i;
                UnloadTerrainTextures();
                SaveWorldTeePrefs();
                return;
            }
        }
        for (int i = 0; i < SkyPalettes.Length; i++)
        {
            var row = new Rectangle(section1X, y + 14 + i * rowH, colW, rowH - 1);
            if (RetroSkin.PointInRect(local, row))
            {
                _skyPaletteIdx = i;
                UnloadTerrainTextures();
                SaveWorldTeePrefs();
                return;
            }
        }
        for (int i = 0; i < BgPalettes.Length; i++)
        {
            var row = new Rectangle(section2X, y + 14 + i * rowH, colW, rowH - 1);
            if (RetroSkin.PointInRect(local, row))
            {
                _bgPaletteIdx = i;
                UnloadTerrainTextures();
                SaveWorldTeePrefs();
                return;
            }
        }

        // Quick-apply presets at the bottom.
        int presetY = (int)r.Y + (int)r.Height - 36;
        var presets = DisplayPresets;
        int presetW = ((int)r.Width - 32 - (presets.Length - 1) * 8) / presets.Length;
        for (int i = 0; i < presets.Length; i++)
        {
            var btn = new Rectangle(contentX + i * (presetW + 8), presetY, presetW, 24);
            if (RetroSkin.PointInRect(local, btn))
            {
                var p = presets[i];
                _meshPaletteIdx = Math.Clamp(p.MeshIdx, 0, MeshPalettes.Length - 1);
                _skyPaletteIdx  = Math.Clamp(p.SkyIdx,  0, SkyPalettes.Length - 1);
                _bgPaletteIdx   = Math.Clamp(p.BgIdx,   0, BgPalettes.Length - 1);
                _aimStyle = p.Aim;
                _planDirty = true;
                UnloadTerrainTextures();
                SaveWorldTeePrefs();
                return;
            }
        }
    }

    private record class DisplayPreset(string Name, int MeshIdx, int SkyIdx, int BgIdx, AimStyle Aim);
    private static readonly DisplayPreset[] DisplayPresets =
    {
        new("Greens / Dawn / Forest / Line", 1, 1, 1, AimStyle.Line),
        new("Dusk / Twilight / Sand / Arc",   3, 3, 4, AimStyle.Arc),
        new("Bands / Day / Slate / Combo",    0, 2, 5, AimStyle.Combo),
    };

    // ── Editor: save / load / reveal ────────────────────────────────────

    /// <summary>JSON shape for a saved single-hole course. schemaVersion
    /// lets future edits add fields without breaking old saves; readers
    /// look at this to decide what's safely loadable.</summary>
    private class CourseSerial
    {
        public int schemaVersion { get; set; } = 1;
        public string savedAtUtc { get; set; } = "";
        public string regionName { get; set; } = "";
        public string difficulty { get; set; } = "";
        public float teeX { get; set; }
        public float teeY { get; set; }
        public float cupX { get; set; }
        public float cupY { get; set; }
        public int par { get; set; }
        public float[] trees { get; set; } = Array.Empty<float>();   // pairs flat
        public float[] hazards { get; set; } = Array.Empty<float>(); // (cx, cy, rx, ry, kind) groups
        public int heightCols { get; set; }
        public int heightRows { get; set; }
        public float heightCellSize { get; set; }
        public float[] heights { get; set; } = Array.Empty<float>(); // row-major, length cols*rows
    }

    private static string CoursesDir
        => Path.Combine(MouseHouse.Core.SaveManager.SaveDirectory, "courses");

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "CourseSerial is a small fixed POCO; reflection-based serialisation is intentional.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Same — small POCO, JSON shape is stable.")]
    private void SaveCurrentCourse()
    {
        try
        {
            Directory.CreateDirectory(CoursesDir);
            var hole = _course[_holeIdx];
            var hf = hole.Heightmap;
            var ser = new CourseSerial
            {
                schemaVersion = 1,
                savedAtUtc = DateTime.UtcNow.ToString("o"),
                regionName = _activeRegion?.Name ?? "",
                difficulty = _difficulty.ToString(),
                teeX = hole.Tee.X, teeY = hole.Tee.Y,
                cupX = hole.Cup.X, cupY = hole.Cup.Y,
                par = hole.Par,
                heightCols = hf.Cols,
                heightRows = hf.Rows,
                heightCellSize = hf.CellSize,
            };
            // Flatten trees as (x, y) pairs.
            var treeBuf = new List<float>(hole.Trees.Count * 2);
            foreach (var t in hole.Trees) { treeBuf.Add(t.X); treeBuf.Add(t.Y); }
            ser.trees = treeBuf.ToArray();
            // Flatten hazards as (cx, cy, rx, ry, kind).
            var hzBuf = new List<float>(hole.Hazards.Count * 5);
            foreach (var (c, rx, ry, kind) in hole.Hazards)
            { hzBuf.Add(c.X); hzBuf.Add(c.Y); hzBuf.Add(rx); hzBuf.Add(ry); hzBuf.Add(kind); }
            ser.hazards = hzBuf.ToArray();
            // Flatten heights row-major.
            var heights = new float[hf.Cols * hf.Rows];
            for (int y = 0; y < hf.Rows; y++)
                for (int x = 0; x < hf.Cols; x++)
                    heights[y * hf.Cols + x] = hf.H[x, y];
            ser.heights = heights;

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string fname = $"{stamp}-{_activeRegion?.Name ?? "course"}-h{_holeIdx + 1}.json";
            // Strip whitespace & path-unfriendly chars from region name.
            fname = string.Concat(fname.Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(CoursesDir, fname);
            var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(ser, opts));
            _editorStatus = $"Saved {fname}";
            _editorStatusTimer = 3f;
        }
        catch (Exception ex)
        {
            _editorStatus = $"Save failed: {ex.Message}";
            _editorStatusTimer = 4f;
        }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "CourseSerial is a small fixed POCO; reflection-based deserialisation is intentional.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Same — small POCO, JSON shape is stable.")]
    private void LoadMostRecentCourse()
    {
        try
        {
            if (!Directory.Exists(CoursesDir))
            { _editorStatus = "No courses folder yet — Save first."; _editorStatusTimer = 3f; return; }
            var files = Directory.GetFiles(CoursesDir, "*.json")
                                 .OrderByDescending(File.GetLastWriteTimeUtc)
                                 .ToArray();
            if (files.Length == 0)
            { _editorStatus = "No saved courses found."; _editorStatusTimer = 3f; return; }
            var path = files[0];
            var json = File.ReadAllText(path);
            var ser = System.Text.Json.JsonSerializer.Deserialize<CourseSerial>(json);
            if (ser == null || ser.schemaVersion < 1)
            { _editorStatus = $"Bad save schema in {Path.GetFileName(path)}"; _editorStatusTimer = 4f; return; }

            // Build a fresh HeightField from the saved grid.
            var hf = new HeightField(ser.heightCols, ser.heightRows, ser.heightCellSize);
            for (int y = 0; y < hf.Rows; y++)
                for (int x = 0; x < hf.Cols; x++)
                    hf.H[x, y] = ser.heights[y * hf.Cols + x];
            // Trees / hazards.
            var trees = new List<Vector2>();
            for (int i = 0; i + 1 < ser.trees.Length; i += 2)
                trees.Add(new Vector2(ser.trees[i], ser.trees[i + 1]));
            var hazards = new List<(Vector2 Center, float Rx, float Ry, int Kind)>();
            for (int i = 0; i + 4 < ser.hazards.Length; i += 5)
                hazards.Add((new Vector2(ser.hazards[i], ser.hazards[i + 1]),
                             ser.hazards[i + 2], ser.hazards[i + 3], (int)ser.hazards[i + 4]));

            // Load REPLACES the active hole. This is an explicit user
            // action — the no-mid-play-swap rule applies to the planner
            // and to silent re-rolls, not to deliberate editor edits.
            // PublishPlannedHole's invariant guards against the planner
            // overwriting; user-driven loads land directly on _course
            // and reset the ball to the new tee like Replay Hole would.
            var loaded = new HoleLayout(
                new Vector2(ser.teeX, ser.teeY),
                new Vector2(ser.cupX, ser.cupY),
                ser.par,
                trees,
                hazards,
                hf,
                TeePlan: null);
            _course[_holeIdx] = loaded;
            _origHeightmap = null;          // new hole — Reset target rebuilds on next sculpt
            _origHoleIdx = -1;
            _undoStack.Clear();
            _redoStack.Clear();
            _planDirty = true;
            UnloadTerrainTextures();
            ResetBall();
            _editorStatus = $"Loaded {Path.GetFileName(path)}";
            _editorStatusTimer = 3f;
        }
        catch (Exception ex)
        {
            _editorStatus = $"Load failed: {ex.Message}";
            _editorStatusTimer = 4f;
        }
    }

    private void RevealCoursesFolder()
    {
        try
        {
            Directory.CreateDirectory(CoursesDir);
            string fileName, args;
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.OSX))
            { fileName = "open"; args = $"\"{CoursesDir}\""; }
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows))
            { fileName = "explorer"; args = $"\"{CoursesDir}\""; }
            else
            { fileName = "xdg-open"; args = $"\"{CoursesDir}\""; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName, Arguments = args, UseShellExecute = false, CreateNoWindow = true,
            });
        }
        catch { /* best effort */ }
    }

    // ── Editor popup ────────────────────────────────────────────────────

    private const int EditorPanelW = 420;
    private const int EditorPanelH = 280;

    private Rectangle EditorPanelRectLocal()
    {
        int x = FrameInset + (CanvasW - EditorPanelW) / 2;
        int y = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
              + (CanvasH - EditorPanelH) / 2;
        return new Rectangle(x, y, EditorPanelW, EditorPanelH);
    }

    private void UpdateEditorPanel(Vector2 local, bool leftPressed)
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { _editorOpen = false; return; }
        if (!leftPressed) return;
        var r = EditorPanelRectLocal();
        var titleBar = new Rectangle(r.X + 3, r.Y + 3, r.Width - 6, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        { _editorOpen = false; return; }
        if (!RetroSkin.PointInRect(local, r)) { _editorOpen = false; return; }

        int contentX = (int)r.X + 16;
        int y = (int)r.Y + RetroWidgets.TitleBarHeight + 16 + 20;

        // Editor mode toggle.
        var toggleBtn = new Rectangle(contentX, y, 160, 24);
        if (RetroSkin.PointInRect(local, toggleBtn)) { _editorMode = !_editorMode; return; }
        y += 32;

        // Brush radius -/+ buttons.
        var minus = new Rectangle(contentX, y, 28, 24);
        var plus  = new Rectangle(contentX + 122, y, 28, 24);
        if (RetroSkin.PointInRect(local, minus)) { _brushRadius = Math.Clamp(_brushRadius - 2f, 4f, 60f); return; }
        if (RetroSkin.PointInRect(local, plus))  { _brushRadius = Math.Clamp(_brushRadius + 2f, 4f, 60f); return; }
        y += 32;

        // Undo / Redo / Reset row.
        var undo  = new Rectangle(contentX,        y, 80, 24);
        var redo  = new Rectangle(contentX +  88,  y, 80, 24);
        var reset = new Rectangle(contentX + 176,  y, 100, 24);
        if (RetroSkin.PointInRect(local, undo))  { UndoSculpt(); return; }
        if (RetroSkin.PointInRect(local, redo))  { RedoSculpt(); return; }
        if (RetroSkin.PointInRect(local, reset)) { ResetHole(); return; }
        y += 32;

        // Save / Load / Reveal row.
        var save   = new Rectangle(contentX,        y,  90, 24);
        var load   = new Rectangle(contentX +  98,  y,  90, 24);
        var reveal = new Rectangle(contentX + 196,  y, 168, 24);
        if (RetroSkin.PointInRect(local, save))   { SaveCurrentCourse(); return; }
        if (RetroSkin.PointInRect(local, load))   { LoadMostRecentCourse(); return; }
        if (RetroSkin.PointInRect(local, reveal)) { RevealCoursesFolder(); return; }
    }

    private void DrawEditorPanel(Vector2 panelOffset)
    {
        if (!_editorOpen) return;
        var rl = EditorPanelRectLocal();
        var abs = new Rectangle(panelOffset.X + rl.X, panelOffset.Y + rl.Y, rl.Width, rl.Height);
        Raylib.DrawRectangle((int)abs.X + 4, (int)abs.Y + 4, (int)abs.Width, (int)abs.Height,
            new Color((byte)0, (byte)0, (byte)0, (byte)110));
        RetroSkin.DrawRaised(abs);
        var titleBar = new Rectangle(abs.X + 3, abs.Y + 3, abs.Width - 6, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Course Editor", true);

        int contentX = (int)abs.X + 16;
        int y = (int)abs.Y + RetroWidgets.TitleBarHeight + 16;

        RetroSkin.DrawText("Right-click sculpts when ON.  Shift+RC = tilt camera.",
            contentX, y, RetroSkin.DisabledText, 12);
        y += 20;

        // Toggle.
        var toggleBtn = new Rectangle(contentX, y, 160, 24);
        if (_editorMode) RetroSkin.DrawPressed(toggleBtn);
        else RetroSkin.DrawRaised(toggleBtn);
        string lbl = _editorMode ? "Editor mode: ON" : "Editor mode: OFF";
        int lw = RetroSkin.MeasureText(lbl, 14);
        RetroSkin.DrawText(lbl,
            (int)(toggleBtn.X + (toggleBtn.Width - lw) / 2),
            (int)toggleBtn.Y + 4 + (_editorMode ? 1 : 0),
            RetroSkin.BodyText, 14);
        // Wheel hint to the right of the button.
        RetroSkin.DrawText("(scroll on canvas to size brush)",
            contentX + 168, y + 6, RetroSkin.DisabledText, 12);
        y += 32;

        // Brush radius readout with -/+ buttons.
        var minus = new Rectangle(contentX, y, 28, 24);
        RetroSkin.DrawRaised(minus);
        RetroSkin.DrawText("-", (int)minus.X + 11, (int)minus.Y + 4, RetroSkin.BodyText, 14);
        RetroSkin.DrawText($"Brush: {(int)_brushRadius} px",
            contentX + 36, y + 6, RetroSkin.BodyText, 14);
        var plus = new Rectangle(contentX + 122, y, 28, 24);
        RetroSkin.DrawRaised(plus);
        RetroSkin.DrawText("+", (int)plus.X + 10, (int)plus.Y + 4, RetroSkin.BodyText, 14);
        y += 32;

        // Undo / Redo / Reset row.
        var undo  = new Rectangle(contentX,        y, 80, 24);
        var redo  = new Rectangle(contentX +  88,  y, 80, 24);
        var reset = new Rectangle(contentX + 176,  y, 100, 24);
        RetroSkin.DrawRaised(undo);
        RetroSkin.DrawRaised(redo);
        RetroSkin.DrawRaised(reset);
        DrawCenteredLabel(undo, _undoStack.Count > 0 ? "Undo" : "Undo (-)");
        DrawCenteredLabel(redo, _redoStack.Count > 0 ? "Redo" : "Redo (-)");
        DrawCenteredLabel(reset, "Reset Hole");
        y += 32;

        // Save / Load / Reveal row.
        var save   = new Rectangle(contentX,        y,  90, 24);
        var load   = new Rectangle(contentX +  98,  y,  90, 24);
        var reveal = new Rectangle(contentX + 196,  y, 168, 24);
        RetroSkin.DrawRaised(save);
        RetroSkin.DrawRaised(load);
        RetroSkin.DrawRaised(reveal);
        DrawCenteredLabel(save,   "Save Course");
        DrawCenteredLabel(load,   "Load Recent");
        DrawCenteredLabel(reveal, "Reveal Courses Folder");
        y += 30;

        // Status line — last save/load message, fades after a few seconds.
        if (_editorStatusTimer > 0 && !string.IsNullOrEmpty(_editorStatus))
        {
            byte alpha = (byte)(MathF.Min(1f, _editorStatusTimer / 1.0f) * 220);
            RetroSkin.DrawText(_editorStatus, contentX, y,
                new Color((byte)200, (byte)180, (byte)80, alpha), 12);
            y += 16;
        }

        RetroSkin.DrawText("Hotkey: Cmd/Ctrl+Z = Undo, Cmd/Ctrl+Shift+Z = Redo",
            contentX, y, RetroSkin.DisabledText, 12);
    }

    private static void DrawCenteredLabel(Rectangle btn, string text)
    {
        int tw = RetroSkin.MeasureText(text, 14);
        RetroSkin.DrawText(text,
            (int)(btn.X + (btn.Width - tw) / 2),
            (int)btn.Y + 4, RetroSkin.BodyText, 14);
    }

    // ── Editor: terrain sculpting ───────────────────────────────────────

    /// <summary>
    /// Apply a Gaussian brush to the active hole's heightmap at the given
    /// canvas pixel position. Positive amp raises, negative amp lowers.
    /// Brush radius is in canvas pixels and converted to heightmap cells.
    /// </summary>
    private void ApplyBrush(Vector2 canvasMouse, float radiusPx, float amp)
    {
        EnsureOriginalSnapshot();
        var hf = _course[_holeIdx].Heightmap;
        float cs = hf.CellSize;
        // Un-project the screen-space cursor through the same tilt the
        // canvas is rendered with, so the brush lands where the user sees
        // it. Without this, screen Y was used directly as world Z — and
        // because the projection inverts Y (world Z=0 lands at the BOTTOM
        // of screen, world Z=large at the TOP), brushing the bottom of
        // the canvas drew at the top of the map.
        var world = ScreenToWorld(canvasMouse);
        float cx = world.X / cs;
        float cy = world.Y / cs;
        float radius = radiusPx / cs;
        // Reuse AddBump's Gaussian profile — same shape the generator uses,
        // so sculpted features blend with the procedural ones.
        hf.AddBump(cx, cy, amp, radius);
    }

    /// <summary>Push a copy of the current heightmap onto the undo stack
    /// (bounded). Called at the *start* of each sculpt drag so each
    /// continuous gesture is one undo step.</summary>
    private void CaptureUndoSnapshot()
    {
        EnsureOriginalSnapshot();
        var hf = _course[_holeIdx].Heightmap;
        var copy = new float[hf.Cols, hf.Rows];
        Array.Copy(hf.H, copy, hf.H.Length);
        _undoStack.Push(copy);
        // Trim to UndoLimit by re-stacking if oversized. Stack<T> doesn't
        // allow drop-from-bottom directly; cheaper to convert and slice.
        if (_undoStack.Count > UndoLimit)
        {
            var arr = _undoStack.ToArray();        // top → bottom
            _undoStack.Clear();
            for (int i = UndoLimit - 1; i >= 0; i--) _undoStack.Push(arr[i]);
        }
    }

    private void UndoSculpt()
    {
        if (_undoStack.Count == 0) return;
        var hf = _course[_holeIdx].Heightmap;
        // Save current to redo before restoring previous.
        var cur = new float[hf.Cols, hf.Rows];
        Array.Copy(hf.H, cur, hf.H.Length);
        _redoStack.Push(cur);
        var snap = _undoStack.Pop();
        Array.Copy(snap, hf.H, snap.Length);
        UnloadTerrainTextures();
    }

    private void RedoSculpt()
    {
        if (_redoStack.Count == 0) return;
        var hf = _course[_holeIdx].Heightmap;
        var cur = new float[hf.Cols, hf.Rows];
        Array.Copy(hf.H, cur, hf.H.Length);
        _undoStack.Push(cur);
        var snap = _redoStack.Pop();
        Array.Copy(snap, hf.H, snap.Length);
        UnloadTerrainTextures();
    }

    /// <summary>Snapshot the heightmap as 'pristine' for the current hole,
    /// so Reset Hole has something to revert to. Called when the hole
    /// first becomes active.</summary>
    private void EnsureOriginalSnapshot()
    {
        if (_origHoleIdx == _holeIdx && _origHeightmap != null) return;
        var hf = _course[_holeIdx].Heightmap;
        _origHeightmap = new float[hf.Cols, hf.Rows];
        Array.Copy(hf.H, _origHeightmap, hf.H.Length);
        _origHoleIdx = _holeIdx;
        _undoStack.Clear();
        _redoStack.Clear();
    }

    private void ResetHole()
    {
        if (_origHeightmap == null) return;
        var hf = _course[_holeIdx].Heightmap;
        if (_origHeightmap.GetLength(0) != hf.Cols || _origHeightmap.GetLength(1) != hf.Rows) return;
        // Push current onto undo so the reset itself is undoable.
        CaptureUndoSnapshot();
        _redoStack.Clear();
        Array.Copy(_origHeightmap, hf.H, _origHeightmap.Length);
        UnloadTerrainTextures();
    }

    private void DrawDisplayPanel(Vector2 panelOffset)
    {
        if (!_displayOpen) return;
        var rl = DisplayPanelRectLocal();
        var abs = new Rectangle(panelOffset.X + rl.X, panelOffset.Y + rl.Y, rl.Width, rl.Height);
        // Drop shadow + raised frame.
        Raylib.DrawRectangle((int)abs.X + 4, (int)abs.Y + 4, (int)abs.Width, (int)abs.Height,
            new Color((byte)0, (byte)0, (byte)0, (byte)110));
        RetroSkin.DrawRaised(abs);

        var titleBar = new Rectangle(abs.X + 3, abs.Y + 3, abs.Width - 6, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Display", true);

        int contentX = (int)abs.X + 16;
        int y = (int)abs.Y + RetroWidgets.TitleBarHeight + 16;

        // Aim row.
        RetroSkin.DrawText("Aim style:", contentX, y, RetroSkin.BodyText, 14);
        y += 18;
        string[] aimLabels = { "Line", "Arc", "Combo" };
        for (int i = 0; i < 3; i++)
        {
            var btn = new Rectangle(contentX + i * 90, y, 80, 24);
            bool selected = (int)_aimStyle == i;
            if (selected) RetroSkin.DrawPressed(btn);
            else RetroSkin.DrawRaised(btn);
            int tw = RetroSkin.MeasureText(aimLabels[i], 14);
            RetroSkin.DrawText(aimLabels[i],
                (int)(btn.X + (btn.Width - tw) / 2),
                (int)btn.Y + 4 + (selected ? 1 : 0),
                RetroSkin.BodyText, 14);
        }
        // Independent Path Arrow toggle — used to be implicit in 'Combo' but
        // now any aim style can show or hide the red pathfinding arrows.
        var pathBtn = new Rectangle(contentX + 3 * 90 + 16, y, 132, 24);
        bool pathSel = _pathArrow;
        if (pathSel) RetroSkin.DrawPressed(pathBtn);
        else RetroSkin.DrawRaised(pathBtn);
        string pathLabel = pathSel ? "Path Arrow: ON" : "Path Arrow: OFF";
        int ptw = RetroSkin.MeasureText(pathLabel, 14);
        RetroSkin.DrawText(pathLabel,
            (int)(pathBtn.X + (pathBtn.Width - ptw) / 2),
            (int)pathBtn.Y + 4 + (pathSel ? 1 : 0),
            RetroSkin.BodyText, 14);
        y += 24 + 18;

        // Three columns of palette swatches.
        int colW = (int)((abs.Width - 32 - 16) / 3);
        int rowH = 18;
        int section0X = contentX;
        int section1X = contentX + colW + 8;
        int section2X = contentX + (colW + 8) * 2;
        RetroSkin.DrawText("Terrain", section0X, y, RetroSkin.BodyText, 14);
        RetroSkin.DrawText("Sky",     section1X, y, RetroSkin.BodyText, 14);
        RetroSkin.DrawText("Ground",  section2X, y, RetroSkin.BodyText, 14);

        DrawMeshList(section0X, y + 14, colW, rowH);
        DrawSkyList (section1X, y + 14, colW, rowH);
        DrawBgList  (section2X, y + 14, colW, rowH);

        // Quick-apply presets row.
        int presetY = (int)abs.Y + (int)abs.Height - 36;
        RetroSkin.DrawText("Presets:", contentX, presetY - 14, RetroSkin.BodyText, 14);
        var presets = DisplayPresets;
        int presetW = ((int)abs.Width - 32 - (presets.Length - 1) * 8) / presets.Length;
        for (int i = 0; i < presets.Length; i++)
        {
            var btn = new Rectangle(contentX + i * (presetW + 8), presetY, presetW, 24);
            RetroSkin.DrawRaised(btn);
            int tw = RetroSkin.MeasureText(presets[i].Name, 12);
            RetroSkin.DrawText(presets[i].Name,
                (int)(btn.X + (btn.Width - tw) / 2),
                (int)btn.Y + 5,
                RetroSkin.BodyText, 12);
        }
    }

    private void DrawMeshList(int x, int y, int width, int rowH)
    {
        for (int i = 0; i < MeshPalettes.Length; i++)
        {
            var row = new Rectangle(x, y + i * rowH, width, rowH - 1);
            bool sel = i == _meshPaletteIdx;
            if (sel) Raylib.DrawRectangleRec(row, new Color((byte)80, (byte)110, (byte)170, (byte)180));
            // Gradient swatch on the right.
            var stops = MeshPalettes[i].Stops;
            int swatchW = 60;
            int swatchX = (int)(row.X + row.Width - swatchW - 2);
            for (int k = 0; k < stops.Length; k++)
            {
                int sx = swatchX + (int)((float)k / stops.Length * swatchW);
                int sw = (int)((float)swatchW / stops.Length) + 1;
                Raylib.DrawRectangle(sx, (int)row.Y + 2, sw, rowH - 5, stops[k]);
            }
            RetroSkin.DrawText(MeshPalettes[i].Name, (int)row.X + 4, (int)row.Y + 2,
                sel ? RetroSkin.TitleText : RetroSkin.BodyText, 12);
        }
    }

    private void DrawSkyList(int x, int y, int width, int rowH)
    {
        for (int i = 0; i < SkyPalettes.Length; i++)
        {
            var row = new Rectangle(x, y + i * rowH, width, rowH - 1);
            bool sel = i == _skyPaletteIdx;
            if (sel) Raylib.DrawRectangleRec(row, new Color((byte)80, (byte)110, (byte)170, (byte)180));
            int swatchW = 28;
            Raylib.DrawRectangle((int)(row.X + row.Width - swatchW - 2), (int)row.Y + 2,
                swatchW, rowH - 5, SkyPalettes[i].Color);
            RetroSkin.DrawText(SkyPalettes[i].Name, (int)row.X + 4, (int)row.Y + 2,
                sel ? RetroSkin.TitleText : RetroSkin.BodyText, 12);
        }
    }

    private void DrawBgList(int x, int y, int width, int rowH)
    {
        for (int i = 0; i < BgPalettes.Length; i++)
        {
            var row = new Rectangle(x, y + i * rowH, width, rowH - 1);
            bool sel = i == _bgPaletteIdx;
            if (sel) Raylib.DrawRectangleRec(row, new Color((byte)80, (byte)110, (byte)170, (byte)180));
            int swatchW = 28;
            Raylib.DrawRectangle((int)(row.X + row.Width - swatchW - 2), (int)row.Y + 2,
                swatchW, rowH - 5, BgPalettes[i].Color);
            RetroSkin.DrawText(BgPalettes[i].Name, (int)row.X + 4, (int)row.Y + 2,
                sel ? RetroSkin.TitleText : RetroSkin.BodyText, 12);
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
            // Mark the active region as beaten and check for the Moon
            // unlock. "Beaten" = completed at least once (any score) so
            // the unlock is generous. The Moon doesn't need to be in the
            // Earth-region list because beating the Moon doesn't unlock
            // anything; it's a victory lap.
            if (_activeRegion != null && _activeRegion.Name != "Moon")
            {
                bool added = _beatenRegions.Add(_activeRegion.Name);
                bool justUnlocked = false;
                if (!_moonUnlocked && EarthRegionNames.All(n => _beatenRegions.Contains(n)))
                {
                    _moonUnlocked = true;
                    _justUnlockedMoon = true;
                    justUnlocked = true;
                }
                if (added || justUnlocked) SaveWorldTeePrefs();
            }
            return;
        }
        _holeIdx++;
        ResetBall();
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        // Splash title card. Fills the canvas area with the splash art
        // (letterboxed if its aspect doesn't match), with a tiny "Click
        // to start" hint in the status bar.
        if (_state == AppState.Splash)
        {
            var titleBarSplash = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
                PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
            RetroWidgets.DrawTitleBarVisual(titleBarSplash, AppTitle, true);

            // Full content area: from below the title+menu bars to above
            // the status bar, spanning the full panel width. The active
            // panel is wider than CanvasW (it includes a score side
            // panel), so a CanvasW-only host pinned the splash to the
            // left edge of the panel and left dead space on the right.
            var hostSplash = new Rectangle(
                panelOffset.X + FrameInset,
                panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight,
                PanelSize.X - 2 * FrameInset,
                PanelSize.Y - 2 * FrameInset
                    - RetroWidgets.TitleBarHeight
                    - RetroWidgets.MenuBarHeight
                    - RetroWidgets.StatusBarHeight);
            Raylib.DrawRectangleRec(hostSplash, new Color((byte)0, (byte)0, (byte)0, (byte)255));
            if (_splashTexLoaded)
            {
                // Letterbox-fit + centre. Preserves aspect (no crop)
                // and centres the result horizontally and vertically;
                // any leftover space inside the host stays the black
                // mat we drew above.
                float texAspect = (float)_splashTex.Width / _splashTex.Height;
                float hostAspect = hostSplash.Width / hostSplash.Height;
                float dw, dh;
                if (texAspect > hostAspect) { dw = hostSplash.Width;  dh = dw / texAspect; }
                else                        { dh = hostSplash.Height; dw = dh * texAspect; }
                var dst = new Rectangle(
                    hostSplash.X + (hostSplash.Width  - dw) / 2f,
                    hostSplash.Y + (hostSplash.Height - dh) / 2f,
                    dw, dh);
                Raylib.DrawTexturePro(_splashTex,
                    new Rectangle(0, 0, _splashTex.Width, _splashTex.Height),
                    dst, Vector2.Zero, 0f, Color.White);
            }

            var statusSplash = new Rectangle(panelOffset.X + FrameInset,
                panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
                PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
            RetroWidgets.StatusBar(statusSplash, AppTitle, "click to start");
            return;
        }

        // Picker mode draws its own self-contained chrome on top of the
        // window frame: title bar with close X, the dithered globe filling
        // the canvas area, and a status hint.
        if (_state == AppState.Picking && _picker != null)
        {
            var titleBarPick = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
                PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
            RetroWidgets.DrawTitleBarVisual(titleBarPick, AppTitle, true);

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
                "Pick a region for course flavor. Difficulty stays on the menu.",
                "Pick to begin");
            return;
        }

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        string regionTag = _activeRegion != null ? $" ({_activeRegion.Name})" : "";
        RetroWidgets.DrawTitleBarVisual(titleBar,
            $"{AppTitle} - Hole {_holeIdx + 1} of {Holes}{regionTag}", true);

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
        DrawDisplayPanel(panelOffset);
        DrawEditorPanel(panelOffset);
        // Brush ring: show under the cursor while in editor mode (in or
        // out of the popup) so the user can target. Only when the cursor
        // is over the canvas.
        if (_editorMode)
        {
            var mp = Raylib.GetMousePosition();
            var canvasOriginAbs = new Vector2(panelOffset.X + FrameInset,
                panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight);
            var local = mp - canvasOriginAbs;
            if (local.X >= 0 && local.X < CanvasW && local.Y >= 0 && local.Y < CanvasH)
            {
                Raylib.DrawCircleLines((int)mp.X, (int)mp.Y, _brushRadius,
                    new Color((byte)255, (byte)200, (byte)80, (byte)180));
                Raylib.DrawCircleLines((int)mp.X, (int)mp.Y, _brushRadius - 1,
                    new Color((byte)40, (byte)24, (byte)8, (byte)200));
            }
        }
    }

    /// <summary>
    /// Refresh the cached supercombo plan if the ball is parked in a new
    /// spot. Skips when not in Combo aim mode (planning is expensive — a
    /// few hundred thousand physics ticks worst case) or while the ball
    /// is in flight, since any plan from a moving ball is stale.
    /// </summary>
    private void EnsurePlan()
    {
        // Path-arrow rendering is now its own toggle, independent of aim
        // style. Also gated on EnableHolePlanner — the arrows depend on
        // the planner's TeePlan / mid-hole replan, both of which are
        // disabled when the planner's off.
        if (!_pathArrow || !_enableHolePlanner
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
        if (_tiltDragging || _sculpting)
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

        // The cup hole — a black disc on the green — is the actual hole,
        // not a sprite, so it sits directly on the ground beneath the
        // flag and the ball. Drawn before the depth-sorted pass below.
        int cupFx = (int)(canvasOrigin.X + cupScreen.X);
        int cupFy = (int)(canvasOrigin.Y + cupScreen.Y);
        Raylib.DrawCircle(cupFx, cupFy, 5, new Color((byte)0, (byte)0, (byte)0, (byte)255));

        // Depth-sort the world-anchored sprites — trees, flag, ball — by
        // their world-Y (= depth into the screen under the tilted view)
        // so a closer object draws over a farther one. Without this, the
        // ball was always painted last and showed up on top of tree
        // foliage and flagpole tips even when those sprites should have
        // been in front of (closer than) it.
        var ballScreenForDraw = ProjectToScreen(_ball, hf);
        var depthList = new List<(float y, Action draw)>(hole.Trees.Count + 2);
        foreach (var tree in hole.Trees)
        {
            var localTree = tree;     // capture
            depthList.Add((tree.Y, () => DrawOneTree(localTree, hf, canvasOrigin)));
        }
        depthList.Add((hole.Cup.Y, () => DrawFlag(canvasOrigin, cupScreen)));
        depthList.Add((_ball.Y, () => DrawBallSprite(canvasOrigin, ballScreenForDraw)));
        // Descending Y = back-to-front. Stable sort keeps relative order
        // when two items share a Y.
        depthList.Sort((a, b) => b.y.CompareTo(a.y));
        foreach (var (_, draw) in depthList) draw();

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

        // Ball is drawn earlier as part of the depth-sorted world-objects
        // pass so it correctly sits behind tree foliage / flagpole tips
        // when those sprites are in front of it (smaller world Y).

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
        Color sky, ground;
        if (_isMoonRound)
        {
            // Moon: deep space backdrop, slight blue-gray cast on the
            // surface so it's not pure neutral. Sky is near-black; ground
            // (which acts as the bottom of the scanline gradient) is a
            // very dark gray that lets the cratered terrain pop.
            sky    = new Color((byte)  6, (byte)  8, (byte) 14, (byte)255);
            ground = new Color((byte) 18, (byte) 20, (byte) 28, (byte)255);
        }
        else
        {
            sky    = SkyPalettes[_skyPaletteIdx].Color;
            ground = BgPalettes[_bgPaletteIdx].Color;
        }
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

        // Moon-only: sprinkle a few dithered stars across the upper
        // two-thirds of the sky and paint a small Earth crescent in the
        // upper-right. Both are rendered into the image *before* the
        // mesh-dot cloud so the terrain reads as the closer plane.
        if (_isMoonRound)
        {
            // Deterministic star field per round — same seed each frame so
            // stars don't twinkle randomly across rebuilds.
            var starRng = new Random(0x5747);
            int stars = 60;
            for (int s = 0; s < stars; s++)
            {
                int sx = starRng.Next(CanvasW);
                int sy = starRng.Next((int)(CanvasH * 0.7f));
                byte br = (byte)(140 + starRng.Next(100));
                Raylib.ImageDrawPixel(ref img, sx, sy, new Color(br, br, br, (byte)255));
                // Half the stars get a tiny halo so they read as bright.
                if (starRng.NextDouble() < 0.4 && sx + 1 < CanvasW)
                {
                    Raylib.ImageDrawPixel(ref img, sx + 1, sy, new Color((byte)(br * 0.6f), (byte)(br * 0.6f), (byte)(br * 0.7f), (byte)255));
                }
            }
            // Earth-in-the-sky: small dithered blue/white disc.
            int ecx = CanvasW - 70, ecy = 40, eR = 16;
            for (int y = ecy - eR; y <= ecy + eR; y++)
            for (int x = ecx - eR; x <= ecx + eR; x++)
            {
                if (x < 0 || x >= CanvasW || y < 0 || y >= CanvasH) continue;
                int dx = x - ecx, dy = y - ecy;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                if (d > eR) continue;
                // Lambert from the same upper-left light direction the
                // globe picker uses, so Earth-as-seen-from-Moon is lit
                // consistently.
                float nx = dx / (float)eR;
                float ny = dy / (float)eR;
                float nz = MathF.Sqrt(MathF.Max(0f, 1f - nx * nx - ny * ny));
                float lambert = MathF.Max(0f, -(nx * -0.5f + ny * -0.8f + nz * -0.6f));
                bool oceanLike = ((x + y) & 3) != 0;        // cheap "ocean dominant" stipple
                int bayer = ((x & 3) * 4 + (y & 3));         // 0..15 micro-Bayer
                float thr = bayer / 16f;
                bool bright = lambert + 0.25f > thr;
                Color cE;
                if (bright && !oceanLike)
                    cE = new Color((byte)200, (byte)190, (byte)160, (byte)255);  // continent
                else if (bright)
                    cE = new Color((byte)100, (byte)140, (byte)180, (byte)255);  // ocean lit
                else
                    cE = new Color((byte) 30, (byte) 50, (byte) 90, (byte)255);  // ocean dark
                Raylib.ImageDrawPixel(ref img, x, y, cE);
            }
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
    private const int ReachAngleSteps = 16;
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
        // 120-step cap (= 2 sim seconds at dt=1/60) is enough for
        // every realistic shot to settle without dragging the planner
        // into long-tail rolls. The reach map only needs to know where
        // shots SETTLE, not how they take their last 1.3 sec to do it.
        var (endPos, end) = SimulatePathLite(pos, vel, hole, maxSteps: 120);
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
    /// <summary>
    /// Render a single tree at world position <paramref name="world"/>:
    /// ground shadow + the user-replaceable PNG sprite anchored at the
    /// base centre, falling back to a procedural trunk + foliage if no
    /// PNG is loaded. Pulled out of DrawCourse so the depth-sorted
    /// world-objects pass can call it once per tree at the right time
    /// in the sort order.
    /// </summary>
    private void DrawOneTree(Vector2 world, HeightField hf, Vector2 canvasOrigin)
    {
        var sp = ProjectToScreen(world, hf);
        int sx = (int)(canvasOrigin.X + sp.X);
        int sy = (int)(canvasOrigin.Y + sp.Y);
        Raylib.DrawEllipse(sx + 2, sy + 2, 11, Math.Max(3, (int)(11 * _cosT)),
            new Color((byte)0, (byte)0, (byte)0, (byte)80));

        if (_treeTexLoaded)
        {
            const float boxW = 28f;
            const float boxH = 40f;
            float scale = MathF.Min(boxW / _treeTex.Width, boxH / _treeTex.Height);
            float dw = _treeTex.Width * scale;
            float dh = _treeTex.Height * scale;
            Raylib.DrawTexturePro(_treeTex,
                new Rectangle(0, 0, _treeTex.Width, _treeTex.Height),
                new Rectangle(sx - dw / 2f, sy - dh, dw, dh),
                Vector2.Zero, 0f, Color.White);
            return;
        }

        int foliageOffset = (int)(14 * _sinT + 6);
        Raylib.DrawRectangle(sx - 2, sy - foliageOffset, 4, foliageOffset + 1,
            new Color((byte)80, (byte)56, (byte)32, (byte)255));
        Raylib.DrawCircle(sx, sy - foliageOffset, 12, new Color((byte)40, (byte)120, (byte)60, (byte)255));
        Raylib.DrawCircle(sx - 3, sy - foliageOffset - 3, 4, new Color((byte)80, (byte)168, (byte)96, (byte)255));
    }

    /// <summary>
    /// The ball: drop shadow + white body + dark outline. Same shape
    /// the late-frame draw used; lifted into a helper so the depth-
    /// sorted pass can place it among the trees / flag.
    /// </summary>
    private static void DrawBallSprite(Vector2 canvasOrigin, Vector2 ballScreen)
    {
        int bx = (int)(canvasOrigin.X + ballScreen.X);
        int by = (int)(canvasOrigin.Y + ballScreen.Y);
        Raylib.DrawCircle(bx + 1, by + 1, 5, new Color((byte)0, (byte)0, (byte)0, (byte)100));
        Raylib.DrawCircle(bx, by, 5, new Color((byte)255, (byte)255, (byte)255, (byte)255));
        Raylib.DrawCircleLines(bx, by, 5, RetroSkin.BodyText);
    }

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
        // White aim arrow: matches the red plan-arrow's shape language —
        // 2.2 px shaft, chevron head with the same back-7 / perp-5 / 2.5
        // proportions — just in white instead of red so the two read as
        // sibling indicators (white = aim, red = computed path).
        //
        // The shaft has a very subtle single-direction lift (peak ~0.6 px
        // mid-shaft) — barely perceptible, soft enough that the arrow
        // still scans as essentially straight, but with a hint of curve
        // that distinguishes it from a flat ruler line. Roughly an order
        // of magnitude less than the Arc-style trajectory predictor.
        var nrm = Vector2.Normalize(dir);
        var perp = new Vector2(-nrm.Y, nrm.X);
        float length = MathF.Min(dir.Length(), 110f);
        var col = new Color((byte)255, (byte)255, (byte)255, (byte)235);

        const int segs = 16;
        const float peakRise = 0.6f;        // ~5-10% of an Arc-style curve
        Vector2 prev = ballAbs;
        for (int i = 1; i <= segs; i++)
        {
            float t = i / (float)segs;
            // Symmetric parabola: 0 at endpoints, peak at t=0.5.
            float rise = -peakRise * 4f * t * (1f - t);
            float along = length * t;
            Vector2 p = ballAbs + nrm * along + perp * rise;
            Raylib.DrawLineEx(prev, p, 2.2f, col);
            prev = p;
        }

        // Chevron head — geometry mirrors DrawPlanArrows's red arrow so
        // both indicators read as the same shape, just different tints.
        Vector2 end = ballAbs + nrm * length;
        var aPerp = new Vector2(-nrm.Y, nrm.X);
        Raylib.DrawLineEx(end, end - nrm * 7f + aPerp * 5f, 2.5f, col);
        Raylib.DrawLineEx(end, end - nrm * 7f - aPerp * 5f, 2.5f, col);
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
