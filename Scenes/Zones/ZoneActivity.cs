using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities;

namespace MouseHouse.Scenes.Zones;

/// <summary>
/// Generic zone scene — floating prop sprites on the transparent overlay with
/// ambient looping audio. Ported from the original Godot project's zones.gd.
/// One activity instance represents one zone (beach, apartment, bedroom, camping).
/// </summary>
public class ZoneActivity : IActivity
{
    private const int PropScale = 4;            // pixel art scale factor
    private const int TitleBarH = 22;           // matches DesktopPetScene's ActivityTitleBarHeight
    private const int Margin = 12;
    private const float CampfireFrameTime = 0.4f;

    public bool TransparentBackground => true;
    public bool IsFinished { get; private set; }
    public Vector2 PanelSize { get; private set; }

    // Only the title bar consumes pet input; empty space + props pass through so the
    // user can drag the pet onto / over the zone.
    public bool ContainsPoint(Vector2 panelLocalPos) =>
        panelLocalPos.X >= 0 && panelLocalPos.X <= PanelSize.X &&
        panelLocalPos.Y >= 0 && panelLocalPos.Y <= TitleBarH;

    // Title-bar buttons (speaker mute) — handled before the title-bar drag triggers.
    public bool OnTitleBarClick(Vector2 panelLocalPos)
    {
        if (Raylib.CheckCollisionPointRec(panelLocalPos, SpeakerRect()))
        {
            _muted = !_muted;
            return true;
        }
        return false;
    }

    private readonly AssetCache _assets;
    private readonly string _zoneName;
    private readonly ZoneDef _def;
    private readonly Dictionary<string, Texture2D> _propTextures = new();
    private Texture2D _campfireAlt;
    private float _campfireTimer;
    private bool _campfireFrameB;

    // Computed prop draw rects (relative to panel top-left) — drives both
    // tight panel sizing and (eventually) per-prop hit testing.
    private List<Rectangle> _propRects = new();

    // Audio
    private Music _music;
    private bool _musicLoaded;
    private bool _muted = true;
    private float _musicVolume;

    private static readonly Color ChromeBg = new(40, 30, 30, 200);
    private static readonly Color ChromeFg = new(230, 220, 210, 255);

    public ZoneActivity(AssetCache assets, string zoneName)
    {
        _assets = assets;
        _zoneName = zoneName;
        if (!Zones.TryGetValue(zoneName, out var def))
            throw new ArgumentException($"Unknown zone '{zoneName}'", nameof(zoneName));
        _def = def;
        // Provisional size — recomputed in Load() once we know texture dimensions.
        PanelSize = new Vector2(360, 220);
    }

    public void Load()
    {
        foreach (var prop in _def.Props)
        {
            if (!_propTextures.ContainsKey(prop.Name))
                _propTextures[prop.Name] = _assets.GetTexture(prop.TexturePath);
        }
        if (_def.Props.Any(p => p.Name == "campfire"))
            _campfireAlt = _assets.GetTexture("assets/sprites/zones/props/campfire_2.png");

        // Compute prop rects (in panel-local coords) and tight panel bounds.
        _propRects.Clear();
        float minX = float.MaxValue, minY = float.MaxValue, maxX = 0, maxY = 0;
        foreach (var prop in _def.Props)
        {
            var tex = _propTextures[prop.Name];
            float w = tex.Width * PropScale;
            float h = tex.Height * PropScale;
            float x = prop.OffsetX;
            float y = prop.OffsetY + TitleBarH;  // shift below title bar
            _propRects.Add(new Rectangle(x, y, w, h));
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x + w);
            maxY = Math.Max(maxY, y + h);
        }
        // Translate everything so minX/minY land at Margin (positive coords)
        float shiftX = Margin - minX;
        float shiftY = TitleBarH - minY;
        if (shiftY < 0) shiftY = 0;
        for (int i = 0; i < _propRects.Count; i++)
        {
            var r = _propRects[i];
            _propRects[i] = new Rectangle(r.X + shiftX, r.Y + shiftY, r.Width, r.Height);
        }
        float panelW = (maxX - minX) + shiftX + Margin;
        float panelH = (maxY - minY) + shiftY + Margin;
        // Ensure space for title bar with X + speaker buttons (~80px wide minimum)
        panelW = Math.Max(panelW, 200);
        PanelSize = new Vector2(panelW, panelH);

        // Ambient music
        var fullPath = Path.Combine(_assets.BasePath, _def.AmbientAudio);
        if (File.Exists(fullPath))
        {
            _music = Raylib.LoadMusicStream(fullPath);
            _music.Looping = true;
            _musicLoaded = true;
            _musicVolume = _muted ? 0f : 0.6f;
            Raylib.SetMusicVolume(_music, _musicVolume);
            Raylib.PlayMusicStream(_music);
        }
    }

    public void Close()
    {
        if (_musicLoaded)
        {
            Raylib.StopMusicStream(_music);
            Raylib.UnloadMusicStream(_music);
            _musicLoaded = false;
        }
        IsFinished = true;
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        // Music streaming + volume tween toward target
        if (_musicLoaded)
        {
            float target = _muted ? 0f : 0.6f;
            if (Math.Abs(_musicVolume - target) > 0.01f)
            {
                _musicVolume += (target - _musicVolume) * Math.Min(1f, delta * 4f);
                Raylib.SetMusicVolume(_music, _musicVolume);
            }
            Raylib.UpdateMusicStream(_music);
        }

        // Campfire 2-frame animation
        if (_campfireAlt.Id != 0)
        {
            _campfireTimer += delta;
            if (_campfireTimer >= CampfireFrameTime)
            {
                _campfireTimer -= CampfireFrameTime;
                _campfireFrameB = !_campfireFrameB;
            }
        }

        // Speaker mute toggle is handled in OnTitleBarClick (it sits inside the title bar
        // rect, so DesktopPetScene calls OnTitleBarClick before deciding whether to drag).
    }

    public void Draw(Vector2 panelOffset)
    {
        // Title-bar chrome (drag handle + close + speaker). No full panel background.
        // Title bar background — semi-transparent strip so users can see/grab it.
        var tbRect = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, TitleBarH);
        Raylib.DrawRectangleRec(tbRect, new Color(40, 30, 30, 140));

        // Zone name on the left of the title bar
        string label = char.ToUpper(_zoneName[0]) + _zoneName[1..];
        FontManager.DrawText(label, (int)panelOffset.X + 8, (int)panelOffset.Y + 4, 12, ChromeFg);

        // Close [X] at top-right (matches the click rect in DesktopPetScene)
        FontManager.DrawText("[X]", (int)(panelOffset.X + PanelSize.X - 28),
            (int)panelOffset.Y + 4, 12, ChromeFg);

        // Speaker (mute) toggle button just left of the close button
        var spk = SpeakerRect();
        spk.X += panelOffset.X;
        spk.Y += panelOffset.Y;
        Raylib.DrawRectangleRec(spk, ChromeBg);
        Raylib.DrawRectangleLinesEx(spk, 1, ChromeFg);
        DrawSpeakerIcon(spk);

        // Props at their computed rects
        for (int i = 0; i < _def.Props.Length; i++)
        {
            var prop = _def.Props[i];
            if (!_propTextures.TryGetValue(prop.Name, out var tex))
                continue;
            var drawTex = (prop.Name == "campfire" && _campfireFrameB && _campfireAlt.Id != 0)
                ? _campfireAlt : tex;
            var r = _propRects[i];
            var src = new Rectangle(0, 0, drawTex.Width, drawTex.Height);
            var dest = new Rectangle(panelOffset.X + r.X, panelOffset.Y + r.Y, r.Width, r.Height);
            Raylib.DrawTexturePro(drawTex, src, dest, Vector2.Zero, 0f, Color.White);
        }
    }

    private void DrawSpeakerIcon(Rectangle r)
    {
        int cx = (int)(r.X + r.Width / 2);
        int cy = (int)(r.Y + r.Height / 2);
        // Triangle-ish speaker body
        Raylib.DrawRectangle(cx - 6, cy - 2, 4, 4, ChromeFg);
        Raylib.DrawRectangle(cx - 4, cy - 4, 2, 8, ChromeFg);
        Raylib.DrawRectangle(cx - 2, cy - 6, 2, 12, ChromeFg);
        if (_muted)
        {
            Raylib.DrawLineEx(new Vector2(cx + 1, cy - 5), new Vector2(cx + 7, cy + 5), 2, ChromeFg);
            Raylib.DrawLineEx(new Vector2(cx + 7, cy - 5), new Vector2(cx + 1, cy + 5), 2, ChromeFg);
        }
        else
        {
            Raylib.DrawRectangle(cx + 1, cy - 1, 2, 2, ChromeFg);
            Raylib.DrawRectangle(cx + 4, cy - 3, 2, 6, ChromeFg);
            Raylib.DrawRectangle(cx + 7, cy - 5, 2, 10, ChromeFg);
        }
    }

    private Rectangle SpeakerRect() => new(PanelSize.X - 60, 4, 22, 14);

    // ─── Zone definitions ───
    // Layouts hand-tuned for PropScale=4 — the original Godot data had a renderer
    // bug (offsets were applied unscaled despite a "* 4" comment) that caused props
    // to clump on top of each other; I've spread them out so each prop is visible.

    public record PropDef(string Name, string TexturePath, int OffsetX, int OffsetY);
    public record ZoneDef(string AmbientAudio, PropDef[] Props, (int X, int Y)[] RestSpots);

    public static readonly Dictionary<string, ZoneDef> Zones = new()
    {
        // Beach: waves on top, umbrella + towel on left, chair + bucket on right
        ["beach"] = new ZoneDef(
            "assets/audio/zone_beach_ambient.wav",
            new[] {
                new PropDef("ocean_waves", "assets/sprites/zones/props/ocean_waves.png", 0, 0),
                new PropDef("beach_umbrella", "assets/sprites/zones/props/beach_umbrella.png", 30, 50),
                new PropDef("beach_towel", "assets/sprites/zones/props/beach_towel.png", 0, 130),
                new PropDef("beach_chair", "assets/sprites/zones/props/beach_chair.png", 175, 80),
                new PropDef("bucket_shovel", "assets/sprites/zones/props/bucket_shovel.png", 260, 110),
            },
            new (int, int)[] { (60, 145), (200, 130) }),

        // Apartment: floor lamp tall on left, couch + coffee table center, tv stand right, rug bottom
        ["apartment"] = new ZoneDef(
            "assets/audio/zone_apartment_ambient.wav",
            new[] {
                new PropDef("floor_lamp", "assets/sprites/zones/props/floor_lamp.png", 0, 0),
                new PropDef("couch", "assets/sprites/zones/props/couch.png", 50, 50),
                new PropDef("coffee_table", "assets/sprites/zones/props/coffee_table.png", 70, 130),
                new PropDef("tv_stand", "assets/sprites/zones/props/tv_stand.png", 230, 30),
                new PropDef("rug", "assets/sprites/zones/props/rug.png", 50, 165),
            },
            new (int, int)[] { (70, 65), (90, 175) }),

        // Bedroom: poster on wall, bed left, slippers next to bed, nightstand + bookshelf right
        ["bedroom"] = new ZoneDef(
            "assets/audio/zone_bedroom_ambient.wav",
            new[] {
                new PropDef("poster", "assets/sprites/zones/props/poster.png", 30, 0),
                new PropDef("bed", "assets/sprites/zones/props/bed.png", 0, 70),
                new PropDef("slippers", "assets/sprites/zones/props/slippers.png", 130, 130),
                new PropDef("nightstand_lamp", "assets/sprites/zones/props/nightstand_lamp.png", 200, 60),
                new PropDef("bookshelf", "assets/sprites/zones/props/bookshelf.png", 270, 30),
            },
            new (int, int)[] { (40, 80), (200, 110) }),

        // Camping: tree left, tent center, campfire + log + lantern right
        ["camping"] = new ZoneDef(
            "assets/audio/zone_camping_ambient.wav",
            new[] {
                new PropDef("tree", "assets/sprites/zones/props/tree.png", 0, 0),
                new PropDef("tent", "assets/sprites/zones/props/tent.png", 80, 30),
                new PropDef("log", "assets/sprites/zones/props/log.png", 220, 80),
                new PropDef("campfire", "assets/sprites/zones/props/campfire.png", 230, 50),
                new PropDef("lantern", "assets/sprites/zones/props/lantern.png", 310, 80),
            },
            new (int, int)[] { (240, 95), (130, 75) }),
    };
}
