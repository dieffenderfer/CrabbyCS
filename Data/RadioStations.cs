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
    private static string _loadStatus = "";

    // FileSystemWatcher for hot-reload of hand-edits to stations.json.
    // Started lazily on first load and runs for the lifetime of the
    // process. Coalesces multiple FSW events per save into one reload
    // via a 250ms debounce timer — typical text editors save through a
    // truncate+write or write+atomic-rename sequence that fires several
    // events back-to-back.
    private static FileSystemWatcher? _watcher;
    private static System.Threading.Timer? _reloadDebounce;
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Fired after a hand-edit to stations.json is detected on disk and
    /// the in-memory list is reloaded. Subscribers should rebind any
    /// cached indexes (e.g. RadioWidget clamps its station-rotation
    /// index and re-resolves the playing station by URL).
    /// </summary>
    public static event Action? Reloaded;

    /// <summary>
    /// Bumped each time the library is mutated. Consumers that cache an
    /// index into the active list can compare versions to know when to
    /// re-resolve.
    /// </summary>
    public static int Version => _version;

    /// <summary>
    /// Non-empty when the most recent load fell back to defaults — usually
    /// because the user's stations.json was malformed or empty. The editor
    /// reads this so it can show the user a status note explaining why
    /// their custom edits aren't there. Cleared after the first successful
    /// save by the editor.
    /// </summary>
    public static string LoadStatus
    {
        get { lock (_lock) { return _loadStatus; } }
    }

    public static void ClearLoadStatus()
    {
        lock (_lock) _loadStatus = "";
    }

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
            _loadStatus = "";
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
            _loadStatus = "";
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
            _loadStatus = "";
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
            _loadStatus = "";
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
        bool fileMissing = !File.Exists(path);

        if (!fileMissing)
        {
            try
            {
                var json = File.ReadAllText(path);
                var arr = System.Text.Json.JsonSerializer.Deserialize<List<RadioStation>>(json);
                if (arr != null && arr.Count > 0)
                {
                    _all = arr;
                    _loadStatus = "";
                    return;
                }
                // Empty array or null deserialise — fall through to the
                // re-seed branch so the user gets a real file back instead
                // of a silent ghost.
            }
            catch { /* malformed file → fall through to re-seed */ }
        }

        // Either no file yet, or the file is malformed / empty. Seed from
        // defaults and write it out so the user has a real parseable
        // file to hand-edit when Shift+Right-Click launches their text
        // editor. _loadStatus is surfaced as a status note explaining
        // why custom edits aren't visible (in the malformed case).
        _all = new List<RadioStation>(Defaults);
        SaveLocked();
        _loadStatus = fileMissing
            ? "Seeded stations.json with defaults."
            : "stations.json was malformed — restored from defaults.";
        // Watcher is started AFTER the seed write so the seed itself
        // doesn't trigger a spurious Reloaded event.
        EnsureWatcherStarted(path);
    }

    private static void EnsureWatcherStarted(string path)
    {
        if (_watcher != null) return;
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;
        try
        {
            _watcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                             | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Renamed += (s, e) => OnFileChanged(s, e);
        }
        catch
        {
            // FSW can fail in sandboxed / restricted environments; the
            // app still works, the user just has to close+reopen the
            // radio to pick up hand-edits.
            _watcher = null;
        }
    }

    private static void OnFileChanged(object _, FileSystemEventArgs __)
    {
        // Debounce: text editors typically fire several FSW events per
        // save (truncate-then-write, or write-then-atomic-rename). Coalesce
        // them into one reload so we don't reload mid-save and read a
        // half-written file.
        var prev = System.Threading.Interlocked.Exchange(ref _reloadDebounce,
            new System.Threading.Timer(_ => ReloadFromDisk(),
                null, ReloadDebounce, System.Threading.Timeout.InfiniteTimeSpan));
        prev?.Dispose();
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "RadioStation is a small fixed POCO.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Same.")]
    private static void ReloadFromDisk()
    {
        bool changed = false;
        lock (_lock)
        {
            var path = StationsJsonPath();
            if (!File.Exists(path)) return;
            try
            {
                var json = File.ReadAllText(path);
                var arr = System.Text.Json.JsonSerializer.Deserialize<List<RadioStation>>(json);
                if (arr != null && arr.Count > 0)
                {
                    // Replace the list reference so consumers iterating
                    // over an old snapshot stay safe (no in-place mutation).
                    _all = arr;
                    _version++;
                    _loadStatus = "";
                    changed = true;
                }
                // Empty / null deserialise: leave the existing in-memory
                // list alone rather than wiping it. The user's editor is
                // probably mid-save with the file briefly empty.
            }
            catch
            {
                // Malformed JSON during a save in progress — ignore and
                // wait for the next FSW event when the editor finishes.
            }
        }
        if (changed)
        {
            try { Reloaded?.Invoke(); }
            catch { /* subscriber threw — don't crash the watcher thread */ }
        }
    }

    /// <summary>
    /// Open <c>stations.json</c> in whatever app the OS has registered
    /// as the default for .json files. Returns the resolved path so the
    /// caller can show it (e.g. as a toast). Best-effort: failures are
    /// swallowed — the user can always navigate to the path manually.
    /// </summary>
    public static string OpenInExternalEditor()
    {
        EnsureLoaded();
        var path = StationsJsonPath();
        try
        {
            if (System.Runtime.InteropServices.RuntimeInformation
                    .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
            {
                System.Diagnostics.Process.Start("open", new[] { path });
            }
            else if (System.Runtime.InteropServices.RuntimeInformation
                         .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                // UseShellExecute routes the path through Win32
                // ShellExecute, which respects the per-extension default
                // app — Notepad, VS Code, whatever the user has set.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                });
            }
            else
            {
                System.Diagnostics.Process.Start("xdg-open", new[] { path });
            }
        }
        catch
        {
            // Editor failed to launch — caller's toast should still surface
            // the path so the user knows where to find it.
        }
        return path;
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
