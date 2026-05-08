namespace MouseHouse.Data;

/// <summary>
/// One station entry. <see cref="Slug"/> is the SomaFM channel id used to
/// fetch the now-playing JSON; leave blank for non-SomaFM streams.
/// <see cref="Active"/> controls whether the station is in the prev/next
/// rotation — inactive stations stay in the library so the user can flip
/// them back on without re-typing the URL.
/// </summary>
public record class RadioStation(
    string Name,
    string Url,
    string Genre,
    string Slug = "",
    bool Active = true);

/// <summary>
/// Bundled list of publicly-broadcast internet streams. SomaFM channels are
/// listener-supported and intentionally open to public streaming. Persisted
/// to <c>stations.json</c> in the save dir so the user can add/remove/toggle
/// stations through the editor and have changes survive restarts.
/// </summary>
public static class RadioStations
{
    public static readonly IReadOnlyList<RadioStation> Defaults = new[]
    {
        new RadioStation("Groove Salad",     "http://ice1.somafm.com/groovesalad-128-mp3",   "ambient",    "groovesalad"),
        new RadioStation("Drone Zone",       "http://ice1.somafm.com/dronezone-128-mp3",     "ambient",    "dronezone"),
        new RadioStation("Indie Pop Rocks",  "http://ice1.somafm.com/indiepop-128-mp3",      "indie",      "indiepop"),
        new RadioStation("Secret Agent",     "http://ice1.somafm.com/secretagent-128-mp3",   "lounge",     "secretagent"),
        new RadioStation("Lush",             "http://ice1.somafm.com/lush-128-mp3",          "vocals",     "lush"),
        new RadioStation("Boot Liquor",      "http://ice1.somafm.com/bootliquor-128-mp3",    "americana",  "bootliquor"),
        new RadioStation("Beat Blender",     "http://ice1.somafm.com/beatblender-128-mp3",   "electronic", "beatblender"),
        new RadioStation("Deep Space One",   "http://ice1.somafm.com/deepspaceone-128-mp3",  "ambient",    "deepspaceone"),
        new RadioStation("Mission Control",  "http://ice1.somafm.com/missioncontrol-128-mp3","space",      "missioncontrol"),
        new RadioStation("DEF CON Radio",    "http://ice1.somafm.com/defcon-128-mp3",        "electronic", "defcon"),
        new RadioStation("Sonic Universe",   "http://ice1.somafm.com/sonicuniverse-128-mp3", "jazz",       "sonicuniverse"),
        // Fluid: instrumental hiphop / future soul / liquid trap — SomaFM's
        // closest thing to a lofi-beats channel.
        new RadioStation("Fluid",            "http://ice1.somafm.com/fluid-128-mp3",         "lofi",       "fluid"),
        // Radio Swiss Classic — SRG SSR's all-classical, no-DJ-talk channel.
        // Public broadcaster Icecast that bursts on connect, so it doesn't
        // hit the playhead-rides-the-live-edge stutter the way WCPE did.
        new RadioStation("Radio Swiss Classic", "https://stream.srg-ssr.ch/m/rsc_de/mp3_128", "classical"),
    };

    private static readonly object _lock = new();
    private static List<RadioStation>? _all;
    private static int _version;

    /// <summary>
    /// Bumped each time the library is mutated. Consumers that cache an
    /// index into the active list can compare versions to know when to
    /// re-resolve.
    /// </summary>
    public static int Version => _version;

    /// <summary>Full station library — both active and inactive entries.</summary>
    public static IReadOnlyList<RadioStation> All
    {
        get { EnsureLoaded(); return _all!; }
    }

    /// <summary>Stations marked Active, in library order. This is the rotation.</summary>
    public static IReadOnlyList<RadioStation> ActiveOnly
    {
        get
        {
            EnsureLoaded();
            var snap = _all!;
            var list = new List<RadioStation>(snap.Count);
            for (int i = 0; i < snap.Count; i++)
                if (snap[i].Active) list.Add(snap[i]);
            return list;
        }
    }

    public static void Add(RadioStation s)
    {
        lock (_lock)
        {
            EnsureLoadedLocked();
            _all!.Add(s);
            _version++;
            SaveLocked();
        }
    }

    public static void Remove(int index)
    {
        lock (_lock)
        {
            EnsureLoadedLocked();
            if (index < 0 || index >= _all!.Count) return;
            _all.RemoveAt(index);
            _version++;
            SaveLocked();
        }
    }

    public static void SetActive(int index, bool active)
    {
        lock (_lock)
        {
            EnsureLoadedLocked();
            if (index < 0 || index >= _all!.Count) return;
            if (_all[index].Active == active) return;
            _all[index] = _all[index] with { Active = active };
            _version++;
            SaveLocked();
        }
    }

    public static void Update(int index, RadioStation replacement)
    {
        lock (_lock)
        {
            EnsureLoadedLocked();
            if (index < 0 || index >= _all!.Count) return;
            _all[index] = replacement;
            _version++;
            SaveLocked();
        }
    }

    private static void EnsureLoaded()
    {
        if (_all != null) return;
        lock (_lock) EnsureLoadedLocked();
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "RadioStation is a small fixed POCO; reflection-based deserialisation is intentional.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Same — small POCO, JSON shape is stable.")]
    private static void EnsureLoadedLocked()
    {
        if (_all != null) return;
        var path = StationsJsonPath();
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var arr = System.Text.Json.JsonSerializer.Deserialize<List<RadioStation>>(json);
                if (arr != null && arr.Count > 0)
                {
                    _all = arr;
                    return;
                }
            }
            catch { /* fall through to defaults */ }
        }
        _all = new List<RadioStation>(Defaults);
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "RadioStation is a small fixed POCO.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Same.")]
    private static void SaveLocked()
    {
        try
        {
            var path = StationsJsonPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(_all, opts));
        }
        catch { /* best-effort */ }
    }

    private static string StationsJsonPath()
        => Path.Combine(Core.SaveManager.SaveDirectory, "stations.json");
}
