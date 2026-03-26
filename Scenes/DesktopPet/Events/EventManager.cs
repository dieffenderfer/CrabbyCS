using MouseHouse.Core;
using MouseHouse.Rendering;

namespace MouseHouse.Scenes.DesktopPet.Events;

/// <summary>
/// Spawns and manages random desktop events (creatures, weather, etc.)
/// </summary>
public class EventManager
{
    private readonly AssetCache _assets;
    private readonly int _screenW;
    private readonly int _screenH;
    private readonly List<EventBase> _active = new();
    private float _spawnTimer;
    private string _colorMode = "2color";

    private const int MaxActive = 1;
    private const float MinInterval = 60f;
    private const float MaxInterval = 180f;

    // Weighted event table: (name, weight, factory)
    private readonly List<(string name, int weight, Func<EventBase> factory)> _eventTable;

    private static readonly Random Rng = new();

    // Sprite frame counts derived from sprite dimensions
    private static readonly Dictionary<string, int> FrameCounts = new()
    {
        ["seagull"] = 8,         // 128/16
        ["butterfly"] = 4,       // 64/16
        ["falling_leaf"] = 4,    // 48/12
        ["shooting_star"] = 3,   // ~60/20 (actually 60x8, 3 frames of 20x8)
        ["firefly"] = 4,         // 48/12
        ["paper_airplane"] = 4,  // ~64/16ish
        ["balloon"] = 2,         // 42/22 ~2
        ["rain_cloud"] = 6,      // 96/16
        ["bat"] = 4,             // ~64/16ish
        ["ladybug"] = 4,         // 48/12
        ["dragonfly"] = 4,       // ~80/20ish
        ["jellyfish"] = 4,       // ~56/14ish but height 20
        ["dolphin"] = 6,         // 96/16
        ["hot_air_balloon"] = 2, // 48/24
        ["comet"] = 3,           // ~72/24ish but height 8
        ["dust_devil"] = 4,      // ~56/14ish
        ["frog"] = 4,            // 48/12
        ["hermit_crab"] = 4,     // 56/14
        ["pelican"] = 4,         // ~96/24ish but height 18
        ["crab_ghost"] = 4,      // 48/12
    };

    public EventManager(AssetCache assets, int screenW, int screenH)
    {
        _assets = assets;
        _screenW = screenW;
        _screenH = screenH;
        _spawnTimer = Rng.NextSingle() * 30f + 15f; // First event sooner for testing

        _eventTable = new()
        {
            ("seagull", 3, () => new SeagullEvent()),
            ("butterfly", 2, () => new ButterflyEvent()),
            ("falling_leaf", 3, () => new FallingLeafEvent()),
            ("shooting_star", 1, () => new ShootingStarEvent()),
            ("firefly", 2, () => new FireflyEvent()),
            ("paper_airplane", 3, () => new PaperAirplaneEvent()),
            ("balloon", 2, () => new BalloonEvent()),
            ("bat", 2, () => new BatEvent()),
            ("ladybug", 2, () => new LadybugEvent()),
            ("dragonfly", 2, () => new DragonFlyEvent()),
            ("jellyfish", 2, () => new JellyfishEvent()),
            ("dolphin", 2, () => new DolphinEvent()),
            ("hot_air_balloon", 1, () => new HotAirBalloonEvent()),
            ("comet", 1, () => new CometEvent()),
            ("dust_devil", 1, () => new DustDevilEvent()),
            ("frog", 2, () => new FrogEvent()),
            ("hermit_crab", 2, () => new HermitCrabEvent()),
            ("pelican", 1, () => new PelicanEvent()),
        };
    }

    public void SetColorMode(string mode) => _colorMode = mode;

    public void Update(float delta)
    {
        // Remove finished events
        _active.RemoveAll(e => e.Finished);

        // Spawn timer
        _spawnTimer -= delta;
        if (_spawnTimer <= 0 && _active.Count < MaxActive)
        {
            _spawnTimer = Rng.NextSingle() * (MaxInterval - MinInterval) + MinInterval;
            TrySpawn();
        }

        // Update active events
        foreach (var e in _active)
            e.Update(delta);
    }

    public void Draw()
    {
        foreach (var e in _active)
            e.Draw();
    }

    private void TrySpawn()
    {
        var (name, factory) = PickWeightedEvent();
        var evt = factory();

        // Load sprite
        var spritePath = GetSpritePath(name);
        int frames = FrameCounts.GetValueOrDefault(name, 4);
        evt.Sheet = _assets.GetSpriteSheet(spritePath, frames);
        evt.Init(_screenW, _screenH);
        _active.Add(evt);
    }

    private (string name, Func<EventBase> factory) PickWeightedEvent()
    {
        int totalWeight = 0;
        foreach (var (_, w, _) in _eventTable)
            totalWeight += w;

        int roll = Rng.Next(1, totalWeight + 1);
        int cumulative = 0;
        foreach (var (name, weight, factory) in _eventTable)
        {
            cumulative += weight;
            if (cumulative >= roll)
                return (name, factory);
        }
        // Fallback
        var last = _eventTable[^1];
        return (last.name, last.factory);
    }

    private string GetSpritePath(string eventName)
    {
        // Color mode: fullcolor uses base path, 1color/2color use subdirectories
        if (_colorMode == "fullcolor" || _colorMode == "2color")
        {
            // Check if color variant exists
            var colorPath = $"assets/sprites/events/{_colorMode}/{eventName}.png";
            if (File.Exists(Path.Combine(_assets.BasePath, colorPath)))
                return colorPath;
        }
        else if (_colorMode == "1color")
        {
            var colorPath = $"assets/sprites/events/1color/{eventName}.png";
            if (File.Exists(Path.Combine(_assets.BasePath, colorPath)))
                return colorPath;
        }
        // Fallback to base sprite
        return $"assets/sprites/events/{eventName}.png";
    }

    /// <summary>Forces an event to spawn immediately (for testing via menu).</summary>
    public void ForceSpawn()
    {
        _spawnTimer = 0;
    }

    public int ActiveCount => _active.Count;
}
